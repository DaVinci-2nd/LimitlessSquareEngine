using LimitlessSquareEngine.Engine;
using Silk.NET.OpenGL;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;

namespace LimitlessSquareEngine
{
    internal partial class Graphics
    {
        private const string AssetPreviewSceneId = "__editor_asset_preview__";
        private const string AssetPreviewCameraObjectId = "__lse_asset_preview_camera__";
        private const string AssetPreviewKeyLightObjectId = "__lse_asset_preview_key_light__";
        private const string AssetPreviewFillLightObjectId = "__lse_asset_preview_fill_light__";
        private const string AssetPreviewMeshObjectId = "__lse_asset_preview_mesh__";

        private uint _assetPreviewFramebuffer;
        private uint _assetPreviewColorTexture;
        private uint _assetPreviewDepthTexture;
        private int _assetPreviewFramebufferWidth;
        private int _assetPreviewFramebufferHeight;

        private uint _scenePassTargetFramebuffer;
        private int _assetPreviewViewportWidth;
        private int _assetPreviewViewportHeight;

        private void EnsureAssetPreviewResources(int width, int height)
        {
            if (_assetPreviewFramebuffer != 0 &&
                _assetPreviewColorTexture != 0 &&
                _assetPreviewDepthTexture != 0 &&
                _assetPreviewFramebufferWidth == width &&
                _assetPreviewFramebufferHeight == height)
            {
                return;
            }

            if (_assetPreviewFramebuffer != 0)
                _gl.DeleteFramebuffer(_assetPreviewFramebuffer);

            if (_assetPreviewColorTexture != 0)
                _gl.DeleteTexture(_assetPreviewColorTexture);

            if (_assetPreviewDepthTexture != 0)
                _gl.DeleteTexture(_assetPreviewDepthTexture);

            _assetPreviewColorTexture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _assetPreviewColorTexture);

            byte[] emptyColor = new byte[width * height * 4];

            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                (ReadOnlySpan<byte>)emptyColor);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

            _assetPreviewDepthTexture = CreatePostProcessDepthTexture(width, height);

