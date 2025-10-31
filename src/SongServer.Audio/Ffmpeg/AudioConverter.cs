using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SongServer.Audio.Ffmpeg;

public unsafe class AudioConverter(Stream inputStream)
{
	private readonly Stream _inputStream = inputStream;

	private class AudioReader(Stream stream)
	{
		private readonly Stream _stream = stream;
		private readonly PipeReader _reader = PipeReader.Create(Stream.Null);

		public int Read(void* opaque, byte* buf, int buf_size)
		{
			if (!_stream.CanRead) {
				return ffmpeg.AVERROR_UNKNOWN;
			}

			try {
				var buffer  = new Span<byte>(buf, buf_size);
				var readCnt = _stream.Read(buffer);

				return readCnt;
			} catch (Exception ex) {
				Console.WriteLine($"Error reading from stream: {ex.Message}");
				return ffmpeg.AVERROR_UNKNOWN;
			}
		}

		public int ReadPipe(void* opaque, byte* buf, int buf_size)
		{
			if (!_reader.TryRead(out var result)) {
				return 0;
			}

			var buffer  = new Span<byte>(buf, buf_size);
			var readCnt = (int)Math.Min(buf_size, result.Buffer.Length);

			result.Buffer.Slice(0, readCnt).CopyTo(buffer);

			return readCnt;
		}

		public long Seek(void* opaque, long offset, int whence)
		{
			//Console.WriteLine($"SEEK -> offset {offset}, whence {whence}");

			const int SEEK_SET = 0x0;
			const int SEEK_CUR = 0x01;
			const int SEEK_END = 0x02;

			try {
				return whence switch {
					ffmpeg.AVSEEK_SIZE => _stream.Length,
					SEEK_SET => _stream.Seek(offset, SeekOrigin.Begin),
					SEEK_CUR => _stream.Seek(offset, SeekOrigin.Current),
					SEEK_END => _stream.Seek(offset, SeekOrigin.End),
					_ => ffmpeg.AVERROR_BUG,
				};
			} catch (Exception e) {
				Console.WriteLine($"Exception during seek (offset {offset} whence {whence}): {e.Message}");
				return ffmpeg.AVERROR_UNKNOWN;
			}
		}
	}

	public static void ConvertToPcm(string inputPath, Stream inputStream, PipeReader inputReader, PipeWriter outputWriter)
	{
		const int bufferLength = 1024 * 128;

		var reader = new AudioReader(inputStream);
		var readerHandle = GCHandle.Alloc(inputReader);
		var audioStreamIndex = -1;
		var result = 0;

		AVFormatContext* formatContext = null;
		byte* mallocBuffer = null;
		AVIOContext* ioContext = null;

		try {
			if ((formatContext = ffmpeg.avformat_alloc_context()) == null) {
				throw new Exception("Unable to allocate AVFormatContext");
			}

			if ((mallocBuffer = (byte*)ffmpeg.av_malloc(bufferLength)) == null) {
				throw new Exception("Unable to allocate IO buffer");
			}

			// we could use the "opaque" pointer if we wanted to, calling GCHandle.Alloc on
			// whatever object we want to be passed around, then using GCHandle.ToIntPtr()
			// and casting to void*. To dereference, we would use GCHandle.FromIntPtr()
			// to get the GCHandle back, then cast the .Target to whatever it started as.

			ioContext = ffmpeg.avio_alloc_context(
				mallocBuffer,
				bufferLength,
				0,
				null,
				new avio_alloc_context_read_packet(reader.Read),
				null,
				inputStream.CanSeek ? new avio_alloc_context_seek(reader.Seek) : null);

			if (ioContext == null) {
				throw new Exception("Unable to allocate IO context");
			}

			// for some reason, if we tell ffmpeg that the stream is seekable, then
			// things go badly for us... but if we don't, it seeks anyway, and it works...
			//ioContext->seekable = inputStream.CanSeek ? ffmpeg.AVIO_SEEKABLE_NORMAL : 0;

			// tell the formatContext that we'll be handling our own I/O
			// we also pass null to avformat_open_input below
			formatContext->pb = ioContext;

			// open the input
			if ((result = ffmpeg.avformat_open_input(&formatContext, null, null, null)) < 0) {
				throw new FFmpegException("Could not open input file", result);
			}

			// read enough to figure out what's in the data
			if ((result = ffmpeg.avformat_find_stream_info(formatContext, null)) < 0) {
				throw new FFmpegException("Could not find stream info", result);
			}

			LogInputFormatInformation(formatContext);

			// find an audio stream
			for (var i = 0; i < formatContext->nb_streams; i++) {
				if (formatContext->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO) {
					audioStreamIndex = i;
					break;
				}
			}

			// make sure we found an audio stream
			if (audioStreamIndex == -1) {
				throw new InvalidOperationException("No audio stream found");
			}

			Console.WriteLine($"Converting stream at index {audioStreamIndex}");

			ConvertStream(formatContext, audioStreamIndex, outputWriter);
		} finally {
			if (formatContext != null) {
				ffmpeg.avformat_close_input(&formatContext);
			}

			if (ioContext != null) {
				ffmpeg.avio_context_free(&ioContext);
			}

			if (mallocBuffer != null) {
				ffmpeg.av_free(&mallocBuffer);
			}

			readerHandle.Free();
		}
	}

