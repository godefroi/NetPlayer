using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace SongServer.Audio.FFmpeg;

public unsafe class FFmpegException(string message, int errorCode) : Exception(FormatMessage(message, errorCode))
{
	public int ErrorCode { get; } = errorCode;

	private static string FormatMessage(string message, int errorCode)
	{
		const int bufferSize = 1024;

		byte* buffer = stackalloc byte[bufferSize];

		ffmpeg.av_make_error_string(buffer, bufferSize, errorCode);

		return $"{message} (Error code: {errorCode}, {Marshal.PtrToStringUTF8((IntPtr)buffer) ?? "Unknown error"})";
	}
}
