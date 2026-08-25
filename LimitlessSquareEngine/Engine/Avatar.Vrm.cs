using System;
using System.Collections.Generic;
using System.Numerics;
using LimitlessSquareEngine;

namespace LimitlessSquareEngine.Engine
{
    public class VrmAvatar : Avatar
    {
        public VrmAvatar()
        {
        }

        public VrmAvatar(string modelKey)
        {
            ModelKey = modelKey;
        }

        private const int SkinnedVertexStrideFloats = 27;

        public string BuildMaterialKey(int materialIndex)
        {
            return $"{ModelKey}::mat_{materialIndex}";
        }

        public void MapToAvatar(Avatar avatar, VrmData data)
        {
            if (avatar == null)
                throw new ArgumentNullException(nameof(avatar));

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            avatar.Skeleton.Bones.Clear();
            avatar.Skins.Clear();
            avatar.Expressions.Clear();
            avatar.ExpressionWeights.Clear();
            avatar.Humanoid.Clear();

            foreach (VrmSkeletonNode node in data.Nodes)
            {
                avatar.Skeleton.AddBone(node.Name, -1);
                AvatarBone bone = avatar.Skeleton.Bones[^1];

                DQuaternion rotation = new DQuaternion(
                    node.Rotation.X,
                    node.Rotation.Y,
                    node.Rotation.Z,
                    node.Rotation.W);

                bone.LocalPosition = new Double3(node.Position.X, node.Position.Y, node.Position.Z);
                bone.LocalRotationQuaternion = rotation;
                bone.LocalRotation = rotation.ToEulerDegrees();
                bone.LocalScale = new Double3(node.Scale.X, node.Scale.Y, node.Scale.Z);
            }

            for (int i = 0; i < data.Nodes.Count && i < avatar.Skeleton.Bones.Count; i++)
            {
                int parentIndex = data.Nodes[i].ParentIndex;

                if (parentIndex >= -1 && parentIndex < avatar.Skeleton.Bones.Count)
                    avatar.Skeleton.Bones[i].ParentIndex = parentIndex;
            }

            foreach (VrmMeshSurfaceData surfaceData in data.Surfaces)
            {
                AvatarSkin? skin = BuildAvatarSkin(avatar, data, surfaceData);
                if (skin != null)
                    avatar.Skins.Add(skin);
            }

            foreach (VrmHumanoidBone humanoidBone in data.HumanoidBones)
            {
                if (humanoidBone.NodeIndex >= 0 && humanoidBone.NodeIndex < avatar.Skeleton.Bones.Count)
                    avatar.Humanoid.SetBoneIndex(humanoidBone.BoneName, humanoidBone.NodeIndex);
            }

            foreach (VrmRawExpression rawExpression in data.Expressions)
            {
                AvatarExpression expression = new AvatarExpression
                {
                    Name = rawExpression.Name,
                    IsPreset = rawExpression.IsPreset,
                    IsBinary = rawExpression.IsBinary,
                    OverrideMouth = rawExpression.OverrideMouth,
                    OverrideBlink = rawExpression.OverrideBlink,
                    OverrideLookAt = rawExpression.OverrideLookAt
                };

                foreach (VrmRawMorphTargetBind bind in rawExpression.MorphTargetBinds)
                {
                    foreach (VrmMeshSurfaceData surfaceData in data.Surfaces)
                    {
                        if (surfaceData.NodeIndex != bind.NodeIndex)
                            continue;

                        expression.MorphTargetBinds.Add(new AvatarMorphTargetBind
                        {
                            MeshKey = avatar.ModelKey,
                            SurfaceId = surfaceData.Name,
                            MorphIndex = bind.MorphIndex,
                            Weight = bind.Weight
                        });
                    }
                }

                foreach (VrmRawMaterialColorBind bind in rawExpression.MaterialColorBinds)
                {
                    expression.MaterialColorBinds.Add(new AvatarMaterialColorBind
                    {
                        MaterialKey = BuildMaterialKey(bind.MaterialIndex),
                        Type = bind.Type,
                        TargetValue = bind.TargetValue
                    });
                }

                foreach (VrmRawTextureTransformBind bind in rawExpression.TextureTransformBinds)
                {
                    expression.TextureTransformBinds.Add(new AvatarTextureTransformBind
                    {
                        MaterialKey = BuildMaterialKey(bind.MaterialIndex),
                        Scale = bind.Scale,
                        Offset = bind.Offset
                    });
                }

                avatar.Expressions[expression.Name] = expression;
            }

            avatar.LookAt.Type = data.LookAt.Type;
            avatar.LookAt.OffsetFromHeadBone = data.LookAt.OffsetFromHeadBone;
            CopyRangeMap(data.LookAt.RangeMapHorizontalInner, avatar.LookAt.RangeMapHorizontalInner);
            CopyRangeMap(data.LookAt.RangeMapHorizontalOuter, avatar.LookAt.RangeMapHorizontalOuter);
            CopyRangeMap(data.LookAt.RangeMapVerticalDown, avatar.LookAt.RangeMapVerticalDown);
            CopyRangeMap(data.LookAt.RangeMapVerticalUp, avatar.LookAt.RangeMapVerticalUp);

            avatar.Meta.Name = data.Meta.Name;
            avatar.Meta.Version = data.Meta.Version;
            avatar.Meta.Authors.Clear();
            foreach (string author in data.Meta.Authors)
                avatar.Meta.Authors.Add(author);
            avatar.Meta.CopyrightInformation = data.Meta.CopyrightInformation;
        }

