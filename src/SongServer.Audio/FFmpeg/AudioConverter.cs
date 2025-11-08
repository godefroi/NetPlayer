using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SongServer.Audio.FFmpeg;

public unsafe class AudioConverter : IDisposable
{
	private const int _bufferLength = 1024 * 128;

	private readonly Stream _inputStream;
	private readonly ILogger _logger;
	private readonly avio_alloc_context_read_packet _readStream;
	private readonly avio_alloc_context_seek _seekStream;
	private bool _disposed = false;

	private AVFormatContext* _formatContext = null;
	private byte* _mallocBuffer = null;
	private AVIOContext* _ioContext = null;

	public AudioConverter(Stream inputStream, ILogger<AudioConverter> logger)
	{
		var result = 0;

		_inputStream  = inputStream;
		_logger       = logger;
		_readStream   = new(ReadFromStream);
		_seekStream   = new(SeekInStream);

		if ((_formatContext = ffmpeg.avformat_alloc_context()) == null) {
			throw new Exception("Unable to allocate AVFormatContext");
		}

		if ((_mallocBuffer = (byte*)ffmpeg.av_malloc(_bufferLength)) == null) {
			throw new Exception("Unable to allocate IO buffer");
		}

		_ioContext = ffmpeg.avio_alloc_context(
			_mallocBuffer,
			_bufferLength,
			0,
			null,
			_readStream,
			null,
			_inputStream.CanSeek ? _seekStream : null);

		if (_ioContext == null) {
			throw new Exception("Unable to allocate IO context");
		}

		// for some reason, if we tell ffmpeg that the stream is seekable, then
		// things go badly for us... but if we don't, it seeks anyway, and it works...
		//ioContext->seekable = inputStream.CanSeek ? ffmpeg.AVIO_SEEKABLE_NORMAL : 0;

		// tell the formatContext that we'll be handling our own I/O
		// we also pass null to avformat_open_input below
		_formatContext->pb = _ioContext;

		// open the input
		fixed (AVFormatContext** ptr = &_formatContext) {
			if ((result = ffmpeg.avformat_open_input(ptr, null, null, null)) < 0) {
				throw new FFmpegException("Could not open input file", result);
			}
		}

		// read enough to figure out what's in the data
		if ((result = ffmpeg.avformat_find_stream_info(_formatContext, null)) < 0) {
			throw new FFmpegException("Could not find stream info", result);
		}

		var audioStreamIndex = -1;

		//LogInputFormatInformation(_formatContext);

		// find an audio stream
		for (var i = 0; i < _formatContext->nb_streams; i++) {
			if (_formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) {
				audioStreamIndex = i;
				break;
			}
		}

		// make sure we found an audio stream
		if (audioStreamIndex == -1) {
			throw new InvalidOperationException("No audio stream found");
		}

		//Console.WriteLine($"Converting stream at index {audioStreamIndex}");

		StreamIndex = audioStreamIndex;
	}

	~AudioConverter() => Dispose(disposing: false);

	public int StreamIndex { get; private set; }

