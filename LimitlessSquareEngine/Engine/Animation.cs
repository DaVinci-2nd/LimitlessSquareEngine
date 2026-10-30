using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    public sealed class AnimationKeyframe
    {
        public double Time { get; set; }

        public float[] Value { get; set; } = Array.Empty<float>();

        public bool Stepped { get; set; }

        public float[] InTangent { get; set; } = Array.Empty<float>();

        public float[] OutTangent { get; set; } = Array.Empty<float>();
    }

    public sealed class AnimationChannel
    {
        public string Binding { get; set; } = "bone";

        public string Path { get; set; } = "";

        public string Property { get; set; } = "rotation";

        public string ValueType { get; set; } = "quaternion";

        public string Interpolation { get; set; } = "linear";

        public float[] RestPosition { get; set; } = Array.Empty<float>();

        public float[] RestRotation { get; set; } = Array.Empty<float>();

        public float[] RestScale { get; set; } = Array.Empty<float>();

        public float[] RestWorldRotation { get; set; } = Array.Empty<float>();

        public float[] RestParentWorldRotation { get; set; } = Array.Empty<float>();

        public List<AnimationKeyframe> Keys { get; set; } = new();
    }

    public sealed class AnimationEventData
    {
        public double Time { get; set; }

        public string Name { get; set; } = "";

        public string StringParameter { get; set; } = "";

        public float FloatParameter { get; set; }
    }

    public sealed class AnimationClipData
    {
        public string Name { get; set; } = "";

        public double Duration { get; set; }

        public bool Loop { get; set; } = true;

        public float FrameRate { get; set; } = 30f;

        public string PlayMode { get; set; } = "normal";

        public List<AnimationChannel> Channels { get; set; } = new();

        public List<AnimationEventData> Events { get; set; } = new();
    }

    internal readonly struct AnimationBoneRest
    {
        public readonly Double3 Position;

        public readonly DQuaternion Rotation;

        public readonly Double3 Scale;

        public readonly DQuaternion WorldRotation;

        public readonly DQuaternion ParentWorldRotation;

        public AnimationBoneRest(
            Double3 position,
            DQuaternion rotation,
            Double3 scale,
            DQuaternion worldRotation,
            DQuaternion parentWorldRotation)
        {
            Position = position;
            Rotation = rotation;
            Scale = scale;
            WorldRotation = worldRotation;
            ParentWorldRotation = parentWorldRotation;
        }

        public static AnimationBoneRest Identity => new AnimationBoneRest(
            Double3.Zero,
            DQuaternion.Identity,
            Double3.One,
            DQuaternion.Identity,
            DQuaternion.Identity);
    }

    internal sealed class AnimationChannelRuntime
    {
        public AnimationChannel Channel { get; init; } = new();

        public int[] SteppedKeyIndices { get; init; } = Array.Empty<int>();
    }

    internal sealed class AnimationPoseBuffer
    {
        public int BoneCount { get; init; }

        public Double3[] Translations { get; init; } = Array.Empty<Double3>();

        public DQuaternion[] Rotations { get; init; } = Array.Empty<DQuaternion>();

        public Double3[] Scales { get; init; } = Array.Empty<Double3>();

        public bool[] Touched { get; init; } = Array.Empty<bool>();

        public bool[] TouchedTranslations { get; init; } = Array.Empty<bool>();

        public bool[] TouchedRotations { get; init; } = Array.Empty<bool>();

        public bool[] TouchedScales { get; init; } = Array.Empty<bool>();

        public Dictionary<string, float> ExpressionWeights { get; } = new(StringComparer.Ordinal);

        public Double3 ObjectPosition { get; set; } = Double3.Zero;

        public DQuaternion ObjectRotation { get; set; } = DQuaternion.Identity;

        public Double3 ObjectScale { get; set; } = Double3.One;

        public bool HasObjectTransform { get; set; }

        public static AnimationPoseBuffer Create(int boneCount)
        {
            if (boneCount < 0)
                boneCount = 0;

            var buffer = new AnimationPoseBuffer
            {
                BoneCount = boneCount,
                Translations = new Double3[boneCount],
                Rotations = new DQuaternion[boneCount],
                Scales = new Double3[boneCount],
                Touched = new bool[boneCount],
                TouchedTranslations = new bool[boneCount],
                TouchedRotations = new bool[boneCount],
                TouchedScales = new bool[boneCount]
            };

            buffer.ResetToRest();
            return buffer;
        }

        public void CopyFrom(AnimationPoseBuffer other)
        {
            if (other == null)
                return;

            int count = Math.Min(BoneCount, other.BoneCount);

            for (int i = 0; i < count; i++)
            {
                Translations[i] = other.Translations[i];
                Rotations[i] = other.Rotations[i];
                Scales[i] = other.Scales[i];
                Touched[i] = other.Touched[i];
                TouchedTranslations[i] = other.TouchedTranslations[i];
                TouchedRotations[i] = other.TouchedRotations[i];
                TouchedScales[i] = other.TouchedScales[i];
            }

            ExpressionWeights.Clear();

            foreach (KeyValuePair<string, float> pair in other.ExpressionWeights)
                ExpressionWeights[pair.Key] = pair.Value;

            ObjectPosition = other.ObjectPosition;
            ObjectRotation = other.ObjectRotation;
            ObjectScale = other.ObjectScale;
            HasObjectTransform = other.HasObjectTransform;
        }

        public void ResetToRest()
        {
            for (int i = 0; i < BoneCount; i++)
            {
                Translations[i] = Double3.Zero;
                Rotations[i] = DQuaternion.Identity;
                Scales[i] = Double3.One;
                Touched[i] = false;
                TouchedTranslations[i] = false;
                TouchedRotations[i] = false;
                TouchedScales[i] = false;
            }

            ExpressionWeights.Clear();

            ObjectPosition = Double3.Zero;
            ObjectRotation = DQuaternion.Identity;
            ObjectScale = Double3.One;
            HasObjectTransform = false;
        }
    }

    internal sealed class AnimationClipRuntime
    {
        public string Key { get; init; } = "";

        public AnimationClipData Data { get; init; } = new();

        public double Duration { get; init; }

        public AnimationChannelRuntime[] Channels { get; init; } = Array.Empty<AnimationChannelRuntime>();

        public static AnimationClipRuntime Create(string key, AnimationClipData data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));

            var channels = new List<AnimationChannelRuntime>(data.Channels.Count);

            double duration = data.Duration;

            foreach (AnimationChannel channel in data.Channels)
            {
                if (channel == null || channel.Keys.Count == 0)
                    continue;

                channel.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));

                var steppedIndices = new List<int>();

                for (int i = 0; i < channel.Keys.Count; i++)
                {
                    if (channel.Keys[i].Stepped)
                        steppedIndices.Add(i);

                    if (channel.Keys[i].Time > duration)
                        duration = channel.Keys[i].Time;
                }

                channels.Add(new AnimationChannelRuntime
                {
                    Channel = channel,
                    SteppedKeyIndices = steppedIndices.ToArray()
                });
            }

            return new AnimationClipRuntime
            {
                Key = key,
                Data = data,
                Duration = duration,
                Channels = channels.ToArray()
            };
        }

        public void Sample(
            double time,
            bool stepped,
            AnimationPoseBuffer pose,
            Func<AnimationChannel, int> resolveBoneIndex,
            Func<int, AnimationBoneRest> resolveBoneRest)
        {
            if (pose == null)
                return;

            double duration = Duration;
            double sampleTime = time;

            if (duration > 0.0)
            {
                if (Data.Loop)
                {
                    sampleTime %= duration;

                    if (sampleTime < 0.0)
                        sampleTime += duration;
                }
                else
                {
                    if (sampleTime < 0.0)
                        sampleTime = 0.0;

                    if (sampleTime > duration)
                        sampleTime = duration;
                }
            }

            Span<float> scratch = stackalloc float[4];

            foreach (AnimationChannelRuntime channelRuntime in Channels)
            {
                AnimationChannel channel = channelRuntime.Channel;

                int valueCount = ResolveValueCount(channel.ValueType);

                if (valueCount <= 0 || valueCount > 4)
                    continue;

                int boneIndex = resolveBoneIndex != null ? resolveBoneIndex(channel) : -1;

                bool isExpression =
                    string.Equals(channel.Binding, "expression", StringComparison.OrdinalIgnoreCase);

                bool isWeight =
                    string.Equals(channel.Property, "weight", StringComparison.OrdinalIgnoreCase);

                bool isObjectTransform =
                    string.Equals(channel.Binding, "transform", StringComparison.OrdinalIgnoreCase);

                if (!isExpression && !isWeight && !isObjectTransform &&
                    (boneIndex < 0 || boneIndex >= pose.BoneCount))
                    continue;

                ResolveChannelValue(channelRuntime, sampleTime, stepped, scratch.Slice(0, valueCount));

                AnimationBoneRest boneRest = resolveBoneRest != null && boneIndex >= 0
                    ? resolveBoneRest(boneIndex)
                    : AnimationBoneRest.Identity;

                if (isObjectTransform)
                {
                    pose.HasObjectTransform = true;

                    if (string.Equals(channel.Property, "translation", StringComparison.OrdinalIgnoreCase) &&
                        valueCount >= 3)
                    {
                        pose.ObjectPosition = new Double3(scratch[0], scratch[1], scratch[2]);
                    }
                    else if (string.Equals(channel.Property, "scale", StringComparison.OrdinalIgnoreCase) &&
                             valueCount >= 3)
                    {
                        pose.ObjectScale = new Double3(scratch[0], scratch[1], scratch[2]);
                    }
                    else if (valueCount >= 4)
                    {
                        pose.ObjectRotation = new DQuaternion(
                            scratch[0],
                            scratch[1],
                            scratch[2],
                            scratch[3]).Normalized();
                    }

                    continue;
                }

                if (isExpression || isWeight)
                {
                    pose.ExpressionWeights[channel.Path] = Math.Clamp(scratch[0], 0f, 1f);
                    continue;
                }

                string property = channel.Property;

                if (string.Equals(property, "translation", StringComparison.OrdinalIgnoreCase))
                {
                    if (valueCount >= 3)
                    {
                        pose.Translations[boneIndex] = ApplyRestTranslation(
                            new Double3(scratch[0], scratch[1], scratch[2]),
                            channel.RestPosition,
                            channel.RestParentWorldRotation,
                            boneRest.Position,
                            boneRest.ParentWorldRotation);

                        pose.Touched[boneIndex] = true;
                        pose.TouchedTranslations[boneIndex] = true;
                    }

                    continue;
                }

                if (string.Equals(property, "scale", StringComparison.OrdinalIgnoreCase))
                {
                    if (valueCount >= 3)
                    {
                        pose.Scales[boneIndex] = ApplyRestScale(
                            new Double3(scratch[0], scratch[1], scratch[2]),
                            channel.RestScale,
                            boneRest.Scale);

                        pose.Touched[boneIndex] = true;
                        pose.TouchedScales[boneIndex] = true;
                    }

                    continue;
                }

                if (valueCount >= 4)
                {
                    pose.Rotations[boneIndex] = ApplyRestRotation(
                        new DQuaternion(
                            scratch[0],
                            scratch[1],
                            scratch[2],
                            scratch[3]).Normalized(),
                        channel.RestRotation,
                        channel.RestWorldRotation,
                        channel.RestParentWorldRotation,
                        boneRest.Rotation,
                        boneRest.WorldRotation,
                        boneRest.ParentWorldRotation);

                    pose.Touched[boneIndex] = true;
                    pose.TouchedRotations[boneIndex] = true;
                }
            }
        }

        static DQuaternion ApplyRestRotation(
            DQuaternion animated,
            float[] sourceRestLocal,
            float[] sourceRestWorld,
            float[] sourceRestParentWorld,
            DQuaternion destinationRestLocal,
            DQuaternion destinationRestWorld,
            DQuaternion destinationRestParentWorld)
        {
            if (sourceRestWorld != null && sourceRestWorld.Length >= 4 &&
                sourceRestParentWorld != null && sourceRestParentWorld.Length >= 4)
            {
                DQuaternion sourceWorld = ToQuaternion(sourceRestWorld);
                DQuaternion sourceParentWorld = ToQuaternion(sourceRestParentWorld);

                return (destinationRestParentWorld.Inverse() *
                        sourceParentWorld *
                        animated *
                        sourceWorld.Inverse() *
                        destinationRestWorld).Normalized();
            }

            if (sourceRestLocal == null || sourceRestLocal.Length < 4)
                return animated;

            return (destinationRestLocal * ToQuaternion(sourceRestLocal).Inverse() * animated).Normalized();
        }

        static DQuaternion ToQuaternion(float[] value)
        {
            return new DQuaternion(value[0], value[1], value[2], value[3]).Normalized();
        }

        static Double3 ApplyRestTranslation(
            Double3 animated,
            float[] sourceRest,
            float[] sourceRestParentWorld,
            Double3 destinationRest,
            DQuaternion destinationRestParentWorld)
        {
            if (sourceRest == null || sourceRest.Length < 3)
                return animated;

            Double3 source = new Double3(sourceRest[0], sourceRest[1], sourceRest[2]);
            Double3 delta = animated - source;

            if (sourceRestParentWorld != null && sourceRestParentWorld.Length >= 4)
            {
                DQuaternion parentDelta =
                    destinationRestParentWorld.Inverse() * ToQuaternion(sourceRestParentWorld);

                delta = parentDelta.Rotate(delta);
            }

            return destinationRest + delta;
        }

        static Double3 ApplyRestScale(Double3 animated, float[] sourceRest, Double3 destinationRest)
        {
            if (sourceRest == null || sourceRest.Length < 3)
                return animated;

            return new Double3(
                destinationRest.X * (Math.Abs(sourceRest[0]) > 1e-9 ? animated.X / sourceRest[0] : 1.0),
                destinationRest.Y * (Math.Abs(sourceRest[1]) > 1e-9 ? animated.Y / sourceRest[1] : 1.0),
                destinationRest.Z * (Math.Abs(sourceRest[2]) > 1e-9 ? animated.Z / sourceRest[2] : 1.0));
        }

        static int ResolveValueCount(string valueType)
        {
            switch (valueType)
            {
                case "float":
                    return 1;

                case "bool":
                    return 1;

                case "vector2":
                    return 2;

                case "vector3":
                    return 3;

                case "quaternion":
                    return 4;

                case "vector4":
                    return 4;

                default:
                    return 0;
            }
        }

        static void ResolveChannelValue(
            AnimationChannelRuntime channelRuntime,
            double time,
            bool stepped,
            Span<float> output)
        {
            AnimationChannel channel = channelRuntime.Channel;
            List<AnimationKeyframe> keys = channel.Keys;

            output.Clear();

            if (keys.Count == 0)
                return;

            if (stepped)
            {
                int[] steppedIndices = channelRuntime.SteppedKeyIndices;

                if (steppedIndices.Length == 0)
                {
                    CopyValue(keys[0], output);
                    return;
                }

                int hit = steppedIndices[0];

                for (int i = 0; i < steppedIndices.Length; i++)
                {
                    if (keys[steppedIndices[i]].Time <= time)
                    {
                        hit = steppedIndices[i];
                        continue;
                    }

                    break;
                }

                CopyValue(keys[hit], output);
                return;
            }

            if (time <= keys[0].Time)
            {
                CopyValue(keys[0], output);
                return;
            }

            int lastIndex = keys.Count - 1;

            if (time >= keys[lastIndex].Time)
            {
                CopyValue(keys[lastIndex], output);
                return;
            }

            int lower = 0;
            int upper = lastIndex;

            while (upper - lower > 1)
            {
                int middle = (lower + upper) / 2;

                if (keys[middle].Time <= time)
                    lower = middle;
                else
                    upper = middle;
            }

            AnimationKeyframe from = keys[lower];
            AnimationKeyframe to = keys[upper];

            double span = to.Time - from.Time;

            if (span <= 0.0)
            {
                CopyValue(to, output);
                return;
            }

            double ratio = (time - from.Time) / span;

            if (string.Equals(channel.Interpolation, "step", StringComparison.OrdinalIgnoreCase))
            {
                CopyValue(from, output);
                return;
            }

            if (string.Equals(channel.Interpolation, "cubic", StringComparison.OrdinalIgnoreCase))
            {
                CubicHermite(from, to, span, ratio, output);
                return;
            }

            Linear(from.Value, to.Value, (float)ratio, output);

            if (string.Equals(channel.ValueType, "quaternion", StringComparison.OrdinalIgnoreCase))
                NormalizeQuaternion(output);
        }

        static void CopyValue(AnimationKeyframe keyframe, Span<float> output)
        {
            for (int i = 0; i < output.Length; i++)
                output[i] = i < keyframe.Value.Length ? keyframe.Value[i] : 0f;

            if (output.Length == 4)
                NormalizeQuaternion(output);
        }

        static void Linear(float[] from, float[] to, float ratio, Span<float> output)
        {
            for (int i = 0; i < output.Length; i++)
            {
                float a = i < from.Length ? from[i] : 0f;
                float b = i < to.Length ? to[i] : 0f;
                output[i] = a + (b - a) * ratio;
            }
        }

        static void CubicHermite(
            AnimationKeyframe from,
            AnimationKeyframe to,
            double span,
            double ratio,
            Span<float> output)
        {
            float t = (float)ratio;
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            float dt = (float)span;

            float[] fromValue = from.Value;
            float[] toValue = to.Value;
            float[] fromTangent = from.OutTangent;
            float[] toTangent = to.InTangent;

            for (int i = 0; i < output.Length; i++)
            {
                float p0 = i < fromValue.Length ? fromValue[i] : 0f;
                float p1 = i < toValue.Length ? toValue[i] : 0f;
                float m0 = i < fromTangent.Length ? fromTangent[i] : 0f;
                float m1 = i < toTangent.Length ? toTangent[i] : 0f;

                output[i] = h00 * p0 + h10 * dt * m0 + h01 * p1 + h11 * dt * m1;
            }

            if (output.Length == 4)
                NormalizeQuaternion(output);
        }

        static void NormalizeQuaternion(Span<float> output)
        {
            float x = output[0];
            float y = output[1];
            float z = output[2];
            float w = output[3];

            float length = MathF.Sqrt(x * x + y * y + z * z + w * w);

            if (length <= 0.000001f)
            {
                output[0] = 0f;
                output[1] = 0f;
                output[2] = 0f;
                output[3] = 1f;
                return;
            }

            output[0] = x / length;
            output[1] = y / length;
            output[2] = z / length;
            output[3] = w / length;
        }
    }

    internal static class AnimationClipRegistry
    {
        private static readonly ConcurrentDictionary<string, AnimationClipRuntime> _clips =
            new(StringComparer.OrdinalIgnoreCase);

        public static void Register(string key, AnimationClipData data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null)
                return;

            _clips[key] = AnimationClipRuntime.Create(key, data);
            Console.WriteLine($"[i] Registered animation clip: {key} ({data.Channels.Count} channels)");
        }

        public static bool TryGet(string key, out AnimationClipRuntime clip)
        {
            clip = null!;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            return _clips.TryGetValue(key, out clip!);
        }

        public static IEnumerable<AnimationClipRuntime> GetAll()
        {
            return _clips.Values;
        }

        public static void Clear()
        {
            _clips.Clear();
            Console.WriteLine("[i] Cleared all animation clips.");
        }
    }
}
