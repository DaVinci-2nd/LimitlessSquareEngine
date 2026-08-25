using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using LimitlessSquareEngine;

namespace LimitlessSquareEngine.Engine
{
    public class Avatar
    {
        public string Id { get; }

        public string SceneId { get; set; } = "";

        public string Name { get; set; } = "";

        public bool Active { get; set; } = true;

        public bool Visible { get; set; } = true;

        public string ModelKey { get; set; } = "";

        public Double3 LocalPosition { get; set; } = Double3.Zero;

        public Double3 LocalRotation { get; set; } = Double3.Zero;

        public Double3 LocalScale { get; set; } = Double3.One;

        public AvatarSkeleton Skeleton { get; set; } = new();

        public List<AvatarSkin> Skins { get; } = new();

        public Avatar()
        {
            Id = "";
        }

        public Avatar(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("[X] Avatar id cannot be null or empty.", nameof(id));

            Id = id;
            Name = id;
        }

        public Avatar(string id, string modelKey)
            : this(id)
        {
            ModelKey = modelKey;
        }

        internal SceneWorldState GetWorldState()
        {
            DQuaternion rotation = DQuaternion.FromEulerDegrees(LocalRotation);
            return new SceneWorldState(LocalPosition, rotation, LocalScale);
        }

        public Matrix4x4 GetModelMatrix()
        {
            SceneWorldState world = GetWorldState();

            DQuaternion flipZ = DQuaternion.CreateAxisAngle(new Double3(0.0, 0.0, 1.0), Math.PI);
            DQuaternion flipAdjusted = flipZ * world.Rotation * flipZ;
            Quaternion rotation = flipAdjusted.ToSingle();

            Matrix4x4 rotationMatrix = Matrix4x4.CreateFromQuaternion(rotation);
            Matrix4x4 scaleMatrix = Matrix4x4.CreateScale(
                (float)world.Scale.X,
                (float)world.Scale.Y,
                (float)world.Scale.Z);

            Matrix4x4 model = scaleMatrix * rotationMatrix;
            model.Translation = new Vector3(
                (float)world.Position.X,
                (float)world.Position.Y,
                (float)world.Position.Z);

            return model;
        }

        public Matrix4x4[] ComputeSkinMatrices(AvatarSkin skin, Matrix4x4[] nodeGlobalMatrices)
        {
            int jointCount = skin.JointBoneIndices.Length;
            var result = new Matrix4x4[jointCount];

            for (int i = 0; i < jointCount; i++)
                result[i] = Matrix4x4.Identity;

            for (int i = 0; i < jointCount; i++)
            {
                int boneIndex = skin.JointBoneIndices[i];

                if (boneIndex >= 0 && boneIndex < nodeGlobalMatrices.Length && i < skin.InverseBindMatrices.Length)
                    result[i] = skin.InverseBindMatrices[i] * nodeGlobalMatrices[boneIndex];
            }

            return result;
        }

        public float[] ComputeMorphUpdatedVertices(AvatarSkin skin, float[] morphWeights)
        {
            int stride = skin.VertexStrideFloats;
            float[] baseVertices = skin.BaseVertices;

            if (baseVertices.Length == 0 || baseVertices.Length % stride != 0)
                return baseVertices;

            float[] result = (float[])baseVertices.Clone();
            int vertexCount = baseVertices.Length / stride;

            for (int m = 0; m < skin.MorphTargets.Count && m < morphWeights.Length; m++)
            {
                float weight = morphWeights[m];
                if (MathF.Abs(weight) <= 0.000001f)
                    continue;

                AvatarMorphTarget morph = skin.MorphTargets[m];
                float[] positionDeltas = morph.PositionDeltas;
                float[] normalDeltas = morph.NormalDeltas;
                float[] tangentDeltas = morph.TangentDeltas;

                for (int v = 0; v < vertexCount; v++)
                {
                    int dst = v * stride;
                    int src = v * 3;

                    if (positionDeltas.Length >= src + 3)
                    {
                        result[dst + 0] += weight * positionDeltas[src + 0];
                        result[dst + 1] += weight * positionDeltas[src + 1];
                        result[dst + 2] += weight * positionDeltas[src + 2];
                    }

                    if (normalDeltas.Length >= src + 3)
                    {
                        result[dst + 9] += weight * normalDeltas[src + 0];
                        result[dst + 10] += weight * normalDeltas[src + 1];
                        result[dst + 11] += weight * normalDeltas[src + 2];
                    }

                    if (tangentDeltas.Length >= src + 3)
                    {
                        result[dst + 12] += weight * tangentDeltas[src + 0];
                        result[dst + 13] += weight * tangentDeltas[src + 1];
                        result[dst + 14] += weight * tangentDeltas[src + 2];
                    }
                }
            }

            return result;
        }

