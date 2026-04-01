using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System;

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

        public EditorMainWindow()
        {
            Title = "Limitless Square Editor";
            Width = 1280;
            Height = 720;
            MinWidth = 960;
            MinHeight = 540;
            Background = new SolidColorBrush(Color.Parse("#111111"));

            ToolbarSlot.VerticalAlignment = VerticalAlignment.Center;

            _sceneHost = new EmbeddedGameHost();
            _sceneHost.NativeControlCreated += OnSceneHostCreated;
            _sceneHost.HostPixelSizeChanged += OnSceneHostSizeChanged;
            _sceneHost.NativeControlDestroyed += OnSceneHostDestroyed;

            // 先这么搞
            LeftDockSlot.Content = CreatePlaceholder("这里边是一个树结构");
            RightDockSlot.Content = CreatePlaceholder("这里展示选中节点的属性");
            BottomDockSlot.Content = CreatePlaceholder("各种文件夹塞这里面");
            ToolbarSlot.Content = CreateToolbarPlaceholder();

            Content = BuildLayout();
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
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Limitless Square Editor (还没加功能看看得了)",
                            VerticalAlignment = VerticalAlignment.Center,
                            Foreground = Brushes.White,
                            FontSize = 13
                        },
                        ToolbarSlot
                    }
                }
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
            Control bottomPanel = CreateDockContainer("资源管理器", BottomDockSlot);

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

        private static Control CreateToolbarPlaceholder()
        {
            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center
            };

            panel.Children.Add(CreateToolbarButton("播放"));
            panel.Children.Add(CreateToolbarButton("暂停"));
            panel.Children.Add(CreateToolbarButton("步进"));
            return panel;
        }

        private static Control CreateToolbarButton(string text)
        {
            return new Button
            {
                Content = text,
                Height = 28,
                MinWidth = 64
            };
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