        private static void CopyRangeMap(VrmRangeMap source, AvatarRangeMap target)
        {
            target.InputMaxValue = source.InputMaxValue;
            target.OutputScale = source.OutputScale;
        }

        private AvatarSkin? BuildAvatarSkin(Avatar avatar, VrmData data, VrmMeshSurfaceData surfaceData)
        {
            if (surfaceData.Indices.Length == 0 || surfaceData.PositionCount <= 0)
                return null;

            VrmSkinData? skinData = surfaceData.SkinIndex >= 0 && surfaceData.SkinIndex < data.Skins.Count
                ? data.Skins[surfaceData.SkinIndex]
                : null;

            bool hasSkin = skinData != null &&
                           surfaceData.JointIndices.Length >= surfaceData.PositionCount * 4 &&
                           surfaceData.JointWeights.Length >= surfaceData.PositionCount * 4;

            int triangleCount = surfaceData.Indices.Length / 3;
            int vertexCount = triangleCount * 3;

            float[] vertices = new float[vertexCount * SkinnedVertexStrideFloats];
            int[] jointIndicesPerVertex = new int[vertexCount * 4];
            float[] jointWeightsPerVertex = new float[vertexCount * 4];

            for (int t = 0; t < triangleCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int srcIndex = surfaceData.Indices[t * 3 + k];
                    int dst = (t * 3 + k) * SkinnedVertexStrideFloats;

                    Vector3 pos = ReadVector3(surfaceData.Positions, srcIndex, Vector3.Zero);
                    Vector3 normal = ReadVector3(surfaceData.Normals, srcIndex, new Vector3(0f, 0f, 1f));
                    Vector2 uv = ReadVector2(surfaceData.TexCoords, srcIndex);
                    Vector4 tangent = ReadVector4(surfaceData.Tangents, srcIndex, new Vector4(1f, 0f, 0f, 1f));

                    vertices[dst + 0] = pos.X;
                    vertices[dst + 1] = pos.Y;
                    vertices[dst + 2] = pos.Z;

                    vertices[dst + 3] = 1f;
                    vertices[dst + 4] = 1f;
                    vertices[dst + 5] = 1f;
                    vertices[dst + 6] = 1f;

                    vertices[dst + 7] = uv.X;
                    vertices[dst + 8] = uv.Y;

                    vertices[dst + 9] = normal.X;
                    vertices[dst + 10] = normal.Y;
                    vertices[dst + 11] = normal.Z;

                    vertices[dst + 12] = tangent.X;
                    vertices[dst + 13] = tangent.Y;
                    vertices[dst + 14] = tangent.Z;
                    vertices[dst + 15] = tangent.W;

                    vertices[dst + 16] = 0f;
                    vertices[dst + 17] = 0f;
                    vertices[dst + 18] = 0f;

                    if (hasSkin)
                    {
                        for (int q = 0; q < 4; q++)
                        {
                            int jointIndex = (int)surfaceData.JointIndices[srcIndex * 4 + q];
                            float weight = surfaceData.JointWeights[srcIndex * 4 + q];

                            jointIndicesPerVertex[(t * 3 + k) * 4 + q] = jointIndex;
                            jointWeightsPerVertex[(t * 3 + k) * 4 + q] = weight;

                            vertices[dst + 19 + q] = jointIndex;
                            vertices[dst + 23 + q] = weight;
                        }
                    }
                    else
                    {
                        jointIndicesPerVertex[(t * 3 + k) * 4 + 0] = 0;
                        jointWeightsPerVertex[(t * 3 + k) * 4 + 0] = 1f;

                        vertices[dst + 19] = 0f;
                        vertices[dst + 20] = 0f;
                        vertices[dst + 21] = 0f;
                        vertices[dst + 22] = 0f;

                        vertices[dst + 23] = 0f;
                        vertices[dst + 24] = 0f;
                        vertices[dst + 25] = 0f;
                        vertices[dst + 26] = 1f;
                    }
                }
            }