        public override string ToString() => $"Avatar({Id})";

        public AvatarHumanoidMapping Humanoid { get; } = new();

        public Dictionary<string, AvatarExpression> Expressions { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, float> ExpressionWeights { get; } = new(StringComparer.Ordinal);

        public AvatarLookAt LookAt { get; } = new();

        public AvatarMeta Meta { get; } = new();

        public bool LookAtEnabled { get; set; } = false;

        public Double3? LookAtTarget { get; set; } = null;

        private bool _expressionMorphsApplied;

        public void SetExpressionWeight(string name, float weight)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;

            if (weight <= 0.0001f)
                ExpressionWeights.Remove(name);
            else
                ExpressionWeights[name] = Math.Clamp(weight, 0f, 1f);
        }

        internal void ApplyExpressions(Graphics graphics)
        {
            if (graphics == null)
                return;

            ApplyExpressionOverrides(out float mouthFactor, out float blinkFactor, out float lookAtFactor);

            bool anyAppliedThisPass = false;

            for (int skinIndex = 0; skinIndex < Skins.Count; skinIndex++)
            {
                AvatarSkin skin = Skins[skinIndex];

                if (skin.MorphTargets.Count == 0)
                    continue;

                float[] morphWeights = new float[skin.MorphTargets.Count];

                foreach (KeyValuePair<string, float> pair in ExpressionWeights)
                {
                    if (!Expressions.TryGetValue(pair.Key, out AvatarExpression? expression))
                        continue;

                    float weight = pair.Value;
                    if (weight <= 0.0001f)
                        continue;

                    if (expression.IsPreset && IsProceduralExpression(pair.Key))
                    {
                        if (IsMouthExpression(pair.Key))
                            weight *= mouthFactor;
                        else if (IsBlinkExpression(pair.Key))
                            weight *= blinkFactor;
                        else if (IsLookAtExpression(pair.Key))
                            weight *= lookAtFactor;
                    }

                    if (weight <= 0.0001f)
                        continue;

                    foreach (AvatarMorphTargetBind bind in expression.MorphTargetBinds)
                    {
                        if (!string.Equals(bind.MeshKey, skin.MeshKey, StringComparison.Ordinal) ||
                            !string.Equals(bind.SurfaceId, skin.SurfaceId, StringComparison.Ordinal))
                            continue;

                        if (bind.MorphIndex >= 0 && bind.MorphIndex < morphWeights.Length)
                            morphWeights[bind.MorphIndex] += weight * bind.Weight;
                    }
                }

                bool any = false;
                foreach (float w in morphWeights)
                {
                    if (MathF.Abs(w) > 0.000001f)
                    {
                        any = true;
                        break;
                    }
                }

                if (any || _expressionMorphsApplied)
                {
                    float[] updated = any ? ComputeMorphUpdatedVertices(skin, morphWeights) : skin.BaseVertices;
                    graphics.UpdateMeshSurfaceVertices(skin.MeshKey, skin.SurfaceId, updated);
                    anyAppliedThisPass = any;
                }
            }

            _expressionMorphsApplied = anyAppliedThisPass;

            ApplyMaterialColorBinds(graphics);
            ApplyTextureTransformBinds(graphics);
        }

        private static bool IsMouthExpression(string name)
        {
            return name is "aa" or "ih" or "ou" or "ee" or "oh";
        }

        private static bool IsBlinkExpression(string name)
        {
            return name is "blink" or "blinkLeft" or "blinkRight";
        }

        private static bool IsLookAtExpression(string name)
        {
            return name is "lookUp" or "lookDown" or "lookLeft" or "lookRight";
        }

