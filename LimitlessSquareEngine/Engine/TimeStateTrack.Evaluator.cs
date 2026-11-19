using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    internal static class TimeStateEvaluator
    {
        public const int MaxChainDepth = 8;

        public static bool TryEvaluate(TimeStateTrackData track, in TimeStateAnchor anchor, double sampleTime, int chainDepth,
            out double value, out byte phase, out bool completed, out string reason)
        {
            value = 0.0;
            phase = 0;
            completed = false;
            reason = string.Empty;

            if (track == null)
            {
                reason = "Track definition is null.";
                return false;
            }

            double t = (sampleTime - anchor.AnchorTime) * anchor.Rate;

            if (!TryEvaluateCore(track, in anchor, t, chainDepth, out value, out completed, out reason))
                return false;

            phase = ResolvePhase(track, value);
            return true;
        }

        private static bool TryEvaluateCore(TimeStateTrackData track, in TimeStateAnchor anchor, double t, int chainDepth,
            out double value, out bool completed, out string reason)
        {
            value = 0.0;
            completed = false;
            reason = string.Empty;

            double start = double.IsNaN(anchor.StartOverride) ? track.StartState : anchor.StartOverride;

            switch (track.Kind)
            {
                case TimeStateEvaluatorKind.Linear:
                {
                    if (track.OpenEnded)
                    {
                        value = start + track.Slope * t;
                        return true;
                    }

                    if (track.DurationSeconds <= 0.0)
                    {
                        reason = $"Track '{track.TrackId}' has a non-positive duration.";
                        return false;
                    }

                    if (t >= track.DurationSeconds)
                        return TryEvaluatePostEnd(track, in anchor, t, chainDepth, track.DurationSeconds,
                            start + (track.EndState - track.StartState), out value, out completed, out reason);

                    value = start + (track.EndState - track.StartState) * (t / track.DurationSeconds);
                    return true;
                }

                case TimeStateEvaluatorKind.Keyframes:
                {
                    List<TimeStateKeyframe> keys = track.Keys;

                    if (keys == null || keys.Count == 0)
                    {
                        reason = $"Track '{track.TrackId}' has no keyframes.";
                        return false;
                    }

                    if (t <= keys[0].Time)
                    {
                        value = keys[0].Value;
                        return true;
                    }

                    int lastIndex = keys.Count - 1;

                    if (t >= keys[lastIndex].Time)
                        return TryEvaluatePostEnd(track, in anchor, t, chainDepth, keys[lastIndex].Time, keys[lastIndex].Value,
                            out value, out completed, out reason);

                    int lower = 0;
                    int upper = lastIndex;

                    while (upper - lower > 1)
                    {
                        int middle = (lower + upper) / 2;

                        if (keys[middle].Time <= t)
                            lower = middle;
                        else
                            upper = middle;
                    }

                    TimeStateKeyframe from = keys[lower];
                    TimeStateKeyframe to = keys[upper];

                    if (from.Stepped)
                    {
                        value = from.Value;
                        return true;
                    }

                    double span = to.Time - from.Time;

                    if (span <= 0.0)
                    {
                        value = to.Value;
                        return true;
                    }

                    double ratio = (t - from.Time) / span;
                    double m0 = SlopeAt(keys, lower);
                    double m1 = SlopeAt(keys, upper);

                    double r2 = ratio * ratio;
                    double r3 = r2 * ratio;
                    double h00 = 2.0 * r3 - 3.0 * r2 + 1.0;
                    double h10 = r3 - 2.0 * r2 + ratio;
                    double h01 = -2.0 * r3 + 3.0 * r2;
                    double h11 = r3 - r2;

                    value = h00 * from.Value + h10 * span * m0 + h01 * to.Value + h11 * span * m1;
                    return true;
                }

                case TimeStateEvaluatorKind.Probability:
                {
                    List<TimeStateProbBand> bands = track.ProbBands;

                    if (bands == null || bands.Count == 0)
                    {
                        reason = $"Track '{track.TrackId}' has no probability bands.";
                        return false;
                    }

                    double elapsed = 0.0;

                    for (int i = 0; i < bands.Count; i++)
                    {
                        TimeStateProbBand band = bands[i];

                        if (band.MaxDuration < band.MinDuration)
                        {
                            reason = $"Track '{track.TrackId}' has a probability band where max duration is less than min duration.";
                            return false;
                        }

                        double bandDuration = band.MinDuration +
                            (band.MaxDuration - band.MinDuration) * HashToUnit01(track.Seed, anchor.Seed, (uint)i);

                        if (t < elapsed + bandDuration)
                        {
                            value = band.Value;
                            return true;
                        }

                        elapsed += bandDuration;
                    }

                    return TryEvaluatePostEnd(track, in anchor, t, chainDepth, elapsed, bands[bands.Count - 1].Value,
                        out value, out completed, out reason);
                }

                case TimeStateEvaluatorKind.Noise:
                {
                    if (track.Noise == null)
                    {
                        reason = $"Track '{track.TrackId}' has no noise instance.";
                        return false;
                    }

                    if (track.OpenEnded)
                    {
                        value = start + track.Slope * t +
                            track.NoiseAmplitude * track.Noise.Fbm(anchor.Seed, t / track.NoiseScale, 0.0);
                        return true;
                    }

                    if (track.DurationSeconds <= 0.0)
                    {
                        reason = $"Track '{track.TrackId}' has a non-positive duration.";
                        return false;
                    }

                    if (t >= track.DurationSeconds)
                        return TryEvaluatePostEnd(track, in anchor, t, chainDepth, track.DurationSeconds,
                            start + (track.EndState - track.StartState), out value, out completed, out reason);

                    value = start + (track.EndState - track.StartState) * (t / track.DurationSeconds) +
                        track.NoiseAmplitude * track.Noise.Fbm(anchor.Seed, t / track.NoiseScale, 0.0);
                    return true;
                }
            }

            reason = $"Track '{track.TrackId}' has an unknown evaluator kind.";
            return false;
        }

        private static bool TryEvaluatePostEnd(TimeStateTrackData track, in TimeStateAnchor anchor, double t, int chainDepth,
            double duration, double endValue, out double value, out bool completed, out string reason)
        {
            value = 0.0;
            completed = false;
            reason = string.Empty;

            switch (track.PostEnd)
            {
                case TimeStatePostEnd.Clamp:
                    value = endValue;
                    completed = true;
                    return true;

                case TimeStatePostEnd.Loop:
                    return TryEvaluateCore(track, in anchor, duration > 0.0 ? t % duration : 0.0, chainDepth,
                        out value, out completed, out reason);

                case TimeStatePostEnd.Chain:
                {
                    if (chainDepth >= MaxChainDepth ||
                        string.IsNullOrWhiteSpace(track.ChainToTrackId) ||
                        !TimeStateTrackManager.TryResolveTrack(track.ChainToTrackId, out TimeStateTrackData next))
                    {
                        value = endValue;
                        completed = true;
                        return true;
                    }

                    return TryEvaluateCore(next, in anchor, t - duration, chainDepth + 1, out value, out completed, out reason);
                }
            }

            value = endValue;
            completed = true;
            return true;
        }

        private static double SlopeAt(List<TimeStateKeyframe> keys, int index)
        {
            int prev = Math.Max(0, index - 1);
            int next = Math.Min(keys.Count - 1, index + 1);

            double span = keys[next].Time - keys[prev].Time;

            if (span <= 0.0)
                return 0.0;

            return (keys[next].Value - keys[prev].Value) / span;
        }

        private static byte ResolvePhase(TimeStateTrackData track, double value)
        {
            List<TimeStatePhaseBand> phases = track.Phases;

            if (phases == null || phases.Count == 0)
                return 0;

            for (int i = 0; i < phases.Count; i++)
            {
                if (value < phases[i].Threshold)
                    return phases[i].Phase;
            }

            return phases[phases.Count - 1].Phase;
        }

        private static double HashToUnit01(uint a, uint b, uint c)
        {
            uint state = a ^ (b * 0x9E3779B9u) ^ (c * 0x85EBCA6Bu);

            if (state == 0u)
                state = 0x9E3779B9u;

            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;

            return (state & 0xFFFFFFu) / 16777216.0;
        }
    }
}
