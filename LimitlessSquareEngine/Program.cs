using LimitlessSquareEngine.Engine;
using LimitlessSquareEngine.Engine.Terrain;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Platforms;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Input;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace LimitlessSquareEngine
{
    /// <summary>
    /// Lua脚本实例
    /// </summary>
    public class LuaScriptInstance
    {
        // 脚本文件路径
        public string FilePath { get; private set; }
        // Lua解释器实例
        public Script LuaScript { get; private set; }
        // 脚本初始化函数
        public DynValue InitFunction { get; set; }
        // 脚本循环函数
        public DynValue LoopFunction { get; set; }

        /// <summary>
        /// 创建Lua脚本实例
        /// </summary>
        /// <param name="filePath"></param>
        public LuaScriptInstance(string filePath)
        {
            FilePath = filePath;
            // 配置Lua访问权限
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

    /// <summary>
    /// Vector4 Json转换器
    /// </summary>
    public class Vector4JsonConverter : JsonConverter<Vector4>
    {
        /// <summary>
        /// 从JSON读取Vector4
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="typeToConvert"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        /// <exception cref="JsonException"></exception>
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

        /// <summary>
        /// 将Vector4写入JSON
        /// </summary>
        /// <param name="writer"></param>
        /// <param name="value"></param>
        /// <param name="options"></param>
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

    public sealed class EditorLaunchOptions
    {
        public string DefaultAssetRootPath { get; set; } = "";
        public string AssetRootPath { get; set; } = "";
        public ResourceLoadTiming ResourceLoadTiming { get; set; } = ResourceLoadTiming.Preload;
    }

    public sealed class EditorHostBootstrapInfo
    {
        public string AssetRootPath { get; set; } = "";
        public EditorEmbeddingMode EmbeddingMode { get; set; } = EditorEmbeddingMode.Unsupported;
        public nint Win32Hwnd { get; set; }
        public nint CocoaWindow { get; set; }
        public nint CocoaContentView { get; set; }
        public nint X11Display { get; set; }
        public nuint X11Window { get; set; }
        public nint WaylandDisplay { get; set; }
        public nint WaylandSurface { get; set; }
        public nint GlfwWindow { get; set; }
        public nint SdlWindow { get; set; }
    }

    public sealed class EditorRenderedFrame
    {
        public int Width { get; init; }
        public int Height { get; init; }
        public byte[] PixelsRgba { get; init; } = Array.Empty<byte>();
    }

    public readonly struct RenderFrameDiagnostics
    {
        public int SubmittedCommands { get; init; }
        public int CulledCommands { get; init; }
        public int DrawCalls { get; init; }
        public long DrawnVertices { get; init; }
        public long DrawnTriangles { get; init; }
        public bool GpuTimeAvailable { get; init; }
        public double GpuFrameMilliseconds { get; init; }
    }

    public enum ResourceLoadTiming
    {
        Lazy = 0,
        Preload = 1
    }

    /// <summary>
    /// 入口类
    /// </summary>
    internal partial class Program
    {
        // 编辑器程序集文件名
        private const string EditorAssemblyFileName = "Limitless Square Editor.dll";
        // 运行模式
        private static bool _isEditorMode = false;
        private static bool _forceGameMode = false;
        private static bool _forceEditorMode = false;
        private static bool _isExternalHostMode = false;
        private static EditorEmbeddingMode _externalHostEmbeddingMode = EditorEmbeddingMode.Unsupported;
        private static nint _externalHostWin32ParentHwnd;
        private static nint _externalHostCocoaParentView;
        private static nint _externalHostX11ParentWindow;
        private static int _externalHostInitialWidth = 1280;
        private static int _externalHostInitialHeight = 720;


        private static string _assetRootPath = Path.Combine(AppContext.BaseDirectory, "Assets");
        private static ResourceLoadTiming _resourceLoadTiming = ResourceLoadTiming.Preload;
        private static string? _editorAssemblyPath;
        private static AssemblyLoadContext? _editorLoadContext;
        private static Assembly? _editorAssembly;
        private static Type? _editorEntryType;
        private static MethodInfo? _editorConfigureMethod;
        private static MethodInfo? _editorStartMethod;
        private static MethodInfo? _editorRunMethod;
        private static MethodInfo? _editorStopMethod;
        private static bool _editorStarted = false;
        static ConcurrentQueue<Action> _editorHostActionQueue = new ConcurrentQueue<Action>();
        private static readonly object _editorFrameSync = new object();
        private static byte[]? _editorLatestFramePixels;
        private static int _editorLatestFrameWidth;
        private static int _editorLatestFrameHeight;
        private static bool _editorLatestFrameDirty;

        // Lua脚本实例列表
        static List<LuaScriptInstance> _luaScriptInstances = new List<LuaScriptInstance>();
        static Script _syntaxCheckScript = new Script(CoreModules.None);
        static List<EditorHostBridge.LuaApiMetadata> _luaApiMeta = new List<EditorHostBridge.LuaApiMetadata>();

        static void RecordLuaApi(string name, string signature, string description, string category)
        {
            _luaApiMeta.Add(new EditorHostBridge.LuaApiMetadata
            {
                Name = name,
                Signature = signature,
                Description = description,
                Category = category
            });
        }

        static void ParseLineColumnFromDecoratedMessage(string decoratedMessage, out int line, out int column)
        {
            line = 1;
            column = 1;
            if (string.IsNullOrEmpty(decoratedMessage))
                return;

            var match = Regex.Match(decoratedMessage, @"\((\d+),(\d+)(?:-(\d+))?\)");
            if (match.Success)
            {
                int.TryParse(match.Groups[1].Value, out line);
                int.TryParse(match.Groups[2].Value, out column);
                return;
            }

            int firstColon = decoratedMessage.IndexOf(':');
            if (firstColon < 0)
                return;

            int secondColon = decoratedMessage.IndexOf(':', firstColon + 1);
            if (secondColon < 0)
                return;

            string lineStr = decoratedMessage.Substring(firstColon + 1, secondColon - firstColon - 1);
            int.TryParse(lineStr, out line);
        }

        // 主窗口实例
        static IWindow? _window;
        // OpenGL实例
        static GL? _gl;

        // 主线程任务队列
        static BlockingCollection<Action> _taskQueue = new BlockingCollection<Action>();
        // 任务结果表
        static ConcurrentDictionary<int, TaskCompletionSource<DynValue>> _taskResults = new ConcurrentDictionary<int, TaskCompletionSource<DynValue>>();
        // 任务ID
        static int _nextTaskId = 0;
        // 图形系统实例
        static Graphics? _graphics;
        // 音频系统实例
        static Audio? _audio;
        // 纹理路径列表
        internal static List<string> _texturePaths = new List<string>();
        // Shader顶点文件表
        internal static List<string> _shaderVertexFiles = new List<string>();
        // UI布局表
        internal static Dictionary<string, List<CanvasElement>> _uiLayouts = new Dictionary<string, List<CanvasElement>>();
        // 当前激活的UI布局Key
        static string? _activeUILayoutKey = null;
        // 当前激活的UI根节点列表
        static List<CanvasElement>? _activeUILayoutRoots = null;
        // 场景文件注册表
        internal static Dictionary<string, string> _sceneFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 材质文件注册表
        internal static Dictionary<string, string> _materialFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 材质生成注册表
        internal static Dictionary<string, string> _generatedMaterialJsonRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 纹理文件注册表
        internal static Dictionary<string, string> _textureFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 音频文件注册表
        internal static Dictionary<string, string> _audioFileRegistry = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 场景文件显示名表
        static Dictionary<string, string> _sceneFileDisplayName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // 输入系统实例
        static Input? _input;

        // 上一帧时间
        private static double _lastFrameTime;
        // 运行时暂停状态
        private static bool _runtimePaused = false;
        // 待执行步进帧数
        private static int _pendingStepFrameCount = 0;
        // 固定步长
        private const float FixedDeltaTime = 0.02f;
        // 游戏启动文件夹
        private static string _gameStartupFolder = AppContext.BaseDirectory;

        // 启动Logo显示选项
        static bool _showStartupLogo = true;
        // 启动Logo路径
        static string? _startupLogoPath = null;
        // 启动画面背景色
        static Color _startupBackgroundColor = Color.SkyBlue;

        // 窗口标题段
        private static string _windowBaseTitle = "";
        // 标题统计累计时间
        private static double _titleStatAccumulatedSeconds = 0.0;
        // 标题统计累计帧数
        private static int _titleStatFrameCount = 0;
        // 标题显示FPS
        private static int _titleDisplayedFps = 0;
        // 标题立即刷新标志
        private static bool _windowTitleDirty = true;

        private static string? _externalControlFilePath;
        private static string? _externalStatusFilePath;

        private static readonly object _assetDiscoveryLock = new object();
        private static readonly HashSet<string> _lazyTextureKeyDiscoveryCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _lazyAudioKeyDiscoveryCompleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions _canvasLayoutJsonReadOptions = new JsonSerializerOptions
        {
            Converters = { new Vector4JsonConverter() },
            PropertyNameCaseInsensitive = true
        };

        static string NormalizeAssetLookupKey(string raw, bool removeExtension = false)
        {
            string key = (raw ?? string.Empty).Replace('\\', '/').Trim();

            while (key.StartsWith("/"))
                key = key[1..];

            if (key.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                key = key["Assets/".Length..];

            if (key.StartsWith("EditorAssets/", StringComparison.OrdinalIgnoreCase))
                key = key["EditorAssets/".Length..];

            if (removeExtension)
            {
                string ext = Path.GetExtension(key);
                if (!string.IsNullOrEmpty(ext))
                    key = key[..^ext.Length];
            }

            return key;
        }

        static string GetAssetPathFromKey(string key, string extensionWithDot)
        {
            string normalizedKey = NormalizeAssetLookupKey(key, removeExtension: false);
            string path = Path.Combine(
                _assetRootPath,
                normalizedKey.Replace('/', Path.DirectorySeparatorChar));

            if (!string.IsNullOrEmpty(extensionWithDot) &&
                !path.EndsWith(extensionWithDot, StringComparison.OrdinalIgnoreCase))
            {
                path += extensionWithDot;
            }

            return path;
        }

        static bool TryRegisterSceneFile(string filePath, out string sceneId)
        {
            sceneId = string.Empty;

            if (!TryValidateSceneFile(filePath, out sceneId, out _))
                return false;

            _sceneFileRegistry[sceneId] = filePath;
            _sceneFileDisplayName[sceneId] = Path.GetFileName(filePath);
            return true;
        }

        internal static bool EnsureSceneRegistered(string sceneId, out string filePath)
        {
            filePath = string.Empty;

            string normalizedSceneId = (sceneId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedSceneId))
                return false;

            if (_sceneFileRegistry.TryGetValue(normalizedSceneId, out filePath))
                return File.Exists(filePath);

            lock (_assetDiscoveryLock)
            {
                if (_sceneFileRegistry.TryGetValue(normalizedSceneId, out filePath))
                    return File.Exists(filePath);

                if (!Directory.Exists(_assetRootPath))
                    return false;

                string[] jsonFiles = Directory.GetFiles(_assetRootPath, "*.json", SearchOption.AllDirectories);
                Array.Sort(jsonFiles, StringComparer.OrdinalIgnoreCase);

                foreach (string jsonFile in jsonFiles)
                {
                    if (!TryRegisterSceneFile(jsonFile, out string discoveredSceneId))
                        continue;

                    if (string.Equals(discoveredSceneId, normalizedSceneId, StringComparison.Ordinal))
                    {
                        filePath = _sceneFileRegistry[discoveredSceneId];
                        Console.WriteLine($"[i] Lazily registered scene: {discoveredSceneId} -> {filePath}");
                        return true;
                    }
                }

                Console.WriteLine($"[!] Scene not found: id='{normalizedSceneId}', root='{_assetRootPath}', scanned={jsonFiles.Length} files");
                return false;
            }
        }

        internal static bool EnsureMaterialRegistered(string materialKey, out string filePath)
        {
            filePath = string.Empty;

            string key = NormalizeAssetLookupKey(materialKey, removeExtension: true);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_materialFileRegistry.TryGetValue(key, out filePath))
                return File.Exists(filePath);

            lock (_assetDiscoveryLock)
            {
                if (_materialFileRegistry.TryGetValue(key, out filePath))
                    return File.Exists(filePath);

                string candidate = GetAssetPathFromKey(key, ".json");
                if (!File.Exists(candidate))
                    return false;

                if (!TryValidateMaterialFile(candidate, out _))
                    return false;

                _materialFileRegistry[key] = candidate;
                filePath = candidate;
                Console.WriteLine($"[i] Lazily registered material: {key} -> {candidate}");
                return true;
            }
        }

        internal static bool EnsureTextureRegistered(string textureKey, out string filePath)
        {
            filePath = string.Empty;

            string key = NormalizeAssetLookupKey(textureKey, removeExtension: false);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_textureFileRegistry.TryGetValue(key, out filePath))
                return File.Exists(filePath);

            lock (_assetDiscoveryLock)
            {
                if (_textureFileRegistry.TryGetValue(key, out filePath))
                    return File.Exists(filePath);

                string candidate = GetAssetPathFromKey(key, "");
                if (!File.Exists(candidate))
                    return false;

                string ext = Path.GetExtension(candidate);
                bool isTexture =
                    ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

                if (!isTexture)
                    return false;

                _textureFileRegistry[key] = candidate;

                if (!_texturePaths.Contains(key, StringComparer.OrdinalIgnoreCase))
                    _texturePaths.Add(key);

                filePath = candidate;
                Console.WriteLine($"[i] Lazily registered texture: {key} -> {candidate}");
                return true;
            }
        }

        static void EnsureAllTextureKeysDiscovered()
        {
            lock (_assetDiscoveryLock)
            {
                if (_lazyTextureKeyDiscoveryCompleted.Contains(_assetRootPath))
                    return;

                if (!Directory.Exists(_assetRootPath))
                {
                    _lazyTextureKeyDiscoveryCompleted.Add(_assetRootPath);
                    return;
                }

                string[] files = Directory.GetFiles(_assetRootPath, "*.*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file);

                    bool isTexture =
                        ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

                    if (!isTexture)
                        continue;

                    string key = BuildAssetKey(_assetRootPath, file, removeExtension: false);
                    _textureFileRegistry[key] = file;

                    if (!_texturePaths.Contains(key, StringComparer.OrdinalIgnoreCase))
                        _texturePaths.Add(key);
                }

                _lazyTextureKeyDiscoveryCompleted.Add(_assetRootPath);
            }
        }

        static void EnsureAllAudioClipKeysDiscovered()
        {
            lock (_assetDiscoveryLock)
            {
                if (_lazyAudioKeyDiscoveryCompleted.Contains(_assetRootPath))
                    return;

                if (!Directory.Exists(_assetRootPath))
                {
                    _lazyAudioKeyDiscoveryCompleted.Add(_assetRootPath);
                    return;
                }

                string[] files = Directory.GetFiles(_assetRootPath, "*.*", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);

                foreach (string file in files)
                {
                    string ext = Path.GetExtension(file);

                    bool isAudio =
                        ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase);

                    if (!isAudio)
                        continue;

                    string key = BuildAssetKey(_assetRootPath, file, removeExtension: false);
                    _audioFileRegistry[key] = file;
                    _audio?.RegisterAudioFile(key, file);
                }

                _lazyAudioKeyDiscoveryCompleted.Add(_assetRootPath);
            }
        }

        static bool EnsureUILayoutLoaded(string layoutKey, out List<CanvasElement>? roots)
        {
            roots = null;

            string key = NormalizeAssetLookupKey(layoutKey, removeExtension: true);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_uiLayouts.TryGetValue(key, out roots) && roots != null)
                return true;

            lock (_assetDiscoveryLock)
            {
                if (_uiLayouts.TryGetValue(key, out roots) && roots != null)
                    return true;

                string candidate = GetAssetPathFromKey(key, ".json");
                if (!File.Exists(candidate))
                    return false;

                if (!TryLoadCanvasLayoutFile(candidate, _canvasLayoutJsonReadOptions, out roots, out _))
                    return false;

                _uiLayouts[key] = roots;
                Console.WriteLine($"[i] Lazily loaded UI layout: {key} -> {candidate}");
                return true;
            }
        }

        internal static bool EnsureShaderRegistered(string shaderKey, out string vertexShaderPath)
        {
            vertexShaderPath = string.Empty;

            string key = NormalizeAssetLookupKey(shaderKey, removeExtension: true);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string? existingPath = _shaderVertexFiles.FirstOrDefault(p => string.Equals(
                BuildAssetKey(_assetRootPath, p, removeExtension: true),
                key,
                StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(existingPath))
            {
                vertexShaderPath = existingPath;
                return File.Exists(vertexShaderPath);
            }

            lock (_assetDiscoveryLock)
            {
                existingPath = _shaderVertexFiles.FirstOrDefault(p => string.Equals(
                    BuildAssetKey(_assetRootPath, p, removeExtension: true),
                    key,
                    StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(existingPath))
                {
                    vertexShaderPath = existingPath;
                    return File.Exists(vertexShaderPath);
                }

                string[] vertexCandidates =
                [
                    GetAssetPathFromKey(key, ".vert"),
            GetAssetPathFromKey(key, ".vs")
                ];

                foreach (string candidate in vertexCandidates)
                {
                    if (!File.Exists(candidate))
                        continue;

                    string normalizedCandidateKey = BuildAssetKey(_assetRootPath, candidate, removeExtension: true);

                    if (!_shaderVertexFiles.Any(p => string.Equals(
                        BuildAssetKey(_assetRootPath, p, removeExtension: true),
                        normalizedCandidateKey,
                        StringComparison.OrdinalIgnoreCase)))
                    {
                        _shaderVertexFiles.Add(candidate);
                    }

                    vertexShaderPath = candidate;
                    Console.WriteLine($"[i] Lazily registered shader: {key} -> {candidate}");
                    return true;
                }

                return false;
            }
        }

        internal static bool EnsureObjMeshRegistered(string meshKey, Graphics graphics)
        {
            string key = NormalizeAssetLookupKey(meshKey, removeExtension: true);
            if (string.IsNullOrWhiteSpace(key))
                return false;

            string candidate = GetAssetPathFromKey(key, ".obj");
            if (!File.Exists(candidate))
                return false;

            try
            {
                graphics.RegisterObjMeshFromFile(_assetRootPath, candidate);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to lazily register OBJ mesh '{key}': {ex.Message}");
                return false;
            }
        }

        internal static string AssetRootPathForShaderLoading => _assetRootPath;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetParent(nint hWndChild, nint hWndNewParent);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool MoveWindow(nint hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        private const int GWL_STYLE = -16;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);

        [System.Runtime.InteropServices.DllImport("libX11")]
        private static extern int XReparentWindow(nint display, nint w, nint parent, int x, int y);

        [System.Runtime.InteropServices.DllImport("libX11")]
        private static extern int XResizeWindow(nint display, nint w, uint width, uint height);

        [System.Runtime.InteropServices.DllImport("libX11")]
        private static extern int XMapWindow(nint display, nint w);

        [System.Runtime.InteropServices.DllImport("libX11")]
        private static extern int XFlush(nint display);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern nint SetFocus(nint hWnd);

        [System.Runtime.InteropServices.DllImport("libX11")]
        private static extern int XSetInputFocus(nint display, nint focus, int revert_to, nint time);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(nint hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetActiveWindow(nint hWnd);

        private const int RevertToParent = 2;

        /// <summary>
        /// 显示启动Logo
        /// </summary>
        static void ShowStartupLogo()
        {
            if (!_showStartupLogo || _window == null || _gl == null)
                return;

            byte[]? logoBytes = null;

            if (!string.IsNullOrWhiteSpace(_startupLogoPath))
            {
                try
                {
                    string fullPath = Path.Combine(AppContext.BaseDirectory, _startupLogoPath);
                    if (File.Exists(fullPath))
                        logoBytes = File.ReadAllBytes(fullPath);
                }
                catch
                {
                    logoBytes = null;
                }
            }

            if (logoBytes == null)
                logoBytes = Properties.Resources.LimitlessSquareEngineLogo;

            uint texture = 0;
            uint vao = 0;
            uint program = 0;
            uint vertexShader = 0;
            uint fragmentShader = 0;

            try
            {
                using var codec = SKCodec.Create(new SKMemoryStream(logoBytes));
                if (codec == null)
                    return;

                var info = new SKImageInfo(
                    codec.Info.Width,
                    codec.Info.Height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Unpremul);

                byte[] pixelBytes = new byte[info.Width * info.Height * 4];
                var result = codec.GetPixels(info, pixelBytes);

                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                {
                    Console.WriteLine($"[X] Failed to decode startup logo: {result}");
                    return;
                }

                int imageWidth = info.Width;
                int imageHeight = info.Height;

                float windowWidth = _window.Size.X;
                float windowHeight = _window.Size.Y;
                float boxSize = MathF.Min(windowWidth, windowHeight);

                float scale = MathF.Min(boxSize / imageWidth, boxSize / imageHeight);
                float drawWidth = imageWidth * scale;
                float drawHeight = imageHeight * scale;

                float left = (windowWidth - drawWidth) * 0.5f;
                float top = (windowHeight - drawHeight) * 0.5f;
                float right = left + drawWidth;
                float bottom = top + drawHeight;

                float x1 = left / windowWidth * 2f - 1f;
                float x2 = right / windowWidth * 2f - 1f;
                float y1 = 1f - top / windowHeight * 2f;
                float y2 = 1f - bottom / windowHeight * 2f;

                const string vertexSource = @"
                    #version 330 core
                    uniform vec4 uRect;
                    out vec2 vUv;

                    void main()
                    {
                        vec2 pos[6] = vec2[6](
                            vec2(uRect.x, uRect.y),
                            vec2(uRect.x, uRect.w),
                            vec2(uRect.z, uRect.y),
                            vec2(uRect.z, uRect.y),
                            vec2(uRect.x, uRect.w),
                            vec2(uRect.z, uRect.w)
                        );

                        vec2 uv[6] = vec2[6](
                            vec2(0.0, 0.0),
                            vec2(0.0, 1.0),
                            vec2(1.0, 0.0),
                            vec2(1.0, 0.0),
                            vec2(0.0, 1.0),
                            vec2(1.0, 1.0)
                        );

                        gl_Position = vec4(pos[gl_VertexID], 0.0, 1.0);
                        vUv = uv[gl_VertexID];
                    }";

                const string fragmentSource = @"
                    #version 330 core
                    in vec2 vUv;
                    uniform sampler2D uTexture;
                    out vec4 FragColor;

                    void main()
                    {
                        FragColor = texture(uTexture, vUv);
                    }";

                vertexShader = _gl.CreateShader(ShaderType.VertexShader);
                _gl.ShaderSource(vertexShader, vertexSource);
                _gl.CompileShader(vertexShader);
                _gl.GetShader(vertexShader, ShaderParameterName.CompileStatus, out int vertexCompiled);
                if (vertexCompiled == 0)
                {
                    Console.WriteLine("[X] Startup logo vertex shader compile failed:");
                    Console.WriteLine(_gl.GetShaderInfoLog(vertexShader));
                    return;
                }

                fragmentShader = _gl.CreateShader(ShaderType.FragmentShader);
                _gl.ShaderSource(fragmentShader, fragmentSource);
                _gl.CompileShader(fragmentShader);
                _gl.GetShader(fragmentShader, ShaderParameterName.CompileStatus, out int fragmentCompiled);
                if (fragmentCompiled == 0)
                {
                    Console.WriteLine("[X] Startup logo fragment shader compile failed:");
                    Console.WriteLine(_gl.GetShaderInfoLog(fragmentShader));
                    return;
                }

                program = _gl.CreateProgram();
                _gl.AttachShader(program, vertexShader);
                _gl.AttachShader(program, fragmentShader);
                _gl.LinkProgram(program);
                _gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
                if (linked == 0)
                {
                    Console.WriteLine("[X] Startup logo shader program link failed:");
                    Console.WriteLine(_gl.GetProgramInfoLog(program));
                    return;
                }

                texture = _gl.GenTexture();
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(TextureTarget.Texture2D, texture);
                _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba8,
                    (uint)imageWidth,
                    (uint)imageHeight,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    in pixelBytes[0]);

                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);

                vao = _gl.GenVertexArray();
                _gl.BindVertexArray(vao);

                _gl.Viewport(0, 0, (uint)Math.Max(1, _window.Size.X), (uint)Math.Max(1, _window.Size.Y));
                _gl.Disable(EnableCap.DepthTest);
                _gl.Clear(ClearBufferMask.ColorBufferBit);

                _gl.UseProgram(program);

                int rectLocation = _gl.GetUniformLocation(program, "uRect");
                int texLocation = _gl.GetUniformLocation(program, "uTexture");

                _gl.Uniform4(rectLocation, x1, y1, x2, y2);
                _gl.Uniform1(texLocation, 0);

                _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);

                _gl.Finish();
                _window.SwapBuffers();
                _window.DoEvents();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[X] Failed to show startup logo: {ex.Message}");
            }
            finally
            {
                if (vao != 0)
                    _gl.DeleteVertexArray(vao);
                if (texture != 0)
                    _gl.DeleteTexture(texture);
                if (program != 0)
                    _gl.DeleteProgram(program);
                if (vertexShader != 0)
                    _gl.DeleteShader(vertexShader);
                if (fragmentShader != 0)
                    _gl.DeleteShader(fragmentShader);
            }
        }

        /// <summary>
        /// 关闭启动Logo
        /// </summary>
        static void CloseStartupLogo()
        {
            _graphics?.ClearBackground();
            _window?.SwapBuffers();
        }

        static void WriteExternalHostStatus()
        {
            if (!_isExternalHostMode)
                return;

            if (_window == null)
                return;

            if (string.IsNullOrWhiteSpace(_externalStatusFilePath))
                return;

            try
            {
                RenderFrameDiagnostics diagnostics = _graphics?.GetLastRenderFrameDiagnostics() ?? default;
                string gpuMilliseconds = diagnostics.GpuTimeAvailable
                    ? diagnostics.GpuFrameMilliseconds.ToString("F2", CultureInfo.InvariantCulture)
                    : "-1";

                string text = string.Join("|",
                    _window.Size.X.ToString(CultureInfo.InvariantCulture),
                    _window.Size.Y.ToString(CultureInfo.InvariantCulture),
                    _titleDisplayedFps.ToString(CultureInfo.InvariantCulture),
                    gpuMilliseconds,
                    diagnostics.DrawCalls.ToString(CultureInfo.InvariantCulture),
                    diagnostics.DrawnVertices.ToString(CultureInfo.InvariantCulture),
                    diagnostics.DrawnTriangles.ToString(CultureInfo.InvariantCulture),
                    diagnostics.CulledCommands.ToString(CultureInfo.InvariantCulture),
                    diagnostics.SubmittedCommands.ToString(CultureInfo.InvariantCulture));
                File.WriteAllText(_externalStatusFilePath, text);
            }
            catch
            {
            }
        }

        static void UpdateWindowTitleNow()
        {
            if (_window == null)
                return;

            string baseTitle = string.IsNullOrWhiteSpace(_windowBaseTitle)
                ? (Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Limitless Square Engine")
                : _windowBaseTitle;

            int width = _window.Size.X;
            int height = _window.Size.Y;

            _window.Title = $"{baseTitle}  <>  {width}x{height}  |  FPS {_titleDisplayedFps}";
            _windowTitleDirty = false;
            WriteExternalHostStatus();
        }

        static void TickWindowTitle(float deltaTime)
        {
            if (_window == null)
                return;

            if (_runtimePaused)
            {
                _titleStatAccumulatedSeconds = 0.0;
                _titleStatFrameCount = 0;

                if (_titleDisplayedFps != 0 || _windowTitleDirty)
                {
                    _titleDisplayedFps = 0;
                    UpdateWindowTitleNow();
                }

                return;
            }

            _titleStatAccumulatedSeconds += deltaTime;
            _titleStatFrameCount++;

            if (_windowTitleDirty)
            {
                UpdateWindowTitleNow();
                return;
            }

            if (_titleStatAccumulatedSeconds >= 1.0)
            {
                _titleDisplayedFps = (int)MathF.Round(_titleStatFrameCount / (float)_titleStatAccumulatedSeconds);

                _titleStatAccumulatedSeconds = 0.0;
                _titleStatFrameCount = 0;

                UpdateWindowTitleNow();
            }
        }

        static bool TryPrepareRuntimeFrameTiming(out float deltaTime, out float fixedDeltaTime)
        {
            deltaTime = 0f;
            fixedDeltaTime = FixedDeltaTime;

            if (_window == null)
                return false;

            double currentTime = _window.Time;
            float realtimeDelta = (float)(currentTime - _lastFrameTime);

            _lastFrameTime = currentTime;

            if (realtimeDelta < 0f)
                realtimeDelta = 0f;

            if (_runtimePaused)
            {
                if (_pendingStepFrameCount > 0)
                {
                    _pendingStepFrameCount--;
                    deltaTime = FixedDeltaTime;
                    return true;
                }

                deltaTime = 0f;
                return false;
            }

            deltaTime = realtimeDelta;
            return true;
        }

        static string ResolveDefaultAssetRootPath()
        {
            if (_isEditorMode)
                return Path.Combine(AppContext.BaseDirectory, "EditorAssets");

            string assetsPath = Path.Combine(_gameStartupFolder, "Assets");
            if (Directory.Exists(assetsPath))
                return assetsPath;

            return _gameStartupFolder;
        }

        static string NormalizeAssetRootPath(string path)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);

            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        }

        static void BindEditorEntryMethods(Assembly assembly)
        {
            _editorEntryType = null;
            _editorConfigureMethod = null;
            _editorStartMethod = null;
            _editorRunMethod = null;
            _editorStopMethod = null;

            foreach (Type type in assembly.GetTypes())
            {
                MethodInfo? configureMethod = type.GetMethod(
                    "Configure",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(EditorLaunchOptions) },
                    null);

                MethodInfo? startMethod = type.GetMethod(
                    "Start",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(EditorHostBootstrapInfo) },
                    null);

                MethodInfo? runMethod = type.GetMethod(
                    "Run",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                MethodInfo? stopMethod = type.GetMethod(
                    "Stop",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    Type.EmptyTypes,
                    null);

                if (configureMethod == null && startMethod == null && runMethod == null && stopMethod == null)
                    continue;

                _editorEntryType = type;
                _editorConfigureMethod = configureMethod;
                _editorStartMethod = startMethod;
                _editorRunMethod = runMethod;
                _editorStopMethod = stopMethod;
                return;
            }

            throw new InvalidOperationException("Editor DLL is missing a public static entry point.");
        }

        static void EnsureEditorAssemblyLoaded()
        {
            if (!_isEditorMode)
                return;

            if (_editorAssembly != null)
                return;

            if (string.IsNullOrWhiteSpace(_editorAssemblyPath) || !File.Exists(_editorAssemblyPath))
                throw new FileNotFoundException("Editor assembly not found.", _editorAssemblyPath);

            _editorLoadContext = new AssemblyLoadContext("LimitlessSquareEditor", false);
            _editorAssembly = _editorLoadContext.LoadFromAssemblyPath(_editorAssemblyPath);
            BindEditorEntryMethods(_editorAssembly);
        }

        static void ConfigureEditorMode()
        {
            string defaultAssetRootPath = ResolveDefaultAssetRootPath();

            if (!_isEditorMode)
            {
                _assetRootPath = NormalizeAssetRootPath(defaultAssetRootPath);
                _resourceLoadTiming = ResourceLoadTiming.Lazy;
                Console.WriteLine($"[i] Asset root: {_assetRootPath}");
                Console.WriteLine($"[i] Resource load timing: {_resourceLoadTiming}");
                return;
            }

            EnsureEditorAssemblyLoaded();

            EditorLaunchOptions launchOptions = new EditorLaunchOptions
            {
                DefaultAssetRootPath = defaultAssetRootPath,
                AssetRootPath = defaultAssetRootPath,
                ResourceLoadTiming = ResourceLoadTiming.Preload
            };

            _editorConfigureMethod?.Invoke(null, new object?[] { launchOptions });

            string selectedAssetRootPath = string.IsNullOrWhiteSpace(launchOptions.AssetRootPath)
                ? launchOptions.DefaultAssetRootPath
                : launchOptions.AssetRootPath;

            _assetRootPath = NormalizeAssetRootPath(selectedAssetRootPath);
            _resourceLoadTiming = launchOptions.ResourceLoadTiming;
            Console.WriteLine($"[i] Asset root: {_assetRootPath}");
            Console.WriteLine($"[i] Resource load timing: {_resourceLoadTiming}");
        }

        static EditorEmbeddingMode ResolveEditorEmbeddingMode(INativeWindow nativeWindow)
        {
            if (nativeWindow.Win32.HasValue || nativeWindow.X11.HasValue)
                return EditorEmbeddingMode.ForeignChildWindow;

            if (nativeWindow.Cocoa.HasValue)
                return EditorEmbeddingMode.CocoaViewHost;

            if (nativeWindow.Wayland.HasValue)
                return EditorEmbeddingMode.NestedWaylandCompositor;

            return EditorEmbeddingMode.Unsupported;
        }

        static EditorHostBootstrapInfo BuildEditorHostBootstrapInfo()
        {
            if (_window == null)
                throw new InvalidOperationException("Engine window is not initialized.");

            INativeWindow nativeWindow = _window.Native;

            EditorHostBootstrapInfo bootstrapInfo = new EditorHostBootstrapInfo
            {
                AssetRootPath = _assetRootPath,
                EmbeddingMode = ResolveEditorEmbeddingMode(nativeWindow)
            };

            if (nativeWindow.Win32.HasValue)
                bootstrapInfo.Win32Hwnd = nativeWindow.Win32.Value.Hwnd;

            if (nativeWindow.Cocoa.HasValue)
            {
                bootstrapInfo.CocoaWindow = nativeWindow.Cocoa.Value;
                bootstrapInfo.CocoaContentView = CocoaNativeInterop.GetContentView(nativeWindow.Cocoa.Value);
            }

            if (nativeWindow.X11.HasValue)
            {
                bootstrapInfo.X11Display = nativeWindow.X11.Value.Display;
                bootstrapInfo.X11Window = nativeWindow.X11.Value.Window;
            }

            if (nativeWindow.Wayland.HasValue)
            {
                bootstrapInfo.WaylandDisplay = nativeWindow.Wayland.Value.Display;
                bootstrapInfo.WaylandSurface = nativeWindow.Wayland.Value.Surface;
            }

            if (nativeWindow.Glfw.HasValue)
                bootstrapInfo.GlfwWindow = nativeWindow.Glfw.Value;

            if (nativeWindow.Sdl.HasValue)
                bootstrapInfo.SdlWindow = nativeWindow.Sdl.Value;

            return bootstrapInfo;
        }

        static void QueueEditorHostAction(Action action)
        {
            if (action == null)
                return;

            _editorHostActionQueue.Enqueue(action);
        }

        static void ExecuteEditorHostActions()
        {
            while (_editorHostActionQueue.TryDequeue(out Action? action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[X] Editor host action failed: {ex.Message}");
                }
            }
        }

        static void SetRenderWindowVisibleCore(bool visible)
        {
            if (_window == null)
                return;

            if (_isEditorMode)
            {
                _window.IsVisible = false;
                return;
            }

            _window.IsVisible = visible;
        }

        static void SetRenderWindowSizeCore(int width, int height)
        {
            if (_window == null)
                return;

            int clampedWidth = Math.Max(1, width);
            int clampedHeight = Math.Max(1, height);

            _externalHostInitialWidth = clampedWidth;
            _externalHostInitialHeight = clampedHeight;

            _window.Size = new Silk.NET.Maths.Vector2D<int>(clampedWidth, clampedHeight);
            _windowTitleDirty = true;

            if (!_isExternalHostMode)
                return;

            INativeWindow native = _window.Native;

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.CocoaViewHost && native.Cocoa.HasValue)
            {
                nint contentView = CocoaNativeInterop.GetContentView(native.Cocoa.Value);
                if (contentView != 0)
                    CocoaNativeInterop.SetViewFrame(contentView, 0, 0, clampedWidth, clampedHeight);

                return;
            }

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.ForeignChildWindow)
            {
                if (native.Win32.HasValue)
                {
                    MoveWindow(native.Win32.Value.Hwnd, 0, 0, clampedWidth, clampedHeight, true);
                    return;
                }

                if (native.X11.HasValue)
                {
                    XResizeWindow(native.X11.Value.Display, (nint)native.X11.Value.Window, (uint)clampedWidth, (uint)clampedHeight);
                    XFlush(native.X11.Value.Display);
                    return;
                }
            }
        }

        static void RequestRenderWindowCloseCore()
        {
            if (_window == null)
                return;

            _window.IsClosing = true;
        }

        static bool IsRenderWindowAlive()
        {
            return _window != null && !_window.IsClosing;
        }

        static void SetRuntimePausedCore(bool paused)
        {
            _runtimePaused = paused;

            if (_window != null)
                _lastFrameTime = _window.Time;
        }

        static bool GetRuntimePausedCore()
        {
            return _runtimePaused;
        }

        static void StepRuntimeFrameCore()
        {
            if (!_runtimePaused)
                return;

            _pendingStepFrameCount++;
        }

        static void SetGameStartupFolderCore(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;

            if (Path.IsPathRooted(folderPath))
                _gameStartupFolder = Path.GetFullPath(folderPath);
            else
                _gameStartupFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, folderPath));
        }

        static string GetGameStartupFolderCore()
        {
            return _gameStartupFolder;
        }

        static void BindEditorHostBridge()
        {
            EditorHostBridge.Bind(
                BuildEditorHostBootstrapInfo,
                visible => QueueEditorHostAction(() => SetRenderWindowVisibleCore(visible)),
                (width, height) => QueueEditorHostAction(() => SetRenderWindowSizeCore(width, height)),
                () => QueueEditorHostAction(RequestRenderWindowCloseCore),
                IsRenderWindowAlive,
                Loop,
                sceneId => QueueEditorHostAction(() =>
                {
                    Scene.RemoveScene(sceneId);
                    _graphics?.ClearScreenSkybox();
                    Scene.LoadScene(sceneId);
                    Scene.RebuildCameraQueue(sceneId);
                }),
                sceneId => QueueEditorHostAction(() =>
                {
                    Scene.RemoveScene(sceneId);
                }),
                assetRootPath => QueueEditorHostAction(() =>
                {
                    SetAssetRootAndReloadAssetsCore(assetRootPath);
                }),
                (sceneId, objectId, value) => Scene.SetLocalPosition(sceneId, objectId, value),
                (sceneId, objectId, value) => Scene.SetLocalRotation(sceneId, objectId, value),
                (sceneId, objectId, value) => Scene.SetLocalScale(sceneId, objectId, value),
                ConsumeLatestFrameCore,
                (screenX, screenY) => _graphics?.RaycastRenderedMeshAtPixel(screenX, screenY) ?? new RenderedMeshRaycastHit
                {
                    Hit = false,
                    ScreenX = screenX,
                    ScreenY = screenY
                },
                (sceneId, objectId, enabled, r, g, b, thicknessPixels) => QueueEditorHostAction(() =>
                {
                    _graphics?.SetSceneObjectContour(sceneId, objectId, enabled, r, g, b, thicknessPixels);
                }),
                sceneId => QueueEditorHostAction(() =>
                {
                    _graphics?.ClearSceneContours(sceneId);
                }),
                paused => QueueEditorHostAction(() => SetRuntimePausedCore(paused)), GetRuntimePausedCore,
                () => QueueEditorHostAction(StepRuntimeFrameCore),
                folderPath => QueueEditorHostAction(() => SetGameStartupFolderCore(folderPath)),
                GetGameStartupFolderCore,
                (worldPos, mode, hoveredAxis, visible) => QueueEditorHostAction(() =>
                {
                    _graphics?.SetGizmoState(worldPos, mode, hoveredAxis, visible);
                }),
                (sceneId, objectId) => Scene.GetPosition(sceneId, objectId),
                (sceneId, objectId, worldDelta) => Scene.WorldDeltaToLocalDelta(sceneId, objectId, worldDelta),
                () => _graphics?.GizmoSceneView ?? System.Numerics.Matrix4x4.Identity,
                () => _graphics?.GizmoSceneProjection ?? System.Numerics.Matrix4x4.Identity,
                hoveredAxis => { if (_graphics != null) _graphics.SetGizmoHover(hoveredAxis); },
                (dragging, activeAxis) => { if (_graphics != null) _graphics.SetGizmoDrag(dragging, activeAxis); },
                sourceCode =>
                {
                    var errors = new List<EditorHostBridge.LuaSyntaxError>();
                    try
                    {
                        _syntaxCheckScript.LoadString(sourceCode);
                    }
                    catch (SyntaxErrorException ex)
                    {
                        if (!ex.IsPrematureStreamTermination)
                        {
                            int line = 1;
                            int col = 1;
                            string msg = ex.DecoratedMessage ?? ex.Message;
                            ParseLineColumnFromDecoratedMessage(msg, out line, out col);
                            errors.Add(new EditorHostBridge.LuaSyntaxError
                            {
                                Line = line,
                                Column = col,
                                Message = msg,
                                IsPrematureStreamTermination = false
                            });
                        }
                    }
                    return errors.ToArray();
                },
                () => _luaApiMeta.ToArray());
        }

        static void UnbindEditorHostBridge()
        {
            EditorHostBridge.Unbind();
        }

        static void ClearRegisteredAssets()
        {
            _sceneFileRegistry.Clear();
            _sceneFileDisplayName.Clear();
            _materialFileRegistry.Clear();
            _generatedMaterialJsonRegistry.Clear();
            _textureFileRegistry.Clear();
            _texturePaths.Clear();
            _audioFileRegistry.Clear();
            _audio?.ClearFileRegistry();
            _uiLayouts.Clear();
            _shaderVertexFiles.Clear();
            _lazyTextureKeyDiscoveryCompleted.Clear();
            _lazyAudioKeyDiscoveryCompleted.Clear();
        }

        static void PreloadRegisteredAssets(Graphics graphics, string assetsPath)
        {
            var options = new JsonSerializerOptions
            {
                Converters = { new Vector4JsonConverter() },
                PropertyNameCaseInsensitive = true
            };

            ClearRegisteredAssets();

            string[] allFiles = Directory.GetFiles(assetsPath, "*.*", SearchOption.AllDirectories);
            Array.Sort(allFiles, StringComparer.OrdinalIgnoreCase);

            foreach (string file in allFiles)
            {
                string ext = Path.GetExtension(file);

                if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (TryValidateSceneFile(file, out string sceneId, out string sceneReason))
                        {
                            if (_sceneFileRegistry.TryGetValue(sceneId, out string? oldPath))
                            {
                                Console.WriteLine($"[!] Duplicate scene id '{sceneId}' found. Replacing:");
                                Console.WriteLine($"    Old: {oldPath}");
                                Console.WriteLine($"    New: {file}");
                            }

                            _sceneFileRegistry[sceneId] = file;
                            _sceneFileDisplayName[sceneId] = Path.GetFileName(file);

                            Console.WriteLine($"[i] Registered scene: {sceneId} -> {file}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Scene scan failed for {file}: {ex.Message}");
                    }

                    try
                    {
                        if (TryValidateMaterialFile(file, out string materialReason))
                        {
                            string key = BuildAssetKey(assetsPath, file, removeExtension: true);
                            _materialFileRegistry[key] = file;

                            Console.WriteLine($"[i] Registered material: {key} -> {file}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Material scan failed for {file}: {ex.Message}");
                    }

                    try
                    {
                        if (TryLoadCanvasLayoutFile(file, options, out List<CanvasElement>? elements, out string canvasReason)
                            && elements != null)
                        {
                            string key = BuildAssetKey(assetsPath, file, removeExtension: true);

                            if (_uiLayouts.ContainsKey(key))
                            {
                                Console.WriteLine($"[!] Duplicate UI layout key '{key}' found. Replacing with: {file}");
                            }

                            _uiLayouts[key] = elements;
                            Console.WriteLine($"[i] Loaded UI layout: {key} -> {file}");
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] UI layout scan failed for {file}: {ex.Message}");
                    }

                    Console.WriteLine($"[i] Unknown json asset skipped: {file}");
                    continue;
                }

                if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    string key = BuildAssetKey(assetsPath, file, removeExtension: false);

                    _textureFileRegistry[key] = file;
                    _texturePaths.Add(key);

                    Console.WriteLine($"[i] Registered texture: {key}");
                    continue;
                }

                if (ext.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
                {
                    string key = BuildAssetKey(assetsPath, file, removeExtension: false);

                    _audioFileRegistry[key] = file;
                    _audio?.RegisterAudioFile(key, file);

                    Console.WriteLine($"[i] Registered audio: {key}");
                    continue;
                }

                if (ext.Equals(".obj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        graphics.RegisterObjMeshFromFile(assetsPath, file);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Failed to scan OBJ mesh {file}: {ex.Message}");
                    }

                    continue;
                }

                if (IsShaderFile(file))
                {
                    string shaderKey = BuildAssetKey(assetsPath, file, removeExtension: false);

                    Console.WriteLine($"[i] Found shader file: {shaderKey}");

                    if (ext.Equals(".vert", StringComparison.OrdinalIgnoreCase))
                    {
                        _shaderVertexFiles.Add(file);
                    }

                    continue;
                }
            }

            Console.WriteLine($"[i] Asset scan completed. Scenes={_sceneFileRegistry.Count}, Materials={_materialFileRegistry.Count}, UI={_uiLayouts.Count}, Textures={_textureFileRegistry.Count}, Audio={_audioFileRegistry.Count}");
            graphics.LoadShaders(_shaderVertexFiles, assetsPath);
            graphics.ConfigureDefaultMainCameraFog();
        }

        static void ReloadRegisteredAssetsForCurrentTiming()
        {
            if (_graphics == null)
                return;

            string assetsPath = _assetRootPath;

            if (!Directory.Exists(assetsPath))
            {
                ClearRegisteredAssets();
                return;
            }

            if (_resourceLoadTiming == ResourceLoadTiming.Preload)
            {
                PreloadRegisteredAssets(_graphics, assetsPath);
                return;
            }

            ClearRegisteredAssets();
            _graphics.ConfigureDefaultMainCameraFog();
            Console.WriteLine("[i] Asset scan skipped. Resource load timing: Lazy");
        }

        static void SetAssetRootAndReloadAssetsCore(string assetRootPath)
        {
            _assetRootPath = NormalizeAssetRootPath(assetRootPath);

            try
            {
                Directory.CreateDirectory(_assetRootPath);
            }
            catch
            {
            }

            ReloadRegisteredAssetsForCurrentTiming();
        }

        static EditorRenderedFrame? ConsumeLatestFrameCore()
        {
            lock (_editorFrameSync)
            {
                if (!_editorLatestFrameDirty || _editorLatestFramePixels == null)
                    return null;

                EditorRenderedFrame frame = new EditorRenderedFrame
                {
                    Width = _editorLatestFrameWidth,
                    Height = _editorLatestFrameHeight,
                    PixelsRgba = _editorLatestFramePixels
                };

                _editorLatestFramePixels = null;
                _editorLatestFrameWidth = 0;
                _editorLatestFrameHeight = 0;
                _editorLatestFrameDirty = false;

                return frame;
            }
        }

        static void CaptureEditorFrameCore()
        {
            if (!_isEditorMode || _gl == null || _window == null)
                return;

            int width = Math.Max(1, _window.Size.X);
            int height = Math.Max(1, _window.Size.Y);

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

            lock (_editorFrameSync)
            {
                _editorLatestFramePixels = pixels;
                _editorLatestFrameWidth = width;
                _editorLatestFrameHeight = height;
                _editorLatestFrameDirty = true;
            }
        }

        static void StartEditorIfNeeded()
        {
            if (!_isEditorMode || _editorStarted)
                return;

            if (_window == null || _gl == null || _graphics == null)
                throw new InvalidOperationException("Engine host is not ready.");

            EnsureEditorAssemblyLoaded();

            if (_editorStartMethod == null)
                throw new InvalidOperationException("Editor DLL is missing Start(EditorHostBootstrapInfo).");

            EditorHostBootstrapInfo bootstrapInfo = BuildEditorHostBootstrapInfo();

            if (bootstrapInfo.EmbeddingMode == EditorEmbeddingMode.Unsupported)
                throw new InvalidOperationException("Current platform does not provide a supported editor embedding mode.");

            if (bootstrapInfo.EmbeddingMode == EditorEmbeddingMode.CocoaViewHost && bootstrapInfo.CocoaContentView == 0)
                throw new InvalidOperationException("Cocoa content view is not available.");

            Console.WriteLine($"[i] Editor embedding mode: {bootstrapInfo.EmbeddingMode}");

            _editorStartMethod.Invoke(null, new object?[] { bootstrapInfo });
            _editorStarted = true;
        }

        static void RunEditorIfNeeded()
        {
            if (!_isEditorMode)
                return;

            EnsureEditorAssemblyLoaded();

            if (_editorRunMethod == null)
                throw new InvalidOperationException("Editor DLL is missing Run().");

            _editorRunMethod.Invoke(null, null);
        }

        static void StopEditorIfNeeded()
        {
            if (!_editorStarted)
                return;

            _editorStopMethod?.Invoke(null, null);
            _editorStarted = false;
        }

        /// <summary>
        /// 初始化程序
        /// </summary>
        static void Initialize()
        {
            // 基础目录结构创建
            try
            {
                Directory.CreateDirectory(_assetRootPath);
            }
            catch 
            {

            }

            // 注册数据类型
            UserData.RegisterType<GameData>();
            UserData.RegisterType<Graphics>();
            UserData.RegisterType<RenderedMeshRaycastHit>();
            UserData.RegisterType<SceneData>();
            UserData.RegisterType<SceneObject>();
            UserData.RegisterType<SceneTransform>();
            UserData.RegisterType<Double3>();
            UserData.RegisterType<Input>();
            UserData.RegisterType<PhysicsRaycastHit>();
            UserData.RegisterType<LimitlessSquareEngine.Engine.Terrain.Terrain>();
            UserData.RegisterType<LimitlessSquareEngine.Engine.Terrain.TerrainProfile>();
            UserData.RegisterType<LimitlessSquareEngine.Engine.Terrain.TerrainStreamer>();
            // 初始化窗口参数
            var options = WindowOptions.Default;
            options.PreferredDepthBufferBits = 32;
            options.PreferredStencilBufferBits = 8;
            _windowBaseTitle = Path.GetFileNameWithoutExtension(Environment.ProcessPath) ?? "Limitless Square Engine";
            options.Title = _windowBaseTitle;
            options.IsVisible = !_isEditorMode && !_isExternalHostMode;
            options.ShouldSwapAutomatically = false;
            options.Size = new Silk.NET.Maths.Vector2D<int>(
                Math.Max(1, _externalHostInitialWidth),
                Math.Max(1, _externalHostInitialHeight));
            _window = Window.Create(options);

            // 窗口加载事件
            _window.Load += () =>
            {
                // 图标数据
                byte[]? iconBytes = TryLoadIconFromBaseDirectory();

                // 如果外部图标解码失败或未找到则使用默认图标
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
                _gl.ClearColor(_startupBackgroundColor);
                var graphics = new Graphics(_gl, _window);
                graphics.Initialize();
                _graphics = graphics;
                Scene.BindGraphics(graphics);
                _audio = new Audio();
                _audio.Initialize();
                Scene.BindAudio(_audio);
                BindEditorHostBridge();
                AttachWindowToExternalHost();

                // 初始化帧时间   
                _lastFrameTime = _window.Time;

                _titleDisplayedFps = 0;
                _titleStatAccumulatedSeconds = 0.0;
                _titleStatFrameCount = 0;
                _windowTitleDirty = true;
                UpdateWindowTitleNow();

                // 显示启动Logo
                ShowStartupLogo();
                _window?.DoRender();

                // 任务提交函数
                Func<string, int> submitTaskFunc = (luaCode) =>
                {
                    int taskId = Interlocked.Increment(ref _nextTaskId);
                    var tcs = new TaskCompletionSource<DynValue>();
                    _taskResults[taskId] = tcs;

                    _taskQueue.Add(() =>
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

                // 获取后台任务结果函数
                Func<int, DynValue[]> getTaskResultFunc = (taskId) =>
                {
                    if (_taskResults.TryGetValue(taskId, out var tcs))
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

                // 游戏数据实例
                GameData gameData = new GameData();

                // 输入系统实例
                _input = new Input(_window);

                // 扫描脚本目录
                string scriptPath = _assetRootPath;
                if (Directory.Exists(scriptPath))
                {
                    // 获取所有Lua脚本
                    string[] luaFiles = Directory.GetFiles(scriptPath, "*.lua", SearchOption.AllDirectories);
                    Array.Sort(luaFiles, StringComparer.OrdinalIgnoreCase);

                    foreach (string file in luaFiles)
                    {
                        var instance = new LuaScriptInstance(file);

                        // 注入数据
                        instance.LuaScript.Globals["game_data"] = gameData;
                        RecordLuaApi("game_data", "game_data", "游戏数据对象", "Data");
                        instance.LuaScript.Globals["graphics"] = graphics;
                        RecordLuaApi("graphics", "graphics", "图形系统对象", "Graphics");
                        // 注入地形系统
                        instance.LuaScript.Globals["terrain_get"] =
                            (Func<string, string, LimitlessSquareEngine.Engine.Terrain.Terrain?>)(
                                (sceneId, objectId) => TerrainManager.GetOrCreate(sceneId, objectId));
                        RecordLuaApi("terrain_get", "terrain_get(sceneId, objectId) -> terrain", "获取或创建地形节点", "Terrain");
                        instance.LuaScript.Globals["terrain_remove"] =
                            (Action<string, string>)((sceneId, objectId) => TerrainManager.Remove(sceneId, objectId));
                        RecordLuaApi("terrain_remove", "terrain_remove(sceneId, objectId)", "移除地形节点", "Terrain");
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

                        // 执行脚本文件
                        try
                        {
                            instance.LuaScript.DoFile(file);

                            // 缓存init
                            DynValue initFunc = instance.LuaScript.Globals.Get("init");
                            if (initFunc?.Type == DataType.Function)
                                instance.InitFunction = initFunc;

                            // 缓存loop函数
                            DynValue loopFunc = instance.LuaScript.Globals.Get("loop");
                            if (loopFunc?.Type == DataType.Function)
                                instance.LoopFunction = loopFunc;

                            _luaScriptInstances.Add(instance);
                            Console.WriteLine($"[i] Loaded script: {file}");
                        }
                        catch (MoonSharp.Interpreter.InterpreterException ex)
                        {
                            Console.WriteLine($"[X] Failed to load script '{file}': {ex.DecoratedMessage}");
                        }
                    }
                }

                // 扫描资源目录
                ReloadRegisteredAssetsForCurrentTiming();
                EnsureAllAudioClipKeysDiscovered();

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

                // 关闭启动Logo
                CloseStartupLogo();
            };

            _window.Resize += (size) =>
            {
                _windowTitleDirty = true;
            };

            // 关闭事件
            _window.Closing += () =>
            {
                StopEditorIfNeeded();
                UnbindEditorHostBridge();
                _input?.Dispose();
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

            ExecuteEditorHostActions();
            ProcessExternalControlCommands();

            // 帧间隔
            bool shouldAdvanceSimulation = TryPrepareRuntimeFrameTiming(out float deltaTime, out float fixedDeltaTime);
            TickWindowTitle(deltaTime);

            // 清除颜色缓冲
            _graphics?.ClearBackground();
            // 接收输入更新
            _input?.Update();
            // 音频引擎更新
            _audio?.Update(deltaTime);
            if (shouldAdvanceSimulation)
            {
                // 遍历所有脚本实例
                foreach (var instance in _luaScriptInstances)
                {
                    if (instance.LoopFunction != null && instance.LoopFunction.Type == DataType.Function)
                    {
                        try
                        {
                            instance.LuaScript.Globals["deltaTime"] = deltaTime;
                            instance.LuaScript.Globals["fixedDeltaTime"] = fixedDeltaTime;
                            instance.LoopFunction.Function.Call();
                        }
                        catch (ScriptRuntimeException ex)
                        {
                            Console.WriteLine($"[X] Error in loop function of script '{instance.FilePath}': {ex.DecoratedMessage}");
                        }
                    }
                }
                // 物理引擎
                Physics.Step(deltaTime, fixedDeltaTime);
                // 把场景层的脏transform同步到Graphics缓存
                Scene.FlushDirtyToRenderer();
                // 地形分帧流式生成
                TerrainManager.TickAll();
            }
            // 提交场景相机渲染
            _graphics?.QueueLoadedSceneRender();
            // 提交画布渲染
            RenderActiveUILayout();

            _graphics?.ExecuteRenderQueue();

            if (_isEditorMode)
            {
                CaptureEditorFrameCore();
            }
            else
            {
                _window.SwapBuffers();
            }
        }

        static ResourceLoadTiming ParseResourceLoadTimingValue(string value)
        {
            string normalized = (value ?? string.Empty).Trim().Trim('"');

            if (normalized.Equals("lazy", StringComparison.OrdinalIgnoreCase))
                return ResourceLoadTiming.Lazy;

            if (normalized.Equals("preload", StringComparison.OrdinalIgnoreCase))
                return ResourceLoadTiming.Preload;

            throw new ArgumentException($"Unsupported resource load timing '{value}'.");
        }

        static void ParseLaunchArguments(string[] args)
        {
            foreach (string rawArg in args)
            {
                if (string.IsNullOrWhiteSpace(rawArg))
                    continue;

                string arg = rawArg.Trim();

                if (arg.Equals("--game", StringComparison.OrdinalIgnoreCase))
                {
                    _forceGameMode = true;
                    _forceEditorMode = false;
                    continue;
                }

                if (arg.Equals("--editor", StringComparison.OrdinalIgnoreCase))
                {
                    _forceEditorMode = true;
                    _forceGameMode = false;
                    continue;
                }

                if (arg.Equals("--external-host", StringComparison.OrdinalIgnoreCase))
                {
                    _isExternalHostMode = true;
                    continue;
                }

                if (arg.StartsWith("--game-startup-folder=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--game-startup-folder=".Length).Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        SetGameStartupFolderCore(value);

                    continue;
                }

                if (arg.StartsWith("--resource-load=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--resource-load=".Length);
                    _resourceLoadTiming = ParseResourceLoadTimingValue(value);
                    continue;
                }

                if (arg.StartsWith("--external-control-file=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-control-file=".Length).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        _externalControlFilePath = value;

                    continue;
                }

                if (arg.StartsWith("--external-status-file=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-status-file=".Length).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        _externalStatusFilePath = value;

                    continue;
                }

                if (arg.StartsWith("--external-host-mode=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-mode=".Length).Trim().Trim('"');

                    if (value.Equals("win32", StringComparison.OrdinalIgnoreCase))
                        _externalHostEmbeddingMode = EditorEmbeddingMode.ForeignChildWindow;
                    else if (value.Equals("cocoa", StringComparison.OrdinalIgnoreCase))
                        _externalHostEmbeddingMode = EditorEmbeddingMode.CocoaViewHost;
                    else if (value.Equals("x11", StringComparison.OrdinalIgnoreCase))
                        _externalHostEmbeddingMode = EditorEmbeddingMode.ForeignChildWindow;
                    else if (value.Equals("wayland", StringComparison.OrdinalIgnoreCase))
                        _externalHostEmbeddingMode = EditorEmbeddingMode.NestedWaylandCompositor;
                    else
                        _externalHostEmbeddingMode = EditorEmbeddingMode.Unsupported;

                    continue;
                }

                if (arg.StartsWith("--external-host-win32-parent=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-win32-parent=".Length).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        _externalHostWin32ParentHwnd = (nint)ParseUnsignedNativeInteger(value);

                    continue;
                }

                if (arg.StartsWith("--external-host-cocoa-parent=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-cocoa-parent=".Length).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        _externalHostCocoaParentView = (nint)ParseUnsignedNativeInteger(value);

                    continue;
                }

                if (arg.StartsWith("--external-host-x11-parent=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-x11-parent=".Length).Trim().Trim('"');
                    if (!string.IsNullOrWhiteSpace(value))
                        _externalHostX11ParentWindow = (nint)ParseUnsignedNativeInteger(value);

                    continue;
                }

                if (arg.StartsWith("--external-host-width=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-width=".Length).Trim().Trim('"');
                    if (int.TryParse(value, out int width) && width > 0)
                        _externalHostInitialWidth = width;

                    continue;
                }

                if (arg.StartsWith("--external-host-height=", StringComparison.OrdinalIgnoreCase))
                {
                    string value = arg.Substring("--external-host-height=".Length).Trim().Trim('"');
                    if (int.TryParse(value, out int height) && height > 0)
                        _externalHostInitialHeight = height;

                    continue;
                }
            }
        }

        static ulong ParseUnsignedNativeInteger(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0UL;

            string raw = text.Trim();

            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return Convert.ToUInt64(raw.Substring(2), 16);

            return Convert.ToUInt64(raw, System.Globalization.CultureInfo.InvariantCulture);
        }

        static void FocusExternalHostedWindow()
        {
            if (!_isExternalHostMode || _window == null)
                return;

            INativeWindow native = _window.Native;

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.CocoaViewHost)
            {
                if (!native.Cocoa.HasValue)
                    return;

                nint nsWindow = native.Cocoa.Value;
                nint contentView = CocoaNativeInterop.GetContentView(nsWindow);

                if (contentView == 0)
                    return;

                CocoaNativeInterop.MakeWindowFirstResponder(nsWindow, contentView);
                return;
            }

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.ForeignChildWindow)
            {
                if (native.Win32.HasValue)
                {
                    nint hwnd = native.Win32.Value.Hwnd;
                    SetForegroundWindow(hwnd);
                    SetActiveWindow(hwnd);
                    SetFocus(hwnd);
                    return;
                }

                if (native.X11.HasValue)
                {
                    XMapWindow(native.X11.Value.Display, (nint)native.X11.Value.Window);
                    XSetInputFocus(
                        native.X11.Value.Display,
                        (nint)native.X11.Value.Window,
                        RevertToParent,
                        0);

                    XFlush(native.X11.Value.Display);
                    return;
                }
            }
        }

        static void ProcessExternalControlCommands()
        {
            if (string.IsNullOrWhiteSpace(_externalControlFilePath))
                return;

            if (!File.Exists(_externalControlFilePath))
                return;

            string[] commands;

            try
            {
                commands = File.ReadAllLines(_externalControlFilePath);
                File.WriteAllText(_externalControlFilePath, string.Empty);
            }
            catch
            {
                return;
            }

            foreach (string rawCommand in commands)
            {
                string command = rawCommand.Trim();
                if (string.IsNullOrWhiteSpace(command))
                    continue;

                if (command.Equals("pause", StringComparison.OrdinalIgnoreCase))
                {
                    SetRuntimePausedCore(true);
                    continue;
                }

                if (command.Equals("resume", StringComparison.OrdinalIgnoreCase))
                {
                    SetRuntimePausedCore(false);
                    continue;
                }

                if (command.Equals("step", StringComparison.OrdinalIgnoreCase))
                {
                    StepRuntimeFrameCore();
                    continue;
                }

                if (command.Equals("focus", StringComparison.OrdinalIgnoreCase))
                {
                    FocusExternalHostedWindow();
                    continue;
                }

                if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    RequestRenderWindowCloseCore();
                    continue;
                }

                if (command.StartsWith("resize ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 &&
                        int.TryParse(parts[1], out int width) &&
                        int.TryParse(parts[2], out int height))
                    {
                        SetRenderWindowSizeCore(width, height);
                    }

                    continue;
                }
            }
        }

        static void AttachWindowToExternalHost()
        {
            if (!_isExternalHostMode || _window == null)
                return;

            INativeWindow native = _window.Native;

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.CocoaViewHost)
            {
                if (!native.Cocoa.HasValue)
                    return;

                nint cocoaWindow = native.Cocoa.Value;
                nint cocoaContentView = CocoaNativeInterop.GetContentView(cocoaWindow);

                if (cocoaContentView == 0 || _externalHostCocoaParentView == 0)
                    return;

                CocoaNativeInterop.RemoveFromSuperview(cocoaContentView);
                CocoaNativeInterop.AddSubview(_externalHostCocoaParentView, cocoaContentView);
                CocoaNativeInterop.SetViewFrame(
                    cocoaContentView,
                    0,
                    0,
                    _externalHostInitialWidth,
                    _externalHostInitialHeight);

                _window.IsVisible = true;
                FocusExternalHostedWindow();
                return;
            }

            if (_externalHostEmbeddingMode == EditorEmbeddingMode.ForeignChildWindow)
            {
                if (native.Win32.HasValue && _externalHostWin32ParentHwnd != 0)
                {
                    nint hwnd = native.Win32.Value.Hwnd;
                    SetParent(hwnd, _externalHostWin32ParentHwnd);
                    SetWindowLong(hwnd, GWL_STYLE, WS_CHILD);
                    MoveWindow(hwnd, 0, 0, _externalHostInitialWidth, _externalHostInitialHeight, true);
                    _window.IsVisible = true;
                    FocusExternalHostedWindow();
                    return;
                }

                if (native.X11.HasValue && _externalHostX11ParentWindow != 0)
                {
                    nint display = native.X11.Value.Display;
                    nint window = (nint)native.X11.Value.Window;

                    XReparentWindow(display, window, _externalHostX11ParentWindow, 0, 0);
                    XResizeWindow(display, window, (uint)_externalHostInitialWidth, (uint)_externalHostInitialHeight);
                    XMapWindow(display, window);
                    XFlush(display);
                    _window.IsVisible = true;
                    FocusExternalHostedWindow();
                    return;
                }
            }
        }

        /// <summary>
        /// 解析当前运行模式。
        /// </summary>
        /// <returns></returns>
        static bool ResolveEditorMode()
        {
            _editorAssemblyPath = Path.Combine(AppContext.BaseDirectory, EditorAssemblyFileName);
            bool hasEditorDll = File.Exists(_editorAssemblyPath);

            if (_forceGameMode)
            {
                _isEditorMode = false;
            }
            else if (_forceEditorMode)
            {
                _isEditorMode = hasEditorDll;
            }
            else
            {
                _isEditorMode = hasEditorDll;
            }

            if (_isEditorMode)
            {
                Console.WriteLine("[i] Editor mode enabled.");
                Console.WriteLine($"[i] Editor assembly: {_editorAssemblyPath}");
            }
            else
            {
                Console.WriteLine("[i] Game mode enabled.");
            }

            return _isEditorMode;
        }

        /// <summary>
        /// 主线程
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        static void Main(string[] args)
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

            // 启动模式
            ParseLaunchArguments(args);
            _gameStartupFolder = Path.GetFullPath(_gameStartupFolder);
            ResolveEditorMode();
            ConfigureEditorMode();

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

            // 必要时启动编辑器
            if (_isEditorMode)
            {
                StartEditorIfNeeded();
                RunEditorIfNeeded();
                return;
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
                Action task = _taskQueue.Take();
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

                if (TryGetProperty(obj, "physics", out JsonElement physicsElement))
                {
                    if (physicsElement.ValueKind == JsonValueKind.Null)
                    {
                        // null 视为没有物理
                    }
                    else if (physicsElement.ValueKind == JsonValueKind.Object)
                    {
                        if (!TryValidatePhysicsElement(physicsElement, objectId, out string physicsReason))
                        {
                            reason = physicsReason;
                            return false;
                        }
                    }
                    else
                    {
                        reason = $"Object '{objectId}' physics must be object or null.";
                        return false;
                    }
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

            if (TryGetProperty(root, "properties", out JsonElement propertiesElement))
            {
                if (propertiesElement.ValueKind != JsonValueKind.Object && propertiesElement.ValueKind != JsonValueKind.Null)
                {
                    reason = "'properties' must be a JSON object or null.";
                    return false;
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

        static bool TryLoadCanvasLayoutFile(
            string filePath,
            JsonSerializerOptions options,
            out List<CanvasElement>? elements,
            out string reason)
        {
            elements = null;
            reason = string.Empty;

            try
            {
                string json = File.ReadAllText(filePath);
                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                {
                    reason = "Root is not a JSON array.";
                    return false;
                }

                var parsed = JsonSerializer.Deserialize<List<CanvasElement>>(json, options);
                if (parsed == null)
                {
                    reason = "Deserialized result is null.";
                    return false;
                }

                void SetParent(CanvasElement element, CanvasElement? parent = null)
                {
                    if (parent != null)
                        element.Parent = parent;

                    foreach (var child in element.Children)
                        SetParent(child, element);
                }

                foreach (var element in parsed)
                    SetParent(element);

                elements = parsed;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        static string BuildAssetKey(string assetsPath, string filePath, bool removeExtension = false)
        {
            string key = Path.GetRelativePath(assetsPath, filePath)
                .Replace('\\', '/');

            if (removeExtension)
            {
                string ext = Path.GetExtension(key);
                if (!string.IsNullOrEmpty(ext))
                    key = key[..^ext.Length];
            }

            return key;
        }

        static bool IsShaderFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);

            return ext.Equals(".vert", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".frag", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".glsl", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".shader", StringComparison.OrdinalIgnoreCase);
        }

        static byte[]? TryLoadIconFromBaseDirectory()
        {
            try
            {
                string baseDir = AppContext.BaseDirectory;

                string[] candidates =
                [
                    Path.Combine(baseDir, "icon.png"),
            Path.Combine(baseDir, "icon.ico"),
            Path.Combine(baseDir, "Icon.png"),
            Path.Combine(baseDir, "Icon.ico")
                ];

                foreach (string path in candidates)
                {
                    if (!File.Exists(path))
                        continue;

                    byte[] bytes = File.ReadAllBytes(path);

                    using var testCodec = SKCodec.Create(new SKMemoryStream(bytes));
                    if (testCodec != null)
                        return bytes;
                }
            }
            catch
            {
            }

            return null;
        }



        /// <summary>
        /// 读取JSON属性
        /// </summary>
        static bool TryValidatePhysicsElement(JsonElement physicsElement, string objectId, out string reason)
        {
            reason = string.Empty;

            if (physicsElement.ValueKind != JsonValueKind.Object)
            {
                reason = $"Object '{objectId}' physics must be a JSON object.";
                return false;
            }

            string motionType = "Static";
            string shapeType = "Box";

            foreach (JsonProperty prop in physicsElement.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "enabled":
                        if (prop.Value.ValueKind != JsonValueKind.True &&
                            prop.Value.ValueKind != JsonValueKind.False)
                        {
                            reason = $"Object '{objectId}' physics.enabled must be true or false.";
                            return false;
                        }
                        break;

                    case "motionType":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                        {
                            reason = $"Object '{objectId}' physics.motionType must be string.";
                            return false;
                        }

                        motionType = prop.Value.GetString()?.Trim() ?? string.Empty;
                        if (motionType != "Static" &&
                            motionType != "Dynamic" &&
                            motionType != "Kinematic")
                        {
                            reason = $"Object '{objectId}' physics.motionType must be 'Static', 'Dynamic' or 'Kinematic'.";
                            return false;
                        }
                        break;

                    case "shapeType":
                        if (prop.Value.ValueKind != JsonValueKind.String)
                        {
                            reason = $"Object '{objectId}' physics.shapeType must be string.";
                            return false;
                        }

                        shapeType = prop.Value.GetString()?.Trim() ?? string.Empty;
                        if (shapeType != "Box" &&
                            shapeType != "Sphere" &&
                            shapeType != "Capsule" &&
                            shapeType != "Mesh")
                        {
                            reason = $"Object '{objectId}' physics.shapeType must be 'Box', 'Sphere', 'Capsule' or 'Mesh'.";
                            return false;
                        }
                        break;

                    case "size":
                        if (!TryValidateStrictDouble3Object(prop.Value, $"Object '{objectId}' physics.size", requirePositive: true, out reason))
                            return false;
                        break;

                    case "radius":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double radius) ||
                            radius <= 0.0)
                        {
                            reason = $"Object '{objectId}' physics.radius must be > 0.";
                            return false;
                        }
                        break;

                    case "length":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double length) ||
                            length < 0.0)
                        {
                            reason = $"Object '{objectId}' physics.length must be >= 0.";
                            return false;
                        }
                        break;

                    case "mass":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double mass) ||
                            mass <= 0.0)
                        {
                            reason = $"Object '{objectId}' physics.mass must be > 0.";
                            return false;
                        }
                        break;

                    case "friction":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double friction) ||
                            friction < 0.0)
                        {
                            reason = $"Object '{objectId}' physics.friction must be >= 0.";
                            return false;
                        }
                        break;

                    case "restitution":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double restitution) ||
                            restitution < 0.0 ||
                            restitution > 1.0)
                        {
                            reason = $"Object '{objectId}' physics.restitution must be between 0 and 1.";
                            return false;
                        }
                        break;

                    case "useGravity":
                        if (prop.Value.ValueKind != JsonValueKind.True &&
                            prop.Value.ValueKind != JsonValueKind.False)
                        {
                            reason = $"Object '{objectId}' physics.useGravity must be true or false.";
                            return false;
                        }
                        break;

                    case "enableSpeculativeContacts":
                        if (prop.Value.ValueKind != JsonValueKind.True &&
                            prop.Value.ValueKind != JsonValueKind.False)
                        {
                            reason = $"Object '{objectId}' physics.enableSpeculativeContacts must be true or false.";
                            return false;
                        }
                        break;

                    case "linearDamping":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double linearDamping) ||
                            linearDamping < 0.0 ||
                            linearDamping > 1.0)
                        {
                            reason = $"Object '{objectId}' physics.linearDamping must be between 0 and 1.";
                            return false;
                        }
                        break;

                    case "angularDamping":
                        if (prop.Value.ValueKind != JsonValueKind.Number ||
                            !prop.Value.TryGetDouble(out double angularDamping) ||
                            angularDamping < 0.0 ||
                            angularDamping > 1.0)
                        {
                            reason = $"Object '{objectId}' physics.angularDamping must be between 0 and 1.";
                            return false;
                        }
                        break;

                    default:
                        reason = $"Object '{objectId}' physics contains unknown or wrong-cased property '{prop.Name}'.";
                        return false;
                }
            }

            if (shapeType == "Box")
            {

            }
            else if (shapeType == "Sphere")
            {

            }
            else if (shapeType == "Capsule")
            {

            }
            else if (shapeType == "Mesh")
            {

            }

            return true;
        }

        static bool TryValidateStrictDouble3Object(JsonElement element, string fieldName, bool requirePositive, out string reason)
        {
            reason = string.Empty;

            if (element.ValueKind != JsonValueKind.Object)
            {
                reason = $"{fieldName} must be an object with lowercase x/y/z.";
                return false;
            }

            bool hasX = false;
            bool hasY = false;
            bool hasZ = false;

            double x = 0.0;
            double y = 0.0;
            double z = 0.0;

            foreach (JsonProperty prop in element.EnumerateObject())
            {
                switch (prop.Name)
                {
                    case "x":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out x))
                        {
                            reason = $"{fieldName}.x must be number.";
                            return false;
                        }
                        hasX = true;
                        break;

                    case "y":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out y))
                        {
                            reason = $"{fieldName}.y must be number.";
                            return false;
                        }
                        hasY = true;
                        break;

                    case "z":
                        if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out z))
                        {
                            reason = $"{fieldName}.z must be number.";
                            return false;
                        }
                        hasZ = true;
                        break;

                    default:
                        reason = $"{fieldName} contains unknown or wrong-cased property '{prop.Name}'.";
                        return false;
                }
            }

            if (!hasX || !hasY || !hasZ)
            {
                reason = $"{fieldName} must contain lowercase x/y/z.";
                return false;
            }

            if (requirePositive && (x <= 0.0 || y <= 0.0 || z <= 0.0))
            {
                reason = $"{fieldName} components must be > 0.";
                return false;
            }

            return true;
        }
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

        /// <summary>
        /// 切换当前激活UI布局
        /// </summary>
        static void SetActiveUILayout(string layoutKey)
        {
            if (string.IsNullOrWhiteSpace(layoutKey))
                return;

            string key = NormalizeAssetLookupKey(layoutKey, removeExtension: true);

            if (!EnsureUILayoutLoaded(key, out var roots) || roots == null)
            {
                Console.WriteLine($"[!] UI layout not found: {key}");
                return;
            }

            _activeUILayoutKey = key;
            _activeUILayoutRoots = roots;
        }

        /// <summary>
        /// 清空当前激活UI布局
        /// </summary>
        static void ClearActiveUILayout()
        {
            _activeUILayoutKey = null;
            _activeUILayoutRoots = null;
        }

        /// <summary>
        /// 渲染当前激活UI布局
        /// </summary>
        static void RenderActiveUILayout()
        {
            if (_graphics == null || _activeUILayoutRoots == null)
                return;

            foreach (var root in _activeUILayoutRoots)
            {
                if (root == null || !root.Visible)
                    continue;

                root.PerformLayout();
                _graphics.DrawUI(root);
            }
        }
    }
}