        private static bool IsProceduralExpression(string name)
        {
            return IsMouthExpression(name) || IsBlinkExpression(name) || IsLookAtExpression(name);
        }

        private void ApplyExpressionOverrides(out float mouthFactor, out float blinkFactor, out float lookAtFactor)
        {
            mouthFactor = 1f;
            blinkFactor = 1f;
            lookAtFactor = 1f;

            float mouthBlendSum = 0f;
            float blinkBlendSum = 0f;
            float lookAtBlendSum = 0f;
            bool mouthBlocked = false;
            bool blinkBlocked = false;
            bool lookAtBlocked = false;

            foreach (KeyValuePair<string, float> pair in ExpressionWeights)
            {
                if (!Expressions.TryGetValue(pair.Key, out AvatarExpression? expression))
                    continue;

                if (expression.IsPreset && IsProceduralExpression(pair.Key))
                    continue;

                float weight = pair.Value;
                if (weight <= 0.0001f)
                    continue;

                float effective = expression.IsBinary ? (weight > 0.5f ? 1f : 0f) : weight;

                switch (expression.OverrideMouth)
                {
                    case "block":
                        if (effective > 0f)
                            mouthBlocked = true;
                        break;
                    case "blend":
                        mouthBlendSum += effective;
                        break;
                }

                switch (expression.OverrideBlink)
                {
                    case "block":
                        if (effective > 0f)
                            blinkBlocked = true;
                        break;
                    case "blend":
                        blinkBlendSum += effective;
                        break;
                }

                switch (expression.OverrideLookAt)
                {
                    case "block":
                        if (effective > 0f)
                            lookAtBlocked = true;
                        break;
                    case "blend":
                        lookAtBlendSum += effective;
                        break;
                }
            }

            if (mouthBlocked)
                mouthFactor = 0f;
            else if (mouthBlendSum > 0f)
                mouthFactor = Math.Max(0f, 1f - Math.Min(1f, mouthBlendSum));

            if (blinkBlocked)
                blinkFactor = 0f;
            else if (blinkBlendSum > 0f)
                blinkFactor = Math.Max(0f, 1f - Math.Min(1f, blinkBlendSum));

            if (lookAtBlocked)
                lookAtFactor = 0f;
            else if (lookAtBlendSum > 0f)
                lookAtFactor = Math.Max(0f, 1f - Math.Min(1f, lookAtBlendSum));
        }

        private void ApplyMaterialColorBinds(Graphics graphics)
        {
            var deltas = new Dictionary<string, Dictionary<string, float[]>>(StringComparer.Ordinal);
            var bases = new Dictionary<string, Dictionary<string, float[]>>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, float> pair in ExpressionWeights)
            {
                if (!Expressions.TryGetValue(pair.Key, out AvatarExpression? expression))
                    continue;

                float weight = pair.Value;
                if (weight <= 0.0001f)
                    continue;

                foreach (AvatarMaterialColorBind bind in expression.MaterialColorBinds)
                {
                    if (!TryResolveMaterialColorUniform(bind.Type, out string uniformName))
                        continue;

                    if (!bases.TryGetValue(bind.MaterialKey, out var baseMap))
                    {
                        baseMap = new Dictionary<string, float[]>(StringComparer.Ordinal);
                        bases[bind.MaterialKey] = baseMap;
                    }

                    if (!baseMap.TryGetValue(uniformName, out float[] baseValues))
                    {
                        graphics.TryReadMaterialUniformValue(bind.MaterialKey, uniformName, out baseValues);
                        if (baseValues.Length == 0)
                            baseValues = new float[] { 1f, 1f, 1f, 1f };
                        baseMap[uniformName] = baseValues;
                    }

                    if (!deltas.TryGetValue(bind.MaterialKey, out var deltaMap))
                    {
                        deltaMap = new Dictionary<string, float[]>(StringComparer.Ordinal);
                        deltas[bind.MaterialKey] = deltaMap;
                    }

                    if (!deltaMap.TryGetValue(uniformName, out float[] delta))
                    {
                        delta = new float[4];
                        deltaMap[uniformName] = delta;
                    }

                    for (int i = 0; i < 4; i++)
                        delta[i] += (bind.TargetValue[i] - baseValues[i]) * weight;
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, float[]>> materialPair in deltas)
            {
                Dictionary<string, float[]> baseMap = bases[materialPair.Key];

                foreach (KeyValuePair<string, float[]> uniformPair in materialPair.Value)
                {
                    float[] baseValues = baseMap[uniformPair.Key];
                    float[] final = new float[4];

                    for (int i = 0; i < 4; i++)
                        final[i] = baseValues[i] + uniformPair.Value[i];

                    graphics.SetMaterialParameterOverride(materialPair.Key, uniformPair.Key, final);
                }
            }
        }

