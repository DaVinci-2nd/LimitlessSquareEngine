using System;

namespace LimitlessSquareEngine.Engine
{
    public class AudioClip
    {
        public string ClipId { get; set; } = "";
        public uint AlBufferId { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public int BitsPerSample { get; set; }
        public int TotalSamples { get; set; }
        public double DurationSeconds { get; set; }
        public int AlFormat { get; set; }

        internal string? FilePath { get; set; }
    }

    public class AudioListenerSettings
    {
        public string OutputMode { get; set; } = "Direct";
        public string? TargetSourceId { get; set; }
        public bool Mute { get; set; }
    }

    public class AudioSourceSettings
    {
        public string ClipId { get; set; } = "";
        public double Volume { get; set; } = 1.0;
        public double MinDistance { get; set; } = 1.0;
        public double MaxDistance { get; set; } = 50.0;
        public double RolloffFactor { get; set; } = 1.0;
        public string AttenuationModel { get; set; } = "InverseDistanceClamped";
        public double SpatialBlend { get; set; } = 1.0;
        public bool Loop { get; set; }
        public bool PlayOnAwake { get; set; }
        public double DopplerFactor { get; set; } = 1.0;
        public double ReferenceDbLevel { get; set; } = 60.0;
        public double CullDbThreshold { get; set; } = -60.0;
        public double SpeedOfSound { get; set; } = 343.0;
    }
}
