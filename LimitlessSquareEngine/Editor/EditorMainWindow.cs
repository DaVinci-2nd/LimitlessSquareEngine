using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        private Border? _resourceExplorerRootBorder;
        private WrapPanel? _resourceExplorerPanel;
        private DateTime _lastResourceNameClickUtc = DateTime.MinValue;
        private string? _lastResourceNameClickPath;
        private TextBox? _activeResourceRenameTextBox;
        private string? _projectRootPath;
        private string? _currentResourceDirectoryPath;
        private string? _projectAssetRootPath;
        private DispatcherTimer? _sceneOpenForceRefreshTimer;
        private int _sceneOpenForceRefreshRemainingTicks;
        private string? _currentTreeCopyPath;
        private string? _currentPreviewScenePath;
        private SceneData? _currentTreeScene;
        private SceneData? _currentPreviewScene;
        private string? _currentPreviewContouredObjectId;

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
        private TreeView? _sceneTreeView;
        private SceneTreeDragOverlay? _sceneTreeDragOverlay;
        private SceneObject? _sceneTreeDragSourceObject;
        private TextBox? _sceneTreeSearchTextBox;
        private Button? _sceneTreeAddButton;
        private Button? _sceneTreeDeleteButton;

        private Point? _sceneTreeDragStartPoint;
        private IPointer? _sceneTreeCapturedPointer;
        private bool _isSceneTreeDragging;
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

        private Button? _playButton;
        private Button? _pauseButton;
        private Button? _stepButton;
        private Window? _playbackWindow;
        private bool _isPlaybackRunning;
        private bool _isPlaybackPaused;
        private EditorEmbeddingMode _playbackEmbeddingMode = EditorEmbeddingMode.Unsupported;
        private nint _playbackWindowNativeHandle;
        private Process? _playbackProcess;
        private string? _playbackControlFilePath;
        private string? _playbackStatusFilePath;
        private WindowState _playbackWindowState = WindowState.Normal;
        private DateTime _playbackSuppressFocusUntilUtc = DateTime.MinValue;
        private DispatcherTimer? _playbackDeferredFocusTimer;
        private DispatcherTimer? _playbackTitlePollTimer;

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

        private sealed class CreateProjectDialogResult
        {
            public bool Confirmed { get; init; }
            public string ProjectName { get; init; } = "";
            public string RootDirectoryPath { get; init; } = "";
            public string TemplateName { get; init; } = "基础模板";
            public bool CreatedSuccessfully { get; init; }
        }

        private sealed class CreateProjectValidationResult
        {
            public bool IsValid { get; init; }
            public string Message { get; init; } = "";
        }

        private sealed class PlaybackRenderStatus
        {
            public int Width { get; init; }
            public int Height { get; init; }
            public int Fps { get; init; }
            public bool GpuTimeAvailable { get; init; }
            public double GpuFrameMilliseconds { get; init; }
            public int DrawCalls { get; init; }
            public long DrawnVertices { get; init; }
            public long DrawnTriangles { get; init; }
            public int CulledCommands { get; init; }
            public int SubmittedCommands { get; init; }
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
                StopPlayback();
                ResetSceneHostNavigationState();
            };

            LeftDockSlot.Content = CreatePlaceholder("未加载场景或画布");
            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");
            ProjectFilesSlot.Content = CreatePlaceholder("未选择项目文件夹");
            BottomDockSlot.Content = CreatePlaceholder("未选择文件夹");
            ToolbarSlot.Content = CreateTopMenuBar();

            Content = BuildLayout();

            AddHandler(InputElement.KeyDownEvent, OnWindowClipboardKeyDown, RoutingStrategies.Tunnel, true);
            AddHandler(InputElement.KeyDownEvent, OnWindowResourceExplorerKeyDown, RoutingStrategies.Tunnel, true);
        }

        private sealed class ResourceItemState
        {
            public Border Item { get; }
            public string Path { get; }
            public bool IsDirectory { get; }
            public bool IsPointerOver { get; set; }
            public bool IsPressed { get; set; }
            public TextBlock NameTextBlock { get; }
            public TextBox NameTextBox { get; }

            public ResourceItemState(Border item, string path, bool isDirectory, TextBlock nameTextBlock, TextBox nameTextBox)
            {
                Item = item;
                Path = path;
                IsDirectory = isDirectory;
                NameTextBlock = nameTextBlock;
                NameTextBox = nameTextBox;
            }
        }

        private sealed class SceneTreeDragOverlay : Control
        {
            private Rect? _highlightRect;

            public SceneTreeDragOverlay()
            {
                IsHitTestVisible = false;
            }

            public void ShowHighlight(Rect rect)
            {
                _highlightRect = rect;
                InvalidateVisual();
            }

            public void HideHighlight()
            {
                if (_highlightRect == null)
                    return;

                _highlightRect = null;
                InvalidateVisual();
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                if (_highlightRect == null)
                    return;

                SolidColorBrush fillBrush = new SolidColorBrush(Color.FromArgb(72, 255, 255, 255));
                SolidColorBrush borderBrush = new SolidColorBrush(Color.FromArgb(144, 255, 255, 255));
                Rect rect = _highlightRect.Value;

                context.DrawRectangle(fillBrush, new Pen(borderBrush, 1), rect, 3, 3);
            }
        }

        private enum SceneTreeDropPlacement
        {
            Before,
            Child,
            After
        }

        private enum ClipboardCommand
        {
            Cut,
            Copy,
            Paste,
            SelectAll
        }

        private enum PlaybackButtonGlyph
        {
            PlayTriangle,
            StopSquare,
            PauseBars,
            StepCircle
        }

        private enum ResourceIconKind
        {
            Folder,
            GenericFile,
            LuaFile,
            JsonFile,
            SceneJsonFile,
            MaterialJsonFile,
            ImageFile,
            FragFile,
            VertFile,
            AudioFile
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

        private void EnsurePlaybackWindow()
        {
            if (_playbackWindow != null)
                return;

            _playbackEmbeddingMode = ResolvePlaybackEmbeddingMode();

            _playbackWindow = new Window
            {
                Title = BuildPlaybackWindowTitle(960, 540, 0),
                Width = 960,
                Height = 540,
                MinWidth = 320,
                MinHeight = 180,
                Background = Brushes.Black,
                Content = null,
                ShowInTaskbar = false,
                WindowDecorations = WindowDecorations.Full,
                CanResize = true
            };

            _playbackWindow.Opened += (_, _) =>
            {
                _playbackWindowState = _playbackWindow?.WindowState ?? WindowState.Normal;

                PixelSize size = GetPlaybackWindowPixelSize();
                if (size.Width > 0 && size.Height > 0)
                    UpdatePlaybackWindowTitle(size.Width, size.Height, 0);

                if (!TryGetPlaybackWindowNativeHandle(out nint handle))
                    return;

                _playbackWindowNativeHandle = handle;
            };

            _playbackWindow.Activated += (_, _) =>
            {
                if (!_isPlaybackRunning || _playbackWindow == null)
                    return;

                if (_playbackWindow.WindowState == WindowState.Minimized)
                    return;

                RequestDeferredPlaybackFocus();
            };

            _playbackWindow.PositionChanged += (_, _) =>
            {
                if (!_isPlaybackRunning)
                    return;

                SuppressPlaybackFocusFor(800);
                _playbackDeferredFocusTimer?.Stop();
            };

            _playbackWindow.PropertyChanged += (_, e) =>
            {
                if (e.Property != Window.WindowStateProperty || _playbackWindow == null)
                    return;

                WindowState state = _playbackWindow.WindowState;
                WindowState previousState = _playbackWindowState;
                _playbackWindowState = state;

                if (!_isPlaybackRunning)
                    return;

                if (state == WindowState.Minimized)
                {
                    SuppressPlaybackFocusFor(600);
                    QueuePlaybackCommand("resize 1 1");
                    return;
                }

                if (previousState == WindowState.Minimized)
                {
                    SuppressPlaybackFocusFor(220);

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!_isPlaybackRunning || _playbackWindow == null)
                            return;

                        if (_playbackWindow.WindowState == WindowState.Minimized)
                            return;

                        PixelSize size = GetPlaybackWindowPixelSize();
                        if (size.Width <= 0 || size.Height <= 0)
                            return;

                        QueuePlaybackCommand($"resize {size.Width} {size.Height}");
                        RequestDeferredPlaybackFocus(260);
                    }, DispatcherPriority.Background);
                }
            };

            _playbackWindow.SizeChanged += (_, _) =>
            {
                if (_playbackWindow == null)
                    return;

                if (_playbackWindow.WindowState == WindowState.Minimized)
                    return;

                PixelSize size = GetPlaybackWindowPixelSize();
                if (size.Width <= 0 || size.Height <= 0)
                    return;

                UpdatePlaybackWindowTitle(size.Width, size.Height, 0);

                if (!_isPlaybackRunning)
                    return;

                QueuePlaybackCommand($"resize {size.Width} {size.Height}");

                if (DateTime.UtcNow >= _playbackSuppressFocusUntilUtc)
                    RequestDeferredPlaybackFocus(140);
            };

            _playbackWindow.Closed += (_, _) =>
            {
                _playbackDeferredFocusTimer?.Stop();
                StopPlaybackTitlePolling();

                if (_isPlaybackRunning)
                    StopPlayback();

                _playbackWindow = null;
                _playbackWindowNativeHandle = 0;
            };
        }

        private EditorEmbeddingMode ResolvePlaybackEmbeddingMode()
        {
            EditorHostBootstrapInfo info = EditorHostBridge.GetBootstrapInfo();

            if (info.EmbeddingMode == EditorEmbeddingMode.ForeignChildWindow)
            {
                if (info.Win32Hwnd != 0)
                    return EditorEmbeddingMode.ForeignChildWindow;

                if (info.X11Window != 0 && info.X11Display != 0)
                    return EditorEmbeddingMode.ForeignChildWindow;
            }

            if (info.EmbeddingMode == EditorEmbeddingMode.CocoaViewHost && info.CocoaContentView != 0)
                return EditorEmbeddingMode.CocoaViewHost;

            return EditorEmbeddingMode.Unsupported;
        }

        private bool TryGetPlaybackWindowNativeHandle(out nint handle)
        {
            handle = 0;

            if (_playbackWindow == null)
                return false;

            object? impl = _playbackWindow.PlatformImpl;
            if (impl == null)
                return false;

            IntPtr nativeHandle = TryExtractPlatformHandle(impl);
            if (nativeHandle == IntPtr.Zero)
                return false;

            if (_playbackEmbeddingMode == EditorEmbeddingMode.CocoaViewHost)
            {
                nint contentView = CocoaNativeInterop.GetContentView(nativeHandle);
                if (contentView == 0)
                    return false;

                handle = contentView;
                return true;
            }

            handle = nativeHandle;
            return true;
        }

        private PixelSize GetPlaybackWindowPixelSize()
        {
            if (_playbackWindow == null)
                return default;

            double scaling = TopLevel.GetTopLevel(_playbackWindow)?.RenderScaling ?? 1.0;
            Size clientSize = _playbackWindow.ClientSize;

            int width = Math.Max(1, (int)Math.Round(clientSize.Width * scaling));
            int height = Math.Max(1, (int)Math.Round(clientSize.Height * scaling));

            return new PixelSize(width, height);
        }

        private string GetPlaybackProjectFolderName()
        {
            string rootPath = string.IsNullOrWhiteSpace(_projectRootPath)
                ? AppContext.BaseDirectory
                : _projectRootPath;

            string trimmed = rootPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            string name = Path.GetFileName(trimmed);

            if (!string.IsNullOrWhiteSpace(name))
                return name;

            return trimmed;
        }

        private string BuildPlaybackWindowTitle(int width, int height, int fps)
        {
            return BuildPlaybackWindowTitle(width, height, fps, null);
        }

        private string BuildPlaybackWindowTitle(int width, int height, int fps, PlaybackRenderStatus? status)
        {
            string projectFolderName = GetPlaybackProjectFolderName();

            if (status == null)
                return $"{projectFolderName}  <>  {width}x{height}  |  FPS {fps}";

            string gpuText = status.GpuTimeAvailable
                ? status.GpuFrameMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms"
                : "--ms";

            return $"{projectFolderName}  <>  {width}x{height}  |  FPS {fps}  |  GPU {gpuText}  |  Draw {status.DrawCalls}  |  Vtx {status.DrawnVertices}  |  Tri {status.DrawnTriangles}  |  Cull {status.CulledCommands}/{status.SubmittedCommands}";
        }

        private void UpdatePlaybackWindowTitle(int width, int height, int fps, PlaybackRenderStatus? status = null)
        {
            if (_playbackWindow == null)
                return;

            _playbackWindow.Title = BuildPlaybackWindowTitle(width, height, fps, status);
        }

        private bool TryReadPlaybackStatus(out PlaybackRenderStatus status)
        {
            status = new PlaybackRenderStatus();

            if (string.IsNullOrWhiteSpace(_playbackStatusFilePath))
                return false;

            if (!File.Exists(_playbackStatusFilePath))
                return false;

            string text;

            try
            {
                text = File.ReadAllText(_playbackStatusFilePath).Trim();
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
                return false;

            string[] parts = text.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 3 && parts.Length != 9)
                return false;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width))
                return false;

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
                return false;

            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fps))
                return false;

            if (width <= 0 || height <= 0 || fps < 0)
                return false;

            if (parts.Length == 3)
            {
                status = new PlaybackRenderStatus
                {
                    Width = width,
                    Height = height,
                    Fps = fps
                };

                return true;
            }

            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double gpuMilliseconds))
                return false;

            if (!int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int drawCalls))
                return false;

            if (!long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long drawnVertices))
                return false;

            if (!long.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out long drawnTriangles))
                return false;

            if (!int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int culledCommands))
                return false;

            if (!int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out int submittedCommands))
                return false;

            if (drawCalls < 0 || drawnVertices < 0 || drawnTriangles < 0 || culledCommands < 0 || submittedCommands < 0)
                return false;

            status = new PlaybackRenderStatus
            {
                Width = width,
                Height = height,
                Fps = fps,
                GpuTimeAvailable = gpuMilliseconds >= 0.0,
                GpuFrameMilliseconds = Math.Max(0.0, gpuMilliseconds),
                DrawCalls = drawCalls,
                DrawnVertices = drawnVertices,
                DrawnTriangles = drawnTriangles,
                CulledCommands = culledCommands,
                SubmittedCommands = submittedCommands
            };

            return true;
        }

        private void StartPlaybackTitlePolling()
        {
            _playbackTitlePollTimer ??= new DispatcherTimer();

            _playbackTitlePollTimer.Stop();
            _playbackTitlePollTimer.Interval = TimeSpan.FromMilliseconds(200);
            _playbackTitlePollTimer.Tick -= OnPlaybackTitlePollTimerTick;
            _playbackTitlePollTimer.Tick += OnPlaybackTitlePollTimerTick;
            _playbackTitlePollTimer.Start();
        }

        private void StopPlaybackTitlePolling()
        {
            _playbackTitlePollTimer?.Stop();
        }

        private void OnPlaybackTitlePollTimerTick(object? sender, EventArgs e)
        {
            if (!_isPlaybackRunning || _playbackWindow == null)
                return;

            if (TryReadPlaybackStatus(out PlaybackRenderStatus status))
            {
                UpdatePlaybackWindowTitle(status.Width, status.Height, status.Fps, status);
                return;
            }

            PixelSize size = GetPlaybackWindowPixelSize();
            if (size.Width > 0 && size.Height > 0)
                UpdatePlaybackWindowTitle(size.Width, size.Height, 0);
        }

        private void SuppressPlaybackFocusFor(int milliseconds)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(milliseconds);
            if (until > _playbackSuppressFocusUntilUtc)
                _playbackSuppressFocusUntilUtc = until;
        }

        private void RequestDeferredPlaybackFocus(int delayMilliseconds = 180)
        {
            if (_playbackWindow == null || !_isPlaybackRunning)
                return;

            _playbackDeferredFocusTimer ??= new DispatcherTimer();
            _playbackDeferredFocusTimer.Stop();
            _playbackDeferredFocusTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);

            _playbackDeferredFocusTimer.Tick -= OnPlaybackDeferredFocusTimerTick;
            _playbackDeferredFocusTimer.Tick += OnPlaybackDeferredFocusTimerTick;
            _playbackDeferredFocusTimer.Start();
        }

        private void OnPlaybackDeferredFocusTimerTick(object? sender, EventArgs e)
        {
            _playbackDeferredFocusTimer?.Stop();

            if (!_isPlaybackRunning || _playbackWindow == null)
                return;

            if (_playbackWindow.WindowState == WindowState.Minimized)
                return;

            if (DateTime.UtcNow < _playbackSuppressFocusUntilUtc)
            {
                RequestDeferredPlaybackFocus(120);
                return;
            }

            QueuePlaybackCommand("focus");
        }

        private IntPtr TryExtractPlatformHandle(object platformImpl)
        {
            Type implType = platformImpl.GetType();

            System.Reflection.PropertyInfo? handleProperty = implType.GetProperty(
                "Handle",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (handleProperty == null)
                return IntPtr.Zero;

            object? platformHandle = handleProperty.GetValue(platformImpl);
            if (platformHandle == null)
                return IntPtr.Zero;

            Type platformHandleType = platformHandle.GetType();

            System.Reflection.PropertyInfo? rawHandleProperty = platformHandleType.GetProperty(
                "Handle",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            if (rawHandleProperty == null)
                return IntPtr.Zero;

            object? rawHandleValue = rawHandleProperty.GetValue(platformHandle);
            if (rawHandleValue == null)
                return IntPtr.Zero;

            return ConvertObjectToIntPtr(rawHandleValue);
        }

        private IntPtr ConvertObjectToIntPtr(object value)
        {
            if (value is IntPtr intPtrValue)
                return intPtrValue;

            if (value is int intValue)
                return new IntPtr(intValue);

            if (value is long longValue)
                return new IntPtr(longValue);

            if (value is uint uintValue)
                return new IntPtr(unchecked((long)uintValue));

            if (value is ulong ulongValue)
                return new IntPtr(unchecked((long)ulongValue));

            return IntPtr.Zero;
        }

        private void ShowPlaybackWindowAsChild()
        {
            EnsurePlaybackWindow();

            if (_playbackWindow == null)
                return;

            if (!_playbackWindow.IsVisible)
                _playbackWindow.Show(this);

            _playbackWindow.Activate();

            if (_isPlaybackRunning)
                QueuePlaybackCommand("focus");
        }

        private string CreatePlaybackControlFilePath()
        {
            string fileName = "lse-playback-control-" + Guid.NewGuid().ToString("N") + ".cmd";
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        private string CreatePlaybackStatusFilePath()
        {
            string fileName = "lse-playback-status-" + Guid.NewGuid().ToString("N") + ".txt";
            return Path.Combine(Path.GetTempPath(), fileName);
        }

        private void QueuePlaybackCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(_playbackControlFilePath))
                return;

            try
            {
                File.AppendAllText(
                    _playbackControlFilePath,
                    command + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch
            {
            }
        }

        private string BuildExternalHostedGameArguments()
        {
            if (_playbackWindow == null)
                throw new InvalidOperationException("Playback window is not ready.");

            if (_playbackWindowNativeHandle == 0)
                throw new InvalidOperationException("Playback native window handle is missing.");

            if (string.IsNullOrWhiteSpace(_playbackControlFilePath))
                throw new InvalidOperationException("Playback control file path is missing.");

            if (string.IsNullOrWhiteSpace(_playbackStatusFilePath))
                throw new InvalidOperationException("Playback status file path is missing.");

            PixelSize size = GetPlaybackWindowPixelSize();

            string arguments = EngineLaunchArgumentBuilder.BuildGameModeArguments(_projectRootPath ?? AppContext.BaseDirectory);
            arguments += " --external-host";
            arguments += $" --external-host-width={size.Width}";
            arguments += $" --external-host-height={size.Height}";
            arguments += $" --external-control-file=\"{_playbackControlFilePath}\"";
            arguments += $" --external-status-file=\"{_playbackStatusFilePath}\"";

            if (_playbackEmbeddingMode == EditorEmbeddingMode.CocoaViewHost)
            {
                arguments += " --external-host-mode=cocoa";
                arguments += $" --external-host-cocoa-parent=0x{_playbackWindowNativeHandle.ToInt64():X}";
                return arguments;
            }

            EditorHostBootstrapInfo info = EditorHostBridge.GetBootstrapInfo();

            if (_playbackEmbeddingMode == EditorEmbeddingMode.ForeignChildWindow)
            {
                if (info.Win32Hwnd != 0)
                {
                    arguments += " --external-host-mode=win32";
                    arguments += $" --external-host-win32-parent=0x{_playbackWindowNativeHandle.ToInt64():X}";
                    return arguments;
                }

                if (info.X11Window != 0 && info.X11Display != 0)
                {
                    arguments += " --external-host-mode=x11";
                    arguments += $" --external-host-x11-parent=0x{_playbackWindowNativeHandle.ToInt64():X}";
                    return arguments;
                }
            }

            throw new InvalidOperationException("Unsupported playback embedding mode.");
        }

        private string ResolveEngineExecutablePath()
        {
            string processPath = Environment.ProcessPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                return processPath;

            string baseDirectory = AppContext.BaseDirectory;

            string[] candidates =
            {
                Path.Combine(baseDirectory, "LimitlessSquareEngine.exe"),
                Path.Combine(baseDirectory, "LimitlessSquareEngine"),
                Path.Combine(baseDirectory, "Limitless Square Engine.exe"),
                Path.Combine(baseDirectory, "Limitless Square Engine")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            throw new FileNotFoundException("Game executable not found.");
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

            if (point.Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            {
                if (!_isSceneHostRightDragging && TryHandleSceneHostRenderedMeshSelection(point.Position))
                    e.Handled = true;

                return;
            }

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

            PointerPoint point = e.GetCurrentPoint(_sceneHost);

            if (point.Properties.IsMiddleButtonPressed)
                _sceneHostMoveSpeedMultiplier = 1.0;

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

        private bool TryHandleSceneHostRenderedMeshSelection(Point position)
        {
            if (_currentTreeScene == null)
                return false;

            if (!EditorHostBridge.IsRenderWindowAlive)
                return false;

            PixelSize hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return false;

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;

            int screenX = (int)Math.Floor(position.X * scaling);
            int screenY = (int)Math.Floor(position.Y * scaling);

            screenX = Math.Clamp(screenX, 0, hostSize.Width - 1);
            screenY = Math.Clamp(screenY, 0, hostSize.Height - 1);

            RenderedMeshRaycastHit hit;

            try
            {
                hit = EditorHostBridge.RaycastRenderedMeshAtPixel(screenX, screenY);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
                return false;
            }

            if (!hit.Hit || string.IsNullOrWhiteSpace(hit.ObjectId))
            {
                ClearSceneHostRenderedMeshSelection();
                return true;
            }

            SceneObject? matched = _currentTreeScene.Objects.FirstOrDefault(
                o => string.Equals(o.Id, hit.ObjectId, StringComparison.Ordinal));

            if (matched == null)
            {
                ClearSceneHostRenderedMeshSelection();
                return true;
            }

            SelectSceneObjectFromSceneHost(matched);
            return true;
        }

        private void SelectSceneObjectFromSceneHost(SceneObject obj)
        {
            _selectedSceneObject = obj;
            ApplyPreviewSelectionContour(obj);

            _isProgrammaticSceneTreeSelection = true;
            try
            {
                TrySelectSceneObjectInTree(obj);
            }
            finally
            {
                _isProgrammaticSceneTreeSelection = false;
            }

            ShowSceneObjectInspector(obj);

            if (_sceneTreeDeleteButton != null)
                _sceneTreeDeleteButton.IsEnabled = true;
        }

        private void ClearSceneHostRenderedMeshSelection()
        {
            ClearSelectedSceneObjectState();
            ClearPreviewSelectionContour();

            if (_sceneTreeView?.ItemsSource is System.Collections.IEnumerable items)
                ClearSceneTreeSelection(items);

            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");
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

        private bool CanPreviewContourSceneObject(SceneObject? obj)
        {
            if (obj == null)
                return false;

            if (!obj.Active || !obj.Visible)
                return false;

            if (string.IsNullOrWhiteSpace(obj.Mesh))
                return false;

            return true;
        }

        private void ClearPreviewSelectionContour()
        {
            _currentPreviewContouredObjectId = null;

            if (!EditorHostBridge.IsRenderWindowAlive)
                return;

            try
            {
                EditorHostBridge.ClearSceneContours(EditorPreviewSceneId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
            }
        }

        private void ApplyPreviewSelectionContour(SceneObject? treeObject)
        {
            ClearPreviewSelectionContour();

            if (treeObject == null)
                return;

            if (_currentPreviewScene == null)
                return;

            SceneObject? previewObject = _currentPreviewScene.Objects.FirstOrDefault(
                o => string.Equals(o.Id, treeObject.Id, StringComparison.Ordinal));

            if (!CanPreviewContourSceneObject(previewObject))
                return;

            if (!EditorHostBridge.IsRenderWindowAlive)
                return;

            try
            {
                EditorHostBridge.SetSceneObjectContour(
                    EditorPreviewSceneId,
                    previewObject.Id,
                    true,
                    0f,
                    1f,
                    1f,
                    2f);

                _currentPreviewContouredObjectId = previewObject.Id;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                Debug.WriteLine(ex.GetBaseException().ToString());
            }
        }

        private void ReapplyPreviewSelectionOutline()
        {
            if (_selectedSceneObject == null)
            {
                ClearPreviewSelectionContour();
                return;
            }

            ApplyPreviewSelectionContour(_selectedSceneObject);
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
            Grid.SetRowSpan(rightPanel, 3);
            workspace.Children.Add(rightPanel);

            Grid.SetColumn(bottomPanel, 0);
            Grid.SetColumnSpan(bottomPanel, 3);
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
            Grid.SetRowSpan(rightSplitter, 3);
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
            Grid.SetColumnSpan(bottomSplitter, 3);
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

        private sealed class PlaybackGlyphIcon : Control
        {
            public static readonly StyledProperty<PlaybackButtonGlyph> GlyphProperty =
                AvaloniaProperty.Register<PlaybackGlyphIcon, PlaybackButtonGlyph>(
                    nameof(Glyph),
                    PlaybackButtonGlyph.PlayTriangle);

            static PlaybackGlyphIcon()
            {
                AffectsRender<PlaybackGlyphIcon>(GlyphProperty);
            }

            public PlaybackButtonGlyph Glyph
            {
                get => GetValue(GlyphProperty);
                set => SetValue(GlyphProperty, value);
            }

            public PlaybackGlyphIcon()
            {
                Width = 14;
                Height = 14;
                HorizontalAlignment = HorizontalAlignment.Center;
                VerticalAlignment = VerticalAlignment.Center;
                IsHitTestVisible = false;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                Rect bounds = new Rect(Bounds.Size);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                IBrush brush = Brushes.White;

                switch (Glyph)
                {
                    case PlaybackButtonGlyph.PlayTriangle:
                        {
                            StreamGeometry geometry = new StreamGeometry();
                            using StreamGeometryContext gc = geometry.Open();
                            gc.BeginFigure(new Point(bounds.Left + 3, bounds.Top + 2), true);
                            gc.LineTo(new Point(bounds.Right - 2, bounds.Center.Y));
                            gc.LineTo(new Point(bounds.Left + 3, bounds.Bottom - 2));
                            gc.EndFigure(true);
                            context.DrawGeometry(brush, null, geometry);
                            break;
                        }

                    case PlaybackButtonGlyph.StopSquare:
                        {
                            Rect rect = new Rect(bounds.Left + 2, bounds.Top + 2, bounds.Width - 4, bounds.Height - 4);
                            context.DrawRectangle(brush, null, rect);
                            break;
                        }

                    case PlaybackButtonGlyph.PauseBars:
                        {
                            double totalWidth = bounds.Width - 4;
                            double gap = totalWidth / 5;
                            double barWidth = gap * 1.5;
                            double barHeight = bounds.Height - 4;
                            double left = bounds.Left + (bounds.Width - (barWidth * 2 + gap)) / 2;
                            double top = bounds.Top + 2;

                            Rect leftBar = new Rect(left, top, barWidth, barHeight);
                            Rect rightBar = new Rect(left + barWidth + gap, top, barWidth, barHeight);

                            context.DrawRectangle(brush, null, leftBar);
                            context.DrawRectangle(brush, null, rightBar);
                            break;
                        }

                    case PlaybackButtonGlyph.StepCircle:
                        {
                            Point center = bounds.Center;
                            double radius = Math.Min(bounds.Width, bounds.Height) / 2 - 2;
                            context.DrawEllipse(brush, null, center, radius, radius);
                            break;
                        }
                }
            }
        }

        private sealed class ResourceGlyphIcon : Control
        {
            public static readonly StyledProperty<ResourceIconKind> KindProperty =
                AvaloniaProperty.Register<ResourceGlyphIcon, ResourceIconKind>(
                    nameof(Kind),
                    ResourceIconKind.GenericFile);

            public ResourceIconKind Kind
            {
                get => GetValue(KindProperty);
                set => SetValue(KindProperty, value);
            }

            private const double ResourceGlyphReferenceSize = 52.0;

            private static double ScaleFromBoundsY(Rect bounds, double absoluteAt52)
            {
                return bounds.Height * (absoluteAt52 / ResourceGlyphReferenceSize);
            }

            private static readonly IBrush WhiteBrush = Brushes.White;
            private static readonly IBrush LuaBrush = new SolidColorBrush(Color.Parse("#103A8A"));
            private static readonly IBrush JsonBrush = new SolidColorBrush(Color.Parse("#7A1F1F"));
            private static readonly IBrush SceneBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#336699"), 0.0),
                    new GradientStop(Color.Parse("#888888"), 0.5),
                    new GradientStop(Color.Parse("#226622"), 1.0)
                }
            };
            private static readonly IBrush RainbowBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.Parse("#993333"), 0.0),
                    new GradientStop(Color.Parse("#999933"), 0.2),
                    new GradientStop(Color.Parse("#339933"), 0.4),
                    new GradientStop(Color.Parse("#339999"), 0.6),
                    new GradientStop(Color.Parse("#333399"), 0.8),
                    new GradientStop(Color.Parse("#993399"), 1.0)
                }
            };
            private static readonly IBrush MaterialBrush = new SolidColorBrush(Color.Parse("#226622"));
            private static readonly IBrush ImageBrush = new SolidColorBrush(Color.Parse("#66C8FF"));
            private static readonly IBrush AudioBrush = new SolidColorBrush(Color.Parse("#7B1FA2"));
            private static readonly IBrush AudioDiscBrush = new SolidColorBrush(Color.Parse("#555555"));
            private static readonly IBrush AudioDiscCenterBrush = new SolidColorBrush(Color.Parse("#000000"));
            private static readonly IBrush MountainLightBrush = new SolidColorBrush(Color.Parse("#6DBE57"));
            private static readonly IBrush MountainDarkBrush = new SolidColorBrush(Color.Parse("#4E9C3A"));
            private static readonly IBrush SunBrush = new SolidColorBrush(Color.Parse("#FFD54A"));
            private static readonly IBrush AxisRedBrush = new SolidColorBrush(Color.Parse("#FF8888"));
            private static readonly IBrush AxisGreenBrush = new SolidColorBrush(Color.Parse("#88FF88"));
            private static readonly IBrush AxisBlueBrush = new SolidColorBrush(Color.Parse("#8888FF"));
            private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.Parse("#888888"));
            private static readonly IBrush ShadowGroundBrush = new SolidColorBrush(Color.Parse("#113311"));

            private static readonly Pen OutlinePen = new Pen(WhiteBrush, 2);

            public ResourceGlyphIcon()
            {
                Width = 50;
                Height = 50;
                HorizontalAlignment = HorizontalAlignment.Center;
                VerticalAlignment = VerticalAlignment.Center;
                IsHitTestVisible = false;
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                Rect bounds = new Rect(Bounds.Size);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                switch (Kind)
                {
                    case ResourceIconKind.Folder:
                        DrawFolder(context, bounds);
                        break;

                    case ResourceIconKind.LuaFile:
                        DrawFileBase(context, bounds, LuaBrush, true);
                        DrawLuaBadge(context, bounds);
                        DrawCenteredLabel(context, bounds, "Lua", ScaleFromBoundsY(bounds, 14.0), FontWeight.Bold);
                        break;

                    case ResourceIconKind.JsonFile:
                        DrawFileBase(context, bounds, JsonBrush, true);
                        DrawCenteredLabel(context, bounds, "Json", ScaleFromBoundsY(bounds, 14.0), FontWeight.Bold);
                        break;

                    case ResourceIconKind.SceneJsonFile:
                        DrawFileBase(context, bounds, SceneBrush, true);
                        DrawSceneGizmo(context, bounds);
                        break;

                    case ResourceIconKind.MaterialJsonFile:
                        DrawFileBase(context, bounds, MaterialBrush, true);
                        DrawMaterialSphere(context, bounds);
                        break;

                    case ResourceIconKind.ImageFile:
                        DrawImageFile(context, bounds);
                        break;

                    case ResourceIconKind.FragFile:
                        DrawFileBase(context, bounds, RainbowBrush, true);
                        DrawCenteredLabel(context, bounds, "F", ScaleFromBoundsY(bounds, 18.0), FontWeight.Bold);
                        break;

                    case ResourceIconKind.VertFile:
                        DrawFileBase(context, bounds, RainbowBrush, true);
                        DrawCenteredLabel(context, bounds, "V", ScaleFromBoundsY(bounds, 18.0), FontWeight.Bold);
                        break;

                    case ResourceIconKind.AudioFile:
                        DrawFileBase(context, bounds, AudioBrush, true);
                        DrawAudioDisc(context, bounds);
                        break;

                    default:
                        DrawFileBase(context, bounds, null, true);
                        break;
                }
            }

            private static void DrawFolder(DrawingContext context, Rect bounds)
            {
                double boxSize = Math.Min(bounds.Width, bounds.Height);
                double boxX = bounds.X + (bounds.Width - boxSize) / 2;
                double boxY = bounds.Y + (bounds.Height - boxSize) / 2;

                double bodyWidth = boxSize * 0.82;
                double bodyHeight = bodyWidth * 3.0 / 4.0;
                double tabHeight = bodyHeight * 0.24;
                double totalHeight = bodyHeight + tabHeight;
                double x = boxX + (boxSize - bodyWidth) / 2;
                double y = boxY + (boxSize - totalHeight) / 2;
                double bodyY = y + tabHeight;

                double radius = Math.Min(bodyWidth, bodyHeight) * 0.10;
                double tabWidth = bodyWidth * 0.34;

                Rect bodyRect = new Rect(x, bodyY, bodyWidth, bodyHeight);
                context.DrawRectangle(null, OutlinePen, new RoundedRect(bodyRect, radius, radius));

                StreamGeometry tabGeometry = new StreamGeometry();
                using (StreamGeometryContext gc = tabGeometry.Open())
                {
                    gc.BeginFigure(new Point(x, bodyY + radius), false);
                    gc.LineTo(new Point(x, y + radius));
                    gc.ArcTo(
                        new Point(x + radius, y),
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);
                    gc.LineTo(new Point(x + tabWidth - radius, y));
                    gc.ArcTo(
                        new Point(x + tabWidth, y + radius),
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);
                    gc.LineTo(new Point(x + tabWidth, bodyY));
                    gc.EndFigure(false);
                }

                context.DrawGeometry(null, OutlinePen, tabGeometry);
            }

            private static void DrawFileBase(DrawingContext context, Rect bounds, IBrush? fillBrush, bool drawFold)
            {
                double boxSize = Math.Min(bounds.Width, bounds.Height);
                double boxX = bounds.X + (bounds.Width - boxSize) / 2;
                double boxY = bounds.Y + (bounds.Height - boxSize) / 2;

                double fileWidth = boxSize * 0.72;
                double fileHeight = fileWidth * 4.0 / 3.0;
                double x = boxX + (boxSize - fileWidth) / 2;
                double y = boxY + (boxSize - fileHeight) / 2;

                double radius = Math.Min(fileWidth, fileHeight) * 0.10;
                double foldSize = fileWidth * 0.32;

                StreamGeometry bodyGeometry = new StreamGeometry();
                using (StreamGeometryContext gc = bodyGeometry.Open())
                {
                    gc.BeginFigure(new Point(x + radius, y), true);
                    gc.LineTo(new Point(x + fileWidth - foldSize, y));
                    gc.LineTo(new Point(x + fileWidth, y + foldSize));
                    gc.LineTo(new Point(x + fileWidth, y + fileHeight - radius));
                    gc.ArcTo(
                        new Point(x + fileWidth - radius, y + fileHeight),
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);
                    gc.LineTo(new Point(x + radius, y + fileHeight));
                    gc.ArcTo(
                        new Point(x, y + fileHeight - radius),
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);
                    gc.LineTo(new Point(x, y + radius));
                    gc.ArcTo(
                        new Point(x + radius, y),
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);
                    gc.EndFigure(true);
                }

                if (fillBrush != null)
                    context.DrawGeometry(fillBrush, null, bodyGeometry);

                context.DrawGeometry(null, OutlinePen, bodyGeometry);

                if (!drawFold)
                    return;

                StreamGeometry foldGeometry = new StreamGeometry();
                using (StreamGeometryContext gc = foldGeometry.Open())
                {
                    double r = radius;
                    Point p0 = new Point(x + fileWidth - foldSize, y);
                    Point p1 = new Point(x + fileWidth, y + foldSize);
                    Point p2 = new Point(x + fileWidth - foldSize, y + foldSize);

                    Vector v21 = p1 - p2;
                    Vector v20 = p0 - p2;
                    double len21 = Math.Sqrt(v21.X * v21.X + v21.Y * v21.Y);
                    double len20 = Math.Sqrt(v20.X * v20.X + v20.Y * v20.Y);
                    double t21 = Math.Min(r / len21, 0.5);
                    double t20 = Math.Min(r / len20, 0.5);
                    Point p2b = p2 + v21 * t21;
                    Point p2a = p2 + v20 * t20;

                    gc.BeginFigure(p0, false);
                    gc.LineTo(p2a);
                    gc.ArcTo(p2b, new Size(r, r), 0, false, SweepDirection.CounterClockwise);
                    gc.LineTo(p1);
                    gc.EndFigure(false);
                }

                context.DrawGeometry(null, OutlinePen, foldGeometry);
            }

            private static void DrawLuaBadge(DrawingContext context, Rect bounds)
            {
                double height = bounds.Height * 0.82;
                double width = height * 0.75;
                double x = bounds.X + (bounds.Width - width) / 2;
                double y = bounds.Y + (bounds.Height - height) / 2;
                double radius = Math.Min(width, height) * 0.11;

                Point center = new Point(x + width * 0.78, y + height * 0.36);
                context.DrawEllipse(WhiteBrush, null, center, radius, radius);
            }

            private static void DrawImageFile(DrawingContext context, Rect bounds)
            {
                double size = bounds.Width * 0.78;
                double x = bounds.X + (bounds.Width - size) / 2;
                double y = bounds.Y + (bounds.Height - size) / 2;

                Rect rect = new Rect(x, y, size, size);

                double cornerRadius = bounds.Width * (4.0 / 52.0);
                context.DrawRectangle(ImageBrush, null, new RoundedRect(rect, cornerRadius, cornerRadius));
                context.DrawRectangle(null, OutlinePen, new RoundedRect(rect, cornerRadius, cornerRadius));

                StreamGeometry backMountain = new StreamGeometry();
                using (StreamGeometryContext gc = backMountain.Open())
                {
                    gc.BeginFigure(new Point(x + size * 0.14, y + size * 0.78), true);
                    gc.LineTo(new Point(x + size * 0.38, y + size * 0.48));
                    gc.LineTo(new Point(x + size * 0.56, y + size * 0.78));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(MountainDarkBrush, null, backMountain);

                StreamGeometry frontMountain = new StreamGeometry();
                using (StreamGeometryContext gc = frontMountain.Open())
                {
                    gc.BeginFigure(new Point(x + size * 0.34, y + size * 0.78), true);
                    gc.LineTo(new Point(x + size * 0.62, y + size * 0.36));
                    gc.LineTo(new Point(x + size * 0.86, y + size * 0.78));
                    gc.EndFigure(true);
                }
                context.DrawGeometry(MountainLightBrush, null, frontMountain);

                Point sunCenter = new Point(x + size * 0.75, y + size * 0.24);
                double sunRadius = size * 0.09;
                context.DrawEllipse(SunBrush, null, sunCenter, sunRadius, sunRadius);
            }

            private static void DrawAudioDisc(DrawingContext context, Rect bounds)
            {
                double outerRadius = bounds.Width * 0.22;
                Point center = bounds.Center;

                context.DrawEllipse(AudioDiscBrush, null, center, outerRadius, outerRadius);

                double innerRadius = outerRadius * 0.35;
                context.DrawEllipse(WhiteBrush, null, center, innerRadius, innerRadius);

                double dotRadius = innerRadius * 0.18;
                context.DrawEllipse(AudioDiscCenterBrush, null, center, dotRadius, dotRadius);
            }

            private static void DrawSceneGizmo(DrawingContext context, Rect bounds)
            {
                double size = bounds.Width * 1.8;
                double height = bounds.Height * 0.82;
                double width = height * 0.75;
                double x = bounds.X + (bounds.Width - width) / 2;
                double y = bounds.Y + (bounds.Height - height) / 2;

                Point center = new Point(x + width * 0.50, y + height * 0.58);

                double axisThickness = bounds.Width * (2.0 / 52.0);

                context.DrawLine(new Pen(AxisRedBrush, axisThickness), center, new Point(center.X - size * 0.1559, center.Y + size * 0.09));
                context.DrawLine(new Pen(AxisGreenBrush, axisThickness), center, new Point(center.X, center.Y - size * 0.18));
                context.DrawLine(new Pen(AxisBlueBrush, axisThickness), center, new Point(center.X + size * 0.1559, center.Y + size * 0.09));

                double centerRadius = bounds.Width * (4.0 / 52.0);
                context.DrawEllipse(WhiteBrush, null, center, centerRadius, centerRadius);
            }

            private static void DrawMaterialSphere(DrawingContext context, Rect bounds)
            {
                double radius = bounds.Width * 0.2;

                double centerYOffset = bounds.Height * (1.0 / 52.0);
                Point center = new Point(bounds.Center.X, bounds.Center.Y + centerYOffset);

                double shadowWidth = radius;
                double shadowHeight = radius * 0.3;
                Point shadowCenter = new Point(center.X + radius * 0.5, center.Y + radius);

                using (context.PushOpacity(0.6))
                {
                    context.DrawEllipse(
                        ShadowGroundBrush,
                        null,
                        shadowCenter,
                        shadowWidth,
                        shadowHeight);
                }

                context.DrawEllipse(WhiteBrush, null, center, radius, radius);

                double d = radius / Math.Sqrt(2.0);

                double epsilon = bounds.Width * (0.01 / 52.0);
                Point tipTopRight = new Point(center.X + d + epsilon, center.Y - d - epsilon);
                Point tipBottomLeft = new Point(center.X - d - epsilon, center.Y + d + epsilon);

                StreamGeometry crescent = new StreamGeometry();
                using (StreamGeometryContext gc = crescent.Open())
                {
                    gc.BeginFigure(tipTopRight, true);

                    gc.ArcTo(
                        tipBottomLeft,
                        new Size(radius, radius),
                        0,
                        false,
                        SweepDirection.Clockwise);

                    gc.ArcTo(
                        tipTopRight,
                        new Size(radius * 1.25, radius * 1.25),
                        0,
                        false,
                        SweepDirection.CounterClockwise);

                    gc.EndFigure(true);
                }

                context.DrawGeometry(ShadowBrush, null, crescent);
            }

            private static void DrawCenteredLabel(
                DrawingContext context,
                Rect bounds,
                string text,
                double fontSize,
                FontWeight fontWeight)
            {
                FormattedText formattedText = new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, fontWeight),
                    fontSize,
                    Brushes.White);

                double textYOffset = bounds.Height * (6.0 / 52.0);

                Point point = new Point(
                    bounds.Center.X - formattedText.Width / 2,
                    bounds.Center.Y - formattedText.Height / 2 + textYOffset);

                context.DrawText(formattedText, point);
            }
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

            _playButton = CreatePlaybackButton(PlaybackButtonGlyph.PlayTriangle);
            _pauseButton = CreatePlaybackButton(PlaybackButtonGlyph.PauseBars);
            _stepButton = CreatePlaybackButton(PlaybackButtonGlyph.StepCircle);

            _playButton.Click += (_, _) => TogglePlayback();
            _pauseButton.Click += (_, _) => TogglePausePlayback();
            _stepButton.Click += (_, _) => StepPlayback();

            panel.Children.Add(_playButton);
            panel.Children.Add(_pauseButton);
            panel.Children.Add(_stepButton);

            UpdatePlaybackButtonsVisualState();
            return panel;
        }

        private Button CreatePlaybackButton(PlaybackButtonGlyph glyph)
        {
            return new Button
            {
                Content = new PlaybackGlyphIcon
                {
                    Glyph = glyph
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

        private void ApplyPlaybackButtonStyle(Button button, string backgroundColor, string borderColor)
        {
            button.Background = new SolidColorBrush(Color.Parse(backgroundColor));
            button.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        }

        private void ApplyDefaultPlaybackButtonStyle(Button button)
        {
            ApplyPlaybackButtonStyle(button, "#2A2A2A", "#555555");
        }

        private void UpdatePlaybackButtonsVisualState()
        {
            bool hasProjectLoaded = !string.IsNullOrWhiteSpace(_projectRootPath);

            if (_playButton != null)
            {
                if (_playButton.Content is PlaybackGlyphIcon playIcon)
                    playIcon.Glyph = _isPlaybackRunning ? PlaybackButtonGlyph.StopSquare : PlaybackButtonGlyph.PlayTriangle;

                _playButton.IsEnabled = _isPlaybackRunning || hasProjectLoaded;

                if (_isPlaybackRunning)
                    ApplyPlaybackButtonStyle(_playButton, "#115511", "#339933");
                else
                    ApplyDefaultPlaybackButtonStyle(_playButton);
            }

            if (_pauseButton != null)
            {
                _pauseButton.IsEnabled = _isPlaybackRunning;

                if (_isPlaybackRunning && _isPlaybackPaused)
                    ApplyPlaybackButtonStyle(_pauseButton, "#113355", "#336699");
                else
                    ApplyDefaultPlaybackButtonStyle(_pauseButton);
            }

            if (_stepButton != null)
            {
                _stepButton.IsEnabled = _isPlaybackRunning && _isPlaybackPaused;
                ApplyDefaultPlaybackButtonStyle(_stepButton);
            }
        }

        private void TogglePlayback()
        {
            if (_isPlaybackRunning)
            {
                StopPlayback();
                return;
            }

            StartPlayback();
        }

        private void StartPlayback()
        {
            if (string.IsNullOrWhiteSpace(_projectRootPath))
                return;

            if (_isPlaybackRunning)
                return;

            EnsurePlaybackWindow();
            ShowPlaybackWindowAsChild();

            if (_playbackWindow == null)
                return;

            if (_playbackEmbeddingMode == EditorEmbeddingMode.Unsupported)
                return;

            _playbackControlFilePath = CreatePlaybackControlFilePath();
            _playbackStatusFilePath = CreatePlaybackStatusFilePath();
            _isPlaybackPaused = false;

            try
            {
                File.WriteAllText(_playbackStatusFilePath, string.Empty);
            }
            catch
            {
            }

            PixelSize startupSize = GetPlaybackWindowPixelSize();
            if (startupSize.Width > 0 && startupSize.Height > 0)
                UpdatePlaybackWindowTitle(startupSize.Width, startupSize.Height, 0);

            StartPlaybackTitlePolling();

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (!TryGetPlaybackWindowNativeHandle(out nint handle) || handle == 0)
                        return;

                    _playbackWindowNativeHandle = handle;

                    string executablePath = ResolveEngineExecutablePath();
                    string arguments = BuildExternalHostedGameArguments();

                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = executablePath,
                        Arguments = arguments,
                        WorkingDirectory = _projectRootPath!,
                        UseShellExecute = false
                    };

                    Process process = new Process
                    {
                        StartInfo = startInfo,
                        EnableRaisingEvents = true
                    };

                    if (!process.Start())
                        return;

                    _playbackProcess = process;
                    _isPlaybackRunning = true;
                    _isPlaybackPaused = false;
                    UpdatePlaybackButtonsVisualState();

                    PixelSize size = GetPlaybackWindowPixelSize();
                    QueuePlaybackCommand($"resize {size.Width} {size.Height}");
                    QueuePlaybackCommand("focus");

                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_isPlaybackRunning)
                            QueuePlaybackCommand("focus");
                    }, DispatcherPriority.Render);

                    DispatcherTimer.RunOnce(() =>
                    {
                        if (_isPlaybackRunning)
                            QueuePlaybackCommand("focus");
                    }, TimeSpan.FromMilliseconds(120));

                    process.Exited += (_, _) =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            _isPlaybackRunning = false;
                            _isPlaybackPaused = false;

                            try
                            {
                                _playbackProcess?.Dispose();
                            }
                            catch
                            {
                            }

                            _playbackProcess = null;

                            StopPlaybackTitlePolling();

                            if (!string.IsNullOrWhiteSpace(_playbackControlFilePath))
                            {
                                try
                                {
                                    File.Delete(_playbackControlFilePath);
                                }
                                catch
                                {
                                }

                                _playbackControlFilePath = null;
                            }

                            if (!string.IsNullOrWhiteSpace(_playbackStatusFilePath))
                            {
                                try
                                {
                                    File.Delete(_playbackStatusFilePath);
                                }
                                catch
                                {
                                }

                                _playbackStatusFilePath = null;
                            }

                            UpdatePlaybackButtonsVisualState();
                        });
                    };
                }
                catch
                {
                }
            }, DispatcherPriority.Render);
        }

        private void StopPlayback()
        {
            _isPlaybackRunning = false;
            _isPlaybackPaused = false;
            StopPlaybackTitlePolling();

            QueuePlaybackCommand("stop");

            if (_playbackProcess != null)
            {
                try
                {
                    if (!_playbackProcess.HasExited)
                        _playbackProcess.Kill(true);
                }
                catch
                {
                }

                try
                {
                    _playbackProcess.Dispose();
                }
                catch
                {
                }

                _playbackProcess = null;
            }

            if (!string.IsNullOrWhiteSpace(_playbackControlFilePath))
            {
                try
                {
                    File.Delete(_playbackControlFilePath);
                }
                catch
                {
                }

                _playbackControlFilePath = null;
            }

            if (!string.IsNullOrWhiteSpace(_playbackStatusFilePath))
            {
                try
                {
                    File.Delete(_playbackStatusFilePath);
                }
                catch
                {
                }

                _playbackStatusFilePath = null;
            }

            if (_playbackWindow != null)
            {
                Window window = _playbackWindow;
                _playbackWindow = null;
                _playbackWindowNativeHandle = 0;
                window.Close();
            }

            UpdatePlaybackButtonsVisualState();
        }

        private void TogglePausePlayback()
        {
            if (!_isPlaybackRunning)
                return;

            if (_isPlaybackPaused)
            {
                QueuePlaybackCommand("resume");
                _isPlaybackPaused = false;
            }
            else
            {
                QueuePlaybackCommand("pause");
                _isPlaybackPaused = true;
            }

            UpdatePlaybackButtonsVisualState();
        }

        private void StepPlayback()
        {
            if (!_isPlaybackRunning)
                return;

            if (!_isPlaybackPaused)
                return;

            QueuePlaybackCommand("step");
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
            root.Children.Add(CreateCollapsibleDataEditor("Data", () => obj.Data ?? "", value =>
            {
                string? newValue = string.IsNullOrWhiteSpace(value) ? null : value;
                string? oldValue = obj.Data;

                if (string.Equals(oldValue ?? string.Empty, newValue ?? string.Empty, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                #pragma warning disable CS8601
                obj.Data = newValue;
                #pragma warning restore CS8601
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

        private Control CreateCollapsibleDataEditor(
            string label,
            Func<string> getter,
            Func<string, bool> apply)
        {
            TextBox textBox = CreateInspectorTextBox(getter());
            textBox.AcceptsReturn = true;
            textBox.TextWrapping = TextWrapping.Wrap;
            textBox.MinHeight = 120;
            textBox.Height = double.NaN;

            Border contentArea = new Border
            {
                Child = textBox,
                IsVisible = false
            };

            Button headerButton = new Button
            {
                Height = InspectorTextBoxHeight,
                Background = new SolidColorBrush(Color.Parse("#222222")),
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = "\u25B6 " + label,
                Foreground = Brushes.White
            };

            headerButton.Click += (_, _) =>
            {
                bool isExpanded = contentArea.IsVisible;
                contentArea.IsVisible = !isExpanded;
                headerButton.Content = (isExpanded ? "\u25B6 " : "\u25BC ") + label;
            };

            void ResetText()
            {
                _isUpdatingSceneInspector = true;
                textBox.Text = getter();
                _isUpdatingSceneInspector = false;
            }

            void Commit()
            {
                if (_isUpdatingSceneInspector)
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

            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = InspectorPropertySpacing,
                Margin = new Thickness(12, 0, 12, 0)
            };

            panel.Children.Add(headerButton);
            panel.Children.Add(contentArea);

            return panel;
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

        private async void OnWindowResourceExplorerKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete)
                return;

            if (_selectedResourceItemState == null)
                return;

            if (GetFocusedTextBox() != null)
                return;

            if (_activeResourceRenameTextBox != null && _activeResourceRenameTextBox.IsVisible)
                return;

            await DeleteSelectedResourceItemAsync();
            e.Handled = true;
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

            button.IsCheckedChanged += (_, _) =>
            {
                bool newValue = button.IsChecked == true;

                if (!apply(newValue))
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

            newItem.Click += async (_, _) => await ShowCreateProjectDialogAsync();
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

        private async Task ShowCreateProjectDialogAsync()
        {
            await ShowCreateProjectDialogCoreAsync();
        }

        private async Task<CreateProjectDialogResult?> ShowCreateProjectDialogCoreAsync()
        {
            string defaultRootDirectoryPath = Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "."));

            bool isBusy = false;

            Window dialog = new Window
            {
                Title = "新建工程",
                Width = 520,
                Height = 300,
                CanResize = false,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111"))
            };

            TextBlock projectNameLabel = new TextBlock
            {
                Text = "工程名称",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBox projectNameTextBox = new TextBox
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

            TextBlock rootDirectoryLabel = new TextBlock
            {
                Text = "根目录所在文件夹",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBox rootDirectoryTextBox = new TextBox
            {
                Text = defaultRootDirectoryPath,
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

            TextBlock templateLabel = new TextBlock
            {
                Text = "工程模板",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            ComboBox templateComboBox = new ComboBox
            {
                ItemsSource = new object[]
                {
                    "空白",
                    "基础模板"
                },
                SelectedIndex = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Height = 30,
                MinHeight = 30
            };

            TextBlock templateDescriptionTextBlock = new TextBlock
            {
                Text = "创建一个包含基础资源和最简单功能实现的工程",
                Foreground = new SolidColorBrush(Color.Parse("#CCCCCC")),
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button cancelButton = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 30,
                MinWidth = 88,
                MinHeight = 30,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            Button createButton = new Button
            {
                Content = "创建",
                Width = 88,
                Height = 30,
                MinWidth = 88,
                MinHeight = 30,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            void UpdateBusyState(bool busy)
            {
                isBusy = busy;
                projectNameTextBox.IsEnabled = !busy;
                rootDirectoryTextBox.IsEnabled = !busy;
                templateComboBox.IsEnabled = !busy;
                selectFolderButton.IsEnabled = !busy;
                cancelButton.IsEnabled = !busy;
                createButton.IsEnabled = !busy;
            }

            templateComboBox.SelectionChanged += (_, _) =>
            {
                string selectedTemplate = templateComboBox.SelectedItem as string ?? "基础模板";

                if (string.Equals(selectedTemplate, "空白", StringComparison.Ordinal))
                    templateDescriptionTextBlock.Text = "创建完全空白的工程，除了必要文件夹什么都没有";
                else
                    templateDescriptionTextBlock.Text = "创建一个包含基础资源和最简单功能实现的工程";
            };

            selectFolderButton.Click += async (_, _) =>
            {
                if (isBusy)
                    return;

                if (!dialog.StorageProvider.CanPickFolder)
                    return;

                IReadOnlyList<IStorageFolder> folders = await dialog.StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions
                    {
                        Title = "选择根目录所在文件夹",
                        AllowMultiple = false
                    });

                if (folders.Count == 0)
                    return;

                string? selectedPath = folders[0].TryGetLocalPath();
                if (string.IsNullOrWhiteSpace(selectedPath))
                    return;

                rootDirectoryTextBox.Text = selectedPath;
            };

            cancelButton.Click += (_, _) =>
            {
                if (isBusy)
                    return;

                dialog.Close(new CreateProjectDialogResult
                {
                    Confirmed = false,
                    CreatedSuccessfully = false
                });
            };

            createButton.Click += async (_, _) =>
            {
                if (isBusy)
                    return;

                UpdateBusyState(true);

                CreateProjectValidationResult validation = ValidateCreateProjectInput(
                    projectNameTextBox.Text,
                    rootDirectoryTextBox.Text);

                if (!validation.IsValid)
                {
                    await ShowSimpleWarningDialogAsync("警告", validation.Message);
                    UpdateBusyState(false);
                    return;
                }

                try
                {
                    string rootDirectoryPath = Path.GetFullPath(rootDirectoryTextBox.Text ?? "");
                    string projectName = (projectNameTextBox.Text ?? "").Trim();
                    string templateName = templateComboBox.SelectedItem as string ?? "基础模板";
                    string projectDirectoryPath = Path.Combine(rootDirectoryPath, projectName);
                    string assetsDirectoryPath = Path.Combine(projectDirectoryPath, "Assets");

                    if (Directory.Exists(projectDirectoryPath) || File.Exists(projectDirectoryPath))
                    {
                        await ShowSimpleWarningDialogAsync("警告", "同名工程已存在");
                        UpdateBusyState(false);
                        return;
                    }

                    Directory.CreateDirectory(projectDirectoryPath);
                    Directory.CreateDirectory(assetsDirectoryPath);

                    if (string.Equals(templateName, "基础模板", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Materials"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Scenes"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Scripts"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Shaders"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Textures"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Models"));
                        Directory.CreateDirectory(Path.Combine(assetsDirectoryPath, "Canvas"));
                    }

                    TryOpenFolderWithSystem(projectDirectoryPath);
                    ShowProjectFolderTree(projectDirectoryPath);

                    dialog.Close(new CreateProjectDialogResult
                    {
                        Confirmed = true,
                        ProjectName = projectName,
                        RootDirectoryPath = rootDirectoryPath,
                        TemplateName = templateName,
                        CreatedSuccessfully = true
                    });
                }
                catch (Exception ex)
                {
                    await ShowSimpleWarningDialogAsync("警告", ex.Message);
                    UpdateBusyState(false);
                }
            };

            Grid rootDirectoryRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,8,Auto"),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            Grid.SetColumn(rootDirectoryTextBox, 0);
            Grid.SetColumn(selectFolderButton, 2);

            rootDirectoryRow.Children.Add(rootDirectoryTextBox);
            rootDirectoryRow.Children.Add(selectFolderButton);

            StackPanel buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                Children =
                {
                    cancelButton,
                    createButton
                }
            };

            StackPanel contentPanel = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 10,
                Children =
                {
                    projectNameLabel,
                    projectNameTextBox,
                    rootDirectoryLabel,
                    rootDirectoryRow,
                    templateLabel,
                    templateComboBox,
                    templateDescriptionTextBlock,
                    buttonPanel
                }
            };

            dialog.Content = contentPanel;

            return await dialog.ShowDialog<CreateProjectDialogResult?>(this);
        }

        private CreateProjectValidationResult ValidateCreateProjectInput(
            string? projectName,
            string? rootDirectoryPath)
        {
            string normalizedProjectName = (projectName ?? string.Empty).Trim();
            string normalizedRootDirectoryPath = (rootDirectoryPath ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(normalizedProjectName))
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "工程名称不能为空"
                };
            }

            char[] invalidNameChars = Path.GetInvalidFileNameChars();
            if (normalizedProjectName.IndexOfAny(invalidNameChars) >= 0)
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "工程名称包含非法字符"
                };
            }

            if (normalizedProjectName == "." || normalizedProjectName == "..")
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "工程名称不合法"
                };
            }

            if (string.IsNullOrWhiteSpace(normalizedRootDirectoryPath))
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "根目录所在文件夹不能为空"
                };
            }

            string fullRootDirectoryPath;
            try
            {
                fullRootDirectoryPath = Path.GetFullPath(normalizedRootDirectoryPath);
            }
            catch
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "根目录所在文件夹路径不合法"
                };
            }

            if (!Directory.Exists(fullRootDirectoryPath))
            {
                return new CreateProjectValidationResult
                {
                    IsValid = false,
                    Message = "根目录所在文件夹不存在"
                };
            }

            return new CreateProjectValidationResult
            {
                IsValid = true,
                Message = ""
            };
        }

        private async Task ShowSimpleWarningDialogAsync(string title, string message)
        {
            Window dialog = new Window
            {
                Title = title,
                Width = 360,
                Height = 120,
                CanResize = false,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111"))
            };

            TextBlock messageText = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
            };

            Button okButton = new Button
            {
                Content = "确定",
                Width = 88,
                Height = 32,
                MinWidth = 88,
                MinHeight = 32,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            okButton.Click += (_, _) =>
            {
                dialog.Close();
            };

            Grid panel = new Grid
            {
                Margin = new Thickness(16),
                RowDefinitions = new RowDefinitions("*,Auto")
            };

            Grid.SetRow(messageText, 0);
            Grid.SetRow(okButton, 1);

            panel.Children.Add(messageText);
            panel.Children.Add(okButton);

            dialog.Content = panel;

            await dialog.ShowDialog(this);
        }

        private void TryOpenFolderWithSystem(string folderPath)
        {
            try
            {
                string fullPath = Path.GetFullPath(folderPath);

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "\"" + fullPath + "\"",
                        UseShellExecute = true
                    });
                    return;
                }

                if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = "\"" + fullPath + "\"",
                        UseShellExecute = false
                    });
                    return;
                }

                if (OperatingSystem.IsLinux())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = "\"" + fullPath + "\"",
                        UseShellExecute = false
                    });
                }
            }
            catch
            {
            }
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
            UpdatePlaybackButtonsVisualState();
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

            _resourceExplorerPanel = panel;

            foreach (string childDirectory in directories)
                panel.Children.Add(CreateResourceIconItem(childDirectory, true));

            foreach (string childFile in files)
                panel.Children.Add(CreateResourceIconItem(childFile, false));

            Border blankArea = new Border
            {
                Background = Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = panel
            };

            blankArea.PointerPressed += (_, e) =>
            {
                if (e.Source == blankArea || e.Source == panel)
                    ClearSelectedResourceItem();
            };

            blankArea.ContextMenu = CreateResourceExplorerBlankContextMenu();

            Control content;

            if (panel.Children.Count == 0)
            {
                Border emptyArea = new Border
                {
                    Background = Brushes.Transparent,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = CreatePlaceholder("该目录为空")
                };

                emptyArea.PointerPressed += (_, _) => ClearSelectedResourceItem();
                emptyArea.ContextMenu = CreateResourceExplorerBlankContextMenu();
                content = emptyArea;
            }
            else
            {
                content = new ScrollViewer
                {
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = blankArea
                };
            }

            BottomDockSlot.Content = CreateResourceExplorerContent(content);
        }

        private Control CreateResourceIconItem(string path, bool isDirectory)
        {
            string name = Path.GetFileName(path);
            ResourceIconKind iconKind = ResolveResourceIconKind(path, isDirectory);

            TextBlock nameTextBlock = new TextBlock
            {
                Text = name,
                Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 86,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsVisible = true
            };

            TextBox nameTextBox = new TextBox
            {
                Text = name,
                Width = 86,
                Height = 24,
                MinHeight = 24,
                Padding = new Thickness(4, 1, 4, 1),
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                Foreground = Brushes.White,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsVisible = false
            };

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
                new ResourceGlyphIcon
                {
                    Kind = iconKind,
                    Width = 80,
                    Height = 80,
                    Margin = new Thickness(0, 0, 0, 2),
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                nameTextBlock,
                nameTextBox
            }
                }
            };

            ResourceItemState state = new ResourceItemState(item, path, isDirectory, nameTextBlock, nameTextBox);
            item.Tag = state;
            nameTextBlock.Tag = state;
            nameTextBox.Tag = state;
            UpdateResourceItemVisual(state);

            item.ContextMenu = CreateResourceItemContextMenu(state);

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

            item.PointerPressed += (_, e) =>
            {
                SelectResourceItem(state);
                state.IsPressed = true;
                UpdateResourceItemVisual(state);

                PointerPoint point = e.GetCurrentPoint(item);
                if (point.Properties.PointerUpdateKind == PointerUpdateKind.RightButtonPressed)
                    e.Handled = true;
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

            nameTextBlock.PointerPressed += (_, e) =>
            {
                SelectResourceItem(state);

                PointerPoint point = e.GetCurrentPoint(nameTextBlock);
                if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
                    return;

                DateTime now = DateTime.UtcNow;
                if (string.Equals(_lastResourceNameClickPath, state.Path, StringComparison.Ordinal) &&
                    (now - _lastResourceNameClickUtc).TotalMilliseconds >= 350 &&
                    (now - _lastResourceNameClickUtc).TotalMilliseconds <= 900)
                {
                    _lastResourceNameClickUtc = DateTime.MinValue;
                    _lastResourceNameClickPath = null;
                    _ = BeginRenameResourceAsync(state);
                    e.Handled = true;
                    return;
                }

                _lastResourceNameClickUtc = now;
                _lastResourceNameClickPath = state.Path;
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

                Border rootBorder = new Border
                {
                    Background = Brushes.Transparent,
                    Child = content
                };

                rootBorder.PointerPressed += (_, e) =>
                {
                    if (e.Source == rootBorder)
                        ClearSelectedResourceItem();
                };

                rootBorder.ContextMenu = CreateResourceExplorerBlankContextMenu();
                _resourceExplorerRootBorder = rootBorder;

                Grid.SetRow(topBar, 0);
                Grid.SetRow(rootBorder, 1);

                grid.Children.Add(topBar);
                grid.Children.Add(rootBorder);
                return grid;
            }

            grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));

            Border bodyBorder = new Border
            {
                Background = Brushes.Transparent,
                Child = content
            };

            bodyBorder.PointerPressed += (_, e) =>
            {
                if (e.Source == bodyBorder)
                    ClearSelectedResourceItem();
            };

            bodyBorder.ContextMenu = CreateResourceExplorerBlankContextMenu();
            _resourceExplorerRootBorder = bodyBorder;

            Grid.SetRow(bodyBorder, 0);
            grid.Children.Add(bodyBorder);
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

        private void ClearSelectedResourceItem()
        {
            ResourceItemState? previous = _selectedResourceItemState;
            _selectedResourceItemState = null;

            if (previous != null)
                UpdateResourceItemVisual(previous);

            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");
        }

        private ResourceItemState? FindResourceItemStateByPath(string path)
        {
            if (_resourceExplorerPanel == null)
                return null;

            string fullPath = Path.GetFullPath(path);

            foreach (Control child in _resourceExplorerPanel.Children)
            {
                if (child is Border border &&
                    border.Tag is ResourceItemState state &&
                    PathsEqual(state.Path, fullPath))
                    return state;
            }

            return null;
        }

        private void BeginRenameResourceByPath(string path)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                ResourceItemState? state = FindResourceItemStateByPath(path);
                if (state == null)
                    return;

                SelectResourceItem(state);
                await BeginRenameResourceAsync(state);
            }, DispatcherPriority.Background);
        }

        private bool IsValidResourceName(string name)
        {
            string normalized = (name ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            if (normalized == "." || normalized == "..")
                return false;

            return normalized.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        private Task<bool> TryRenameResourceAsync(ResourceItemState state, string newName)
        {
            string normalized = (newName ?? string.Empty).Trim();

            if (!IsValidResourceName(normalized))
                return Task.FromResult(false);

            try
            {
                string? parentPath = Path.GetDirectoryName(state.Path);
                if (string.IsNullOrWhiteSpace(parentPath))
                    return Task.FromResult(false);

                string targetPath;

                if (state.IsDirectory)
                {
                    targetPath = Path.Combine(parentPath, normalized);
                }
                else
                {
                    string extension = Path.GetExtension(state.Path);
                    targetPath = Path.Combine(parentPath, normalized + extension);
                }

                if (!PathsEqual(state.Path, targetPath) &&
                    (Directory.Exists(targetPath) || File.Exists(targetPath)))
                    return Task.FromResult(false);

                if (state.IsDirectory)
                    Directory.Move(state.Path, targetPath);
                else
                    File.Move(state.Path, targetPath);

                if (!string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                    ShowResourceDirectory(_currentResourceDirectoryPath);

                TrySelectProjectTreePath(targetPath);
                ShowResourceDetailsInViewer(targetPath, state.IsDirectory);
                return Task.FromResult(true);
            }
            catch
            {
                return Task.FromResult(false);
            }
        }

        private Task BeginRenameResourceAsync(ResourceItemState state)
        {
            state.NameTextBlock.IsVisible = false;
            state.NameTextBox.Text = state.IsDirectory
                ? Path.GetFileName(state.Path)
                : Path.GetFileNameWithoutExtension(state.Path);
            state.NameTextBox.IsVisible = true;
            _activeResourceRenameTextBox = state.NameTextBox;
            state.NameTextBox.Focus();
            state.NameTextBox.SelectAll();

            async Task CommitAsync()
            {
                bool success = await TryRenameResourceAsync(state, state.NameTextBox.Text ?? string.Empty);
                state.NameTextBox.IsVisible = false;
                state.NameTextBlock.IsVisible = true;

                if (ReferenceEquals(_activeResourceRenameTextBox, state.NameTextBox))
                    _activeResourceRenameTextBox = null;

                if (!success && !string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                    ShowResourceDirectory(_currentResourceDirectoryPath);
            }

            void Cancel()
            {
                state.NameTextBox.IsVisible = false;
                state.NameTextBlock.IsVisible = true;

                if (ReferenceEquals(_activeResourceRenameTextBox, state.NameTextBox))
                    _activeResourceRenameTextBox = null;
            }

            void OnLostFocus(object? sender, RoutedEventArgs e)
            {
                state.NameTextBox.LostFocus -= OnLostFocus;
                _ = CommitAsync();
            }

            void OnKeyDown(object? sender, KeyEventArgs e)
            {
                if (e.Key == Key.Enter)
                {
                    state.NameTextBox.KeyDown -= OnKeyDown;
                    state.NameTextBox.LostFocus -= OnLostFocus;
                    _ = CommitAsync();
                    e.Handled = true;
                    return;
                }

                if (e.Key == Key.Escape)
                {
                    state.NameTextBox.KeyDown -= OnKeyDown;
                    state.NameTextBox.LostFocus -= OnLostFocus;
                    Cancel();
                    e.Handled = true;
                }
            }

            state.NameTextBox.LostFocus += OnLostFocus;
            state.NameTextBox.KeyDown += OnKeyDown;
            return Task.CompletedTask;
        }

        private async Task<bool> ShowDeleteResourceDialogAsync(ResourceItemState state)
        {
            string fileName = Path.GetFileName(state.Path);
            bool confirmed = false;

            Window dialog = new Window
            {
                Title = "删除警告",
                Width = 360,
                Height = 120,
                CanResize = false,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111"))
            };

            TextBlock nameLine = new TextBlock
            {
                Text = fileName,
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };

            TextBlock questionLine = new TextBlock
            {
                Text = "是否确认删除？",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18),
                TextWrapping = TextWrapping.Wrap
            };

            Button confirmButton = new Button
            {
                Content = "确定",
                Width = 88,
                Height = 32,
                MinWidth = 88,
                MinHeight = 32,
                Padding = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            Button cancelButton = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                MinWidth = 88,
                MinHeight = 32,
                Padding = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            confirmButton.Click += (_, _) =>
            {
                confirmed = true;
                dialog.Close();
            };

            cancelButton.Click += (_, _) =>
            {
                dialog.Close();
            };

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Spacing = 12,
                Children =
                {
                    confirmButton,
                    cancelButton
                }
            };

            StackPanel content = new StackPanel
            {
                Margin = new Thickness(16),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    nameLine,
                    questionLine,
                    buttons
                }
            };

            dialog.Content = content;

            await dialog.ShowDialog(this);
            return confirmed;
        }

        private async Task DeleteSelectedResourceItemAsync()
        {
            if (_selectedResourceItemState == null)
                return;

            ResourceItemState state = _selectedResourceItemState;
            string? currentDirectoryPath = _currentResourceDirectoryPath;

            if (!await ShowDeleteResourceDialogAsync(state))
                return;

            try
            {
                if (state.IsDirectory)
                    Directory.Delete(state.Path, true);
                else
                    File.Delete(state.Path);

                ClearSelectedResourceItem();

                if (!string.IsNullOrWhiteSpace(currentDirectoryPath))
                    ShowResourceDirectory(currentDirectoryPath);
            }
            catch (Exception ex)
            {
                await ShowSimpleWarningDialogAsync("警告", ex.Message);
            }
        }

        private void OpenResourceInSystemExplorer(string path)
        {
            try
            {
                string fullPath = Path.GetFullPath(path);

                if (OperatingSystem.IsWindows())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = "/select,\"" + fullPath + "\"",
                        UseShellExecute = true
                    });
                    return;
                }

                if (OperatingSystem.IsMacOS())
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = "-R \"" + fullPath + "\"",
                        UseShellExecute = false
                    });
                    return;
                }

                if (OperatingSystem.IsLinux())
                {
                    string? directoryPath = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrWhiteSpace(directoryPath))
                        TryOpenFolderWithSystem(directoryPath);
                }
            }
            catch
            {
            }
        }

        private Task CreateNewResourceAsync(string kind)
        {
            if (string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                return Task.CompletedTask;
            string directoryPath = _currentResourceDirectoryPath;
            string baseName;
            string fileName;
            string content;
            string createdPath;

            switch (kind)
            {
                case "文件夹":
                    baseName = "新建文件夹";
                    fileName = GetUniqueResourceName(directoryPath, baseName, null);
                    createdPath = Path.Combine(directoryPath, fileName);
                    Directory.CreateDirectory(createdPath);
                    ShowResourceDirectory(directoryPath);
                    BeginRenameResourceByPath(createdPath);
                    return Task.CompletedTask;
                case "Lua脚本":
                    baseName = "NewLuaScript";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".lua");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = @"-- init is called in the first frame
function init()

end

-- loop is called recursively in each frame
function loop()

end
";
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                case "顶点着色器":
                    baseName = "NewVertexShader";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".vert");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = @"#version 430 core

layout(location = 0) in vec3 aPos;

void main()
{
    gl_Position = vec4(aPos, 1.0);
}
";
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                case "片元着色器":
                    baseName = "NewFragmentShader";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".frag");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = @"#version 430 core

