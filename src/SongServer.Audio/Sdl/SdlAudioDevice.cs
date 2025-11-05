using SDL3;

namespace SongServer.Audio.Sdl;

public sealed class SdlAudioDevice : IDisposable
{
	private bool _disposed;
	private uint _deviceId;
	private SDL.AudioSpec _audioSpec;

	public SdlAudioDevice(string? deviceName = null, SDL.AudioSpec? desiredSpec = null, float gain = 1.0f)
	{
		var devId = SDL.AudioDeviceDefaultPlayback;

		if (!string.IsNullOrWhiteSpace(deviceName)) {
			var deviceIds = SDL.GetAudioPlaybackDevices(out var cnt);

			if (deviceIds == null || cnt == 0) {
				Console.Error.WriteLine($"Error retrieving device list: {SDL.GetError()}");
				return;
			}
			
			var deviceDict = deviceIds
				.Select(d => (Name: SDL.GetAudioDeviceName(d), Id: d))
				.Where(d => !string.IsNullOrWhiteSpace(d.Name))
				.ToDictionary(d => d.Name!, d => d.Id);

			if (!deviceDict.TryGetValue(deviceName, out devId)) {
				throw new InvalidOperationException("The specified audio output device was not found.");
			}
		}

		if (desiredSpec == null) {
			if (!SDL.GetAudioDeviceFormat(devId, out var dSpec, out _)) {
				throw new Exception("Unable to retrieve default audio device format");
			}

			desiredSpec = dSpec;
		}

		_deviceId = SDL.OpenAudioDevice(devId, desiredSpec.Value);

		if (_deviceId == 0) {
			throw new Exception($"Unable to open audio device: {SDL.GetError()}");
		}

		if (!SDL.GetAudioDeviceFormat(_deviceId, out var devSpec, out _)) {
			SDL.CloseAudioDevice(_deviceId);
			_deviceId = 0;
			throw new Exception("Unable to retrieve audio format from device");
		}

		SDL.SetAudioDeviceGain(_deviceId, gain);

		_audioSpec = devSpec;
	}

	~SdlAudioDevice() => Dispose(false);

	public SDL.AudioSpec AudioSpec => _disposed ? throw new ObjectDisposedException(nameof(SdlAudioDevice)) : _audioSpec;

	public uint DeviceId => _disposed ? throw new ObjectDisposedException(nameof(SdlAudioDevice)) : _deviceId;

	public bool IsPaused => SDL.AudioDevicePaused(_deviceId);

	public bool Pause() => SDL.PauseAudioDevice(_deviceId);

	public bool Resume() => SDL.ResumeAudioDevice(_deviceId);

	public void Dispose()
	{
		Dispose(true);
		GC.SuppressFinalize(this);
	}

	private void Dispose(bool disposing)
	{
		if (_disposed) {
			return;
		}

		if (disposing) {
			// TODO: dispose managed state (managed objects)
		}

		// free unmanaged resources
		if (_deviceId != 0) {
			SDL.CloseAudioDevice(_deviceId);
			_deviceId = 0;
		}

		_disposed = true;
	}

	public static SDL.AudioSpec GetDeviceAudioSpec(uint deviceId = SDL.AudioDeviceDefaultPlayback)
	{
		if (!SDL.GetAudioDeviceFormat(deviceId, out var dSpec, out _)) {
			throw new Exception("Unable to retrieve default audio device format");
		}

		return dSpec;
	}

	public static void ListAudioDevices()
	{
		var deviceIds = SDL.GetAudioPlaybackDevices(out var cnt);

		if (deviceIds == null || cnt == 0) {
			Console.Error.WriteLine($"Error retrieving device list: {SDL.GetError()}");
			return;
		}

		var defaultName = SDL.GetAudioDeviceName(SDL.AudioDeviceDefaultPlayback);

		foreach (var devId in deviceIds) {
			var devName = SDL.GetAudioDeviceName(devId);
			Console.WriteLine($"Name: [{devName}], default: {devName == defaultName}");
		}

		Console.WriteLine($"Default output: {defaultName}");
	}
}
