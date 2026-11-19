using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    public enum TimeStateEvaluatorKind
    {
        Linear,
        Keyframes,
        Probability,
        Noise
    }

    public enum TimeStatePostEnd
    {
        Clamp,
        Loop,
        Chain
    }

    public sealed class TimeStateKeyframe
    {
        public double Time;
        public double Value;
        public bool Stepped;
    }

    public sealed class TimeStatePhaseBand
    {
        public double Threshold;
        public byte Phase;
    }

    public sealed class TimeStateProbBand
    {
        public double MinDuration;
        public double MaxDuration;
        public double Value;
    }

    public sealed class TimeStateTrackData
    {
        public string TrackId = string.Empty;
        public int Handle;
        public TimeStateEvaluatorKind Kind = TimeStateEvaluatorKind.Linear;
        public double StartState;
        public double EndState;
        public bool OpenEnded;
        public double DurationSeconds;
        public double Slope;
        public uint Seed;
        public List<TimeStateKeyframe> Keys = new List<TimeStateKeyframe>();
        public List<TimeStatePhaseBand> Phases = new List<TimeStatePhaseBand>();
        public List<TimeStateProbBand> ProbBands = new List<TimeStateProbBand>();
        public double NoiseAmplitude;
        public double NoiseScale = 1.0;
        public TimeStatePostEnd PostEnd = TimeStatePostEnd.Clamp;
        public string ChainToTrackId = string.Empty;
        public Noise3D? Noise;
    }

    public readonly struct TimeStateAnchor
    {
        public readonly int Handle;
        public readonly double AnchorTime;
        public readonly uint Seed;
        public readonly float Rate;
        public readonly double StartOverride;

        public TimeStateAnchor(int handle, double anchorTime, uint seed, float rate, double startOverride)
        {
            Handle = handle;
            AnchorTime = anchorTime;
            Seed = seed;
            Rate = rate;
            StartOverride = startOverride;
        }
    }

    public static partial class TimeStateTrackManager
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TimeStateTrackData> _tracks =
            new System.Collections.Concurrent.ConcurrentDictionary<string, TimeStateTrackData>(StringComparer.Ordinal);

        private static readonly List<TimeStateTrackData?> _trackByHandle = new List<TimeStateTrackData?>();
        private static readonly object _handleLock = new object();
        private static ITimeStateAnchorStore _store = new InMemoryTimeStateAnchorStore();
        private static long _nextAnchorId = 1;

        public static int TrackCount => _tracks.Count;
        public static int AnchorCount => _store.Count;

        public static void SetStore(ITimeStateAnchorStore store)
        {
            if (store == null)
                return;

            _store = store;
            _nextAnchorId = 1;
        }

        public static bool TryRegisterTrack(string trackId, TimeStateTrackData track, out string reason)
        {
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(trackId))
            {
                reason = "Track id is null or empty.";
                return false;
            }

            if (track == null)
            {
                reason = "Track definition is null.";
                return false;
            }

            if (track.Kind == TimeStateEvaluatorKind.Keyframes && (track.Keys == null || track.Keys.Count == 0))
            {
                reason = $"Track '{trackId}' has no keyframes.";
                return false;
            }

            if (track.Kind == TimeStateEvaluatorKind.Probability && (track.ProbBands == null || track.ProbBands.Count == 0))
            {
                reason = $"Track '{trackId}' has no probability bands.";
                return false;
            }

            if (track.Kind == TimeStateEvaluatorKind.Noise && track.Noise == null)
                track.Noise = new Noise3D(track.Seed);

            if (track.PostEnd == TimeStatePostEnd.Chain &&
                string.Equals(track.ChainToTrackId, trackId, StringComparison.Ordinal))
            {
                reason = $"Track '{trackId}' chains to itself.";
                return false;
            }

            if (track.PostEnd == TimeStatePostEnd.Chain && !string.IsNullOrWhiteSpace(track.ChainToTrackId))
            {
                string cursor = track.ChainToTrackId;

                for (int depth = 0; depth < TimeStateEvaluator.MaxChainDepth; depth++)
                {
                    if (string.Equals(cursor, trackId, StringComparison.Ordinal))
                    {
                        reason = $"Track '{trackId}' chain forms a cycle.";
                        return false;
                    }

                    if (!_tracks.TryGetValue(cursor, out TimeStateTrackData? next) || next == null ||
                        next.PostEnd != TimeStatePostEnd.Chain || string.IsNullOrWhiteSpace(next.ChainToTrackId))
                        break;

                    cursor = next.ChainToTrackId;
                }
            }

            track.TrackId = trackId;

            if (_tracks.TryGetValue(trackId, out _))
                Console.WriteLine($"[!] Track '{trackId}' is already registered; overwriting.");

            lock (_handleLock)
            {
                if (_tracks.TryGetValue(trackId, out TimeStateTrackData? existing) && existing != null)
                {
                    track.Handle = existing.Handle;
                    _trackByHandle[track.Handle] = track;
                }
                else
                {
                    track.Handle = _trackByHandle.Count;
                    _trackByHandle.Add(track);
                }
            }

            _tracks[trackId] = track;
            return true;
        }

        public static bool TryResolveTrack(string trackId, out TimeStateTrackData track)
        {
            track = null!;

            if (!string.IsNullOrWhiteSpace(trackId) &&
                _tracks.TryGetValue(trackId, out TimeStateTrackData? resolved) && resolved != null)
            {
                track = resolved;
                return true;
            }

            return false;
        }

        public static void UnregisterTrack(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId) ||
                !_tracks.TryRemove(trackId, out TimeStateTrackData? removed) || removed == null)
                return;

            lock (_handleLock)
            {
                if (removed.Handle >= 0 && removed.Handle < _trackByHandle.Count &&
                    ReferenceEquals(_trackByHandle[removed.Handle], removed))
                    _trackByHandle[removed.Handle] = null;
            }
        }

        public static bool TryAttachAnchor(string trackId, double anchorTime, uint seed, float rate, double startOverride,
            out long anchorId, out string reason)
        {
            anchorId = 0;
            reason = string.Empty;

            if (float.IsNaN(rate) || rate <= 0f)
            {
                reason = "Anchor rate must be positive.";
                return false;
            }

            if (!TryResolveTrack(trackId, out TimeStateTrackData track))
            {
                reason = $"Track '{trackId}' is not registered.";
                return false;
            }

            TimeStateAnchor anchor = new TimeStateAnchor(track.Handle, anchorTime, seed, rate, startOverride);

            long assignedId = _nextAnchorId++;

            if (!_store.TryAddAnchor(assignedId, in anchor, out reason))
                return false;

            anchorId = assignedId;
            return true;
        }

        public static bool TryRemoveAnchor(long anchorId, out string reason)
        {
            return _store.TryRemoveAnchor(anchorId, out reason);
        }

        public static bool TrySample(long anchorId, out double value, out byte phase, out bool completed, out string reason)
        {
            return TrySampleAt(anchorId, GameClock.Now, out value, out phase, out completed, out reason);
        }

        public static bool TrySampleAt(long anchorId, double sampleTime, out double value, out byte phase, out bool completed, out string reason)
        {
            value = 0.0;
            phase = 0;
            completed = false;
            reason = string.Empty;

            if (!_store.TryGetAnchor(anchorId, out TimeStateAnchor anchor))
            {
                reason = $"Anchor {anchorId} does not exist.";
                return false;
            }

            if (anchor.Handle < 0 || anchor.Handle >= _trackByHandle.Count)
            {
                reason = $"Anchor {anchorId} references an unregistered track handle.";
                return false;
            }

            TimeStateTrackData? track = _trackByHandle[anchor.Handle];

            if (track == null)
            {
                reason = $"Anchor {anchorId} references an unregistered track handle.";
                return false;
            }

            return TimeStateEvaluator.TryEvaluate(track, in anchor, sampleTime, 0, out value, out phase, out completed, out reason);
        }

        public static bool TryReanchor(long anchorId, string newTrackId, bool preserveValue, double? explicitValue,
            uint? newSeed, float? newRate, out string reason)
        {
            reason = string.Empty;

            if (!_store.TryGetAnchor(anchorId, out TimeStateAnchor anchor))
            {
                reason = $"Anchor {anchorId} does not exist.";
                return false;
            }

            if (!TryResolveTrack(newTrackId, out TimeStateTrackData next))
            {
                reason = $"Track '{newTrackId}' is not registered.";
                return false;
            }

            double startOverride = double.NaN;

            if (explicitValue.HasValue)
            {
                startOverride = explicitValue.Value;
            }
            else if (preserveValue)
            {
                if (anchor.Handle < 0 || anchor.Handle >= _trackByHandle.Count || _trackByHandle[anchor.Handle] == null)
                {
                    reason = $"Anchor {anchorId} references an unregistered track handle.";
                    return false;
                }

                if (!TimeStateEvaluator.TryEvaluate(_trackByHandle[anchor.Handle]!, in anchor, GameClock.Now, 0,
                        out double value, out _, out _, out string sampleReason))
                {
                    reason = sampleReason;
                    return false;
                }

                startOverride = value;
            }

            if (!double.IsNaN(startOverride) &&
                next.Kind != TimeStateEvaluatorKind.Linear && next.Kind != TimeStateEvaluatorKind.Noise)
            {
                Console.WriteLine($"[!] Reanchor start override only applies to linear or noise tracks; ignoring for '{newTrackId}'.");
                startOverride = double.NaN;
            }

            TimeStateAnchor updated = new TimeStateAnchor(next.Handle, GameClock.Now, newSeed ?? anchor.Seed, newRate ?? anchor.Rate, startOverride);

            return _store.TryUpdateAnchor(anchorId, in updated, out reason);
        }

        public static bool TrySetRate(long anchorId, float rate, out string reason)
        {
            reason = string.Empty;

            if (float.IsNaN(rate) || rate <= 0f)
            {
                reason = "Anchor rate must be positive.";
                return false;
            }

            if (!_store.TryGetAnchor(anchorId, out TimeStateAnchor anchor))
            {
                reason = $"Anchor {anchorId} does not exist.";
                return false;
            }

            if (Math.Abs(rate - anchor.Rate) < 1e-9)
                return true;

            double progress = (GameClock.Now - anchor.AnchorTime) * anchor.Rate;
            double newAnchorTime = GameClock.Now - progress / rate;

            TimeStateAnchor updated = new TimeStateAnchor(anchor.Handle, newAnchorTime, anchor.Seed, rate, anchor.StartOverride);

            return _store.TryUpdateAnchor(anchorId, in updated, out reason);
        }
    }
}