layout(location = 0) out vec4 FragColor;

void main()
{
    FragColor = vec4(1.0, 1.0, 1.0, 1.0);
}
";
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                case "材质":
                    baseName = "NewMaterial";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".json");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = JsonSerializer.Serialize(new
                    {
                        assetType = "Material"
                    }, SceneJsonWriteOptions);
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                case "场景":
                    baseName = "NewScene";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".json");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = JsonSerializer.Serialize(new SceneData
                    {
                        SceneId = Guid.NewGuid().ToString("N"),
                        Objects = new List<SceneObject>()
                    }, SceneJsonWriteOptions);
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                case "画布":
                    baseName = "NewCanvas";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".json");
                    createdPath = Path.Combine(directoryPath, fileName);
                    content = "{}";
                    File.WriteAllText(createdPath, content, Encoding.UTF8);
                    break;

                default:
                    return Task.CompletedTask;
            }

            ShowResourceDirectory(directoryPath);
            BeginRenameResourceByPath(createdPath);
            return Task.CompletedTask;
        }

        private string GetUniqueResourceName(string directoryPath, string baseName, string? extension)
        {
            string candidate = extension == null ? baseName : baseName + extension;
            int index = 1;

            while (Directory.Exists(Path.Combine(directoryPath, candidate)) || File.Exists(Path.Combine(directoryPath, candidate)))
            {
                candidate = extension == null
                    ? baseName + index.ToString(CultureInfo.InvariantCulture)
                    : baseName + index.ToString(CultureInfo.InvariantCulture) + extension;
                index++;
            }

            return candidate;
        }

        private ContextMenu CreateResourceItemContextMenu(ResourceItemState state)
        {
            MenuItem openItem = new MenuItem { Header = "打开" };
            MenuItem revealItem = new MenuItem { Header = "查看文件" };
            MenuItem renameItem = new MenuItem { Header = "重命名" };
            MenuItem deleteItem = new MenuItem { Header = "删除" };

            openItem.Click += (_, _) =>
            {
                SelectResourceItem(state);
                OnResourceItemDoubleTapped(state.Path, state.IsDirectory);
            };

            revealItem.Click += (_, _) =>
            {
                SelectResourceItem(state);
                OpenResourceInSystemExplorer(state.Path);
            };

            renameItem.Click += async (_, _) =>
            {
                SelectResourceItem(state);
                await BeginRenameResourceAsync(state);
            };

            deleteItem.Click += async (_, _) =>
            {
                SelectResourceItem(state);
                await DeleteSelectedResourceItemAsync();
            };

            return new ContextMenu
            {
                ItemsSource = new object[]
                {
                    openItem,
                    revealItem,
                    renameItem,
                    deleteItem
                }
            };
        }

        private ContextMenu CreateResourceExplorerBlankContextMenu()
        {
            MenuItem newFolderItem = new MenuItem { Header = "文件夹" };
            MenuItem newLuaItem = new MenuItem { Header = "Lua脚本" };
            MenuItem newVertexShaderItem = new MenuItem { Header = "顶点着色器" };
            MenuItem newFragmentShaderItem = new MenuItem { Header = "片元着色器" };
            MenuItem newShaderItem = new MenuItem
            {
                Header = "着色器",
                ItemsSource = new object[]
                {
                    newVertexShaderItem,
                    newFragmentShaderItem
                }
            };
            MenuItem newMaterialItem = new MenuItem { Header = "材质" };
            MenuItem newSceneItem = new MenuItem { Header = "场景" };
            MenuItem newCanvasItem = new MenuItem { Header = "画布" };
            MenuItem newItem = new MenuItem
            {
                Header = "新建",
                ItemsSource = new object[]
                {
                    newFolderItem,
                    newLuaItem,
                    newShaderItem,
                    newMaterialItem,
                    newSceneItem,
                    newCanvasItem
                }
            };
            MenuItem revealItem = new MenuItem { Header = "查看文件" };

            newFolderItem.Click += async (_, _) => await CreateNewResourceAsync("文件夹");
            newLuaItem.Click += async (_, _) => await CreateNewResourceAsync("Lua脚本");
            newVertexShaderItem.Click += async (_, _) => await CreateNewResourceAsync("顶点着色器");
            newFragmentShaderItem.Click += async (_, _) => await CreateNewResourceAsync("片元着色器");
            newMaterialItem.Click += async (_, _) => await CreateNewResourceAsync("材质");
            newSceneItem.Click += async (_, _) => await CreateNewResourceAsync("场景");
            newCanvasItem.Click += async (_, _) => await CreateNewResourceAsync("画布");

            revealItem.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                    TryOpenFolderWithSystem(_currentResourceDirectoryPath);
            };

            return new ContextMenu
            {
                ItemsSource = new object[]
                {
            newItem,
            revealItem
                }
            };
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

        private ResourceIconKind ResolveResourceIconKind(string path, bool isDirectory)
        {
            if (isDirectory)
                return ResourceIconKind.Folder;

            string extension = Path.GetExtension(path).Trim().ToLowerInvariant();

            switch (extension)
            {
                case ".lua":
                    return ResourceIconKind.LuaFile;

                case ".frag":
                    return ResourceIconKind.FragFile;

                case ".vert":
                    return ResourceIconKind.VertFile;

                case ".wav":
                case ".ogg":
                case ".mp3":
                    return ResourceIconKind.AudioFile;

                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".gif":
                case ".webp":
                case ".tga":
                    return ResourceIconKind.ImageFile;

                case ".json":
                    return ResolveJsonResourceIconKind(path);

                default:
                    return ResourceIconKind.GenericFile;
            }
        }

        private ResourceIconKind ResolveJsonResourceIconKind(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                    return ResourceIconKind.JsonFile;

                bool hasSceneId =
                    root.TryGetProperty("sceneId", out JsonElement sceneIdElement) &&
                    sceneIdElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(sceneIdElement.GetString());

                bool hasObjects =
                    root.TryGetProperty("objects", out JsonElement objectsElement) &&
                    objectsElement.ValueKind == JsonValueKind.Array;

                if (hasSceneId && hasObjects)
                    return ResourceIconKind.SceneJsonFile;

                if (root.TryGetProperty("assetType", out JsonElement assetTypeElement) &&
                    assetTypeElement.ValueKind == JsonValueKind.String &&
                    string.Equals(assetTypeElement.GetString(), "Material", StringComparison.OrdinalIgnoreCase))
                    return ResourceIconKind.MaterialJsonFile;

                return ResourceIconKind.JsonFile;
            }
            catch
            {
                return ResourceIconKind.JsonFile;
            }
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
                ClearSelectedSceneObjectState();
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
            if (_sceneTreeView == null)
                return false;

            if (_sceneTreeView.ItemsSource is not System.Collections.IEnumerable items)
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

        private void ClearSelectedSceneObjectState()
        {
            _selectedSceneObject = null;
            ClearPreviewSelectionContour();

            if (_sceneTreeDeleteButton != null)
                _sceneTreeDeleteButton.IsEnabled = false;
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
            double renderPlaneOffsetX = 0.0;
            double renderPlaneOffsetY = 0.0;
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

                    if (root.TryGetProperty("renderPlaneOffset", out JsonElement renderPlaneOffsetElement) &&
                        renderPlaneOffsetElement.ValueKind == JsonValueKind.Object)
                    {
                        if (renderPlaneOffsetElement.TryGetProperty("x", out JsonElement renderPlaneOffsetXElement) &&
                            renderPlaneOffsetXElement.ValueKind == JsonValueKind.Number)
                            renderPlaneOffsetX = renderPlaneOffsetXElement.GetDouble();

                        if (renderPlaneOffsetElement.TryGetProperty("y", out JsonElement renderPlaneOffsetYElement) &&
                            renderPlaneOffsetYElement.ValueKind == JsonValueKind.Number)
                            renderPlaneOffsetY = renderPlaneOffsetYElement.GetDouble();
                    }

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
                renderPlaneOffset = new
                {
                    x = renderPlaneOffsetX,
                    y = renderPlaneOffsetY
                },
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

                ClearSelectedSceneObjectState();
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

            SceneTreeDragOverlay overlay = new SceneTreeDragOverlay();

            Grid treeContainer = new Grid();
            treeContainer.Children.Add(treeView);
            treeContainer.Children.Add(overlay);

            _sceneTreeView = treeView;
            _sceneTreeDragOverlay = overlay;

            treeView.Classes.Add("scene-tree");
            treeView.SelectionChanged += OnSceneTreeSelectionChanged;
            treeView.AddHandler(InputElement.PointerPressedEvent, OnSceneTreePointerPressed, RoutingStrategies.Tunnel, true);
            treeView.AddHandler(InputElement.PointerMovedEvent, OnSceneTreePointerMoved, RoutingStrategies.Tunnel, true);
            treeView.AddHandler(InputElement.PointerReleasedEvent, OnSceneTreePointerReleased, RoutingStrategies.Tunnel, true);
            treeView.AddHandler(InputElement.PointerCaptureLostEvent, OnSceneTreePointerCaptureLost, RoutingStrategies.Tunnel, true);

            TextBox searchBox = new TextBox
            {
                Watermark = "搜索节点",
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(8, 1, 8, 1),
                Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            Button addButton = new Button
            {
                Content = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text = "+",
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, -4, 0, 0)
                    }
                },
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3)
            };

            Button deleteButton = new Button
            {
                Content = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Child = new TextBlock
                    {
                        Text = "×",
                        FontSize = 20,
                        FontWeight = FontWeight.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextAlignment = TextAlignment.Center,
                        Margin = new Thickness(0, -4, 0, 0)
                    }
                },
                Width = 28,
                Height = 28,
                MinWidth = 28,
                MinHeight = 28,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#2A2A2A")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                IsEnabled = _selectedSceneObject != null
            };

            _sceneTreeSearchTextBox = searchBox;
            _sceneTreeAddButton = addButton;
            _sceneTreeDeleteButton = deleteButton;

            searchBox.KeyDown += OnSceneTreeSearchBoxKeyDown;
            addButton.Click += OnSceneTreeAddButtonClick;
            deleteButton.Click += OnSceneTreeDeleteButtonClick;

            Grid bottomBar = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,Auto"),
                Margin = new Thickness(8),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(searchBox, 0);
            Grid.SetColumn(addButton, 2);
            Grid.SetColumn(deleteButton, 4);

            bottomBar.Children.Add(searchBox);
            bottomBar.Children.Add(addButton);
            bottomBar.Children.Add(deleteButton);

            Border bottomBarHost = new Border
            {
                Height = 44,
                MinHeight = 44,
                Background = new SolidColorBrush(Color.Parse("#181818")),
                BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = bottomBar
            };

            Grid root = new Grid();
            root.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            root.RowDefinitions.Add(new RowDefinition(44, GridUnitType.Pixel));

            Grid.SetRow(treeContainer, 0);
            Grid.SetRow(bottomBarHost, 1);

            root.Children.Add(treeContainer);
            root.Children.Add(bottomBarHost);

            return root;
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

        private void OnSceneTreeSearchBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            ExecuteSceneTreeSearch();
            e.Handled = true;
        }

        private void ExecuteSceneTreeSearch()
        {
            if (_currentTreeScene == null || _sceneTreeSearchTextBox == null)
                return;

            string raw = (_sceneTreeSearchTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(raw))
                return;

            SceneObject? matched;

            if (raw.Length >= 2 && raw[0] == '[' && raw[^1] == ']')
            {
                string id = raw.Substring(1, raw.Length - 2).Trim();
                if (string.IsNullOrWhiteSpace(id))
                    return;

                matched = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Id, id, StringComparison.Ordinal));
            }
            else
            {
                matched = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Name ?? string.Empty, raw, StringComparison.Ordinal));
            }

            if (matched == null)
                return;

            _selectedSceneObject = matched;
            TrySelectSceneObjectInTree(matched);
            ShowSceneObjectInspector(matched);

            if (_sceneTreeDeleteButton != null)
                _sceneTreeDeleteButton.IsEnabled = true;
        }

        private void OnSceneTreeAddButtonClick(object? sender, RoutedEventArgs e)
        {
            AddSceneTreeObject();
        }

        private void AddSceneTreeObject()
        {
            if (_currentTreeScene == null)
                return;

            SceneObject newObject = CreateNewSceneTreeObject();

            BeginSceneParameterChange();

            if (_selectedSceneObject == null)
            {
                newObject.Transform.ParentId = null;
                _currentTreeScene.Objects.Add(newObject);
            }
            else
            {
                string? parentId = _selectedSceneObject.Transform?.ParentId;
                newObject.Transform.ParentId = string.IsNullOrWhiteSpace(parentId) ? null : parentId;

                int selectedIndex = _currentTreeScene.Objects.IndexOf(_selectedSceneObject);
                int insertIndex = selectedIndex < 0
                    ? _currentTreeScene.Objects.Count
                    : selectedIndex + GetSceneSubtreeSpan(_selectedSceneObject);

                if (insertIndex < 0)
                    insertIndex = 0;

                if (insertIndex > _currentTreeScene.Objects.Count)
                    insertIndex = _currentTreeScene.Objects.Count;

                _currentTreeScene.Objects.Insert(insertIndex, newObject);
            }

            PersistSceneObjectChanges(newObject, true);
        }

        private SceneObject CreateNewSceneTreeObject()
        {
            return new SceneObject
            {
                Id = GenerateNewSceneTreeObjectId(),
                Name = "New Object",
                Tags = new List<string>(),
                Active = true,
                Transform = new SceneTransform
                {
                    ParentId = null,
                    LocalPosition = Double3.Zero,
                    LocalRotation = Double3.Zero,
                    LocalScale = Double3.One
                },
                Type = "Object",
                Controller = null,
                Data = "",
                Mesh = null,
                Visible = true,
                RenderTag = "MainCamera",
                Physics = null,
                Materials = null
            };
        }

        private string GenerateNewSceneTreeObjectId()
        {
            if (_currentTreeScene == null)
                return "object_0";

            int index = 0;

            while (true)
            {
                string id = "object_" + index.ToString(CultureInfo.InvariantCulture);

                bool exists = _currentTreeScene.Objects.Any(
                    o => string.Equals(o.Id, id, StringComparison.Ordinal));

                if (!exists)
                    return id;

                index++;
            }
        }

        private async void OnSceneTreeDeleteButtonClick(object? sender, RoutedEventArgs e)
        {
            await DeleteSelectedSceneTreeObjectAsync();
        }

        private async Task DeleteSelectedSceneTreeObjectAsync()
        {
            if (_currentTreeScene == null || _selectedSceneObject == null)
                return;

            SceneObject target = _selectedSceneObject;

            Window dialog = new Window
            {
                Title = "删除警告",
                Width = 360,
                Height = 120,
                CanResize = false,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111"))
            };

            TextBlock nameLine = new TextBlock
            {
                Text = $"{target.Name}[id: {target.Id}]",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap
            };

            TextBlock questionLine = new TextBlock
            {
                Text = "是否确认删除？",
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18),
                TextWrapping = TextWrapping.Wrap
            };

            Button confirmButton = new Button
            {
                Content = "确定",
                Width = 88,
                Height = 32,
                MinWidth = 88,
                MinHeight = 32,
                Padding = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            Button cancelButton = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 32,
                MinWidth = 88,
                MinHeight = 32,
                Padding = new Thickness(0, 0, 0, 1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            confirmButton.Click += (_, _) => dialog.Close(true);
            cancelButton.Click += (_, _) => dialog.Close(false);

            StackPanel root = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,16,Auto"),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            Grid.SetColumn(confirmButton, 0);
            Grid.SetColumn(cancelButton, 2);

            buttons.Children.Add(confirmButton);
            buttons.Children.Add(cancelButton);

            root.Children.Add(nameLine);
            root.Children.Add(questionLine);
            root.Children.Add(buttons);

            dialog.Content = root;

            bool? confirmed = await dialog.ShowDialog<bool?>(this);

            if (confirmed != true)
                return;

            BeginSceneParameterChange();

            string deletedId = target.Id;
            List<SceneObject> toRemove = new List<SceneObject> { target };
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(deletedId);

            while (queue.Count > 0)
            {
                string currentId = queue.Dequeue();

                List<SceneObject> children = _currentTreeScene.Objects
                    .Where(o => string.Equals(o.Transform?.ParentId, currentId, StringComparison.Ordinal))
                    .ToList();

                foreach (SceneObject child in children)
                {
                    if (toRemove.Contains(child))
                        continue;

                    toRemove.Add(child);
                    queue.Enqueue(child.Id);
                }
            }

            foreach (SceneObject obj in toRemove)
                _currentTreeScene.Objects.Remove(obj);

            _selectedSceneObject = null;
            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");

            if (_sceneTreeDeleteButton != null)
                _sceneTreeDeleteButton.IsEnabled = false;

            PersistSceneObjectChanges(target, true);
        }

        private void OnSceneTreePointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_sceneTreeView == null)
                return;

            PointerPoint point = e.GetCurrentPoint(_sceneTreeView);
            if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
                return;

            if (IsSceneTreeExpanderSource(e.Source))
            {
                ClearSceneTreeDragState();
                return;
            }

            TreeViewItem? item = FindSceneTreeItemFromSource(e.Source);

            if (item == null)
            {
                ClearSceneTreeDragState();
                return;
            }

            if (item.Tag is not SceneObject obj)
            {
                ClearSceneTreeDragState();
                return;
            }

            _sceneTreeDragSourceObject = obj;
            _sceneTreeDragStartPoint = e.GetPosition(_sceneTreeView);
            _isSceneTreeDragging = false;
        }

        private TreeViewItem? FindSceneTreeItemFromSource(object? source)
        {
            Visual? visual = source as Visual;

            while (visual != null)
            {
                if (visual is TreeViewItem item)
                    return item;

                visual = visual.GetVisualParent();
            }

            return null;
        }

        private void OnSceneTreePointerMoved(object? sender, PointerEventArgs e)
        {
            if (_sceneTreeView == null)
                return;

            if (_sceneTreeDragSourceObject == null || _sceneTreeDragStartPoint == null)
                return;

            PointerPoint point = e.GetCurrentPoint(_sceneTreeView);
            if (!point.Properties.IsLeftButtonPressed)
            {
                ClearSceneTreeDragState();
                return;
            }

            Point current = e.GetPosition(_sceneTreeView);
            Vector delta = current - _sceneTreeDragStartPoint.Value;

            if (!_isSceneTreeDragging)
            {
                if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
                    return;

                _isSceneTreeDragging = true;
                _sceneTreeCapturedPointer = e.Pointer;
                _sceneTreeCapturedPointer.Capture(_sceneTreeView);
            }

            UpdateSceneTreeDragHighlight(current);
        }

        private void OnSceneTreePointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_sceneTreeView == null)
                return;

            if (_sceneTreeDragSourceObject == null)
            {
                ClearSceneTreeDragState();
                return;
            }

            PointerPoint point = e.GetCurrentPoint(_sceneTreeView);
            if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonReleased)
            {
                ClearSceneTreeDragState();
                return;
            }

            if (!_isSceneTreeDragging)
            {
                ClearSceneTreeDragState();
                return;
            }

            Point position = e.GetPosition(_sceneTreeView);

            TreeViewItem? targetItem = FindSceneTreeItemFromSource(e.Source);

            if (targetItem == null && !TryGetSceneTreeItemAtPoint(_sceneTreeView, position, out targetItem))
            {
                ClearSceneTreeDragState();
                e.Handled = true;
                return;
            }

            if (targetItem.Tag is not SceneObject targetObject)
            {
                ClearSceneTreeDragState();
                e.Handled = true;
                return;
            }

            Point? origin = targetItem.TranslatePoint(new Point(0, 0), _sceneTreeView);
            if (origin == null)
            {
                ClearSceneTreeDragState();
                e.Handled = true;
                return;
            }

            Point pointInItem = new Point(
                position.X - origin.Value.X,
                position.Y - origin.Value.Y);

            SceneTreeDropPlacement placement = ResolveSceneTreeDropPlacement(targetItem, pointInItem);
            TryMoveSceneTreeObject(_sceneTreeDragSourceObject, targetObject, placement);

            ClearSceneTreeDragState();
            e.Handled = true;
        }

        private void OnSceneTreePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            ClearSceneTreeDragState();
        }

        private void ClearSceneTreeDragState()
        {
            if (_sceneTreeCapturedPointer != null)
            {
                _sceneTreeCapturedPointer.Capture(null);
                _sceneTreeCapturedPointer = null;
            }

            _sceneTreeDragOverlay?.HideHighlight();
            _sceneTreeDragSourceObject = null;
            _sceneTreeDragStartPoint = null;
            _isSceneTreeDragging = false;
        }

        private bool IsSceneTreeExpanderSource(object? source)
        {
            Visual? visual = source as Visual;

            while (visual != null)
            {
                if (visual is ToggleButton)
                    return true;

                visual = visual.GetVisualParent();
            }

            return false;
        }

        private void UpdateSceneTreeDragHighlight(Point point)
        {
            if (_sceneTreeView == null || _sceneTreeDragOverlay == null)
                return;

            if (!TryGetSceneTreeItemAtPoint(_sceneTreeView, point, out TreeViewItem item))
            {
                _sceneTreeDragOverlay.HideHighlight();
                return;
            }

            Point? origin = item.TranslatePoint(new Point(0, 0), _sceneTreeView);
            if (origin == null)
            {
                _sceneTreeDragOverlay.HideHighlight();
                return;
            }

            Rect itemRect = new Rect(origin.Value, item.Bounds.Size);
            Point pointInItem = new Point(
                point.X - origin.Value.X,
                point.Y - origin.Value.Y);

            SceneTreeDropPlacement placement = ResolveSceneTreeDropPlacement(item, pointInItem);
            Rect highlightRect = GetSceneTreeHighlightRect(itemRect, placement);

            _sceneTreeDragOverlay.ShowHighlight(highlightRect);
        }

        private Rect GetSceneTreeHighlightRect(Rect itemRect, SceneTreeDropPlacement placement)
        {
            if (placement == SceneTreeDropPlacement.Child)
                return itemRect;

            double thickness = Math.Min(6.0, Math.Max(4.0, itemRect.Height * 0.25));

            if (placement == SceneTreeDropPlacement.Before)
                return new Rect(itemRect.X, itemRect.Y, itemRect.Width, thickness);

            return new Rect(itemRect.X, itemRect.Bottom - thickness, itemRect.Width, thickness);
        }

        private bool TryGetSceneTreeItemRect(TreeViewItem item, out Rect rect)
        {
            rect = default;

            if (_sceneTreeView == null)
                return false;

            Point? origin = item.TranslatePoint(new Point(0, 0), _sceneTreeView);
            if (origin == null)
                return false;

            rect = new Rect(origin.Value, item.Bounds.Size);
            return true;
        }

        private bool TryGetSceneTreeItemAtPoint(TreeView treeView, Point point, out TreeViewItem item)
        {
            if (treeView.ItemsSource is System.Collections.IEnumerable items &&
                TryGetSceneTreeItemAtPoint(treeView, items, point, out item))
                return true;

            item = null!;
            return false;
        }

        private bool TryGetSceneTreeItemAtPoint(TreeView treeView, System.Collections.IEnumerable items, Point point, out TreeViewItem item)
        {
            foreach (object? itemObject in items)
            {
                if (itemObject is not TreeViewItem treeViewItem)
                    continue;

                if (treeViewItem.IsExpanded &&
                    treeViewItem.ItemsSource is System.Collections.IEnumerable childItems &&
                    TryGetSceneTreeItemAtPoint(treeView, childItems, point, out item))
                    return true;

                if (TryGetSceneTreeItemRect(treeViewItem, out Rect rect) && rect.Contains(point))
                {
                    item = treeViewItem;
                    return true;
                }
            }

            item = null!;
            return false;
        }

        private SceneTreeDropPlacement ResolveSceneTreeDropPlacement(TreeViewItem item, Point pointInItem)
        {
            double height = Math.Max(item.Bounds.Height, InspectorTextBoxHeight);
            double gapHeight = Math.Min(6.0, height * 0.25);
            double y = pointInItem.Y;

            if (y <= gapHeight)
                return SceneTreeDropPlacement.Before;

            if (y >= height - gapHeight)
                return SceneTreeDropPlacement.After;

            return SceneTreeDropPlacement.Child;
        }

        private bool TryMoveSceneTreeObject(SceneObject source, SceneObject target, SceneTreeDropPlacement placement)
        {
            if (_currentTreeScene == null)
                return false;

            if (ReferenceEquals(source, target))
                return false;

            source.Transform ??= CloneTransform(null);

            int sourceIndex = _currentTreeScene.Objects.IndexOf(source);
            int targetIndex = _currentTreeScene.Objects.IndexOf(target);

            if (sourceIndex < 0 || targetIndex < 0)
                return false;

            BeginSceneParameterChange();

            if (placement == SceneTreeDropPlacement.Child)
            {
                if (string.IsNullOrWhiteSpace(target.Id))
                    return false;

                if (WouldCreateParentCycle(source, target.Id))
                    return false;

                _currentTreeScene.Objects.RemoveAt(sourceIndex);

                if (sourceIndex < targetIndex)
                    targetIndex--;

                source.Transform.ParentId = target.Id;

                int insertIndex = targetIndex + GetSceneSubtreeSpan(target);

                if (insertIndex < 0)
                    insertIndex = 0;

                if (insertIndex > _currentTreeScene.Objects.Count)
                    insertIndex = _currentTreeScene.Objects.Count;

                _currentTreeScene.Objects.Insert(insertIndex, source);

                return PersistSceneObjectChanges(source, true);
            }

            string? newParentId = target.Transform?.ParentId;

            if (!string.IsNullOrWhiteSpace(newParentId))
            {
                SceneObject? parent = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Id, newParentId, StringComparison.Ordinal));

                if (parent == null)
                    newParentId = null;
            }

            if (!string.IsNullOrWhiteSpace(newParentId) && WouldCreateParentCycle(source, newParentId))
                return false;

            int targetSpan = GetSceneSubtreeSpan(target);

            _currentTreeScene.Objects.RemoveAt(sourceIndex);

            if (sourceIndex < targetIndex)
                targetIndex--;

            source.Transform.ParentId = newParentId;

            int siblingInsertIndex = placement == SceneTreeDropPlacement.Before
                ? targetIndex
                : targetIndex + targetSpan;

            if (siblingInsertIndex < 0)
                siblingInsertIndex = 0;

            if (siblingInsertIndex > _currentTreeScene.Objects.Count)
                siblingInsertIndex = _currentTreeScene.Objects.Count;

            _currentTreeScene.Objects.Insert(siblingInsertIndex, source);

            return PersistSceneObjectChanges(source, true);
        }

        private int GetSceneSubtreeSpan(SceneObject root)
        {
            if (_currentTreeScene == null)
                return 0;

            int rootIndex = _currentTreeScene.Objects.IndexOf(root);
            if (rootIndex < 0)
                return 0;

            string rootId = root.Id;
            int span = 1;

            for (int i = rootIndex + 1; i < _currentTreeScene.Objects.Count; i++)
            {
                SceneObject current = _currentTreeScene.Objects[i];
                string? parentId = current.Transform?.ParentId;
                bool isDescendant = false;

                while (!string.IsNullOrWhiteSpace(parentId))
                {
                    if (string.Equals(parentId, rootId, StringComparison.Ordinal))
                    {
                        isDescendant = true;
                        break;
                    }

                    SceneObject? parent = _currentTreeScene.Objects.FirstOrDefault(
                        o => string.Equals(o.Id, parentId, StringComparison.Ordinal));

                    parentId = parent?.Transform?.ParentId;
                }

                if (!isDescendant)
                    break;

                span++;
            }

            return span;
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
                ApplyPreviewSelectionContour(obj);
                ShowSceneObjectInspector(obj);

                if (_sceneTreeDeleteButton != null)
                    _sceneTreeDeleteButton.IsEnabled = true;
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