        private static bool TryResolveMaterialColorUniform(string type, out string uniformName)
        {
            switch (type)
            {
                case "color":
                    uniformName = "uColor";
                    return true;

                default:
                    uniformName = "";
                    return false;
            }
        }

        private void ApplyTextureTransformBinds(Graphics graphics)
        {
            var scales = new Dictionary<string, Vector2>(StringComparer.Ordinal);
            var offsets = new Dictionary<string, Vector2>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, float> pair in ExpressionWeights)
            {
                if (!Expressions.TryGetValue(pair.Key, out AvatarExpression? expression))
                    continue;

                float weight = pair.Value;
                if (weight <= 0.0001f)
                    continue;

                foreach (AvatarTextureTransformBind bind in expression.TextureTransformBinds)
                {
                    if (!scales.TryGetValue(bind.MaterialKey, out Vector2 scale))
                        scale = Vector2.One;

                    if (!offsets.TryGetValue(bind.MaterialKey, out Vector2 offset))
                        offset = Vector2.Zero;

                    scale += (bind.Scale - Vector2.One) * weight;
                    offset += bind.Offset * weight;

                    scales[bind.MaterialKey] = scale;
                    offsets[bind.MaterialKey] = offset;
                }
            }

            foreach (KeyValuePair<string, Vector2> pair in scales)
            {
                Vector2 scale = pair.Value;
                Vector2 offset = offsets.TryGetValue(pair.Key, out Vector2 o) ? o : Vector2.Zero;

                graphics.SetMaterialParameterOverride(
                    pair.Key,
                    "uTextureScaleOffset",
                    new float[] { scale.X, scale.Y, offset.X, offset.Y });
            }
        }

        internal void UpdateLookAt()
        {
            if (!LookAtEnabled || !LookAtTarget.HasValue)
                return;

            bool isBone = string.Equals(LookAt.Type, "bone", StringComparison.OrdinalIgnoreCase);
            bool isExpression = string.Equals(LookAt.Type, "expression", StringComparison.OrdinalIgnoreCase);

            if (!isBone && !isExpression)
                return;

            if (!Humanoid.TryGetBoneIndex("head", out int headBoneIndex))
                return;

            Matrix4x4[] nodeGlobal = Skeleton.ComputeNodeGlobalMatrices();

            if (headBoneIndex < 0 || headBoneIndex >= nodeGlobal.Length)
                return;

            Double3 target = LookAtTarget.Value;

            if (!Matrix4x4.Invert(GetModelMatrix(), out Matrix4x4 invAvatarWorld))
                return;

            Vector3 targetModel = Vector3.Transform(
                new Vector3((float)target.X, (float)target.Y, (float)target.Z),
                invAvatarWorld);

            Matrix4x4 headMatrix = nodeGlobal[headBoneIndex];

            Matrix4x4 headRotationMatrix = headMatrix;
            headRotationMatrix.M41 = 0f;
            headRotationMatrix.M42 = 0f;
            headRotationMatrix.M43 = 0f;

            Vector3 headPosition = new Vector3(headMatrix.M41, headMatrix.M42, headMatrix.M43);
            Vector3 lookAtOrigin = headPosition + Vector3.Transform(LookAt.OffsetFromHeadBone, headRotationMatrix);

            Vector3 direction = targetModel - lookAtOrigin;

            Quaternion headRotation = Quaternion.CreateFromRotationMatrix(headRotationMatrix);
            Vector3 localDirection = Vector3.Transform(direction, Quaternion.Inverse(headRotation));

            float yawDegrees = MathF.Atan2(localDirection.X, localDirection.Z) * (180f / MathF.PI);
            float xzLength = MathF.Sqrt(localDirection.X * localDirection.X + localDirection.Z * localDirection.Z);
            float pitchDegrees = MathF.Atan2(-localDirection.Y, xzLength) * (180f / MathF.PI);

            if (isBone)
                ApplyLookAtToBones(yawDegrees, pitchDegrees);
            else
                ApplyLookAtToExpressions(yawDegrees, pitchDegrees);
        }