            _assetPreviewFramebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _assetPreviewFramebuffer);

            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                _assetPreviewColorTexture,
                0);

            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.DepthStencilAttachment,
                TextureTarget.Texture2D,
                _assetPreviewDepthTexture,
                0);

            _gl.DrawBuffer(GLEnum.ColorAttachment0);
            _gl.ReadBuffer(GLEnum.ColorAttachment0);

            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new Exception($"[X] Asset preview framebuffer incomplete: {status}");

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _assetPreviewFramebufferWidth = width;
            _assetPreviewFramebufferHeight = height;
        }

        public void ProcessAssetPreviewQueue(
            ConcurrentQueue<EditorHostBridge.AssetPreviewRequest> requests,
            ConcurrentQueue<EditorHostBridge.AssetPreviewResult> results)
        {
            if (requests == null || results == null || requests.IsEmpty)
                return;

            int budget = 2;

            while (budget-- > 0 && requests.TryDequeue(out EditorHostBridge.AssetPreviewRequest? request))
            {
                if (request == null || string.IsNullOrWhiteSpace(request.RequestKey))
                    continue;

                EditorRenderedFrame? frame = null;

                try
                {
                    frame = RenderAssetPreview(request);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[!] Asset preview '{request.RequestKey}' failed: {ex.Message}");
                }

                results.Enqueue(new EditorHostBridge.AssetPreviewResult
                {
                    RequestKey = request.RequestKey,
                    Frame = frame
                });
            }
        }

        private EditorRenderedFrame? RenderAssetPreview(EditorHostBridge.AssetPreviewRequest request)
        {
            if (!_isInitialized)
                Initialize();

            RegisterBuiltInMeshes();

            int width = Math.Clamp(request.Width, 8, 512);
            int height = Math.Clamp(request.Height, 8, 512);

            EnsureAssetPreviewResources(width, height);

            List<SceneRenderObjectSnapshot> snapshots = new List<SceneRenderObjectSnapshot>();
            Avatar? clipAvatar = null;
            AnimationClipData? clipData = null;
            double fieldOfViewDegrees = 40.0;
            double viewPitchDegrees = 12.0;

            switch (request.Kind)
            {
                case EditorHostBridge.AssetPreviewKind.Material:
                    if (string.IsNullOrWhiteSpace(request.MaterialKey))
                        return null;

                    snapshots.Add(new SceneRenderObjectSnapshot
                    {
                        SceneId = AssetPreviewSceneId,
                        ObjectId = AssetPreviewMeshObjectId,
                        Type = "Object",
                        Active = true,
                        Visible = true,
                        Mesh = "builtin/sphere_1x1x1",
                        Materials = new List<string> { request.MaterialKey },
                        WorldScale = Double3.One,
                        WorldRotation = DQuaternion.Identity,
                        StaticRenderEligible = false,
                        TransformRevision = 0
                    });
                    fieldOfViewDegrees = 35.0;
                    viewPitchDegrees = 0.0;
                    break;

                case EditorHostBridge.AssetPreviewKind.Prefab:
                    {
                        if (string.IsNullOrWhiteSpace(request.AssetFilePath) || !File.Exists(request.AssetFilePath))
                            return null;

                        PrefabData? prefab = Prefab.ParsePrefabFile(request.AssetFilePath);

                        if (prefab == null || prefab.Objects.Count == 0)
                            return null;

                        Dictionary<string, SceneObject> prefabObjectsById = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
                        Dictionary<string, int> prefabDepths = new Dictionary<string, int>(StringComparer.Ordinal);
                        Dictionary<string, SceneWorldState> prefabWorlds = new Dictionary<string, SceneWorldState>(StringComparer.Ordinal);

                        foreach (SceneObject prefabObject in prefab.Objects)
                        {
                            if (string.IsNullOrWhiteSpace(prefabObject.Id))
                                continue;

                            prefabObjectsById[prefabObject.Id] = prefabObject;
                        }

                        foreach (SceneObject prefabObject in prefabObjectsById.Values)
                        {
                            int depth = 0;
                            string? cursorId = prefabObject.Transform?.ParentId;
                            HashSet<string> visitedIds = new HashSet<string>(StringComparer.Ordinal);

                            while (!string.IsNullOrWhiteSpace(cursorId) && visitedIds.Add(cursorId) && depth <= prefab.Objects.Count)
                            {
                                depth++;

                                if (!prefabObjectsById.TryGetValue(cursorId, out SceneObject? parentObject))
                                    break;

                                cursorId = parentObject.Transform?.ParentId;
                            }

                            prefabDepths[prefabObject.Id] = depth;
                        }

                        foreach (SceneObject prefabObject in prefabObjectsById.Values.OrderBy(o => prefabDepths[o.Id]))
                        {
                            SceneTransform prefabTransform = prefabObject.Transform ?? new SceneTransform();
                            string? parentId = prefabTransform.ParentId;
                            SceneWorldState world;

                            if (string.IsNullOrWhiteSpace(parentId) || !prefabWorlds.TryGetValue(parentId, out SceneWorldState parentWorld))
                            {
                                world = new SceneWorldState(
                                    prefabTransform.LocalPosition,
                                    DQuaternion.FromEulerDegrees(prefabTransform.LocalRotation),
                                    prefabTransform.LocalScale);
                            }
                            else
                            {
                                Double3 scaledLocalPosition = Double3.Multiply(prefabTransform.LocalPosition, parentWorld.Scale);
                                Double3 rotatedLocalPosition = parentWorld.Rotation.Rotate(scaledLocalPosition);

                                world = new SceneWorldState(
                                    parentWorld.Position + rotatedLocalPosition,
                                    (parentWorld.Rotation * DQuaternion.FromEulerDegrees(prefabTransform.LocalRotation)).Normalized(),
                                    Double3.Multiply(parentWorld.Scale, prefabTransform.LocalScale));
                            }

                            prefabWorlds[prefabObject.Id] = world;

                            if (!prefabObject.Active || !prefabObject.Visible || string.IsNullOrWhiteSpace(prefabObject.Mesh))
                                continue;

                            if (string.Equals(prefabObject.Type, "Camera", StringComparison.Ordinal) ||
                                string.Equals(prefabObject.Type, "Light", StringComparison.Ordinal))
                                continue;

                            snapshots.Add(new SceneRenderObjectSnapshot
                            {
                                SceneId = AssetPreviewSceneId,
                                ObjectId = prefabObject.Id,
                                Type = prefabObject.Type,
                                AvatarId = Scene.ParseAvatarId(prefabObject),
                                Active = true,
                                Visible = true,
                                Mesh = prefabObject.Mesh,
                                Materials = prefabObject.Materials,
                                LodSettings = null,
                                RenderTag = prefabObject.RenderTag,
                                WorldPosition = world.Position,
                                WorldRotation = world.Rotation,
                                WorldScale = world.Scale,
                                StaticRenderEligible = false,
                                TransformRevision = 0
                            });
                        }

                        if (snapshots.Count == 0)
                            return null;

                        break;
                    }

                case EditorHostBridge.AssetPreviewKind.Avatar:
                case EditorHostBridge.AssetPreviewKind.AnimationClip:
                    {
                        if (string.IsNullOrWhiteSpace(request.AvatarMeshKey))
                            return null;

                        if (request.Kind == EditorHostBridge.AssetPreviewKind.AnimationClip)
                        {
                            if (string.IsNullOrWhiteSpace(request.AssetFilePath) || !File.Exists(request.AssetFilePath))
                                return null;

                            if (string.Equals(Path.GetExtension(request.AssetFilePath), ".vrma", StringComparison.OrdinalIgnoreCase))
                            {
                                if (!VrmaConverter.TryConvert(request.AssetFilePath, out AnimationClipData? convertedClip, out _))
                                    return null;

                                clipData = convertedClip;
                            }
                            else if (!AnimationJson.TryLoadClip(request.AssetFilePath, out AnimationClipData? loadedClip, out _))
                            {
                                return null;
                            }
                            else
                            {
                                clipData = loadedClip;
                            }
                        }

                        snapshots.Add(new SceneRenderObjectSnapshot
                        {
                            SceneId = AssetPreviewSceneId,
                            ObjectId = AssetPreviewMeshObjectId,
                            Type = "Avatar",
                            AvatarId = AssetPreviewMeshObjectId,
                            Active = true,
                            Visible = true,
                            Mesh = request.AvatarMeshKey,
                            WorldScale = Double3.One,
                            WorldRotation = DQuaternion.Identity,
                            StaticRenderEligible = false,
                            TransformRevision = 0
                        });
                        break;
                    }

                default:
                    return null;
            }

            Vector3 focusMin = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            Vector3 focusMax = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            bool hasFocusBounds = false;

            foreach (SceneRenderObjectSnapshot snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.Mesh))
                    continue;

                if (!_meshes.TryGetValue(snapshot.Mesh, out MeshData focusMesh))
                {
                    if (snapshot.Mesh.EndsWith(".vrm", StringComparison.OrdinalIgnoreCase))
                        Program.EnsureVrmRegistered(snapshot.Mesh, this);
                    else
                        Program.EnsureObjMeshRegistered(snapshot.Mesh, this);

                    if (!_meshes.TryGetValue(snapshot.Mesh, out focusMesh))
                        continue;
                }

                Matrix4x4 focusModel =
                    CreateSceneObjectScaleRotationMatrix(snapshot) *
                    Matrix4x4.CreateTranslation(ToRenderWorldPosition(snapshot.WorldPosition));
                Vector3 focusCenterWorld = Vector3.Transform(focusMesh.BoundingSphereCenter, focusModel);
                float focusScale = MathF.Max(
                    MathF.Abs((float)snapshot.WorldScale.X),
                    MathF.Max(MathF.Abs((float)snapshot.WorldScale.Y), MathF.Abs((float)snapshot.WorldScale.Z)));
                float focusRadius = focusMesh.BoundingSphereRadius * focusScale;

                focusMin = Vector3.Min(focusMin, focusCenterWorld - new Vector3(focusRadius));
                focusMax = Vector3.Max(focusMax, focusCenterWorld + new Vector3(focusRadius));
                hasFocusBounds = true;
            }

            if (!hasFocusBounds)
                return null;

            Vector3 focusCenterRender = (focusMin + focusMax) * 0.5f;
            double focusRadiusWorld = MathF.Max(0.01f, (focusMax - focusMin).Length() * 0.5f);
            Double3 focusCenter = new Double3(focusCenterRender.X, focusCenterRender.Y, -focusCenterRender.Z);

            if (request.Kind == EditorHostBridge.AssetPreviewKind.Material)
                focusRadiusWorld = 0.55;

            double pitchRadians = viewPitchDegrees * Math.PI / 180.0;
            double yawRadians = 180.0 * Math.PI / 180.0;
            double forwardX = Math.Sin(yawRadians) * Math.Cos(pitchRadians);
            double forwardY = -Math.Sin(pitchRadians);
            double forwardZ = Math.Cos(yawRadians) * Math.Cos(pitchRadians);
            double sideX = Math.Cos(yawRadians);
            double sideY = Math.Sin(yawRadians) * Math.Sin(pitchRadians);
            double engineYaw = Math.Asin(Math.Clamp(forwardX, -1.0, 1.0));
            double enginePitch = Math.Atan2(-forwardY, forwardZ);
            double engineRoll = Math.Atan2(-sideY, sideX);
            DQuaternion cameraRotation = DQuaternion.FromEulerDegrees(new Double3(
                enginePitch * 180.0 / Math.PI,
                engineYaw * 180.0 / Math.PI,
                engineRoll * 180.0 / Math.PI));
            double cameraDistance = focusRadiusWorld / Math.Sin(fieldOfViewDegrees * Math.PI / 360.0) * 1.15;
            Double3 cameraPosition = new Double3(
                focusCenter.X - forwardX * cameraDistance,
                focusCenter.Y - forwardY * cameraDistance,
                focusCenter.Z - forwardZ * cameraDistance);

            uint previousProgram = _currentProgram;
            RenderSpace previousRenderSpace = _activeRenderSpace;
            Matrix4x4 previousModel = _activeModelMatrix;
            Matrix4x4 previousView = _activeViewMatrix;
            Matrix4x4 previousProjection = _activeProjectionMatrix;

            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFramebuffer);
            _gl.GetInteger(GLEnum.DrawFramebufferBinding, out int previousDrawFramebuffer);
            _gl.GetInteger(GLEnum.ReadFramebufferBinding, out int previousReadFramebuffer);
            _gl.GetInteger(GLEnum.CurrentProgram, out int previousGlProgram);

            int[] previousViewport = new int[4];
            int[] previousScissorBox = new int[4];
            _gl.GetInteger(GLEnum.Viewport, previousViewport);
            _gl.GetInteger(GLEnum.ScissorBox, previousScissorBox);

            bool previousScissorEnabled = _gl.IsEnabled(GLEnum.ScissorTest);
            bool previousBlendEnabled = _gl.IsEnabled(GLEnum.Blend);
            bool previousDepthTestEnabled = _gl.IsEnabled(GLEnum.DepthTest);
            bool previousCullFaceEnabled = _gl.IsEnabled(GLEnum.CullFace);

            _gl.GetInteger(GLEnum.DepthFunc, out int previousDepthFunc);
            _gl.GetInteger(GLEnum.CullFaceMode, out int previousCullFaceMode);
            _gl.GetInteger(GLEnum.FrontFace, out int previousFrontFace);

            bool[] previousColorMask = new bool[4];
            _gl.GetBoolean(GLEnum.ColorWritemask, previousColorMask);

            _gl.GetBoolean(GLEnum.DepthWritemask, out bool previousDepthMask);

            float[] previousClearColor = new float[4];
            _gl.GetFloat(GLEnum.ColorClearValue, previousClearColor);

            _gl.GetDouble(GLEnum.DepthClearValue, out double previousClearDepth);

            _gl.GetInteger(GLEnum.ActiveTexture, out int previousActiveTexture);
            _gl.GetInteger(GLEnum.ArrayBufferBinding, out int previousArrayBuffer);
            _gl.GetInteger(GLEnum.VertexArrayBinding, out int previousVertexArray);

            _gl.GetInteger(GLEnum.ReadBuffer, out int previousReadBuffer);
            _gl.GetInteger(GLEnum.DrawBuffer, out int previousDrawBuffer);

            bool previousGizmoVisible = _gizmoVisible;
            Matrix4x4 previousGizmoView = _gizmoSceneView;
            Matrix4x4 previousGizmoProjection = _gizmoSceneProjection;
            Double3 previousGizmoCameraWorldPos = _gizmoSceneCameraWorldPos;
            bool previousGizmoSceneCameraSet = _gizmoSceneCameraSet;
            Vector4 previousBackgroundColor = _backgroundColor;

            try
            {
                RemoveSceneCache(AssetPreviewSceneId);

                foreach (SceneRenderObjectSnapshot snapshot in snapshots)
                    UpsertSceneObject(snapshot);

                if (clipData != null)
                {
                    clipAvatar = AvatarRegistry.GetAll().FirstOrDefault(
                        a => string.Equals(a.SceneId, AssetPreviewSceneId, StringComparison.Ordinal));

                    if (clipAvatar == null)
                        clipData = null;
                }

                UpsertSceneLight(new SceneRenderLightSnapshot
                {
                    SceneId = AssetPreviewSceneId,
                    ObjectId = AssetPreviewKeyLightObjectId,
                    Settings = new LightRenderSettings
                    {
                        LightMode = (int)LightKind.Directional,
                        Color = new Double3(1.0, 1.0, 1.0),
                        Intensity = 1.0,
                        CastShadow = false
                    },
                    World = new SceneWorldState(
                        Double3.Zero,
                        DQuaternion.FromEulerDegrees(new Double3(-40.0, 35.0, 0.0)),
                        Double3.One),
                    Active = true,
                    Visible = true
                });

                UpsertSceneLight(new SceneRenderLightSnapshot
                {
                    SceneId = AssetPreviewSceneId,
                    ObjectId = AssetPreviewFillLightObjectId,
                    Settings = new LightRenderSettings
                    {
                        LightMode = (int)LightKind.Directional,
                        Color = new Double3(1.0, 1.0, 1.0),
                        Intensity = 0.35,
                        CastShadow = false
                    },
                    World = new SceneWorldState(
                        Double3.Zero,
                        DQuaternion.FromEulerDegrees(new Double3(15.0, -40.0, 0.0)),
                        Double3.One),
                    Active = true,
                    Visible = true
                });

                ReplaceSceneCameras(AssetPreviewSceneId, new List<SceneRenderCameraSnapshot>
                {
                    new SceneRenderCameraSnapshot
                    {
                        SceneId = AssetPreviewSceneId,
                        ObjectId = AssetPreviewCameraObjectId,
                        Settings = new CameraRenderSettings
                        {
                            FovOrSize = fieldOfViewDegrees,
                            NearClip = 0.01,
                            FarClip = 1000.0,
                            ProjectionType = 0
                        },
                        SubmissionOrder = 0,
                        World = new SceneWorldState(cameraPosition, cameraRotation, Double3.One),
                        Active = true,
                        Visible = true
                    }
                });

                _gizmoVisible = false;
                _backgroundColor = new Vector4(0f, 0f, 0f, 0f);
                _assetPreviewViewportWidth = width;
                _assetPreviewViewportHeight = height;
                _scenePassTargetFramebuffer = _assetPreviewFramebuffer;

                if (clipData != null && clipAvatar != null)
                {
                    Avatar posedAvatar = clipAvatar;
                    AnimationClipData posedClip = clipData;
                    Dictionary<string, int> clipBoneNameToIndex = new Dictionary<string, int>(StringComparer.Ordinal);

                    for (int boneIndex = 0; boneIndex < posedAvatar.Skeleton.Bones.Count; boneIndex++)
                        clipBoneNameToIndex[posedAvatar.Skeleton.Bones[boneIndex].Name] = boneIndex;

                    Func<AnimationChannel, int> resolveBoneIndex = channel =>
                    {
                        if (string.Equals(channel.Binding, "humanoid", StringComparison.OrdinalIgnoreCase))
                            return posedAvatar.Rig.Bones.TryGetValue(channel.Path, out HumanoidBoneBinding? humanoidBinding)
                                ? humanoidBinding.BoneIndex
                                : -1;

                        return clipBoneNameToIndex.TryGetValue(channel.Path, out int boneIndex) ? boneIndex : -1;
                    };

                    Func<int, AnimationBoneRest> resolveBoneRest = boneIndex =>
                    {
                        if (boneIndex < 0 || boneIndex >= posedAvatar.Skeleton.Bones.Count)
                            return AnimationBoneRest.Identity;

                        AvatarBone restBone = posedAvatar.Skeleton.Bones[boneIndex];
                        DQuaternion worldRotation = DQuaternion.Identity;

                        if (restBone.RestWorldRotation.HasValue)
                        {
                            worldRotation = restBone.RestWorldRotation.Value;
                        }
                        else
                        {
                            AvatarBone? cursor = restBone;

                            while (cursor != null)
                            {
                                worldRotation = cursor.RestRotation * worldRotation;

                                if (cursor.ParentIndex < 0 || cursor.ParentIndex >= posedAvatar.Skeleton.Bones.Count)
                                    break;

                                cursor = posedAvatar.Skeleton.Bones[cursor.ParentIndex];
                            }
                        }

                        DQuaternion parentWorldRotation = DQuaternion.Identity;

                        if (restBone.ParentIndex >= 0 && restBone.ParentIndex < posedAvatar.Skeleton.Bones.Count)
                        {
                            AvatarBone parent = posedAvatar.Skeleton.Bones[restBone.ParentIndex];

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

                                    if (cursor.ParentIndex < 0 || cursor.ParentIndex >= posedAvatar.Skeleton.Bones.Count)
                                        break;

                                    cursor = posedAvatar.Skeleton.Bones[cursor.ParentIndex];
                                }
                            }
                        }

                        return new AnimationBoneRest(
                            restBone.RestPosition,
                            restBone.RestRotation,
                            restBone.RestScale,
                            worldRotation,
                            parentWorldRotation);
                    };

                    AnimationPoseBuffer clipPose = AnimationPoseBuffer.Create(posedAvatar.Skeleton.Bones.Count);
                    clipPose.ResetToRest();

                    AnimationClipRuntime clipRuntime = AnimationClipRuntime.Create(request.RequestKey, posedClip);
                    clipRuntime.Sample(clipRuntime.Duration * 0.5, false, clipPose, resolveBoneIndex, resolveBoneRest);

                    for (int boneIndex = 0; boneIndex < clipPose.BoneCount && boneIndex < posedAvatar.Skeleton.Bones.Count; boneIndex++)
                    {
                        if (!clipPose.Touched[boneIndex])
                            continue;

                        AvatarBone poseBone = posedAvatar.Skeleton.Bones[boneIndex];

                        if (clipPose.TouchedTranslations[boneIndex])
                            poseBone.LocalPosition = clipPose.Translations[boneIndex];

                        if (clipPose.TouchedRotations[boneIndex])
                        {
                            poseBone.LocalRotationQuaternion = clipPose.Rotations[boneIndex];
                            poseBone.LocalRotation = clipPose.Rotations[boneIndex].ToEulerDegrees();
                        }

                        if (clipPose.TouchedScales[boneIndex])
                            poseBone.LocalScale = clipPose.Scales[boneIndex];
                    }
                }

                UploadAvatarBoneMatrices();

                QueueLoadedSceneRender(AssetPreviewSceneId);

                _sceneCommandsScratch.Clear();

                for (int i = 0; i < _renderQueue.Count; i++)
                {
                    if (_renderQueue[i].Pass == RenderPass.Scene)
                        _sceneCommandsScratch.Add(_renderQueue[i]);
                }

                _renderQueue.Clear();

                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _assetPreviewFramebuffer);
                _gl.Viewport(0, 0, (uint)width, (uint)height);
                _gl.ColorMask(true, true, true, true);
                _gl.DepthMask(true);
                _gl.ClearColor(0f, 0f, 0f, 0f);
                _gl.ClearDepth(1.0);
                _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);

                if (_sceneCommandsScratch.Count > 0)
                    ExecuteScenePass(_sceneCommandsScratch);

                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _assetPreviewFramebuffer);
                _gl.ReadBuffer(GLEnum.ColorAttachment0);

                byte[] pixels = new byte[width * height * 4];

                _gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                _gl.ReadPixels<byte>(
                    0,
                    0,
                    (uint)width,
                    (uint)height,
                    GLEnum.Rgba,
                    GLEnum.UnsignedByte,
                    pixels);

                return new EditorRenderedFrame
                {
                    Width = width,
                    Height = height,
                    PixelsRgba = pixels
                };
            }
            finally
            {
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)previousDrawFramebuffer);
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)previousReadFramebuffer);

                if (previousDrawFramebuffer == previousReadFramebuffer)
                    _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer);

                _gl.Viewport(
                    previousViewport[0],
                    previousViewport[1],
                    (uint)Math.Max(1, previousViewport[2]),
                    (uint)Math.Max(1, previousViewport[3]));

                _gl.Scissor(
                    previousScissorBox[0],
                    previousScissorBox[1],
                    (uint)Math.Max(1, previousScissorBox[2]),
                    (uint)Math.Max(1, previousScissorBox[3]));

                if (previousScissorEnabled)
                    _gl.Enable(GLEnum.ScissorTest);
                else
                    _gl.Disable(GLEnum.ScissorTest);

                if (previousBlendEnabled)
                    _gl.Enable(GLEnum.Blend);
                else
                    _gl.Disable(GLEnum.Blend);

                if (previousDepthTestEnabled)
                    _gl.Enable(GLEnum.DepthTest);
                else
                    _gl.Disable(GLEnum.DepthTest);

                if (previousCullFaceEnabled)
                    _gl.Enable(GLEnum.CullFace);
                else
                    _gl.Disable(GLEnum.CullFace);

                _gl.DepthFunc((GLEnum)previousDepthFunc);
                _gl.CullFace((GLEnum)previousCullFaceMode);
                _gl.FrontFace((GLEnum)previousFrontFace);

                _gl.ColorMask(
                    previousColorMask[0],
                    previousColorMask[1],
                    previousColorMask[2],
                    previousColorMask[3]);

                _gl.DepthMask(previousDepthMask);

                _gl.ClearColor(
                    previousClearColor[0],
                    previousClearColor[1],
                    previousClearColor[2],
                    previousClearColor[3]);

                _gl.ClearDepth(previousClearDepth);

                try
                {
                    _gl.DrawBuffer((GLEnum)previousDrawBuffer);
                }
                catch
                {
                }

                try
                {
                    _gl.ReadBuffer((GLEnum)previousReadBuffer);
                }
                catch
                {
                }

                _currentProgram = previousProgram;
                _gl.UseProgram((uint)previousGlProgram);

                _activeRenderSpace = previousRenderSpace;
                _activeModelMatrix = previousModel;
                _activeViewMatrix = previousView;
                _activeProjectionMatrix = previousProjection;

                _gl.ActiveTexture((TextureUnit)previousActiveTexture);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, (uint)previousArrayBuffer);
                _gl.BindVertexArray((uint)previousVertexArray);

                _gizmoVisible = previousGizmoVisible;
                _gizmoSceneView = previousGizmoView;
                _gizmoSceneProjection = previousGizmoProjection;
                _gizmoSceneCameraWorldPos = previousGizmoCameraWorldPos;
                _gizmoSceneCameraSet = previousGizmoSceneCameraSet;
                _backgroundColor = previousBackgroundColor;
                _assetPreviewViewportWidth = 0;
                _assetPreviewViewportHeight = 0;
                _scenePassTargetFramebuffer = 0;

                ClearCameraPostProcess(AssetPreviewCameraObjectId);
                RemoveSceneCache(AssetPreviewSceneId);
            }
        }
    }
}
