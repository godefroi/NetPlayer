using FFmpeg.AutoGen;
using SDL3;

namespace SongServer.Audio;

public enum SampleFormat
{
	None,
	FloatPlanarLE,
	FloatPlanarBE,
	F32LE,
	F32BE,
	S16LE,
	S16BE,
}

public static class SampleFormatExtensions
{
	public static AVSampleFormat AsAVSampleFormat(this SampleFormat format) => format switch {
		SampleFormat.F32LE when BitConverter.IsLittleEndian => AVSampleFormat.AV_SAMPLE_FMT_FLT,
		SampleFormat.F32LE when !BitConverter.IsLittleEndian => throw new InvalidOperationException("ffmpeg must always deal in native endianness."),
		SampleFormat.F32BE when BitConverter.IsLittleEndian => throw new InvalidOperationException("ffmpeg must always deal in native endianness."),
		SampleFormat.F32BE when !BitConverter.IsLittleEndian => AVSampleFormat.AV_SAMPLE_FMT_FLT,
		SampleFormat.S16LE when BitConverter.IsLittleEndian => AVSampleFormat.AV_SAMPLE_FMT_S16,
		SampleFormat.S16LE when !BitConverter.IsLittleEndian => throw new InvalidOperationException("ffmpeg must always deal in native endianness."),
		SampleFormat.S16BE when BitConverter.IsLittleEndian => throw new InvalidOperationException("ffmpeg must always deal in native endianness."),
		SampleFormat.S16BE when !BitConverter.IsLittleEndian => AVSampleFormat.AV_SAMPLE_FMT_S16,
		_ => throw new NotImplementedException($"The sample format {format} is not yet implemented for conversion to FFmpeg AVSampleFormat."),
	};

	public static SampleFormat AsFormat(this SDL.AudioFormat format) => format switch {
		SDL.AudioFormat.AudioS16LE => SampleFormat.S16LE,
		SDL.AudioFormat.AudioS16BE => SampleFormat.S16BE,
		SDL.AudioFormat.AudioF32LE => SampleFormat.F32LE,
		SDL.AudioFormat.AudioF32BE => SampleFormat.F32BE,
		_ => throw new NotImplementedException($"Format conversion for SDL format {format} is not yet implemented."),
	};
}

public record PcmAudioSpec(SampleFormat Format, int Channels, int SampleFrequency)
{
	public static PcmAudioSpec Invalid { get; } = new(SampleFormat.None, 0, 0);
}
