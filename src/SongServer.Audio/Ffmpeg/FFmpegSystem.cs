using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace SongServer.Audio.FFmpeg;

public unsafe class FFmpegSystem
{
	private static ILogger? _logger = null;
	private static readonly av_log_set_callback_callback _logCallback = new(LogCallback);

	private FFmpegSystem() {}

	public static void Initialize(ILogger<FFmpegSystem> logger)
	{
		// https://blog.gaein.cn/passages/beginner-guide-to-ffmpeg-autogen/
		ffmpeg.RootPath = @"C:\Users\MarkParker\source\repos\song-server\src\SongServer.Player\_ffmpeg_bin";

		_logger ??= logger;

		ffmpeg.av_log_set_callback(_logCallback);
	}

	private static void LogCallback(void* avcl, int level, string format, byte* vl)
	{
		// if we have no logger (for some reason), get us out of here
		if (_logger == null) {
			return;
		}

		var mselLevel = level switch {
			ffmpeg.AV_LOG_PANIC => LogLevel.Critical,
			ffmpeg.AV_LOG_FATAL => LogLevel.Critical,
			ffmpeg.AV_LOG_ERROR => LogLevel.Error,
			ffmpeg.AV_LOG_WARNING => LogLevel.Warning,
			ffmpeg.AV_LOG_INFO => LogLevel.Information,
			ffmpeg.AV_LOG_VERBOSE => LogLevel.Debug, // ?? maybe information?
			ffmpeg.AV_LOG_DEBUG => LogLevel.Debug,
			ffmpeg.AV_LOG_TRACE => LogLevel.Trace,
			ffmpeg.AV_LOG_QUIET => LogLevel.None,
			_ => LogLevel.Information,
		};

		// if we're not logging at the specified level, exit now
		if (!_logger.IsEnabled(mselLevel)) {
			return;
		}

		var lineSize    = 1024;
		var lineBuffer  = stackalloc byte[lineSize];
		var printPrefix = 1;

		ffmpeg.av_log_format_line(avcl, level, format, vl, lineBuffer, lineSize, &printPrefix);

		var line = Marshal.PtrToStringUTF8((nint)lineBuffer)?.Trim();

		if (string.IsNullOrWhiteSpace(line)) {
			return;
		}

		#pragma warning disable CA2254 // Template should be a static expression
		_logger.Log(mselLevel, line);
		#pragma warning restore CA2254 // Template should be a static expression
	}
}
