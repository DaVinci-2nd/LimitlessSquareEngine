using System;

namespace LimitlessSquareEngine.Engine
{
    internal class AudioSourceRuntime
    {
        public string SourceId { get; set; } = "";
        public string SceneId { get; set; } = "";
        public uint AlSourceId { get; set; }
        public string? CurrentClipId { get; set; }
        public float Volume { get; set; } = 1f;
        public float Pitch { get; set; } = 1f;
        public float Pan { get; set; }
        public bool IsPlaying { get; set; }
        public bool IsPaused { get; set; }
        public bool IsLooping { get; set; }
        public double PlaybackPosition { get; set; }
        public bool IsSpatial { get; set; }
        public bool Finished { get; set; }

        public Double3 LastWorldPosition;
        public Double3 WorldPosition;
        public Double3 WorldVelocity;
        public AudioSourceSettings? Settings { get; set; }
    }
}