        private void ApplyLookAtToBones(float yawDegrees, float pitchDegrees)
        {
            if (Humanoid.TryGetBoneIndex("leftEye", out int leftEyeIndex) &&
                leftEyeIndex >= 0 && leftEyeIndex < Skeleton.Bones.Count)
            {
                float yaw = yawDegrees > 0f
                    ? MapRange(yawDegrees, LookAt.RangeMapHorizontalOuter)
                    : -MapRange(-yawDegrees, LookAt.RangeMapHorizontalInner);

                float pitch = pitchDegrees > 0f
                    ? MapRange(pitchDegrees, LookAt.RangeMapVerticalDown)
                    : -MapRange(-pitchDegrees, LookAt.RangeMapVerticalUp);

                SetEyeBoneRotation(leftEyeIndex, yaw, pitch);
            }

            if (Humanoid.TryGetBoneIndex("rightEye", out int rightEyeIndex) &&
                rightEyeIndex >= 0 && rightEyeIndex < Skeleton.Bones.Count)
            {
                float yaw = yawDegrees > 0f
                    ? MapRange(yawDegrees, LookAt.RangeMapHorizontalInner)
                    : -MapRange(-yawDegrees, LookAt.RangeMapHorizontalOuter);

                float pitch = pitchDegrees > 0f
                    ? MapRange(pitchDegrees, LookAt.RangeMapVerticalDown)
                    : -MapRange(-pitchDegrees, LookAt.RangeMapVerticalUp);

                SetEyeBoneRotation(rightEyeIndex, yaw, pitch);
            }
        }

        private void SetEyeBoneRotation(int boneIndex, float yawDegrees, float pitchDegrees)
        {
            AvatarBone bone = Skeleton.Bones[boneIndex];

            double yawRadians = yawDegrees * Math.PI / 180.0;
            double pitchRadians = pitchDegrees * Math.PI / 180.0;

            DQuaternion qYaw = DQuaternion.CreateAxisAngle(new Double3(0.0, 1.0, 0.0), yawRadians);
            DQuaternion qPitch = DQuaternion.CreateAxisAngle(new Double3(1.0, 0.0, 0.0), pitchRadians);
            DQuaternion rotation = qYaw * qPitch;

            bone.LocalRotationQuaternion = null;
            bone.LocalRotation = rotation.ToEulerDegrees();
        }

        private void ApplyLookAtToExpressions(float yawDegrees, float pitchDegrees)
        {
            if (yawDegrees > 0f)
            {
                float weight = MapRange(yawDegrees, LookAt.RangeMapHorizontalOuter);
                SetExpressionWeight("lookLeft", weight);
                SetExpressionWeight("lookRight", 0f);
            }
            else
            {
                float weight = MapRange(-yawDegrees, LookAt.RangeMapHorizontalOuter);
                SetExpressionWeight("lookRight", weight);
                SetExpressionWeight("lookLeft", 0f);
            }

            if (pitchDegrees > 0f)
            {
                float weight = MapRange(pitchDegrees, LookAt.RangeMapVerticalDown);
                SetExpressionWeight("lookDown", weight);
                SetExpressionWeight("lookUp", 0f);
            }
            else
            {
                float weight = MapRange(-pitchDegrees, LookAt.RangeMapVerticalUp);
                SetExpressionWeight("lookUp", weight);
                SetExpressionWeight("lookDown", 0f);
            }
        }

        private static float MapRange(float value, AvatarRangeMap rangeMap)
        {
            float inputMax = MathF.Max(0.001f, rangeMap.InputMaxValue);
            return MathF.Min(MathF.Abs(value), rangeMap.InputMaxValue) / inputMax * rangeMap.OutputScale;
        }