	private static void ConvertStream(AVFormatContext* formatContext, int audioStreamIndex, PipeWriter writer)
	{
		var codecParams  = formatContext->streams[audioStreamIndex]->codecpar;
		var codec        = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
		var codecContext = ffmpeg.avcodec_alloc_context3(codec);
		var result       = 0;

		LogCodecParameters(codec, codecParams);

		SwrContext* swrContext = null;
		AVPacket* packet = null;
		AVFrame* frame = null;
		AVFrame* pcmFrame = null;

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

			// setup resampler for PCM conversion
			result = ffmpeg.swr_alloc_set_opts2(
				&swrContext,
				&codecContext->ch_layout,
				AVSampleFormat.AV_SAMPLE_FMT_S16, // PCM 16-bit
				codecContext->sample_rate,
				&codecContext->ch_layout,
				codecContext->sample_fmt,
				codecContext->sample_rate,
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

			if ((frame = ffmpeg.av_frame_alloc()) == null) {
				throw new FFmpegException("Failed to allocate input frame", ffmpeg.AVERROR_UNKNOWN);
			}

			if ((pcmFrame = ffmpeg.av_frame_alloc()) == null) {
				throw new FFmpegException("Failed to allocate output frame", ffmpeg.AVERROR_UNKNOWN);
			}

			// read packets from avformat (these are packets in the source format)
			while (ffmpeg.av_read_frame(formatContext, packet) >= 0) {
				// skip packets for streams we're not interested in
				if (packet->stream_index != audioStreamIndex) {
					continue;
				}

				// give the packet to the codec for decoding
				ffmpeg.avcodec_send_packet(codecContext, packet);

				// receive decoded frames from the codec
				while (ffmpeg.avcodec_receive_frame(codecContext, frame) >= 0) {
					// Convert to PCM
					pcmFrame->ch_layout   = frame->ch_layout;
					pcmFrame->sample_rate = frame->sample_rate;
					pcmFrame->format      = (int)AVSampleFormat.AV_SAMPLE_FMT_S16;
	
					ffmpeg.swr_convert_frame(swrContext, pcmFrame, frame);

					// figure out how much data we're dealing with
					var dataSize = ffmpeg.av_samples_get_buffer_size(null, codecContext->ch_layout.nb_channels, pcmFrame->nb_samples, (AVSampleFormat)pcmFrame->format, 1);

					// create spans for our source and destination
					var sourceSpan = new Span<byte>(pcmFrame->data[0], dataSize);
					var outputSpan = writer.GetSpan(dataSize);

					// copy data from the output frame to outputSpan
					sourceSpan.CopyTo(outputSpan);

					// advance the pipewriter
					writer.Advance(dataSize);

					// clean up the decoded/converted frame
					ffmpeg.av_frame_unref(pcmFrame);
				}

				ffmpeg.av_packet_unref(packet);
			}
		} finally {
			writer.Complete();

			if (frame != null) {
				ffmpeg.av_frame_free(&frame);
			}

			if (pcmFrame != null) {
				ffmpeg.av_frame_free(&pcmFrame);
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

	private static int ReadDataFromPipe(void* opaque, byte* buf, int buf_size)
	{
		var gch = GCHandle.FromIntPtr((nint)opaque);

		if (gch.Target is not PipeReader reader) {
			return ffmpeg.AVERROR_EXTERNAL;
		}

		if (!reader.TryRead(out var result)) {
			return 0;
		}

		var buffer  = new Span<byte>(buf, buf_size);
		var readCnt = (int)Math.Min(buf_size, result.Buffer.Length);

		result.Buffer.Slice(0, readCnt).CopyTo(buffer);

		return readCnt;
	}

	private static void LogInputFormatInformation(AVFormatContext* formatContext)
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
}
