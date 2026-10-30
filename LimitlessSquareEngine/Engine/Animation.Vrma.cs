using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace LimitlessSquareEngine
{
    public static class VrmaConverter
    {
        public static bool TryConvert(string filePath, out AnimationClipData? clip, out string reason)
        {
            clip = null;
            reason = string.Empty;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                reason = "File not found.";
                return false;
            }

            VrmData data;

            try
            {
                data = VrmLoader.LoadFromFile(filePath);
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }

            if (data.Animations.Count == 0)
            {
                reason = "VRMA contains no glTF animation.";
                return false;
            }

            Dictionary<int, string> boneNamesByNode = new Dictionary<int, string>();
            Dictionary<int, string> expressionNamesByNode = new Dictionary<int, string>();

            foreach (VrmHumanoidBone humanoidBone in data.HumanoidBones)
            {
                if (HumanoidBoneSpecifications.Find(humanoidBone.BoneName) == null)
                    continue;

                boneNamesByNode[humanoidBone.NodeIndex] = humanoidBone.BoneName;
            }

            foreach (VrmAnimationExpressionData expression in data.AnimationExpressions)
                expressionNamesByNode[expression.NodeIndex] = expression.ExpressionName;

            if (boneNamesByNode.Count == 0)
            {
                reason = "'humanoid.humanBones' is missing or empty.";
                return false;
            }

            AnimationClipData result = new AnimationClipData
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                Loop = true,
                FrameRate = 30f,
                PlayMode = "normal"
            };

            VrmAnimationData animation = data.Animations[0];

            foreach (VrmAnimationChannelData channelData in animation.Channels)
            {
                if (channelData.SamplerIndex < 0 || channelData.SamplerIndex >= animation.Samplers.Count)
                    continue;

                VrmAnimationSamplerData sampler = animation.Samplers[channelData.SamplerIndex];

                if (boneNamesByNode.TryGetValue(channelData.NodeIndex, out string? boneName))
                {
                    BuildHumanoidChannel(result, data, boneName, channelData, sampler);
                    continue;
                }

                if (expressionNamesByNode.TryGetValue(channelData.NodeIndex, out string? expressionName))
                    BuildExpressionChannel(result, expressionName, channelData, sampler);
            }

            if (result.Channels.Count == 0)
            {
                reason = "VRMA has no humanoid or expression channels.";
                return false;
            }

            result.Duration = ResolveDuration(result);

            clip = result;
            return true;
        }

        private static void BuildHumanoidChannel(
            AnimationClipData clip,
            VrmData data,
            string boneName,
            VrmAnimationChannelData channelData,
            VrmAnimationSamplerData sampler)
        {
            AnimationChannel channel = new AnimationChannel
            {
                Binding = "humanoid",
                Path = boneName,
                Interpolation = ResolveInterpolation(sampler.Interpolation)
            };

            if (string.Equals(channelData.Path, "rotation", StringComparison.OrdinalIgnoreCase))
            {
                channel.Property = "rotation";
                channel.ValueType = "quaternion";
                channel.RestRotation = ReadRestRotation(data, channelData.NodeIndex);
                channel.RestWorldRotation = ReadRestWorldRotation(data, channelData.NodeIndex);
                channel.RestParentWorldRotation = ReadRestParentWorldRotation(data, channelData.NodeIndex);

                BuildKeys(channel, sampler, 4);
            }
            else if (string.Equals(channelData.Path, "translation", StringComparison.OrdinalIgnoreCase))
            {
                channel.Property = "translation";
                channel.ValueType = "vector3";
                channel.RestPosition = ReadRestPosition(data, channelData.NodeIndex);
                channel.RestParentWorldRotation = ReadRestParentWorldRotation(data, channelData.NodeIndex);

                BuildKeys(channel, sampler, 3);
            }
            else if (string.Equals(channelData.Path, "scale", StringComparison.OrdinalIgnoreCase))
            {
                channel.Property = "scale";
                channel.ValueType = "vector3";
                channel.RestScale = ReadRestScale(data, channelData.NodeIndex);

                BuildKeys(channel, sampler, 3);
            }
            else
            {
                return;
            }

            if (channel.Keys.Count > 0)
                clip.Channels.Add(channel);
        }

        private static void BuildExpressionChannel(
            AnimationClipData clip,
            string expressionName,
            VrmAnimationChannelData channelData,
            VrmAnimationSamplerData sampler)
        {
            if (!string.Equals(channelData.Path, "translation", StringComparison.OrdinalIgnoreCase))
                return;

            int inputCount = sampler.Input.Length;

            if (inputCount == 0 || sampler.Output.Length < inputCount * 3)
                return;

            AnimationChannel channel = new AnimationChannel
            {
                Binding = "expression",
                Path = expressionName,
                Property = "weight",
                ValueType = "float",
                Interpolation = ResolveInterpolation(sampler.Interpolation)
            };

            for (int i = 0; i < inputCount; i++)
            {
                channel.Keys.Add(new AnimationKeyframe
                {
                    Time = sampler.Input[i],
                    Value = new[] { sampler.Output[i * 3] }
                });
            }

            clip.Channels.Add(channel);
        }

        private static void BuildKeys(AnimationChannel channel, VrmAnimationSamplerData sampler, int componentCount)
        {
            int inputCount = sampler.Input.Length;

            if (inputCount == 0)
                return;

            bool isCubic = string.Equals(sampler.Interpolation, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase);
            int stride = isCubic ? componentCount * 3 : componentCount;

            if (sampler.Output.Length < inputCount * stride)
                return;

            for (int i = 0; i < inputCount; i++)
            {
                int offset = i * stride;

                float[] value = new float[componentCount];
                float[] inTangent = Array.Empty<float>();
                float[] outTangent = Array.Empty<float>();

                if (isCubic)
                {
                    inTangent = new float[componentCount];
                    outTangent = new float[componentCount];

                    for (int c = 0; c < componentCount; c++)
                    {
                        inTangent[c] = sampler.Output[offset + c];
                        value[c] = sampler.Output[offset + componentCount + c];
                        outTangent[c] = sampler.Output[offset + componentCount * 2 + c];
                    }
                }
                else
                {
                    for (int c = 0; c < componentCount; c++)
                        value[c] = sampler.Output[offset + c];
                }

                channel.Keys.Add(new AnimationKeyframe
                {
                    Time = sampler.Input[i],
                    Value = value,
                    InTangent = inTangent,
                    OutTangent = outTangent
                });
            }
        }

        private static double ResolveDuration(AnimationClipData clip)
        {
            double duration = 0.0;

            foreach (AnimationChannel channel in clip.Channels)
            {
                foreach (AnimationKeyframe keyframe in channel.Keys)
                {
                    if (keyframe.Time > duration)
                        duration = keyframe.Time;
                }
            }

            return duration;
        }

        private static string ResolveInterpolation(string interpolation)
        {
            if (string.Equals(interpolation, "STEP", StringComparison.OrdinalIgnoreCase))
                return "step";

            if (string.Equals(interpolation, "CUBICSPLINE", StringComparison.OrdinalIgnoreCase))
                return "cubic";

            return "linear";
        }

        private static float[] ReadRestRotation(VrmData data, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= data.Nodes.Count)
                return Array.Empty<float>();

            Quaternion rotation = data.Nodes[nodeIndex].Rotation;

            return new[] { rotation.X, rotation.Y, rotation.Z, rotation.W };
        }

        private static float[] ReadRestPosition(VrmData data, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= data.Nodes.Count)
                return Array.Empty<float>();

            Vector3 position = data.Nodes[nodeIndex].Position;

            return new[] { position.X, position.Y, position.Z };
        }

        private static float[] ReadRestScale(VrmData data, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= data.Nodes.Count)
                return Array.Empty<float>();

            Vector3 scale = data.Nodes[nodeIndex].Scale;

            return new[] { scale.X, scale.Y, scale.Z };
        }

        private static float[] ReadRestWorldRotation(VrmData data, int nodeIndex)
        {
            Quaternion world = ResolveRestWorldRotation(data, nodeIndex);

            return new[] { world.X, world.Y, world.Z, world.W };
        }

        private static float[] ReadRestParentWorldRotation(VrmData data, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= data.Nodes.Count)
                return Array.Empty<float>();

            int parentIndex = data.Nodes[nodeIndex].ParentIndex;

            if (parentIndex < 0 || parentIndex >= data.Nodes.Count)
                return new[] { 0f, 0f, 0f, 1f };

            Quaternion parentWorld = ResolveRestWorldRotation(data, parentIndex);

            return new[] { parentWorld.X, parentWorld.Y, parentWorld.Z, parentWorld.W };
        }

        private static Quaternion ResolveRestWorldRotation(VrmData data, int nodeIndex)
        {
            if (nodeIndex < 0 || nodeIndex >= data.Nodes.Count)
                return Quaternion.Identity;

            int parentIndex = data.Nodes[nodeIndex].ParentIndex;

            Quaternion parentWorld = parentIndex >= 0 && parentIndex < data.Nodes.Count
                ? ResolveRestWorldRotation(data, parentIndex)
                : Quaternion.Identity;

            return Quaternion.Normalize(parentWorld * data.Nodes[nodeIndex].Rotation);
        }
    }
}
