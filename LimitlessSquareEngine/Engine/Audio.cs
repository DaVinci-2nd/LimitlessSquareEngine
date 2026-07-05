using Silk.NET.OpenAL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LimitlessSquareEngine.Engine
{
    internal static class AlcNative
    {
        public const string LibName = "openal32";

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint alcOpenDevice(string? devicename);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern nint alcCreateContext(nint device, nint attrlist);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool alcMakeContextCurrent(nint context);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void alcDestroyContext(nint context);

        [DllImport(LibName, CallingConvention = CallingConvention.Cdecl)]
        public static extern bool alcCloseDevice(nint device);
    }

    internal partial class Audio
    {
        private AL _al = null!;
        private nint _alDevice;
        private nint _alContext;

        private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, AudioSourceRuntime> _sources = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _audioFileRegistry = new(StringComparer.OrdinalIgnoreCase);

        private string? _activeListenerSourceId;

        private float _masterVolume = 1f;
        private float _dopplerFactor = 1f;
        private float _speedOfSound = 343f;

        private Double3 _listenerWorldPosition = Double3.Zero;
        private DQuaternion _listenerWorldRotation = DQuaternion.Identity;

        private const int AL_ORIENTATION = 0x100F;

        [DllImport(AlcNative.LibName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void alListenerfv(int param, float[] values);

        private bool _initialized;

        public void Initialize()
        {
            if (_initialized)
                return;

            _al = AL.GetApi(true);

            _alDevice = AlcNative.alcOpenDevice(null);
            if (_alDevice == IntPtr.Zero)
            {
                Console.WriteLine("[!] Audio: Failed to open OpenAL device. Audio disabled.");
                return;
            }

            _alContext = AlcNative.alcCreateContext(_alDevice, IntPtr.Zero);
            if (_alContext == IntPtr.Zero)
            {
                Console.WriteLine("[!] Audio: Failed to create OpenAL context. Audio disabled.");
                AlcNative.alcCloseDevice(_alDevice);
                _alDevice = IntPtr.Zero;
                return;
            }

            if (!AlcNative.alcMakeContextCurrent(_alContext))
            {
                Console.WriteLine("[!] Audio: Failed to activate OpenAL context. Audio disabled.");
                AlcNative.alcDestroyContext(_alContext);
                AlcNative.alcCloseDevice(_alDevice);
                _alContext = IntPtr.Zero;
                _alDevice = IntPtr.Zero;
                return;
            }

            Console.WriteLine("[i] Audio initialized.");

            _al.DistanceModel(DistanceModel.InverseDistanceClamped);
            _al.DopplerFactor(_dopplerFactor);
            _al.SpeedOfSound(_speedOfSound);

            _al.SetListenerProperty(ListenerVector3.Position, 0f, 0f, 0f);
            _al.SetListenerProperty(ListenerVector3.Velocity, 0f, 0f, 0f);

            _initialized = true;
        }

        public void Shutdown()
        {
            if (!_initialized)
                return;

            foreach (var kv in _clips)
            {
                if (kv.Value.AlBufferId != 0)
                {
                    _al.DeleteBuffer(kv.Value.AlBufferId);
                }
            }
            _clips.Clear();

            foreach (var kv in _sources)
            {
                if (kv.Value.AlSourceId != 0)
                {
                    _al.SourceStop(kv.Value.AlSourceId);
                    _al.DeleteSource(kv.Value.AlSourceId);
                }
            }
            _sources.Clear();

            if (_alContext != IntPtr.Zero)
            {
                AlcNative.alcMakeContextCurrent(IntPtr.Zero);
                AlcNative.alcDestroyContext(_alContext);
                _alContext = IntPtr.Zero;
            }

            if (_alDevice != IntPtr.Zero)
            {
                AlcNative.alcCloseDevice(_alDevice);
                _alDevice = IntPtr.Zero;
            }

            _initialized = false;
            Console.WriteLine("[i] Audio shutdown.");
        }

        public void RegisterAudioFile(string assetKey, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(assetKey) || string.IsNullOrWhiteSpace(absolutePath))
                return;
            _audioFileRegistry[assetKey] = absolutePath;
        }

        public void ClearFileRegistry()
        {
            _audioFileRegistry.Clear();
        }

        public AudioClip? LoadClip(string clipId)
        {
            if (!_initialized)
                return null;

            if (_clips.TryGetValue(clipId, out var cached))
                return cached;

            if (!_audioFileRegistry.TryGetValue(clipId, out string? filePath) || !File.Exists(filePath))
            {
                Console.WriteLine($"[!] Audio clip '{clipId}' not found in registry.");
                return null;
            }

            try
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                WavDecoder.WavData wavData;
                if (ext == ".wav")
                {
                    wavData = WavDecoder.Decode(fileBytes);
                    if (wavData.BitsPerSample == 32)
                    {
                        wavData = ConvertWav32To16(wavData);
                    }
                }
                else if (ext == ".ogg")
                {
                    wavData = DecodeOgg(fileBytes);
                }
                else if (ext == ".mp3")
                {
                    wavData = DecodeMp3(fileBytes);
                }
                else
                {
                    Console.WriteLine($"[!] Unsupported audio format: {ext}");
                    return null;
                }

                int alFormat = ResolveAlFormat(wavData.Channels, wavData.BitsPerSample);
                if (alFormat < 0)
                {
                    Console.WriteLine($"[!] Unsupported audio format: {wavData.Channels}ch {wavData.BitsPerSample}bit");
                    return null;
                }

                uint bufferId = _al.GenBuffer();

                _al.BufferData(bufferId, (BufferFormat)alFormat, wavData.PcmBytes, wavData.SampleRate);

                AudioError error = _al.GetError();
                if (error != AudioError.NoError)
                {
                    Console.WriteLine($"[!] OpenAL error {error} while loading clip '{clipId}'.");
                    _al.DeleteBuffer(bufferId);
                    return null;
                }

                double duration = (double)wavData.TotalSamples / wavData.SampleRate;

                var clip = new AudioClip
                {
                    ClipId = clipId,
                    AlBufferId = bufferId,
                    SampleRate = wavData.SampleRate,
                    Channels = wavData.Channels,
                    BitsPerSample = wavData.BitsPerSample,
                    TotalSamples = wavData.TotalSamples,
                    DurationSeconds = duration,
                    AlFormat = alFormat,
                    FilePath = filePath
                };

                _clips[clipId] = clip;
                Console.WriteLine($"[i] Audio clip loaded: '{clipId}' ({wavData.SampleRate}Hz, {wavData.Channels}ch, {wavData.BitsPerSample}bit, {duration:F2}s)");
                return clip;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to load audio clip '{clipId}': {ex.Message}");
                return null;
            }
        }

        public void UnloadClip(string clipId)
        {
            if (!_clips.TryGetValue(clipId, out var clip))
                return;

            if (clip.AlBufferId != 0)
            {
                _al.DeleteBuffer(clip.AlBufferId);
            }

            foreach (var kv in _sources)
            {
                if (kv.Value.CurrentClipId == clipId)
                {
                    _al.SourceStop(kv.Value.AlSourceId);
                    kv.Value.CurrentClipId = null;
                    kv.Value.IsPlaying = false;
                    kv.Value.IsPaused = false;
                }
            }

            _clips.Remove(clipId);
        }

        public string PlayNonSpatial(string clipId, float volume = 1f, float pitch = 1f, float pan = 0f)
        {
            var clip = LoadClip(clipId);
            if (clip == null)
                return string.Empty;

            string sourceId = Guid.NewGuid().ToString("N");

            uint alSource = _al.GenSource();

            _al.SetSourceProperty(alSource, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(alSource, SourceInteger.Buffer, clip.AlBufferId);
            _al.SetSourceProperty(alSource, SourceFloat.Gain, volume * _masterVolume);
            _al.SetSourceProperty(alSource, SourceFloat.Pitch, pitch);
            _al.SetSourceProperty(alSource, SourceFloat.RolloffFactor, 0f);
            _al.SetSourceProperty(alSource, SourceBoolean.Looping, false);

            if (clip.Channels >= 2)
            {
                float leftGain = pan <= 0f ? 1f : (1f - pan);
                float rightGain = pan >= 0f ? 1f : (1f + pan);
                _al.SetSourceProperty(alSource, SourceFloat.ConeOuterGain, leftGain);
            }

            _al.SourcePlay(alSource);

            var runtime = new AudioSourceRuntime
            {
                SourceId = sourceId,
                SceneId = string.Empty,
                AlSourceId = alSource,
                CurrentClipId = clipId,
                Volume = volume,
                Pitch = pitch,
                Pan = pan,
                IsPlaying = true,
                IsPaused = false,
                IsLooping = false,
                IsSpatial = false,
                PlaybackPosition = 0.0
            };

            _sources[sourceId] = runtime;
            return sourceId;
        }

        public string PlayNonSpatialLooping(string clipId, float volume = 1f, float pitch = 1f, float pan = 0f)
        {
            var clip = LoadClip(clipId);
            if (clip == null)
                return string.Empty;

            string sourceId = Guid.NewGuid().ToString("N");

            uint alSource = _al.GenSource();

            _al.SetSourceProperty(alSource, SourceBoolean.SourceRelative, true);
            _al.SetSourceProperty(alSource, SourceInteger.Buffer, clip.AlBufferId);
            _al.SetSourceProperty(alSource, SourceFloat.Gain, volume * _masterVolume);
            _al.SetSourceProperty(alSource, SourceFloat.Pitch, pitch);
            _al.SetSourceProperty(alSource, SourceFloat.RolloffFactor, 0f);
            _al.SetSourceProperty(alSource, SourceBoolean.Looping, true);

            _al.SourcePlay(alSource);

            var runtime = new AudioSourceRuntime
            {
                SourceId = sourceId,
                SceneId = string.Empty,
                AlSourceId = alSource,
                CurrentClipId = clipId,
                Volume = volume,
                Pitch = pitch,
                Pan = pan,
                IsPlaying = true,
                IsPaused = false,
                IsLooping = true,
                IsSpatial = false,
                PlaybackPosition = 0.0
            };

            _sources[sourceId] = runtime;
            return sourceId;
        }

        public void Stop(string sourceId)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            _al.SourceStop(source.AlSourceId);

            if (!source.IsSpatial)
            {
                source.Finished = true;
                _al.DeleteSource(source.AlSourceId);
                _sources.Remove(sourceId);
            }
            else
            {
                source.IsPlaying = false;
                source.IsPaused = false;
            }
        }

        public void Pause(string sourceId)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            _al.SourcePause(source.AlSourceId);
            source.IsPaused = true;
            source.IsPlaying = false;

            _al.GetSourceProperty(source.AlSourceId, SourceFloat.SecOffset, out float sec);
            source.PlaybackPosition = sec;
        }

        public void Resume(string sourceId)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            if (!source.IsPaused)
                return;

            _al.SourcePlay(source.AlSourceId);
            source.IsPlaying = true;
            source.IsPaused = false;
        }

        public void SetVolume(string sourceId, float volume)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            volume = Math.Clamp(volume, 0f, 1f);
            source.Volume = volume;
            _al.SetSourceProperty(source.AlSourceId, SourceFloat.Gain, volume * _masterVolume);
        }

        public void SetPitch(string sourceId, float pitch)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            source.Pitch = pitch;
            _al.SetSourceProperty(source.AlSourceId, SourceFloat.Pitch, pitch);
        }

        public void SetPan(string sourceId, float pan)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            source.Pan = Math.Clamp(pan, -1f, 1f);
        }

        public void SetPosition(string sourceId, float seconds)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            _al.SetSourceProperty(source.AlSourceId, SourceFloat.SecOffset, seconds);
            source.PlaybackPosition = seconds;
        }

        public float GetPosition(string sourceId)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return 0f;

            _al.GetSourceProperty(source.AlSourceId, SourceFloat.SecOffset, out float sec);
            source.PlaybackPosition = sec;
            return sec;
        }

        public bool IsPlaying(string sourceId)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                return false;

            return !source.Finished && !source.IsPaused;
        }

        public void SetMasterVolume(float volume)
        {
            _masterVolume = Math.Clamp(volume, 0f, 1f);
        }

        public float GetClipDuration(string clipId)
        {
            if (_clips.TryGetValue(clipId, out var clip))
                return (float)clip.DurationSeconds;

            var loaded = LoadClip(clipId);
            if (loaded != null)
                return (float)loaded.DurationSeconds;

            return 0f;
        }

        public void SetListener(string sceneId, string objectId, AudioListenerSettings settings,
            Double3 worldPosition, DQuaternion worldRotation, Double3 worldVelocity)
        {
            if (!_initialized)
                return;

            string sourceId = SourceKey(sceneId, objectId);

            if (!_sources.TryGetValue(sourceId, out var runtime))
            {
                uint alSource = _al.GenSource();

                runtime = new AudioSourceRuntime
                {
                    SourceId = sourceId,
                    SceneId = sceneId,
                    AlSourceId = alSource,
                    IsSpatial = false,
                    Settings = null
                };

                _sources[sourceId] = runtime;
            }

            _activeListenerSourceId = sourceId;

            _listenerWorldPosition = worldPosition;
            _listenerWorldRotation = worldRotation;

            runtime.WorldPosition = worldPosition;
            runtime.WorldVelocity = worldVelocity;

            _al.SetListenerProperty(ListenerVector3.Position, 0f, 0f, 0f);
            _al.SetListenerProperty(ListenerVector3.Velocity, 0f, 0f, 0f);

            Double3 forward = worldRotation.Rotate(new Double3(0.0, 0.0, -1.0));
            Double3 up = worldRotation.Rotate(new Double3(0.0, 1.0, 0.0));
            float[] orientation = {
                (float)forward.X, (float)forward.Y, (float)forward.Z,
                (float)up.X, (float)up.Y, (float)up.Z
            };
            alListenerfv(AL_ORIENTATION, orientation);
        }

        public void RegisterOrUpdateSceneAudioSource(
            string sceneId, string objectId,
            AudioSourceSettings settings,
            Double3 worldPosition, DQuaternion worldRotation)
        {
            if (!_initialized)
                return;

            string sourceId = SourceKey(sceneId, objectId);

            if (!_sources.TryGetValue(sourceId, out var runtime))
            {
                var clip = LoadClip(settings.ClipId);
                if (clip == null)
                    return;

                uint alSource = _al.GenSource();

                _al.SetSourceProperty(alSource, SourceBoolean.SourceRelative, false);
                _al.SetSourceProperty(alSource, SourceInteger.Buffer, clip.AlBufferId);
                _al.SetSourceProperty(alSource, SourceFloat.Gain, (float)settings.Volume * _masterVolume);
                _al.SetSourceProperty(alSource, SourceFloat.Pitch, 1f);
                _al.SetSourceProperty(alSource, SourceFloat.ReferenceDistance, (float)settings.MinDistance);
                _al.SetSourceProperty(alSource, SourceFloat.MaxDistance, (float)settings.MaxDistance);
                _al.SetSourceProperty(alSource, SourceFloat.RolloffFactor, (float)settings.RolloffFactor);
                _al.SetSourceProperty(alSource, SourceBoolean.Looping, settings.Loop);

                Double3 relativePos = worldPosition - _listenerWorldPosition;
                _al.SetSourceProperty(alSource, SourceVector3.Position,
                    (float)relativePos.X, (float)relativePos.Y, (float)relativePos.Z);

                runtime = new AudioSourceRuntime
                {
                    SourceId = sourceId,
                    SceneId = sceneId,
                    AlSourceId = alSource,
                    CurrentClipId = settings.ClipId,
                    Volume = (float)settings.Volume,
                    Pitch = 1f,
                    Pan = 0f,
                    IsPlaying = false,
                    IsPaused = false,
                    IsLooping = settings.Loop,
                    IsSpatial = true,
                    PlaybackPosition = 0.0,
                    Settings = settings,
                    WorldPosition = worldPosition,
                    LastWorldPosition = worldPosition,
                    WorldVelocity = Double3.Zero
                };

                _sources[sourceId] = runtime;

                if (settings.PlayOnAwake)
                {
                    _al.SourcePlay(alSource);
                    runtime.IsPlaying = true;
                }
            }
            else
            {
                runtime.WorldPosition = worldPosition;
                Double3 relativePos = worldPosition - _listenerWorldPosition;
                _al.SetSourceProperty(runtime.AlSourceId, SourceVector3.Position,
                    (float)relativePos.X, (float)relativePos.Y, (float)relativePos.Z);
            }
        }

        public void PlaySceneSource(string sceneId, string objectId)
        {
            string sourceId = SourceKey(sceneId, objectId);
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            _al.SourcePlay(source.AlSourceId);
            source.IsPlaying = true;
            source.IsPaused = false;
            source.Finished = false;
        }

        public void StopSceneSource(string sceneId, string objectId)
        {
            string sourceId = SourceKey(sceneId, objectId);
            if (!_sources.TryGetValue(sourceId, out var source))
                return;

            _al.SourceStop(source.AlSourceId);
            source.IsPlaying = false;
            source.IsPaused = false;
            source.Finished = true;
        }

        public void Update(double deltaTime)
        {
            if (!_initialized)
                return;

            var toRemove = new List<string>();

            foreach (var kv in _sources)
            {
                var source = kv.Value;

                if (source.Finished)
                    continue;

                if (source.IsSpatial)
                {
                    Double3 relativePos = source.WorldPosition - _listenerWorldPosition;
                    _al.SetSourceProperty(source.AlSourceId, SourceVector3.Position,
                        (float)relativePos.X, (float)relativePos.Y, (float)relativePos.Z);

                    double dx = relativePos.X, dy = relativePos.Y, dz = relativePos.Z;
                    double distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);

                    AudioSourceSettings? s = source.Settings;
                    if (s != null && distance > s.MaxDistance)
                    {
                        double openalGainAtMax = s.MinDistance
                            / (s.MinDistance + s.RolloffFactor * (s.MaxDistance - s.MinDistance));
                        double dBatMax = s.ReferenceDbLevel + 20.0 * Math.Log10(Math.Max(openalGainAtMax, 1e-12));
                        double remainingDb = dBatMax - s.CullDbThreshold;
                        double extraRange = Math.Max(remainingDb, 0.0);

                        double extraAttenuation;
                        if (extraRange > 0.0)
                        {
                            extraAttenuation = 1.0 - (distance - s.MaxDistance) / extraRange;
                            if (extraAttenuation < 0.0) extraAttenuation = 0.0;
                        }
                        else
                        {
                            extraAttenuation = 0.0;
                        }

                        float finalGain = (float)s.Volume * _masterVolume * (float)extraAttenuation;
                        _al.SetSourceProperty(source.AlSourceId, SourceFloat.Gain, finalGain);
                    }
                    else
                    {
                        float baseGain = source.Settings != null
                            ? (float)source.Settings.Volume * _masterVolume
                            : source.Volume * _masterVolume;
                        _al.SetSourceProperty(source.AlSourceId, SourceFloat.Gain, baseGain);
                    }

                    continue;
                }

                _al.GetSourceProperty(source.AlSourceId, SourceFloat.SecOffset, out float sec);
                source.PlaybackPosition = sec;

                if (!source.IsLooping && source.CurrentClipId != null)
                {
                    if (_clips.TryGetValue(source.CurrentClipId, out var clip)
                        && clip.DurationSeconds > 0.0
                        && sec >= clip.DurationSeconds - 0.05)
                    {
                        source.Finished = true;
                        _al.DeleteSource(source.AlSourceId);
                        toRemove.Add(kv.Key);
                    }
                }
            }

            foreach (string id in toRemove)
                _sources.Remove(id);
        }

        internal static string SourceKey(string sceneId, string objectId)
        {
            return $"{sceneId}::{objectId}";
        }

        private static int ResolveAlFormat(int channels, int bitsPerSample)
        {
            if (channels == 1 && bitsPerSample == 8)
                return (int)BufferFormat.Mono8;
            if (channels == 1 && bitsPerSample == 16)
                return (int)BufferFormat.Mono16;
            if (channels == 2 && bitsPerSample == 8)
                return (int)BufferFormat.Stereo8;
            if (channels == 2 && bitsPerSample == 16)
                return (int)BufferFormat.Stereo16;
            return -1;
        }

        private static WavDecoder.WavData ConvertWav32To16(WavDecoder.WavData input)
        {
            int bytesPerSample = 4;
            int sampleCount = input.PcmBytes.Length / bytesPerSample;
            byte[] pcm16 = new byte[sampleCount * 2];

            for (int i = 0; i < sampleCount; i++)
            {
                float sample = BitConverter.ToSingle(input.PcmBytes, i * 4);
                short s = (short)Math.Clamp((int)(sample * 32767f), -32768, 32767);
                pcm16[i * 2] = (byte)(s & 0xFF);
                pcm16[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            return new WavDecoder.WavData
            {
                SampleRate = input.SampleRate,
                Channels = input.Channels,
                BitsPerSample = 16,
                TotalSamples = sampleCount / input.Channels,
                PcmBytes = pcm16
            };
        }

        private static WavDecoder.WavData DecodeOgg(byte[] fileBytes)
        {
            using var ms = new MemoryStream(fileBytes);
            using var reader = new NVorbis.VorbisReader(ms, false);

            int channels = reader.Channels;
            int sampleRate = reader.SampleRate;
            long totalSamples = reader.TotalSamples;

            float[] floatBuffer = new float[totalSamples * channels];
            int read = reader.ReadSamples(floatBuffer, 0, floatBuffer.Length);

            byte[] pcmBytes;
            if (channels == 1)
            {
                pcmBytes = ConvertFloatTo16BitMono(floatBuffer, read);
            }
            else
            {
                pcmBytes = ConvertFloatTo16BitStereo(floatBuffer, read);
            }

            int samples = read / channels;

            return new WavDecoder.WavData
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = 16,
                TotalSamples = samples,
                PcmBytes = pcmBytes
            };
        }

        private static WavDecoder.WavData DecodeMp3(byte[] fileBytes)
        {
            using var ms = new MemoryStream(fileBytes);
            var decoder = new NLayer.MpegFile(ms);

            int channels = decoder.Channels;
            int sampleRate = decoder.SampleRate;

            var allSamples = new List<float>();
            float[] buffer = new float[16384];
            int read;
            while ((read = decoder.ReadSamples(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                    allSamples.Add(buffer[i]);
            }

            float[] floatBuffer = allSamples.ToArray();
            int totalRead = floatBuffer.Length;

            byte[] pcmBytes;
            if (channels == 1)
            {
                pcmBytes = ConvertFloatTo16BitMono(floatBuffer, totalRead);
            }
            else
            {
                pcmBytes = ConvertFloatTo16BitStereo(floatBuffer, totalRead);
            }

            int totalSamples = totalRead / channels;

            return new WavDecoder.WavData
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = 16,
                TotalSamples = totalSamples,
                PcmBytes = pcmBytes
            };
        }

        private static byte[] ConvertFloatTo16BitMono(float[] samples, int count)
        {
            byte[] pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short s = (short)Math.Clamp((int)(samples[i] * 32767f), -32768, 32767);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }

        private static byte[] ConvertFloatTo16BitStereo(float[] samples, int count)
        {
            byte[] pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                short s = (short)Math.Clamp((int)(samples[i] * 32767f), -32768, 32767);
                pcm[i * 2] = (byte)(s & 0xFF);
                pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            return pcm;
        }
    }
}