        private static void CopyRangeMap(AvatarRangeMap source, AvatarRangeMap target)
        {
            target.InputMaxValue = source.InputMaxValue;
            target.OutputScale = source.OutputScale;
        }
    }

    public sealed class AvatarBone
    {
        public string Name { get; set; } = "";

        public int ParentIndex { get; set; } = -1;

        public Double3 LocalPosition { get; set; } = Double3.Zero;

        public Double3 LocalRotation { get; set; } = Double3.Zero;

        public Double3 LocalScale { get; set; } = Double3.One;

        internal DQuaternion? LocalRotationQuaternion { get; set; }
    }

    public sealed class AvatarSkeleton
    {
        public List<AvatarBone> Bones { get; } = new();

        public int AddBone(string name, int parentIndex)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("[X] Bone name cannot be null or empty.", nameof(name));

            if (parentIndex < -1 || parentIndex >= Bones.Count)
                throw new ArgumentOutOfRangeException(nameof(parentIndex));

            var bone = new AvatarBone { Name = name, ParentIndex = parentIndex };
            Bones.Add(bone);
            return Bones.Count - 1;
        }

        public AvatarBone? GetBone(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            for (int i = 0; i < Bones.Count; i++)
            {
                if (string.Equals(Bones[i].Name, name, StringComparison.Ordinal))
                    return Bones[i];
            }

            return null;
        }

        public AvatarBone? GetBone(int index)
        {
            if (index < 0 || index >= Bones.Count)
                return null;

            return Bones[index];
        }

        public int GetBoneIndex(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return -1;

            for (int i = 0; i < Bones.Count; i++)
            {
                if (string.Equals(Bones[i].Name, name, StringComparison.Ordinal))
                    return i;
            }

            return -1;
        }

        public Matrix4x4[] ComputeNodeGlobalMatrices()
        {
            var result = new Matrix4x4[Bones.Count];
            var computed = new bool[Bones.Count];

            for (int i = 0; i < Bones.Count; i++)
                result[i] = Matrix4x4.Identity;

            bool progress = true;

            while (progress)
            {
                progress = false;

                for (int i = 0; i < Bones.Count; i++)
                {
                    if (computed[i])
                        continue;

                    AvatarBone bone = Bones[i];

                    DQuaternion rotation = bone.LocalRotationQuaternion ??
                                           DQuaternion.FromEulerDegrees(bone.LocalRotation);

                    Matrix4x4 local =
                        Matrix4x4.CreateScale(
                            (float)bone.LocalScale.X,
                            (float)bone.LocalScale.Y,
                            (float)bone.LocalScale.Z) *
                        Matrix4x4.CreateFromQuaternion(rotation.ToSingle()) *
                        Matrix4x4.CreateTranslation(
                            (float)bone.LocalPosition.X,
                            (float)bone.LocalPosition.Y,
                            (float)bone.LocalPosition.Z);

                    if (bone.ParentIndex < 0)
                    {
                        result[i] = local;
                        computed[i] = true;
                        progress = true;
                    }
                    else if (bone.ParentIndex < Bones.Count && computed[bone.ParentIndex])
                    {
                        result[i] = local * result[bone.ParentIndex];
                        computed[i] = true;
                        progress = true;
                    }
                }
            }

            return result;
        }
    }

    public sealed class AvatarMorphTarget
    {
        public string Name { get; set; } = "";

        public float[] PositionDeltas { get; set; } = Array.Empty<float>();

        public float[] NormalDeltas { get; set; } = Array.Empty<float>();

        public float[] TangentDeltas { get; set; } = Array.Empty<float>();
    }

    public sealed class AvatarSkin
    {
        public string MeshKey { get; set; } = "";

        public string SurfaceId { get; set; } = "";

        public int VertexStrideFloats { get; set; } = 27;

        public float[] BaseVertices { get; set; } = Array.Empty<float>();

        public int[] JointBoneIndices { get; set; } = Array.Empty<int>();

        public Matrix4x4[] InverseBindMatrices { get; set; } = Array.Empty<Matrix4x4>();

        public int[] JointIndicesPerVertex { get; set; } = Array.Empty<int>();

        public float[] JointWeightsPerVertex { get; set; } = Array.Empty<float>();

        public List<AvatarMorphTarget> MorphTargets { get; } = new();
    }

    internal static class AvatarRegistry
    {
        private static readonly ConcurrentDictionary<string, Avatar> _avatars = new(StringComparer.Ordinal);

        public static bool Register(Avatar avatar)
        {
            if (avatar == null)
                throw new ArgumentNullException(nameof(avatar));

            bool added = _avatars.TryAdd(avatar.Id, avatar);
            if (added)
                Console.WriteLine($"[i] Registered avatar: {avatar.Id}");

            return added;
        }

        public static bool Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return false;

            bool removed = _avatars.TryRemove(id, out _);
            if (removed)
                Console.WriteLine($"[i] Removed avatar: {id}");

            return removed;
        }

        public static bool TryGet(string id, out Avatar? avatar)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                avatar = null;
                return false;
            }

            return _avatars.TryGetValue(id, out avatar);
        }

        public static IEnumerable<Avatar> GetAll()
        {
            return _avatars.Values;
        }

        public static void Clear()
        {
            _avatars.Clear();
            Console.WriteLine("[i] Cleared all avatars.");
        }
    }

    public sealed class AvatarHumanoidMapping
    {
        private readonly Dictionary<string, int> _boneIndices = new(StringComparer.Ordinal);

        public bool TryGetBoneIndex(string boneName, out int boneIndex)
        {
            if (string.IsNullOrWhiteSpace(boneName))
            {
                boneIndex = -1;
                return false;
            }

            return _boneIndices.TryGetValue(boneName, out boneIndex);
        }

        public void SetBoneIndex(string boneName, int boneIndex)
        {
            if (string.IsNullOrWhiteSpace(boneName))
                throw new ArgumentException("[X] Humanoid bone name cannot be null or empty.", nameof(boneName));

            if (boneIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(boneIndex));

            _boneIndices[boneName] = boneIndex;
        }

        public void Clear()
        {
            _boneIndices.Clear();
        }

        public IEnumerable<KeyValuePair<string, int>> Enumerate()
        {
            return _boneIndices;
        }
    }

    public sealed class AvatarRangeMap
    {
        public float InputMaxValue { get; set; } = 90f;

        public float OutputScale { get; set; } = 10f;
    }

    public sealed class AvatarLookAt
    {
        public string Type { get; set; } = "bone";

        public Vector3 OffsetFromHeadBone { get; set; } = Vector3.Zero;

        public AvatarRangeMap RangeMapHorizontalInner { get; } = new();

        public AvatarRangeMap RangeMapHorizontalOuter { get; } = new();

        public AvatarRangeMap RangeMapVerticalDown { get; } = new();

        public AvatarRangeMap RangeMapVerticalUp { get; } = new();
    }

    public sealed class AvatarMeta
    {
        public string Name { get; set; } = "";

        public string Version { get; set; } = "";

        public List<string> Authors { get; } = new();

        public string CopyrightInformation { get; set; } = "";
    }

    public sealed class AvatarMorphTargetBind
    {
        public string MeshKey { get; set; } = "";

        public string SurfaceId { get; set; } = "";

        public int MorphIndex { get; set; }

        public float Weight { get; set; } = 1f;
    }

    public sealed class AvatarMaterialColorBind
    {
        public string MaterialKey { get; set; } = "";

        public string Type { get; set; } = "color";

        public Vector4 TargetValue { get; set; }
    }

    public sealed class AvatarTextureTransformBind
    {
        public string MaterialKey { get; set; } = "";

        public Vector2 Scale { get; set; } = Vector2.One;

        public Vector2 Offset { get; set; } = Vector2.Zero;
    }

    public sealed class AvatarExpression
    {
        public string Name { get; set; } = "";

        public bool IsPreset { get; set; }

        public bool IsBinary { get; set; }

        public string OverrideMouth { get; set; } = "none";

        public string OverrideBlink { get; set; } = "none";

        public string OverrideLookAt { get; set; } = "none";

        public List<AvatarMorphTargetBind> MorphTargetBinds { get; } = new();

        public List<AvatarMaterialColorBind> MaterialColorBinds { get; } = new();

        public List<AvatarTextureTransformBind> TextureTransformBinds { get; } = new();
    }
}