            AvatarSkin skin = new AvatarSkin
            {
                MeshKey = avatar.ModelKey,
                SurfaceId = surfaceData.Name,
                VertexStrideFloats = SkinnedVertexStrideFloats,
                BaseVertices = vertices,
                JointBoneIndices = skinData?.Joints ?? Array.Empty<int>(),
                InverseBindMatrices = skinData?.InverseBindMatrices ?? Array.Empty<Matrix4x4>(),
                JointIndicesPerVertex = jointIndicesPerVertex,
                JointWeightsPerVertex = jointWeightsPerVertex
            };

            for (int m = 0; m < surfaceData.MorphTargets.Count; m++)
            {
                VrmMorphTargetData morphData = surfaceData.MorphTargets[m];

                skin.MorphTargets.Add(new AvatarMorphTarget
                {
                    Name = $"morph_{m}",
                    PositionDeltas = ExpandMorphDeltas(morphData.PositionDeltas, surfaceData.Indices),
                    NormalDeltas = ExpandMorphDeltas(morphData.NormalDeltas, surfaceData.Indices)
                });
            }

            return skin;
        }

        private static float[] ExpandMorphDeltas(float[] deltas, int[] indices)
        {
            if (deltas == null || deltas.Length == 0)
                return Array.Empty<float>();

            float[] expanded = new float[indices.Length * 3];

            for (int i = 0; i < indices.Length; i++)
            {
                int src = indices[i] * 3;
                expanded[i * 3 + 0] = deltas[src + 0];
                expanded[i * 3 + 1] = deltas[src + 1];
                expanded[i * 3 + 2] = deltas[src + 2];
            }

            return expanded;
        }

        private static Vector3 ReadVector3(float[] values, int index, Vector3 fallback)
        {
            if (values.Length < index * 3 + 3)
                return fallback;

            return new Vector3(values[index * 3 + 0], values[index * 3 + 1], values[index * 3 + 2]);
        }

        private static Vector2 ReadVector2(float[] values, int index)
        {
            if (values.Length < index * 2 + 2)
                return Vector2.Zero;

            return new Vector2(values[index * 2 + 0], values[index * 2 + 1]);
        }

