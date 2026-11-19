using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    public interface ITimeStateAnchorStore
    {
        int Count { get; }

        bool TryGetAnchor(long anchorId, out TimeStateAnchor anchor);

        bool TryAddAnchor(long anchorId, in TimeStateAnchor anchor, out string reason);

        bool TryUpdateAnchor(long anchorId, in TimeStateAnchor anchor, out string reason);

        bool TryRemoveAnchor(long anchorId, out string reason);
    }

    internal sealed class InMemoryTimeStateAnchorStore : ITimeStateAnchorStore
    {
        private readonly List<TimeStateAnchor?> _anchors = new List<TimeStateAnchor?>();

        public int Count { get; private set; }

        public InMemoryTimeStateAnchorStore()
        {
            _anchors.Add(null);
        }

        public bool TryGetAnchor(long anchorId, out TimeStateAnchor anchor)
        {
            anchor = default;

            if (anchorId < 0 || anchorId >= _anchors.Count)
                return false;

            TimeStateAnchor? slot = _anchors[(int)anchorId];

            if (slot == null)
                return false;

            anchor = slot.Value;
            return true;
        }

        public bool TryAddAnchor(long anchorId, in TimeStateAnchor anchor, out string reason)
        {
            reason = string.Empty;

            if (anchorId != _anchors.Count)
            {
                reason = "Anchor ids must be appended sequentially.";
                return false;
            }

            _anchors.Add(anchor);
            Count++;
            return true;
        }

        public bool TryUpdateAnchor(long anchorId, in TimeStateAnchor anchor, out string reason)
        {
            reason = string.Empty;

            if (anchorId < 0 || anchorId >= _anchors.Count || _anchors[(int)anchorId] == null)
            {
                reason = $"Anchor {anchorId} does not exist.";
                return false;
            }

            _anchors[(int)anchorId] = anchor;
            return true;
        }

        public bool TryRemoveAnchor(long anchorId, out string reason)
        {
            reason = string.Empty;

            if (anchorId < 0 || anchorId >= _anchors.Count || _anchors[(int)anchorId] == null)
            {
                reason = $"Anchor {anchorId} does not exist.";
                return false;
            }

            _anchors[(int)anchorId] = null;
            Count--;
            return true;
        }
    }
}
