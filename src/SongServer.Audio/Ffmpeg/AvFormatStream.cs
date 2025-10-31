using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SongServer.Audio.Ffmpeg;

/// <summary>
/// Wrapper for FFmpeg AVFormatContext that handles native resource management.
/// </summary>
public unsafe class AvFormatStream : IDisposable
{
	private AVFormatContext* _formatContext;
	private bool _disposed;

	public AVFormatContext* Context => _formatContext;

	public bool IsOpen => _formatContext != null && !_disposed;

	public int StreamCount => IsOpen ? (int)_formatContext->nb_streams : 0;

	~AvFormatStream() => Dispose();

	public void OpenInput(string url)
	{
		// TODO: these are null... that's probably ok?
		AVInputFormat* format = null;
		AVDictionary** options = null;

		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext != null) {
			throw new InvalidOperationException("Format context is already open");
		}

		var ctx = ffmpeg.avformat_alloc_context();
		var result = ffmpeg.avformat_open_input(&ctx, url, format, options);

		if (result < 0) {
			throw new FFmpegException($"Failed to open input file '{url}'", result);
		}

		_formatContext = ctx;
	}

	/// <summary>
	/// Reads packets from the format context to get stream information.
	/// </summary>
	/// <param name="options">Dictionary options (can be null)</param>
	public void FindStreamInfo()
	{
		AVDictionary** options = null;

		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext == null) {
			throw new InvalidOperationException("Format context is not open");
		}

		int result = ffmpeg.avformat_find_stream_info(_formatContext, options);

		if (result < 0) {
			throw new FFmpegException("Failed to find stream information", result);
		}

		// Print format information
		Console.WriteLine($"Format: {Marshal.PtrToStringUTF8((IntPtr)_formatContext->iformat->name)}");
		Console.WriteLine($"Duration: {_formatContext->duration / (double)ffmpeg.AV_TIME_BASE:F2} seconds");
		Console.WriteLine($"Bit rate: {_formatContext->bit_rate} bps");
		Console.WriteLine($"Number of streams: {_formatContext->nb_streams}");
		Console.WriteLine();

		// Print information about each stream
		for (int i = 0; i < _formatContext->nb_streams; i++)
		{
			AVStream* stream = _formatContext->streams[i];
			AVCodecParameters* codecParams = stream->codecpar;
			
			Console.WriteLine($"Stream #{i}:");
			Console.WriteLine($"  Type: {codecParams->codec_type}");
			Console.WriteLine($"  Codec: {ffmpeg.avcodec_get_name(codecParams->codec_id)}");
			
			if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
			{
				Console.WriteLine($"  Sample Rate: {codecParams->sample_rate} Hz");
				Console.WriteLine($"  Channels: {codecParams->ch_layout.nb_channels}");
				Console.WriteLine($"  Sample Format: {codecParams->format}");
				Console.WriteLine($"  Bit Rate: {codecParams->bit_rate} bps");
			}
			else if (codecParams->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
			{
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

	/// <summary>
	/// Gets the stream at the specified index.
	/// </summary>
	/// <param name="index">Stream index</param>
	/// <returns>Pointer to the AVStream</returns>
	public AVStream* GetStream(int index)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext == null)
			throw new InvalidOperationException("Format context is not open");

		if (index < 0 || index >= _formatContext->nb_streams)
			throw new ArgumentOutOfRangeException(nameof(index));

		return _formatContext->streams[index];
	}

	/// <summary>
	/// Finds the best stream of the specified type.
	/// </summary>
	/// <param name="type">Media type to search for</param>
	/// <param name="wantedStreamNb">Desired stream number (-1 for automatic)</param>
	/// <param name="relatedStream">Related stream (-1 for none)</param>
	/// <param name="decoderRet">Pointer to receive the decoder (can be null)</param>
	/// <returns>Stream index or negative error code</returns>
	public int FindBestStream(AVMediaType type, int wantedStreamNb = -1, int relatedStream = -1, AVCodec** decoderRet = null)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext == null)
			throw new InvalidOperationException("Format context is not open");

		return ffmpeg.av_find_best_stream(_formatContext, type, wantedStreamNb, relatedStream, decoderRet, 0);
	}

	/// <summary>
	/// Reads the next frame from the format context.
	/// </summary>
	/// <param name="packet">Packet to receive the frame data</param>
	/// <returns>0 on success, negative error code on failure, AVERROR_EOF at end of file</returns>
	public int ReadFrame(AVPacket* packet)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext == null)
			throw new InvalidOperationException("Format context is not open");

		return ffmpeg.av_read_frame(_formatContext, packet);
	}

	/// <summary>
	/// Seeks to a timestamp in the stream.
	/// </summary>
	/// <param name="streamIndex">Stream index (-1 for default stream)</param>
	/// <param name="timestamp">Target timestamp</param>
	/// <param name="flags">Seek flags</param>
	/// <returns>0 on success, negative error code on failure</returns>
	public int Seek(int streamIndex, long timestamp, int flags = 0)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);

		if (_formatContext == null)
			throw new InvalidOperationException("Format context is not open");

		return ffmpeg.av_seek_frame(_formatContext, streamIndex, timestamp, flags);
	}

	public void Dispose()
	{
		if (_disposed) {
			return;
		}

		if (_formatContext != null) {
			// Close the input and free the format context
			AVFormatContext* ctx = _formatContext;
			ffmpeg.avformat_close_input(&ctx);
			_formatContext = null;
		}

		_disposed = true;
		GC.SuppressFinalize(this);
	}
}

/// <summary>
/// Exception thrown when FFmpeg operations fail.
/// </summary>
