using Avalonia;
using Avalonia.Threading;
using Avalonia.Controls;
using System.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Editor
{
    public static class EditorEntry
    {
        public static void Configure(EditorLaunchOptions options)
        {
            string basePath = Path.GetDirectoryName(options.DefaultAssetRootPath) ?? AppContext.BaseDirectory;
            string editorRuntimePath = Path.Combine(basePath, ".lse-editor-runtime");

            Directory.CreateDirectory(editorRuntimePath);
            Directory.CreateDirectory(Path.Combine(editorRuntimePath, "EditorPreview"));
            Directory.CreateDirectory(Path.Combine(editorRuntimePath, "EditorTreeCopies"));

            EnsurePreviewPlaceholderScene(editorRuntimePath);

            options.AssetRootPath = editorRuntimePath;
        }

        private static void EnsurePreviewPlaceholderScene(string editorRuntimePath)
        {
            string previewScenePath = Path.Combine(
                editorRuntimePath,
                "EditorPreview",
                "__editor_preview_scene__.json");

            if (File.Exists(previewScenePath))
                return;

            string json = """
                {
                  "sceneId": "__editor_preview_scene__",
                  "objects": []
                }
                """;

            File.WriteAllText(previewScenePath, json);
        }

        public static void Start(EditorHostBootstrapInfo info)
        {
            EditorRuntimeState.BootstrapInfo = info;
        }

        public static void Run()
        {
            if (EditorRuntimeState.BootstrapInfo == null)
                throw new InvalidOperationException("Editor bootstrap info is missing.");

            BuildAvaloniaApp().Start(AppMain, Array.Empty<string>());
        }

        public static void Stop()
        {
            EditorRuntimeState.CancellationTokenSource?.Cancel();
        }

        private static AppBuilder BuildAvaloniaApp()
        {
            return AppBuilder.Configure<EditorApp>().UsePlatformDetect();
        }

        private static void AppMain(Application app, string[] args)
        {
            CancellationTokenSource cts = new CancellationTokenSource();
            EditorRuntimeState.CancellationTokenSource = cts;

            EditorMainWindow window = new EditorMainWindow();

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += (_, _) =>
            {
                if (EditorHostBridge.IsRenderWindowAlive)
                {
                    EditorHostBridge.RunRenderFrame();
                    window.PresentLatestFrame();
                }
            };

            window.Closed += (_, _) =>
            {
                timer.Stop();
                cts.Cancel();
            };

            window.Show();
            timer.Start();

            app.Run(cts.Token);

            EditorRuntimeState.CancellationTokenSource = null;
        }
    }
}
