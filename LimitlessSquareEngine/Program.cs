using System.Collections.Concurrent;
using MoonSharp.Interpreter;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

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
            // 创建独立Lua解释器环境
            LuaScript = new Script();
            InitFunction = DynValue.Nil;
            LoopFunction = DynValue.Nil;
        }
    }

    internal class Program
    {
        // Lua脚本定义
        static List<LuaScriptInstance> _luaScriptInstances = new List<LuaScriptInstance>();
        // 窗口定义
        static IWindow _window;
        // 图形定义
        static GL _gl;

        // 定义任务队列
        static BlockingCollection<Action> taskQueue = new BlockingCollection<Action>();
        // 存储任务结果
        static ConcurrentDictionary<int, TaskCompletionSource<DynValue>> taskResults = new ConcurrentDictionary<int, TaskCompletionSource<DynValue>>();
        // 任务ID
        static int nextTaskId = 0;
        static Graphics _graphics;

        /// <summary>
        /// 初始化方法
        /// </summary>
        static void Initialize()
        {
            // 注册数据类型
            UserData.RegisterType<GameData>();
            // 初始化窗口
            var options = WindowOptions.Default;
            options.Size = new Silk.NET.Maths.Vector2D<int>(800, 600);
            options.Title = "Limitless Square Engine";
            options.IsVisible = true;
            options.ShouldSwapAutomatically = false;
            _window = Window.Create(options);

            UserData.RegisterType<Graphics>();

            // 加载事件
            _window.Load += () =>
            {
                // 初始化OpenGL
                _gl = _window.CreateOpenGL();
                _gl.ClearColor(0.2f, 0.2f, 0.2f, 1.0f);
                var graphics = new Graphics(_gl, _window);
                graphics.Initialize();
                _graphics = graphics;

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
                                return new DynValue[] { DynValue.Nil, DynValue.NewString(tcs.Task.Exception.InnerException.Message) };
                            }
                            else
                            {
                                return new DynValue[] { tcs.Task.Result };
                            }
                        }
                    }
                    return new DynValue[] { DynValue.Nil };
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
                        Console.WriteLine($"Loaded script: {file}");
                    }
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
                        Console.WriteLine($"Error in init function of script '{instance.FilePath}': {ex.DecoratedMessage}");
                    }
                }
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

            // 清除颜色缓冲
            _graphics?.ClearBackground();

            // 遍历所有脚本实例
            foreach (var instance in _luaScriptInstances)
            {
                if (instance.LoopFunction != null && instance.LoopFunction.Type == DataType.Function)
                {
                    try
                    {
                        instance.LoopFunction.Function.Call();
                    }
                    catch (ScriptRuntimeException ex)
                    {
                        Console.WriteLine($"Error in loop function of script '{instance.FilePath}': {ex.DecoratedMessage}");
                    }
                }
            }

            // 交换缓冲区
            _window.SwapBuffers();
        }

        /// <summary>
        /// 主线程
        /// </summary>
        static void Main()
        {
            Console.WriteLine("Hello, World!");
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
    }
}
