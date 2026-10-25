using System;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    internal sealed class AnimationMixerEntry
    {
        public AnimationClipRuntime Clip { get; init; } = null!;

        public double[] Position { get; init; } = Array.Empty<double>();

        public float[] Weights { get; init; } = Array.Empty<float>();
    }

    internal static class AnimationMixer
    {
        public static void ComputeInverseDistanceWeights(
            IReadOnlyList<double> parameters,
            IReadOnlyList<AnimationMixerEntry> entries,
            float falloff,
            Span<float> weights)
        {
            int entryCount = entries.Count;
            int dimensionCount = parameters.Count;

            if (weights.Length < entryCount)
                return;

            float exponent = falloff > 0f ? falloff : 2f;

            float total = 0f;
            int exactIndex = -1;

            for (int i = 0; i < entryCount; i++)
            {
                AnimationMixerEntry entry = entries[i];

                double sum = 0.0;

                for (int d = 0; d < dimensionCount; d++)
                {
                    double entryValue = d < entry.Position.Length ? entry.Position[d] : 0.0;
                    double delta = parameters[d] - entryValue;
                    sum += delta * delta;
                }

                double distance = Math.Sqrt(sum);

                if (distance <= 0.0000001)
                {
                    exactIndex = i;
                    break;
                }

                float weight = (float)(1.0 / Math.Pow(distance, exponent));

                weights[i] = weight;
                total += weight;
            }

            if (exactIndex >= 0)
            {
                for (int i = 0; i < entryCount; i++)
                    weights[i] = i == exactIndex ? 1f : 0f;

                return;
            }

            if (total <= 0f)
            {
                for (int i = 0; i < entryCount; i++)
                    weights[i] = i == 0 ? 1f : 0f;

                return;
            }

            for (int i = 0; i < entryCount; i++)
                weights[i] /= total;
        }

        public static void ComputeDirectWeights(
            IReadOnlyList<AnimationMixerEntry> entries,
            IReadOnlyList<float> parameters,
            Span<float> weights)
        {
            int entryCount = entries.Count;

            if (weights.Length < entryCount)
                return;

            float total = 0f;

            for (int i = 0; i < entryCount; i++)
            {
                AnimationMixerEntry entry = entries[i];
                float weight = i < parameters.Count ? parameters[i] : 0f;

                if (entry.Weights.Length > 0)
                    weight *= entry.Weights[0];

                if (weight < 0f)
                    weight = 0f;

                weights[i] = weight;
                total += weight;
            }

            if (total <= 0f)
            {
                for (int i = 0; i < entryCount; i++)
                    weights[i] = i == 0 ? 1f : 0f;

                return;
            }

            for (int i = 0; i < entryCount; i++)
                weights[i] /= total;
        }

        public static void Blend(
            IReadOnlyList<AnimationMixerEntry> entries,
            IReadOnlyList<float> entryWeights,
            double time,
            bool stepped,
            AnimationPoseBuffer result,
            Func<AnimationChannel, int> resolveBoneIndex)
        {
            if (result == null)
                return;

            result.ResetToRest();

            int entryCount = entries.Count;

            if (entryCount == 0)
                return;

            float accumulated = 0f;

            for (int i = 0; i < entryCount; i++)
            {
                float weight = i < entryWeights.Count ? entryWeights[i] : 0f;

                if (weight <= 0f)
                    continue;

                AnimationMixerEntry entry = entries[i];

                if (entry.Clip == null)
                    continue;

                AnimationPoseBuffer source = AnimationPoseBuffer.Create(result.BoneCount);
                entry.Clip.Sample(time, stepped, source, resolveBoneIndex);

                accumulated += weight;

                AccumulatePose(result, source, accumulated > 0f ? weight / accumulated : 1f);
            }
        }

        public static void AccumulatePose(AnimationPoseBuffer target, AnimationPoseBuffer source, float weight)
        {
            if (target == null || source == null)
                return;

            if (weight <= 0f)
                return;

            int count = Math.Min(target.BoneCount, source.BoneCount);

            for (int i = 0; i < count; i++)
            {
                if (!source.Touched[i])
                    continue;

                Double3 targetTranslation = target.Translations[i];
                Double3 sourceTranslation = source.Translations[i];

                target.Translations[i] = targetTranslation + (sourceTranslation - targetTranslation) * (double)weight;

                target.Rotations[i] = DQuaternion.Slerp(target.Rotations[i], source.Rotations[i], weight);

                Double3 targetScale = target.Scales[i];
                Double3 sourceScale = source.Scales[i];

                target.Scales[i] = targetScale + (sourceScale - targetScale) * (double)weight;

                target.Touched[i] = true;
            }

            if (source.HasObjectTransform)
            {
                Double3 targetPosition = target.ObjectPosition;
                target.ObjectPosition = targetPosition + (source.ObjectPosition - targetPosition) * (double)weight;

                target.ObjectRotation = DQuaternion.Slerp(target.ObjectRotation, source.ObjectRotation, weight);

                Double3 targetScale = target.ObjectScale;
                target.ObjectScale = targetScale + (source.ObjectScale - targetScale) * (double)weight;

                target.HasObjectTransform = true;
            }

            foreach (KeyValuePair<string, float> pair in source.ExpressionWeights)
            {
                float current = target.ExpressionWeights.TryGetValue(pair.Key, out float existing) ? existing : 0f;
                target.ExpressionWeights[pair.Key] = current + (pair.Value - current) * weight;
            }
        }
    }
}
