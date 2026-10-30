using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace LimitlessSquareEngine.Engine
{
    public sealed class AnimatorParameterData
    {
        public string Name { get; set; } = "";

        public string Type { get; set; } = "float";

        public double Default { get; set; }
    }

    public sealed class AnimatorConditionData
    {
        public string Parameter { get; set; } = "";

        public string Mode { get; set; } = "if";

        public double Threshold { get; set; }
    }

    public sealed class AnimatorTransitionData
    {
        public string From { get; set; } = "";

        public string To { get; set; } = "";

        public bool HasExitTime { get; set; }

        public double ExitTime { get; set; }

        public bool FixedDuration { get; set; } = true;

        public double Duration { get; set; } = 0.1;

        public double Offset { get; set; }

        public string InterruptionSource { get; set; } = "none";

        public bool OrderedInterruption { get; set; } = true;

        public bool CanTransitionToSelf { get; set; } = true;

        public List<AnimatorConditionData> Conditions { get; set; } = new();
    }

    public sealed class AnimatorBlendEntryData
    {
        public string Clip { get; set; } = "";

        public double[] Position { get; set; } = Array.Empty<double>();

        public float[] Weights { get; set; } = Array.Empty<float>();
    }

    public sealed class AnimatorMotionData
    {
        public string Type { get; set; } = "clip";

        public string Clip { get; set; } = "";

        public string Mode { get; set; } = "inverseDistance";

        public List<string> Parameters { get; set; } = new();

        public float Falloff { get; set; } = 2f;

        public List<AnimatorBlendEntryData> Entries { get; set; } = new();
    }

    public sealed class AnimatorStateData
    {
        public string Name { get; set; } = "";

        public double Speed { get; set; } = 1.0;

        public bool Loop { get; set; } = true;

        public string PlayMode { get; set; } = "";

        public AnimatorMotionData Motion { get; set; } = new();
    }

    public sealed class AnimatorLayerData
    {
        public string Name { get; set; } = "";

        public double Weight { get; set; } = 1.0;

        public string BlendMode { get; set; } = "override";

        public List<string> Mask { get; set; } = new();

        public string DefaultState { get; set; } = "";

        public List<AnimatorStateData> States { get; set; } = new();

        public List<AnimatorTransitionData> Transitions { get; set; } = new();

        public List<AnimatorTransitionData> AnyStateTransitions { get; set; } = new();
    }

    public sealed class AnimatorControllerData
    {
        public string Name { get; set; } = "";

        public bool ApplyRootMotion { get; set; } = true;

        public List<AnimatorParameterData> Parameters { get; set; } = new();

        public List<AnimatorLayerData> Layers { get; set; } = new();
    }

    internal sealed class AnimatorParameterRuntime
    {
        public string Name { get; init; } = "";

        public string Type { get; init; } = "float";

        public float FloatValue { get; set; }

        public int IntValue { get; set; }

        public bool BoolValue { get; set; }

        public bool TriggerValue { get; set; }
    }

    internal sealed class AnimatorStatePlayback
    {
        public AnimatorStateData State { get; set; } = new();

        public double Time { get; set; }

        public List<AnimationMixerEntry> Entries { get; } = new();

        public float[] Weights { get; set; } = Array.Empty<float>();

        public AnimationPoseBuffer Pose { get; set; } = null!;

        public bool EntriesBuilt { get; set; }

        public void BuildEntries()
        {
            Entries.Clear();

            AnimatorMotionData motion = State.Motion;

            if (string.Equals(motion.Type, "blend", StringComparison.OrdinalIgnoreCase))
            {
                foreach (AnimatorBlendEntryData entryData in motion.Entries)
                {
                    if (!TryResolveClip(entryData.Clip, out AnimationClipRuntime clip))
                    {
                        Console.WriteLine($"[!] Animator blend entry clip '{entryData.Clip}' is not registered.");
                        continue;
                    }

                    Entries.Add(new AnimationMixerEntry
                    {
                        Clip = clip,
                        Position = entryData.Position,
                        Weights = entryData.Weights
                    });
                }
            }
            else
            {
                if (TryResolveClip(motion.Clip, out AnimationClipRuntime clip))
                {
                    Entries.Add(new AnimationMixerEntry
                    {
                        Clip = clip,
                        Position = Array.Empty<double>(),
                        Weights = Array.Empty<float>()
                    });
                }
                else
                {
                    Console.WriteLine($"[!] Animator state '{State.Name}' clip '{motion.Clip}' is not registered.");
                }
            }

            Weights = new float[Entries.Count];
            EntriesBuilt = true;
        }

        private static bool TryResolveClip(string key, out AnimationClipRuntime clip)
        {
            if (AnimationClipRegistry.TryGet(key, out clip))
                return true;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (!key.EndsWith(".vrma", StringComparison.OrdinalIgnoreCase))
                return false;

            Program.EnsureVrmaRegistered(key);

            return AnimationClipRegistry.TryGet(key, out clip);
        }

        public bool ResolveStepped()
        {
            if (!string.IsNullOrWhiteSpace(State.PlayMode))
                return string.Equals(State.PlayMode, "stepped", StringComparison.OrdinalIgnoreCase);

            if (Entries.Count == 0)
                return false;

            return Entries[0].Clip != null &&
                   string.Equals(Entries[0].Clip.Data.PlayMode, "stepped", StringComparison.OrdinalIgnoreCase);
        }

        public double ResolveDuration()
        {
            if (Entries.Count == 0)
                return 0.0;

            double duration = 0.0;

            foreach (AnimationMixerEntry entry in Entries)
            {
                if (entry.Clip != null && entry.Clip.Duration > duration)
                    duration = entry.Clip.Duration;
            }

            return duration;
        }

        public void Evaluate(AnimatorInstance owner)
        {
            if (!EntriesBuilt)
                BuildEntries();

            if (Entries.Count == 0 || Pose == null)
                return;

            AnimatorMotionData motion = State.Motion;

            if (string.Equals(motion.Type, "blend", StringComparison.OrdinalIgnoreCase))
            {
                int dimensionCount = motion.Parameters.Count;
                double[] parameters = new double[dimensionCount];

                for (int i = 0; i < dimensionCount; i++)
                {
                    AnimatorParameterRuntime parameter = owner.GetParameter(motion.Parameters[i]);
                    parameters[i] = parameter != null ? parameter.FloatValue : 0.0;
                }

                if (string.Equals(motion.Mode, "direct", StringComparison.OrdinalIgnoreCase))
                {
                    float[] direct = new float[dimensionCount];

                    for (int i = 0; i < dimensionCount; i++)
                        direct[i] = (float)parameters[i];

                    AnimationMixer.ComputeDirectWeights(Entries, direct, Weights);
                }
                else
                {
                    AnimationMixer.ComputeInverseDistanceWeights(parameters, Entries, motion.Falloff, Weights);
                }
            }
            else
            {
                for (int i = 0; i < Weights.Length; i++)
                    Weights[i] = 1f;
            }

            AnimationMixer.Blend(Entries, Weights, Time, ResolveStepped(), Pose, owner.ResolveBoneIndex, owner.ResolveBoneRest);
        }
    }

    internal sealed class AnimatorLayerRuntime
    {
        public AnimatorLayerData Data { get; init; } = null!;

        public Dictionary<string, AnimatorStateData> States { get; init; } = new(StringComparer.Ordinal);

        public HashSet<string> Mask { get; init; } = new(StringComparer.Ordinal);

        public AnimationPoseBuffer Pose { get; set; } = null!;

        public AnimatorStatePlayback? Current { get; set; }

        public AnimatorStatePlayback? Blending { get; set; }

        public AnimatorTransitionData? ActiveTransition { get; set; }

        public double TransitionElapsed { get; set; }

        public double TransitionDuration { get; set; }

        public double TransitionOffset { get; set; }
    }

    internal sealed class AnimatorInstance
    {
        public string SceneId { get; init; } = "";

        public string ObjectId { get; init; } = "";

        public AnimatorControllerData Data { get; init; } = new();

        public Dictionary<string, AnimatorParameterRuntime> Parameters { get; } = new(StringComparer.Ordinal);

        public List<AnimatorLayerRuntime> Layers { get; } = new();

        public Dictionary<string, int> BoneNameToIndex { get; } = new(StringComparer.Ordinal);

        public AnimationPoseBuffer Pose { get; set; } = AnimationPoseBuffer.Create(0);

        public HumanoidRig? Rig { get; set; }

        public Avatar? Avatar { get; set; }

        public bool Paused { get; set; }

        public int RootBoneIndex { get; set; } = -1;

        private bool _hasRootSample;

        private Double3 _lastRootPosition;

        private double _lastRootMotionTime;

        private bool _bindingsResolved;

        public static AnimatorInstance Create(string sceneId, string objectId, AnimatorControllerData data)
        {
            var instance = new AnimatorInstance
            {
                SceneId = sceneId,
                ObjectId = objectId,
                Data = data
            };

            foreach (AnimatorParameterData parameterData in data.Parameters)
            {
                if (parameterData == null || string.IsNullOrWhiteSpace(parameterData.Name))
                    continue;

                var runtime = new AnimatorParameterRuntime
                {
                    Name = parameterData.Name,
                    Type = string.IsNullOrWhiteSpace(parameterData.Type) ? "float" : parameterData.Type
                };

                if (string.Equals(runtime.Type, "int", StringComparison.OrdinalIgnoreCase))
                    runtime.IntValue = (int)parameterData.Default;
                else if (string.Equals(runtime.Type, "bool", StringComparison.OrdinalIgnoreCase))
                    runtime.BoolValue = Math.Abs(parameterData.Default) > 0.000001;
                else if (string.Equals(runtime.Type, "trigger", StringComparison.OrdinalIgnoreCase))
                    runtime.TriggerValue = false;
                else
                    runtime.FloatValue = (float)parameterData.Default;

                instance.Parameters[runtime.Name] = runtime;
            }

            foreach (AnimatorLayerData layerData in data.Layers)
            {
                if (layerData == null)
                    continue;

                var layer = new AnimatorLayerRuntime
                {
                    Data = layerData
                };

                foreach (AnimatorStateData state in layerData.States)
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Name))
                        continue;

                    layer.States[state.Name] = state;
                }

                foreach (string maskName in layerData.Mask)
                {
                    if (!string.IsNullOrWhiteSpace(maskName))
                        layer.Mask.Add(maskName);
                }

                AnimatorStateData? defaultState;

                if (!string.IsNullOrWhiteSpace(layerData.DefaultState))
                    layer.States.TryGetValue(layerData.DefaultState, out defaultState);
                else
                    defaultState = layerData.States.Count > 0 ? layerData.States[0] : null;

                if (defaultState == null)
                {
                    Console.WriteLine($"[!] Animator layer '{layerData.Name}' has no usable default state.");
                    continue;
                }

                layer.Current = new AnimatorStatePlayback
                {
                    State = defaultState
                };

                instance.Layers.Add(layer);
            }

            instance.EnsureBindings();
            return instance;
        }

        public AnimatorParameterRuntime? GetParameter(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            return Parameters.TryGetValue(name, out AnimatorParameterRuntime parameter) ? parameter : null;
        }

        public void EnsureBindings()
        {
            if (_bindingsResolved)
                return;

            SceneObject? source = Scene.GetSceneObject(SceneId, ObjectId);

            if (source == null)
                return;

            if (string.Equals(source.Type, "Avatar", StringComparison.Ordinal))
            {
                string avatarId = Scene.ParseAvatarId(source);

                if (!AvatarRegistry.TryGet(avatarId, out Avatar? avatar) || avatar == null)
                    return;

                Avatar = avatar;
                Rig = avatar.Rig;

                BoneNameToIndex.Clear();

                for (int i = 0; i < avatar.Skeleton.Bones.Count; i++)
                    BoneNameToIndex[avatar.Skeleton.Bones[i].Name] = i;
            }

            _bindingsResolved = true;

            int boneCount = Avatar != null ? Avatar.Skeleton.Bones.Count : 0;

            RootBoneIndex = -1;

            int rootBoneIndex = -1;

            if (Avatar != null && Avatar.Humanoid.TryGetBoneIndex("hips", out rootBoneIndex))
                RootBoneIndex = rootBoneIndex;

            Pose = AnimationPoseBuffer.Create(boneCount);

            foreach (AnimatorLayerRuntime layer in Layers)
            {
                layer.Pose = AnimationPoseBuffer.Create(boneCount);

                if (layer.Current != null)
                    layer.Current.Pose = AnimationPoseBuffer.Create(boneCount);

                if (layer.Blending != null)
                    layer.Blending.Pose = AnimationPoseBuffer.Create(boneCount);
            }
        }

        public int ResolveBoneIndex(AnimationChannel channel)
        {
            if (channel == null)
                return -1;

            if (string.Equals(channel.Binding, "humanoid", StringComparison.OrdinalIgnoreCase))
            {
                if (Rig == null)
                    return -1;

                if (!Rig.Bones.TryGetValue(channel.Path, out HumanoidBoneBinding? binding))
                    return -1;

                return binding.BoneIndex;
            }

            if (BoneNameToIndex.TryGetValue(channel.Path, out int index))
                return index;

            return -1;
        }

        public AnimationBoneRest ResolveBoneRest(int boneIndex)
        {
            if (Avatar == null || boneIndex < 0 || boneIndex >= Avatar.Skeleton.Bones.Count)
                return AnimationBoneRest.Identity;

            AvatarBone bone = Avatar.Skeleton.Bones[boneIndex];

            DQuaternion worldRotation = DQuaternion.Identity;

            if (bone.RestWorldRotation.HasValue)
            {
                worldRotation = bone.RestWorldRotation.Value;
            }
            else
            {
                AvatarBone? cursor = bone;

                while (cursor != null)
                {
                    worldRotation = cursor.RestRotation * worldRotation;

                    if (cursor.ParentIndex < 0 || cursor.ParentIndex >= Avatar.Skeleton.Bones.Count)
                        break;

                    cursor = Avatar.Skeleton.Bones[cursor.ParentIndex];
                }
            }

            DQuaternion parentWorldRotation = DQuaternion.Identity;

            if (bone.ParentIndex >= 0 && bone.ParentIndex < Avatar.Skeleton.Bones.Count)
            {
                AvatarBone parent = Avatar.Skeleton.Bones[bone.ParentIndex];

                if (parent.RestWorldRotation.HasValue)
                {
                    parentWorldRotation = parent.RestWorldRotation.Value;
                }
                else
                {
                    AvatarBone? cursor = parent;

                    while (cursor != null)
                    {
                        parentWorldRotation = cursor.RestRotation * parentWorldRotation;

                        if (cursor.ParentIndex < 0 || cursor.ParentIndex >= Avatar.Skeleton.Bones.Count)
                            break;

                        cursor = Avatar.Skeleton.Bones[cursor.ParentIndex];
                    }
                }
            }

            return new AnimationBoneRest(
                bone.RestPosition,
                bone.RestRotation,
                bone.RestScale,
                worldRotation,
                parentWorldRotation);
        }

        public void Tick(double deltaTime)
        {
            if (Paused)
                return;

            EnsureBindings();

            foreach (AnimatorLayerRuntime layer in Layers)
            {
                if (layer.Current == null)
                    continue;

                double speed = layer.Current.State.Speed > 0.0 ? layer.Current.State.Speed : 1.0;
                double advance = deltaTime * speed;

                layer.Current.Time += advance;

                if (layer.ActiveTransition != null)
                {
                    layer.TransitionElapsed += deltaTime;

                    if (layer.Blending != null)
                        layer.Blending.Time += advance;

                    if (layer.TransitionDuration <= 0.0 || layer.TransitionElapsed >= layer.TransitionDuration)
                    {
                        layer.Blending = null;
                        layer.ActiveTransition = null;
                        layer.TransitionElapsed = 0.0;
                        layer.TransitionDuration = 0.0;
                        layer.TransitionOffset = 0.0;
                    }
                }
                else
                {
                    AnimatorTransitionData? transition = FindTransition(layer);

                    if (transition != null)
                        BeginTransition(layer, transition);
                }

                layer.Current.Evaluate(this);

                if (layer.Blending != null)
                    layer.Blending.Evaluate(this);

                BlendLayerPose(layer);
            }

            Pose.ResetToRest();

            foreach (AnimatorLayerRuntime layer in Layers)
                MergeLayer(layer);

            ApplyPose(ResolveRootMotionTime());
        }

        private double ResolveRootMotionTime()
        {
            if (Layers.Count == 0 || Layers[0].Current == null)
                return 0.0;

            AnimatorStatePlayback playback = Layers[0].Current;
            double duration = playback.ResolveDuration();

            if (duration <= 0.0)
                return playback.Time;

            double wrapped = playback.Time % duration;

            return wrapped < 0.0 ? wrapped + duration : wrapped;
        }

        private void ApplyRootMotionOffset(double sampleTime)
        {
            Double3 rootPosition = Pose.Translations[RootBoneIndex];

            if (!_hasRootSample)
            {
                _lastRootPosition = rootPosition;
                _lastRootMotionTime = sampleTime;
                _hasRootSample = true;
                return;
            }

            bool looped = sampleTime < _lastRootMotionTime;

            Double3 delta = rootPosition - _lastRootPosition;

            _lastRootPosition = rootPosition;
            _lastRootMotionTime = sampleTime;

            if (looped)
                return;

            Double3 modelDelta = ResolveBoneRest(RootBoneIndex).ParentWorldRotation.Rotate(delta);

            DQuaternion avatarWorldRotation = DQuaternion.FromEulerDegrees(Scene.GetRotation(SceneId, ObjectId));

            Double3 worldDelta = avatarWorldRotation.Rotate(new Double3(modelDelta.X, modelDelta.Y, -modelDelta.Z));

            Scene.SetLocalPosition(SceneId, ObjectId, Scene.GetLocalPosition(SceneId, ObjectId) + Scene.WorldDeltaToLocalDelta(SceneId, ObjectId, worldDelta));
        }

        private AnimatorTransitionData? FindTransition(AnimatorLayerRuntime layer)
        {
            AnimatorStateData? current = layer.Current?.State;

            foreach (AnimatorTransitionData transition in layer.Data.AnyStateTransitions)
            {
                if (transition == null)
                    continue;

                if (!CanEnter(layer, transition))
                    continue;

                return transition;
            }

            if (current == null)
                return null;

            foreach (AnimatorTransitionData transition in layer.Data.Transitions)
            {
                if (transition == null)
                    continue;

                if (!string.Equals(transition.From, current.Name, StringComparison.Ordinal))
                    continue;

                if (!CanEnter(layer, transition))
                    continue;

                return transition;
            }

            return null;
        }

        private bool CanEnter(AnimatorLayerRuntime layer, AnimatorTransitionData transition)
        {
            AnimatorStatePlayback? current = layer.Current;
            AnimatorStateData? currentState = current?.State;

            bool isAnyState = string.IsNullOrWhiteSpace(transition.From);

            if (currentState != null && string.Equals(transition.To, currentState.Name, StringComparison.Ordinal))
            {
                if (isAnyState && !transition.CanTransitionToSelf)
                    return false;
            }

            if (!layer.States.ContainsKey(transition.To))
                return false;

            if (transition.HasExitTime && current != null)
            {
                double duration = current.ResolveDuration();

                if (duration > 0.0)
                {
                    double normalized = current.Time / duration;

                    if (normalized < transition.ExitTime)
                        return false;
                }
            }

            foreach (AnimatorConditionData condition in transition.Conditions)
            {
                if (!EvaluateCondition(condition))
                    return false;
            }

            return true;
        }

        private bool EvaluateCondition(AnimatorConditionData condition)
        {
            if (condition == null || string.IsNullOrWhiteSpace(condition.Parameter))
                return true;

            AnimatorParameterRuntime? parameter = GetParameter(condition.Parameter);

            if (parameter == null)
                return false;

            string mode = condition.Mode;

            if (string.Equals(mode, "if", StringComparison.OrdinalIgnoreCase))
                return parameter.BoolValue || parameter.TriggerValue;

            if (string.Equals(mode, "ifnot", StringComparison.OrdinalIgnoreCase))
                return !parameter.BoolValue && !parameter.TriggerValue;

            float value = parameter.FloatValue;

            if (string.Equals(parameter.Type, "int", StringComparison.OrdinalIgnoreCase))
                value = parameter.IntValue;
            else if (string.Equals(parameter.Type, "bool", StringComparison.OrdinalIgnoreCase))
                value = parameter.BoolValue ? 1f : 0f;
            else if (string.Equals(parameter.Type, "trigger", StringComparison.OrdinalIgnoreCase))
                value = parameter.TriggerValue ? 1f : 0f;

            float threshold = (float)condition.Threshold;

            if (string.Equals(mode, "greater", StringComparison.OrdinalIgnoreCase))
                return value > threshold;

            if (string.Equals(mode, "less", StringComparison.OrdinalIgnoreCase))
                return value <= threshold;

            if (string.Equals(mode, "equals", StringComparison.OrdinalIgnoreCase))
                return Math.Abs(value - threshold) <= 0.0001f;

            if (string.Equals(mode, "notequal", StringComparison.OrdinalIgnoreCase))
                return Math.Abs(value - threshold) > 0.0001f;

            return false;
        }

        private void BeginTransition(AnimatorLayerRuntime layer, AnimatorTransitionData transition)
        {
            if (layer.Current == null)
                return;

            if (!layer.States.TryGetValue(transition.To, out AnimatorStateData? target))
                return;

            layer.Blending = layer.Current;

            var next = new AnimatorStatePlayback
            {
                State = target,
                Time = -transition.Offset,
                Pose = AnimationPoseBuffer.Create(Pose.BoneCount)
            };

            layer.Current = next;

            layer.ActiveTransition = transition;
            layer.TransitionElapsed = 0.0;
            layer.TransitionOffset = transition.Offset;

            if (transition.FixedDuration)
            {
                layer.TransitionDuration = transition.Duration;
            }
            else
            {
                double sourceDuration = layer.Blending.ResolveDuration();
                layer.TransitionDuration = sourceDuration > 0.0
                    ? sourceDuration * transition.Duration
                    : 0.0;
            }

            foreach (AnimatorConditionData condition in transition.Conditions)
            {
                AnimatorParameterRuntime? parameter = GetParameter(condition.Parameter);

                if (parameter == null)
                    continue;

                if (string.Equals(parameter.Type, "trigger", StringComparison.OrdinalIgnoreCase))
                    parameter.TriggerValue = false;
            }
        }

        private void BlendLayerPose(AnimatorLayerRuntime layer)
        {
            if (layer.Pose == null)
                return;

            if (layer.Blending == null || layer.Blending.Pose == null)
            {
                layer.Pose.CopyFrom(layer.Current?.Pose);
                return;
            }

            if (layer.TransitionDuration <= 0.0)
            {
                layer.Pose.CopyFrom(layer.Current?.Pose);
                return;
            }

            float ratio = (float)Math.Clamp(layer.TransitionElapsed / layer.TransitionDuration, 0.0, 1.0);

            layer.Pose.CopyFrom(layer.Blending.Pose);
            AnimationMixer.AccumulatePose(layer.Pose, layer.Current?.Pose, ratio);
        }

        private void MergeLayer(AnimatorLayerRuntime layer)
        {
            if (layer.Pose == null)
                return;

            float weight = (float)Math.Clamp(layer.Data.Weight, 0.0, 1.0);

            if (weight <= 0f)
                return;

            bool additive =
                string.Equals(layer.Data.BlendMode, "additive", StringComparison.OrdinalIgnoreCase);

            int count = Math.Min(Pose.BoneCount, layer.Pose.BoneCount);

            for (int i = 0; i < count; i++)
            {
                if (!layer.Pose.Touched[i])
                    continue;

                if (layer.Mask.Count > 0 && !IsMasked(layer, i))
                    continue;

                if (additive)
                {
                    if (layer.Pose.TouchedTranslations[i])
                    {
                        Pose.Translations[i] += layer.Pose.Translations[i] * (double)weight;
                        Pose.TouchedTranslations[i] = true;
                    }

                    if (layer.Pose.TouchedRotations[i])
                    {
                        DQuaternion delta = DQuaternion.Slerp(DQuaternion.Identity, layer.Pose.Rotations[i], weight);
                        Pose.Rotations[i] = (Pose.Rotations[i] * delta).Normalized();
                        Pose.TouchedRotations[i] = true;
                    }

                    if (layer.Pose.TouchedScales[i])
                    {
                        Pose.Scales[i] += (layer.Pose.Scales[i] - Double3.One) * (double)weight;
                        Pose.TouchedScales[i] = true;
                    }
                }
                else
                {
                    if (layer.Pose.TouchedTranslations[i])
                    {
                        Pose.Translations[i] = Pose.Translations[i] +
                                               (layer.Pose.Translations[i] - Pose.Translations[i]) * (double)weight;
                        Pose.TouchedTranslations[i] = true;
                    }

                    if (layer.Pose.TouchedRotations[i])
                    {
                        Pose.Rotations[i] = DQuaternion.Slerp(Pose.Rotations[i], layer.Pose.Rotations[i], weight);
                        Pose.TouchedRotations[i] = true;
                    }

                    if (layer.Pose.TouchedScales[i])
                    {
                        Pose.Scales[i] = Pose.Scales[i] +
                                         (layer.Pose.Scales[i] - Pose.Scales[i]) * (double)weight;
                        Pose.TouchedScales[i] = true;
                    }
                }

                Pose.Touched[i] = true;
            }

            if (layer.Pose.HasObjectTransform)
            {
                float objectWeight = weight;

                if (additive)
                {
                    Pose.ObjectPosition += layer.Pose.ObjectPosition * (double)objectWeight;
                    Pose.ObjectScale += (layer.Pose.ObjectScale - Double3.One) * (double)objectWeight;
                }
                else if (!Pose.HasObjectTransform)
                {
                    Pose.ObjectPosition = layer.Pose.ObjectPosition;
                    Pose.ObjectRotation = layer.Pose.ObjectRotation;
                    Pose.ObjectScale = layer.Pose.ObjectScale;
                }
                else
                {
                    Pose.ObjectPosition = Pose.ObjectPosition +
                                          (layer.Pose.ObjectPosition - Pose.ObjectPosition) * (double)objectWeight;

                    Pose.ObjectRotation = DQuaternion.Slerp(Pose.ObjectRotation, layer.Pose.ObjectRotation, objectWeight);

                    Pose.ObjectScale = Pose.ObjectScale +
                                       (layer.Pose.ObjectScale - Pose.ObjectScale) * (double)objectWeight;
                }

                Pose.HasObjectTransform = true;
            }

            foreach (KeyValuePair<string, float> pair in layer.Pose.ExpressionWeights)
            {
                float current = Pose.ExpressionWeights.TryGetValue(pair.Key, out float existing) ? existing : 0f;

                if (additive)
                    Pose.ExpressionWeights[pair.Key] = Math.Clamp(current + pair.Value * weight, 0f, 1f);
                else
                    Pose.ExpressionWeights[pair.Key] = current + (pair.Value - current) * weight;
            }
        }

        private bool IsMasked(AnimatorLayerRuntime layer, int boneIndex)
        {
            if (Avatar == null)
                return true;

            if (boneIndex < 0 || boneIndex >= Avatar.Skeleton.Bones.Count)
                return false;

            string boneName = Avatar.Skeleton.Bones[boneIndex].Name;

            if (layer.Mask.Contains(boneName))
                return true;

            if (Rig == null)
                return false;

            foreach (KeyValuePair<string, HumanoidBoneBinding> pair in Rig.Bones)
            {
                if (pair.Value.BoneIndex != boneIndex)
                    continue;

                if (layer.Mask.Contains(pair.Key))
                    return true;
            }

            int parentIndex = Avatar.Skeleton.Bones[boneIndex].ParentIndex;

            while (parentIndex >= 0 && parentIndex < Avatar.Skeleton.Bones.Count)
            {
                string parentName = Avatar.Skeleton.Bones[parentIndex].Name;

                if (layer.Mask.Contains(parentName))
                    return true;

                if (Rig != null)
                {
                    foreach (KeyValuePair<string, HumanoidBoneBinding> pair in Rig.Bones)
                    {
                        if (pair.Value.BoneIndex != parentIndex)
                            continue;

                        if (layer.Mask.Contains(pair.Key))
                            return true;
                    }
                }

                parentIndex = Avatar.Skeleton.Bones[parentIndex].ParentIndex;
            }

            return false;
        }

        private void ApplyPose(double sampleTime)
        {
            if (Avatar != null)
            {
                bool applyRootMotion =
                    Data.ApplyRootMotion &&
                    RootBoneIndex >= 0 &&
                    RootBoneIndex < Pose.BoneCount &&
                    Pose.TouchedTranslations[RootBoneIndex];

                if (applyRootMotion)
                    ApplyRootMotionOffset(sampleTime);

                for (int i = 0; i < Pose.BoneCount && i < Avatar.Skeleton.Bones.Count; i++)
                {
                    if (!Pose.Touched[i])
                        continue;

                    AvatarBone bone = Avatar.Skeleton.Bones[i];

                    if (Pose.TouchedTranslations[i])
                    {
                        if (applyRootMotion && i == RootBoneIndex)
                            bone.LocalPosition = bone.RestPosition;
                        else
                            bone.LocalPosition = Pose.Translations[i];
                    }

                    if (Pose.TouchedRotations[i])
                    {
                        bone.LocalRotationQuaternion = Pose.Rotations[i];
                        bone.LocalRotation = Pose.Rotations[i].ToEulerDegrees();
                    }

                    if (Pose.TouchedScales[i])
                        bone.LocalScale = Pose.Scales[i];
                }

                foreach (KeyValuePair<string, float> pair in Pose.ExpressionWeights)
                    Avatar.SetExpressionWeight(pair.Key, pair.Value);
            }

            if (Pose.HasObjectTransform)
            {
                Scene.ApplyAnimatedLocalTransform(
                    SceneId,
                    ObjectId,
                    Pose.ObjectPosition,
                    Pose.ObjectRotation,
                    Pose.ObjectScale);
            }
        }

        public void SetTrigger(string name)
        {
            AnimatorParameterRuntime? parameter = GetParameter(name);

            if (parameter != null)
                parameter.TriggerValue = true;
        }

        public void ResetTrigger(string name)
        {
            AnimatorParameterRuntime? parameter = GetParameter(name);

            if (parameter != null)
                parameter.TriggerValue = false;
        }
    }

    internal static class Animator
    {
        private static readonly ConcurrentDictionary<string, AnimatorControllerData> _controllers =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly ConcurrentDictionary<string, AnimatorInstance> _instances =
            new(StringComparer.Ordinal);

        private static string BuildKey(string sceneId, string objectId)
        {
            return sceneId + "::" + objectId;
        }

        public static void RegisterController(string key, AnimatorControllerData data)
        {
            if (string.IsNullOrWhiteSpace(key) || data == null)
                return;

            _controllers[key] = data;
            Console.WriteLine($"[i] Registered animator controller: {key} ({data.Layers.Count} layers)");
        }

        public static bool TryGetController(string key, out AnimatorControllerData data)
        {
            data = null!;

            if (string.IsNullOrWhiteSpace(key))
                return false;

            return _controllers.TryGetValue(key, out data!);
        }

        public static bool Attach(string sceneId, string objectId, string controllerKey)
        {
            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return false;

            if (!TryGetController(controllerKey, out AnimatorControllerData data))
            {
                Program.EnsureAllAnimationKeysDiscovered();

                if (!TryGetController(controllerKey, out data))
                {
                    Console.WriteLine($"[!] Animator controller '{controllerKey}' is not registered.");
                    return false;
                }
            }

            string key = BuildKey(sceneId, objectId);

            _instances[key] = AnimatorInstance.Create(sceneId, objectId, data);
            Console.WriteLine($"[i] Attached animator '{controllerKey}' to '{key}'");

            return true;
        }

        public static bool TryGet(string sceneId, string objectId, out AnimatorInstance instance)
        {
            instance = null!;

            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return false;

            return _instances.TryGetValue(BuildKey(sceneId, objectId), out instance!);
        }

        public static void Detach(string sceneId, string objectId)
        {
            if (string.IsNullOrWhiteSpace(sceneId) || string.IsNullOrWhiteSpace(objectId))
                return;

            _instances.TryRemove(BuildKey(sceneId, objectId), out _);
        }

        public static void RemoveScene(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                return;

            string prefix = sceneId + "::";

            foreach (string key in _instances.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    _instances.TryRemove(key, out _);
            }
        }

        public static void Clear()
        {
            _instances.Clear();
            _controllers.Clear();
            Console.WriteLine("[i] Cleared all animators.");
        }

        public static void TickAll(double deltaTime)
        {
            foreach (AnimatorInstance instance in _instances.Values)
            {
                try
                {
                    instance.Tick(deltaTime);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Animator tick failed for '{instance.SceneId}::{instance.ObjectId}': {ex.Message}");
                }
            }
        }

        public static bool SetFloat(string sceneId, string objectId, string name, float value)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            AnimatorParameterRuntime? parameter = instance.GetParameter(name);

            if (parameter == null)
                return false;

            parameter.FloatValue = value;
            return true;
        }

        public static bool SetInt(string sceneId, string objectId, string name, int value)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            AnimatorParameterRuntime? parameter = instance.GetParameter(name);

            if (parameter == null)
                return false;

            parameter.IntValue = value;
            return true;
        }

        public static bool SetBool(string sceneId, string objectId, string name, bool value)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            AnimatorParameterRuntime? parameter = instance.GetParameter(name);

            if (parameter == null)
                return false;

            parameter.BoolValue = value;
            return true;
        }

        public static bool SetTrigger(string sceneId, string objectId, string name)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            AnimatorParameterRuntime? parameter = instance.GetParameter(name);

            if (parameter == null)
                return false;

            parameter.TriggerValue = true;
            return true;
        }

        public static bool ResetTrigger(string sceneId, string objectId, string name)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            AnimatorParameterRuntime? parameter = instance.GetParameter(name);

            if (parameter == null)
                return false;

            parameter.TriggerValue = false;
            return true;
        }

        public static bool SetLayerWeight(string sceneId, string objectId, string layerName, double weight)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            foreach (AnimatorLayerRuntime layer in instance.Layers)
            {
                if (!string.Equals(layer.Data.Name, layerName, StringComparison.Ordinal))
                    continue;

                layer.Data.Weight = Math.Clamp(weight, 0.0, 1.0);
                return true;
            }

            return false;
        }

        public static bool SetStateSpeed(string sceneId, string objectId, string layerName, string stateName, double speed)
        {
            if (!TryGet(sceneId, objectId, out AnimatorInstance instance))
                return false;

            foreach (AnimatorLayerRuntime layer in instance.Layers)
            {
                if (!string.Equals(layer.Data.Name, layerName, StringComparison.Ordinal))
                    continue;

                if (!layer.States.TryGetValue(stateName, out AnimatorStateData? state))
                    return false;

                state.Speed = speed;
                return true;
            }

            return false;
        }
    }
}
