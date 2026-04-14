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

        private readonly PreviewOrientationGizmoOverlay _previewOrientationGizmo;

        public EmbeddedGameHost SceneHost => _sceneHost;
        public bool IsSceneHostNavigationActive => _isSceneHostRightDragging;

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

        private string? _currentSceneOriginalPath;
        private Border? _sceneTreeDockHeader;
        private TextBlock? _sceneTreeDockHeaderTitleText;
        private TextBlock? _sceneTreeDockHeaderDirtyMarkText;
        private MenuItem? _saveSceneMenuItem;
        private MenuItem? _undoSceneMenuItem;
        private MenuItem? _redoSceneMenuItem;
        private readonly List<SceneData> _sceneUndoStack = new List<SceneData>();
        private readonly List<SceneData> _sceneRedoStack = new List<SceneData>();
        private bool _isApplyingSceneHistory;

        private SceneObject? _selectedSceneObject;
        private bool _isUpdatingSceneInspector;
        private bool _isProgrammaticSceneTreeSelection;
        private TextBox? _activeInspectorTextBox;
        private string? _currentPreviewCameraId;
        private string? _previewCameraEditorId;
        private string? _previewCameraEditorRuntimeName;
        private Double3 _previewCameraEditorPosition = Double3.Zero;
        private Double3 _previewCameraEditorRotation = Double3.Zero;
        private double _previewCameraEditorViewYawDegrees;
        private double _previewCameraEditorViewPitchDegrees;
        private string _previewCameraEditorData = "";
        private readonly HashSet<Key> _sceneHostNavigationKeys = new HashSet<Key>();
        private DateTime _sceneHostNavigationLastTickUtc;
        private bool _isSceneHostRightDragging;
        private Point _sceneHostLastPointerPosition;
        private IPointer? _sceneHostCapturedPointer;
        private double _sceneHostMoveSpeedMultiplier = 1.0;

        private const string EditorPreviewSceneId = "__editor_preview_scene__";
        private const string EditorPreviewDirectoryName = "EditorPreview";
        private const string EditorTreeCopyDirectoryName = "EditorTreeCopies";
        private const string EditorPreviewSceneFileName = EditorPreviewSceneId + ".json";
        private const string PreviewCameraIdPrefix = "__lse_editor_preview_camera__";
        private const string PreviewCameraNamePrefix = "__lse_editor_preview_camera_name__";
        private const double SceneHostMoveSpeed = 6.0;
        private const double SceneHostLookSensitivity = 0.2;
        private const double SceneHostMoveSpeedMultiplierStepUp = 1.25;
        private const double SceneHostMoveSpeedMultiplierStepDown = 0.8;

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
            _sceneHost.PointerPressed += OnSceneHostPointerPressed;
            _sceneHost.PointerReleased += OnSceneHostPointerReleased;
            _sceneHost.PointerMoved += OnSceneHostPointerMoved;
            _sceneHost.PointerWheelChanged += OnSceneHostPointerWheelChanged;
            _sceneHost.PointerCaptureLost += OnSceneHostPointerCaptureLost;
            _sceneHost.LostFocus += OnSceneHostLostFocus;
            _sceneHost.KeyDown += OnSceneHostKeyDown;
            _sceneHost.KeyUp += OnSceneHostKeyUp;

            _previewOrientationGizmo = new PreviewOrientationGizmoOverlay
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };

            _previewOrientationGizmo.SetCameraStateProvider(() =>
            {
                if (!EditorHostBridge.IsRenderWindowAlive)
                    return null;

                if (string.IsNullOrWhiteSpace(_currentPreviewCameraId))
                    return null;

                Double3 right = GetPreviewCameraRightFromEditorState();
                Double3 up = GetPreviewCameraUpFromEditorState();
                Double3 forward = GetPreviewCameraForwardFromEditorState();

                return new PreviewOrientationGizmoOverlay.CameraOrientationState(right, up, forward);
            });

            Opened += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ForceSyncScenePreviewSurface();
                }, DispatcherPriority.Render);
            };

            SizeChanged += (_, _) =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    _sceneHost.NotifyHostLayoutChanged();
                    ForceSyncScenePreviewSurface();
                }, DispatcherPriority.Render);
            };

            Closed += (_, _) =>
            {
                ResetSceneHostNavigationState();
            };

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
            _previewOrientationGizmo.InvalidateVisual();
        }

        public void TickSceneHostNavigation()
        {
            DateTime now = DateTime.UtcNow;
            double deltaSeconds = (now - _sceneHostNavigationLastTickUtc).TotalSeconds;
            _sceneHostNavigationLastTickUtc = now;

            if (!_isSceneHostRightDragging)
                return;

            if (deltaSeconds <= 0.0)
                return;

            if (string.IsNullOrWhiteSpace(_currentPreviewCameraId))
                return;

            Double3 forward = GetPreviewCameraForwardFromEditorState();
            Double3 right = GetPreviewCameraRightFromEditorState();
            Double3 up = GetPreviewCameraUpFromEditorState();
            Double3 move = Double3.Zero;

            if (_sceneHostNavigationKeys.Contains(Key.W))
                move += forward;

            if (_sceneHostNavigationKeys.Contains(Key.S))
                move -= forward;

            if (_sceneHostNavigationKeys.Contains(Key.D))
                move += right;

            if (_sceneHostNavigationKeys.Contains(Key.A))
                move -= right;

            if (_sceneHostNavigationKeys.Contains(Key.Space))
                move += up;

            if (_sceneHostNavigationKeys.Contains(Key.LeftShift) ||
                _sceneHostNavigationKeys.Contains(Key.RightShift))
                move -= up;

            if (!TryNormalizeDouble3(move, out Double3 normalized))
                return;

            Double3 positionDelta = normalized * (SceneHostMoveSpeed * _sceneHostMoveSpeedMultiplier * deltaSeconds);
            _previewCameraEditorPosition += positionDelta;

            ApplyPreviewCameraEditorStateToRuntime();
        }

        private void OnSceneHostPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            PointerPoint point = e.GetCurrentPoint(_sceneHost);

            if (point.Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed)
            {
                if (!_isSceneHostRightDragging)
                    return;

                _sceneHostMoveSpeedMultiplier = 1.0;
                e.Handled = true;
                return;
            }

            if (point.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
                return;

            if (string.IsNullOrWhiteSpace(_currentPreviewCameraId))
                return;

            _sceneHost.Focus();
            _isSceneHostRightDragging = true;
            _sceneHostLastPointerPosition = point.Position;
            _sceneHostNavigationLastTickUtc = DateTime.UtcNow;
            _sceneHostCapturedPointer = e.Pointer;
            e.Pointer.Capture(_sceneHost);
            e.Handled = true;
        }

        private void OnSceneHostPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            PointerPoint point = e.GetCurrentPoint(_sceneHost);

            if (point.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
                return;

            ResetSceneHostNavigationState();
            e.Handled = true;
        }

        private void OnSceneHostPointerMoved(object? sender, PointerEventArgs e)
        {
            if (!_isSceneHostRightDragging)
                return;

            if (string.IsNullOrWhiteSpace(_currentPreviewCameraId))
                return;

            Point currentPosition = e.GetPosition(_sceneHost);
            Vector delta = currentPosition - _sceneHostLastPointerPosition;
            _sceneHostLastPointerPosition = currentPosition;

            double yawDelta = delta.X * SceneHostLookSensitivity;
            double pitchDelta = delta.Y * SceneHostLookSensitivity;
            bool changed = false;

            if (yawDelta != 0.0)
            {
                _previewCameraEditorViewYawDegrees += yawDelta;
                changed = true;
            }

            if (pitchDelta != 0.0)
            {
                _previewCameraEditorViewPitchDegrees = Math.Clamp(
                    _previewCameraEditorViewPitchDegrees + pitchDelta,
                    -89.0,
                    89.0);

                changed = true;
            }

            if (changed)
            {
                UpdatePreviewCameraEditorRotationFromViewAngles();
                ApplyPreviewCameraEditorStateToRuntime();
            }

            e.Handled = true;
        }

        private void OnSceneHostPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (!_isSceneHostRightDragging)
                return;

            if (e.Delta.Y > 0.0)
            {
                _sceneHostMoveSpeedMultiplier *= SceneHostMoveSpeedMultiplierStepUp;
            }
            else if (e.Delta.Y < 0.0)
            {
                _sceneHostMoveSpeedMultiplier *= SceneHostMoveSpeedMultiplierStepDown;
            }
            else
            {
                return;
            }

            e.Handled = true;
        }

        private void OnSceneHostPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            ResetSceneHostNavigationState();
        }

        private void OnSceneHostLostFocus(object? sender, RoutedEventArgs e)
        {
            ResetSceneHostNavigationState();
        }

        private void OnSceneHostKeyDown(object? sender, KeyEventArgs e)
        {
            if (!IsSceneHostNavigationKey(e.Key))
                return;

            _sceneHostNavigationKeys.Add(e.Key);

            if (_isSceneHostRightDragging)
                e.Handled = true;
        }

        private void OnSceneHostKeyUp(object? sender, KeyEventArgs e)
        {
            if (!IsSceneHostNavigationKey(e.Key))
                return;

            _sceneHostNavigationKeys.Remove(e.Key);

            if (_isSceneHostRightDragging)
                e.Handled = true;
        }

        private bool IsSceneHostNavigationKey(Key key)
        {
            return key == Key.W ||
                   key == Key.A ||
                   key == Key.S ||
                   key == Key.D ||
                   key == Key.Space ||
                   key == Key.LeftShift ||
                   key == Key.RightShift;
        }

        private void ResetSceneHostNavigationState()
        {
            _isSceneHostRightDragging = false;
            _sceneHostNavigationKeys.Clear();
            _sceneHostNavigationLastTickUtc = DateTime.UtcNow;

            if (_sceneHostCapturedPointer != null)
            {
                _sceneHostCapturedPointer.Capture(null);
                _sceneHostCapturedPointer = null;
            }
        }

        private bool TryNormalizeDouble3(Double3 value, out Double3 normalized)
        {
            double length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);

            if (length <= 1e-12)
            {
                normalized = Double3.Zero;
                return false;
            }

            normalized = value / length;
            return true;
        }

        private void RefreshCurrentPreviewCameraId()
        {
            _currentPreviewCameraId = _previewCameraEditorId;
        }

        private void InitializePreviewCameraEditorState(SceneData sourceScene)
        {
            SceneObject? sourceCamera = sourceScene.Objects.FirstOrDefault(
                o => string.Equals(o.Type, "Camera", StringComparison.Ordinal));

            _previewCameraEditorId = CreateUniquePreviewCameraId(sourceScene);
            _previewCameraEditorRuntimeName = CreateUniquePreviewCameraName(sourceScene);

            if (sourceCamera?.Transform != null)
            {
                _previewCameraEditorPosition = sourceCamera.Transform.LocalPosition;
                _previewCameraEditorViewPitchDegrees = sourceCamera.Transform.LocalRotation.X;
                _previewCameraEditorViewYawDegrees = sourceCamera.Transform.LocalRotation.Y;
            }
            else
            {
                _previewCameraEditorPosition = new Double3(0.0, 0.0, 5.0);
                _previewCameraEditorViewPitchDegrees = 0.0;
                _previewCameraEditorViewYawDegrees = 180.0;
            }

            UpdatePreviewCameraEditorRotationFromViewAngles();
            _previewCameraEditorData = BuildPreviewCameraData(sourceCamera?.Data, true);
        }

        private void EnsurePreviewCameraEditorState(SceneData sourceScene)
        {
            if (!string.IsNullOrWhiteSpace(_previewCameraEditorId) &&
                !string.IsNullOrWhiteSpace(_previewCameraEditorRuntimeName))
                return;

            InitializePreviewCameraEditorState(sourceScene);
        }

        private string CreateUniquePreviewCameraId(SceneData sourceScene)
        {
            HashSet<string> ids = new HashSet<string>(
                sourceScene.Objects
                    .Where(o => !string.IsNullOrWhiteSpace(o.Id))
                    .Select(o => o.Id),
                StringComparer.Ordinal);

            while (true)
            {
                string candidate = PreviewCameraIdPrefix + Guid.NewGuid().ToString("N");
                if (!ids.Contains(candidate))
                    return candidate;
            }
        }

        private string CreateUniquePreviewCameraName(SceneData sourceScene)
        {
            HashSet<string> names = new HashSet<string>(
                sourceScene.Objects
                    .Where(o => !string.IsNullOrWhiteSpace(o.Name))
                    .Select(o => o.Name!),
                StringComparer.Ordinal);

            while (true)
            {
                string candidate = PreviewCameraNamePrefix + Guid.NewGuid().ToString("N");
                if (!names.Contains(candidate))
                    return candidate;
            }
        }

        private SceneTransform CreatePreviewCameraTransform()
        {
            return new SceneTransform
            {
                ParentId = null,
                LocalPosition = _previewCameraEditorPosition,
                LocalRotation = _previewCameraEditorRotation,
                LocalScale = Double3.One
            };
        }

        private void ApplyPreviewCameraEditorStateToRuntime()
        {
            if (string.IsNullOrWhiteSpace(_currentPreviewCameraId))
                return;

            EditorHostBridge.SetSceneObjectLocalPosition(
                EditorPreviewSceneId,
                _currentPreviewCameraId,
                _previewCameraEditorPosition);

            EditorHostBridge.SetSceneObjectLocalRotation(
                EditorPreviewSceneId,
                _currentPreviewCameraId,
                _previewCameraEditorRotation);

            _previewOrientationGizmo.InvalidateVisual();
        }

        private Double3 GetPreviewCameraForwardFromEditorState()
        {
            double pitchRadians = _previewCameraEditorViewPitchDegrees * Math.PI / 180.0;
            double yawRadians = _previewCameraEditorViewYawDegrees * Math.PI / 180.0;

            Double3 forward = new Double3(
                Math.Sin(yawRadians) * Math.Cos(pitchRadians),
                -Math.Sin(pitchRadians),
                Math.Cos(yawRadians) * Math.Cos(pitchRadians));

            if (!TryNormalizeDouble3(forward, out Double3 normalized))
                return Double3.Zero;

            return normalized;
        }

        private Double3 GetPreviewCameraRightFromEditorState()
        {
            Double3 forward = GetPreviewCameraForwardFromEditorState();
            Double3 right = CrossDouble3(new Double3(0.0, 1.0, 0.0), forward);

            if (!TryNormalizeDouble3(right, out Double3 normalized))
                return Double3.Zero;

            return normalized;
        }

        private Double3 GetPreviewCameraUpFromEditorState()
        {
            Double3 forward = GetPreviewCameraForwardFromEditorState();
            Double3 right = GetPreviewCameraRightFromEditorState();
            Double3 up = CrossDouble3(forward, right);

            if (!TryNormalizeDouble3(up, out Double3 normalized))
                return Double3.Zero;

            return normalized;
        }

        private void UpdatePreviewCameraEditorRotationFromViewAngles()
        {
            double pitchRadians = _previewCameraEditorViewPitchDegrees * Math.PI / 180.0;
            double yawRadians = _previewCameraEditorViewYawDegrees * Math.PI / 180.0;

            double m13 = Math.Sin(yawRadians) * Math.Cos(pitchRadians);
            double m23 = -Math.Sin(pitchRadians);
            double m33 = Math.Cos(yawRadians) * Math.Cos(pitchRadians);
            double m12 = Math.Sin(yawRadians) * Math.Sin(pitchRadians);
            double m11 = Math.Cos(yawRadians);

            double engineY = Math.Asin(ClampUnit(m13));
            double engineX = Math.Atan2(-m23, m33);
            double engineZ = Math.Atan2(-m12, m11);

            _previewCameraEditorRotation = new Double3(
                engineX * 180.0 / Math.PI,
                engineY * 180.0 / Math.PI,
                engineZ * 180.0 / Math.PI);
        }

        private double ClampUnit(double value)
        {
            if (value < -1.0)
                return -1.0;

            if (value > 1.0)
                return 1.0;

            return value;
        }

        private Double3 CrossDouble3(Double3 left, Double3 right)
        {
            return new Double3(
                left.Y * right.Z - left.Z * right.Y,
                left.Z * right.X - left.X * right.Z,
                left.X * right.Y - left.Y * right.X);
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

            Control leftPanel = CreateSceneTreeDockContainer();
            Control scenePanel = CreateDockContainer("场景/画布", CreateSceneHostContent());
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

        private Control CreateSceneHostContent()
        {
            Grid overlayRoot = new Grid();

            Border hostBorder = new Border
            {
                Background = Brushes.Black,
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(0),
                Child = _sceneHost
            };

            overlayRoot.Children.Add(hostBorder);
            overlayRoot.Children.Add(_previewOrientationGizmo);

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#111111")),
                Padding = new Thickness(0),
                Child = overlayRoot
            };
        }

        private static Control CreateDockContainer(string title, Control content)
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
                    FontWeight = FontWeight.Bold
                }
            };

            Border body = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#111111")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(1),
                Child = content
            };

            Grid.SetRow(header, 0);
            Grid.SetRow(body, 1);

            grid.Children.Add(header);
            grid.Children.Add(body);

            return grid;
        }

        private Control CreateSceneTreeDockContainer()
        {
            Grid grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition(32, GridUnitType.Pixel));
            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

            _sceneTreeDockHeaderTitleText = new TextBlock
            {
                Text = "场景树/画布树",
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold
            };

            _sceneTreeDockHeaderDirtyMarkText = new TextBlock
            {
                Text = "",
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold
            };

            Grid headerContent = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto")
            };

            Grid.SetColumn(_sceneTreeDockHeaderTitleText, 0);
            Grid.SetColumn(_sceneTreeDockHeaderDirtyMarkText, 1);

            headerContent.Children.Add(_sceneTreeDockHeaderTitleText);
            headerContent.Children.Add(_sceneTreeDockHeaderDirtyMarkText);

            _sceneTreeDockHeader = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#222222")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 0),
                Child = headerContent
            };

            Border body = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#111111")),
                BorderBrush = new SolidColorBrush(Color.Parse("#444444")),
                BorderThickness = new Thickness(1),
                Child = LeftDockSlot
            };

            Grid.SetRow(_sceneTreeDockHeader, 0);
            Grid.SetRow(body, 1);

            grid.Children.Add(_sceneTreeDockHeader);
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
                string newValue = value ?? string.Empty;
                string oldValue = obj.Name ?? string.Empty;

                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Name = newValue;
                return PersistSceneObjectChanges(obj, true);
            }));

            root.Children.Add(CreateTextPropertyEditor("类型", () => obj.Type ?? "", value =>
            {
                string newValue = value ?? string.Empty;
                string oldValue = obj.Type ?? string.Empty;

                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Type = newValue;
                return PersistSceneObjectChanges(obj, true);
            }));

            root.Children.Add(CreateTextPropertyEditor("父节点", () => obj.Transform?.ParentId ?? "", value =>
            {
                return TryApplyParentId(obj, value);
            }));

            root.Children.Add(CreateInspectorSectionHeader("变换"));

            root.Children.Add(CreateVector3PropertyEditor(
                "位置",
                () => obj.Transform!.LocalPosition,
                value =>
                {
                    if (Double3Equals(obj.Transform!.LocalPosition, value))
                        return true;

                    BeginSceneParameterChange();
                    obj.Transform!.LocalPosition = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            root.Children.Add(CreateVector3PropertyEditor(
                "旋转",
                () => obj.Transform!.LocalRotation,
                value =>
                {
                    if (Double3Equals(obj.Transform!.LocalRotation, value))
                        return true;

                    BeginSceneParameterChange();
                    obj.Transform!.LocalRotation = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            root.Children.Add(CreateVector3PropertyEditor(
                "缩放",
                () => obj.Transform!.LocalScale,
                value =>
                {
                    if (Double3Equals(obj.Transform!.LocalScale, value))
                        return true;

                    BeginSceneParameterChange();
                    obj.Transform!.LocalScale = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            root.Children.Add(CreateInspectorSectionHeader("参数"));
            root.Children.Add(CreateTextPropertyEditor("Controller", () => obj.Controller ?? "", value =>
            {
                string? newValue = string.IsNullOrWhiteSpace(value) ? null : value;
                string? oldValue = obj.Controller;

                if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Controller = newValue;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Mesh", () => obj.Mesh ?? "", value =>
            {
                string? newValue = string.IsNullOrWhiteSpace(value) ? null : value;
                string? oldValue = obj.Mesh;

                if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Mesh = newValue;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("RenderTag", () => obj.RenderTag ?? "", value =>
            {
                string newValue = value ?? "";
                string oldValue = obj.RenderTag ?? "";

                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.RenderTag = newValue;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Tags", () => obj.Tags == null ? "" : string.Join(", ", obj.Tags), value =>
            {
                List<string> newValue = SplitCommaSeparatedList(value);
                string oldText = obj.Tags == null ? "" : string.Join(", ", obj.Tags);
                string newText = newValue.Count == 0 ? "" : string.Join(", ", newValue);

                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Tags = newValue;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Materials", () => obj.Materials == null ? "" : string.Join(", ", obj.Materials), value =>
            {
                List<string> list = SplitCommaSeparatedList(value);
                List<string>? newValue = list.Count == 0 ? null : list;
                string oldText = obj.Materials == null ? "" : string.Join(", ", obj.Materials);
                string newText = newValue == null ? "" : string.Join(", ", newValue);

                if (string.Equals(oldText, newText, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Materials = newValue;
                return PersistSceneObjectChanges(obj, false);
            }));
            root.Children.Add(CreateTextPropertyEditor("Data", () => obj.Data ?? "", value =>
            {
                string? newValue = string.IsNullOrWhiteSpace(value) ? null : value;
                string? oldValue = obj.Data;

                if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Data = newValue;
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

            if (e.Key == Key.S)
            {
                if (TrySaveCurrentSceneToOriginalFile())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Z)
            {
                if (TryHandleSceneUndoCommand())
                    e.Handled = true;
                return;
            }

            if (e.Key == Key.Y)
            {
                if (TryHandleSceneRedoCommand())
                    e.Handled = true;
                return;
            }

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
                if (obj.Active == value)
                    return true;

                BeginSceneParameterChange();
                obj.Active = value;
                return PersistSceneObjectChanges(obj, false);
            });

            Control visibleToggle = CreateInlineBoolToggle("V", () => obj.Visible, value =>
            {
                if (obj.Visible == value)
                    return true;

                BeginSceneParameterChange();
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

            TextBox xBox = CreateInspectorTextBox(FormatDouble(current.X), new SolidColorBrush(Color.Parse("#301010")));
            TextBox yBox = CreateInspectorTextBox(FormatDouble(current.Y), new SolidColorBrush(Color.Parse("#103010")));
            TextBox zBox = CreateInspectorTextBox(FormatDouble(current.Z), new SolidColorBrush(Color.Parse("#101030")));

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

            Border xTag = CreateAxisTag("X", "#551111");
            Border yTag = CreateAxisTag("Y", "#115511");
            Border zTag = CreateAxisTag("Z", "#111155");

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

        private bool Double3Equals(Double3 left, Double3 right)
        {
            return left.X == right.X &&
                   left.Y == right.Y &&
                   left.Z == right.Z;
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

            BeginSceneParameterChange();

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

            string? oldParentId = target.Transform.ParentId;
            if (string.Equals(oldParentId, parentId, StringComparison.Ordinal))
                return true;

            BeginSceneParameterChange();
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
            _saveSceneMenuItem = new MenuItem { Header = "保存", IsEnabled = false };
            MenuItem exitItem = new MenuItem { Header = "退出" };

            openItem.Click += async (_, _) => await OpenProjectFolderAsync();
            _saveSceneMenuItem.Click += (_, _) => TrySaveCurrentSceneToOriginalFile();
            exitItem.Click += (_, _) => Close();

            return new[]
            {
                newItem,
                openItem,
                _saveSceneMenuItem,
                new MenuItem { Header = "-" },
                exitItem
            };
        }

        private IEnumerable<MenuItem> CreateEditMenuItems()
        {
            _undoSceneMenuItem = new MenuItem { Header = "撤销", IsEnabled = false };
            _redoSceneMenuItem = new MenuItem { Header = "重做", IsEnabled = false };
            MenuItem cutItem = new MenuItem { Header = "剪切" };
            MenuItem copyItem = new MenuItem { Header = "复制" };
            MenuItem pasteItem = new MenuItem { Header = "粘贴" };
            MenuItem selectAllItem = new MenuItem { Header = "全选" };

            _undoSceneMenuItem.Click += (_, _) => TryHandleSceneUndoCommand();
            _redoSceneMenuItem.Click += (_, _) => TryHandleSceneRedoCommand();
            cutItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Cut);
            copyItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Copy);
            pasteItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.Paste);
            selectAllItem.Click += async (_, _) => await TryExecuteClipboardCommandAsync(ClipboardCommand.SelectAll);

            return new[]
            {
                _undoSceneMenuItem,
                _redoSceneMenuItem,
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
                state.Item.Background = new SolidColorBrush(Color.Parse("#113355"));
                state.Item.BorderBrush = new SolidColorBrush(Color.Parse("#225588"));
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

                _currentSceneOriginalPath = Path.GetFullPath(filePath);
                _currentTreeCopyPath = openResult.TreeCopyPath;
                _currentPreviewScenePath = openResult.PreviewScenePath;
                _currentTreeScene = openResult.TreeScene;
                _selectedSceneObject = null;
                _sceneUndoStack.Clear();
                _sceneRedoStack.Clear();
                _isApplyingSceneHistory = false;

                InitializePreviewCameraEditorState(openResult.TreeScene);
                _currentPreviewScene = BuildPreviewScene(openResult.TreeScene);
                SaveSceneData(_currentPreviewScenePath, _currentPreviewScene);

                RefreshCurrentPreviewCameraId();
                ResetSceneHostNavigationState();

                LeftDockSlot.Content = BuildSceneTreeControl(openResult.TreeScene);

                UpdateSceneDirtyVisualState();

                EditorHostBridge.SetAssetRootAndReloadAssets(assetRootPath);
                EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);
                ApplyPreviewCameraEditorStateToRuntime();

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
            SceneData previewScene = new SceneData
            {
                SceneId = EditorPreviewSceneId,
                Objects = new List<SceneObject>()
            };

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

        private void UpdateSceneDirtyVisualState()
        {
            bool isDirty = IsCurrentSceneTreeDirty();

            if (_sceneTreeDockHeaderDirtyMarkText != null)
                _sceneTreeDockHeaderDirtyMarkText.Text = isDirty ? "*" : string.Empty;

            if (_saveSceneMenuItem != null)
                _saveSceneMenuItem.IsEnabled = isDirty;

            if (_undoSceneMenuItem != null)
                _undoSceneMenuItem.IsEnabled = _sceneUndoStack.Count > 0;

            if (_redoSceneMenuItem != null)
                _redoSceneMenuItem.IsEnabled = _sceneRedoStack.Count > 0;
        }

        private bool IsCurrentSceneTreeDirty()
        {
            if (string.IsNullOrWhiteSpace(_currentSceneOriginalPath) ||
                string.IsNullOrWhiteSpace(_currentTreeCopyPath) ||
                !File.Exists(_currentSceneOriginalPath) ||
                !File.Exists(_currentTreeCopyPath))
                return false;

            return !FilesContentEqual(_currentSceneOriginalPath, _currentTreeCopyPath);
        }

        private bool FilesContentEqual(string leftPath, string rightPath)
        {
            byte[] leftBytes = File.ReadAllBytes(leftPath);
            byte[] rightBytes = File.ReadAllBytes(rightPath);

            if (leftBytes.Length != rightBytes.Length)
                return false;

            for (int i = 0; i < leftBytes.Length; i++)
            {
                if (leftBytes[i] != rightBytes[i])
                    return false;
            }

            return true;
        }

        private bool TrySaveCurrentSceneToOriginalFile()
        {
            if (string.IsNullOrWhiteSpace(_currentSceneOriginalPath) ||
                string.IsNullOrWhiteSpace(_currentTreeCopyPath) ||
                !File.Exists(_currentTreeCopyPath))
                return false;

            if (!IsCurrentSceneTreeDirty())
            {
                UpdateSceneDirtyVisualState();
                return false;
            }

            File.Copy(_currentTreeCopyPath, _currentSceneOriginalPath, true);
            UpdateSceneDirtyVisualState();
            return true;
        }

        private bool TryHandleSceneUndoCommand()
        {
            bool result = TryUndoSceneChange();
            UpdateSceneDirtyVisualState();
            return result;
        }

        private bool TryHandleSceneRedoCommand()
        {
            bool result = TryRedoSceneChange();
            UpdateSceneDirtyVisualState();
            return result;
        }

        private bool PersistSceneObjectChanges(SceneObject target, bool refreshSceneTree)
        {
            if (_currentTreeScene == null ||
                string.IsNullOrWhiteSpace(_currentTreeCopyPath) ||
                string.IsNullOrWhiteSpace(_currentPreviewScenePath))
                return false;

            SaveSceneData(_currentTreeCopyPath, _currentTreeScene);

            _currentPreviewScene = BuildPreviewScene(_currentTreeScene);

            RefreshCurrentPreviewCameraId();
            SaveSceneData(_currentPreviewScenePath, _currentPreviewScene);

            EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);
            ApplyPreviewCameraEditorStateToRuntime();
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

                    UpdateSceneDirtyVisualState();
                }, DispatcherPriority.Background);
            }
            else
            {
                UpdateSceneDirtyVisualState();
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
            EnsurePreviewCameraEditorState(sourceScene);

            SceneData previewScene = CloneSceneData(sourceScene);
            previewScene.SceneId = EditorPreviewSceneId;

            foreach (SceneObject obj in previewScene.Objects)
            {
                obj.Physics = null;

                if (string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                {
                    obj.Active = false;
                    obj.Visible = false;
                    obj.Data = BuildPreviewCameraData(obj.Data, false);
                }
            }

            SceneObject previewCamera = CreatePreviewCamera();
            previewScene.Objects.Add(previewCamera);

            return previewScene;
        }

        private SceneObject CreatePreviewCamera()
        {
            return new SceneObject
            {
                Id = _previewCameraEditorId ?? (PreviewCameraIdPrefix + Guid.NewGuid().ToString("N")),
                Name = _previewCameraEditorRuntimeName ?? (PreviewCameraNamePrefix + Guid.NewGuid().ToString("N")),
                Tags = new List<string>(),
                Active = true,
                Transform = CreatePreviewCameraTransform(),
                Type = "Camera",
                Controller = null,
                Data = _previewCameraEditorData,
                Mesh = null,
                Visible = true,
                RenderTag = "",
                Physics = null,
                Materials = null
            };
        }

        private string BuildPreviewCameraData(string? sourceData, bool isMainCamera)
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
                isMainCamera
            });
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

        private void PushUndoSceneState()
        {
            if (_currentTreeScene == null)
                return;

            _sceneUndoStack.Add(CloneSceneData(_currentTreeScene));
            _undoSceneMenuItem?.SetCurrentValue(MenuItem.IsEnabledProperty, true);
        }

        private void ClearRedoSceneState()
        {
            _sceneRedoStack.Clear();
            _redoSceneMenuItem?.SetCurrentValue(MenuItem.IsEnabledProperty, false);
        }

        private void BeginSceneParameterChange()
        {
            if (_currentTreeScene == null)
                return;

            if (_isApplyingSceneHistory)
                return;

            PushUndoSceneState();
            ClearRedoSceneState();
        }

        private bool TryUndoSceneChange()
        {
            if (_currentTreeScene == null || _sceneUndoStack.Count == 0)
                return false;

            _sceneRedoStack.Add(CloneSceneData(_currentTreeScene));

            SceneData previous = _sceneUndoStack[_sceneUndoStack.Count - 1];
            _sceneUndoStack.RemoveAt(_sceneUndoStack.Count - 1);

            ApplySceneState(previous);
            return true;
        }

        private bool TryRedoSceneChange()
        {
            if (_currentTreeScene == null || _sceneRedoStack.Count == 0)
                return false;

            _sceneUndoStack.Add(CloneSceneData(_currentTreeScene));

            SceneData next = _sceneRedoStack[_sceneRedoStack.Count - 1];
            _sceneRedoStack.RemoveAt(_sceneRedoStack.Count - 1);

            ApplySceneState(next);
            return true;
        }

        private void ApplySceneState(SceneData sceneState)
        {
            if (string.IsNullOrWhiteSpace(_currentTreeCopyPath) ||
                string.IsNullOrWhiteSpace(_currentPreviewScenePath))
                return;

            _isApplyingSceneHistory = true;
            try
            {
                _currentTreeScene = CloneSceneData(sceneState);
                SaveSceneData(_currentTreeCopyPath, _currentTreeScene);

                _currentPreviewScene = BuildPreviewScene(_currentTreeScene);

                RefreshCurrentPreviewCameraId();
                SaveSceneData(_currentPreviewScenePath, _currentPreviewScene);

                _selectedSceneObject = null;
                LeftDockSlot.Content = BuildSceneTreeControl(_currentTreeScene);
                RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");

                EditorHostBridge.ReloadSceneById(EditorPreviewSceneId);
                ApplyPreviewCameraEditorStateToRuntime();
                StartSceneOpenForceRefresh();
            }
            finally
            {
                _isApplyingSceneHistory = false;
                UpdateSceneDirtyVisualState();
            }
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

    public sealed class PreviewOrientationGizmoOverlay : Control
    {
        public readonly record struct CameraOrientationState(Double3 Right, Double3 Up, Double3 Forward);

        private Func<CameraOrientationState?>? _cameraStateProvider;

        private static readonly IBrush XBrush = new SolidColorBrush(Color.Parse("#FF0000"));
        private static readonly IBrush YBrush = new SolidColorBrush(Color.Parse("#00FF00"));
        private static readonly IBrush ZBrush = new SolidColorBrush(Color.Parse("#0000FF"));
        private static readonly IBrush WhiteBrush = Brushes.White;

        private static readonly Pen XPen = new Pen(XBrush, 2);
        private static readonly Pen YPen = new Pen(YBrush, 2);
        private static readonly Pen ZPen = new Pen(ZBrush, 2);

        public double FovDegrees { get; set; } = 70.0;

        public void SetCameraStateProvider(Func<CameraOrientationState?> provider)
        {
            _cameraStateProvider = provider;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            CameraOrientationState? state = _cameraStateProvider?.Invoke();
            if (state == null)
                return;

            Size size = Bounds.Size;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            Point center = new Point(size.Width - 56, 56);
            double centerRadius = 6.0;
            double axisBaseOffset = centerRadius;
            double axisLength = 28.0;
            double coneLength = 10.0;
            double coneBaseRadius = 4.5;

            GizmoAxis xAxis = BuildAxis(
                center,
                new Double3(1.0, 0.0, 0.0),
                state.Value,
                axisBaseOffset,
                axisLength,
                coneLength,
                coneBaseRadius,
                XBrush,
                XPen,
                FovDegrees);

            GizmoAxis yAxis = BuildAxis(
                center,
                new Double3(0.0, 1.0, 0.0),
                state.Value,
                axisBaseOffset,
                axisLength,
                coneLength,
                coneBaseRadius,
                YBrush,
                YPen,
                FovDegrees);

            GizmoAxis zAxis = BuildAxis(
                center,
                new Double3(0.0, 0.0, 1.0),
                state.Value,
                axisBaseOffset,
                axisLength,
                coneLength,
                coneBaseRadius,
                ZBrush,
                ZPen,
                FovDegrees);

            List<GizmoAxis> frontAxes = new List<GizmoAxis>();
            List<GizmoAxis> backAxes = new List<GizmoAxis>();

            foreach (GizmoAxis axis in new[] { xAxis, yAxis, zAxis })
            {
                if (axis.Depth >= 0.0)
                    backAxes.Add(axis);
                else
                    frontAxes.Add(axis);
            }

            frontAxes.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            backAxes.Sort((a, b) => a.Depth.CompareTo(b.Depth));

            foreach (GizmoAxis axis in backAxes)
                DrawAxis(context, axis);

            context.DrawEllipse(WhiteBrush, null, center, centerRadius, centerRadius);

            foreach (GizmoAxis axis in frontAxes)
                DrawAxis(context, axis);
        }

        private static GizmoAxis BuildAxis(
    Point center,
    Double3 worldAxis,
    CameraOrientationState camera,
    double axisBaseOffset,
    double axisLength,
    double coneLength,
    double coneBaseRadius,
    IBrush brush,
    Pen pen,
    double fovDegrees)
        {
            Double3 axis = Normalize3(worldAxis);

            BuildPerpendicularBasis(axis, out Double3 side, out Double3 upOnPlane);

            Double3 lineStart3 = axis * axisBaseOffset;
            Double3 lineEnd3 = axis * (axisBaseOffset + axisLength);
            Double3 coneTip3 = axis * (axisBaseOffset + axisLength + coneLength);
            Double3 coneBaseCenter3 = lineEnd3;

            ProjectedPoint lineStart = ProjectToScreen(center, lineStart3, camera, fovDegrees);
            ProjectedPoint lineEnd = ProjectToScreen(center, lineEnd3, camera, fovDegrees);
            ProjectedPoint coneTip = ProjectToScreen(center, coneTip3, camera, fovDegrees);
            ProjectedPoint coneBaseCenter = ProjectToScreen(center, coneBaseCenter3, camera, fovDegrees);

            Double3 majorAxis3 = Cross(camera.Forward, axis);
            if (majorAxis3.X * majorAxis3.X + majorAxis3.Y * majorAxis3.Y + majorAxis3.Z * majorAxis3.Z <= 1e-8)
                majorAxis3 = side;
            else
                majorAxis3 = Normalize3(majorAxis3);

            Vector majorAxis2D = new Vector(
                Dot(majorAxis3, camera.Right),
                -Dot(majorAxis3, camera.Up));

            double majorAxis2DLength = Math.Sqrt(
                majorAxis2D.X * majorAxis2D.X +
                majorAxis2D.Y * majorAxis2D.Y);

            if (majorAxis2DLength <= 1e-8)
                majorAxis2D = new Vector(1.0, 0.0);
            else
                majorAxis2D /= majorAxis2DLength;

            double coneBaseScale = coneBaseCenter.Scale;
            Point ellipseAxisA = coneBaseCenter.Position + majorAxis2D * (coneBaseRadius * coneBaseScale);
            Point ellipseAxisB = coneBaseCenter.Position - majorAxis2D * (coneBaseRadius * coneBaseScale);

            ProjectedPoint ellipseAxisCPoint = ProjectToScreen(center, coneBaseCenter3 + upOnPlane * coneBaseRadius, camera, fovDegrees);
            ProjectedPoint ellipseAxisDPoint = ProjectToScreen(center, coneBaseCenter3 - upOnPlane * coneBaseRadius, camera, fovDegrees);

            double facing = Math.Abs(Dot(axis, camera.Forward));
            facing = Math.Clamp(facing, 0.0, 1.0);

            double ellipseRadiusX = coneBaseRadius * coneBaseScale;
            double ellipseRadiusY = ellipseRadiusX * facing;
            double ellipseRotation = Math.Atan2(majorAxis2D.Y, majorAxis2D.X);

            StreamGeometry triangle = new StreamGeometry();
            using (StreamGeometryContext gc = triangle.Open())
            {
                gc.BeginFigure(coneTip.Position, true);
                gc.LineTo(ellipseAxisA);
                gc.LineTo(ellipseAxisB);
                gc.EndFigure(true);
            }

            double depth = Dot(axis, camera.Forward);

            return new GizmoAxis(
                brush,
                pen,
                lineStart.Position,
                lineEnd.Position,
                coneBaseCenter.Position,
                coneTip.Position,
                triangle,
                ellipseRadiusX,
                ellipseRadiusY,
                ellipseRotation,
                depth);
        }

        private static void DrawAxis(DrawingContext context, GizmoAxis axis)
        {
            context.DrawLine(axis.Pen, axis.LineStart, axis.LineEnd);
            context.DrawGeometry(axis.Brush, null, axis.TriangleGeometry);

            if (axis.EllipseRadiusY > 0.01)
            {
                Matrix transform =
                    Matrix.CreateTranslation(-axis.ConeBaseCenter.X, -axis.ConeBaseCenter.Y) *
                    Matrix.CreateRotation(axis.EllipseRotation) *
                    Matrix.CreateTranslation(axis.ConeBaseCenter.X, axis.ConeBaseCenter.Y);

                using (context.PushTransform(transform))
                {
                    context.DrawEllipse(
                        axis.Brush,
                        null,
                        axis.ConeBaseCenter,
                        axis.EllipseRadiusX,
                        axis.EllipseRadiusY);
                }
            }
            else
            {
                Vector normal = new Vector(Math.Cos(axis.EllipseRotation), Math.Sin(axis.EllipseRotation));
                Point a = Add(axis.ConeBaseCenter, normal * axis.EllipseRadiusX);
                Point b = Add(axis.ConeBaseCenter, normal * -axis.EllipseRadiusX);
                context.DrawLine(new Pen(axis.Brush, 1), a, b);
            }
        }

        private static double Dot(Double3 a, Double3 b)
        {
            return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        }

        private readonly record struct ProjectedPoint(Point Position, double Scale);

        private static ProjectedPoint ProjectToScreen(
            Point center,
            Double3 point,
            CameraOrientationState camera,
            double fovDegrees)
        {
            double x = Dot(point, camera.Right);
            double y = -Dot(point, camera.Up);
            double z = Dot(point, camera.Forward);

            if (Math.Abs(fovDegrees) <= 1e-8)
                return new ProjectedPoint(new Point(center.X + x, center.Y + y), 1.0);

            double fovRadians = fovDegrees * Math.PI / 180.0;
            double focalLength = 56.0 / Math.Tan(fovRadians * 0.5);
            double perspectiveDistance = focalLength + 56.0;
            double scale = perspectiveDistance / (perspectiveDistance + z);
            scale = Math.Clamp(scale, 0.7, 1.35);

            return new ProjectedPoint(
                new Point(center.X + x * scale, center.Y + y * scale),
                scale);
        }

        private static void BuildPerpendicularBasis(
            Double3 axis,
            out Double3 side,
            out Double3 upOnPlane)
        {
            Double3 helper =
                Math.Abs(axis.Y) < 0.9
                ? new Double3(0.0, 1.0, 0.0)
                : new Double3(1.0, 0.0, 0.0);

            side = Cross(axis, helper);
            side = Normalize3(side);

            upOnPlane = Cross(axis, side);
            upOnPlane = Normalize3(upOnPlane);
        }

        private static Double3 Normalize3(Double3 value)
        {
            double length = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z);
            if (length <= 1e-8)
                return Double3.Zero;

            return value / length;
        }

        private static Double3 Cross(Double3 a, Double3 b)
        {
            return new Double3(
                a.Y * b.Z - a.Z * b.Y,
                a.Z * b.X - a.X * b.Z,
                a.X * b.Y - a.Y * b.X);
        }

        private static Point Add(Point point, Vector vector)
        {
            return new Point(point.X + vector.X, point.Y + vector.Y);
        }

        private readonly record struct Vector2(double X, double Y)
        {
            public double Length => Math.Sqrt(X * X + Y * Y);

            public static Vector operator *(Vector2 value, double scalar)
            {
                return new Vector(value.X * scalar, value.Y * scalar);
            }

            public static Vector operator *(double scalar, Vector2 value)
            {
                return new Vector(value.X * scalar, value.Y * scalar);
            }
        }

        private readonly record struct GizmoAxis(
            IBrush Brush,
            Pen Pen,
            Point LineStart,
            Point LineEnd,
            Point ConeBaseCenter,
            Point ConeTip,
            StreamGeometry TriangleGeometry,
            double EllipseRadiusX,
            double EllipseRadiusY,
            double EllipseRotation,
            double Depth);
    }
}