	public void ConvertStream(Stream outputStream, PcmAudioSpec outputSpec, CancellationToken cancellationToken = default)
	{
		var codecParams  = _formatContext->streams[StreamIndex]->codecpar;
		var codec        = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
		var codecContext = ffmpeg.avcodec_alloc_context3(codec);
		var result       = 0;
		var outputFormat = outputSpec.Format.AsAVSampleFormat();

		AVChannelLayout outputLayout = default;

		if (codecContext->ch_layout.nb_channels != outputSpec.Channels) {
			ffmpeg.av_channel_layout_default(&outputLayout, 2);
		} else {
			outputLayout = codecContext->ch_layout;
		}

		//LogCodecParameters(codec, codecParams);

		SwrContext* swrContext = null;
		AVPacket* packet = null;
		AVFrame* decodedFrame = null;
		AVFrame* resampledFrame = null;

		if (codecContext == null) {
			throw new FFmpegException("Unable to allocate codec context", ffmpeg.AVERROR_UNKNOWN);
		}

		try {
			if ((result = ffmpeg.avcodec_parameters_to_context(codecContext, codecParams)) < 0) {
				throw new FFmpegException("Failed to set codec parameters on context", result);
			}

			if ((result = ffmpeg.avcodec_open2(codecContext, codec, null)) < 0) {
				throw new FFmpegException("Failed to open AV codec", result);
			}

			_logger.LogInformation(
				"Initializing resampler with input spec [format {inFmt} channels {inChan} rate {inRate}] output spec [format {outFmt} channels {outChan} rate {outRate}]",
				 codecContext->sample_fmt,
				 codecContext->ch_layout.nb_channels,
				 codecContext->sample_rate,
				 outputFormat,
				 outputLayout.nb_channels,
				 outputSpec.SampleFrequency);

			// setup resampler for PCM conversion
			result = ffmpeg.swr_alloc_set_opts2(
				&swrContext,
				&outputLayout,              // out channel layout (was &codecContext->ch_layout)
				outputFormat,               // out sample format (was AVSampleFormat.AV_SAMPLE_FMT_S16, // PCM 16-bit)
				outputSpec.SampleFrequency, // out sample rate (was codecContext->sample_rate)
				&codecContext->ch_layout,   // in channel layout
				codecContext->sample_fmt,   // in sample format
				codecContext->sample_rate,  // in sample rate
				0,
				null);

			if (result < 0) {
				throw new FFmpegException("Failed to allocate/configure software resampler", result);
			}

			if ((result = ffmpeg.swr_init(swrContext)) < 0) {
				throw new FFmpegException("Failed to initialize software resampler", result);
			}

			if ((packet = ffmpeg.av_packet_alloc()) == null) {
				throw new FFmpegException("Failed to allocate AV packet", ffmpeg.AVERROR_UNKNOWN);
			}

			if ((decodedFrame = ffmpeg.av_frame_alloc()) == null) {
				throw new FFmpegException("Failed to allocate input frame", ffmpeg.AVERROR_UNKNOWN);
			}

			if ((resampledFrame = ffmpeg.av_frame_alloc()) == null) {
				throw new FFmpegException("Failed to allocate output frame", ffmpeg.AVERROR_UNKNOWN);
			}

			// read packets from avformat (these are packets in the source format)
			while (!cancellationToken.IsCancellationRequested && ffmpeg.av_read_frame(_formatContext, packet) >= 0) {
				// skip packets for streams we're not interested in
				if (packet->stream_index != StreamIndex) {
					continue;
				}

				// give the packet to the codec for decoding
				ffmpeg.avcodec_send_packet(codecContext, packet);

				// receive decoded frames from the codec
				while (!cancellationToken.IsCancellationRequested && ffmpeg.avcodec_receive_frame(codecContext, decodedFrame) >= 0) {
					// Convert to PCM
					resampledFrame->ch_layout   = outputLayout;
					resampledFrame->sample_rate = outputSpec.SampleFrequency; // was frame->sample_rate;
					resampledFrame->format      = (int)outputFormat; // was (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
	
					ffmpeg.swr_convert_frame(swrContext, resampledFrame, decodedFrame);

					// figure out how much data we're dealing with
					var dataSize = ffmpeg.av_samples_get_buffer_size(null, outputLayout.nb_channels, resampledFrame->nb_samples, (AVSampleFormat)resampledFrame->format, 1);

					// create a span for our source
					var sourceSpan = new Span<byte>(resampledFrame->data[0], dataSize);

					// write it to the output stream
					outputStream.Write(sourceSpan);

					// clean up the decoded/converted frame
					ffmpeg.av_frame_unref(resampledFrame);
				}

				ffmpeg.av_packet_unref(packet);
			}
		} finally {
			outputStream.Flush();

			if (decodedFrame != null) {
				ffmpeg.av_frame_free(&decodedFrame);
			}

			if (resampledFrame != null) {
				ffmpeg.av_frame_free(&resampledFrame);
			}

			if (packet != null) {
				ffmpeg.av_packet_free(&packet);
			}

			if (swrContext != null) {
				ffmpeg.swr_free(&swrContext);
			}

			if (codecContext != null) {
				ffmpeg.avcodec_free_context(&codecContext);
			}
		}
	}

	private int ReadFromStream(void* opaque, byte* buf, int buf_size)
	{
		if (!_inputStream.CanRead) {
			return ffmpeg.AVERROR_UNKNOWN;
		}

		try {
			var buffer  = new Span<byte>(buf, buf_size);
			var readCnt = _inputStream.Read(buffer);

			return readCnt;
		} catch (Exception ex) {
			Console.WriteLine($"Error reading from stream: {ex.Message}");
			return ffmpeg.AVERROR_UNKNOWN;
		}
	}

