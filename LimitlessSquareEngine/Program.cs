using System.Collections.Concurrent;
using MoonSharp.Interpreter;

namespace LimitlessSquareEngine
{
    internal class Program
    {
        //Lua脚本定义
        static Script luaScript;
        //定义任务队列
        static BlockingCollection<Action> taskQueue = new BlockingCollection<Action>();
        //存储任务结果
        static ConcurrentDictionary<int, TaskCompletionSource<DynValue>> taskResults = new ConcurrentDictionary<int, TaskCompletionSource<DynValue>>();
        //任务ID
        static int nextTaskId = 0;
        //Lua定义的循环回调
        static DynValue loopCallback = DynValue.Nil;

        //初始化方法
        static void Initialize()
        {
            //创建全局Lua脚本实例
            luaScript = new Script();

            //注册提交任务函数到当前脚本
            luaScript.Globals["submit_task"] = (Func<string, int>)(
                (luaCode) =>
                {
                    int taskId = Interlocked.Increment(ref nextTaskId);
                    var tcs = new TaskCompletionSource<DynValue>();
                    taskResults[taskId] = tcs;

                    taskQueue.Add(() =>
                    {
                        try
                        {
                            //线程独立Lua实例
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
                }
            );

            //注册获取结果函数到当前脚本
            luaScript.Globals["get_task_result"] = (Func<int, DynValue[]>)(
                (taskId) =>
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
                }
            );

            //获取所有Lua文件
            string[] luaFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.lua");

            foreach (string file in luaFiles)
            {
                //执行当前Lua文件
                luaScript.DoFile(file);
            }

            //调用init函数（如果存在）
            DynValue initFunc = luaScript.Globals.Get("init");
            if (initFunc != null && initFunc.Type == DataType.Function)
            {
                initFunc.Function.Call();
            }

            //获取loop函数（如果存在）
            DynValue loopFunc = luaScript.Globals.Get("loop");
            if (loopFunc != null && loopFunc.Type == DataType.Function)
            {
                loopCallback = loopFunc;
            }
        }

        //循环方法
        static void Loop()
        {
            if (loopCallback != null && loopCallback.Type == DataType.Function)
            {
                loopCallback.Function.Call();
            }

            //限制帧率
            Thread.Sleep(1);
        }

        //主线程
        static void Main(string[] args)
        {
            Console.WriteLine("Hello, World!");
            //执行初始化
            Initialize();

            //启动所有后台线程
            for (int i = 0; i < 25; i++)
            {
                //创建进程
                Thread thread = new Thread(BackgroundThread);
                //设置为后台进程
                thread.IsBackground = true;
                //启动线程
                thread.Start();
            }

            //执行主循环
            while (true)
            {
                Loop();
            }
        }

        //后台线程
        static void BackgroundThread()
        {
            while (true)
            {
                //抽取任务
                Action task = taskQueue.Take();
                //执行任务
                task();
            }
        }
    }
}
