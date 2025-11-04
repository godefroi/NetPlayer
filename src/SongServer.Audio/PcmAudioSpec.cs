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
		_ => throw new NotImplementedException($"The sample format {format} is not yet implemented for conversion to FFmpeg AVSampleFormat."),
	};

	public static SDL.AudioFormat AsSdlFormat(this SampleFormat format) => format switch {
		SampleFormat.FloatPlanarLE => SDL.AudioFormat.AudioF32LE,
		SampleFormat.FloatPlanarBE => SDL.AudioFormat.AudioF32BE,
		_ => throw new NotImplementedException($"SDL sample format conversion for format {format} is not yet implemented."),
	};

	public static SampleFormat AsFormat(this SDL.AudioFormat format) => format switch {
		SDL.AudioFormat.AudioF32LE => SampleFormat.F32LE,
		SDL.AudioFormat.AudioF32BE => SampleFormat.F32BE,
		_ => throw new NotImplementedException($"Format conversion for SDL format {format} is not yet implemented."),
	};

	public static SampleFormat AsFormat(this AVSampleFormat format) => format switch {
		AVSampleFormat.AV_SAMPLE_FMT_FLTP when BitConverter.IsLittleEndian => SampleFormat.FloatPlanarLE,
		AVSampleFormat.AV_SAMPLE_FMT_FLTP when !BitConverter.IsLittleEndian => SampleFormat.FloatPlanarBE,
		AVSampleFormat.AV_SAMPLE_FMT_S16 when BitConverter.IsLittleEndian => SampleFormat.S16LE,
		AVSampleFormat.AV_SAMPLE_FMT_S16 when !BitConverter.IsLittleEndian => SampleFormat.S16BE,
		_ => throw new NotImplementedException($"FFmpeg sample format {format} is not yet implemented for audio spec conversion."),
	};

	public static PcmAudioSpec ToPcmAudioSpec(this SDL.AudioSpec sdlSpec) => new(
		sdlSpec.Format.AsFormat(),
		sdlSpec.Channels == 2 ? sdlSpec.Channels : throw new NotSupportedException("Channel counts other than 2 are not supported because I don't know how to make a channel layout"),
		sdlSpec.Freq
	);

}


public record PcmAudioSpec(SampleFormat Format, int Channels, int SampleFrequency)
{
	public static PcmAudioSpec Invalid { get; } = new(SampleFormat.None, 0, 0);
}
