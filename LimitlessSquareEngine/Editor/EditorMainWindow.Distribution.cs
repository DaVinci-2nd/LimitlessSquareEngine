using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Editor
{
    public sealed partial class EditorMainWindow : Window
    {
        private sealed class GameDistributionResult
        {
            public bool Confirmed { get; init; }
            public string GameName { get; init; } = "";
            public string TargetDirectory { get; init; } = "";
            public string FolderName { get; init; } = "";
        }

        private sealed class GameDistributionConfigDialog : Window
        {
            public GameDistributionConfigDialog(string defaultFolderName, string projectRootPath)
            {
                Title = "分发游戏";
                Width = 520;
                Height = 350;
                CanResize = false;
                CanMinimize = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                WindowDecorations = WindowDecorations.Full;
                ShowInTaskbar = false;
                Background = new SolidColorBrush(Color.Parse("#111111"));

                string selectedTargetDirectory = "";

                TextBlock gameNameLabel = new TextBlock
                {
                    Text = "游戏名称",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };

                TextBox gameNameTextBox = new TextBox
                {
                    Text = "",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                    Foreground = Brushes.White,
                    Height = 30,
                    MinHeight = 30,
                    Padding = new Thickness(8, 1, 8, 1)
                };

                TextBlock targetDirectoryLabel = new TextBlock
                {
                    Text = "目标目录",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };

                TextBox targetDirectoryTextBox = new TextBox
                {
                    Text = "",
                    IsReadOnly = true,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                    Foreground = Brushes.White,
                    Height = 30,
                    MinHeight = 30,
                    Padding = new Thickness(8, 1, 8, 1)
                };

                Button selectFolderButton = new Button
                {
                    Content = "选择文件夹",
                    Width = 110,
                    Height = 30,
                    MinWidth = 110,
                    MinHeight = 30,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                TextBlock folderNameLabel = new TextBlock
                {
                    Text = "分发文件夹名称",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center
                };

                TextBox folderNameTextBox = new TextBox
                {
                    Text = defaultFolderName,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                    Foreground = Brushes.White,
                    Height = 30,
                    MinHeight = 30,
                    Padding = new Thickness(8, 1, 8, 1)
                };

                Border placeholderArea = new Border
                {
                    MinHeight = 40,
                    Background = Brushes.Transparent
                };

                Button cancelButton = new Button
                {
                    Content = "取消",
                    Width = 88,
                    Height = 32,
                    MinWidth = 88,
                    MinHeight = 32,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center
                };

                Button confirmButton = new Button
                {
                    Content = "确认",
                    Width = 88,
                    Height = 32,
                    MinWidth = 88,
                    MinHeight = 32,
                    Padding = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    IsEnabled = false
                };

                char[] invalidFileNameChars = Path.GetInvalidFileNameChars();

                void ValidateInputs()
                {
                    string gameName = gameNameTextBox.Text?.Trim() ?? "";
                    string folderName = folderNameTextBox.Text?.Trim() ?? "";

                    bool gameNameValid = !string.IsNullOrWhiteSpace(gameName)
                        && gameName.IndexOfAny(invalidFileNameChars) < 0;

                    bool targetDirValid = !string.IsNullOrWhiteSpace(selectedTargetDirectory)
                        && Directory.Exists(selectedTargetDirectory);

                    if (targetDirValid && !string.IsNullOrWhiteSpace(projectRootPath))
                    {
                        string fullTarget = Path.GetFullPath(selectedTargetDirectory);
                        string fullProject = Path.GetFullPath(projectRootPath);

                        StringComparison comparison = OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal;

                        if (fullTarget.StartsWith(fullProject, comparison)
                            && fullTarget.Length > fullProject.Length
                            && fullTarget[fullProject.Length] == Path.DirectorySeparatorChar)
                        {
                            targetDirValid = false;
                        }
                        else if (string.Equals(fullTarget, fullProject, comparison))
                        {
                            targetDirValid = false;
                        }
                    }

                    bool folderNameValid = !string.IsNullOrWhiteSpace(folderName)
                        && folderName.IndexOfAny(invalidFileNameChars) < 0;

                    confirmButton.IsEnabled = gameNameValid && targetDirValid && folderNameValid;
                }

                gameNameTextBox.TextChanged += (_, _) => ValidateInputs();
                folderNameTextBox.TextChanged += (_, _) => ValidateInputs();

                selectFolderButton.Click += async (_, _) =>
                {
                    if (!StorageProvider.CanPickFolder)
                        return;

                    var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                    {
                        Title = "选择分发目标目录",
                        AllowMultiple = false
                    });

                    if (folders.Count == 0)
                        return;

                    string? path = folders[0].TryGetLocalPath();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        selectedTargetDirectory = Path.GetFullPath(path);
                        targetDirectoryTextBox.Text = selectedTargetDirectory;
                        ValidateInputs();
                    }
                };

                cancelButton.Click += (_, _) => Close(null);

                confirmButton.Click += (_, _) =>
                {
                    Close(new GameDistributionResult
                    {
                        Confirmed = true,
                        GameName = gameNameTextBox.Text?.Trim() ?? "",
                        TargetDirectory = selectedTargetDirectory,
                        FolderName = folderNameTextBox.Text?.Trim() ?? ""
                    });
                };

                Grid targetDirRow = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Thickness(0, 8, 0, 0)
                };
                targetDirRow.Children.Add(targetDirectoryTextBox);
                Grid.SetColumn(selectFolderButton, 1);
                targetDirRow.Children.Add(selectFolderButton);

                StackPanel buttonRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 12
                };
                buttonRow.Children.Add(cancelButton);
                buttonRow.Children.Add(confirmButton);

                Grid layout = new Grid
                {
                    Margin = new Thickness(20),
                    RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto")
                };

                StackPanel row0 = new StackPanel { Spacing = 4 };
                row0.Children.Add(gameNameLabel);
                row0.Children.Add(gameNameTextBox);
                layout.Children.Add(row0);
                Grid.SetRow(row0, 0);

                StackPanel row1 = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
                row1.Children.Add(targetDirectoryLabel);
                row1.Children.Add(targetDirRow);
                layout.Children.Add(row1);
                Grid.SetRow(row1, 1);

                StackPanel row2 = new StackPanel { Spacing = 4, Margin = new Thickness(0, 12, 0, 0) };
                row2.Children.Add(folderNameLabel);
                row2.Children.Add(folderNameTextBox);
                layout.Children.Add(row2);
                Grid.SetRow(row2, 2);

                layout.Children.Add(placeholderArea);
                Grid.SetRow(placeholderArea, 3);

                layout.Children.Add(buttonRow);
                Grid.SetRow(buttonRow, 4);

                Content = layout;
            }
        }

        private sealed class GameDistributionProgressWindow : Window
        {
            private readonly TextBlock _statusText;

            public GameDistributionProgressWindow()
            {
                Title = "正在分发...";
                Width = 450;
                Height = 160;
                CanResize = false;
                CanMinimize = false;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                WindowDecorations = WindowDecorations.Full;
                ShowInTaskbar = false;
                Background = new SolidColorBrush(Color.Parse("#111111"));

                _statusText = new TextBlock
                {
                    Text = "准备中...",
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 16)
                };

                ProgressBar progressBar = new ProgressBar
                {
                    IsIndeterminate = true,
                    Height = 6,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Minimum = 0,
                    Maximum = 1
                };

                StackPanel panel = new StackPanel
                {
                    Margin = new Thickness(20),
                    VerticalAlignment = VerticalAlignment.Center
                };
                panel.Children.Add(_statusText);
                panel.Children.Add(progressBar);

                Content = panel;
            }

            public void Report(string message)
            {
                _statusText.Text = message;
            }
        }

        private static Task<string?> RunDistributionAsync(
            string sourceRuntimeDir,
            string sourceAssetsDir,
            string gameName,
            string targetDir,
            string folderName,
            Action<string> onProgress)
        {
            string distPath = Path.Combine(targetDir, folderName);

            try
            {
                onProgress("正在创建分发文件夹...");
                Directory.CreateDirectory(distPath);

                onProgress("正在复制运行时文件...");

                string exeSource = Path.Combine(sourceRuntimeDir, "Limitless Square Engine.exe");
                if (File.Exists(exeSource))
                {
                    File.Copy(exeSource, Path.Combine(distPath, gameName + ".exe"), true);
                }
                else
                {
                    return Task.FromResult<string?>("未找到引擎可执行文件：Limitless Square Engine.exe");
                }

                string engineDllSource = Path.Combine(sourceRuntimeDir, "Limitless Square Engine.dll");
                if (File.Exists(engineDllSource))
                    File.Copy(engineDllSource, Path.Combine(distPath, "Limitless Square Engine.dll"), true);

                string runtimeConfigSource = Path.Combine(sourceRuntimeDir, "Limitless Square Engine.runtimeconfig.json");
                if (File.Exists(runtimeConfigSource))
                    File.Copy(runtimeConfigSource, Path.Combine(distPath, "Limitless Square Engine.runtimeconfig.json"), true);

                string depsJsonSource = Path.Combine(sourceRuntimeDir, "Limitless Square Engine.deps.json");
                if (File.Exists(depsJsonSource))
                    File.Copy(depsJsonSource, Path.Combine(distPath, "Limitless Square Engine.deps.json"), true);

                foreach (string dllFile in Directory.GetFiles(sourceRuntimeDir, "*.dll"))
                {
                    string fileName = Path.GetFileName(dllFile);
                    if (string.Equals(fileName, "Limitless Square Editor.dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(fileName, "Limitless Square Engine.dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    File.Copy(dllFile, Path.Combine(distPath, fileName), true);
                }

                string runtimesSource = Path.Combine(sourceRuntimeDir, "runtimes");
                if (Directory.Exists(runtimesSource))
                {
                    CopyDirectory(runtimesSource, Path.Combine(distPath, "runtimes"));
                }

                string builtinShadersSource = Path.Combine(sourceRuntimeDir, "Assets", "Shaders", "Builtin");
                if (Directory.Exists(builtinShadersSource))
                {
                    string builtinShadersTarget = Path.Combine(distPath, "Assets", "Shaders", "Builtin");
                    CopyDirectory(builtinShadersSource, builtinShadersTarget);
                }

                onProgress("正在复制游戏资源...");

                if (Directory.Exists(sourceAssetsDir))
                {
                    CopyDirectory(sourceAssetsDir, Path.Combine(distPath, "Assets"));
                }
                else
                {
                    return Task.FromResult<string?>("未找到游戏资源目录：" + sourceAssetsDir);
                }

                onProgress("正在校验...");

                if (!File.Exists(Path.Combine(distPath, gameName + ".exe")))
                    return Task.FromResult<string?>("校验失败：游戏可执行文件未找到");

                if (!File.Exists(Path.Combine(distPath, "Limitless Square Engine.dll")))
                    return Task.FromResult<string?>("校验失败：引擎核心 DLL 未找到");

                if (!File.Exists(Path.Combine(distPath, "Limitless Square Engine.runtimeconfig.json")))
                    return Task.FromResult<string?>("校验失败：runtimeconfig.json 未找到");

                if (!File.Exists(Path.Combine(distPath, "Limitless Square Engine.deps.json")))
                    return Task.FromResult<string?>("校验失败：deps.json 未找到");

                string glfwDll = Path.Combine(distPath, "runtimes", "win-x64", "native", "glfw3.dll");
                if (!File.Exists(glfwDll))
                    return Task.FromResult<string?>("校验失败：glfw3.dll 未找到");

                string skiaDll = Path.Combine(distPath, "runtimes", "win-x64", "native", "libSkiaSharp.dll");
                if (!File.Exists(skiaDll))
                    return Task.FromResult<string?>("校验失败：libSkiaSharp.dll 未找到");

                string assimpDll = Path.Combine(distPath, "runtimes", "win-x64", "native", "assimp.dll");
                if (!File.Exists(assimpDll))
                    return Task.FromResult<string?>("校验失败：assimp.dll 未找到");

                if (!Directory.Exists(Path.Combine(distPath, "Assets")))
                    return Task.FromResult<string?>("校验失败：Assets 目录未找到");

                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                return Task.FromResult<string?>("分发过程中发生错误：" + ex.Message);
            }
        }

        private static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, targetFile, true);
            }

            foreach (string subDir in Directory.GetDirectories(sourceDir))
            {
                string targetSubDir = Path.Combine(targetDir, Path.GetFileName(subDir));
                CopyDirectory(subDir, targetSubDir);
            }
        }

        private async Task ShowDistributionDialogAsync()
        {
            if (string.IsNullOrWhiteSpace(_projectRootPath))
            {
                await ShowSimpleWarningDialogAsync("分发", "请先打开一个工程。");
                return;
            }

            string defaultFolderName = new DirectoryInfo(_projectRootPath).Name;
            string sourceAssetsDir = _projectAssetRootPath ?? Path.Combine(_projectRootPath, "Assets");

            GameDistributionConfigDialog configDialog = new GameDistributionConfigDialog(defaultFolderName, _projectRootPath);

            GameDistributionResult? result = await configDialog.ShowDialog<GameDistributionResult?>(this);

            if (result == null || !result.Confirmed)
                return;

            GameDistributionProgressWindow progressWindow = new GameDistributionProgressWindow();

            progressWindow.Show(this);

            string? error = await RunDistributionAsync(
                AppContext.BaseDirectory,
                sourceAssetsDir,
                result.GameName,
                result.TargetDirectory,
                result.FolderName,
                msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => progressWindow.Report(msg))
            );

            progressWindow.Close();

            if (error != null)
            {
                await ShowSimpleWarningDialogAsync("分发失败", error);
            }
            else
            {
                await ShowSimpleWarningDialogAsync("分发成功",
                    $"游戏 \"{result.GameName}\" 已成功分发到：\n{Path.Combine(result.TargetDirectory, result.FolderName)}");
            }
        }
    }
}