        private static Vector4 ReadVector4(float[] values, int index, Vector4 fallback)
        {
            if (values.Length < index * 4 + 4)
                return fallback;

            return new Vector4(values[index * 4 + 0], values[index * 4 + 1], values[index * 4 + 2], values[index * 4 + 3]);
        }
    }

    public sealed class VrmHumanoidMapping
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

    public sealed class VrmExpression
    {
        public string Name { get; set; } = "";

        public bool IsPreset { get; set; }

        public bool IsBinary { get; set; }

        public string OverrideMouth { get; set; } = "none";

        public string OverrideBlink { get; set; } = "none";

        public string OverrideLookAt { get; set; } = "none";

        public List<VrmMorphTargetBind> MorphTargetBinds { get; } = new();

        public List<VrmMaterialColorBind> MaterialColorBinds { get; } = new();

        public List<VrmTextureTransformBind> TextureTransformBinds { get; } = new();
    }

    public sealed class VrmMorphTargetBind
    {
        public string MeshKey { get; set; } = "";

        public string SurfaceId { get; set; } = "";

        public int MorphIndex { get; set; }

        public float Weight { get; set; } = 1f;
    }

    public sealed class VrmMaterialColorBind
    {
        public string MaterialKey { get; set; } = "";

        public string Type { get; set; } = "color";

        public Vector4 TargetValue { get; set; }
    }

    public sealed class VrmTextureTransformBind
    {
        public string MaterialKey { get; set; } = "";

        public Vector2 Scale { get; set; } = Vector2.One;

        public Vector2 Offset { get; set; } = Vector2.Zero;
    }

    public sealed class VrmLookAt
    {
        public string Type { get; set; } = "bone";

        public Vector3 OffsetFromHeadBone { get; set; } = Vector3.Zero;

        public VrmRangeMap RangeMapHorizontalInner { get; } = new();

        public VrmRangeMap RangeMapHorizontalOuter { get; } = new();

        public VrmRangeMap RangeMapVerticalDown { get; } = new();

        public VrmRangeMap RangeMapVerticalUp { get; } = new();
    }

    public sealed class VrmRangeMap
    {
        public float InputMaxValue { get; set; } = 90f;

        public float OutputScale { get; set; } = 10f;
    }

    public sealed class VrmMeta
    {
        public string Name { get; set; } = "";

        public string Version { get; set; } = "";

        public List<string> Authors { get; } = new();

        public string CopyrightInformation { get; set; } = "";
    }

    public sealed class VrmSkeletonNode
    {
        public string Name { get; set; } = "";

        public int ParentIndex { get; set; } = -1;

        public Vector3 Position { get; set; }

        public Quaternion Rotation { get; set; } = Quaternion.Identity;

        public Vector3 Scale { get; set; } = Vector3.One;
    }

    public sealed class VrmMorphTargetData
    {
        public float[] PositionDeltas { get; set; } = Array.Empty<float>();

        public float[] NormalDeltas { get; set; } = Array.Empty<float>();
    }

    public sealed class VrmMeshSurfaceData
    {
        public string Name { get; set; } = "";

        public int NodeIndex { get; set; }

        public int MaterialIndex { get; set; }

        public int SkinIndex { get; set; } = -1;

        public int PositionCount { get; set; }

        public float[] Positions { get; set; } = Array.Empty<float>();

        public float[] Normals { get; set; } = Array.Empty<float>();

        public float[] TexCoords { get; set; } = Array.Empty<float>();

        public float[] Tangents { get; set; } = Array.Empty<float>();

        public int[] Indices { get; set; } = Array.Empty<int>();

        public int[] JointIndices { get; set; } = Array.Empty<int>();

        public float[] JointWeights { get; set; } = Array.Empty<float>();

        public List<VrmMorphTargetData> MorphTargets { get; } = new();
    }

    public sealed class VrmSkinData
    {
        public int[] Joints { get; set; } = Array.Empty<int>();

        public Matrix4x4[] InverseBindMatrices { get; set; } = Array.Empty<Matrix4x4>();
    }

    public sealed class VrmTextureData
    {
        public string Name { get; set; } = "";

        public byte[] Content { get; set; } = Array.Empty<byte>();

        public string FileExtension { get; set; } = "png";
    }

    public sealed class VrmMtoonData
    {
        public string SpecVersion { get; set; } = "";

        public Vector3 ShadeColorFactor { get; set; } = Vector3.One;

        public int ShadeMultiplyTextureIndex { get; set; } = -1;

        public float ShadingShiftFactor { get; set; }

        public float ShadingToonyFactor { get; set; }

        public Vector3 ParametricRimColorFactor { get; set; } = Vector3.Zero;

        public float ParametricRimFresnelPowerFactor { get; set; } = 1f;

        public float ParametricRimLiftFactor { get; set; }

        public int RimMultiplyTextureIndex { get; set; } = -1;

        public float RimLightingMixFactor { get; set; }

        public Vector3 MatcapFactor { get; set; } = Vector3.One;

        public int MatcapTextureIndex { get; set; } = -1;

        public string OutlineWidthMode { get; set; } = "none";

        public Vector3 OutlineColorFactor { get; set; } = Vector3.Zero;

        public float OutlineWidthFactor { get; set; }

        public float OutlineLightingMixFactor { get; set; }

        public int OutlineWidthMultiplyTextureIndex { get; set; } = -1;

        public float GiEqualizationFactor { get; set; }

        public bool TransparentWithZWrite { get; set; }

        public int RenderQueueOffsetNumber { get; set; }

        public float UvAnimationScrollXSpeedFactor { get; set; }

        public float UvAnimationScrollYSpeedFactor { get; set; }

        public float UvAnimationRotationSpeedFactor { get; set; }
    }

    public sealed class VrmMaterialData
    {
        public string Name { get; set; } = "";

        public bool Unlit { get; set; }

        public bool DoubleSided { get; set; }

        public string AlphaMode { get; set; } = "Opaque";

        public float AlphaCutoff { get; set; } = 0.5f;

        public Vector4 BaseColor { get; set; } = Vector4.One;

        public int BaseColorTextureIndex { get; set; } = -1;

        public Vector2 BaseColorTextureScale { get; set; } = Vector2.One;

        public Vector2 BaseColorTextureOffset { get; set; } = Vector2.Zero;

        public float Metallic { get; set; }

        public float Roughness { get; set; } = 1f;

        public float NormalStrength { get; set; } = 1f;

        public int NormalTextureIndex { get; set; } = -1;

        public Vector2 NormalTextureScale { get; set; } = Vector2.One;

        public Vector2 NormalTextureOffset { get; set; } = Vector2.Zero;

        public Vector4 EmissiveColor { get; set; } = Vector4.Zero;

        public int EmissiveTextureIndex { get; set; } = -1;

        public int OcclusionTextureIndex { get; set; } = -1;

        public VrmMtoonData? Mtoon { get; set; }
    }

    public sealed class VrmHumanoidBone
    {
        public string BoneName { get; set; } = "";

        public int NodeIndex { get; set; }
    }

    public sealed class VrmRawMorphTargetBind
    {
        public int NodeIndex { get; set; }

        public int MorphIndex { get; set; }

        public float Weight { get; set; } = 1f;
    }

    public sealed class VrmRawMaterialColorBind
    {
        public int MaterialIndex { get; set; }

        public string Type { get; set; } = "color";

        public Vector4 TargetValue { get; set; }
    }

    public sealed class VrmRawTextureTransformBind
    {
        public int MaterialIndex { get; set; }

        public Vector2 Scale { get; set; } = Vector2.One;

        public Vector2 Offset { get; set; } = Vector2.Zero;
    }

    public sealed class VrmRawExpression
    {
        public string Name { get; set; } = "";

        public bool IsPreset { get; set; }

        public bool IsBinary { get; set; }

        public string OverrideMouth { get; set; } = "none";

        public string OverrideBlink { get; set; } = "none";

        public string OverrideLookAt { get; set; } = "none";

        public List<VrmRawMorphTargetBind> MorphTargetBinds { get; } = new();

        public List<VrmRawMaterialColorBind> MaterialColorBinds { get; } = new();

        public List<VrmRawTextureTransformBind> TextureTransformBinds { get; } = new();
    }

    public sealed class VrmData
    {
        public List<VrmSkeletonNode> Nodes { get; } = new();

        public List<VrmMeshSurfaceData> Surfaces { get; } = new();

        public List<VrmSkinData> Skins { get; } = new();

        public List<VrmMaterialData> Materials { get; } = new();

        public List<VrmTextureData> Textures { get; } = new();

        public List<VrmHumanoidBone> HumanoidBones { get; } = new();

        public List<VrmRawExpression> Expressions { get; } = new();

        public VrmLookAt LookAt { get; } = new();

        public VrmMeta Meta { get; } = new();
    }
}
