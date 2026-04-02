using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Diagnostics;

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
            _sceneHost.NativeControlCreated += OnSceneHostCreated;
            _sceneHost.HostPixelSizeChanged += OnSceneHostSizeChanged;
            _sceneHost.NativeControlDestroyed += OnSceneHostDestroyed;

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
            return false;
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

        private void OnSceneHostCreated(IntPtr handle, string descriptor, PixelSize pixelSize)
        {
            // 把原生宿主句柄交给引擎
            // EditorHostBridge.AttachEmbeddedRenderSurface(handle, descriptor, pixelSize.Width, pixelSize.Height);
        }

        private void OnSceneHostSizeChanged(PixelSize pixelSize)
        {
            // 通知引擎重设渲染目标大小
            // EditorHostBridge.ResizeEmbeddedRenderSurface(pixelSize.Width, pixelSize.Height);
        }

        private void OnSceneHostDestroyed()
        {
            // 解除绑定
            // EditorHostBridge.DetachEmbeddedRenderSurface();
        }
    }
}