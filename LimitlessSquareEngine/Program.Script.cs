using LimitlessSquareEngine.Engine;
using MoonSharp.Interpreter;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace LimitlessSquareEngine
{
    internal partial class Program
    {
        static readonly Dictionary<string, LuaScriptInstance> _busScriptByName = new Dictionary<string, LuaScriptInstance>(StringComparer.Ordinal);
        static readonly List<LuaScriptInstance> _sceneScriptInstances = new List<LuaScriptInstance>();
        static readonly Dictionary<string, List<LuaScriptInstance>> _sceneScriptsByScene = new Dictionary<string, List<LuaScriptInstance>>(StringComparer.Ordinal);
        static readonly List<string> _sceneScriptOrder = new List<string>();
        static readonly List<LuaScriptInstance> _pendingComponentInits = new List<LuaScriptInstance>();
        static readonly ConcurrentQueue<Action> _busCommandQueue = new ConcurrentQueue<Action>();
        internal static readonly Dictionary<string, string> _scriptFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal static GameData? _gameData;
        internal static Func<string, int>? _submitTaskFunc;
        internal static Func<int, DynValue[]>? _getTaskResultFunc;
        static bool _isBusScriptPhase = false;

        static bool IsBusScriptFile(string filePath)
        {
            try
            {
                foreach (string rawLine in File.ReadAllLines(filePath))
                {
                    string line = rawLine.Trim();

                    if (line.Length == 0)
                        continue;

                    if (line.StartsWith("--@bus", StringComparison.Ordinal))
                        return true;

                    if (line.StartsWith("--", StringComparison.Ordinal))
                        continue;

                    return false;
                }
            }
            catch
            {
            }

            return false;
        }

        internal static bool EnsureScriptRegistered(string scriptKey, out string filePath)
        {
            filePath = string.Empty;

            string key = NormalizeAssetLookupKey(scriptKey, removeExtension: true);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_scriptFileRegistry.TryGetValue(key, out filePath!))
                return File.Exists(filePath);

            lock (_assetDiscoveryLock)
            {
                if (_scriptFileRegistry.TryGetValue(key, out filePath!))
                    return File.Exists(filePath);

                string candidate = GetAssetPathFromKey(key, ".lua");
                if (!File.Exists(candidate))
                    return false;

                _scriptFileRegistry[key] = candidate;
                filePath = candidate;
                return true;
            }
        }

        static void BindScriptSymbols(LuaScriptInstance instance)
        {
            bool isComponent =
                !string.IsNullOrWhiteSpace(instance.SceneId) &&
                !string.IsNullOrWhiteSpace(instance.ObjectId);

            RecordLuaApi("bus", "bus", "总线寻址入口", "Bus");
            RecordLuaApi("scene", "scene", "场景寻址入口", "Bus");

            instance.LuaScript.Globals["bus"] = new LuaBusProxy();
            instance.LuaScript.Globals["scene"] = new LuaSceneProxy(instance.LuaScript, null);

            if (!isComponent)
            {
                instance.LuaScript.Globals["thisObject"] = CreateUnboundSymbol(instance.LuaScript, "thisObject");
                instance.LuaScript.Globals["thisScene"] = CreateUnboundSymbol(instance.LuaScript, "thisScene");
                return;
            }

            RecordLuaApi("thisObject", "thisObject", "当前对象，仅挂载脚本可用", "Component");
            RecordLuaApi("thisScene", "thisScene", "当前场景，仅挂载脚本可用", "Component");

            instance.LuaScript.Globals["thisObject"] = new LuaObjectProxy(
                instance.LuaScript,
                instance.SceneId!,
                instance.ObjectId!);

            instance.LuaScript.Globals["thisScene"] = new LuaSceneProxy(
                instance.LuaScript,
                instance.SceneId!);
        }

        static DynValue CreateUnboundSymbol(Script script, string symbolName)
        {
            Table table = new Table(script);
            Table meta = new Table(script);

            meta["__index"] = DynValue.NewCallback((context, args) =>
            {
                throw new ScriptRuntimeException(
                    $"[X] '{symbolName}' is bound to the bus, not to any scene or object.");
            });

            table.MetaTable = meta;
            return DynValue.NewTable(table);
        }

        static void BindLuaGlobals(
            LuaScriptInstance instance,
            GameData gameData,
            Graphics graphics,
            Func<string, int> submitTaskFunc,
            Func<int, DynValue[]> getTaskResultFunc)
        {
            // 注入数据
            instance.LuaScript.Globals["game_data"] = gameData;
            RecordLuaApi("game_data", "game_data", "游戏数据对象", "Data");
            instance.LuaScript.Globals["graphics"] = graphics;
            RecordLuaApi("graphics", "graphics", "图形系统对象", "Graphics");
            // 注入后台任务函数
            instance.LuaScript.Globals["submit_task"] = submitTaskFunc;
            RecordLuaApi("submit_task", "submit_task(luaCode) -> taskId", "提交后台任务", "Task");
            instance.LuaScript.Globals["get_task_result"] = getTaskResultFunc;
            RecordLuaApi("get_task_result", "get_task_result(taskId) -> result", "获取后台任务结果", "Task");
            // 注入图形系统
            // 注入打印函数
            instance.LuaScript.Globals["print"] = (Action<object>)((obj) => Console.Write(obj));
            RecordLuaApi("print", "print(object)", "输出文本到控制台", "Core");
            // 注入UI设置函数
            instance.LuaScript.Globals["set_ui"] = (Action<string>)((layoutKey) =>
            {
                SetActiveUILayout(layoutKey);
            });
            RecordLuaApi("set_ui", "set_ui(layoutKey)", "设置激活的UI布局", "UI");
            // 注入UI清空函数
            instance.LuaScript.Globals["clear_ui"] = (Action)(() =>
            {
                ClearActiveUILayout();
            });
            RecordLuaApi("clear_ui", "clear_ui()", "清空当前UI布局", "UI");
            // 注入纹理路径表
            EnsureAllTextureKeysDiscovered();
            Table textureTable = new Table(instance.LuaScript);
            foreach (var path in _texturePaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                textureTable.Append(DynValue.NewString(path));
            instance.LuaScript.Globals["texture_paths"] = textureTable;
            RecordLuaApi("texture_paths", "texture_paths", "纹理路径数组", "Graphics");

            // 注入音频系统
            instance.LuaScript.Globals["audio_play"] = (Func<string, float, float, float, string>)((clipId, volume, pitch, pan) =>
            {
                if (_audio == null) return string.Empty;
                return _audio.PlayNonSpatial(clipId, volume, pitch, pan);
            });
            RecordLuaApi("audio_play", "audio_play(clipId, volume, pitch, pan) -> sourceId", "播放非空间化音频", "Audio");
            instance.LuaScript.Globals["audio_play_looping"] = (Func<string, float, float, float, string>)((clipId, volume, pitch, pan) =>
            {
                if (_audio == null) return string.Empty;
                return _audio.PlayNonSpatialLooping(clipId, volume, pitch, pan);
            });
            RecordLuaApi("audio_play_looping", "audio_play_looping(clipId, volume, pitch, pan) -> sourceId", "播放循环非空间化音频", "Audio");
            instance.LuaScript.Globals["audio_stop"] = (Action<string>)((sourceId) =>
            {
                _audio?.Stop(sourceId);
            });
            RecordLuaApi("audio_stop", "audio_stop(sourceId)", "停止音频播放", "Audio");
            instance.LuaScript.Globals["audio_pause"] = (Action<string>)((sourceId) =>
            {
                _audio?.Pause(sourceId);
            });
            RecordLuaApi("audio_pause", "audio_pause(sourceId)", "暂停音频播放", "Audio");
            instance.LuaScript.Globals["audio_resume"] = (Action<string>)((sourceId) =>
            {
                _audio?.Resume(sourceId);
            });
            RecordLuaApi("audio_resume", "audio_resume(sourceId)", "恢复音频播放", "Audio");
            instance.LuaScript.Globals["audio_set_volume"] = (Action<string, float>)((sourceId, volume) =>
            {
                _audio?.SetVolume(sourceId, volume);
            });
            RecordLuaApi("audio_set_volume", "audio_set_volume(sourceId, volume)", "设置音频音量", "Audio");
            instance.LuaScript.Globals["audio_set_pitch"] = (Action<string, float>)((sourceId, pitch) =>
            {
                _audio?.SetPitch(sourceId, pitch);
            });
            RecordLuaApi("audio_set_pitch", "audio_set_pitch(sourceId, pitch)", "设置音频音调", "Audio");
            instance.LuaScript.Globals["audio_set_pan"] = (Action<string, float>)((sourceId, pan) =>
            {
                _audio?.SetPan(sourceId, pan);
            });
            RecordLuaApi("audio_set_pan", "audio_set_pan(sourceId, pan)", "设置音频声道平衡", "Audio");
            instance.LuaScript.Globals["audio_play_source"] = (Action<string, string>)((sceneId, objectId) =>
            {
                _audio?.PlaySceneSource(sceneId, objectId);
            });
            RecordLuaApi("audio_play_source", "audio_play_source(sceneId, objectId)", "播放场景音频源", "Audio");
            instance.LuaScript.Globals["audio_stop_source"] = (Action<string, string>)((sceneId, objectId) =>
            {
                _audio?.StopSceneSource(sceneId, objectId);
            });
            RecordLuaApi("audio_stop_source", "audio_stop_source(sceneId, objectId)", "停止场景音频源", "Audio");
            instance.LuaScript.Globals["audio_set_position"] = (Action<string, float>)((sourceId, seconds) =>
            {
                _audio?.SetPosition(sourceId, seconds);
            });
            RecordLuaApi("audio_set_position", "audio_set_position(sourceId, seconds)", "设置音频播放位置", "Audio");
            instance.LuaScript.Globals["audio_get_position"] = (Func<string, float>)((sourceId) =>
            {
                if (_audio == null) return 0f;
                return _audio.GetPosition(sourceId);
            });
            RecordLuaApi("audio_get_position", "audio_get_position(sourceId) -> seconds", "获取音频播放位置", "Audio");
            instance.LuaScript.Globals["audio_is_playing"] = (Func<string, bool>)((sourceId) =>
            {
                if (_audio == null) return false;
                return _audio.IsPlaying(sourceId);
            });
            RecordLuaApi("audio_is_playing", "audio_is_playing(sourceId) -> bool", "检查音频是否正在播放", "Audio");
            instance.LuaScript.Globals["audio_register_clip"] = (Action<string, string>)((clipId, filePath) =>
            {
                _audio?.RegisterAudioFile(clipId, filePath);
            });
            RecordLuaApi("audio_register_clip", "audio_register_clip(clipId, filePath)", "注册音频文件", "Audio");
            instance.LuaScript.Globals["audio_unload_clip"] = (Action<string>)((clipId) =>
            {
                _audio?.UnloadClip(clipId);
            });
            RecordLuaApi("audio_unload_clip", "audio_unload_clip(clipId)", "卸载音频剪辑", "Audio");
            instance.LuaScript.Globals["audio_set_master_volume"] = (Action<float>)((volume) =>
            {
                _audio?.SetMasterVolume(volume);
            });
            RecordLuaApi("audio_set_master_volume", "audio_set_master_volume(volume)", "设置主音量", "Audio");
            instance.LuaScript.Globals["audio_get_clip_duration"] = (Func<string, float>)((clipId) =>
            {
                if (_audio == null) return 0f;
                return _audio.GetClipDuration(clipId);
            });
            RecordLuaApi("audio_get_clip_duration", "audio_get_clip_duration(clipId) -> duration", "获取音频剪辑时长", "Audio");

            // 注入场景加载函数
            instance.LuaScript.Globals["load_scene"] = (Func<string, SceneData>)((sceneId) =>
            {
                return Scene.LoadScene(sceneId);
            });
            RecordLuaApi("load_scene", "load_scene(sceneId) -> sceneData", "加载场景", "Scene");

            // 注入场景移除函数
            instance.LuaScript.Globals["remove_scene"] = (Action<string>)((sceneId) =>
            {
                Scene.RemoveScene(sceneId);
            });
            RecordLuaApi("remove_scene", "remove_scene(sceneId)", "移除场景", "Scene");

            // 注入雾设置函数
            instance.LuaScript.Globals["set_camera_fog_enabled"] = (Action<string, bool>)((cameraObjectId, enabled) =>
            {
                graphics.SetCameraFogEnabled(cameraObjectId, enabled);
            });
            RecordLuaApi("set_camera_fog_enabled", "set_camera_fog_enabled(cameraObjectId, enabled)", "设置相机雾效开关", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_enabled"] = (Action<bool>)((enabled) =>
            {
                graphics.SetMainCameraFogEnabled(enabled);
            });
            RecordLuaApi("set_main_camera_fog_enabled", "set_main_camera_fog_enabled(enabled)", "设置主相机雾效开关", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_mode"] = (Action<string, string>)((cameraObjectId, mode) =>
            {
                graphics.SetCameraFogMode(cameraObjectId, mode);
            });
            RecordLuaApi("set_camera_fog_mode", "set_camera_fog_mode(cameraObjectId, mode)", "设置相机雾效模式", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_mode"] = (Action<string>)((mode) =>
            {
                graphics.SetMainCameraFogMode(mode);
            });
            RecordLuaApi("set_main_camera_fog_mode", "set_main_camera_fog_mode(mode)", "设置主相机雾效模式", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_color"] = (Action<string, double, double, double, double>)((cameraObjectId, r, g, b, a) =>
            {
                graphics.SetCameraFogColor(cameraObjectId, (float)r, (float)g, (float)b, (float)a);
            });
            RecordLuaApi("set_camera_fog_color", "set_camera_fog_color(cameraObjectId, r, g, b, a)", "设置相机雾效颜色", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_color_rgb"] = (Action<string, int, int, int, int>)((cameraObjectId, r, g, b, a) =>
            {
                graphics.SetCameraFogColorRGB(cameraObjectId, r, g, b, a);
            });
            RecordLuaApi("set_camera_fog_color_rgb", "set_camera_fog_color_rgb(cameraObjectId, r, g, b, a)", "设置相机雾效颜色(RGB 0-255)", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_color"] = (Action<double, double, double, double>)((r, g, b, a) =>
            {
                graphics.SetMainCameraFogColor((float)r, (float)g, (float)b, (float)a);
            });
            RecordLuaApi("set_main_camera_fog_color", "set_main_camera_fog_color(r, g, b, a)", "设置主相机雾效颜色", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_color_rgb"] = (Action<int, int, int, int>)((r, g, b, a) =>
            {
                graphics.SetMainCameraFogColorRGB(r, g, b, a);
            });
            RecordLuaApi("set_main_camera_fog_color_rgb", "set_main_camera_fog_color_rgb(r, g, b, a)", "设置主相机雾效颜色(RGB 0-255)", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_texture"] = (Action<string, string>)((cameraObjectId, texturePath) =>
            {
                graphics.SetCameraFogCylindricalTexture(cameraObjectId, texturePath);
            });
            RecordLuaApi("set_camera_fog_texture", "set_camera_fog_texture(cameraObjectId, texturePath)", "设置相机雾效纹理", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_texture"] = (Action<string>)((texturePath) =>
            {
                graphics.SetMainCameraFogCylindricalTexture(texturePath);
            });
            RecordLuaApi("set_main_camera_fog_texture", "set_main_camera_fog_texture(texturePath)", "设置主相机雾效纹理", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_edge_transition_to_skybox"] = (Action<string, bool>)((cameraObjectId, enabled) =>
            {
                graphics.SetCameraFogEdgeTransitionToSkybox(cameraObjectId, enabled);
            });
            RecordLuaApi("set_camera_fog_edge_transition_to_skybox", "set_camera_fog_edge_transition_to_skybox(cameraObjectId, enabled)", "设置雾效边缘过渡到天空盒", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_edge_transition_to_skybox"] = (Action<bool>)((enabled) =>
            {
                graphics.SetMainCameraFogEdgeTransitionToSkybox(enabled);
            });
            RecordLuaApi("set_main_camera_fog_edge_transition_to_skybox", "set_main_camera_fog_edge_transition_to_skybox(enabled)", "设置主相机雾效边缘过渡", "CameraFog");

            instance.LuaScript.Globals["set_camera_fog_range"] = (Action<string, double, double>)((cameraObjectId, start, end) =>
            {
                graphics.SetCameraFogRange(cameraObjectId, (float)start, (float)end);
            });
            RecordLuaApi("set_camera_fog_range", "set_camera_fog_range(cameraObjectId, start, end)", "设置相机雾效范围", "CameraFog");

            instance.LuaScript.Globals["set_main_camera_fog_range"] = (Action<double, double>)((start, end) =>
            {
                graphics.SetMainCameraFogRange((float)start, (float)end);
            });
            RecordLuaApi("set_main_camera_fog_range", "set_main_camera_fog_range(start, end)", "设置主相机雾效范围", "CameraFog");

            instance.LuaScript.Globals["clear_camera_fog"] = (Action<string>)((cameraObjectId) =>
            {
                graphics.ClearCameraFog(cameraObjectId);
            });
            RecordLuaApi("clear_camera_fog", "clear_camera_fog(cameraObjectId)", "清除相机雾效", "CameraFog");

            // 注入刚体速度读取函数
            instance.LuaScript.Globals["get_rigidbody_velocity"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Physics.GetVelocity(sceneId, objectId);
                });
            RecordLuaApi("get_rigidbody_velocity", "get_rigidbody_velocity(sceneId, objectId) -> Double3", "获取刚体速度", "Physics");

            // 注入刚体速度设置函数
            instance.LuaScript.Globals["set_rigidbody_velocity"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Physics.SetVelocity(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_rigidbody_velocity", "set_rigidbody_velocity(sceneId, objectId, x, y, z)", "设置刚体速度", "Physics");

            // 注入刚体角速度读取函数
            instance.LuaScript.Globals["get_rigidbody_angular_velocity"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Physics.GetAngularVelocity(sceneId, objectId);
                });
            RecordLuaApi("get_rigidbody_angular_velocity", "get_rigidbody_angular_velocity(sceneId, objectId) -> Double3", "获取刚体角速度", "Physics");

            // 注入刚体角速度设置函数
            instance.LuaScript.Globals["set_rigidbody_angular_velocity"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Physics.SetAngularVelocity(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_rigidbody_angular_velocity", "set_rigidbody_angular_velocity(sceneId, objectId, x, y, z)", "设置刚体角速度", "Physics");

            // 注入力函数
            instance.LuaScript.Globals["add_rigidbody_force"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Physics.AddForce(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("add_rigidbody_force", "add_rigidbody_force(sceneId, objectId, x, y, z)", "对刚体施加力", "Physics");

            // 注入作用点力函数
            instance.LuaScript.Globals["add_rigidbody_force_at_position"] =
                (Action<string, string, double, double, double, double, double, double>)((sceneId, objectId, fx, fy, fz, px, py, pz) =>
                {
                    Physics.AddForceAtPosition(
                        sceneId,
                        objectId,
                        new Double3(fx, fy, fz),
                        new Double3(px, py, pz));
                });
            RecordLuaApi("add_rigidbody_force_at_position", "add_rigidbody_force_at_position(sceneId, objectId, fx, fy, fz, px, py, pz)", "在指定位置对刚体施加力", "Physics");

            // 注入冲量函数
            instance.LuaScript.Globals["add_rigidbody_impulse"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Physics.ApplyImpulse(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("add_rigidbody_impulse", "add_rigidbody_impulse(sceneId, objectId, x, y, z)", "对刚体施加冲量", "Physics");

            // 注入作用点冲量函数
            instance.LuaScript.Globals["add_rigidbody_impulse_at_position"] =
                (Action<string, string, double, double, double, double, double, double>)((sceneId, objectId, ix, iy, iz, px, py, pz) =>
                {
                    Physics.ApplyImpulseAtPosition(
                        sceneId,
                        objectId,
                        new Double3(ix, iy, iz),
                        new Double3(px, py, pz));
                });
            RecordLuaApi("add_rigidbody_impulse_at_position", "add_rigidbody_impulse_at_position(sceneId, objectId, ix, iy, iz, px, py, pz)", "在指定位置对刚体施加冲量", "Physics");

            // 注入刚体激活设置函数
            instance.LuaScript.Globals["set_rigidbody_active"] =
                (Action<string, string, bool>)((sceneId, objectId, active) =>
                {
                    Physics.SetActivationState(sceneId, objectId, active);
                });
            RecordLuaApi("set_rigidbody_active", "set_rigidbody_active(sceneId, objectId, active)", "设置刚体激活状态", "Physics");

            // 注入射线检测函数
            instance.LuaScript.Globals["raycast"] =
                (Func<string, double, double, double, double, double, double, double, PhysicsRaycastHit?>)((sceneId, ox, oy, oz, dx, dy, dz, maxDistance) =>
                {
                    return Physics.Raycast(
                        sceneId,
                        new Double3(ox, oy, oz),
                        new Double3(dx, dy, dz),
                        maxDistance);
                });
            RecordLuaApi("raycast", "raycast(sceneId, ox, oy, oz, dx, dy, dz, maxDistance) -> hit", "射线检测", "Physics");

            // 注入重建摄像机队列函数
            instance.LuaScript.Globals["rescan_scene_cameras"] = (Action<string>)((sceneId) =>
            {
                Scene.RebuildCameraQueue(sceneId);
            });
            RecordLuaApi("rescan_scene_cameras", "rescan_scene_cameras(sceneId)", "重建场景摄像机队列", "Camera");

            // 注入相机渲染模式设置函数
            instance.LuaScript.Globals["set_camera_render_mode"] =
                (Action<string, string, int>)((sceneId, objectId, renderMode) =>
                {
                    Scene.SetCameraRenderMode(sceneId, objectId, renderMode);
                });
            RecordLuaApi("set_camera_render_mode", "set_camera_render_mode(sceneId, objectId, renderMode)", "设置相机渲染模式", "Camera");

            // 注入相机视角或正交尺寸设置函数
            instance.LuaScript.Globals["set_camera_fov_or_size"] =
                (Action<string, string, double>)((sceneId, objectId, fovOrSize) =>
                {
                    Scene.SetCameraFovOrSize(sceneId, objectId, fovOrSize);
                });
            RecordLuaApi("set_camera_fov_or_size", "set_camera_fov_or_size(sceneId, objectId, fovOrSize)", "设置相机FOV或正交尺寸", "Camera");

            // 注入相机渲染面偏移设置函数
            instance.LuaScript.Globals["set_camera_render_plane_offset"] =
                (Action<string, string, double, double>)((sceneId, objectId, x, y) =>
                {
                    Scene.SetCameraRenderPlaneOffset(sceneId, objectId, x, y);
                });
            RecordLuaApi("set_camera_render_plane_offset", "set_camera_render_plane_offset(sceneId, objectId, x, y)", "设置相机渲染面偏移", "Camera");

            // 注入相机近裁剪面设置函数
            instance.LuaScript.Globals["set_camera_near_clip"] =
                (Action<string, string, double>)((sceneId, objectId, nearClip) =>
                {
                    Scene.SetCameraNearClip(sceneId, objectId, nearClip);
                });
            RecordLuaApi("set_camera_near_clip", "set_camera_near_clip(sceneId, objectId, nearClip)", "设置相机近裁剪面", "Camera");

            // 注入相机远裁剪面设置函数
            instance.LuaScript.Globals["set_camera_far_clip"] =
                (Action<string, string, double>)((sceneId, objectId, farClip) =>
                {
                    Scene.SetCameraFarClip(sceneId, objectId, farClip);
                });
            RecordLuaApi("set_camera_far_clip", "set_camera_far_clip(sceneId, objectId, farClip)", "设置相机远裁剪面", "Camera");

            // 注入相机裁剪范围设置函数
            instance.LuaScript.Globals["set_camera_clip_range"] =
                (Action<string, string, double, double>)((sceneId, objectId, nearClip, farClip) =>
                {
                    Scene.SetCameraClipRange(sceneId, objectId, nearClip, farClip);
                });
            RecordLuaApi("set_camera_clip_range", "set_camera_clip_range(sceneId, objectId, nearClip, farClip)", "设置相机裁剪范围", "Camera");

            // 注入相机投影类型设置函数
            instance.LuaScript.Globals["set_camera_projection_type"] =
                (Action<string, string, int>)((sceneId, objectId, projectionType) =>
                {
                    Scene.SetCameraProjectionType(sceneId, objectId, projectionType);
                });
            RecordLuaApi("set_camera_projection_type", "set_camera_projection_type(sceneId, objectId, projectionType)", "设置相机投影类型", "Camera");

            // 注入主相机标记设置函数
            instance.LuaScript.Globals["set_camera_main"] =
                (Action<string, string, bool>)((sceneId, objectId, isMainCamera) =>
                {
                    Scene.SetCameraMain(sceneId, objectId, isMainCamera);
                });
            RecordLuaApi("set_camera_main", "set_camera_main(sceneId, objectId, isMainCamera)", "设置主相机标记", "Camera");

            // 注入相机后处理总开关设置函数
            instance.LuaScript.Globals["set_camera_post_process_enabled"] =
                (Action<string, string, bool>)((sceneId, objectId, enabled) =>
                {
                    Scene.SetCameraPostProcessEnabled(sceneId, objectId, enabled);
                });
            RecordLuaApi("set_camera_post_process_enabled", "set_camera_post_process_enabled(sceneId, objectId, enabled)", "设置相机后处理开关", "PostProcess");

            // 注入相机亮度后处理设置函数
            instance.LuaScript.Globals["set_camera_post_brightness"] =
                (Action<string, string, bool, double>)((sceneId, objectId, enabled, value) =>
                {
                    Scene.SetCameraPostBrightness(sceneId, objectId, enabled, value);
                });
            RecordLuaApi("set_camera_post_brightness", "set_camera_post_brightness(sceneId, objectId, enabled, value)", "设置相机后处理亮度", "PostProcess");

            // 注入相机对比度后处理设置函数
            instance.LuaScript.Globals["set_camera_post_contrast"] =
                (Action<string, string, bool, double>)((sceneId, objectId, enabled, value) =>
                {
                    Scene.SetCameraPostContrast(sceneId, objectId, enabled, value);
                });
            RecordLuaApi("set_camera_post_contrast", "set_camera_post_contrast(sceneId, objectId, enabled, value)", "设置相机后处理对比度", "PostProcess");

            // 注入相机饱和度后处理设置函数
            instance.LuaScript.Globals["set_camera_post_saturation"] =
                (Action<string, string, bool, double>)((sceneId, objectId, enabled, value) =>
                {
                    Scene.SetCameraPostSaturation(sceneId, objectId, enabled, value);
                });
            RecordLuaApi("set_camera_post_saturation", "set_camera_post_saturation(sceneId, objectId, enabled, value)", "设置相机后处理饱和度", "PostProcess");

            // 注入相机色相后处理设置函数
            instance.LuaScript.Globals["set_camera_post_hue"] =
                (Action<string, string, bool, double>)((sceneId, objectId, enabled, degrees) =>
                {
                    Scene.SetCameraPostHue(sceneId, objectId, enabled, degrees);
                });
            RecordLuaApi("set_camera_post_hue", "set_camera_post_hue(sceneId, objectId, enabled, degrees)", "设置相机后处理色相", "PostProcess");

            // 注入相机色温后处理设置函数
            instance.LuaScript.Globals["set_camera_post_temperature"] =
                (Action<string, string, bool, double>)((sceneId, objectId, enabled, value) =>
                {
                    Scene.SetCameraPostTemperature(sceneId, objectId, enabled, value);
                });
            RecordLuaApi("set_camera_post_temperature", "set_camera_post_temperature(sceneId, objectId, enabled, value)", "设置相机后处理色温", "PostProcess");

            // 注入相机泛光后处理设置函数
            instance.LuaScript.Globals["set_camera_bloom"] =
                (Action<string, string, bool, double, double, double, int, int, double>)((sceneId, objectId, enabled, threshold, softKnee, intensity, iterations, downsample, range) =>
                {
                    Scene.SetCameraBloom(sceneId, objectId, enabled, threshold, softKnee, intensity, iterations, downsample, range);
                });
            RecordLuaApi("set_camera_bloom", "set_camera_bloom(sceneId, objectId, enabled, threshold, softKnee, intensity, iterations, downsample, range)", "设置相机泛光效果", "PostProcess");

            // 注入天空盒设置函数
            instance.LuaScript.Globals["set_skybox"] = (Action<string, string>)((shaderName, parametersJson) =>
            {
                graphics.SetScreenSkybox(shaderName, parametersJson);
            });
            RecordLuaApi("set_skybox", "set_skybox(shaderName, parametersJson)", "设置天空盒", "Graphics");

            // 注入天空盒清除函数
            instance.LuaScript.Globals["clear_skybox"] = (Action)(() =>
            {
                graphics.ClearScreenSkybox();
            });
            RecordLuaApi("clear_skybox", "clear_skybox()", "清除天空盒", "Graphics");

            // Transform: set position
            instance.LuaScript.Globals["set_local_position"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetLocalPosition(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_local_position", "set_local_position(sceneId, objectId, x, y, z)", "设置局部坐标", "Transform");

            instance.LuaScript.Globals["set_position"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetPosition(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_position", "set_position(sceneId, objectId, x, y, z)", "设置世界坐标", "Transform");

            instance.LuaScript.Globals["alter_local_position"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterLocalPosition(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_local_position", "alter_local_position(sceneId, objectId, x, y, z)", "增量修改局部坐标", "Transform");

            instance.LuaScript.Globals["alter_position"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterPosition(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_position", "alter_position(sceneId, objectId, x, y, z)", "增量修改世界坐标", "Transform");

            // Transform: set rotation
            instance.LuaScript.Globals["set_local_rotation"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetLocalRotation(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_local_rotation", "set_local_rotation(sceneId, objectId, x, y, z)", "设置局部旋转(欧拉角)", "Transform");

            instance.LuaScript.Globals["set_rotation"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetRotation(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_rotation", "set_rotation(sceneId, objectId, x, y, z)", "设置世界旋转(欧拉角)", "Transform");

            instance.LuaScript.Globals["alter_local_rotation"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterLocalRotate(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_local_rotation", "alter_local_rotation(sceneId, objectId, x, y, z)", "增量修改局部旋转", "Transform");

            instance.LuaScript.Globals["alter_rotation"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterRotate(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_rotation", "alter_rotation(sceneId, objectId, x, y, z)", "增量修改世界旋转", "Transform");

            // Transform: set scale
            instance.LuaScript.Globals["set_local_scale"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetLocalScale(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_local_scale", "set_local_scale(sceneId, objectId, x, y, z)", "设置局部缩放", "Transform");

            instance.LuaScript.Globals["set_scale"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.SetScale(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("set_scale", "set_scale(sceneId, objectId, x, y, z)", "设置世界缩放", "Transform");

            instance.LuaScript.Globals["alter_local_scale"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterLocalScale(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_local_scale", "alter_local_scale(sceneId, objectId, x, y, z)", "增量修改局部缩放", "Transform");

            instance.LuaScript.Globals["alter_scale"] =
                (Action<string, string, double, double, double>)((sceneId, objectId, x, y, z) =>
                {
                    Scene.AlterScale(sceneId, objectId, new Double3(x, y, z));
                });
            RecordLuaApi("alter_scale", "alter_scale(sceneId, objectId, x, y, z)", "增量修改世界缩放", "Transform");

            // Transform: get position
            instance.LuaScript.Globals["get_local_position"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalPosition(sceneId, objectId);
                });
            RecordLuaApi("get_local_position", "get_local_position(sceneId, objectId) -> Double3", "获取局部坐标", "Transform");

            instance.LuaScript.Globals["get_position"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetPosition(sceneId, objectId);
                });
            RecordLuaApi("get_position", "get_position(sceneId, objectId) -> Double3", "获取世界坐标", "Transform");

            instance.LuaScript.Globals["get_local_rotation"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalRotation(sceneId, objectId);
                });
            RecordLuaApi("get_local_rotation", "get_local_rotation(sceneId, objectId) -> Double3", "获取局部旋转", "Transform");

            instance.LuaScript.Globals["get_rotation"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetRotation(sceneId, objectId);
                });
            RecordLuaApi("get_rotation", "get_rotation(sceneId, objectId) -> Double3", "获取世界旋转", "Transform");

            instance.LuaScript.Globals["get_local_scale"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalScale(sceneId, objectId);
                });
            RecordLuaApi("get_local_scale", "get_local_scale(sceneId, objectId) -> Double3", "获取局部缩放", "Transform");

            instance.LuaScript.Globals["get_scale"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetScale(sceneId, objectId);
                });
            RecordLuaApi("get_scale", "get_scale(sceneId, objectId) -> Double3", "获取世界缩放", "Transform");

            // Transform: direction getters
            instance.LuaScript.Globals["get_local_right"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalRight(sceneId, objectId);
                });
            RecordLuaApi("get_local_right", "get_local_right(sceneId, objectId) -> Double3", "获取局部右方向", "Transform");

            instance.LuaScript.Globals["get_local_left"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalLeft(sceneId, objectId);
                });
            RecordLuaApi("get_local_left", "get_local_left(sceneId, objectId) -> Double3", "获取局部左方向", "Transform");

            instance.LuaScript.Globals["get_local_up"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalUp(sceneId, objectId);
                });
            RecordLuaApi("get_local_up", "get_local_up(sceneId, objectId) -> Double3", "获取局部上方向", "Transform");

            instance.LuaScript.Globals["get_local_down"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalDown(sceneId, objectId);
                });
            RecordLuaApi("get_local_down", "get_local_down(sceneId, objectId) -> Double3", "获取局部下方向", "Transform");

            instance.LuaScript.Globals["get_local_forward"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalForward(sceneId, objectId);
                });
            RecordLuaApi("get_local_forward", "get_local_forward(sceneId, objectId) -> Double3", "获取局部前方向", "Transform");

            instance.LuaScript.Globals["get_local_back"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLocalBack(sceneId, objectId);
                });
            RecordLuaApi("get_local_back", "get_local_back(sceneId, objectId) -> Double3", "获取局部后方向", "Transform");

            instance.LuaScript.Globals["get_right"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetRight(sceneId, objectId);
                });
            RecordLuaApi("get_right", "get_right(sceneId, objectId) -> Double3", "获取世界右方向", "Transform");

            instance.LuaScript.Globals["get_left"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetLeft(sceneId, objectId);
                });
            RecordLuaApi("get_left", "get_left(sceneId, objectId) -> Double3", "获取世界左方向", "Transform");

            instance.LuaScript.Globals["get_up"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetUp(sceneId, objectId);
                });
            RecordLuaApi("get_up", "get_up(sceneId, objectId) -> Double3", "获取世界上方向", "Transform");

            instance.LuaScript.Globals["get_down"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetDown(sceneId, objectId);
                });
            RecordLuaApi("get_down", "get_down(sceneId, objectId) -> Double3", "获取世界下方向", "Transform");

            instance.LuaScript.Globals["get_forward"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetForward(sceneId, objectId);
                });
            RecordLuaApi("get_forward", "get_forward(sceneId, objectId) -> Double3", "获取世界前方向", "Transform");

            instance.LuaScript.Globals["get_back"] =
                (Func<string, string, Double3>)((sceneId, objectId) =>
                {
                    return Scene.GetBack(sceneId, objectId);
                });
            RecordLuaApi("get_back", "get_back(sceneId, objectId) -> Double3", "获取世界后方向", "Transform");

            instance.LuaScript.Globals["get_parent_id"] =
                (Func<string, string, string?>)((sceneId, objectId) =>
                {
                    return Scene.GetParentId(sceneId, objectId);
                });
            RecordLuaApi("get_parent_id", "get_parent_id(sceneId, objectId) -> string", "获取父节点ID", "Scene");

            instance.LuaScript.Globals["get_child_ids"] =
                (Func<string, string, Table>)((sceneId, objectId) =>
                {
                    string[] ids = Scene.GetChildIds(sceneId, objectId);

                    Table table = new Table(instance.LuaScript);
                    foreach (string id in ids)
                        table.Append(DynValue.NewString(id));

                    return table;
                });
            RecordLuaApi("get_child_ids", "get_child_ids(sceneId, objectId) -> table", "获取子节点ID列表", "Scene");

            instance.LuaScript.Globals["get_object_count"] =
                (Func<string, int>)((sceneId) =>
                {
                    return Scene.GetObjectCount(sceneId);
                });
            RecordLuaApi("get_object_count", "get_object_count(sceneId) -> int", "获取场景对象数量", "Scene");

            instance.LuaScript.Globals["get_object_ids"] =
                (Func<string, Table>)((sceneId) =>
                {
                    string[] ids = Scene.GetObjectIds(sceneId);
                    Table table = new Table(instance.LuaScript);
                    foreach (string id in ids)
                        table.Append(DynValue.NewString(id));
                    return table;
                });
            RecordLuaApi("get_object_ids", "get_object_ids(sceneId) -> table", "获取场景对象ID列表", "Scene");

            // 注入输入系统
            instance.LuaScript.Globals["input"] = _input;
            RecordLuaApi("input", "input", "输入系统对象", "Input");
        }

        static LuaScriptInstance? CreateComponentScript(string sceneId, string objectId, SceneScriptAttachment attachment)
        {
            if (attachment == null || string.IsNullOrWhiteSpace(attachment.Script))
                return null;

            string key = NormalizeAssetLookupKey(attachment.Script, removeExtension: true);

            string filePath;
            if (!_scriptFileRegistry.TryGetValue(key, out filePath!) && !EnsureScriptRegistered(key, out filePath))
            {
                Console.WriteLine($"[!] Component script '{attachment.Script}' is not registered.");
                return null;
            }

            if (!File.Exists(filePath))
            {
                Console.WriteLine($"[!] Component script file is missing: {filePath}");
                return null;
            }

            var instance = new LuaScriptInstance(filePath);
            instance.ScriptKey = key;
            instance.SceneId = sceneId;
            instance.ObjectId = objectId;
            instance.Enabled = attachment.Enabled;

            BindScriptSymbols(instance);
            BindLuaGlobals(instance, _gameData!, _graphics!, _submitTaskFunc!, _getTaskResultFunc!);

            try
            {
                instance.LuaScript.DoFile(filePath);

                DynValue initFunc = instance.LuaScript.Globals.Get("init");
                if (initFunc?.Type == DataType.Function)
                    instance.InitFunction = initFunc;

                DynValue startFunc = instance.LuaScript.Globals.Get("start");
                if (startFunc?.Type == DataType.Function)
                    instance.StartFunction = startFunc;

                DynValue loopFunc = instance.LuaScript.Globals.Get("loop");
                if (loopFunc?.Type == DataType.Function)
                    instance.LoopFunction = loopFunc;

                DynValue enableFunc = instance.LuaScript.Globals.Get("on_enable");
                if (enableFunc?.Type == DataType.Function)
                    instance.EnableFunction = enableFunc;

                DynValue disableFunc = instance.LuaScript.Globals.Get("on_disable");
                if (disableFunc?.Type == DataType.Function)
                    instance.DisableFunction = disableFunc;

                DynValue destroyFunc = instance.LuaScript.Globals.Get("on_destroy");
                if (destroyFunc?.Type == DataType.Function)
                    instance.DestroyFunction = destroyFunc;
            }
            catch (InterpreterException ex)
            {
                Console.WriteLine($"[X] Failed to load component script '{key}': {ex.DecoratedMessage}");
                return null;
            }

            Console.WriteLine($"[i] Attached script: {key} -> {sceneId}::{objectId}");
            return instance;
        }

        internal static void AttachSceneScripts(string sceneId, SceneData scene)
        {
            List<LuaScriptInstance> attached = new List<LuaScriptInstance>();

            foreach (SceneObject obj in scene.Objects)
            {
                if (obj.Scripts == null)
                    continue;

                foreach (SceneScriptAttachment attachment in obj.Scripts)
                {
                    LuaScriptInstance? instance = CreateComponentScript(sceneId, obj.Id, attachment);

                    if (instance != null)
                        attached.Add(instance);
                }
            }

            if (attached.Count == 0)
                return;

            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
            {
                list = new List<LuaScriptInstance>();
                _sceneScriptsByScene[sceneId] = list;
            }

            foreach (LuaScriptInstance instance in attached)
            {
                list.Add(instance);
                _sceneScriptInstances.Add(instance);
            }

            if (!_sceneScriptOrder.Contains(sceneId))
                _sceneScriptOrder.Add(sceneId);

            if (_isBusScriptPhase)
            {
                _pendingComponentInits.AddRange(attached);
                return;
            }

            RunComponentInits(attached);
        }

        internal static void RemoveSceneScripts(string sceneId)
        {
            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                return;

            foreach (LuaScriptInstance instance in list)
                DestroyComponentScript(instance);

            _sceneScriptsByScene.Remove(sceneId);
            _sceneScriptOrder.Remove(sceneId);
        }

        internal static void RemoveObjectScripts(string sceneId, string objectId)
        {
            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(list[i].ObjectId, objectId, StringComparison.Ordinal))
                    continue;

                LuaScriptInstance instance = list[i];

                list.RemoveAt(i);
                DestroyComponentScript(instance);
            }
        }

        static void DestroyComponentScript(LuaScriptInstance instance)
        {
            try
            {
                if (instance.DestroyFunction != null && instance.DestroyFunction.Type == DataType.Function)
                    instance.DestroyFunction.Function.Call();
            }
            catch (ScriptRuntimeException ex)
            {
                Console.WriteLine($"[X] Error in destroy function of script '{instance.ScriptKey}': {ex.DecoratedMessage}");
            }

            _sceneScriptInstances.Remove(instance);
            _pendingComponentInits.Remove(instance);
        }

        static void RunComponentInits(List<LuaScriptInstance> instances)
        {
            foreach (LuaScriptInstance instance in instances)
            {
                if (instance.InitFunction == null || instance.InitFunction.Type != DataType.Function)
                    continue;

                try
                {
                    instance.InitFunction.Function.Call();
                }
                catch (ScriptRuntimeException ex)
                {
                    Console.WriteLine($"[X] Error in init function of script '{instance.ScriptKey}': {ex.DecoratedMessage}");
                }
            }
        }

        static void RunPendingComponentInits()
        {
            if (_pendingComponentInits.Count == 0)
                return;

            List<LuaScriptInstance> pending = new List<LuaScriptInstance>(_pendingComponentInits);
            _pendingComponentInits.Clear();
            RunComponentInits(pending);
        }

        static bool IsComponentActive(LuaScriptInstance instance)
        {
            if (instance.SceneId == null || instance.ObjectId == null)
                return false;

            SceneObject? source = Scene.GetSceneObject(instance.SceneId, instance.ObjectId);

            if (source == null)
                return true;

            return source.Active;
        }

        static void TickSceneScripts(double deltaTime, float fixedDeltaTime)
        {
            if (_sceneScriptOrder.Count == 0)
                return;

            foreach (string sceneId in _sceneScriptOrder.ToArray())
            {
                if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                    continue;

                foreach (LuaScriptInstance instance in list.ToArray())
                {
                    bool shouldRun = instance.Enabled && IsComponentActive(instance);

                    if (shouldRun != instance.Running)
                    {
                        instance.Running = shouldRun;

                        if (shouldRun)
                        {
                            try
                            {
                                if (instance.EnableFunction != null && instance.EnableFunction.Type == DataType.Function)
                                    instance.EnableFunction.Function.Call();

                                if (!instance.Started)
                                {
                                    instance.Started = true;

                                    if (instance.StartFunction != null && instance.StartFunction.Type == DataType.Function)
                                        instance.StartFunction.Function.Call();
                                }
                            }
                            catch (ScriptRuntimeException ex)
                            {
                                Console.WriteLine($"[X] Error in start function of script '{instance.ScriptKey}': {ex.DecoratedMessage}");
                            }
                        }
                        else
                        {
                            try
                            {
                                if (instance.DisableFunction != null && instance.DisableFunction.Type == DataType.Function)
                                    instance.DisableFunction.Function.Call();
                            }
                            catch (ScriptRuntimeException ex)
                            {
                                Console.WriteLine($"[X] Error in disable function of script '{instance.ScriptKey}': {ex.DecoratedMessage}");
                            }
                        }
                    }

                    if (!shouldRun)
                        continue;

                    if (instance.LoopFunction == null || instance.LoopFunction.Type != DataType.Function)
                        continue;

                    try
                    {
                        instance.LuaScript.Globals["deltaTime"] = deltaTime;
                        instance.LuaScript.Globals["fixedDeltaTime"] = fixedDeltaTime;
                        instance.LoopFunction.Function.Call();
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        Console.WriteLine($"[X] Error in loop function of script '{instance.ScriptKey}': {ex.DecoratedMessage}");
                    }
                }
            }
        }

        internal static bool AttachComponent(string sceneId, string objectId, string scriptKey, bool enabled)
        {
            SceneObject? target = Scene.GetSceneObject(sceneId, objectId);
            if (target == null)
                return false;

            string key = NormalizeAssetLookupKey(scriptKey, removeExtension: true);

            LuaScriptInstance? instance = CreateComponentScript(sceneId, objectId, new SceneScriptAttachment
            {
                Script = key,
                Enabled = enabled
            });

            if (instance == null)
                return false;

            if (target.Scripts == null)
                target.Scripts = new List<SceneScriptAttachment>();

            target.Scripts.Add(new SceneScriptAttachment { Script = key, Enabled = enabled });

            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
            {
                list = new List<LuaScriptInstance>();
                _sceneScriptsByScene[sceneId] = list;
            }

            list.Add(instance);
            _sceneScriptInstances.Add(instance);

            if (!_sceneScriptOrder.Contains(sceneId))
                _sceneScriptOrder.Add(sceneId);

            if (_isBusScriptPhase)
            {
                _pendingComponentInits.Add(instance);
                return true;
            }

            RunComponentInits(new List<LuaScriptInstance> { instance });
            return true;
        }

        internal static bool DetachComponent(string sceneId, string objectId, string scriptKey, int index)
        {
            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                return false;

            string key = NormalizeAssetLookupKey(scriptKey, removeExtension: true);
            int position = index <= 0 ? 1 : index;
            int seen = 0;
            LuaScriptInstance? target = null;

            foreach (LuaScriptInstance instance in list)
            {
                if (!string.Equals(instance.ObjectId, objectId, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(instance.ScriptKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                seen++;

                if (seen == position)
                {
                    target = instance;
                    break;
                }
            }

            if (target == null)
                return false;

            list.Remove(target);
            DestroyComponentScript(target);

            SceneObject? source = Scene.GetSceneObject(sceneId, objectId);

            if (source?.Scripts != null)
            {
                int remaining = position;

                for (int i = 0; i < source.Scripts.Count; i++)
                {
                    if (!string.Equals(source.Scripts[i].Script, key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    remaining--;

                    if (remaining == 0)
                    {
                        source.Scripts.RemoveAt(i);
                        break;
                    }
                }
            }

            return true;
        }

        internal static bool SetComponentEnabled(string sceneId, string objectId, string scriptKey, int index, bool enabled)
        {
            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                return false;

            string key = NormalizeAssetLookupKey(scriptKey, removeExtension: true);
            int position = index <= 0 ? 1 : index;
            int seen = 0;

            foreach (LuaScriptInstance instance in list)
            {
                if (!string.Equals(instance.ObjectId, objectId, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(instance.ScriptKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                seen++;

                if (seen != position)
                    continue;

                instance.Enabled = enabled;

                SceneObject? source = Scene.GetSceneObject(sceneId, objectId);

                if (source?.Scripts != null)
                {
                    int remaining = position;

                    foreach (SceneScriptAttachment attachment in source.Scripts)
                    {
                        if (!string.Equals(attachment.Script, key, StringComparison.OrdinalIgnoreCase))
                            continue;

                        remaining--;

                        if (remaining == 0)
                        {
                            attachment.Enabled = enabled;
                            break;
                        }
                    }
                }

                return true;
            }

            return false;
        }

        internal static LuaScriptInstance[] GetComponentInstances(string sceneId, string objectId, string scriptKey)
        {
            if (!_sceneScriptsByScene.TryGetValue(sceneId, out List<LuaScriptInstance>? list))
                return Array.Empty<LuaScriptInstance>();

            string key = NormalizeAssetLookupKey(scriptKey, removeExtension: true);
            List<LuaScriptInstance> result = new List<LuaScriptInstance>();

            foreach (LuaScriptInstance instance in list)
            {
                if (!string.Equals(instance.ObjectId, objectId, StringComparison.Ordinal))
                    continue;

                if (!string.Equals(instance.ScriptKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(instance);
            }

            return result.ToArray();
        }

        internal static bool TryGetBusScript(string name, out LuaScriptInstance? instance)
        {
            instance = null;

            if (string.IsNullOrWhiteSpace(name))
                return false;

            string key = NormalizeAssetLookupKey(name, removeExtension: true);

            return _busScriptByName.TryGetValue(key, out instance);
        }

        internal static DynValue CallScriptFunction(LuaScriptInstance target, string methodName, DynValue[] args)
        {
            DynValue function = target.LuaScript.Globals.Get(methodName);

            if (function == null || function.Type != DataType.Function)
            {
                Console.WriteLine($"[!] Script '{target.ScriptKey}' has no function '{methodName}'.");
                return DynValue.Nil;
            }

            object?[] converted = new object?[args.Length];

            for (int i = 0; i < args.Length; i++)
                converted[i] = args[i].ToObject();

            try
            {
                DynValue result = function.Function.Call(converted);

                if (result == null)
                    return DynValue.Nil;

                if (result.Type == DataType.Number || result.Type == DataType.String || result.Type == DataType.Boolean)
                    return result;

                return DynValue.Nil;
            }
            catch (ScriptRuntimeException ex)
            {
                Console.WriteLine($"[X] Error calling '{methodName}' on script '{target.ScriptKey}': {ex.DecoratedMessage}");
                return DynValue.Nil;
            }
        }

        internal static void QueueBusCommand(Action action)
        {
            if (action == null)
                return;

            _busCommandQueue.Enqueue(action);
        }

        static void ExecuteBusCommands()
        {
            while (_busCommandQueue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Bus command failed: {ex.Message}");
                }
            }
        }
    }

    [MoonSharpUserData]
    public sealed class LuaBusProxy
    {
        public LuaBusHandle? Find(string name)
        {
            if (!Program.TryGetBusScript(name, out LuaScriptInstance? target) || target == null)
            {
                Console.WriteLine($"[!] Bus script '{name}' is not loaded.");
                return null;
            }

            return new LuaBusHandle(target.ScriptKey);
        }
    }

    [MoonSharpUserData]
    public sealed class LuaBusHandle
    {
        private readonly string _name;

        internal LuaBusHandle(string name)
        {
            _name = name;
        }

        public DynValue Call(string methodName, params DynValue[] args)
        {
            Program.QueueBusCommand(() =>
            {
                if (!Program.TryGetBusScript(_name, out LuaScriptInstance? target) || target == null)
                    return;

                Program.CallScriptFunction(target, methodName, args);
            });

            return DynValue.Nil;
        }
    }

    [MoonSharpUserData]
    public sealed class LuaSceneProxy
    {
        private readonly Script _script;
        private readonly string? _sceneId;

        internal LuaSceneProxy(Script script, string? sceneId)
        {
            _script = script;
            _sceneId = sceneId;
        }

        public LuaSceneProxy? Get(string sceneId)
        {
            if (string.IsNullOrWhiteSpace(sceneId))
                return null;

            return new LuaSceneProxy(_script, sceneId.Trim());
        }

        public string GetId()
        {
            return _sceneId ?? string.Empty;
        }

        public LuaObjectProxy? GetObject(string objectId)
        {
            if (string.IsNullOrWhiteSpace(_sceneId) || string.IsNullOrWhiteSpace(objectId))
                return null;

            string trimmed = objectId.Trim();

            if (Scene.GetSceneObject(_sceneId, trimmed) == null)
                return null;

            return new LuaObjectProxy(_script, _sceneId, trimmed);
        }

        public int GetObjectCount()
        {
            if (string.IsNullOrWhiteSpace(_sceneId))
                return 0;

            return Scene.GetObjectCount(_sceneId);
        }

        public Table GetObjectIds()
        {
            Table table = new Table(_script);

            if (string.IsNullOrWhiteSpace(_sceneId))
                return table;

            foreach (string id in Scene.GetObjectIds(_sceneId))
                table.Append(DynValue.NewString(id));

            return table;
        }

        public LuaObjectProxy? AddObject(string objectId, string name, string type)
        {
            if (string.IsNullOrWhiteSpace(_sceneId) || string.IsNullOrWhiteSpace(objectId))
                return null;

            SceneObject? created = Scene.AddObject(_sceneId, objectId.Trim(), name ?? "", type ?? "Object");

            if (created == null)
                return null;

            return new LuaObjectProxy(_script, _sceneId, created.Id);
        }

        public bool RemoveObject(string objectId)
        {
            if (string.IsNullOrWhiteSpace(_sceneId) || string.IsNullOrWhiteSpace(objectId))
                return false;

            return Scene.RemoveObject(_sceneId, objectId.Trim());
        }
    }

    [MoonSharpUserData]
    public sealed class LuaObjectProxy
    {
        private readonly Script _script;
        private readonly string _sceneId;
        private readonly string _objectId;

        internal LuaObjectProxy(Script script, string sceneId, string objectId)
        {
            _script = script;
            _sceneId = sceneId;
            _objectId = objectId;
        }

        public string GetId()
        {
            return _objectId;
        }

        public string GetSceneId()
        {
            return _sceneId;
        }

        public string GetName()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.Name ?? string.Empty;
        }

        public void SetName(string value)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source != null)
                source.Name = value ?? string.Empty;
        }

        public string GetObjectType()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.Type ?? string.Empty;
        }

        public void SetObjectType(string value)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source != null)
                source.Type = string.IsNullOrWhiteSpace(value) ? "Object" : value;
        }

        public bool GetActive()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.Active ?? false;
        }

        public void SetActive(bool value)
        {
            Scene.SetObjectActive(_sceneId, _objectId, value);
        }

        public bool GetVisible()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.Visible ?? false;
        }

        public void SetVisible(bool value)
        {
            Scene.SetObjectVisible(_sceneId, _objectId, value);
        }

        public string GetMesh()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.Mesh ?? string.Empty;
        }

        public void SetMesh(string value)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source != null)
                source.Mesh = string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public string GetRenderTag()
        {
            return Scene.GetSceneObject(_sceneId, _objectId)?.RenderTag ?? string.Empty;
        }

        public void SetRenderTag(string value)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source != null)
                source.RenderTag = value ?? string.Empty;
        }

        public Table GetTags()
        {
            Table table = new Table(_script);
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source?.Tags == null)
                return table;

            foreach (string tag in source.Tags)
                table.Append(DynValue.NewString(tag));

            return table;
        }

        public void SetTags(Table tags)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source == null)
                return;

            List<string> list = new List<string>();

            if (tags != null)
            {
                foreach (DynValue value in tags.Values)
                {
                    if (value.Type == DataType.String)
                        list.Add(value.String);
                }
            }

            source.Tags = list;
        }

        public Table GetMaterials()
        {
            Table table = new Table(_script);
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source?.Materials == null)
                return table;

            foreach (string material in source.Materials)
                table.Append(DynValue.NewString(material));

            return table;
        }

        public void SetMaterials(Table materials)
        {
            SceneObject? source = Scene.GetSceneObject(_sceneId, _objectId);

            if (source == null)
                return;

            List<string>? list = null;

            if (materials != null)
            {
                foreach (DynValue value in materials.Values)
                {
                    if (value.Type != DataType.String)
                        continue;

                    list ??= new List<string>();
                    list.Add(value.String);
                }
            }

            source.Materials = list;
        }

        public string GetParentId()
        {
            return Scene.GetParentId(_sceneId, _objectId) ?? string.Empty;
        }

        public Table GetChildIds()
        {
            Table table = new Table(_script);

            foreach (string id in Scene.GetChildIds(_sceneId, _objectId))
                table.Append(DynValue.NewString(id));

            return table;
        }

        public Double3 GetLocalPosition()
        {
            return Scene.GetLocalPosition(_sceneId, _objectId);
        }

        public Double3 GetPosition()
        {
            return Scene.GetPosition(_sceneId, _objectId);
        }

        public void SetLocalPosition(double x, double y, double z)
        {
            Scene.SetLocalPosition(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void SetPosition(double x, double y, double z)
        {
            Scene.SetPosition(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterLocalPosition(double x, double y, double z)
        {
            Scene.AlterLocalPosition(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterPosition(double x, double y, double z)
        {
            Scene.AlterPosition(_sceneId, _objectId, new Double3(x, y, z));
        }

        public Double3 GetLocalRotation()
        {
            return Scene.GetLocalRotation(_sceneId, _objectId);
        }

        public Double3 GetRotation()
        {
            return Scene.GetRotation(_sceneId, _objectId);
        }

        public void SetLocalRotation(double x, double y, double z)
        {
            Scene.SetLocalRotation(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void SetRotation(double x, double y, double z)
        {
            Scene.SetRotation(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterLocalRotation(double x, double y, double z)
        {
            Scene.AlterLocalRotate(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterRotation(double x, double y, double z)
        {
            Scene.AlterRotate(_sceneId, _objectId, new Double3(x, y, z));
        }

        public Double3 GetLocalScale()
        {
            return Scene.GetLocalScale(_sceneId, _objectId);
        }

        public Double3 GetScale()
        {
            return Scene.GetScale(_sceneId, _objectId);
        }

        public void SetLocalScale(double x, double y, double z)
        {
            Scene.SetLocalScale(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void SetScale(double x, double y, double z)
        {
            Scene.SetScale(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterLocalScale(double x, double y, double z)
        {
            Scene.AlterLocalScale(_sceneId, _objectId, new Double3(x, y, z));
        }

        public void AlterScale(double x, double y, double z)
        {
            Scene.AlterScale(_sceneId, _objectId, new Double3(x, y, z));
        }

        public Double3 GetLocalRight()
        {
            return Scene.GetLocalRight(_sceneId, _objectId);
        }

        public Double3 GetLocalLeft()
        {
            return Scene.GetLocalLeft(_sceneId, _objectId);
        }

        public Double3 GetLocalUp()
        {
            return Scene.GetLocalUp(_sceneId, _objectId);
        }

        public Double3 GetLocalDown()
        {
            return Scene.GetLocalDown(_sceneId, _objectId);
        }

        public Double3 GetLocalForward()
        {
            return Scene.GetLocalForward(_sceneId, _objectId);
        }

        public Double3 GetLocalBack()
        {
            return Scene.GetLocalBack(_sceneId, _objectId);
        }

        public Double3 GetRight()
        {
            return Scene.GetRight(_sceneId, _objectId);
        }

        public Double3 GetLeft()
        {
            return Scene.GetLeft(_sceneId, _objectId);
        }

        public Double3 GetUp()
        {
            return Scene.GetUp(_sceneId, _objectId);
        }

        public Double3 GetDown()
        {
            return Scene.GetDown(_sceneId, _objectId);
        }

        public Double3 GetForward()
        {
            return Scene.GetForward(_sceneId, _objectId);
        }

        public Double3 GetBack()
        {
            return Scene.GetBack(_sceneId, _objectId);
        }

        public DynValue GetComponent(string scriptKey)
        {
            LuaScriptInstance[] instances = Program.GetComponentInstances(_sceneId, _objectId, scriptKey ?? string.Empty);
            Table group = new Table(_script);

            for (int index = 0; index < instances.Length; index++)
            {
                LuaScriptInstance instance = instances[index];
                int position = index + 1;
                Table item = new Table(_script);

                item["Call"] = DynValue.NewCallback((context, args) =>
                {
                    DynValue methodName = args.Count > 1 ? args[1] : DynValue.Nil;
                    DynValue[] forwarded = new DynValue[Math.Max(0, args.Count - 2)];

                    for (int i = 2; i < args.Count; i++)
                        forwarded[i - 2] = args[i];

                    return Program.CallScriptFunction(instance, methodName.CastToString() ?? string.Empty, forwarded);
                });

                item["GetScriptKey"] = DynValue.NewCallback((context, args) => DynValue.NewString(instance.ScriptKey));
                item["GetSceneId"] = DynValue.NewCallback((context, args) => DynValue.NewString(instance.SceneId ?? string.Empty));
                item["GetObjectId"] = DynValue.NewCallback((context, args) => DynValue.NewString(instance.ObjectId ?? string.Empty));
                item["IsEnabled"] = DynValue.NewCallback((context, args) => DynValue.NewBoolean(instance.Enabled));

                item["SetEnabled"] = DynValue.NewCallback((context, args) =>
                {
                    if (args.Count > 1)
                        Program.SetComponentEnabled(_sceneId, _objectId, instance.ScriptKey, position, args[1].Boolean);

                    return DynValue.Nil;
                });

                group.Append(DynValue.NewTable(item));
            }

            Table meta = new Table(_script);

            meta["__index"] = DynValue.NewCallback((context, args) =>
            {
                DynValue first = group.Get(1);

                if (first.Type != DataType.Table)
                    return DynValue.Nil;

                return first.Table.Get(args[1]);
            });

            group.MetaTable = meta;

            return DynValue.NewTable(group);
        }

        public DynValue AddComponent(string scriptKey)
        {
            string key = Program.NormalizeAssetLookupKey(scriptKey ?? string.Empty, removeExtension: true);

            if (!Program.AttachComponent(_sceneId, _objectId, key, true))
                return DynValue.Nil;

            return GetComponent(key);
        }

        public bool RemoveComponent(string scriptKey, DynValue index)
        {
            int position = 1;

            if (index != null && index.Type == DataType.Number)
                position = (int)index.Number;

            return Program.DetachComponent(_sceneId, _objectId, scriptKey ?? string.Empty, position);
        }

        public bool SetComponentActive(string scriptKey, DynValue index, bool enabled)
        {
            int position = 1;

            if (index != null && index.Type == DataType.Number)
                position = (int)index.Number;

            return Program.SetComponentEnabled(_sceneId, _objectId, scriptKey ?? string.Empty, position, enabled);
        }
    }
}
