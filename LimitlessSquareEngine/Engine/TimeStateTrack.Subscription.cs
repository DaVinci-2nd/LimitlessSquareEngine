using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace LimitlessSquareEngine.Engine
{
    public sealed class TimeStateSubscription
    {
        public long Id;
        public LuaScriptInstance? Instance;
        public string CallbackName = string.Empty;
        public double IntervalSeconds = 0.25;
        public double LastSampleTime;
        public List<long> AnchorIds = new List<long>();
    }

    internal struct TimeStateSampleJob
    {
        public long AnchorId;
        public TimeStateAnchor Anchor;
        public TimeStateTrackData? Track;
        public double SampleTime;
    }

    internal readonly struct TimeStateSampleResult
    {
        public readonly long AnchorId;
        public readonly double Value;
        public readonly byte Phase;
        public readonly bool Completed;

        public TimeStateSampleResult(long anchorId, double value, byte phase, bool completed)
        {
            AnchorId = anchorId;
            Value = value;
            Phase = phase;
            Completed = completed;
        }
    }

    public static partial class TimeStateTrackManager
    {
        private static readonly Dictionary<long, TimeStateSubscription> _subscriptions = new Dictionary<long, TimeStateSubscription>();
        private static readonly Dictionary<long, List<long>> _anchorSubscriptions = new Dictionary<long, List<long>>();
        private static readonly ConcurrentQueue<TimeStateSampleJob> _pendingSamples = new ConcurrentQueue<TimeStateSampleJob>();
        private static readonly ConcurrentQueue<TimeStateSampleResult> _sampleResults = new ConcurrentQueue<TimeStateSampleResult>();
        private static readonly List<TimeStateSampleJob> _inFlightSamples = new List<TimeStateSampleJob>();
        private static readonly object _inFlightLock = new object();
        private static readonly HashSet<long> _queuedAnchorIds = new HashSet<long>();
        private static long _nextSubscriptionId = 1;

        public static int MaxSampleCommitsPerTick = 256;
        public static int MaxInFlightSamples = 64;

        public static int SubscriptionCount => _subscriptions.Count;

        public static bool TrySubscribe(LuaScriptInstance instance, IReadOnlyList<long> anchorIds, string callbackName,
            double intervalSeconds, out long subscriptionId, out string reason)
        {
            subscriptionId = 0;
            reason = string.Empty;

            if (instance == null)
            {
                reason = "Script instance is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(callbackName))
            {
                reason = "Callback name is null or empty.";
                return false;
            }

            if (anchorIds == null || anchorIds.Count == 0)
            {
                reason = "Anchor id list is null or empty.";
                return false;
            }

            for (int i = 0; i < anchorIds.Count; i++)
            {
                if (!_store.TryGetAnchor(anchorIds[i], out TimeStateAnchor _))
                {
                    reason = $"Anchor {anchorIds[i]} does not exist.";
                    return false;
                }
            }

            if (double.IsNaN(intervalSeconds) || intervalSeconds < 0.0)
                intervalSeconds = 0.25;

            TimeStateSubscription subscription = new TimeStateSubscription
            {
                Id = _nextSubscriptionId++,
                Instance = instance,
                CallbackName = callbackName,
                IntervalSeconds = intervalSeconds,
                LastSampleTime = GameClock.Now
            };

            for (int i = 0; i < anchorIds.Count; i++)
            {
                subscription.AnchorIds.Add(anchorIds[i]);

                if (!_anchorSubscriptions.TryGetValue(anchorIds[i], out List<long>? subIds))
                {
                    subIds = new List<long>();
                    _anchorSubscriptions[anchorIds[i]] = subIds;
                }

                subIds.Add(subscription.Id);
            }

            _subscriptions[subscription.Id] = subscription;
            subscriptionId = subscription.Id;
            return true;
        }

        public static bool TryUnsubscribe(long subscriptionId, out string reason)
        {
            reason = string.Empty;

            if (!_subscriptions.Remove(subscriptionId, out TimeStateSubscription? subscription) || subscription == null)
            {
                reason = $"Subscription {subscriptionId} does not exist.";
                return false;
            }

            for (int i = 0; i < subscription.AnchorIds.Count; i++)
            {
                long anchorId = subscription.AnchorIds[i];

                if (_anchorSubscriptions.TryGetValue(anchorId, out List<long>? subIds))
                {
                    subIds.Remove(subscriptionId);

                    if (subIds.Count == 0)
                        _anchorSubscriptions.Remove(anchorId);
                }
            }

            return true;
        }

        public static void TickAll()
        {
            double now = GameClock.Now;
            _queuedAnchorIds.Clear();

            foreach (KeyValuePair<long, TimeStateSubscription> pair in _subscriptions)
            {
                TimeStateSubscription subscription = pair.Value;

                if (now - subscription.LastSampleTime < subscription.IntervalSeconds)
                    continue;

                subscription.LastSampleTime = now;

                for (int i = 0; i < subscription.AnchorIds.Count; i++)
                {
                    long anchorId = subscription.AnchorIds[i];

                    if (!_queuedAnchorIds.Add(anchorId))
                        continue;

                    if (!_store.TryGetAnchor(anchorId, out TimeStateAnchor anchor))
                        continue;

                    if (anchor.Handle < 0 || anchor.Handle >= _trackByHandle.Count)
                        continue;

                    TimeStateTrackData? track = _trackByHandle[anchor.Handle];

                    if (track == null)
                        continue;

                    _pendingSamples.Enqueue(new TimeStateSampleJob
                    {
                        AnchorId = anchorId,
                        Anchor = anchor,
                        Track = track,
                        SampleTime = now
                    });
                }
            }

            EnsureSampleWorkers();
            DrainSampleResults();
        }

        private static void EnsureSampleWorkers()
        {
            bool needMore;

            lock (_inFlightLock)
                needMore = _inFlightSamples.Count < MaxInFlightSamples && _pendingSamples.Count > 0;

            if (!needMore)
                return;

            ThreadPool.QueueUserWorkItem(SampleWorkerLoop);
        }

        private static void SampleWorkerLoop(object? state)
        {
            while (true)
            {
                if (!_pendingSamples.TryDequeue(out TimeStateSampleJob job))
                    return;

                lock (_inFlightLock)
                {
                    if (_inFlightSamples.Count >= MaxInFlightSamples)
                    {
                        _pendingSamples.Enqueue(job);
                        return;
                    }

                    _inFlightSamples.Add(job);
                }

                try
                {
                    if (job.Track != null &&
                        TimeStateEvaluator.TryEvaluate(job.Track, job.Anchor, job.SampleTime, 0,
                            out double value, out byte phase, out bool completed, out string _))
                    {
                        _sampleResults.Enqueue(new TimeStateSampleResult(job.AnchorId, value, phase, completed));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Time state sample failed for anchor {job.AnchorId}: {ex.Message}");
                }
                finally
                {
                    lock (_inFlightLock)
                        _inFlightSamples.Remove(job);
                }
            }
        }

        private static void DrainSampleResults()
        {
            int processed = 0;
            Dictionary<long, List<TimeStateSampleResult>>? perSubscription = null;

            while (processed < MaxSampleCommitsPerTick && _sampleResults.TryDequeue(out TimeStateSampleResult result))
            {
                processed++;

                if (!_anchorSubscriptions.TryGetValue(result.AnchorId, out List<long>? subIds) || subIds == null || subIds.Count == 0)
                    continue;

                perSubscription ??= new Dictionary<long, List<TimeStateSampleResult>>();

                for (int i = 0; i < subIds.Count; i++)
                {
                    if (!perSubscription.TryGetValue(subIds[i], out List<TimeStateSampleResult>? list))
                    {
                        list = new List<TimeStateSampleResult>();
                        perSubscription[subIds[i]] = list;
                    }

                    list.Add(result);
                }
            }

            if (perSubscription == null)
                return;

            foreach (KeyValuePair<long, List<TimeStateSampleResult>> pair in perSubscription)
            {
                if (!_subscriptions.TryGetValue(pair.Key, out TimeStateSubscription? subscription) ||
                    subscription?.Instance == null)
                    continue;

                List<TimeStateSampleResult> results = pair.Value;

                if (results.Count == 0)
                    continue;

                Table table = new Table(subscription.Instance.LuaScript);

                for (int i = 0; i < results.Count; i++)
                {
                    Table item = new Table(subscription.Instance.LuaScript);
                    item["id"] = results[i].AnchorId;
                    item["value"] = results[i].Value;
                    item["phase"] = results[i].Phase;
                    item["completed"] = results[i].Completed;
                    table.Append(DynValue.NewTable(item));
                }

                Program.CallScriptFunction(subscription.Instance, subscription.CallbackName,
                    new DynValue[] { DynValue.NewTable(table) });
            }
        }
    }
}
