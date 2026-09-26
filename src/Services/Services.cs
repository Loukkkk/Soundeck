#define DEBUG
using NAudio.CoreAudioApi;
using NAudio.Wave.SampleProviders;
using NAudio.Wave;
using Newtonsoft.Json;
using Soundeck.Models;
using Soundeck.ViewModels;
using Microsoft.Win32;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;
using Soundeck.Views.Dialogs;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System;
namespace Soundeck.Services;
public class Id3SkippingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly long _id3Size;
    private long _position;
    public Id3SkippingStream(Stream innerStream)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        byte[] header = new byte[10];
        int bytesRead = _innerStream.Read(header, 0, 10);
        if (bytesRead == 10 && header[0] == 0x49 && header[1] == 0x44 && header[2] == 0x33)
        {
            int size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
            _id3Size = 10 + size;
        }
        else
        {
            _id3Size = 0;
        }
        _innerStream.Position = _id3Size;
        _position = 0;
    }
    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _innerStream.Length - _id3Size;
    public override long Position
    {
        get => _position;
        set
        {
            _position = value;
            _innerStream.Position = _id3Size + _position;
        }
    }
    public override void Flush() => _innerStream.Flush();
    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _innerStream.Read(buffer, offset, count);
        _position += read;
        return read;
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Begin) Position = offset;
        else if (origin == SeekOrigin.Current) Position += offset;
        else if (origin == SeekOrigin.End) Position = Length + offset;
        return Position;
    }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _innerStream.Dispose();
        base.Dispose(disposing);
    }
}
public class AudioEngine : IDisposable
{
	private class ActiveSound
	{
		public string Id { get; } = Guid.NewGuid().ToString();
		public string FilePath { get; set; } = "";
		public AudioFileReader? CableReader { get; set; }
		public ISampleProvider? CableProvider { get; set; }
		public AudioFileReader? MonitorReader { get; set; }
		public ISampleProvider? MonitorProvider { get; set; }
		public IDisposable? CableReaderAlt { get; set; }
		public IDisposable? MonitorReaderAlt { get; set; }
		public VolumeSampleProvider? CableVol { get; set; }
		public VolumeSampleProvider? MonitorVol { get; set; }
		public float Volume { get; set; } = 1f;
		public float Gain { get; set; } = 1f;
		public float NormGain { get; set; } = 1f;
		public bool IsPaused { get; set; } = false;
	}
	public record AudioDevice(string Id, string Name, bool IsInput);
	private const float MasterGain = 0.5f;
	private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
	private MixingSampleProvider? _cableMixer;
	private VolumeSampleProvider? _cableVolProv;
	private WasapiOut? _cableOut;
	private MixingSampleProvider? _monitorMixer;
	private VolumeSampleProvider? _monitorVolProv;
	private WasapiOut? _monitorOut;
	private WasapiCapture? _micCapture;
	private BufferedWaveProvider? _micBuffer;
	private VolumeSampleProvider? _micVolProv;
	private readonly List<ActiveSound> _activeSounds = new List<ActiveSound>();
	private readonly object _soundsLock = new object();
	private static MMDeviceEnumerator? _sharedEnumerator;
	private static string? _ffmpegCache;
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, float> _peakCache = new();
	public bool IsRunning { get; private set; }
	public bool Polyphony { get; set; } = false;
	private bool _normalize = true;
	public bool Normalize
	{
		get => _normalize;
		set => UpdateNormalization(value);
	}
	public float MasterVolume { get; set; } = 0.8f;
	public float MonitorVolume { get; set; } = 1.0f;
	public float MicVolume { get; set; } = 1f;
	public float MicLevel { get; private set; }
	public static float CurrentMicLevel { get; private set; }
	public static Soundeck.Models.AppConfig Config { get; set; }
	public float MasterLevel { get; private set; }
	private static MMDeviceEnumerator SharedEnumerator => _sharedEnumerator ?? (_sharedEnumerator = new MMDeviceEnumerator());
	public static List<AudioDevice> GetInputDevices()
	{
		List<AudioDevice> list = new List<AudioDevice>();
		try
		{
			foreach (MMDevice item in SharedEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
			{
				list.Add(new AudioDevice(item.ID, item.FriendlyName, IsInput: true));
			}
		}
		catch
		{
		}
		return list;
	}
	public static List<AudioDevice> GetOutputDevices()
	{
		List<AudioDevice> list = new List<AudioDevice>();
		try
		{
			foreach (MMDevice item in SharedEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				list.Add(new AudioDevice(item.ID, item.FriendlyName, IsInput: false));
			}
		}
		catch
		{
		}
		return list;
	}
	public static bool IsVbCableInstalled()
	{
		return FindVbCableDevice() != null;
	}
	public static MMDevice? FindVbCableDevice()
	{
		try
		{
			foreach (MMDevice item in SharedEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
			{
				string text = item.FriendlyName.ToLowerInvariant();
				if (text.Contains("voicemeeter") || text.Contains("voicemod") || (!text.Contains("cable input") && !text.Contains("vb-audio virtual cable") && !text.Contains("vbcable")))
				{
					continue;
				}
				return item;
			}
		}
		catch
		{
		}
		return null;
	}
	private static MMDevice? GetDefaultRenderDevice()
	{
		try
		{
			return new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
		}
		catch
		{
			return null;
		}
	}
	public (bool Success, string? Error) Start(string? micDeviceId, string? vbCableDeviceId, string? monitorDeviceId)
	{
		Stop();
		try
		{
			_cableMixer = new MixingSampleProvider(MixFormat)
			{
				ReadFully = true
			};
			_cableVolProv = new VolumeSampleProvider(_cableMixer)
			{
				Volume = MasterVolume * 0.5f
			};
			if (!string.IsNullOrEmpty(vbCableDeviceId))
			{
				try
				{
					MMDevice device = new MMDeviceEnumerator().GetDevice(vbCableDeviceId);
					_cableOut = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, 50);
					var duckedCable = new DuckingSampleProvider(_cableVolProv);
					_cableOut.Init(duckedCable);
					_cableOut.Play();
				}
				catch (Exception ex)
				{
					return (Success: false, Error: "Impossible d'ouvrir la sortie VB-Cable : " + ex.Message);
				}
			}
			_monitorMixer = new MixingSampleProvider(MixFormat)
			{
				ReadFully = true
			};
			_monitorVolProv = new VolumeSampleProvider(_monitorMixer)
			{
				Volume = MonitorVolume * 0.5f
			};
			MMDevice mMDevice = null;
			try
			{
				if (!string.IsNullOrEmpty(monitorDeviceId))
				{
					mMDevice = new MMDeviceEnumerator().GetDevice(monitorDeviceId);
				}
			}
			catch
			{
				mMDevice = null;
			}
			if (mMDevice == null)
			{
				mMDevice = GetDefaultRenderDevice();
			}
			if (mMDevice != null)
			{
				try
				{
					_monitorOut = new WasapiOut(mMDevice, AudioClientShareMode.Shared, useEventSync: true, 50);
					var duckedMonitor = new DuckingSampleProvider(_monitorVolProv);
					_monitorOut.Init(duckedMonitor);
					_monitorOut.Play();
				}
				catch
				{
					_monitorOut = null;
				}
			}
			if (!string.IsNullOrEmpty(micDeviceId))
			{
				try
				{
					MMDevice device2 = new MMDeviceEnumerator().GetDevice(micDeviceId);
					_micCapture = new WasapiCapture(device2);
					_micBuffer = new BufferedWaveProvider(_micCapture.WaveFormat)
					{
						DiscardOnBufferOverflow = true,
						BufferDuration = TimeSpan.FromSeconds(2.0)
					};
					_micCapture.DataAvailable += delegate(object? _, WaveInEventArgs e)
					{
						_micBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
						try {
							float max = 0;
							if (_micCapture.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat) {
								for (int i = 0; i < e.BytesRecorded; i += 4) {
									float val = Math.Abs(BitConverter.ToSingle(e.Buffer, i));
									if (val > max) max = val;
								}
							} else if (_micCapture.WaveFormat.Encoding == WaveFormatEncoding.Pcm && _micCapture.WaveFormat.BitsPerSample == 16) {
								for (int i = 0; i < e.BytesRecorded; i += 2) {
									float val = Math.Abs((float)BitConverter.ToInt16(e.Buffer, i) / 32768f);
									if (val > max) max = val;
								}
							}
							CurrentMicLevel = max;
						} catch { }
					};
					ISampleProvider sampleProvider = _micBuffer.ToSampleProvider();
					if (sampleProvider.WaveFormat.SampleRate != 48000 || sampleProvider.WaveFormat.Channels != 2)
					{
						sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 48000);
						if (_micBuffer.WaveFormat.Channels == 1)
						{
							sampleProvider = sampleProvider.ToStereo();
						}
					}
				_micVolProv = new VolumeSampleProvider(sampleProvider)
				{
					Volume = MicVolume * 2.0f
				};
					_cableMixer.AddMixerInput(_micVolProv);
					_micCapture.StartRecording();
				}
				catch (Exception ex2)
				{
					_micCapture = null;
					Debug.WriteLine("Mic capture error: " + ex2.Message);
				}
			}
			IsRunning = true;
			return (Success: true, Error: null);
		}
		catch (Exception ex3)
		{
			Stop();
			return (Success: false, Error: ex3.Message);
		}
	}
	public void Stop()
	{
		IsRunning = false;
		lock (_soundsLock)
		{
			_activeSounds.Clear();
		}
		try
		{
			_micCapture?.StopRecording();
		}
		catch
		{
		}
		_micCapture?.Dispose();
		_micCapture = null;
		_micBuffer = null;
		_micVolProv = null;
		_cableOut?.Stop();
		_cableOut?.Dispose();
		_cableOut = null;
		_monitorOut?.Stop();
		_monitorOut?.Dispose();
		_monitorOut = null;
		_cableMixer = null;
		_cableVolProv = null;
		_monitorMixer = null;
		_monitorVolProv = null;
	}
	public string? EnsureOpusConverted(string opusPath)
	{
		return ConvertToWavViaffmpeg(opusPath);
	}
	private class CustomDisposable : IDisposable
	{
		private Action _action;
		public CustomDisposable(Action action) { _action = action; }
		public void Dispose() { _action?.Invoke(); }
	}
	private string RemuxDashToMp4Viaffmpeg(string sourcePath)
	{
		try
		{
			string text = Path.Combine(Path.GetTempPath(), "Soundeck_Temp");
			Directory.CreateDirectory(text);
			string text2 = Path.Combine(text, Guid.NewGuid().ToString() + ".mp4");
			Process process = new Process();
			process.StartInfo.FileName = "ffmpeg";
			process.StartInfo.Arguments = $"-y -i \"{sourcePath}\" -c:a copy -f mp4 \"{text2}\"";
			process.StartInfo.UseShellExecute = false;
			process.StartInfo.CreateNoWindow = true;
			process.Start();
			process.WaitForExit();
			if (process.ExitCode == 0 && File.Exists(text2)) return text2;
		}
		catch { }
		return null;
	}
	public async Task<string?> PlaySoundAsync(string filePath, float volume = 1f, bool loop = false)
	{
		if (!IsRunning)
		{
			return null;
		}
		if (!Polyphony)
		{
			StopAllSounds();
		}
		string filePath2 = filePath;
		string text = Path.GetExtension(filePath).ToLowerInvariant();
		if (text == ".opus")
		{
			string text2 = ConvertToWavViaffmpeg(filePath);
			if (text2 != null)
			{
				filePath = text2;
			}
		}
		ActiveSound activeSound = new ActiveSound
		{
			FilePath = filePath2,
			Volume = volume
		};
		bool flag = false;
		if (_cableMixer != null)
		{
			var tuple = await BuildProviderAsync(filePath, volume, loop);
			if (tuple.HasValue)
			{
				_cableMixer.AddMixerInput(tuple.Value.Item1);
				activeSound.CableReader = tuple.Value.Item2;
				activeSound.CableReaderAlt = tuple.Value.Item3;
				activeSound.CableProvider = tuple.Value.Item1;
				activeSound.CableVol = tuple.Value.Item4;
				activeSound.Gain = tuple.Value.Item5;
				activeSound.NormGain = tuple.Value.Item6;
				flag = true;
			}
		}
		if (_monitorMixer != null)
		{
			var tuple2 = await BuildProviderAsync(filePath, volume, loop);
			if (tuple2.HasValue)
			{
				_monitorMixer.AddMixerInput(tuple2.Value.Item1);
				activeSound.MonitorReader = tuple2.Value.Item2;
				activeSound.MonitorReaderAlt = tuple2.Value.Item3;
				activeSound.MonitorProvider = tuple2.Value.Item1;
				activeSound.MonitorVol = tuple2.Value.Item4;
				activeSound.Gain = tuple2.Value.Item5;
				activeSound.NormGain = tuple2.Value.Item6;
				flag = true;
			}
		}
		if (!flag)
		{
			return null;
		}
		lock (_soundsLock)
		{
			_activeSounds.Add(activeSound);
		}
		return activeSound.Id;
	}
	private async Task<(ISampleProvider Provider, AudioFileReader? Reader, IDisposable? ReaderAlt, VolumeSampleProvider? VolProvider, float Gain, float NormGain)?> BuildProviderAsync(string filePath, float volume, bool loop)
	{
		return await Task.Run<(ISampleProvider Provider, AudioFileReader? Reader, IDisposable? ReaderAlt, VolumeSampleProvider? VolProvider, float Gain, float NormGain)?>(() =>
		{
			try
			{
				AudioFileReader audioFileReader = null;
				IDisposable item = null;
				ISampleProvider sampleProvider = null;
				WaveFormat waveFormat = null;
				float gain = 1f;
				string ext = Path.GetExtension(filePath).ToLowerInvariant();
				try
				{
					audioFileReader = new AudioFileReader(filePath);
					sampleProvider = audioFileReader;
					waveFormat = audioFileReader.WaveFormat;
				}
				catch
				{
					audioFileReader = null;
				}
				if (sampleProvider == null && (ext == ".m4a" || ext == ".mp4" || ext == ".mp3"))
				{
					try
					{
						var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
						var id3Stream = new Id3SkippingStream(fs);
						var mfr = new StreamMediaFoundationReader(id3Stream);
						item = new CustomDisposable(() => { mfr.Dispose(); id3Stream.Dispose(); });
						sampleProvider = mfr.ToSampleProvider();
						waveFormat = mfr.WaveFormat;
					}
					catch
					{
						item?.Dispose();
						item = null;
					}
				}
				if (sampleProvider == null && (ext == ".m4a" || ext == ".mp4"))
				{
					bool isDash = false;
					try
					{
						using var fs = File.OpenRead(filePath);
						if (fs.Length > 12)
						{
							byte[] buf = new byte[12];
							fs.Read(buf, 0, 12);
							string sig = System.Text.Encoding.ASCII.GetString(buf, 4, 8);
							if (sig == "ftypdash" || sig.Contains("dash")) isDash = true;
						}
					}
					catch { }
					if (isDash)
					{
						string remuxedPath = RemuxDashToMp4Viaffmpeg(filePath);
						if (remuxedPath != null)
						{
							try
							{
								var mfr = new MediaFoundationReader(remuxedPath);
								item = new CustomDisposable(() => { mfr.Dispose(); try { File.Delete(remuxedPath); } catch { } });
								sampleProvider = mfr.ToSampleProvider();
								waveFormat = mfr.WaveFormat;
							}
							catch
							{
								item?.Dispose();
								item = null;
							}
						}
					}
				}
				if (sampleProvider == null)
				{
					string wavPath = ConvertToWavViaffmpeg(filePath);
					if (wavPath != null)
					{
						try
						{
							audioFileReader = new AudioFileReader(wavPath);
							item = new CustomDisposable(() => { audioFileReader.Dispose(); try { File.Delete(wavPath); } catch { } });
							sampleProvider = audioFileReader;
							waveFormat = audioFileReader.WaveFormat;
						}
						catch
						{
							item?.Dispose();
							item = null;
						}
					}
				}
				if (sampleProvider == null) return null;
				if (waveFormat.SampleRate != 48000 || waveFormat.Channels != 2)
				{
					sampleProvider = new WdlResamplingSampleProvider(sampleProvider, 48000);
					if (waveFormat.Channels == 1)
					{
						sampleProvider = sampleProvider.ToStereo();
					}
				}
				if (loop && audioFileReader != null)
				{
					sampleProvider = new LoopingSampleProvider(audioFileReader, sampleProvider);
				}
				string cacheKey = filePath;
				try
				{
					if (File.Exists(filePath))
						cacheKey = $"{filePath}|{new FileInfo(filePath).Length}|{File.GetLastWriteTimeUtc(filePath).Ticks}";
				}
				catch { }

				float maxPeak = 0.001f;
				if (!_peakCache.TryGetValue(cacheKey, out maxPeak))
				{
					maxPeak = 0.001f;
					if (audioFileReader != null)
					{
						try 
						{
							float[] scanBuf = new float[48000];
							int r;
							while ((r = audioFileReader.Read(scanBuf, 0, scanBuf.Length)) > 0)
							{
								for (int i = 0; i < r; i++)
								{
									float v = Math.Abs(scanBuf[i]);
									if (v > maxPeak) maxPeak = v;
								}
							}
							audioFileReader.Position = 0;
							_peakCache[cacheKey] = maxPeak;
						}
						catch { }
					}
				}

				float target = (AudioEngine.Config != null && AudioEngine.Config.MicPeakLevel > 0) 
					? (AudioEngine.Config.MicPeakLevel * AudioEngine.Config.VoiceTargetRatio) 
					: 0.3f;
				float normGain = (maxPeak > 0.0001f) ? (target / maxPeak) : 1f;

				bool isNormalizeActive = (AudioEngine.Config != null ? AudioEngine.Config.Normalize : _normalize);
				gain = isNormalizeActive ? normGain : 1f;

				VolumeSampleProvider volumeSampleProvider = new VolumeSampleProvider(sampleProvider)
				{
					Volume = volume * gain * 0.5f
				};
				sampleProvider = volumeSampleProvider;
				return (sampleProvider, audioFileReader, item, volumeSampleProvider, gain, normGain);
			}
			catch (Exception ex)
			{
				Debug.WriteLine("BuildProviderAsync error: " + ex.Message);
				return null;
			}
		});
	}
	public void PauseSound(string soundId)
	{
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId);
			if (activeSound != null && !activeSound.IsPaused)
			{
				activeSound.IsPaused = true;
				try { if (activeSound.CableProvider != null) _cableMixer?.RemoveMixerInput(activeSound.CableProvider); } catch { }
				try { if (activeSound.MonitorProvider != null) _monitorMixer?.RemoveMixerInput(activeSound.MonitorProvider); } catch { }
			}
		}
	}
	public void ResumeSound(string soundId)
	{
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId);
			if (activeSound != null && activeSound.IsPaused)
			{
				activeSound.IsPaused = false;
				try { if (activeSound.CableProvider != null) _cableMixer?.AddMixerInput(activeSound.CableProvider); } catch { }
				try { if (activeSound.MonitorProvider != null) _monitorMixer?.AddMixerInput(activeSound.MonitorProvider); } catch { }
			}
		}
	}
	public void StopSound(string soundId)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId2);
			if (activeSound != null)
			{
				_activeSounds.Remove(activeSound);
				RemoveAndDispose(activeSound);
			}
		}
	}
	public void StopAllSounds()
	{
		lock (_soundsLock)
		{
			foreach (ActiveSound activeSound in _activeSounds)
			{
				RemoveAndDispose(activeSound);
			}
			_activeSounds.Clear();
		}
	}
	private void RemoveAndDispose(ActiveSound s)
	{
		try { if (s.CableProvider != null) _cableMixer?.RemoveMixerInput(s.CableProvider); } catch { }
		try { if (s.MonitorProvider != null) _monitorMixer?.RemoveMixerInput(s.MonitorProvider); } catch { }
		Task.Run(() => {
			try { s.CableReader?.Dispose(); } catch { }
			try { s.MonitorReader?.Dispose(); } catch { }
			try { s.CableReaderAlt?.Dispose(); } catch { }
			try { s.MonitorReaderAlt?.Dispose(); } catch { }
		});
	}
	public string? GetActiveEngineId(string filePath)
	{
		string filePath2 = filePath;
		lock (_soundsLock)
		{
			return _activeSounds.FirstOrDefault((ActiveSound x) => string.Equals(x.FilePath, filePath2, StringComparison.OrdinalIgnoreCase))?.Id;
		}
	}
	public bool IsSoundPlaying(string soundId)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			return _activeSounds.Any((ActiveSound s) => s.Id == soundId2);
		}
	}
	public double GetCurrentTime(string soundId)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId2);
			if (activeSound == null)
			{
				return 0.0;
			}
			if (activeSound.MonitorReader != null)
			{
				return activeSound.MonitorReader.CurrentTime.TotalSeconds;
			}
			if (activeSound.CableReader != null)
			{
				return activeSound.CableReader.CurrentTime.TotalSeconds;
			}
			return ((activeSound.MonitorReaderAlt ?? activeSound.CableReaderAlt) is MediaFoundationReader { CurrentTime: var currentTime }) ? currentTime.TotalSeconds : 0.0;
		}
	}
	public double GetTotalTime(string soundId)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId2);
			if (activeSound == null)
			{
				return 0.0;
			}
			if (activeSound.MonitorReader != null)
			{
				return activeSound.MonitorReader.TotalTime.TotalSeconds;
			}
			if (activeSound.CableReader != null)
			{
				return activeSound.CableReader.TotalTime.TotalSeconds;
			}
			return ((activeSound.MonitorReaderAlt ?? activeSound.CableReaderAlt) is MediaFoundationReader { TotalTime: var totalTime }) ? totalTime.TotalSeconds : 0.0;
		}
	}
	public void SeekSound(string soundId, double seconds)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId2);
			if (activeSound == null)
			{
				return;
			}
			TimeSpan currentTime = TimeSpan.FromSeconds(Math.Max(0.0, seconds));
			try
			{
				if (activeSound.MonitorReader != null)
				{
					activeSound.MonitorReader.CurrentTime = currentTime;
				}
			}
			catch
			{
			}
			try
			{
				if (activeSound.CableReader != null)
				{
					activeSound.CableReader.CurrentTime = currentTime;
				}
			}
			catch
			{
			}
			MediaFoundationReader mediaFoundationReader = activeSound.MonitorReaderAlt as MediaFoundationReader;
			MediaFoundationReader mediaFoundationReader2 = activeSound.CableReaderAlt as MediaFoundationReader;
			try
			{
				if (mediaFoundationReader != null)
				{
					mediaFoundationReader.CurrentTime = currentTime;
				}
			}
			catch
			{
			}
			try
			{
				if (mediaFoundationReader2 != null)
				{
					mediaFoundationReader2.CurrentTime = currentTime;
				}
			}
			catch
			{
			}
		}
	}
	public void UpdateMonitorVolume(float volume)
	{
		MonitorVolume = volume;
		if (_monitorVolProv != null)
		{
			_monitorVolProv.Volume = volume * MasterVolume * 0.5f;
		}
	}
	public void UpdateNormalization(bool normalize)
	{
		_normalize = normalize;
		if (Config != null) Config.Normalize = normalize;
		lock (_soundsLock)
		{
			foreach (var sound in _activeSounds)
			{
				sound.Gain = normalize ? sound.NormGain : 1f;
				float vol2 = sound.Volume * sound.Gain * 0.5f;
				if (sound.CableVol != null) sound.CableVol.Volume = vol2;
				if (sound.MonitorVol != null) sound.MonitorVol.Volume = vol2;
			}
		}
	}
	public void UpdateVoiceTargetRatio(float newRatio)
	{
		if (Config == null || Config.MicPeakLevel <= 0) return;
		float oldRatio = Config.VoiceTargetRatio;
		if (oldRatio <= 0.001f) oldRatio = 0.001f;
		Config.VoiceTargetRatio = newRatio;
		float multiplier = newRatio / oldRatio;
		lock (_soundsLock)
		{
			foreach (var sound in _activeSounds)
			{
				sound.NormGain *= multiplier;
				if (_normalize)
				{
					sound.Gain = sound.NormGain;
					float vol2 = sound.Volume * sound.Gain * 0.5f;
					if (sound.CableVol != null) sound.CableVol.Volume = vol2;
					if (sound.MonitorVol != null) sound.MonitorVol.Volume = vol2;
				}
			}
		}
	}
	public void UpdateMasterVolume(float volume)
	{
		MasterVolume = volume;
		if (_cableVolProv != null)
		{
			_cableVolProv.Volume = volume * 0.5f;
		}
		if (_monitorVolProv != null)
		{
			_monitorVolProv.Volume = MonitorVolume * volume * 0.5f;
		}
	}
	public void UpdateMicVolume(float volume)
	{
		MicVolume = volume;
		if (_micVolProv != null)
		{
			_micVolProv.Volume = volume * 2.0f;
		}
	}
	public void SetSoundVolume(string soundId, float volume)
	{
		string soundId2 = soundId;
		lock (_soundsLock)
		{
			ActiveSound activeSound = _activeSounds.FirstOrDefault((ActiveSound x) => x.Id == soundId2);
			if (activeSound != null)
			{
				activeSound.Volume = volume;
				float volume2 = volume * activeSound.Gain * 0.5f;
				if (activeSound.CableVol != null)
				{
					activeSound.CableVol.Volume = volume2;
				}
				if (activeSound.MonitorVol != null)
				{
					activeSound.MonitorVol.Volume = volume2;
				}
			}
		}
	}
	public List<(string Id, string FilePath)> CleanupFinishedSounds()
	{
		List<(string, string)> list = new List<(string, string)>();
		lock (_soundsLock)
		{
			foreach (ActiveSound item in _activeSounds.ToList())
			{
				double num = 0.0;
				try
				{
					num = ((item.MonitorReader != null) ? item.MonitorReader.TotalTime.TotalSeconds : ((item.CableReader == null) ? (((item.MonitorReaderAlt ?? item.CableReaderAlt) is MediaFoundationReader { TotalTime: var totalTime }) ? totalTime.TotalSeconds : 0.0) : item.CableReader.TotalTime.TotalSeconds));
				}
				catch
				{
				}
				if (!(num < 0.1))
				{
					double num2 = 0.0;
					try
					{
						num2 = ((item.MonitorReader != null) ? item.MonitorReader.CurrentTime.TotalSeconds : ((item.CableReader == null) ? (((item.MonitorReaderAlt ?? item.CableReaderAlt) is MediaFoundationReader { CurrentTime: var currentTime }) ? currentTime.TotalSeconds : 0.0) : item.CableReader.CurrentTime.TotalSeconds));
					}
					catch
					{
					}
					if (num2 >= num - 0.15)
					{
						list.Add((item.Id, item.FilePath));
						_activeSounds.Remove(item);
						RemoveAndDispose(item);
					}
				}
			}
		}
		return list;
	}
	private static string? FindFfmpeg()
	{
		if (_ffmpegCache != null && File.Exists(_ffmpegCache))
		{
			return _ffmpegCache;
		}
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string item = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
		List<string> list = new List<string>
		{
			item,
			Path.Combine(folderPath, "Soundeck", "ffmpeg"),
			Path.Combine(folderPath, "Soundeck"),
			"C:\\ffmpeg\\bin",
			"C:\\ffmpeg",
			"C:\\Program Files\\ffmpeg\\bin"
		};
		list.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
		foreach (string item2 in list)
		{
			if (!string.IsNullOrEmpty(item2))
			{
				string text = Path.Combine(item2, "ffmpeg.exe");
				if (File.Exists(text))
				{
					_ffmpegCache = text;
					return text;
				}
			}
		}
		return null;
	}
	private static string? ConvertToWavViaffmpeg(string sourcePath)
	{
		try
		{
			string text = FindFfmpeg();
			if (text == null)
			{
				return null;
			}
			string text2 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "tmp_wav");
			Directory.CreateDirectory(text2);
			string text3 = Path.Combine(text2, Path.GetFileNameWithoutExtension(sourcePath) + "_" + Math.Abs(sourcePath.GetHashCode()) + ".wav");
			if (File.Exists(text3))
			{
				return text3;
			}
			ProcessStartInfo startInfo = new ProcessStartInfo
			{
				FileName = text,
				Arguments = $"-y -i \"{sourcePath}\" -ar 48000 -ac 2 \"{text3}\"",
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true,
				RedirectStandardOutput = true
			};
			Process process = Process.Start(startInfo);
			if (process == null)
			{
				return null;
			}
			string value = process.StandardError.ReadToEnd();
			process.WaitForExit(30000);
			if (!File.Exists(text3))
			{
				try
				{
					string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "ffmpeg_error.log");
					File.WriteAllText(path, $"[{DateTime.Now}]\nSource: {sourcePath}\nffmpeg: {text}\nStderr:\n{value}");
				}
				catch
				{
				}
				return null;
			}
			return text3;
		}
		catch
		{
			return null;
		}
	}
	public void Dispose()
	{
		Stop();
		GC.SuppressFinalize(this);
	}
}
	public 	class DuckingSampleProvider : ISampleProvider
	{
		private readonly ISampleProvider _source;
		private float _currentMultiplier = 1.0f;
		private float _attackCoef = 0.0001f;
		private float _releaseCoef = 0.000005f;
		private DateTime _lastSpoke = DateTime.MinValue;
		public DuckingSampleProvider(ISampleProvider source)
		{
			_source = source;
		}
		public WaveFormat WaveFormat => _source.WaveFormat;
		public int Read(float[] buffer, int offset, int count)
		{
			int read = _source.Read(buffer, offset, count);
			if (read == 0) return 0;
			if (AudioEngine.Config != null && AudioEngine.Config.AutoDucking)
			{
				if (AudioEngine.CurrentMicLevel > 0.02f) 
				{
					_lastSpoke = DateTime.UtcNow;
				}
				float targetMultiplier = 1.0f;
				if ((DateTime.UtcNow - _lastSpoke).TotalMilliseconds < 1200) // Hold 1200ms
				{
					targetMultiplier = 1.0f - AudioEngine.Config.DuckingStrength;
					if (targetMultiplier < 0.0f) targetMultiplier = 0.0f;
				}
				for (int i = 0; i < read; i++)
				{
					float coef = targetMultiplier < _currentMultiplier ? _attackCoef : _releaseCoef;
					_currentMultiplier += (targetMultiplier - _currentMultiplier) * coef;
					buffer[offset + i] *= _currentMultiplier;
				}
			}
			else 
			{
				_currentMultiplier = 1.0f;
			}
			return read;
		}
	}
	public class LoopingSampleProvider : ISampleProvider
{
	private readonly AudioFileReader _reader;
	private readonly ISampleProvider _inner;
	public WaveFormat WaveFormat => _inner.WaveFormat;
	public LoopingSampleProvider(AudioFileReader reader, ISampleProvider inner)
	{
		_reader = reader;
		_inner = inner;
	}
	public int Read(float[] buffer, int offset, int count)
	{
		int i;
		int num;
		for (i = 0; i < count; i += num)
		{
			num = _inner.Read(buffer, offset + i, count - i);
			if (num == 0)
			{
				_reader.Position = 0L;
				num = _inner.Read(buffer, offset + i, count - i);
				if (num == 0)
				{
					break;
				}
			}
		}
		return i;
	}
}
public class ConfigService
{
	private static readonly string AppDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck");
	public static readonly string ConfigPath = Path.Combine(AppDataDir, "config.json");
	public static readonly string DownloadsDir = Path.Combine(AppDataDir, "downloads");
	public AppConfig Load()
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			Directory.CreateDirectory(DownloadsDir);
			if (!File.Exists(ConfigPath))
			{
				return new AppConfig();
			}
			string value = File.ReadAllText(ConfigPath);
			return JsonConvert.DeserializeObject<AppConfig>(value) ?? new AppConfig();
		}
		catch
		{
			return new AppConfig();
		}
	}
	public void Save(AppConfig config)
	{
		try
		{
			Directory.CreateDirectory(AppDataDir);
			File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
		}
		catch
		{
		}
	}
}
public static class AudioDeviceCache
{
	private static Task? _warmupTask;
	public static List<AudioEngine.AudioDevice> Inputs { get; private set; } = new List<AudioEngine.AudioDevice>();
	public static List<AudioEngine.AudioDevice> Outputs { get; private set; } = new List<AudioEngine.AudioDevice>();
	public static bool IsWarm { get; private set; }
	public static Task WarmupAsync()
	{
		return _warmupTask ?? (_warmupTask = RefreshAsync());
	}
	public static async Task RefreshAsync()
	{
		(List<AudioEngine.AudioDevice>, List<AudioEngine.AudioDevice>) tuple = await Task.Run(() => (AudioEngine.GetInputDevices(), AudioEngine.GetOutputDevices()));
		List<AudioEngine.AudioDevice> ins = tuple.Item1;
		List<AudioEngine.AudioDevice> outs = tuple.Item2;
		Inputs = ins;
		Outputs = outs;
		IsWarm = true;
	}
}
public class HotkeyService : IDisposable
{
	private const uint MOD_ALT = 1u;
	private const uint MOD_CONTROL = 2u;
	private const uint MOD_SHIFT = 4u;
	private const uint MOD_WIN = 8u;
	private const uint MOD_NOREPEAT = 16384u;
	private readonly Dictionary<int, Action> _hotkeys = new Dictionary<int, Action>();
	private nint _hwnd;
	private int _nextId = 9000;
	private static readonly Dictionary<string, uint> _keyToVk = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
	{
		["key:f1"] = 112u,
		["key:f2"] = 113u,
		["key:f3"] = 114u,
		["key:f4"] = 115u,
		["key:f5"] = 116u,
		["key:f6"] = 117u,
		["key:f7"] = 118u,
		["key:f8"] = 119u,
		["key:f9"] = 120u,
		["key:f10"] = 121u,
		["key:f11"] = 122u,
		["key:f12"] = 123u,
		["key:space"] = 32u,
		["key:enter"] = 13u,
		["key:tab"] = 9u,
		["key:backspace"] = 8u,
		["key:delete"] = 46u,
		["key:insert"] = 45u,
		["key:home"] = 36u,
		["key:end"] = 35u,
		["key:page_up"] = 33u,
		["key:page_down"] = 34u,
		["key:up"] = 38u,
		["key:down"] = 40u,
		["key:left"] = 37u,
		["key:right"] = 39u,
		["key:esc"] = 27u,
		["key:num_lock"] = 144u,
		["key:print_screen"] = 44u,
		["key:pause"] = 19u
	};
	[DllImport("user32.dll")]
	private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);
	[DllImport("user32.dll")]
	private static extern bool UnregisterHotKey(nint hWnd, int id);
	public void Initialize(nint hwnd)
	{
		_hwnd = hwnd;
	}
	public int Register(List<string>? keyList, Action callback)
	{
		if (keyList == null || keyList.Count == 0)
		{
			return -1;
		}
		uint num = 16384u;
		uint num2 = 0u;
		foreach (string key in keyList)
		{
			string text = key.ToLowerInvariant();
			bool flag;
			switch (text)
			{
			case "key:ctrl":
			case "key:ctrl_l":
			case "key:ctrl_r":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				num |= 2u;
				continue;
			}
			switch (text)
			{
			case "key:alt":
			case "key:alt_l":
			case "key:alt_r":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				num |= 1u;
				continue;
			}
			switch (text)
			{
			case "key:shift":
			case "key:shift_l":
			case "key:shift_r":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				num |= 4u;
				continue;
			}
			if (text == "key:cmd")
			{
				num |= 8u;
				continue;
			}
			if (text.StartsWith("char:") && text.Length == 6)
			{
				num2 = char.ToUpper(text[5]);
				continue;
			}
			if (text.StartsWith("vk:"))
			{
				string text2 = text;
				if (uint.TryParse(text2.Substring(3, text2.Length - 3), out var result))
				{
					num2 = result;
					continue;
				}
			}
			if (_keyToVk.TryGetValue(text, out var value))
			{
				num2 = value;
			}
		}
		if (num2 == 0)
		{
			return -1;
		}
		int num3 = _nextId++;
		if (RegisterHotKey(_hwnd, num3, num, num2))
		{
			_hotkeys[num3] = callback;
			return num3;
		}
		return -1;
	}
	public void Unregister(int id)
	{
		if (id >= 0)
		{
			UnregisterHotKey(_hwnd, id);
			_hotkeys.Remove(id);
		}
	}
	public void UnregisterAll()
	{
		foreach (int key in _hotkeys.Keys)
		{
			UnregisterHotKey(_hwnd, key);
		}
		_hotkeys.Clear();
	}
	public void HandleHotKeyMessage(int id)
	{
		if (_hotkeys.TryGetValue(id, out Action value))
		{
			value();
		}
	}
	public static string ComboToDisplayString(List<string>? keyList)
	{
		if (keyList == null || keyList.Count == 0)
		{
			return "";
		}
		List<string> list = new List<string>();
		List<string> list2 = new List<string>();
		foreach (string key in keyList)
		{
			string text = key.ToLowerInvariant();
			bool flag;
			switch (text)
			{
			case "key:ctrl":
			case "key:alt":
			case "key:shift":
			case "key:cmd":
				flag = true;
				break;
			default:
				flag = false;
				break;
			}
			if (flag)
			{
				list.Add(text);
			}
			else
			{
				list2.Add(key);
			}
		}
		list.Sort((string a, string b) => ModOrder(a).CompareTo(ModOrder(b)));
		IEnumerable<string> values = list.Select(HotkeyDialog.TokenToDisplay).Concat(list2.Select(HotkeyDialog.TokenToDisplay));
		return string.Join(" + ", values);
		static int ModOrder(string t)
		{
			string text2 = t.ToLowerInvariant();
			if (1 == 0)
			{
			}
			int result = text2 switch
			{
				"key:ctrl" => 0, 
				"key:alt" => 1, 
				"key:shift" => 2, 
				"key:cmd" => 3, 
				_ => 4, 
			};
			if (1 == 0)
			{
			}
			return result;
		}
	}
	public void Dispose()
	{
		UnregisterAll();
		GC.SuppressFinalize(this);
	}
}
public sealed class TrayIconService : IDisposable
{
	private struct POINT
	{
		public int X;
		public int Y;
	}
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	private struct NOTIFYICONDATA
	{
		public int cbSize;
		public nint hWnd;
		public int uID;
		public int uFlags;
		public int uCallbackMessage;
		public nint hIcon;
		[MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
		public string szTip;
	}
	private const int NIM_ADD = 0;
	private const int NIM_DELETE = 2;
	private const int NIF_MESSAGE = 1;
	private const int NIF_ICON = 2;
	private const int NIF_TIP = 4;
	private const uint MF_STRING = 0u;
	private const uint TPM_RETURNCMD = 256u;
	private const uint TPM_RIGHTBUTTON = 2u;
	public const uint WM_TRAYICON = 32769u;
	public const uint WM_LBUTTONUP = 514u;
	public const uint WM_LBUTTONDBLCLK = 515u;
	public const uint WM_RBUTTONUP = 517u;
	public const int MENU_ID_SHOW = 1001;
	public const int MENU_ID_QUIT = 1002;
	private NOTIFYICONDATA _data;
	private bool _added;
	private readonly nint _hwnd;
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);
	[DllImport("user32.dll")]
	private static extern nint CreatePopupMenu();
	[DllImport("user32.dll", CharSet = CharSet.Unicode)]
	private static extern bool AppendMenuW(nint hMenu, uint uFlags, nuint uIDNewItem, string lpNewItem);
	[DllImport("user32.dll")]
	private static extern bool DestroyMenu(nint hMenu);
	[DllImport("user32.dll")]
	private static extern bool SetForegroundWindow(nint hWnd);
	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT lpPoint);
	[DllImport("user32.dll")]
	private static extern int TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);
	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern nint ExtractIconW(nint hInst, string lpszExeFileName, int nIconIndex);
	public TrayIconService(nint hwnd, nint hIcon, string tooltip)
	{
		_hwnd = hwnd;
		_data = new NOTIFYICONDATA
		{
			cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
			hWnd = hwnd,
			uID = 1,
			uFlags = 7,
			uCallbackMessage = 32769,
			hIcon = hIcon,
			szTip = tooltip
		};
	}
	public void Show()
	{
		if (!_added)
		{
			_added = Shell_NotifyIconW(0u, ref _data);
		}
	}
	public void Hide()
	{
		if (_added)
		{
			Shell_NotifyIconW(2u, ref _data);
			_added = false;
		}
	}
	public int ShowContextMenu()
	{
		nint num = CreatePopupMenu();
		if (num == 0)
		{
			return 0;
		}
		try
		{
			AppendMenuW(num, 0u, 1001u, "Afficher Soundeck Pro");
			AppendMenuW(num, 0u, 1002u, I18n.T("Quitter"));
			GetCursorPos(out var lpPoint);
			SetForegroundWindow(_hwnd);
			return TrackPopupMenu(num, 258u, lpPoint.X, lpPoint.Y, 0, _hwnd, 0);
		}
		finally
		{
			DestroyMenu(num);
		}
	}
	public static nint LoadAppIcon()
	{
		try
		{
			string text = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}
			nint num = ExtractIconW(0, text, 0);
			return (num == 1) ? 0 : num;
		}
		catch
		{
			return 0;
		}
	}
	public void Dispose()
	{
		Hide();
	}
}
public static class PcScanService
{
	private static readonly HashSet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".flac", ".ogg", ".aiff", ".aif", ".wma", ".m4a", ".aac", ".opus" };
	private static readonly HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"appdata", "windows", "program files", "program files (x86)", "programdata", "system32", "syswow64", "$recycle.bin", "system volume information", ".git",
		"node_modules", "__pycache__", "site-packages", "dist-packages", "windowsapps", "microsoft", "temp", "tmp", "cache", "caches",
		"inetcache", "webcachelocal", "mediacache", "gpucache", "code cache", "shadercache", "blob_storage", "packages", "localstate", "roamingstate",
		"chromium"
	};
	private static readonly Regex UuidPattern = new Regex("^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}", RegexOptions.IgnoreCase);
	private static bool IsValidAudioFilename(string filename)
	{
		string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filename);
		if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
		{
			return false;
		}
		if (UuidPattern.IsMatch(fileNameWithoutExtension))
		{
			return false;
		}
		if (fileNameWithoutExtension.Length >= 16 && fileNameWithoutExtension.All((char c) => "0123456789abcdefABCDEF".Contains(c)))
		{
			return false;
		}
		return true;
	}
	public static List<string> GetScanRoots()
	{
		List<string> list = new List<string>();
		string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		string[] array = new string[9] { "Music", "Musique", "Desktop", "Bureau", "Downloads", "Téléchargements", "Documents", "Videos", "Vidéos" };
		foreach (string path in array)
		{
			string text = Path.Combine(folderPath, path);
			if (Directory.Exists(text))
			{
				list.Add(text);
			}
		}
		string[] array2 = new string[2] { "OneDrive", "OneDrive - Personal" };
		foreach (string path2 in array2)
		{
			string text2 = Path.Combine(folderPath, path2);
			if (!Directory.Exists(text2))
			{
				continue;
			}
			string[] array3 = new string[4] { "Music", "Desktop", "Downloads", "Documents" };
			foreach (string path3 in array3)
			{
				string text3 = Path.Combine(text2, path3);
				if (Directory.Exists(text3))
				{
					list.Add(text3);
				}
			}
		}
		string folderPath2 = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		string[] array4 = new string[2] { "Soundeck\\downloads", "Soundeck" };
		foreach (string path4 in array4)
		{
			string text4 = Path.Combine(folderPath2, path4);
			if (Directory.Exists(text4))
			{
				list.Add(text4);
			}
		}
		return list.Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
	}
	public static List<(string Name, string Path)> ScanAudioFiles(IEnumerable<string> roots, int maxDepth = 7, int maxFiles = 3000, CancellationToken ct = default(CancellationToken))
	{
		List<(string, string)> results = new List<(string, string)>();
		HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string root in roots)
		{
			if (Directory.Exists(root))
			{
				Walk(root, 0);
			}
		}
		return results;
		void Walk(string folder, int depth)
		{
			if (!ct.IsCancellationRequested && depth <= maxDepth && results.Count < maxFiles)
			{
				string fullPath;
				try
				{
					fullPath = Path.GetFullPath(folder);
				}
				catch
				{
					return;
				}
				if (visited.Add(fullPath))
				{
					IEnumerable<string> enumerable;
					try
					{
						enumerable = Directory.EnumerateFileSystemEntries(folder);
					}
					catch
					{
						return;
					}
					foreach (string item in enumerable)
					{
						if (ct.IsCancellationRequested || results.Count >= maxFiles)
						{
							break;
						}
						try
						{
							FileAttributes attributes = File.GetAttributes(item);
							string fileName = Path.GetFileName(item);
							if (attributes.HasFlag(FileAttributes.Directory))
							{
								if (!ExcludedDirs.Contains(fileName) && !fileName.StartsWith('.'))
								{
									Walk(item, depth + 1);
								}
							}
							else if (AudioExtensions.Contains(Path.GetExtension(item)) && IsValidAudioFilename(fileName))
							{
								results.Add((fileName, item));
							}
						}
						catch
						{
						}
					}
				}
			}
		}
	}
	public static List<(string Name, string Path)> MergeWithKnownSounds(List<(string Name, string Path)> scanned, IEnumerable<string> knownFilePaths)
	{
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		List<(string, string)> list = new List<(string, string)>();
		foreach (var (item, text) in scanned)
		{
			string item2;
			try
			{
				item2 = Path.GetFullPath(text);
			}
			catch
			{
				item2 = text;
			}
			if (hashSet.Add(item2))
			{
				list.Add((item, text));
			}
		}
		foreach (string knownFilePath in knownFilePaths)
		{
			if (!string.IsNullOrEmpty(knownFilePath) && File.Exists(knownFilePath))
			{
				string item3;
				try
				{
					item3 = Path.GetFullPath(knownFilePath);
				}
				catch
				{
					item3 = knownFilePath;
				}
				if (hashSet.Add(item3))
				{
					list.Add((Path.GetFileName(knownFilePath), knownFilePath));
				}
			}
		}
		list.Sort(((string, string) a, (string, string) b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
		return list;
	}
}
public class FreesoundService
{
	public record FreesoundResult(int Id, string Name, string Username, float Duration, string License, string PreviewUrl, string? DownloadUrl, int Downloads, float AverageRating, string Tags);
	private class FreesoundSearchResponse
	{
		[JsonPropertyName("results")]
		public List<FreesoundItem>? Results { get; set; }
	}
	private class FreesoundItem
	{
		[JsonPropertyName("id")]
		public int Id { get; set; }
		[JsonPropertyName("name")]
		public string Name { get; set; } = "";
		[JsonPropertyName("username")]
		public string Username { get; set; } = "";
		[JsonPropertyName("duration")]
		public float Duration { get; set; }
		[JsonPropertyName("license")]
		public string License { get; set; } = "";
		[JsonPropertyName("previews")]
		public FreesoundPreviews? Previews { get; set; }
		[JsonPropertyName("download")]
		public string? Download { get; set; }
		[JsonPropertyName("num_downloads")]
		public int NumDownloads { get; set; }
		[JsonPropertyName("avg_rating")]
		public float AvgRating { get; set; }
		[JsonPropertyName("tags")]
		public List<string>? Tags { get; set; }
	}
	private class FreesoundPreviews
	{
		[JsonPropertyName("preview-lq-mp3")]
		public string? PreviewMpLq { get; set; }
		[JsonPropertyName("preview-hq-mp3")]
		public string? PreviewHqMp3 { get; set; }
	}
	private const string BaseUrl = "https://freesound.org/apiv2";
	private readonly HttpClient _http = new HttpClient();
	public async Task<List<FreesoundResult>> SearchAsync(string query, string apiKey, float maxDuration = 30f, int count = 15)
	{
		string url = $"{"https://freesound.org/apiv2"}/search/text/?query={Uri.EscapeDataString(query)}&token={apiKey}&filter=duration%3A[0+TO+{maxDuration}]&fields=id,name,username,duration,license,previews,download,num_downloads,avg_rating,tags&page_size={count}";
		try
		{
			FreesoundSearchResponse json = await _http.GetFromJsonAsync<FreesoundSearchResponse>(url);
			if (json?.Results == null)
			{
				return new List<FreesoundResult>();
			}
			return json.Results.Select((FreesoundItem r) => new FreesoundResult(r.Id, r.Name, r.Username, r.Duration, r.License, r.Previews?.PreviewMpLq ?? r.Previews?.PreviewHqMp3 ?? "", r.Download, r.NumDownloads, r.AvgRating, (r.Tags != null) ? string.Join(", ", r.Tags.Take(5)) : "")).ToList();
		}
		catch
		{
			return new List<FreesoundResult>();
		}
	}
	public async Task<string?> DownloadAsync(string url, string apiKey, string destPath)
	{
		try
		{
			await File.WriteAllBytesAsync(destPath, await _http.GetByteArrayAsync(url.Contains("?") ? url : (url + "?token=" + apiKey)));
			return destPath;
		}
		catch
		{
			return null;
		}
	}
}
public static class DefaultDeviceGuardService
{
    private record SavedDefaults(string? RenderName, string? CaptureName);
    /// <summary>Capture les noms (pas les IDs, qui deviennent invalides) des
    /// périphériques de lecture et capture actuellement par défaut.</summary>
    public static object CaptureCurrentDefaults()
    {
        string? renderName = null, captureName = null;
        try
        {
            var enumerator = new MMDeviceEnumerator();
            try { renderName = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)?.FriendlyName; } catch { }
            try { captureName = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)?.FriendlyName; } catch { }
        }
        catch { }
        return new SavedDefaults(renderName, captureName);
    }
    /// <summary>Lit le registre HKLM\...\MMDevices\Audio\{direction} et trouve l'ID
    /// du périphérique dont le FriendlyName correspond. Retourne null si introuvable.
    /// Cette approche est plus robuste que MMDeviceEnumerator après redémarrage
    /// du service audio car elle lit directement les clés registre.</summary>
    private static string? FindDeviceIdByName(string name, DataFlow flow)
    {
        if (string.IsNullOrEmpty(name)) return null;
        string regKey = flow == DataFlow.Render
            ? @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render"
            : @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\" +
                (flow == DataFlow.Render ? "Render" : "Capture"));
            if (root == null) return null;
            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var devKey = root.OpenSubKey(subKeyName);
                if (devKey == null) continue;
                // Parcourir les valeurs nommées "xx" (xx = nombre hexa à 2 chiffres)
                // pour trouver le FriendlyName
                foreach (var valName in devKey.GetValueNames())
                {
                    if (valName.Length != 2) continue;
                    if (!int.TryParse(valName, System.Globalization.NumberStyles.HexNumber, null, out _)) continue;
                    var val = devKey.GetValue(valName) as string;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (val.Length < 2 || val.Length > 80) continue;
                    if (val.Contains('\\') || val.Contains('%') || val.Contains('#') || val.Contains(':')) continue;
                    if (val.Contains(".inf", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains(".NT", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("ROOT", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("HDAUDIO", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("INTELAUDIO", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("oem", StringComparison.OrdinalIgnoreCase) ||
                        val.Contains("0.0.") || val.Contains("0.4."))
                        continue;
                    if (val.Equals(name, StringComparison.OrdinalIgnoreCase))
                        return $"{{{subKeyName.ToUpperInvariant()}}}";
                }
            }
        }
        catch { }
        return null;
    }
    /// <summary>Attend que le service Windows Audio soit à l'état 'running'
    /// (max 5s), puis laisse 1.5s de stabilisation.</summary>
    private static void WaitForAudioService()
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("sc", "query audiosrv")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                });
                if (p != null)
                {
                    p.WaitForExit(1000);
                    if (p.StandardOutput.ReadToEnd().Contains("RUNNING"))
                    {
                        Thread.Sleep(800);
                        return;
                    }
                }
            }
            catch { }
            Thread.Sleep(100);
        }
    }
    /// <summary>Re-résout les périphériques sauvegardés par nom depuis le registre
    /// et les réimpose comme défaut pour tous les rôles (Console, Multimedia,
    /// Communications). Attend que audiosrv soit running puis restaure
    /// agressivement en boucle pendant 10s (car Windows Audio réinitialise les
    /// defaults plusieurs fois après son démarrage).</summary>
    public static void RestoreDefaults(object saved)
    {
        if (saved is not SavedDefaults s) return;
        WaitForAudioService();
        double t0 = Environment.TickCount;
        while (Environment.TickCount - t0 < 5000.0)
        {
            try
            {
                string? renderId = null, captureId = null;
                if (!string.IsNullOrEmpty(s.RenderName))
                    renderId = FindDeviceIdByName(s.RenderName, DataFlow.Render);
                if (!string.IsNullOrEmpty(s.CaptureName))
                    captureId = FindDeviceIdByName(s.CaptureName, DataFlow.Capture);
                if (renderId == null || captureId == null)
                {
                    var enumerator = new MMDeviceEnumerator();
                    if (renderId == null && !string.IsNullOrEmpty(s.RenderName))
                    {
                        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                            if (d.FriendlyName.Equals(s.RenderName, StringComparison.OrdinalIgnoreCase))
                            { renderId = d.ID; break; }
                    }
                    if (captureId == null && !string.IsNullOrEmpty(s.CaptureName))
                    {
                        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                            if (d.FriendlyName.Equals(s.CaptureName, StringComparison.OrdinalIgnoreCase))
                            { captureId = d.ID; break; }
                    }
                }
                var ids = new List<string>();
                if (renderId  != null) ids.Add(renderId);
                if (captureId != null) ids.Add(captureId);
                if (ids.Count > 0)
                    SetDefaultEndpoints(ids);
            }
            catch { }
            Thread.Sleep(250);
        }
        try
        {
            string? renderId = null, captureId = null;
            if (!string.IsNullOrEmpty(s.RenderName))
                renderId = FindDeviceIdByName(s.RenderName, DataFlow.Render);
            if (!string.IsNullOrEmpty(s.CaptureName))
                captureId = FindDeviceIdByName(s.CaptureName, DataFlow.Capture);
            if (renderId == null || captureId == null)
            {
                var enumerator = new MMDeviceEnumerator();
                if (renderId == null && !string.IsNullOrEmpty(s.RenderName))
                {
                    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        if (d.FriendlyName.Equals(s.RenderName, StringComparison.OrdinalIgnoreCase))
                        { renderId = d.ID; break; }
                }
                if (captureId == null && !string.IsNullOrEmpty(s.CaptureName))
                {
                    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        if (d.FriendlyName.Equals(s.CaptureName, StringComparison.OrdinalIgnoreCase))
                        { captureId = d.ID; break; }
                }
            }
            var finalIds = new List<string>();
            if (renderId  != null) finalIds.Add(renderId);
            if (captureId != null) finalIds.Add(captureId);
            if (finalIds.Count > 0)
                SetDefaultEndpoints(finalIds);
        }
        catch { }
    }
    /// <summary>Restauration rapide : utilise WASAPI (déjà running) pour
    /// retrouver les IDs, puis impose les 3 rôles. Boucle 3s à 250ms pour
    /// contrer les réinitialisations de Windows Audio après install driver.</summary>
    public static void QuickRestore(object saved)
    {
        if (saved is not SavedDefaults s) return;
        double t0 = Environment.TickCount;
        while (Environment.TickCount - t0 < 3000.0)
        {
            try
            {
                string? renderId = null, captureId = null;
                // WASAPI en premier (service tourne déjà)
                var enumerator = new MMDeviceEnumerator();
                if (!string.IsNullOrEmpty(s.RenderName))
                {
                    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                        if (d.FriendlyName.Equals(s.RenderName, StringComparison.OrdinalIgnoreCase))
                        { renderId = d.ID; break; }
                }
                if (!string.IsNullOrEmpty(s.CaptureName))
                {
                    foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                        if (d.FriendlyName.Equals(s.CaptureName, StringComparison.OrdinalIgnoreCase))
                        { captureId = d.ID; break; }
                }
                // Fallback registre si WASAPI n'a rien trouvé
                if (renderId == null && !string.IsNullOrEmpty(s.RenderName))
                    renderId = FindDeviceIdByName(s.RenderName, DataFlow.Render);
                if (captureId == null && !string.IsNullOrEmpty(s.CaptureName))
                    captureId = FindDeviceIdByName(s.CaptureName, DataFlow.Capture);
                var ids = new List<string>();
                if (renderId != null) ids.Add(renderId);
                if (captureId != null) ids.Add(captureId);
                if (ids.Count > 0)
                    SetDefaultEndpoints(ids);
            }
            catch { }
            Thread.Sleep(250);
        }
    }
    // ── IPolicyConfig via ctypes raw (CLSCTX_INPROC_SERVER) ────────────────────
    // CLSCTX_ALL (23) fait échouer silencieusement IPolicyConfig car il tente
    // une instanciation hors-process. Il faut CLSCTX_INPROC_SERVER (1).
    private static void SetDefaultEndpoints(List<string> deviceIds)
    {
        var clsid = new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"); // CPolicyConfigClient
        var iid   = new Guid("F8679F50-850A-41CF-9C72-430F290290C8"); // IPolicyConfig
        const int CLSCTX_INPROC_SERVER = 1;
        const int VT_SETDEFAULTENDPOINT = 13;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr pPolicyConfig);
        if (hr != 0 || pPolicyConfig == IntPtr.Zero) return;
        try
        {
            IntPtr vtable = Marshal.ReadIntPtr(pPolicyConfig);
            IntPtr setDefaultEndpointPtr = Marshal.ReadIntPtr(vtable, VT_SETDEFAULTENDPOINT * IntPtr.Size);
            var setDefaultEndpoint = Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointDelegate>(setDefaultEndpointPtr);
            foreach (var deviceId in deviceIds)
            {
                // Réimpose le périphérique pour les 3 rôles (Console, Multimedia, Communications)
                for (int role = 0; role <= 2; role++)
                    setDefaultEndpoint(pPolicyConfig, deviceId, role);
            }
        }
        catch { }
        finally { Marshal.Release(pPolicyConfig); }
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetDefaultEndpointDelegate(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid, IntPtr pUnkOuter, int dwClsContext,
        ref Guid riid, out IntPtr ppv);
}
public class VbCableService
{
	private const string VbCableUrl = "https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip";
	private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "Soundeck_VBCable");
	private static readonly string[] VbRegKeys = new string[3] { "SOFTWARE\\VB-Audio\\Cable", "SOFTWARE\\VB-Audio\\VACMMSetup", "SOFTWARE\\VB-Audio\\Virtual Cable" };
	public static bool IsInstalled()
	{
		return AudioEngine.IsVbCableInstalled();
	}
	private static void BlockBrowser()
	{
		try
		{
			string value = "C:\\Windows\\System32\\cmd.exe /c exit";
			string[] array = new string[2] { "http", "https" };
			foreach (string text in array)
			{
				string name = "Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\" + text + "\\UserChoice";
				using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(name, writable: true);
				if (registryKey == null)
				{
					continue;
				}
				string text2 = registryKey.GetValue("ProgId") as string;
				if (string.IsNullOrEmpty(text2))
				{
					continue;
				}
				using RegistryKey registryKey2 = Registry.ClassesRoot.OpenSubKey(text2 + "\\shell\\open\\command", writable: true);
				if (registryKey2 != null)
				{
					string text3 = registryKey2.GetValue("") as string;
					if (!string.IsNullOrEmpty(text3) && !text3.Contains("cmd.exe"))
					{
						registryKey2.SetValue("_saved", text3);
						registryKey2.SetValue("", value);
					}
				}
			}
		}
		catch
		{
		}
	}
	private static void UnblockBrowser()
	{
		try
		{
			string[] array = new string[2] { "http", "https" };
			foreach (string text in array)
			{
				string name = "Software\\Microsoft\\Windows\\Shell\\Associations\\UrlAssociations\\" + text + "\\UserChoice";
				using RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(name, writable: true);
				if (registryKey == null)
				{
					continue;
				}
				string text2 = registryKey.GetValue("ProgId") as string;
				if (string.IsNullOrEmpty(text2))
				{
					continue;
				}
				using RegistryKey registryKey2 = Registry.ClassesRoot.OpenSubKey(text2 + "\\shell\\open\\command", writable: true);
				if (registryKey2 != null)
				{
					string value = registryKey2.GetValue("_saved") as string;
					if (!string.IsNullOrEmpty(value))
					{
						registryKey2.SetValue("", value);
						registryKey2.DeleteValue("_saved", throwOnMissingValue: false);
					}
				}
			}
		}
		catch
		{
		}
	}
	private static void KillAllResidual()
	{
		string[] array = new string[9] { "VBCABLE_Setup_x64", "VBCABLE_Setup_x86", "VBCABLE_Setup", "DPInst", "dpinst", "DPInst64", "dpinst64", "InfDefaultInstall", "rundll32" };
		string[] array2 = array;
		foreach (string text in array2)
		{
			try
			{
				Process[] processesByName = Process.GetProcessesByName(text);
				foreach (Process process in processesByName)
				{
					if (text == "rundll32")
					{
						try
						{
							if (process.MainModule?.FileName?.Contains("syssetup", StringComparison.OrdinalIgnoreCase) != true)
							{
								continue;
							}
						}
						catch
						{
							continue;
						}
					}
					try
					{
						process.Kill(entireProcessTree: true);
					}
					catch
					{
					}
				}
			}
			catch
			{
			}
		}
		string[] array3 = array;
		foreach (string text2 in array3)
		{
			if (!(text2 == "rundll32"))
			{
				try
				{
					Process.Start(new ProcessStartInfo("taskkill", "/F /IM " + text2 + ".exe /T")
					{
						UseShellExecute = false,
						CreateNoWindow = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true
					})?.WaitForExit(2000);
				}
				catch
				{
				}
			}
		}
	}
	public static async Task<(bool Success, string? Error)> InstallAsync(IProgress<string>? progress = null)
	{
		object savedDefaults = DefaultDeviceGuardService.CaptureCurrentDefaults();
		try
		{
			Directory.CreateDirectory(TempDir);
			progress?.Report("Downloading VB-Cable driver...");
			string zipPath = Path.Combine(TempDir, "vbcable.zip");
			using HttpClient http = new HttpClient
			{
				Timeout = TimeSpan.FromSeconds(120.0)
			};
			string path = zipPath;
			await File.WriteAllBytesAsync(path, await http.GetByteArrayAsync("https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack43.zip"));
			progress?.Report("Extracting...");
			string extractDir = Path.Combine(TempDir, "extracted");
			if (Directory.Exists(extractDir))
			{
				Directory.Delete(extractDir, recursive: true);
			}
			ZipFile.ExtractToDirectory(zipPath, extractDir);
			string[] setupFiles = Directory.GetFiles(extractDir, "VBCABLE_Setup_x64.exe", SearchOption.AllDirectories);
			if (setupFiles.Length == 0)
			{
				setupFiles = Directory.GetFiles(extractDir, "VBCABLE_Setup*.exe", SearchOption.AllDirectories);
			}
			if (setupFiles.Length == 0)
			{
				return (Success: false, Error: "VB-Cable setup executable not found in the downloaded package.");
			}
			BlockBrowser();
			progress?.Report("Installing VB-Cable (requires admin)...");
			string setupDir = Path.GetDirectoryName(setupFiles[0]);
			bool wasInstalled = AudioEngine.IsVbCableInstalled();
			bool wentMissing = false;
			bool detected = false;
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = setupFiles[0],
				WorkingDirectory = setupDir,
				UseShellExecute = true,
				Verb = "runas"
			});
			if (process == null)
			{
				UnblockBrowser();
				return (Success: false, Error: "Impossible de lancer l'installateur.");
			}
			for (int attempt = 0; attempt < 600; attempt++)
			{
				await Task.Delay(100);
				bool isInstalledNow = AudioEngine.IsVbCableInstalled();
				if (wasInstalled && !isInstalledNow)
				{
					wentMissing = true;
				}
				if (isInstalledNow && (!wasInstalled || wentMissing))
				{
					await Task.Delay(500);
					detected = true;
					break;
				}
				if (process.HasExited)
				{
					detected = isInstalledNow;
					break;
				}
			}
			for (int i = 0; i < 3; i++)
			{
				KillAllResidual();
				await Task.Delay(300);
			}
			if (!detected)
			{
				UnblockBrowser();
				return (Success: false, Error: "VB-Cable n'a pas été détecté après l'installation.");
			}
			progress?.Report("Restoring default audio devices...");
			await Task.Run(delegate
			{
				DefaultDeviceGuardService.QuickRestore(savedDefaults);
			});
			return (Success: true, Error: null);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			return (Success: false, Error: "Installation failed: " + ex2.Message);
		}
		finally
		{
			UnblockBrowser();
			try
			{
				if (Directory.Exists(TempDir))
				{
					Directory.Delete(TempDir, recursive: true);
				}
			}
			catch
			{
			}
		}
	}
}
public class YouTubeService
{
	private static string? _ytdlpPath;
	private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck");
	public static string DownloadsDir => Path.Combine(AppDataDir, "downloads");
	public static async Task<string?> EnsureYtDlpAsync(IProgress<string>? progress = null)
	{
		if (_ytdlpPath != null && File.Exists(_ytdlpPath))
		{
			return _ytdlpPath;
		}
		try
		{
			Process p = Process.Start(new ProcessStartInfo("yt-dlp", "--version")
			{
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true
			});
			if (p != null)
			{
				await p.WaitForExitAsync();
				_ytdlpPath = "yt-dlp";
				return _ytdlpPath;
			}
		}
		catch
		{
		}
		string exePath = Path.Combine(AppDataDir, "yt-dlp.exe");
		if (File.Exists(exePath))
		{
			_ytdlpPath = exePath;
			return _ytdlpPath;
		}
		try
		{
			progress?.Report("Downloading yt-dlp...");
			using HttpClient http = new HttpClient();
			string path = exePath;
			string path2 = path;
			await File.WriteAllBytesAsync(path2, await http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe"));
			_ytdlpPath = exePath;
			return _ytdlpPath;
		}
		catch
		{
			return null;
		}
	}
	public static async Task<(bool Success, string? FilePath, string? Error)> DownloadAsync(string url, string format = "mp3", IProgress<string>? progress = null)
	{
		Directory.CreateDirectory(DownloadsDir);
		string ytdlp = await EnsureYtDlpAsync(progress);
		if (ytdlp == null)
		{
			return (Success: false, FilePath: null, Error: "yt-dlp introuvable. Installez-le depuis https://github.com/yt-dlp/yt-dlp");
		}
		string ffmpegDir = FindFfmpegDir();
		string outTemplate = Path.Combine(DownloadsDir, "%(title)s.%(ext)s");
		string args = ((ffmpegDir != null) ? $"\"{url}\" --extractor-args \"youtube:player_client=android\" --no-playlist --extract-audio --audio-format {format} --audio-quality 192K -o \"{outTemplate}\" --ffmpeg-location \"{ffmpegDir}\" --no-warnings --progress" : $"\"{url}\" --extractor-args \"youtube:player_client=android\" --no-playlist -f bestaudio -o \"{outTemplate}\" --no-warnings --progress");
		if (ffmpegDir == null)
		{
			progress?.Report("ffmpeg non trouvé — téléchargement en format natif (opus/webm)…");
		}
		try
		{
			Process proc = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = ytdlp,
					Arguments = args,
					UseShellExecute = false,
					CreateNoWindow = true,
					RedirectStandardOutput = true,
					RedirectStandardError = true
				},
				EnableRaisingEvents = true
			};
			string outputFile = null;
			List<string> stderrParts = new List<string>();
			object stderrLock = new object();
			proc.OutputDataReceived += delegate(object _, DataReceivedEventArgs e)
			{
				if (e.Data != null)
				{
					progress?.Report(e.Data);
					Match match = Regex.Match(e.Data, "\\[(?:download|ExtractAudio|Merger|ffmpeg)\\]\\s+(?:Destination|Merging formats into):\\s*(.+)");
					if (match.Success)
					{
						outputFile = match.Groups[1].Value.Trim().Trim('"');
					}
					else
					{
						match = Regex.Match(e.Data, "\\[download\\]\\s+(.+\\.(?:mp3|m4a|webm|ogg|opus|wav|flac))\\s+has already been downloaded", RegexOptions.IgnoreCase);
						if (match.Success)
						{
							outputFile = match.Groups[1].Value.Trim();
						}
						else
						{
							match = Regex.Match(e.Data, "(?:^|\\s)([A-Za-z]:[\\\\/].+\\.(?:mp3|m4a|webm|ogg|opus|wav|flac))$", RegexOptions.IgnoreCase);
							if (match.Success && File.Exists(match.Groups[1].Value.Trim()))
							{
								outputFile = match.Groups[1].Value.Trim();
							}
						}
					}
				}
			};
			proc.ErrorDataReceived += delegate(object _, DataReceivedEventArgs e)
			{
				if (e.Data != null)
				{
					lock (stderrLock)
					{
						stderrParts.Add(e.Data);
					}
				}
			};
			proc.Start();
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();
			await proc.WaitForExitAsync();
			if (stderrParts.Count > 0)
			{
				try
				{
					File.WriteAllText(Path.Combine(AppDataDir, "ytdlp_error.log"), $"[{DateTime.Now}] ExitCode={proc.ExitCode}\nArgs: {args}\n\n" + string.Join("\n", stderrParts));
				}
				catch
				{
				}
			}
			if (proc.ExitCode != 0)
			{
				string errMsg;
				lock (stderrLock)
				{
					errMsg = stderrParts.LastOrDefault((string l) => l.Trim().Length > 0) ?? "erreur inconnue";
				}
				return (Success: false, FilePath: null, Error: "yt-dlp : " + errMsg);
			}
			if (outputFile == null || !File.Exists(outputFile))
			{
				outputFile = Directory.GetFiles(DownloadsDir).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
			}
			return ((bool Success, string? FilePath, string? Error))((outputFile != null && File.Exists(outputFile)) ? (Success: true, FilePath: outputFile, Error: null) : (Success: false, FilePath: null, Error: "Fichier téléchargé introuvable."));
		}
		catch (Exception ex)
		{
			return (Success: false, FilePath: null, Error: ex.Message);
		}
	}
	private static string? FindFfmpegDir()
	{
		if (_ytdlpPath != null)
		{
			string directoryName = Path.GetDirectoryName(_ytdlpPath);
			if (directoryName != null && File.Exists(Path.Combine(directoryName, "ffmpeg.exe")))
			{
				return directoryName;
			}
		}
		string directoryName2 = Path.GetDirectoryName(Environment.ProcessPath ?? "");
		if (directoryName2 != null && File.Exists(Path.Combine(directoryName2, "ffmpeg.exe")))
		{
			return directoryName2;
		}
		string[] array = new string[4]
		{
			"C:\\ffmpeg\\bin",
			"C:\\ffmpeg",
			"C:\\Program Files\\ffmpeg\\bin",
			Path.Combine(AppDataDir, "ffmpeg")
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			if (File.Exists(Path.Combine(text, "ffmpeg.exe")))
			{
				return text;
			}
		}
		string[] array3 = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
		string[] array4 = array3;
		foreach (string text2 in array4)
		{
			if (File.Exists(Path.Combine(text2, "ffmpeg.exe")))
			{
				return text2;
			}
		}
		return null;
	}
	public static async Task<List<(string Title, string Id, string Duration, string Thumbnail)>> SearchAsync(string query, int maxResults = 5)
	{
		try
		{
			YoutubeClient youtube = new YoutubeClient();
			List<(string, string, string, string)> results = new List<(string, string, string, string)>();
			await foreach (VideoSearchResult video in youtube.Search.GetVideosAsync(query))
			{
				results.Add(new ValueTuple<string, string, string, string>(item3: video.Duration.HasValue ? FormatDuration(video.Duration.Value.TotalSeconds) : "", item4: video.Thumbnails.OrderByDescending((Thumbnail t) => t.Resolution.Area).FirstOrDefault()?.Url ?? "", item1: video.Title, item2: video.Id.Value));
				if (results.Count >= maxResults)
				{
					break;
				}
			}
			return results;
		}
		catch
		{
			return new List<(string, string, string, string)>();
		}
	}
	private static string FormatDuration(double seconds)
	{
		TimeSpan timeSpan = TimeSpan.FromSeconds(seconds);
		return (timeSpan.Hours > 0) ? timeSpan.ToString("h\\:mm\\:ss") : timeSpan.ToString("m\\:ss");
	}
}
public static class StarterPackService
{
	public const string CollectionName = "Starter Pack";
	private static readonly (string Name, string Url)[] StarterSounds = new(string, string)[25]
	{
		("Air Horn", "https://www.myinstants.com/media/sounds/air-horn-club-sample.mp3"),
		("Sad Trombone", "https://www.myinstants.com/media/sounds/sadtrombone.swf.mp3"),
		("Ba Dum Tss", "https://www.myinstants.com/media/sounds/ba-dum-tss.mp3"),
		("Bruh", "https://www.myinstants.com/media/sounds/bruh.mp3"),
		("Metal Pipe", "https://www.myinstants.com/media/sounds/metal-pipe-falling.mp3"),
		("Crickets", "https://www.myinstants.com/media/sounds/crickets.mp3"),
		("Applause", "https://www.myinstants.com/media/sounds/applause.mp3"),
		("MLG Airhorn", "https://www.myinstants.com/media/sounds/mlg-airhorn.mp3"),
		("Oof", "https://www.myinstants.com/media/sounds/roblox-death-sound_1.mp3"),
		("Nani", "https://www.myinstants.com/media/sounds/nani-meme-sound-effect.mp3"),
		("Evil Laugh", "https://www.myinstants.com/media/sounds/evil-laugh.mp3"),
		("Oh No", "https://www.myinstants.com/media/sounds/oh-no.mp3"),
		("Emotional Damage", "https://www.myinstants.com/media/sounds/emotional-damage.mp3"),
		("Windows XP Error", "https://www.myinstants.com/media/sounds/windows-xp-error.mp3"),
		("Fart", "https://www.myinstants.com/media/sounds/fart-with-reverb.mp3"),
		("Mario Coin", "https://www.myinstants.com/media/sounds/mario-coin-sound-effect.mp3"),
		("Nyan Cat", "https://www.myinstants.com/media/sounds/nyan-cat_1.mp3"),
		("To Be Continued", "https://www.myinstants.com/media/sounds/to-be-continued.mp3"),
		("John Cena", "https://www.myinstants.com/media/sounds/and-his-name-is-john-cena-1.mp3"),
		("Dramatic Chipmunk", "https://www.myinstants.com/media/sounds/dramatic-chipmunk.mp3"),
		("Inception Horn", "https://www.myinstants.com/media/sounds/inceptionbutton.mp3"),
		("Troll Song", "https://www.myinstants.com/media/sounds/troll.mp3"),
		("Coffin Dance", "https://www.myinstants.com/media/sounds/coffin-dance.mp3"),
		("Rizz", "https://www.myinstants.com/media/sounds/rizz-sound-effect.mp3"),
		("Skibidi", "https://www.myinstants.com/media/sounds/skibidi-toilet.mp3")
	};
	public static async Task<(bool Ok, string? Error)> DownloadAsync(IProgress<(int Current, int Total, string Name)>? progress = null, CancellationToken ct = default(CancellationToken))
	{
		string dlDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "downloads", "starter");
		Directory.CreateDirectory(dlDir);
		List<(string Name, string Path)> added = new List<(string, string)>();
		int total = StarterSounds.Length;
		using HttpClient http = new HttpClient();
		http.DefaultRequestHeaders.UserAgent.ParseAdd("Soundeck/2.0");
		http.Timeout = TimeSpan.FromSeconds(15.0);
		for (int i = 0; i < total; i++)
		{
			ct.ThrowIfCancellationRequested();
			(string, string) tuple = StarterSounds[i];
			string name = tuple.Item1;
			string url = tuple.Item2;
			string safe = new string(name.Where((char c) => char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-').ToArray());
			if (safe.Length > 40)
			{
				safe = safe.Substring(0, 40);
			}
			string ext = url.Split('.').Last().Split('?')
				.First();
			if (string.IsNullOrEmpty(ext))
			{
				ext = "mp3";
			}
			string dest = Path.Combine(dlDir, "starter_" + safe + "." + ext);
			if (File.Exists(dest) && new FileInfo(dest).Length > 1024)
			{
				added.Add((name, dest));
				progress?.Report((i + 1, total, name));
				continue;
			}
			try
			{
				string archiveUrl = "https://web.archive.org/web/2id_/" + url;
				await File.WriteAllBytesAsync(dest, await http.GetByteArrayAsync(archiveUrl, ct), ct);
				added.Add((name, dest));
			}
			catch
			{
			}
			progress?.Report((i + 1, total, name));
		}
		return (Ok: added.Count > 0, Error: (added.Count == 0) ? "Aucun son n'a pu être téléchargé." : null);
	}
	public static List<Sound> GetDownloadedSounds()
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "downloads", "starter");
		if (!Directory.Exists(path))
		{
			return new List<Sound>();
		}
		List<Sound> list = new List<Sound>();
		string[] files = Directory.GetFiles(path);
		foreach (string text in files)
		{
			string text2 = Path.GetFileNameWithoutExtension(text);
			if (text2.StartsWith("starter_"))
			{
				text2 = text2.Substring("starter_".Length);
			}
			list.Add(new Sound
			{
				Name = text2,
				Filepath = text,
				Collection = "Starter Pack"
			});
		}
		return list;
	}
	public static bool IsInstalled(Soundeck.ViewModels.MainViewModel vm)
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "downloads", "starter");
		return Directory.Exists(path) && Directory.GetFiles(path).Length >= 10 && vm.Collections.Contains(CollectionName);
	}
	public static void Remove()
	{
		string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "downloads", "starter");
		if (Directory.Exists(path))
		{
			try
			{
				Directory.Delete(path, recursive: true);
			}
			catch
			{
			}
		}
	}
}
public static class CoverArtService
{
	private static readonly HttpClient _http;
	private static readonly string _cacheDir;
	static CoverArtService()
	{
		_http = new HttpClient
		{
			Timeout = TimeSpan.FromSeconds(8.0)
		};
		_cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck", "covers");
		Directory.CreateDirectory(_cacheDir);
	}
	public static async Task<string?> GetCoverAsync(string soundName, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(soundName))
		{
			return null;
		}
		string safeKey = new string(soundName.Where((char c) => char.IsLetterOrDigit(c) || c == '_').ToArray()).ToLowerInvariant();
		if (safeKey.Length > 60)
		{
			safeKey = safeKey.Substring(0, 60);
		}
		string cachedPath = Path.Combine(_cacheDir, safeKey + ".jpg");
		if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 500)
		{
			return cachedPath;
		}
		try
		{
			string query = Uri.EscapeDataString(soundName);
			string url = "https://itunes.apple.com/search?term=" + query + "&media=music&limit=1";
			string artworkUrl = ExtractArtworkUrl(await _http.GetStringAsync(url, ct));
			if (artworkUrl == null)
			{
				return null;
			}
			artworkUrl = artworkUrl.Replace("100x100bb", "600x600bb");
			byte[] bytes = await _http.GetByteArrayAsync(artworkUrl, ct);
			if (bytes.Length < 500)
			{
				return null;
			}
			await File.WriteAllBytesAsync(cachedPath, bytes, ct);
			return cachedPath;
		}
		catch
		{
			return null;
		}
	}
	private static string? ExtractArtworkUrl(string json)
	{
		try
		{
			using JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement property = jsonDocument.RootElement.GetProperty("results");
			if (property.GetArrayLength() == 0)
			{
				return null;
			}
			if (property[0].TryGetProperty("artworkUrl100", out var value))
			{
				return value.GetString();
			}
			return null;
		}
		catch
		{
			return null;
		}
	}
}
