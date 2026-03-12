using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Platforms;
using Silk.NET.Core;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LimitlessSquareEngine
{
    /// <summary>
    /// lua脚本类
    /// </summary>
    public class LuaScriptInstance
    {
        public string FilePath { get; private set; }
        public Script LuaScript { get; private set; }
        public DynValue InitFunction { get; set; }
        public DynValue LoopFunction { get; set; }

        public LuaScriptInstance(string filePath)
        {
            FilePath = filePath;
            // 配置Lua设置
            Script.GlobalOptions.Platform = new LimitedPlatformAccessor();
            // 创建独立Lua解释器环境
            LuaScript = new Script(CoreModules.Basic | CoreModules.Math | CoreModules.String | CoreModules.Table | CoreModules.Coroutine);
            // 防止开发者顺网线爬进你的系统
            LuaScript.Globals["os"] = DynValue.Nil;
            LuaScript.Globals["io"] = DynValue.Nil;
            LuaScript.Globals["file"] = DynValue.Nil;
            LuaScript.Globals["load"] = DynValue.Nil;
            LuaScript.Globals["loadfile"] = DynValue.Nil;
            LuaScript.Globals["dofile"] = DynValue.Nil;
            LuaScript.Globals["loadstring"] = DynValue.Nil;
            LuaScript.Globals["package"] = DynValue.Nil;
            LuaScript.Globals["require"] = DynValue.Nil;
            InitFunction = DynValue.Nil;
            LoopFunction = DynValue.Nil;
        }
    }

    public class Vector4JsonConverter : JsonConverter<Vector4>
    {
        public override Vector4 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException("Expected start of array.");
            var values = new float[4];
            for (int i = 0; i < 4; i++)
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.EndArray)
                    throw new JsonException("Insufficient array elements.");
                values[i] = reader.GetSingle();
            }
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException("Expected end of array.");
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        public override void Write(Utf8JsonWriter writer, Vector4 value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(value.X);
            writer.WriteNumberValue(value.Y);
            writer.WriteNumberValue(value.Z);
            writer.WriteNumberValue(value.W);
            writer.WriteEndArray();
        }
    }

    internal class Program
    {
        // Lua脚本定义
        static List<LuaScriptInstance> _luaScriptInstances = new List<LuaScriptInstance>();
        // 窗口定义
        static IWindow? _window;
        // 图形定义
        static GL? _gl;

        // 定义任务队列
        static BlockingCollection<Action> taskQueue = new BlockingCollection<Action>();
        // 存储任务结果
        static ConcurrentDictionary<int, TaskCompletionSource<DynValue>> taskResults = new ConcurrentDictionary<int, TaskCompletionSource<DynValue>>();
        // 任务ID
        static int nextTaskId = 0;

        static Graphics? _graphics;
        // 定义纹理路径集合
        internal static List<string> _texturePaths = new List<string>();
        // 定义布局集合
        internal static Dictionary<string, List<UIElement>> _uiLayouts = new Dictionary<string, List<UIElement>>();
        // 场景文件注册表
        internal static Dictionary<string, string> _sceneFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 材质文件注册表
        internal static Dictionary<string, string> _materialFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 场景文件验证信息
        static Dictionary<string, string> _sceneFileDisplayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 定义前一帧时间
        private static double _lastFrameTime;

        /// <summary>
        /// 初始化方法
        /// </summary>
        static void Initialize()
        {
            // 文件结构创建
            try
            {
                Directory.CreateDirectory("Assets/Scene");
                Directory.CreateDirectory("Assets/Textures/Icon");
                Directory.CreateDirectory("Assets/UI");
                Directory.CreateDirectory("Assets/Materials");
                Directory.CreateDirectory("Scripts");
                Directory.CreateDirectory("Assets/Shaders");
            }
            catch 
            {

            }

            // 注册数据类型
            UserData.RegisterType<GameData>();
            UserData.RegisterType<Graphics>();
            UserData.RegisterType<SceneData>();
            UserData.RegisterType<SceneObject>();
            UserData.RegisterType<SceneTransform>();
            UserData.RegisterType<Double3>();
            // 初始化窗口
            var options = WindowOptions.Default;
            options.Size = new Silk.NET.Maths.Vector2D<int>(800, 600);
            options.Title = "Limitless Square Engine";
            options.IsVisible = true;
            options.ShouldSwapAutomatically = false;
            _window = Window.Create(options);

            // 加载事件
            _window.Load += () =>
            {
                // 图标设置
                byte[]? iconBytes = null;
                // 搜索图标文件夹
                string iconFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Textures", "Icon");
                if (Directory.Exists(iconFolder))
                {
                    var iconFiles = Directory.GetFiles(iconFolder, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => f.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(f => f)
                        .ToList();
                    if (iconFiles.Count > 0)
                    {
                        string firstIcon = iconFiles[0];
                        try
                        {
                            iconBytes = File.ReadAllBytes(firstIcon);
                            // 尝试解码验证有效性
                            using var testCodec = SKCodec.Create(new SKMemoryStream(iconBytes));
                            if (testCodec == null)
                            {
                                iconBytes = null;
                            }
                        }
                        catch
                        {
                            iconBytes = null;
                        }
                    }
                }

                // 如果文件图标加载失败或未找到，使用默认图标
                if (iconBytes == null)
                {
                    iconBytes = Properties.Resources.LimitlessSquareEngineIcon;
                }

                using var codec = SKCodec.Create(new SKMemoryStream(iconBytes));

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                byte[] rgba = new byte[info.Width * info.Height * 4];

                var result = codec.GetPixels(info, rgba);

                int w = info.Width;
                int h = info.Height;

                var iconImage = new RawImage(w, h, rgba);
                _window.SetWindowIcon(new[] { iconImage });
                _window.SetWindowIcon(new[] { iconImage });

                // 初始化OpenGL
                _gl = _window.CreateOpenGL();
                _gl.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
                var graphics = new Graphics(_gl, _window);
                graphics.Initialize();
                _graphics = graphics;

                // 初始化帧时间
                _lastFrameTime = _window.Time;

                // 任务提交函数
                Func<string, int> submitTaskFunc = (luaCode) =>
                {
                    int taskId = Interlocked.Increment(ref nextTaskId);
                    var tcs = new TaskCompletionSource<DynValue>();
                    taskResults[taskId] = tcs;

                    taskQueue.Add(() =>
                    {
                        try
                        {
                            Script threadScript = new Script();
                            DynValue func = threadScript.LoadString(luaCode);
                            DynValue result = func.Function.Call();
                            tcs.SetResult(result);
                        }
                        catch (Exception ex)
                        {
                            tcs.SetException(ex);
                        }
                    });

                    return taskId;
                };

                // 获取任务结果函数
                Func<int, DynValue[]> getTaskResultFunc = (taskId) =>
                {
                    if (taskResults.TryGetValue(taskId, out var tcs))
                    {
                        if (tcs.Task.IsCompleted)
                        {
                            if (tcs.Task.IsFaulted)
                            {
                                return [DynValue.Nil, DynValue.NewString(tcs.Task.Exception.InnerException.Message)];
                            }
                            else
                            {
                                return [tcs.Task.Result];
                            }
                        }
                    }
                    return [DynValue.Nil];
                };

                // 定义游戏数据
                GameData gameData = new GameData();

                // 扫描脚本文件夹
                string scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts");
                if (Directory.Exists(scriptPath))
                {
                    // 获取所有lua脚本
                    string[] luaFiles = Directory.GetFiles(scriptPath, "*.lua", SearchOption.AllDirectories);
                    foreach (string file in luaFiles)
                    {
                        var instance = new LuaScriptInstance(file);

                        // 注入数据
                        instance.LuaScript.Globals["game_data"] = gameData;
                        // 注入线程工具函数
                        instance.LuaScript.Globals["submit_task"] = submitTaskFunc;
                        instance.LuaScript.Globals["get_task_result"] = getTaskResultFunc;
                        // 注入图形对象
                        instance.LuaScript.Globals["graphics"] = graphics;
                        // 注入打印输出
                        instance.LuaScript.Globals["print"] = (Action<object>)((obj) => Console.WriteLine(obj));
                        // 注入纹理目录
                        Table textureTable = new Table(instance.LuaScript);
                        foreach (var path in _texturePaths)
                            textureTable.Append(DynValue.NewString(path));
                        instance.LuaScript.Globals["texture_paths"] = textureTable;

                        // 注入场景加载函数
                        instance.LuaScript.Globals["load_scene"] = (Func<string, SceneData>)((sceneId) =>
                        {
                            return Scene.LoadScene(sceneId);
                        });

                        // 注入场景移除函数
                        instance.LuaScript.Globals["remove_scene"] = (Action<string>)((sceneId) =>
                        {
                            Scene.RemoveScene(sceneId);
                        });

                        // 注入手动重扫摄像机函数
                        instance.LuaScript.Globals["rescan_scene_cameras"] = (Action<string>)((sceneId) =>
                        {
                            Scene.RebuildCameraQueue(sceneId);
                        });

                        // 注入天空盒设置函数
                        instance.LuaScript.Globals["set_skybox"] = (Action<string, string>)((shaderName, parametersJson) =>
                        {
                            graphics.SetScreenSkybox(shaderName, parametersJson);
                        });

                        // 注入天空盒卸载函数
                        instance.LuaScript.Globals["clear_skybox"] = (Action)(() =>
                        {
                            graphics.ClearScreenSkybox();
                        });

                        // 执行脚本文件
                        instance.LuaScript.DoFile(file);

                        // 缓存init和loop函数
                        DynValue initFunc = instance.LuaScript.Globals.Get("init");
                        if (initFunc?.Type == DataType.Function)
                            instance.InitFunction = initFunc;

                        DynValue loopFunc = instance.LuaScript.Globals.Get("loop");
                        if (loopFunc?.Type == DataType.Function)
                            instance.LoopFunction = loopFunc;

                        _luaScriptInstances.Add(instance);
                        Console.WriteLine($"[i] Loaded script: {file}");
                    }
                }

                // 扫描资源文件夹
                string assetsPath = Path.Combine(AppContext.BaseDirectory, "Assets");
                if (Directory.Exists(assetsPath))
                {
                    var options = new JsonSerializerOptions
                    {
                        Converters = { new Vector4JsonConverter() },
                        PropertyNameCaseInsensitive = true
                    };

                    string uiBasePath = Path.Combine(assetsPath, "UI");
                    string texturesBasePath = Path.Combine(assetsPath, "Textures");
                    string sceneBasePath = Path.Combine(assetsPath, "Scene");
                    string materialsBasePath = Path.Combine(assetsPath, "Materials");

                    _sceneFileRegistry.Clear();
                    _sceneFileDisplayName.Clear();
                    _materialFileRegistry.Clear();

                    _sceneFileRegistry.Clear();
                    _sceneFileDisplayName.Clear();

                    string[] allFiles = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories);
                    Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);

                    foreach (string file in allFiles)
                    {
                        string? directory = Path.GetDirectoryName(file);
                        if (string.IsNullOrWhiteSpace(directory))
                            continue;

                        // UI布局文件
                        if (directory.StartsWith(uiBasePath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    string json = File.ReadAllText(file);
                                    var elements = JsonSerializer.Deserialize<List<UIElement>>(json, options);
                                    if (elements != null)
                                    {
                                        void SetParent(UIElement element, UIElement? parent = null)
                                        {
                                            if (parent != null)
                                                element.Parent = parent;
                                            foreach (var child in element.Children)
                                                SetParent(child, element);
                                        }

                                        foreach (var element in elements)
                                            SetParent(element);

                                        string key = Path.GetFileNameWithoutExtension(file);
                                        _uiLayouts[key] = elements;
                                        Console.WriteLine($"[i] Loaded UI layout: {key}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[!] Failed to load UI layout from {file}: {ex.Message}");
                                }
                            }

                            continue;
                        }

                        // 场景文件
                        if (directory.StartsWith(sceneBasePath, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if (!TryValidateSceneFile(file, out string sceneId, out string reason))
                                {
                                    Console.WriteLine($"[!] Invalid scene file skipped: {file} | Reason: {reason}");
                                    continue;
                                }

                                if (_sceneFileRegistry.TryGetValue(sceneId, out string? oldPath))
                                {
                                    Console.WriteLine($"[!] Duplicate scene id '{sceneId}' found. Replacing:");
                                    Console.WriteLine($"    Old: {oldPath}");
                                    Console.WriteLine($"    New: {file}");
                                }

                                _sceneFileRegistry[sceneId] = file;
                                _sceneFileDisplayName[sceneId] = Path.GetFileName(file);

                                Console.WriteLine($"[i] Registered scene: {sceneId} -> {file}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] Failed to scan scene file {file}: {ex.Message}");
                            }

                            continue;
                        }

                        // 材质文件
                        if (directory.StartsWith(materialsBasePath, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if (!TryValidateMaterialFile(file, out string reason))
                                {
                                    Console.WriteLine($"[!] Invalid material file skipped: {file} | Reason: {reason}");
                                    continue;
                                }

                                string key = Path.GetRelativePath(materialsBasePath, file)
                                    .Replace('\\', '/');

                                if (key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                                    key = key[..^5];

                                _materialFileRegistry[key] = file;
                                Console.WriteLine($"[i] Registered material: {key} -> {file}");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[!] Failed to scan material file {file}: {ex.Message}");
                            }

                            continue;
                        }

                        // 纹理文件
                        if (file.StartsWith(texturesBasePath, StringComparison.OrdinalIgnoreCase) &&
                            Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
                        {
                            string relativePath = file.Substring(texturesBasePath.Length + 1);
                            relativePath = relativePath.Replace('\\', '/');
                            _texturePaths.Add(relativePath);
                            continue;
                        }
                    }

                    Console.WriteLine($"[i] Scene scan completed. Registered scenes: {_sceneFileRegistry.Count}, materials: {_materialFileRegistry.Count}");
                }

                // 调用所有脚本的init函数
                foreach (var instance in _luaScriptInstances)
                {
                    try
                    {
                        if (instance.InitFunction != null && instance.InitFunction.Type == DataType.Function)
                        {
                            instance.InitFunction.Function.Call();
                        }
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        Console.WriteLine($"[X] Error in init function of script '{instance.FilePath}': {ex.DecoratedMessage}");
                    }
                }
            };

            // 关闭事件
            _window.Closing += () =>
            {
                // 这里以后写退出时的操作！！！！！！！！！！！！！！！！！！！！！！！！！！！！！！

            };

            // 初始化窗口
            _window.Initialize();
        }

        /// <summary>
        /// 循环方法
        /// </summary>
        static void Loop()
        {
            // 处理窗口事件
            _window.DoEvents();

            // 如果OpenGL未初始化、窗口准备关闭，跳过渲染
            if (_gl == null || _window.IsClosing)
                return;

            // 帧间隔
            double currentTime = _window.Time;
            float deltaTime = (float)(currentTime - _lastFrameTime);
            _lastFrameTime = currentTime;

            // 清除颜色缓冲
            _graphics?.ClearBackground();
            // 交场景相机渲染
            _graphics?.QueueLoadedSceneRender();

            // 遍历所有脚本实例
            foreach (var instance in _luaScriptInstances)
            {
                if (instance.LoopFunction != null && instance.LoopFunction.Type == DataType.Function)
                {
                    try
                    {
                        instance.LuaScript.Globals["deltaTime"] = deltaTime;
                        instance.LoopFunction.Function.Call();
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        Console.WriteLine($"[X] Error in loop function of script '{instance.FilePath}': {ex.DecoratedMessage}");
                    }
                }
            }
            _graphics?.ExecuteRenderQueue();

            // 交换缓冲区
            _window.SwapBuffers();
        }

        /// <summary>
        /// 主线程
        /// </summary>
        static void Main()
        {
            Console.WriteLine("////////////////////////////////////////////////////");
            Console.WriteLine("Limitless Square Engine");
            Console.WriteLine("by DaVinci-2nd");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("https://github.com/DaVinci-2nd/LimitlessSquareEngine");
            Console.WriteLine("////////////////////////////////////////////////////");
            Console.WriteLine("献给热爱游戏的大家！");
            Console.WriteLine("Dedicated to all those who love games!");
            Console.WriteLine("----------------------------------------------------");
            Console.WriteLine("欢迎关注作者的bilibili账号！");
            Console.WriteLine("Welcome to follow the author's Bilibili account!");
            Console.WriteLine("https://space.bilibili.com/432070384");
            Console.WriteLine("////////////////////////////////////////////////////");
            // 执行初始化
            Initialize();

            // 根据核心数线程数目
            int threadCount = Math.Max(1, Environment.ProcessorCount);
            // 启动所有后台线程
            for (int i = 0; i < threadCount; i++)
            {
                // 创建进程
                Thread thread = new Thread(BackgroundThread);
                // 设置为后台进程
                thread.IsBackground = true;
                // 启动线程
                thread.Start();
            }

            // 执行主循环
            while (!_window.IsClosing)
            {
                Loop();
            }
        }

        /// <summary>
        /// 后台线程
        /// </summary>
        static void BackgroundThread()
        {
            while (true)
            {
                // 抽取任务
                Action task = taskQueue.Take();
                // 执行任务
                task();
            }
        }

        /// <summary>
        /// 验证场景文件结构是否合法。
        /// </summary>
        /// <param name="filePath"></param>
        /// <param name="sceneId"></param>
        /// <param name="reason"></param>
        /// <returns></returns>
        static bool TryValidateSceneFile(string filePath, out string sceneId, out string reason)
        {
            sceneId = string.Empty;
            reason = string.Empty;

            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = "Root must be a JSON object.";
                return false;
            }

            if (!TryGetProperty(root, "sceneId", out JsonElement sceneIdElement) ||
                sceneIdElement.ValueKind != JsonValueKind.String)
            {
                reason = "Missing or invalid 'sceneId'.";
                return false;
            }

            sceneId = sceneIdElement.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(sceneId))
            {
                reason = "'sceneId' cannot be empty.";
                return false;
            }

            if (!TryGetProperty(root, "objects", out JsonElement objectsElement) ||
                objectsElement.ValueKind != JsonValueKind.Array)
            {
                reason = "Missing or invalid 'objects' array.";
                return false;
            }

            // 基础验证
            HashSet<string> objectIds = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, string?> parentMap = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (JsonElement obj in objectsElement.EnumerateArray())
            {
                if (obj.ValueKind != JsonValueKind.Object)
                {
                    reason = "Every item in 'objects' must be a JSON object.";
                    return false;
                }

                if (!TryGetProperty(obj, "id", out JsonElement objectIdElement) ||
                    objectIdElement.ValueKind != JsonValueKind.String)
                {
                    reason = "Every object must contain a string 'id'.";
                    return false;
                }

                string objectId = objectIdElement.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(objectId))
                {
                    reason = "Object 'id' cannot be empty.";
                    return false;
                }

                if (!objectIds.Add(objectId))
                {
                    reason = $"Duplicate object id '{objectId}' in scene '{sceneId}'.";
                    return false;
                }

                string? parentId = null;

                if (TryGetProperty(obj, "transform", out JsonElement transformElement))
                {
                    if (transformElement.ValueKind != JsonValueKind.Object)
                    {
                        reason = $"Object '{objectId}' has invalid 'transform'.";
                        return false;
                    }

                    if (TryGetProperty(transformElement, "parentId", out JsonElement parentIdElement))
                    {
                        if (parentIdElement.ValueKind == JsonValueKind.Null)
                        {
                            parentId = null;
                        }
                        else if (parentIdElement.ValueKind == JsonValueKind.String)
                        {
                            parentId = parentIdElement.GetString();
                            if (string.IsNullOrWhiteSpace(parentId))
                                parentId = null;
                        }
                        else
                        {
                            reason = $"Object '{objectId}' has invalid 'transform.parentId'.";
                            return false;
                        }
                    }
                }
                string objectType = "Object";
                if (TryGetProperty(obj, "type", out JsonElement typeElement))
                {
                    if (typeElement.ValueKind != JsonValueKind.String)
                    {
                        reason = $"Object '{objectId}' has invalid 'type'.";
                        return false;
                    }

                    objectType = typeElement.GetString()?.Trim() ?? "Object";
                }

                if (string.Equals(objectType, "Camera", StringComparison.Ordinal))
                {
                    if (TryGetProperty(obj, "data", out JsonElement dataElement))
                    {
                        if (dataElement.ValueKind == JsonValueKind.Null)
                        {
                            // null 也视为空
                        }
                        else if (dataElement.ValueKind == JsonValueKind.String)
                        {
                            string rawCameraData = dataElement.GetString() ?? string.Empty;
                            if (!Scene.TryValidateCameraDataString(rawCameraData, objectId, out string cameraReason))
                            {
                                reason = cameraReason;
                                return false;
                            }
                        }
                        else
                        {
                            reason = $"Object '{objectId}' camera 'data' must be string or null.";
                            return false;
                        }
                    }
                }

                parentMap[objectId] = parentId;
            }

            // 检查parentId是否存在
            foreach (var pair in parentMap)
            {
                string objectId = pair.Key;
                string? parentId = pair.Value;

                if (!string.IsNullOrWhiteSpace(parentId) && !objectIds.Contains(parentId))
                {
                    reason = $"Object '{objectId}' references missing parent '{parentId}'.";
                    return false;
                }
            }

            // 检查是否存在循环父子关系
            foreach (string objectId in objectIds)
            {
                HashSet<string> visited = new HashSet<string>(StringComparer.Ordinal);
                string? current = objectId;

                while (!string.IsNullOrWhiteSpace(current))
                {
                    if (!visited.Add(current))
                    {
                        reason = $"Circular parent relationship detected at object '{objectId}'.";
                        return false;
                    }

                    if (!parentMap.TryGetValue(current, out string? nextParent))
                        break;

                    current = nextParent;
                }
            }

            return true;
        }

        /// <summary>
        /// 验证材质文件结构是否合法。
        /// </summary>
        static bool TryValidateMaterialFile(string filePath, out string reason)
        {
            reason = string.Empty;

            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);

            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                reason = "Root must be a JSON object.";
                return false;
            }

            if (!TryGetProperty(root, "assetType", out JsonElement assetTypeElement) ||
                assetTypeElement.ValueKind != JsonValueKind.String)
            {
                reason = "Missing or invalid 'assetType'.";
                return false;
            }

            string assetType = assetTypeElement.GetString()?.Trim() ?? string.Empty;
            if (!string.Equals(assetType, "Material", StringComparison.Ordinal))
            {
                reason = "'assetType' must be 'Material'.";
                return false;
            }

            if (TryGetProperty(root, "shader", out JsonElement shaderElement))
            {
                if (shaderElement.ValueKind != JsonValueKind.String &&
                    shaderElement.ValueKind != JsonValueKind.Null)
                {
                    reason = "'shader' must be string or null.";
                    return false;
                }
            }

            if (TryGetProperty(root, "parameters", out JsonElement parametersElement))
            {
                if (parametersElement.ValueKind != JsonValueKind.Object &&
                    parametersElement.ValueKind != JsonValueKind.Null)
                {
                    reason = "'parameters' must be object or null.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 读取JSON属性
        /// </summary>
        static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
        {
            foreach (JsonProperty prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.Ordinal))
                {
                    value = prop.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }
    }
}