	private long SeekInStream(void* opaque, long offset, int whence)
	{
		//Console.WriteLine($"SEEK -> offset {offset}, whence {whence}");
		if (!_inputStream.CanSeek) {
			return ffmpeg.AVERROR_UNKNOWN;
		}

		const int SEEK_SET = 0x0;
		const int SEEK_CUR = 0x01;
		const int SEEK_END = 0x02;

		try {
			return whence switch {
				ffmpeg.AVSEEK_SIZE => _inputStream.Length,
				SEEK_SET => _inputStream.Seek(offset, SeekOrigin.Begin),
				SEEK_CUR => _inputStream.Seek(offset, SeekOrigin.Current),
				SEEK_END => _inputStream.Seek(offset, SeekOrigin.End),
				_ => ffmpeg.AVERROR_BUG,
			};
		} catch (Exception e) {
			Console.WriteLine($"Exception during seek (offset {offset} whence {whence}): {e.Message}");
			return ffmpeg.AVERROR_UNKNOWN;
		}
	}

	private void LogInputFormatInformation(AVFormatContext* formatContext)
	{
		// Print format information
		Console.WriteLine($"Format: {Marshal.PtrToStringUTF8((nint)formatContext->iformat->name)}");
		Console.WriteLine($"Duration: {formatContext->duration / (double)ffmpeg.AV_TIME_BASE:F2} seconds");
		Console.WriteLine($"Bit rate: {formatContext->bit_rate} bps");
		Console.WriteLine($"Number of streams: {formatContext->nb_streams}");
		Console.WriteLine();

		// Print information about each stream
		for (var i = 0; i < formatContext->nb_streams; i++) {
			AVStream* stream = formatContext->streams[i];
			AVCodecParameters* codecParams = stream->codecpar;
			
			Console.WriteLine($"Stream #{i}:");
			Console.WriteLine($"  Type: {codecParams->codec_type}");
			Console.WriteLine($"  Codec: {ffmpeg.avcodec_get_name(codecParams->codec_id)}");
			
			if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) {
				Console.WriteLine($"  Sample Rate: {codecParams->sample_rate} Hz");
				Console.WriteLine($"  Channels: {codecParams->ch_layout.nb_channels}");
				Console.WriteLine($"  Sample Format: {(AVSampleFormat)codecParams->format}");
				Console.WriteLine($"  Bit Rate: {codecParams->bit_rate} bps");
			} else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO) {
				Console.WriteLine($"  Resolution: {codecParams->width}x{codecParams->height}");
				Console.WriteLine($"  Frame Rate: {stream->avg_frame_rate.num}/{stream->avg_frame_rate.den} fps");
				Console.WriteLine($"  Pixel Format: {codecParams->format}");
				Console.WriteLine($"  Bit Rate: {codecParams->bit_rate} bps");
			}
			
			Console.WriteLine($"  Time Base: {stream->time_base.num}/{stream->time_base.den}");
			Console.WriteLine($"  Duration: {stream->duration * stream->time_base.num / (double)stream->time_base.den:F2} seconds");
			Console.WriteLine();
		}
	}

	private static void LogCodecParameters(AVCodec* codec, AVCodecParameters* codecParams)
	{
		Console.WriteLine($"Decoding source with codec {Marshal.PtrToStringUTF8((nint)codec->long_name)} ({Marshal.PtrToStringUTF8((nint)codec->name)}, id: {codecParams->codec_id})");
		Console.WriteLine($"  sample format: {(AVSampleFormat)codecParams->format}");
		Console.WriteLine($"  bits/sample (coded): {codecParams->bits_per_coded_sample}");
		Console.WriteLine($"  bits/sample (raw): {codecParams->bits_per_raw_sample}");
		Console.WriteLine($"  frame size: {codecParams->frame_size}");		
		Console.WriteLine($"  bit rate: {codecParams->bit_rate}");
		Console.WriteLine($"  channels: {codecParams->ch_layout.nb_channels}");
		Console.WriteLine($"  sample rate: {codecParams->sample_rate}");
	}

	protected virtual void Dispose(bool disposing)
	{
		if (_disposed) {
			return;
		}

		if (disposing) {
			// TODO: dispose managed state (managed objects)
		}

		if (_formatContext != null) {
			fixed(AVFormatContext** ptr = &_formatContext) {
				ffmpeg.avformat_close_input(ptr);
			}
			_formatContext = null;
		}

		if (_ioContext != null) {
			fixed(AVIOContext** ptr = &_ioContext) {
				ffmpeg.avio_context_free(ptr);
			}
			_ioContext = null;
		}

		if (_mallocBuffer != null) {
			Console.WriteLine("About to free _mallocBuffer");
			fixed(void* ptr = &_mallocBuffer) {
				ffmpeg.av_free(ptr);
			}
			_mallocBuffer = null;
			Console.WriteLine("Freed _mallocBuffer");
		}

		// TODO: set large fields to null
		_disposed = true;
	}

	public void Dispose()
	{
		// Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
		Dispose(disposing: true);
		GC.SuppressFinalize(this);
	}
}

