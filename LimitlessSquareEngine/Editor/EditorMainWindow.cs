using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        private DispatcherTimer? _sceneOpenForceRefreshTimer;
        private int _sceneOpenForceRefreshRemainingTicks;
        private string? _currentTreeCopyPath;
        private string? _currentPreviewScenePath;
        private SceneData? _currentTreeScene;
        private SceneData? _currentPreviewScene;
        private SceneObject? _selectedSceneObject;
        private bool _isUpdatingSceneInspector;
        private bool _isProgrammaticSceneTreeSelection;
        private TextBox? _activeInspectorTextBox;

        private const string EditorPreviewSceneId = "__editor_preview_scene__";
        private const string EditorPreviewDirectoryName = "EditorPreview";
        private const string EditorTreeCopyDirectoryName = "EditorTreeCopies";
        private const string EditorPreviewSceneFileName = EditorPreviewSceneId + ".json";
        private const string PreviewCameraIdBase = "__editor_preview_camera__";

        private const double InspectorTextBoxHeight = 22;
        private static readonly Thickness InspectorTextBoxPadding = new Thickness(6, 1, 6, 1);
        private const double InspectorPropertySpacing = 1;

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

            Opened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ForceSyncScenePreviewSurface();
                }, DispatcherPriority.Render);
            };

            // 先这么搞
            LeftDockSlot.Content = CreatePlaceholder("未加载场景或画布");
            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");
            ProjectFilesSlot.Content = CreatePlaceholder("未选择项目文件夹");
            BottomDockSlot.Content = CreatePlaceholder("未选择文件夹");
            ToolbarSlot.Content = CreateTopMenuBar();

            Content = BuildLayout();

            AddHandler(InputElement.KeyDownEvent, OnWindowClipboardKeyDown, RoutingStrategies.Tunnel, true);
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

        private enum ClipboardCommand
        {
            Cut,
            Copy,
            Paste,
            SelectAll
        }

        private void OnSceneHostResized(PixelSize hostSize)
        {
            if (hostSize.Width > 0 && hostSize.Height > 0)
                EditorHostBridge.SetRenderWindowSize(hostSize.Width, hostSize.Height);
        }

        private void ForceSyncScenePreviewSurface()
        {
            PixelSize hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            EditorHostBridge.SetRenderWindowSize(hostSize.Width, hostSize.Height);
        }

        private void StartSceneOpenForceRefresh()
        {
            _sceneOpenForceRefreshRemainingTicks = 4;

            if (_sceneOpenForceRefreshTimer == null)
            {
                _sceneOpenForceRefreshTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(1)
                };

                _sceneOpenForceRefreshTimer.Tick += OnSceneOpenForceRefreshTick;
            }

            _sceneOpenForceRefreshTimer.Stop();

            RunSceneOpenForceRefreshPass();
            _sceneOpenForceRefreshTimer.Start();
        }

        private void OnSceneOpenForceRefreshTick(object? sender, EventArgs e)
        {
            RunSceneOpenForceRefreshPass();

            _sceneOpenForceRefreshRemainingTicks--;
            if (_sceneOpenForceRefreshRemainingTicks <= 0)
                _sceneOpenForceRefreshTimer?.Stop();
        }

        private void RunSceneOpenForceRefreshPass()
        {
            _sceneHost.InvalidateMeasure();
            _sceneHost.InvalidateArrange();
            _sceneHost.InvalidateVisual();

            PixelSize hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            int pulseWidth = hostSize.Width;
            int pulseHeight = hostSize.Height;

            if (pulseWidth > 1)
            {
                pulseWidth -= 1;
            }
            else
            {
                pulseWidth += 1;
            }

            if (pulseWidth == hostSize.Width)
            {
                if (pulseHeight > 1)
                    pulseHeight -= 1;
                else
                    pulseHeight += 1;
            }

            EditorHostBridge.SetRenderWindowSize(pulseWidth, pulseHeight);
            EditorHostBridge.SetRenderWindowSize(hostSize.Width, hostSize.Height);

            if (EditorHostBridge.IsRenderWindowAlive)
            {
                EditorHostBridge.RunRenderFrame();
                PresentLatestFrame();
            }
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

            workspace.ColumnDefinitions.Add(new ColumnDefinition(420, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            workspace.ColumnDefinitions.Add(new ColumnDefinition(320, GridUnitType.Pixel));

            workspace.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            workspace.RowDefinitions.Add(new RowDefinition(5, GridUnitType.Pixel));
            workspace.RowDefinitions.Add(new RowDefinition(320, GridUnitType.Pixel));

            Control leftPanel = CreateDockContainer("场景树/画布树", LeftDockSlot);
            Control scenePanel = CreateSceneContainer();
            Control rightPanel = CreateDockContainer("查看器", RightDockSlot);
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
                ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock titleText = new TextBlock
            {
                Text = "Limitless Square Editor",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Margin = new Thickness(0, 0, 12, 0)
            };

            StackPanel leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Children =
                {
                    titleText,
                    ToolbarSlot
                }
            };

            Control playbackControls = CreatePlaybackControls();

            Border rightSpacer = new Border();

            Grid.SetColumn(leftPanel, 0);
            Grid.SetColumn(playbackControls, 1);
            Grid.SetColumn(rightSpacer, 2);

            grid.Children.Add(leftPanel);
            grid.Children.Add(playbackControls);
            grid.Children.Add(rightSpacer);

            return grid;
        }

        private Control CreatePlaybackControls()
        {
            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(CreatePlaybackButton("▶"));
            panel.Children.Add(CreatePlaybackButton("◯"));
            panel.Children.Add(CreatePlaybackButton("▷"));

            return panel;
        }

        private Button CreatePlaybackButton(string glyph)
        {
            return new Button
            {
                Content = new TextBlock
                {
                    Text = glyph,
                    FontSize = 13,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                },
                Width = 30,
                Height = 24,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };
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

            grid.ColumnDefinitions.Add(new ColumnDefinition(360, GridUnitType.Pixel));
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

        private Control CreateCompactTreeHeader(string text)
        {
            return new Border
            {
                Height = InspectorTextBoxHeight,
                MinHeight = InspectorTextBoxHeight,
                Padding = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };
        }

        private Control CreateViewerContent(string title, IEnumerable<(string Label, string Value)> items)
        {
            StackPanel stack = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8
            };

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Brushes.White,
                FontSize = 14,
                FontWeight = FontWeight.Bold
            });

            foreach ((string label, string value) in items)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"{label}: {value}",
                    Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private void ShowResourceDetailsInViewer(string path, bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    DirectoryInfo directoryInfo = new DirectoryInfo(path);

                    RightDockSlot.Content = CreateViewerContent(
                        "文件夹信息",
                        new (string Label, string Value)[]
                        {
                            ("名称", directoryInfo.Name),
                            ("路径", directoryInfo.FullName),
                            ("类型", "文件夹"),
                            ("最后修改", directoryInfo.Exists ? directoryInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")
                        });

                    return;
                }

                FileInfo fileInfo = new FileInfo(path);

                RightDockSlot.Content = CreateViewerContent(
                    "文件信息",
                    new (string Label, string Value)[]
                    {
                        ("名称", fileInfo.Name),
                        ("路径", fileInfo.FullName),
                        ("类型", string.IsNullOrWhiteSpace(fileInfo.Extension) ? "未知" : fileInfo.Extension),
                        ("大小", fileInfo.Exists ? $"{fileInfo.Length} 字节" : "-"),
                        ("最后修改", fileInfo.Exists ? fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss") : "-")
                    });
            }
            catch (Exception ex)
            {
                RightDockSlot.Content = CreateViewerContent(
                    "文件信息",
                    new (string Label, string Value)[]
                    {
                        ("路径", path),
                        ("错误", ex.Message)
                    });
            }
        }

        private void ShowSceneObjectInspector(SceneObject obj)
        {
            RightDockSlot.Content = CreateSceneObjectInspector(obj);
        }

        private Control CreateSceneObjectInspector(SceneObject obj)
        {
            obj.Transform ??= CloneTransform(null);

            StackPanel root = new StackPanel
            {
                Spacing = 4
            };

            root.Children.Add(CreateTopInlineIdentityRow(obj));

            root.Children.Add(CreateTextPropertyEditor("名称", () => obj.Name ?? "", value =>
            {
                obj.Name = value;
                return PersistSceneObjectChanges(obj, true);
            }));
            root.Children.Add(CreateTextPropertyEditor("类型", () => obj.Type ?? "", value =>
            {
                obj.Type = value;
                return PersistSceneObjectChanges(obj, true);
            }));

            root.Children.Add(CreateTextPropertyEditor("父节点", () => obj.Transform?.ParentId ?? "", value =>
            {
                return TryApplyParentId(obj, value);
            }));

            root.Children.Add(CreateInspectorSectionHeader("Transform"));
            root.Children.Add(CreateVector3PropertyEditor(
                "位置",
                () => obj.Transform!.LocalPosition,
                value =>
                {
                    obj.Transform!.LocalPosition = value;
                    return PersistSceneObjectChanges(obj, false);
                }));
            root.Children.Add(CreateVector3PropertyEditor(
                "旋转",
                () => obj.Transform!.LocalRotation,
                value =>
                {
                    obj.Transform!.LocalRotation = value;
                    return PersistSceneObjectChanges(obj, false);
                }));
            root.Children.Add(CreateVector3PropertyEditor(
                "缩放",
                () => obj.Transform!.LocalScale,
                value =>
                {
                    obj.Transform!.LocalScale = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            root.Children.Add(CreateInspectorSectionHeader("其它"));
            root.Children.Add(CreateTextPropertyEditor("Controller", () => obj.Controller ?? "", value =>
            {
                obj.Controller = string.IsNullOrWhiteSpace(value) ? null : value;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Mesh", () => obj.Mesh ?? "", value =>
            {
                obj.Mesh = string.IsNullOrWhiteSpace(value) ? null : value;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("RenderTag", () => obj.RenderTag ?? "", value =>
            {
                obj.RenderTag = value ?? "";
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Tags", () => obj.Tags == null ? "" : string.Join(", ", obj.Tags), value =>
            {
                obj.Tags = SplitCommaSeparatedList(value);
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Materials", () => obj.Materials == null ? "" : string.Join(", ", obj.Materials), value =>
            {
                List<string> list = SplitCommaSeparatedList(value);
                obj.Materials = list.Count == 0 ? null : list;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Data", () => obj.Data ?? "", value =>
            {
                obj.Data = string.IsNullOrWhiteSpace(value) ? null : value;
                return PersistSceneObjectChanges(obj, false);
            }));

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = root
            };
        }

        private Control CreateInspectorSectionHeader(string title)
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#222222")),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Height = InspectorTextBoxHeight,
                Padding = new Thickness(12, 0, 12, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Child = new TextBlock
                {
                    Text = title,
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        private Control CreatePropertyRow(string label, Control editor)
        {
            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = InspectorPropertySpacing,
                Margin = new Thickness(12, 0, 12, 0)
            };

            panel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.Parse("#CFCFCF")),
                VerticalAlignment = VerticalAlignment.Center
            });

            panel.Children.Add(editor);
            return panel;
        }

        private async void OnWindowClipboardKeyDown(object? sender, KeyEventArgs e)
        {
            if (!HasPrimaryCommandModifier(e.KeyModifiers))
                return;

            TextBox? textBox = GetFocusedTextBox();
            if (textBox == null)
                return;

            ClipboardCommand? command = e.Key switch
            {
                Key.X => ClipboardCommand.Cut,
                Key.C => ClipboardCommand.Copy,
                Key.V => ClipboardCommand.Paste,
                Key.A => ClipboardCommand.SelectAll,
                _ => null
            };

            if (command == null)
                return;

            e.Handled = true;

            try
            {
                await ExecuteClipboardCommandAsync(textBox, command.Value);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
            }
        }

        private bool HasPrimaryCommandModifier(KeyModifiers modifiers)
        {
            return OperatingSystem.IsMacOS()
                ? modifiers.HasFlag(KeyModifiers.Meta)
                : modifiers.HasFlag(KeyModifiers.Control);
        }

        private TextBox? GetFocusedTextBox()
        {
            if (_activeInspectorTextBox != null)
                return _activeInspectorTextBox;

            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            return topLevel?.FocusManager?.GetFocusedElement() as TextBox;
        }

        private async Task<bool> TryExecuteClipboardCommandAsync(ClipboardCommand command)
        {
            TextBox? textBox = GetFocusedTextBox();
            if (textBox == null)
                return false;

            return await ExecuteClipboardCommandAsync(textBox, command);
        }

        private Task<bool> ExecuteClipboardCommandAsync(TextBox textBox, ClipboardCommand command)
        {
            try
            {
                if (!textBox.IsFocused)
                    textBox.Focus();

                switch (command)
                {
                    case ClipboardCommand.Copy:
                        textBox.Copy();
                        return Task.FromResult(true);

                    case ClipboardCommand.Cut:
                        if (textBox.IsReadOnly)
                            return Task.FromResult(false);

                        textBox.Cut();
                        return Task.FromResult(true);

                    case ClipboardCommand.Paste:
                        if (textBox.IsReadOnly)
                            return Task.FromResult(false);

                        textBox.Paste();
                        return Task.FromResult(true);

                    case ClipboardCommand.SelectAll:
                        textBox.SelectAll();
                        return Task.FromResult(true);

                    default:
                        return Task.FromResult(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
                return Task.FromResult(false);
            }
        }

        private static (int Start, int End) GetTextBoxSelectionRange(TextBox textBox)
        {
            string text = textBox.Text ?? string.Empty;
            int length = text.Length;

            int start = Math.Clamp(Math.Min(textBox.SelectionStart, textBox.SelectionEnd), 0, length);
            int end = Math.Clamp(Math.Max(textBox.SelectionStart, textBox.SelectionEnd), 0, length);

            return (start, end);
        }

        private static void ReplaceTextBoxSelection(TextBox textBox, string replacement)
        {
            string text = textBox.Text ?? string.Empty;
            (int start, int end) = GetTextBoxSelectionRange(textBox);

            string newText = text.Substring(0, start) + replacement + text.Substring(end);
            int caretIndex = start + replacement.Length;

            textBox.Text = newText;
            textBox.SelectionStart = caretIndex;
            textBox.SelectionEnd = caretIndex;
            textBox.CaretIndex = caretIndex;
        }

        private Control CreateInlineBoolToggle(
            string text,
            Func<bool> getter,
            Func<bool, bool> apply)
        {
            ToggleButton button = new ToggleButton
            {
                Content = text,
                IsChecked = getter(),
                Height = InspectorTextBoxHeight,
                MinHeight = InspectorTextBoxHeight,
                Padding = new Thickness(5, 0),
                CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            void UpdateVisual()
            {
                bool isChecked = button.IsChecked == true;
                button.Background = isChecked
                    ? new SolidColorBrush(Color.Parse("#3E6A4A"))
                    : new SolidColorBrush(Color.Parse("#2A2A2A"));
                button.BorderBrush = isChecked
                    ? new SolidColorBrush(Color.Parse("#6FB083"))
                    : new SolidColorBrush(Color.Parse("#555555"));
                button.Foreground = Brushes.White;
            }

            button.Checked += (_, _) =>
            {
                if (!apply(true))
                    button.IsChecked = getter();

                UpdateVisual();
            };

            button.Unchecked += (_, _) =>
            {
                if (!apply(false))
                    button.IsChecked = getter();

                UpdateVisual();
            };

            UpdateVisual();
            return button;
        }

        private Control CreateTopInlineIdentityRow(SceneObject obj)
        {
            Control activeToggle = CreateInlineBoolToggle("A", () => obj.Active, value =>
            {
                obj.Active = value;
                return PersistSceneObjectChanges(obj, false);
            });

            Control visibleToggle = CreateInlineBoolToggle("V", () => obj.Visible, value =>
            {
                obj.Visible = value;
                return PersistSceneObjectChanges(obj, false);
            });

            TextBox idBox = CreateInspectorTextBox(obj.Id);

            void ResetId()
            {
                _isUpdatingSceneInspector = true;
                idBox.Text = obj.Id;
                _isUpdatingSceneInspector = false;
            }

            void CommitId()
            {
                if (_isUpdatingSceneInspector)
                    return;

                string value = idBox.Text ?? string.Empty;
                if (!TryApplySceneObjectId(obj, value))
                {
                    ResetId();
                    return;
                }

                ResetId();
            }

            idBox.LostFocus += (_, _) => CommitId();
            idBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    CommitId();
            };

            Grid row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,4,Auto,4,*"),
                Margin = new Thickness(12, 8, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn((Control)activeToggle, 0);
            Grid.SetColumn((Control)visibleToggle, 2);
            Grid.SetColumn(idBox, 4);

            row.Children.Add((Control)activeToggle);
            row.Children.Add((Control)visibleToggle);
            row.Children.Add(idBox);

            return row;
        }

        private TextBox CreateInspectorTextBox(string text, IBrush? background = null)
        {
            TextBox textBox = new TextBox
            {
                Text = text,
                Height = InspectorTextBoxHeight,
                MinHeight = InspectorTextBoxHeight,
                Padding = InspectorTextBoxPadding,
                Background = background ?? new SolidColorBrush(Color.Parse("#1A1A1A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left
            };

            textBox.GotFocus += (_, _) => _activeInspectorTextBox = textBox;
            textBox.PointerPressed += (_, _) => _activeInspectorTextBox = textBox;
            textBox.ContextMenu = CreateInspectorTextBoxContextMenu(textBox);
            return textBox;
        }

        private ContextMenu CreateInspectorTextBoxContextMenu(TextBox textBox)
        {
            MenuItem cutItem = new MenuItem { Header = "剪切" };
            MenuItem copyItem = new MenuItem { Header = "复制" };
            MenuItem pasteItem = new MenuItem { Header = "粘贴" };
            MenuItem selectAllItem = new MenuItem { Header = "全选" };

            cutItem.Click += async (_, _) => await ExecuteClipboardCommandAsync(textBox, ClipboardCommand.Cut);
            copyItem.Click += async (_, _) => await ExecuteClipboardCommandAsync(textBox, ClipboardCommand.Copy);
            pasteItem.Click += async (_, _) => await ExecuteClipboardCommandAsync(textBox, ClipboardCommand.Paste);
            selectAllItem.Click += async (_, _) => await ExecuteClipboardCommandAsync(textBox, ClipboardCommand.SelectAll);

            return new ContextMenu
            {
                ItemsSource = new object[]
                {
            cutItem,
            copyItem,
            pasteItem,
            new MenuItem { Header = "-" },
            selectAllItem
                }
            };
        }

        private Control CreateTextPropertyEditor(
    string label,
    Func<string> getter,
    Func<string, bool> apply,
    bool isReadOnly = false)
        {
            TextBox textBox = CreateInspectorTextBox(getter());
            textBox.IsReadOnly = isReadOnly;

            void ResetText()
            {
                _isUpdatingSceneInspector = true;
                textBox.Text = getter();
                _isUpdatingSceneInspector = false;
            }

            void Commit()
            {
                if (_isUpdatingSceneInspector || isReadOnly)
                    return;

                string value = textBox.Text ?? string.Empty;

                if (!apply(value))
                {
                    ResetText();
                    return;
                }

                ResetText();
            }

            textBox.LostFocus += (_, _) => Commit();
            textBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    Commit();
            };

            return CreatePropertyRow(label, textBox);
        }

        private Control CreateVector3PropertyEditor(
            string label,
            Func<Double3> getter,
            Func<Double3, bool> apply)
        {
            Double3 current = getter();

            TextBox xBox = CreateInspectorTextBox(FormatDouble(current.X), new SolidColorBrush(Color.Parse("#331111")));
            TextBox yBox = CreateInspectorTextBox(FormatDouble(current.Y), new SolidColorBrush(Color.Parse("#113311")));
            TextBox zBox = CreateInspectorTextBox(FormatDouble(current.Z), new SolidColorBrush(Color.Parse("#111133")));

            Border CreateAxisTag(string axis, string color)
            {
                return new Border
                {
                    Width = 20,
                    Height = InspectorTextBoxHeight,
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Color.Parse(color)),
                    BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = axis,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center
                    }
                };
            }

            void ResetFromCurrent()
            {
                Double3 value = getter();

                _isUpdatingSceneInspector = true;
                xBox.Text = FormatDouble(value.X);
                yBox.Text = FormatDouble(value.Y);
                zBox.Text = FormatDouble(value.Z);
                _isUpdatingSceneInspector = false;
            }

            void Commit()
            {
                if (_isUpdatingSceneInspector)
                    return;

                if (!TryParseEditorDouble(xBox.Text, out double x) ||
                    !TryParseEditorDouble(yBox.Text, out double y) ||
                    !TryParseEditorDouble(zBox.Text, out double z))
                {
                    ResetFromCurrent();
                    return;
                }

                if (!apply(new Double3(x, y, z)))
                {
                    ResetFromCurrent();
                    return;
                }

                ResetFromCurrent();
            }

            void BindCommit(TextBox box)
            {
                box.LostFocus += (_, _) => Commit();
                box.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                        Commit();
                };
            }

            BindCommit(xBox);
            BindCommit(yBox);
            BindCommit(zBox);

            Grid editorGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,*,4,Auto,*,4,Auto,*"),
                VerticalAlignment = VerticalAlignment.Center
            };

            Border xTag = CreateAxisTag("X", "#331111");
            Border yTag = CreateAxisTag("Y", "#113311");
            Border zTag = CreateAxisTag("Z", "#111133");

            Grid.SetColumn(xTag, 0);
            Grid.SetColumn(xBox, 1);
            Grid.SetColumn(yTag, 3);
            Grid.SetColumn(yBox, 4);
            Grid.SetColumn(zTag, 6);
            Grid.SetColumn(zBox, 7);

            editorGrid.Children.Add(xTag);
            editorGrid.Children.Add(xBox);
            editorGrid.Children.Add(yTag);
            editorGrid.Children.Add(yBox);
            editorGrid.Children.Add(zTag);
            editorGrid.Children.Add(zBox);

            Grid.SetColumn(editorGrid.Children[0], 0);
            Grid.SetColumn(editorGrid.Children[1], 1);
            Grid.SetColumn(editorGrid.Children[2], 3);
            Grid.SetColumn(editorGrid.Children[3], 4);
            Grid.SetColumn(editorGrid.Children[4], 6);
            Grid.SetColumn(editorGrid.Children[5], 7);

            return CreatePropertyRow(label, editorGrid);
        }

        private string FormatDouble(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private bool TryParseEditorDouble(string? text, out double value)
        {
            string raw = (text ?? string.Empty).Trim();

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
                return true;

            if (double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
                return true;

            return false;
        }

        private List<string> SplitCommaSeparatedList(string text)
        {
            return (text ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        private bool TryApplySceneObjectId(SceneObject target, string value)
        {
            if (_currentTreeScene == null)
                return false;

            string newId = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newId))
                return false;

            if (_currentTreeScene.Objects.Any(o => !ReferenceEquals(o, target) && string.Equals(o.Id, newId, StringComparison.Ordinal)))
                return false;

            string oldId = target.Id;

            if (string.Equals(oldId, newId, StringComparison.Ordinal))
                return true;

            target.Id = newId;

            foreach (SceneObject obj in _currentTreeScene.Objects)
            {
                if (ReferenceEquals(obj, target))
                    continue;

                if (string.Equals(obj.Transform?.ParentId, oldId, StringComparison.Ordinal))
                    obj.Transform!.ParentId = newId;
            }

            return PersistSceneObjectChanges(target, true);
        }

        private bool TryApplyParentId(SceneObject target, string value)
        {
            if (_currentTreeScene == null)
                return false;

            string? parentId = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            if (string.Equals(parentId, target.Id, StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(parentId))
            {
                SceneObject? parent = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Id, parentId, StringComparison.Ordinal));

                if (parent == null)
                    return false;

                if (WouldCreateParentCycle(target, parentId))
                    return false;
            }

            target.Transform ??= CloneTransform(null);
            target.Transform.ParentId = parentId;

            return PersistSceneObjectChanges(target, true);
        }

        private bool WouldCreateParentCycle(SceneObject target, string newParentId)
        {
            if (_currentTreeScene == null)
                return true;

            string? currentId = newParentId;

            while (!string.IsNullOrWhiteSpace(currentId))
            {
                if (string.Equals(currentId, target.Id, StringComparison.Ordinal))
                    return true;

                SceneObject? current = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Id, currentId, StringComparison.Ordinal));

                currentId = current?.Transform?.ParentId;
            }

            return false;
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
                    Header = "工具",
                    Foreground = Brushes.White,
                    ItemsSource = CreateToolMenuItems()
                },
                new MenuItem
                {
                    Header = "配置",
                    Foreground = Brushes.White,
                    ItemsSource = CreateConfigMenuItems()
                },
                new MenuItem
                {
                    Header = "关于",
                    Foreground = Brushes.White,
                    ItemsSource = CreateAboutMenuItems()
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
            MenuItem undoItem = new MenuItem { Header = "撤销" };
            MenuItem redoItem = new MenuItem { Header = "重做" };
            MenuItem cutItem = new MenuItem { Header = "剪切" };
            MenuItem copyItem = new MenuItem { Header = "复制" };
            MenuItem pasteItem = new MenuItem { Header = "粘贴" };
            MenuItem selectAllItem = new MenuItem { Header = "全选" };

            cutItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Cut);
            copyItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Copy);
            pasteItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Paste);
            selectAllItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.SelectAll);

            return new[]
            {
                undoItem,
                redoItem,
                new MenuItem { Header = "-" },
                cutItem,
                copyItem,
                pasteItem,
                new MenuItem { Header = "-" },
                selectAllItem
            };
        }

        private IEnumerable<MenuItem> CreateToolMenuItems()
        {
            return new[]
            {
                new MenuItem { Header = "性能监控" },
                new MenuItem { Header = "帧分析" }
            };
        }

        private IEnumerable<MenuItem> CreateConfigMenuItems()
        {
            return new[]
            {
                new MenuItem { Header = "预览" },
                new MenuItem { Header = "性能" },
                new MenuItem { Header = "设置" }
            };
        }

        private IEnumerable<MenuItem> CreateAboutMenuItems()
        {
            return new[]
            {
                new MenuItem { Header = "关于Limitless Square" },
                new MenuItem { Header = "GitHub" }
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
                ItemsSource = new object[]
                {
                    CreateDirectoryNode(fullRootPath, true)
                }
            };

            treeView.Classes.Add("project-file-tree");

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
                Header = CreateCompactTreeHeader(GetDirectoryDisplayName(directoryPath, isRoot)),
                Tag = directoryPath,
                IsExpanded = isRoot,
                MinHeight = InspectorTextBoxHeight,
                Padding = new Thickness(0)
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
                Header = CreateCompactTreeHeader(Path.GetFileName(filePath)),
                Tag = filePath,
                MinHeight = InspectorTextBoxHeight,
                Padding = new Thickness(0)
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
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Child = new StackPanel
                {
                    Spacing = 4,
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

                Button backButton = (Button)CreateResourceBackButton();

                TextBlock pathText = new TextBlock
                {
                    Text = BuildRelativeResourceDirectoryDisplayPath(),
                    Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };

                Grid topBarContent = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,12,*"),
                    VerticalAlignment = VerticalAlignment.Center
                };

                Grid.SetColumn(backButton, 0);
                Grid.SetColumn(pathText, 2);

                topBarContent.Children.Add(backButton);
                topBarContent.Children.Add(pathText);

                Border topBar = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#181818")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(8, 4),
                    Child = topBarContent
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

        private string BuildRelativeResourceDirectoryDisplayPath()
        {
            if (string.IsNullOrWhiteSpace(_projectRootPath) ||
                string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                return string.Empty;

            string rootFullPath = Path.GetFullPath(_projectRootPath);
            string currentFullPath = Path.GetFullPath(_currentResourceDirectoryPath);

            if (PathsEqual(rootFullPath, currentFullPath))
                return string.Empty;

            string relativePath = Path.GetRelativePath(rootFullPath, currentFullPath);
            if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
                return string.Empty;

            string[] segments = relativePath
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

            return string.Join(" > ", segments);
        }

        private Button CreateResourceBackButton()
        {
            Button button = new Button
            {
                Content = "返回上一级",
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
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
            ShowResourceDetailsInViewer(state.Path, state.IsDirectory);
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

            state.Item.Background = Brushes.Transparent;
            state.Item.BorderBrush = Brushes.Transparent;
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

                _currentTreeCopyPath = openResult.TreeCopyPath;
                _currentPreviewScenePath = openResult.PreviewScenePath;
                _currentTreeScene = openResult.TreeScene;
                _currentPreviewScene = openResult.PreviewScene;
                _selectedSceneObject = null;

                LeftDockSlot.Content = BuildSceneTreeControl(openResult.TreeScene);

                EditorHostBridge.SetAssetRootAndReloadAssets(assetRootPath);
                EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);

                StartSceneOpenForceRefresh();

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
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

        private bool PersistSceneObjectChanges(SceneObject target, bool refreshSceneTree)
        {
            if (_currentTreeScene == null ||
                string.IsNullOrWhiteSpace(_currentTreeCopyPath) ||
                string.IsNullOrWhiteSpace(_currentPreviewScenePath))
                return false;

            SaveSceneData(_currentTreeCopyPath, _currentTreeScene);

            _currentPreviewScene = BuildPreviewScene(_currentTreeScene);
            SaveSceneData(_currentPreviewScenePath, _currentPreviewScene);

            EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);
            StartSceneOpenForceRefresh();

            if (refreshSceneTree)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (_currentTreeScene == null)
                        return;

                    _isProgrammaticSceneTreeSelection = true;
                    try
                    {
                        LeftDockSlot.Content = BuildSceneTreeControl(_currentTreeScene);
                        TrySelectSceneObjectInTree(target);
                        _selectedSceneObject = target;
                    }
                    finally
                    {
                        _isProgrammaticSceneTreeSelection = false;
                    }
                }, DispatcherPriority.Background);
            }

            return true;
        }

        private bool TrySelectSceneObjectInTree(SceneObject target)
        {
            if (LeftDockSlot.Content is not TreeView treeView)
                return false;

            if (treeView.ItemsSource is not System.Collections.IEnumerable items)
                return false;

            ClearSceneTreeSelection(items);
            return TrySelectSceneObjectInTree(items, target);
        }

        private void ClearSceneTreeSelection(System.Collections.IEnumerable items)
        {
            foreach (object? itemObject in items)
            {
                if (itemObject is not TreeViewItem item)
                    continue;

                item.IsSelected = false;

                if (item.ItemsSource is System.Collections.IEnumerable childItems)
                    ClearSceneTreeSelection(childItems);
            }
        }

        private bool TrySelectSceneObjectInTree(System.Collections.IEnumerable items, SceneObject target)
        {
            foreach (object? itemObject in items)
            {
                if (itemObject is not TreeViewItem item)
                    continue;

                if (ReferenceEquals(item.Tag, target))
                {
                    item.IsExpanded = true;
                    item.IsSelected = true;
                    item.BringIntoView();
                    return true;
                }

                if (item.ItemsSource is System.Collections.IEnumerable childItems &&
                    TrySelectSceneObjectInTree(childItems, target))
                {
                    item.IsExpanded = true;
                    return true;
                }
            }

            return false;
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

            TreeView treeView = new TreeView
            {
                ItemsSource = roots.Select(root => CreateSceneTreeItem(root, childrenMap)).Cast<object>().ToList()
            };

            treeView.Classes.Add("scene-tree");
            treeView.SelectionChanged += OnSceneTreeSelectionChanged;

            return treeView;
        }

        private TreeViewItem CreateSceneTreeItem(
            SceneObject obj,
            Dictionary<string, List<SceneObject>> childrenMap)
        {
            string nameText = string.IsNullOrWhiteSpace(obj.Name) ? string.Empty : obj.Name;
            string idText = string.IsNullOrWhiteSpace(obj.Id) ? "Unnamed" : obj.Id;
            string headerText = string.IsNullOrWhiteSpace(nameText)
                ? idText
                : $"{nameText} [{idText}]";

            TreeViewItem item = new TreeViewItem
            {
                Header = CreateCompactTreeHeader(headerText),
                Tag = obj,
                MinHeight = InspectorTextBoxHeight,
                Padding = new Thickness(0)
            };

            if (childrenMap.TryGetValue(obj.Id, out List<SceneObject>? children) && children.Count > 0)
                item.ItemsSource = children.Select(child => CreateSceneTreeItem(child, childrenMap)).Cast<object>().ToList();

            return item;
        }

        private void OnSceneTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_isProgrammaticSceneTreeSelection)
                return;

            try
            {
                if (sender is not TreeView treeView)
                    return;

                if (treeView.SelectedItem is not TreeViewItem item)
                    return;

                if (item.Tag is not SceneObject obj)
                    return;

                _selectedSceneObject = obj;
                ShowSceneObjectInspector(obj);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
                throw;
            }
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