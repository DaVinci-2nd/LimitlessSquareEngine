using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace LimitlessSquareEngine.Editor
{
    public sealed class EditorMainWindow : Window
    {
        private readonly EmbeddedGameHost _sceneHost;

        public EmbeddedGameHost SceneHost => _sceneHost;

        // 预留给后续功能窗口的插槽
        public ContentControl ToolbarSlot { get; } = new ContentControl();
        public ContentControl LeftDockSlot { get; } = new ContentControl();
        public ContentControl RightDockSlot { get; } = new ContentControl();
        public ContentControl BottomDockSlot { get; } = new ContentControl();
        public ContentControl ProjectFilesSlot { get; } = new ContentControl();
        private ResourceItemState? _selectedResourceItemState;
        private string? _projectRootPath;
        private string? _currentResourceDirectoryPath;
        private string? _projectAssetRootPath;

        private const string EditorPreviewSceneId = "__editor_preview_scene__";
        private const string EditorPreviewDirectoryName = "EditorPreview";
        private const string EditorTreeCopyDirectoryName = "EditorTreeCopies";
        private const string EditorPreviewSceneFileName = EditorPreviewSceneId + ".json";
        private const string PreviewCameraIdBase = "__editor_preview_camera__";

        private static readonly JsonSerializerOptions SceneJsonReadOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static readonly JsonSerializerOptions SceneJsonWriteOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            WriteIndented = true
        };

        private sealed class EditorSceneOpenResult
        {
            public string TreeCopyPath { get; init; } = "";
            public string PreviewScenePath { get; init; } = "";
            public SceneData TreeScene { get; init; } = new SceneData();
            public SceneData PreviewScene { get; init; } = new SceneData();
        }

        public EditorMainWindow()
        {
            Title = "Limitless Square Editor";
            Width = 1280;
            Height = 720;
            MinWidth = 960;
            MinHeight = 540;
            Background = new SolidColorBrush(Color.Parse("#111111"));

            ToolbarSlot.VerticalAlignment = VerticalAlignment.Center;
            ToolbarSlot.HorizontalAlignment = HorizontalAlignment.Left;
            ToolbarSlot.HorizontalContentAlignment = HorizontalAlignment.Left;

            _sceneHost = new EmbeddedGameHost();
            _sceneHost.RenderSurfaceResized += OnSceneHostResized;

            // 先这么搞
            LeftDockSlot.Content = CreatePlaceholder("这里边是一个树结构");
            RightDockSlot.Content = CreatePlaceholder("这里展示选中节点的属性");
            ProjectFilesSlot.Content = CreatePlaceholder("未选择项目文件夹");
            BottomDockSlot.Content = CreatePlaceholder("未选择文件夹");
            ToolbarSlot.Content = CreateTopMenuBar();

            Content = BuildLayout();
        }

        private sealed class ResourceItemState
        {
            public Border Item { get; }
            public string Path { get; }
            public bool IsDirectory { get; }
            public bool IsPointerOver { get; set; }
            public bool IsPressed { get; set; }

            public ResourceItemState(Border item, string path, bool isDirectory)
            {
                Item = item;
                Path = path;
                IsDirectory = isDirectory;
            }
        }

        private void OnSceneHostResized(PixelSize hostSize)
        {
            if (hostSize.Width > 0 && hostSize.Height > 0)
                EditorHostBridge.SetRenderWindowSize(hostSize.Width, hostSize.Height);
        }

        public void PresentLatestFrame()
        {
            EditorRenderedFrame? frame = EditorHostBridge.ConsumeLatestFrame();
            if (frame == null)
                return;

            _sceneHost.PresentFrame(frame);
        }

        private Control BuildLayout()
        {
            DockPanel root = new DockPanel();

            Border topBar = new Border
            {
                Height = 40,
                Background = new SolidColorBrush(Color.Parse("#111111")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 0),
                Child = CreateTopBarContent()
            };
            DockPanel.SetDock(topBar, Dock.Top);
            root.Children.Add(topBar);

            Grid workspace = new Grid();

            workspace.ColumnDefinitions.Add(new ColumnDefinition(260, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(320, GridUnitType.Pixel));

            workspace.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            workspace.RowDefinitions.Add(new RowDefinition(5, GridUnitType.Pixel));
            workspace.RowDefinitions.Add(new RowDefinition(220, GridUnitType.Pixel));

            Control leftPanel = CreateDockContainer("场景树/画布树", LeftDockSlot);
            Control scenePanel = CreateSceneContainer();
            Control rightPanel = CreateDockContainer("节点查看器", RightDockSlot);
            Control bottomPanel = CreateBottomWorkspace();

            Grid.SetColumn(leftPanel, 0);
            Grid.SetRow(leftPanel, 0);
            workspace.Children.Add(leftPanel);

            Grid.SetColumn(scenePanel, 2);
            Grid.SetRow(scenePanel, 0);
            workspace.Children.Add(scenePanel);

            Grid.SetColumn(rightPanel, 4);
            Grid.SetRow(rightPanel, 0);
            workspace.Children.Add(rightPanel);

            Grid.SetColumn(bottomPanel, 0);
            Grid.SetColumnSpan(bottomPanel, 5);
            Grid.SetRow(bottomPanel, 2);
            workspace.Children.Add(bottomPanel);

            GridSplitter leftSplitter = new GridSplitter
            {
                Width = 5,
                Background = new SolidColorBrush(Color.Parse("#333333")),
                ResizeDirection = GridResizeDirection.Columns,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(leftSplitter, 1);
            Grid.SetRow(leftSplitter, 0);
            workspace.Children.Add(leftSplitter);

            GridSplitter rightSplitter = new GridSplitter
            {
                Width = 5,
                Background = new SolidColorBrush(Color.Parse("#333333")),
                ResizeDirection = GridResizeDirection.Columns,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(rightSplitter, 3);
            Grid.SetRow(rightSplitter, 0);
            workspace.Children.Add(rightSplitter);

            GridSplitter bottomSplitter = new GridSplitter
            {
                Height = 5,
                Background = new SolidColorBrush(Color.Parse("#333333")),
                ResizeDirection = GridResizeDirection.Rows,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(bottomSplitter, 0);
            Grid.SetColumnSpan(bottomSplitter, 5);
            Grid.SetRow(bottomSplitter, 1);
            workspace.Children.Add(bottomSplitter);

            root.Children.Add(workspace);
            return root;
        }

        private Control CreateTopBarContent()
        {
            Grid grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock titleText = new TextBlock
            {
                Text = "Limitless Square Editor",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 13,
                Margin = new Thickness(0, 0, 12, 0)
            };

            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(ToolbarSlot, 1);

            grid.Children.Add(titleText);
            grid.Children.Add(ToolbarSlot);

            return grid;
        }

        private Control CreateSceneContainer()
        {
            Grid sceneGrid = new Grid();
            sceneGrid.RowDefinitions.Add(new RowDefinition(32, GridUnitType.Pixel));
            sceneGrid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

            Border header = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#222222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 0),
                Child = new TextBlock
                {
                    Text = "场景/画布",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 12
                }
            };

            Border hostBorder = new Border
            {
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(1),
                Child = _sceneHost
            };

            Grid.SetRow(header, 0);
            Grid.SetRow(hostBorder, 1);

            sceneGrid.Children.Add(header);
            sceneGrid.Children.Add(hostBorder);

            return sceneGrid;
        }

        private static Control CreateDockContainer(string title, ContentControl slot)
        {
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition(32, GridUnitType.Pixel));
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

            Border header = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#222222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 0),
                Child = new TextBlock
                {
                    Text = title,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 12
                }
            };

            Border body = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#111111")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(1),
                Child = slot
            };

            Grid.SetRow(header, 0);
            Grid.SetRow(body, 1);

            grid.Children.Add(header);
            grid.Children.Add(body);

            return grid;
        }

        private Control CreateBottomWorkspace()
        {
            Grid grid = new Grid();

            grid.ColumnDefinitions.Add(new ColumnDefinition(280, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            Control projectFilesPanel = CreateDockContainer("项目文件", ProjectFilesSlot);
            Control resourcePanel = CreateDockContainer("资源管理器", BottomDockSlot);

            Grid.SetColumn(projectFilesPanel, 0);
            grid.Children.Add(projectFilesPanel);

            GridSplitter splitter = new GridSplitter
            {
                Width = 5,
                Background = new SolidColorBrush(Color.Parse("#333333")),
                ResizeDirection = GridResizeDirection.Columns,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            Grid.SetColumn(resourcePanel, 2);
            grid.Children.Add(resourcePanel);

            return grid;
        }

        private static Control CreatePlaceholder(string text)
        {
            return new Border
            {
                Padding = new Thickness(12),
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(Color.Parse("#CCCCCC"))
                }
            };
        }

        private Control CreateTopMenuBar()
        {
            Menu menu = new Menu
            {
                Background = Brushes.Transparent,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            menu.ItemsSource = new object[]
            {
                new MenuItem
                {
                    Header = "文件",
                    Foreground = Brushes.White,
                    ItemsSource = CreateFileMenuItems()
                },
                new MenuItem
                {
                    Header = "编辑",
                    Foreground = Brushes.White,
                    ItemsSource = CreateEditMenuItems()
                },
                new MenuItem
                {
                    Header = "窗口",
                    Foreground = Brushes.White,
                    ItemsSource = CreateWindowMenuItems()
                }
            };

            return menu;
        }

        private IEnumerable<MenuItem> CreateFileMenuItems()
        {
            MenuItem newItem = new MenuItem { Header = "新建" };
            MenuItem openItem = new MenuItem { Header = "打开" };
            MenuItem saveItem = new MenuItem { Header = "保存" };
            MenuItem exitItem = new MenuItem { Header = "退出" };

            openItem.Click += async (_, _) => await OpenProjectFolderAsync();

            return new[]
            {
                newItem,
                openItem,
                saveItem,
                new MenuItem { Header = "-" },
                exitItem
            };
        }

        private IEnumerable<MenuItem> CreateEditMenuItems()
        {
            return new[]
            {
                new MenuItem { Header = "撤销" },
                new MenuItem { Header = "重做" },
                new MenuItem { Header = "-" },
                new MenuItem { Header = "复制" },
                new MenuItem { Header = "粘贴" }
            };
        }

        private IEnumerable<MenuItem> CreateWindowMenuItems()
        {
            return new[]
            {
                new MenuItem { Header = "场景树" },
                new MenuItem { Header = "节点查看器" },
                new MenuItem { Header = "资源管理器" }
            };
        }

        private async Task OpenProjectFolderAsync()
        {
            if (!StorageProvider.CanPickFolder)
                return;

            IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                new FolderPickerOpenOptions
                {
                    Title = "选择工程文件夹",
                    AllowMultiple = false
                });

            if (folders.Count == 0)
                return;

            string? rootPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                ProjectFilesSlot.Content = CreatePlaceholder("无法读取本地文件夹路径");
                return;
            }

            ShowProjectFolderTree(rootPath);
        }

        private void ShowProjectFolderTree(string rootPath)
        {
            string fullRootPath = Path.GetFullPath(rootPath);
            _projectRootPath = fullRootPath;
            _projectAssetRootPath = ResolveProjectAssetRoot(fullRootPath);
            _currentResourceDirectoryPath = fullRootPath;

            TreeView treeView = new TreeView
            {
                Margin = new Thickness(0),
                ItemsSource = new object[]
                {
            CreateDirectoryNode(fullRootPath, true)
                }
            };

            treeView.SelectionChanged += OnProjectTreeSelectionChanged;

            ProjectFilesSlot.Content = treeView;
            ShowResourceDirectory(fullRootPath);
        }

        private string ResolveProjectAssetRoot(string projectRootPath)
        {
            string fullProjectRoot = Path.GetFullPath(projectRootPath);
            string assetsPath = Path.Combine(fullProjectRoot, "Assets");

            if (Directory.Exists(assetsPath))
                return assetsPath;

            return fullProjectRoot;
        }

        private string ResolveSceneAssetRoot(string sceneFilePath)
        {
            if (!string.IsNullOrWhiteSpace(_projectAssetRootPath) && Directory.Exists(_projectAssetRootPath))
                return _projectAssetRootPath;

            string fullScenePath = Path.GetFullPath(sceneFilePath);
            DirectoryInfo? current = new DirectoryInfo(Path.GetDirectoryName(fullScenePath)!);

            while (current != null)
            {
                if (string.Equals(current.Name, "Assets", StringComparison.OrdinalIgnoreCase))
                    return current.FullName;

                current = current.Parent;
            }

            if (!string.IsNullOrWhiteSpace(_projectRootPath))
                return ResolveProjectAssetRoot(_projectRootPath);

            return Path.GetDirectoryName(fullScenePath) ?? fullScenePath;
        }

        private string GetEditorWorkingRoot(string assetRootPath)
        {
            return Path.Combine(assetRootPath, ".lse-editor-runtime");
        }

        private TreeViewItem CreateDirectoryNode(string directoryPath, bool isRoot = false)
        {
            TreeViewItem item = new TreeViewItem
            {
                Header = GetDirectoryDisplayName(directoryPath, isRoot),
                Tag = directoryPath,
                IsExpanded = isRoot
            };

            string[] directories;
            string[] files;

            try
            {
                directories = Directory.GetDirectories(directoryPath);
                files = Directory.GetFiles(directoryPath);
            }
            catch
            {
                item.ItemsSource = new object[]
                {
                    new TreeViewItem
                    {
                        Header = "无法访问"
                    }
                };
                return item;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            List<object> children = new List<object>();

            foreach (string childDirectory in directories)
                children.Add(CreateDirectoryNode(childDirectory));

            foreach (string childFile in files)
                children.Add(CreateFileNode(childFile));

            item.ItemsSource = children;
            return item;
        }

        private TreeViewItem CreateFileNode(string filePath)
        {
            return new TreeViewItem
            {
                Header = Path.GetFileName(filePath),
                Tag = filePath
            };
        }

        private void OnProjectTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (sender is not TreeView treeView)
                return;

            if (treeView.SelectedItem is not TreeViewItem item)
                return;

            if (item.Tag is not string selectedPath || string.IsNullOrWhiteSpace(selectedPath))
                return;

            if (Directory.Exists(selectedPath))
            {
                ShowResourceDirectory(selectedPath);
                return;
            }

            if (File.Exists(selectedPath))
            {
                string? directoryPath = Path.GetDirectoryName(selectedPath);
                if (!string.IsNullOrWhiteSpace(directoryPath))
                    ShowResourceDirectory(directoryPath);
            }
        }

        private void ShowResourceDirectory(string directoryPath)
        {
            string fullDirectoryPath = Path.GetFullPath(directoryPath);
            _currentResourceDirectoryPath = fullDirectoryPath;
            _selectedResourceItemState = null;

            string[] directories;
            string[] files;

            try
            {
                directories = Directory.GetDirectories(fullDirectoryPath);
                files = Directory.GetFiles(fullDirectoryPath);
            }
            catch
            {
                BottomDockSlot.Content = CreateResourceExplorerContent(CreatePlaceholder("无法读取该目录内容"));
                return;
            }

            Array.Sort(directories, StringComparer.OrdinalIgnoreCase);
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);

            WrapPanel panel = new WrapPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(12)
            };

            foreach (string childDirectory in directories)
                panel.Children.Add(CreateResourceIconItem(childDirectory, true));

            foreach (string childFile in files)
                panel.Children.Add(CreateResourceIconItem(childFile, false));

            Control content;

            if (panel.Children.Count == 0)
            {
                content = CreatePlaceholder("该目录为空");
            }
            else
            {
                content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                };
            }

            BottomDockSlot.Content = CreateResourceExplorerContent(content);
        }

        private Control CreateResourceIconItem(string path, bool isDirectory)
        {
            string name = Path.GetFileName(path);

            Border item = new Border
            {
                Width = 110,
                Margin = new Thickness(0, 0, 12, 12),
                Padding = new Thickness(10),
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new StackPanel
                {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = isDirectory ? "📁" : "📄",
                            FontSize = 50,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            TextAlignment = TextAlignment.Center
                        },
                        new TextBlock
                        {
                            Text = name,
                            Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                            TextAlignment = TextAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            MaxWidth = 86,
                            HorizontalAlignment = HorizontalAlignment.Center
                        }
                    }
                }
            };

            ResourceItemState state = new ResourceItemState(item, path, isDirectory);
            UpdateResourceItemVisual(state);

            item.PointerEntered += (_, _) =>
            {
                state.IsPointerOver = true;
                UpdateResourceItemVisual(state);
            };

            item.PointerExited += (_, _) =>
            {
                state.IsPointerOver = false;
                state.IsPressed = false;
                UpdateResourceItemVisual(state);
            };

            item.PointerPressed += (_, _) =>
            {
                SelectResourceItem(state);
                state.IsPressed = true;
                UpdateResourceItemVisual(state);
            };

            item.PointerReleased += (_, _) =>
            {
                state.IsPressed = false;
                UpdateResourceItemVisual(state);
            };

            item.Tapped += (_, _) =>
            {
                SelectResourceItem(state);
            };

            item.DoubleTapped += (_, _) =>
            {
                SelectResourceItem(state);
                OnResourceItemDoubleTapped(path, isDirectory);
            };

            return item;
        }

        private Control CreateResourceExplorerContent(Control content)
        {
            Grid grid = new Grid();

            if (ShouldShowResourceBackButton())
            {
                grid.RowDefinitions.Add(new RowDefinition(40, GridUnitType.Pixel));
                grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

                Border topBar = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#181818")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 4),
                    Child = CreateResourceBackButton()
                };

                Grid.SetRow(topBar, 0);
                Grid.SetRow(content, 1);

                grid.Children.Add(topBar);
                grid.Children.Add(content);
                return grid;
            }

            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            Grid.SetRow(content, 0);
            grid.Children.Add(content);
            return grid;
        }

        private Control CreateResourceBackButton()
        {
            Button button = new Button
            {
                Content = "返回上一级",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 120,
                Height = 28,
                Padding = new Thickness(14, 0)
            };

            button.Click += (_, _) => NavigateToParentResourceDirectory();
            return button;
        }

        private void NavigateToParentResourceDirectory()
        {
            if (string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                return;

            if (string.IsNullOrWhiteSpace(_projectRootPath))
                return;

            string currentFullPath = Path.GetFullPath(_currentResourceDirectoryPath);
            string rootFullPath = Path.GetFullPath(_projectRootPath);

            if (PathsEqual(currentFullPath, rootFullPath))
                return;

            DirectoryInfo? parent = Directory.GetParent(currentFullPath);
            if (parent == null)
                return;

            string parentFullPath = Path.GetFullPath(parent.FullName);

            if (!IsPathInsideProjectRoot(parentFullPath))
                return;

            EnterResourceDirectory(parentFullPath);
        }

        private bool ShouldShowResourceBackButton()
        {
            if (string.IsNullOrWhiteSpace(_projectRootPath))
                return false;

            if (string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                return false;

            return !PathsEqual(_projectRootPath, _currentResourceDirectoryPath);
        }

        private bool IsPathInsideProjectRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(_projectRootPath))
                return false;

            string rootFullPath = Path.GetFullPath(_projectRootPath);
            string targetFullPath = Path.GetFullPath(path);

            StringComparison comparison =
                OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            string normalizedRoot = Path.TrimEndingDirectorySeparator(rootFullPath);
            string normalizedTarget = Path.TrimEndingDirectorySeparator(targetFullPath);

            if (string.Equals(normalizedRoot, normalizedTarget, comparison))
                return true;

            string rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
            return normalizedTarget.StartsWith(rootPrefix, comparison);
        }

        private void SelectResourceItem(ResourceItemState state)
        {
            ResourceItemState? previous = _selectedResourceItemState;
            _selectedResourceItemState = state;

            if (previous != null && !ReferenceEquals(previous, state))
                UpdateResourceItemVisual(previous);

            UpdateResourceItemVisual(state);
        }

        private void UpdateResourceItemVisual(ResourceItemState state)
        {
            bool isSelected = ReferenceEquals(_selectedResourceItemState, state);

            if (isSelected)
            {
                state.Item.Background = new SolidColorBrush(Color.Parse("#225588"));
                state.Item.BorderBrush = new SolidColorBrush(Color.Parse("#44AAFF"));
                return;
            }

            if (state.IsPressed)
            {
                state.Item.Background = new SolidColorBrush(Color.Parse("#2A2A2A"));
                state.Item.BorderBrush = new SolidColorBrush(Color.Parse("#666666"));
                return;
            }

            if (state.IsPointerOver)
            {
                state.Item.Background = new SolidColorBrush(Color.Parse("#2F2F2F"));
                state.Item.BorderBrush = new SolidColorBrush(Color.Parse("#777777"));
                return;
            }

            state.Item.Background = new SolidColorBrush(Color.Parse("#202020"));
            state.Item.BorderBrush = new SolidColorBrush(Color.Parse("#444444"));
        }

        private string GetDirectoryDisplayName(string directoryPath, bool isRoot)
        {
            if (isRoot)
            {
                string trimmedPath = Path.TrimEndingDirectorySeparator(directoryPath);
                string name = Path.GetFileName(trimmedPath);
                return string.IsNullOrWhiteSpace(name) ? directoryPath : name;
            }

            string directoryName = Path.GetFileName(directoryPath);
            return string.IsNullOrWhiteSpace(directoryName) ? directoryPath : directoryName;
        }

        private void OnResourceItemDoubleTapped(string path, bool isDirectory)
        {
            if (isDirectory)
            {
                EnterResourceDirectory(path);
                return;
            }

            string analyzedFileType = AnalyzeFileType(path);

            if (TryHandleSpecialFileOpen(path, analyzedFileType))
                return;

            TryOpenFileWithSystem(path);
        }

        private void EnterResourceDirectory(string directoryPath)
        {
            string fullDirectoryPath = Path.GetFullPath(directoryPath);

            TrySelectProjectTreePath(fullDirectoryPath);
            ShowResourceDirectory(fullDirectoryPath);
        }

        private void ClearProjectTreeSelection(System.Collections.IEnumerable items)
        {
            foreach (object? itemObject in items)
            {
                if (itemObject is not TreeViewItem item)
                    continue;

                item.IsSelected = false;

                if (item.ItemsSource is System.Collections.IEnumerable childItems)
                    ClearProjectTreeSelection(childItems);
            }
        }

        private bool TrySelectProjectTreePath(string targetPath)
        {
            if (ProjectFilesSlot.Content is not TreeView treeView)
                return false;

            if (treeView.ItemsSource is not System.Collections.IEnumerable rootItems)
                return false;

            ClearProjectTreeSelection(rootItems);
            return TrySelectProjectTreePath(rootItems, Path.GetFullPath(targetPath));
        }

        private bool TrySelectProjectTreePath(System.Collections.IEnumerable items, string targetPath)
        {
            foreach (object? itemObject in items)
            {
                if (itemObject is not TreeViewItem item)
                    continue;

                if (item.Tag is string itemPath && PathsEqual(itemPath, targetPath))
                {
                    item.IsExpanded = true;
                    item.IsSelected = true;
                    item.BringIntoView();
                    return true;
                }

                if (item.ItemsSource is System.Collections.IEnumerable childItems &&
                    TrySelectProjectTreePath(childItems, targetPath))
                {
                    item.IsExpanded = true;
                    return true;
                }
            }

            return false;
        }

        private string AnalyzeFileType(string filePath)
        {
            return Path.GetExtension(filePath);
        }

        private bool TryHandleSpecialFileOpen(string filePath, string analyzedFileType)
        {
            if (!string.Equals(analyzedFileType, ".json", StringComparison.OrdinalIgnoreCase))
                return false;

            return TryOpenSceneJson(filePath);
        }

        private bool TryOpenSceneJson(string filePath)
        {
            if (!TryReadSceneJson(filePath, out _, out _))
                return false;

            try
            {
                string assetRootPath = ResolveSceneAssetRoot(filePath);
                EditorSceneOpenResult openResult = PrepareEditorSceneCopies(filePath, assetRootPath);

                LeftDockSlot.Content = BuildSceneTreeControl(openResult.TreeScene);

                EditorHostBridge.SetAssetRootAndReloadAssets(assetRootPath);
                EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);

                PixelSize hostSize = GetSceneHostPixelSize();
                if (hostSize.Width > 0 && hostSize.Height > 0)
                    EditorHostBridge.SetRenderWindowSize(hostSize.Width, hostSize.Height);

                if (EditorHostBridge.IsRenderWindowAlive)
                    EditorHostBridge.RunRenderFrame();

                return true;
            }
            catch
            {
                return false;
            }
        }

        private EditorSceneOpenResult PrepareEditorSceneCopies(string originalPath, string assetRootPath)
        {
            string editorWorkingRoot = GetEditorWorkingRoot(assetRootPath);

            string treeCopyDirectory = Path.Combine(editorWorkingRoot, EditorTreeCopyDirectoryName);
            string previewDirectory = Path.Combine(editorWorkingRoot, EditorPreviewDirectoryName);

            Directory.CreateDirectory(editorWorkingRoot);
            Directory.CreateDirectory(treeCopyDirectory);
            Directory.CreateDirectory(previewDirectory);

            string treeCopyPath = BuildTreeCopyPath(treeCopyDirectory, originalPath);
            string previewScenePath = Path.Combine(previewDirectory, EditorPreviewSceneFileName);

            File.Copy(originalPath, treeCopyPath, true);

            SceneData treeScene = LoadSceneDataFromFile(treeCopyPath);
            SceneData previewScene = BuildPreviewScene(treeScene);

            SaveSceneData(previewScenePath, previewScene);

            return new EditorSceneOpenResult
            {
                TreeCopyPath = treeCopyPath,
                PreviewScenePath = previewScenePath,
                TreeScene = treeScene,
                PreviewScene = previewScene
            };
        }

        private bool TryReadSceneJson(string filePath, out SceneData scene, out string reason)
        {
            scene = new SceneData();
            reason = string.Empty;

            try
            {
                string json = File.ReadAllText(filePath);
                using JsonDocument doc = JsonDocument.Parse(json);

                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    reason = "Root must be a JSON object.";
                    return false;
                }

                if (!root.TryGetProperty("sceneId", out JsonElement sceneIdElement) ||
                    sceneIdElement.ValueKind != JsonValueKind.String)
                {
                    reason = "Missing or invalid 'sceneId'.";
                    return false;
                }

                string sceneId = sceneIdElement.GetString()?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(sceneId))
                {
                    reason = "'sceneId' cannot be empty.";
                    return false;
                }

                if (!root.TryGetProperty("objects", out JsonElement objectsElement) ||
                    objectsElement.ValueKind != JsonValueKind.Array)
                {
                    reason = "Missing or invalid 'objects' array.";
                    return false;
                }

                SceneData? parsed = JsonSerializer.Deserialize<SceneData>(json, SceneJsonReadOptions);
                if (parsed == null)
                {
                    reason = "Failed to deserialize scene.";
                    return false;
                }

                scene = parsed;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private SceneData LoadSceneDataFromFile(string filePath)
        {
            if (!TryReadSceneJson(filePath, out SceneData scene, out string reason))
                throw new InvalidDataException(reason);

            return scene;
        }

        private void SaveSceneData(string filePath, SceneData scene)
        {
            string json = JsonSerializer.Serialize(scene, SceneJsonWriteOptions);
            File.WriteAllText(filePath, json);
        }

        private SceneData BuildPreviewScene(SceneData sourceScene)
        {
            SceneData previewScene = CloneSceneData(sourceScene);
            previewScene.SceneId = EditorPreviewSceneId;

            foreach (SceneObject obj in previewScene.Objects)
            {
                obj.Physics = null;

                if (string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                {
                    obj.Active = false;
                    obj.Visible = false;
                }
            }

            SceneObject previewCamera = CreatePreviewCamera(sourceScene, previewScene.Objects);
            previewScene.Objects.Add(previewCamera);

            return previewScene;
        }

        private SceneObject CreatePreviewCamera(SceneData sourceScene, List<SceneObject> targetObjects)
        {
            SceneObject? sourceCamera = sourceScene.Objects.FirstOrDefault(
                o => string.Equals(o.Type, "Camera", StringComparison.Ordinal));

            string previewCameraId = MakeUniqueObjectId(targetObjects, PreviewCameraIdBase);
            string previewCameraName = !string.IsNullOrWhiteSpace(sourceCamera?.Name)
                ? sourceCamera!.Name
                : "MainCamera";

            SceneTransform previewTransform = sourceCamera?.Transform != null
                ? CloneTransform(sourceCamera.Transform)
                : new SceneTransform
                {
                    ParentId = null,
                    LocalPosition = new Double3(0.0, 0.0, 5.0),
                    LocalRotation = new Double3(0.0, 180.0, 0.0),
                    LocalScale = Double3.One
                };

            string previewCameraData = BuildPreviewCameraData(sourceCamera?.Data);

            return new SceneObject
            {
                Id = previewCameraId,
                Name = previewCameraName,
                Tags = new List<string>(),
                Active = true,
                Transform = previewTransform,
                Type = "Camera",
                Controller = null,
                Data = previewCameraData,
                Mesh = null,
                Visible = true,
                RenderTag = "",
                Physics = null,
                Materials = null
            };
        }

        private string BuildPreviewCameraData(string? sourceData)
        {
            int renderMode = 0;
            double fovOrSize = 75.0;
            double nearClip = 0.01;
            double farClip = 1000.0;
            int projectionType = 0;

            if (!string.IsNullOrWhiteSpace(sourceData))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(sourceData);
                    JsonElement root = doc.RootElement;

                    if (root.TryGetProperty("renderMode", out JsonElement renderModeElement) &&
                        renderModeElement.ValueKind == JsonValueKind.Number)
                        renderMode = renderModeElement.GetInt32();

                    if (root.TryGetProperty("fovOrSize", out JsonElement fovElement) &&
                        fovElement.ValueKind == JsonValueKind.Number)
                        fovOrSize = fovElement.GetDouble();

                    if (root.TryGetProperty("nearClip", out JsonElement nearElement) &&
                        nearElement.ValueKind == JsonValueKind.Number)
                        nearClip = nearElement.GetDouble();

                    if (root.TryGetProperty("farClip", out JsonElement farElement) &&
                        farElement.ValueKind == JsonValueKind.Number)
                        farClip = farElement.GetDouble();

                    if (root.TryGetProperty("projectionType", out JsonElement projectionElement) &&
                        projectionElement.ValueKind == JsonValueKind.Number)
                        projectionType = projectionElement.GetInt32();
                }
                catch
                {
                }
            }

            return JsonSerializer.Serialize(new
            {
                renderMode,
                fovOrSize,
                nearClip,
                farClip,
                projectionType,
                isMainCamera = true
            });
        }

        private string MakeUniqueObjectId(IEnumerable<SceneObject> objects, string baseId)
        {
            HashSet<string> ids = new HashSet<string>(
                objects.Select(o => o.Id),
                StringComparer.Ordinal);

            if (!ids.Contains(baseId))
                return baseId;

            int index = 1;
            while (true)
            {
                string candidate = baseId + index.ToString();
                if (!ids.Contains(candidate))
                    return candidate;

                index++;
            }
        }

        private SceneData CloneSceneData(SceneData source)
        {
            return new SceneData
            {
                SceneId = source.SceneId,
                Objects = source.Objects.Select(CloneSceneObject).ToList()
            };
        }

        private SceneObject CloneSceneObject(SceneObject source)
        {
            return new SceneObject
            {
                Id = source.Id,
                Name = source.Name,
                Tags = source.Tags != null ? new List<string>(source.Tags) : new List<string>(),
                Active = source.Active,
                Transform = CloneTransform(source.Transform),
                Type = source.Type,
                Controller = source.Controller,
                Data = source.Data,
                Mesh = source.Mesh,
                Visible = source.Visible,
                RenderTag = source.RenderTag,
                Physics = ClonePhysicsBody(source.Physics),
                Materials = source.Materials != null ? new List<string>(source.Materials) : null
            };
        }

        private SceneTransform CloneTransform(SceneTransform? source)
        {
            if (source == null)
            {
                return new SceneTransform
                {
                    ParentId = null,
                    LocalPosition = Double3.Zero,
                    LocalRotation = Double3.Zero,
                    LocalScale = Double3.One
                };
            }

            return new SceneTransform
            {
                ParentId = source.ParentId,
                LocalPosition = source.LocalPosition,
                LocalRotation = source.LocalRotation,
                LocalScale = source.LocalScale
            };
        }

        private PhysicsBody? ClonePhysicsBody(PhysicsBody? source)
        {
            if (source == null)
                return null;

            return new PhysicsBody
            {
                Enabled = source.Enabled,
                MotionType = source.MotionType,
                ShapeType = source.ShapeType,
                Size = source.Size,
                Radius = source.Radius,
                Length = source.Length,
                Mass = source.Mass,
                Friction = source.Friction,
                Restitution = source.Restitution,
                UseGravity = source.UseGravity,
                EnableSpeculativeContacts = source.EnableSpeculativeContacts,
                LinearDamping = source.LinearDamping,
                AngularDamping = source.AngularDamping
            };
        }

        private Control BuildSceneTreeControl(SceneData scene)
        {
            Dictionary<string, SceneObject> objectMap = new Dictionary<string, SceneObject>(StringComparer.Ordinal);
            Dictionary<string, List<SceneObject>> childrenMap = new Dictionary<string, List<SceneObject>>(StringComparer.Ordinal);
            List<SceneObject> roots = new List<SceneObject>();

            foreach (SceneObject obj in scene.Objects)
            {
                if (!string.IsNullOrWhiteSpace(obj.Id))
                    objectMap[obj.Id] = obj;
            }

            foreach (SceneObject obj in scene.Objects)
            {
                string? parentId = obj.Transform?.ParentId;

                if (!string.IsNullOrWhiteSpace(parentId) && objectMap.ContainsKey(parentId))
                {
                    if (!childrenMap.TryGetValue(parentId, out List<SceneObject>? children))
                    {
                        children = new List<SceneObject>();
                        childrenMap[parentId] = children;
                    }

                    children.Add(obj);
                }
                else
                {
                    roots.Add(obj);
                }
            }

            roots.Sort(CompareSceneObjects);

            foreach (List<SceneObject> children in childrenMap.Values)
                children.Sort(CompareSceneObjects);

            TreeView treeView = new TreeView
            {
                Margin = new Thickness(8),
                ItemsSource = roots.Select(root => CreateSceneTreeItem(root, childrenMap)).Cast<object>().ToList()
            };

            return treeView;
        }

        private TreeViewItem CreateSceneTreeItem(
            SceneObject obj,
            Dictionary<string, List<SceneObject>> childrenMap)
        {
            string title = string.IsNullOrWhiteSpace(obj.Name) ? obj.Id : obj.Name;
            string type = string.IsNullOrWhiteSpace(obj.Type) ? "Object" : obj.Type;

            TreeViewItem item = new TreeViewItem
            {
                Header = $"{title} [{type}]",
                Tag = obj.Id
            };

            if (childrenMap.TryGetValue(obj.Id, out List<SceneObject>? children) && children.Count > 0)
                item.ItemsSource = children.Select(child => CreateSceneTreeItem(child, childrenMap)).Cast<object>().ToList();

            return item;
        }

        private int CompareSceneObjects(SceneObject left, SceneObject right)
        {
            string leftKey = string.IsNullOrWhiteSpace(left.Name) ? left.Id : left.Name;
            string rightKey = string.IsNullOrWhiteSpace(right.Name) ? right.Id : right.Name;
            return string.Compare(leftKey, rightKey, StringComparison.OrdinalIgnoreCase);
        }

        private string BuildTreeCopyPath(string treeCopyDirectory, string originalPath)
        {
            string baseName = Path.GetFileNameWithoutExtension(originalPath);
            string safeBaseName = SanitizeFileName(baseName);
            string hash = ComputeStableShortHash(Path.GetFullPath(originalPath));

            return Path.Combine(
                treeCopyDirectory,
                $"{safeBaseName}_{hash}.treecopy");
        }

        private string SanitizeFileName(string fileName)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(fileName.Length);

            foreach (char ch in fileName)
            {
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            }

            return builder.ToString();
        }

        private string ComputeStableShortHash(string text)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
            return Convert.ToHexString(bytes, 0, 6);
        }

        private PixelSize GetSceneHostPixelSize()
        {
            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;
            int width = Math.Max(1, (int)Math.Round(_sceneHost.Bounds.Width * scaling));
            int height = Math.Max(1, (int)Math.Round(_sceneHost.Bounds.Height * scaling));
            return new PixelSize(width, height);
        }

        private void TryOpenFileWithSystem(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = filePath,
                    UseShellExecute = true
                });
            }
            catch
            {
            }
        }

        private bool PathsEqual(string leftPath, string rightPath)
        {
            string leftFullPath = Path.GetFullPath(leftPath);
            string rightFullPath = Path.GetFullPath(rightPath);

            StringComparison comparison =
                OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(leftFullPath, rightFullPath, comparison);
        }
    }
}