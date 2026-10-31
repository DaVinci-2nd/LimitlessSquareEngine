using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
using SNVec2 = System.Numerics.Vector2;
using SNVec3 = System.Numerics.Vector3;
using SNVec4 = System.Numerics.Vector4;
using SNMat4 = System.Numerics.Matrix4x4;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace LimitlessSquareEngine.Editor
{
    public sealed partial class EditorMainWindow : Window
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
        private MenuItem? _animationClipMenuItem;
        private MenuItem? _animatorControllerMenuItem;
        private readonly List<SceneData> _sceneUndoStack = new List<SceneData>();
        private readonly List<SceneData> _sceneRedoStack = new List<SceneData>();
        private bool _isApplyingSceneHistory;
        private bool _sceneHasUnsavedChanges;

        private SceneObject? _selectedSceneObject;
        private bool _isUpdatingSceneInspector;
        private readonly List<Action> _inspectorVector3RefreshActions = new();
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

        private bool _gizmoDragActive;
        private GizmoHandle _gizmoDragHandle = GizmoHandle.None;
        private int _gizmoDragType;
        private Point _gizmoDragLastMouse;
        private Double3 _gizmoDragStartLocalPos;
        private Double3 _gizmoDragStartLocalRot;
        private Double3 _gizmoDragStartLocalScale;
        private GizmoMode _currentGizmoMode = GizmoMode.Translate;

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

        private readonly List<Window> _openLuaViewers = new List<Window>();

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
            AnimationClipJsonFile,
            AnimatorControllerJsonFile,
            VrmaFile,
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
                throw new InvalidOperationException("[X] Playback window is not ready.");

            if (_playbackWindowNativeHandle == 0)
                throw new InvalidOperationException("[X] Playback native window handle is missing.");

            if (string.IsNullOrWhiteSpace(_playbackControlFilePath))
                throw new InvalidOperationException("[X] Playback control file path is missing.");

            if (string.IsNullOrWhiteSpace(_playbackStatusFilePath))
                throw new InvalidOperationException("[X] Playback status file path is missing.");

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

            throw new InvalidOperationException("[X] Unsupported playback embedding mode.");
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

            throw new FileNotFoundException("[X] Game executable not found.");
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
                if (_selectedSceneObject != null)
                {
                    GizmoHandle hit = HitTestGizmo(point.Position);
                    if (hit != GizmoHandle.None)
                    {
                        _gizmoDragActive = true;
                        _gizmoDragHandle = hit;
                        _gizmoDragLastMouse = point.Position;
                        _gizmoDragStartLocalPos = _selectedSceneObject.Transform!.LocalPosition;
                        _gizmoDragStartLocalRot = _selectedSceneObject.Transform!.LocalRotation;
                        _gizmoDragStartLocalScale = _selectedSceneObject.Transform!.LocalScale;
                        EditorHostBridge.SetGizmoDrag(true, (int)hit);
                        e.Pointer.Capture(_sceneHost);
                        e.Handled = true;
                        return;
                    }
                }

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

            if (_gizmoDragActive)
            {
                _gizmoDragActive = false;
                _gizmoDragHandle = GizmoHandle.None;
                EditorHostBridge.SetGizmoDrag(false, -1);
                if (_selectedSceneObject != null)
                {
                    BeginSceneParameterChange();
                    PersistSceneObjectChanges(_selectedSceneObject, false);
                }
                e.Handled = true;
                return;
            }

            if (point.Properties.PointerUpdateKind != PointerUpdateKind.RightButtonReleased)
                return;

            ResetSceneHostNavigationState();
            e.Handled = true;
        }

        private void OnSceneHostPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_gizmoDragActive)
            {
                Point cur = e.GetPosition(_sceneHost);
                SNVec2 screenDelta = new SNVec2(
                    (float)(cur.X - _gizmoDragLastMouse.X),
                    (float)(cur.Y - _gizmoDragLastMouse.Y));
                ApplyGizmoDrag(screenDelta);
                _gizmoDragLastMouse = cur;
                e.Handled = true;
                return;
            }

            if (!_isSceneHostRightDragging && _selectedSceneObject != null)
            {
                GizmoHandle hovered = HitTestGizmo(e.GetPosition(_sceneHost));
                EditorHostBridge.SetGizmoHover((int)hovered);
            }

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

            var wp = EditorHostBridge.GetSceneObjectWorldPosition(EditorPreviewSceneId, obj.Id);
            EditorHostBridge.SetGizmoState(wp, (int)_currentGizmoMode, (int)GizmoHandle.None, true);
        }

        private void ClearSceneHostRenderedMeshSelection()
        {
            EditorHostBridge.SetGizmoState(Double3.Zero, (int)GizmoMode.None, (int)GizmoHandle.None, false);

            ClearSelectedSceneObjectState();

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
            private static readonly IBrush AnimationClipBrush = new SolidColorBrush(Color.Parse("#8A5A1F"));
            private static readonly IBrush AnimatorControllerBrush = new SolidColorBrush(Color.Parse("#5A1F6A"));
            private static readonly IBrush VrmaBrush = new SolidColorBrush(Color.Parse("#1F5A6A"));
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

                    case ResourceIconKind.AnimationClipJsonFile:
                        DrawFileBase(context, bounds, AnimationClipBrush, true);
                        DrawAnimationClipBars(context, bounds);
                        break;

                    case ResourceIconKind.AnimatorControllerJsonFile:
                        DrawFileBase(context, bounds, AnimatorControllerBrush, true);
                        DrawAnimatorStateNodes(context, bounds);
                        break;

                    case ResourceIconKind.VrmaFile:
                        DrawFileBase(context, bounds, VrmaBrush, true);
                        DrawVrmaBones(context, bounds);
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

            private static void DrawAnimationClipBars(DrawingContext context, Rect bounds)
            {
                double boxSize = Math.Min(bounds.Width, bounds.Height);
                double rowHeight = boxSize * 0.06;
                double gap = boxSize * 0.05;
                double totalHeight = rowHeight * 3.0 + gap * 2.0;
                double left = bounds.X + boxSize * 0.24;
                double top = bounds.Y + (bounds.Height - totalHeight) / 2.0;

                double[] lengths = { 0.46, 0.30, 0.40 };

                for (int i = 0; i < lengths.Length; i++)
                {
                    double width = boxSize * lengths[i];
                    Rect bar = new Rect(left, top + i * (rowHeight + gap), width, rowHeight);
                    context.DrawRectangle(WhiteBrush, null, bar);
                }
            }

            private static void DrawAnimatorStateNodes(DrawingContext context, Rect bounds)
            {
                double boxSize = Math.Min(bounds.Width, bounds.Height);
                double nodeWidth = boxSize * 0.30;
                double nodeHeight = boxSize * 0.22;
                double left = bounds.X + boxSize * 0.24;
                double top = bounds.Y + boxSize * 0.26;
                double offset = boxSize * 0.22;

                Rect first = new Rect(left, top, nodeWidth, nodeHeight);
                Rect second = new Rect(left + offset, top + offset, nodeWidth, nodeHeight);

                context.DrawRectangle(WhiteBrush, null, first);
                context.DrawRectangle(WhiteBrush, null, second);

                Pen linkPen = new Pen(WhiteBrush, Math.Max(1.0, boxSize * 0.03));

                context.DrawLine(
                    linkPen,
                    new Point(first.Right, first.Center.Y),
                    new Point(second.Left, second.Center.Y));
            }

            private static void DrawVrmaBones(DrawingContext context, Rect bounds)
            {
                double boxSize = Math.Min(bounds.Width, bounds.Height);
                double radius = Math.Max(1.0, boxSize * 0.05);
                double centerX = bounds.X + boxSize * 0.5;

                Point head = new Point(centerX, bounds.Y + boxSize * 0.24);
                Point chest = new Point(centerX, bounds.Y + boxSize * 0.46);
                Point hip = new Point(centerX, bounds.Y + boxSize * 0.66);

                Point leftHand = new Point(bounds.X + boxSize * 0.24, bounds.Y + boxSize * 0.42);
                Point rightHand = new Point(bounds.X + boxSize * 0.76, bounds.Y + boxSize * 0.42);

                Point leftFoot = new Point(bounds.X + boxSize * 0.36, bounds.Y + boxSize * 0.90);
                Point rightFoot = new Point(bounds.X + boxSize * 0.64, bounds.Y + boxSize * 0.90);

                Pen bonePen = new Pen(WhiteBrush, Math.Max(1.0, boxSize * 0.045));

                context.DrawLine(bonePen, head, chest);
                context.DrawLine(bonePen, chest, hip);
                context.DrawLine(bonePen, chest, leftHand);
                context.DrawLine(bonePen, chest, rightHand);
                context.DrawLine(bonePen, hip, leftFoot);
                context.DrawLine(bonePen, hip, rightFoot);

                context.DrawEllipse(WhiteBrush, null, head, radius, radius);
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
            overlayRoot.Children.Add(CreateGizmoModeButtons());

            return new Border
            {
                Background = new SolidColorBrush(Color.Parse("#111111")),
                Padding = new Thickness(0),
                Child = overlayRoot
            };
        }

        private Control CreateGizmoModeButtons()
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0),
                ZIndex = 10
            };

            panel.Children.Add(CreateGizmoModeButton("▸", "选择", GizmoMode.None));
            panel.Children.Add(CreateGizmoModeButton("↔", "位移", GizmoMode.Translate));
            panel.Children.Add(CreateGizmoModeButton("⟳", "旋转", GizmoMode.Rotate));
            panel.Children.Add(CreateGizmoModeButton("◻", "缩放", GizmoMode.Scale));
            panel.Children.Add(CreateGizmoModeButton("☐", "变换", GizmoMode.Transform));

            RefreshGizmoModeButtonHighlights(panel);

            return panel;
        }

        private Button CreateGizmoModeButton(string text, string toolTip, GizmoMode mode)
        {
            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 14,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 28,
                Height = 28,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#333333")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                Tag = mode
            };

            ToolTip.SetTip(btn, new TextBlock { Text = toolTip, Foreground = Brushes.White, FontSize = 11 });
            btn.Click += OnGizmoModeButtonClick;

            return btn;
        }

        private void OnGizmoModeButtonClick(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not GizmoMode mode)
                return;

            _currentGizmoMode = mode;

            var panel = btn.Parent as StackPanel;
            if (panel != null)
                RefreshGizmoModeButtonHighlights(panel);

            UpdateGizmoForCurrentMode();
        }

        private void RefreshGizmoModeButtonHighlights(StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is Button btn && btn.Tag is GizmoMode mode)
                {
                    bool selected = mode == _currentGizmoMode;
                    btn.Background = new SolidColorBrush(Color.Parse(selected ? "#225588" : "#333333"));
                    btn.BorderBrush = new SolidColorBrush(Color.Parse(selected ? "#4488CC" : "#555555"));
                }
            }
        }

        private void UpdateGizmoForCurrentMode()
        {
            if (_selectedSceneObject == null)
                return;

            if (_currentGizmoMode == GizmoMode.None)
            {
                EditorHostBridge.SetGizmoState(Double3.Zero, (int)GizmoMode.None, (int)GizmoHandle.None, false);
                return;
            }

            var wp = EditorHostBridge.GetSceneObjectWorldPosition(EditorPreviewSceneId, _selectedSceneObject.Id);
            EditorHostBridge.SetGizmoState(wp, (int)_currentGizmoMode, (int)GizmoHandle.None, true);
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

                RightDockSlot.Content = CreateResourceViewerContent(path);
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

            _inspectorVector3RefreshActions.Clear();

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

            root.Children.Add(CreateComboPropertyEditor("类型", SceneObjectTypeOptions, () => obj.Type ?? "", value =>
            {
                string newValue = value ?? string.Empty;
                string oldValue = obj.Type ?? string.Empty;

                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                    return true;

                BeginSceneParameterChange();
                obj.Type = newValue;

                if (!PersistSceneObjectChanges(obj, true))
                    return false;

                Dispatcher.UIThread.Post(() => ShowSceneObjectInspector(obj), DispatcherPriority.Background);
                return true;
            }));

            List<string> parentIdOptions = new List<string> { string.Empty };

            if (_currentTreeScene != null)
            {
                foreach (SceneObject candidate in _currentTreeScene.Objects)
                {
                    if (ReferenceEquals(candidate, obj))
                        continue;

                    if (string.IsNullOrWhiteSpace(candidate.Id))
                        continue;

                    parentIdOptions.Add(candidate.Id);
                }
            }

            root.Children.Add(CreateComboPropertyEditor("父节点", parentIdOptions, () => obj.Transform?.ParentId ?? "", value =>
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
            root.Children.Add(CreateInspectorSectionHeader("脚本"));

            List<SceneScriptAttachment> scriptAttachments = obj.Scripts ?? new List<SceneScriptAttachment>();
            StackPanel scriptPanel = new StackPanel { Spacing = InspectorPropertySpacing };

            foreach (SceneScriptAttachment attachment in scriptAttachments)
            {
                SceneScriptAttachment capturedAttachment = attachment;
                StackPanel scriptRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };

                CheckBox enabledBox = new CheckBox
                {
                    IsChecked = capturedAttachment.Enabled,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };

                enabledBox.IsCheckedChanged += (_, _) =>
                {
                    if (_isUpdatingSceneInspector)
                        return;

                    BeginSceneParameterChange();
                    capturedAttachment.Enabled = enabledBox.IsChecked == true;
                    PersistSceneObjectChanges(obj, false);
                };

                TextBox scriptBox = CreateInspectorTextBox(capturedAttachment.Script);
                scriptBox.Width = 240;

                void CommitScript()
                {
                    if (_isUpdatingSceneInspector)
                        return;

                    string value = scriptBox.Text ?? string.Empty;

                    if (string.Equals(capturedAttachment.Script, value, StringComparison.Ordinal))
                        return;

                    BeginSceneParameterChange();
                    capturedAttachment.Script = value;
                    PersistSceneObjectChanges(obj, false);
                }

                scriptBox.LostFocus += (_, _) => CommitScript();
                scriptBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                        CommitScript();
                };

                Button removeScriptButton = CreateTextCommandButton("移除", 56);

                removeScriptButton.Click += (_, _) =>
                {
                    BeginSceneParameterChange();
                    scriptAttachments.Remove(capturedAttachment);
                    obj.Scripts = scriptAttachments.Count == 0 ? null : scriptAttachments;
                    PersistSceneObjectChanges(obj, true);
                };

                scriptRow.Children.Add(enabledBox);
                scriptRow.Children.Add(scriptBox);
                scriptRow.Children.Add(removeScriptButton);
                scriptPanel.Children.Add(scriptRow);
            }

            Button addScriptButton = CreateTextCommandButton("添加脚本", 96);

            addScriptButton.Click += (_, _) =>
            {
                BeginSceneParameterChange();
                scriptAttachments.Add(new SceneScriptAttachment { Script = "", Enabled = true });
                obj.Scripts = scriptAttachments;
                PersistSceneObjectChanges(obj, true);
            };

            scriptPanel.Children.Add(addScriptButton);
            root.Children.Add(scriptPanel);

            root.Children.Add(CreateSceneObjectDataSection(obj));
            root.Children.Add(CreateSceneObjectPhysicsSection(obj));

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

            _inspectorVector3RefreshActions.Add(ResetFromCurrent);

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
            MenuItem distributeItem = new MenuItem { Header = "分发" };
            MenuItem exitItem = new MenuItem { Header = "退出" };

            newItem.Click += async (_, _) => await ShowCreateProjectDialogAsync();
            openItem.Click += async (_, _) => await OpenProjectFolderAsync();
            _saveSceneMenuItem.Click += (_, _) => TrySaveCurrentSceneToOriginalFile();
            distributeItem.Click += async (_, _) => await ShowDistributionDialogAsync();
            exitItem.Click += (_, _) => Close();

            return new[]
            {
                newItem,
                openItem,
                _saveSceneMenuItem,
                new MenuItem { Header = "-" },
                distributeItem,
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
            _animationClipMenuItem = new MenuItem { Header = "动画", IsEnabled = false };
            _animatorControllerMenuItem = new MenuItem { Header = "动画器", IsEnabled = false };

            _animationClipMenuItem.Click += (_, _) => OpenSelectedAnimationAsset(true);
            _animatorControllerMenuItem.Click += (_, _) => OpenSelectedAnimationAsset(false);

            return new[]
            {
                _animationClipMenuItem,
                _animatorControllerMenuItem,
                new MenuItem { Header = "-" },
                new MenuItem { Header = "性能监控" },
                new MenuItem { Header = "帧分析" }
            };
        }

        private void OpenSelectedAnimationAsset(bool animationClip)
        {
            if (_selectedResourceItemState == null || _selectedResourceItemState.IsDirectory)
                return;

            string path = _selectedResourceItemState.Path;

            if (animationClip)
                ShowAnimationClipWindow(path);
            else
                ShowAnimatorControllerWindow(path);
        }

        private void UpdateAnimationMenuItems()
        {
            bool hasAnimationClip = false;
            bool hasAnimatorController = false;

            if (_selectedResourceItemState != null && !_selectedResourceItemState.IsDirectory)
            {
                string assetType = ReadJsonAssetType(_selectedResourceItemState.Path);

                hasAnimationClip = string.Equals(assetType, "AnimationClip", StringComparison.OrdinalIgnoreCase);
                hasAnimatorController = string.Equals(assetType, "AnimatorController", StringComparison.OrdinalIgnoreCase);
            }

            _animationClipMenuItem?.SetCurrentValue(MenuItem.IsEnabledProperty, hasAnimationClip);
            _animatorControllerMenuItem?.SetCurrentValue(MenuItem.IsEnabledProperty, hasAnimatorController);
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
            {
                if (IsEditorConfigFile(childFile))
                    continue;

                panel.Children.Add(CreateResourceIconItem(childFile, false));
            }

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
            UpdateAnimationMenuItems();
        }

        private void ClearSelectedResourceItem()
        {
            ResourceItemState? previous = _selectedResourceItemState;
            _selectedResourceItemState = null;

            if (previous != null)
                UpdateResourceItemVisual(previous);

            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");
            UpdateAnimationMenuItems();
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

                if (!state.IsDirectory)
                    MoveEditorConfig(state.Path, targetPath);

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

                if (!state.IsDirectory)
                    RemoveEditorConfig(state.Path);

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

                case "动画":
                    baseName = "NewAnimationClip";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".json");
                    createdPath = Path.Combine(directoryPath, fileName);
                    WriteAssetJsonFile(createdPath, "AnimationClip", new AnimationClipData
                    {
                        Name = Path.GetFileNameWithoutExtension(fileName),
                        Duration = 1.0,
                        Loop = true,
                        FrameRate = 30f,
                        PlayMode = "normal"
                    });
                    break;

                case "动画器":
                    baseName = "NewAnimatorController";
                    fileName = GetUniqueResourceName(directoryPath, baseName, ".json");
                    createdPath = Path.Combine(directoryPath, fileName);
                    WriteAssetJsonFile(createdPath, "AnimatorController", new AnimatorControllerData
                    {
                        Name = Path.GetFileNameWithoutExtension(fileName)
                    });
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

            bool isVrma =
                !state.IsDirectory &&
                string.Equals(Path.GetExtension(state.Path), ".vrma", StringComparison.OrdinalIgnoreCase);

            List<object> items = new List<object> { openItem };

            if (isVrma)
            {
                MenuItem convertItem = new MenuItem { Header = "转换为动画剪辑" };

                convertItem.Click += async (_, _) =>
                {
                    SelectResourceItem(state);
                    await ConvertVrmaToClipAsync(state.Path);
                };

                items.Add(convertItem);
            }

            items.Add(revealItem);
            items.Add(renameItem);
            items.Add(deleteItem);

            return new ContextMenu
            {
                ItemsSource = items.ToArray()
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
            MenuItem newAnimationClipItem = new MenuItem { Header = "动画" };
            MenuItem newAnimatorControllerItem = new MenuItem { Header = "动画器" };
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
                    newCanvasItem,
                    newAnimationClipItem,
                    newAnimatorControllerItem
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
            newAnimationClipItem.Click += async (_, _) => await CreateNewResourceAsync("动画");
            newAnimatorControllerItem.Click += async (_, _) => await CreateNewResourceAsync("动画器");

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

            if (string.Equals(analyzedFileType, ".lua", StringComparison.OrdinalIgnoreCase))
            {
                ShowLuaViewerWindow(path);
                return;
            }

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

                case ".vrma":
                    return ResourceIconKind.VrmaFile;

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
                    assetTypeElement.ValueKind == JsonValueKind.String)
                {
                    string assetType = assetTypeElement.GetString() ?? string.Empty;

                    if (string.Equals(assetType, "Material", StringComparison.OrdinalIgnoreCase))
                        return ResourceIconKind.MaterialJsonFile;

                    if (string.Equals(assetType, "AnimationClip", StringComparison.OrdinalIgnoreCase))
                        return ResourceIconKind.AnimationClipJsonFile;

                    if (string.Equals(assetType, "AnimatorController", StringComparison.OrdinalIgnoreCase))
                        return ResourceIconKind.AnimatorControllerJsonFile;
                }

                return ResourceIconKind.JsonFile;
            }
            catch
            {
                return ResourceIconKind.JsonFile;
            }
        }

        private bool TryHandleSpecialFileOpen(string filePath, string analyzedFileType)
        {
            if (IsEditorConfigFile(filePath))
                return false;

            if (string.Equals(analyzedFileType, ".vrma", StringComparison.OrdinalIgnoreCase))
            {
                ShowVrmaClipWindow(filePath);
                return true;
            }

            if (!string.Equals(analyzedFileType, ".json", StringComparison.OrdinalIgnoreCase))
                return false;

            string assetType = ReadJsonAssetType(filePath);

            if (string.Equals(assetType, "AnimationClip", StringComparison.OrdinalIgnoreCase))
            {
                ShowAnimationClipWindow(filePath);
                return true;
            }

            if (string.Equals(assetType, "AnimatorController", StringComparison.OrdinalIgnoreCase))
            {
                ShowAnimatorControllerWindow(filePath);
                return true;
            }

            return TryOpenSceneJson(filePath);
        }

        private static string ReadJsonAssetType(string filePath)
        {
            try
            {
                string json = File.ReadAllText(filePath);

                using JsonDocument doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    return string.Empty;

                if (!doc.RootElement.TryGetProperty("assetType", out JsonElement element) ||
                    element.ValueKind != JsonValueKind.String)
                    return string.Empty;

                return element.GetString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool TryOpenSceneJson(string filePath)
        {
            try
            {
                EditorHostBridge.SetAssetRootAndReloadAssets(ResolveSceneAssetRoot(filePath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex.ToString());
                return false;
            }

            SceneData? scene = EditorHostBridge.LoadSceneFile(filePath);
            if (scene == null)
                return false;

            try
            {
                string assetRootPath = ResolveSceneAssetRoot(filePath);
                string editorWorkingRoot = GetEditorWorkingRoot(assetRootPath);
                string treeCopyDirectory = Path.Combine(editorWorkingRoot, EditorTreeCopyDirectoryName);
                string previewDirectory = Path.Combine(editorWorkingRoot, EditorPreviewDirectoryName);

                Directory.CreateDirectory(editorWorkingRoot);
                Directory.CreateDirectory(treeCopyDirectory);
                Directory.CreateDirectory(previewDirectory);

                string treeCopyPath = BuildTreeCopyPath(treeCopyDirectory, filePath);
                string previewScenePath = Path.Combine(previewDirectory, EditorPreviewSceneFileName);

                File.Copy(filePath, treeCopyPath, true);

                _currentSceneOriginalPath = Path.GetFullPath(filePath);
                _currentTreeCopyPath = treeCopyPath;
                _currentPreviewScenePath = previewScenePath;
                _currentTreeScene = scene;
                ClearSelectedSceneObjectState();
                _sceneUndoStack.Clear();
                _sceneRedoStack.Clear();
                _isApplyingSceneHistory = false;
                _sceneHasUnsavedChanges = false;

                InitializePreviewCameraEditorState(scene);
                _currentPreviewScene = BuildPreviewScene(scene);
                SaveSceneData(_currentPreviewScenePath, _currentPreviewScene);

                RefreshCurrentPreviewCameraId();
                ResetSceneHostNavigationState();

                LeftDockSlot.Content = BuildSceneTreeControl(scene);

                UpdateSceneDirtyVisualState();

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
            bool isDirty = IsCurrentSceneTreeDirty() || _sceneHasUnsavedChanges;

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
                string.IsNullOrWhiteSpace(_currentTreeCopyPath))
                return false;

            if (_currentTreeScene != null)
                SaveSceneData(_currentTreeCopyPath, _currentTreeScene);

            if (!File.Exists(_currentTreeCopyPath))
                return false;

            if (!IsCurrentSceneTreeDirty())
            {
                _sceneHasUnsavedChanges = false;
                UpdateSceneDirtyVisualState();
                return false;
            }

            File.Copy(_currentTreeCopyPath, _currentSceneOriginalPath, true);
            _sceneHasUnsavedChanges = false;
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
            EditorHostBridge.SetGizmoState(Double3.Zero, (int)GizmoMode.None, (int)GizmoHandle.None, false);

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
                obj.Scripts = null;
                obj.Controller = null;

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
                Objects = source.Objects.Select(CloneSceneObject).ToList(),
                Properties = source.Properties
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
                Materials = source.Materials != null ? new List<string>(source.Materials) : null,
                Scripts = source.Scripts == null
                    ? null
                    : source.Scripts.Select(item => new SceneScriptAttachment
                    {
                        Script = item.Script,
                        Enabled = item.Enabled
                    }).ToList()
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

            ClearSelectedSceneObjectState();
            RightDockSlot.Content = CreatePlaceholder("未选中文件或节点");

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

                UpdateGizmoForCurrentMode();
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

        private void ShowLuaViewerWindow(string filePath)
        {
            string originalContent = File.ReadAllText(filePath);
            string fileName = Path.GetFileName(filePath);
            bool isDirty = false;
            bool isClosing = false;
            TextEditor? sourceEditor = null;
            Window window = null!;

            void RefreshTitle()
            {
                window.Title = isDirty ? fileName + " *" : fileName;
            }

            void SaveFile()
            {
                if (sourceEditor == null)
                    return;
                string newContent = sourceEditor.Text;
                File.WriteAllText(filePath, newContent, Encoding.UTF8);
                originalContent = newContent;
                isDirty = false;
                RefreshTitle();
            }

            void OnEditorTextChanged()
            {
                if (sourceEditor == null)
                    return;
                if (IsLuaSyncingToSource())
                    return;
                bool changed = sourceEditor.Text != originalContent;
                if (changed == isDirty)
                    return;
                isDirty = changed;
                RefreshTitle();
            }

            void OnClosing(object? sender, WindowClosingEventArgs e)
            {
                if (!isDirty || isClosing)
                    return;
                e.Cancel = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    bool confirmed = await ShowLuaExitConfirmDialog(window);
                    if (confirmed)
                    {
                        isClosing = true;
                        _openLuaViewers.Remove(window);
                        window.Close();
                    }
                });
            }

            window = new Window
            {
                Title = fileName,
                Width = 900,
                Height = 650,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111")),
                Content = BuildLuaViewerContent(filePath, SaveFile, OnEditorTextChanged, editor => { sourceEditor = editor; })
            };

            window.Closing += OnClosing;
            window.Closed += (_, _) => _openLuaViewers.Remove(window);
            _openLuaViewers.Add(window);
            window.Show(this);
        }

        private Control BuildLuaViewerContent(string filePath, Action onSave, Action onTextChanged, Action<TextEditor> onEditorCreated)
        {
            var contentHost = new ContentControl();

            var visualButton = new Button
            {
                Content = "可视化",
                Width = 80,
                Height = 30,
                FontSize = 13,
                Padding = new Thickness(0),
                Tag = "visual",
                Background = new SolidColorBrush(Color.Parse("#225588")),
                BorderBrush = new SolidColorBrush(Color.Parse("#4488CC")),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var sourceButton = new Button
            {
                Content = "源代码",
                Width = 80,
                Height = 30,
                FontSize = 13,
                Padding = new Thickness(0),
                Tag = "source",
                Background = new SolidColorBrush(Color.Parse("#333333")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };

            var visualRoot = BuildLuaVisualEditor(filePath);
            var sourcePage = BuildLuaSourceEditor(filePath, onTextChanged, out var ed);
            onEditorCreated(ed);
            _luaSourceEditor = ed;

            void RefreshButtons()
            {
                bool isVisual = contentHost.Content == null || ReferenceEquals(contentHost.Content, _luaVisualScrollViewer);
                visualButton.Background = new SolidColorBrush(Color.Parse(isVisual ? "#225588" : "#333333"));
                visualButton.BorderBrush = new SolidColorBrush(Color.Parse(isVisual ? "#4488CC" : "#555555"));
                sourceButton.Background = new SolidColorBrush(Color.Parse(isVisual ? "#333333" : "#225588"));
                sourceButton.BorderBrush = new SolidColorBrush(Color.Parse(isVisual ? "#555555" : "#4488CC"));
            }

            visualButton.Click += (_, _) =>
            {
                if (_luaSourceEditor != null)
                {
                    _luaVisualBlocks = ParseLuaToBlocks(_luaSourceEditor.Text);
                    RebuildLuaVisualView();
                }
                contentHost.Content = _luaVisualScrollViewer;
                RefreshButtons();
            };

            sourceButton.Click += (_, _) =>
            {
                SyncVisualToSourceSilent();
                contentHost.Content = sourcePage;
                RefreshButtons();
            };

            contentHost.Content = _luaVisualScrollViewer;

            var tabBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Height = 38,
                Margin = new Thickness(10, 6, 10, 0),
                Children = { visualButton, sourceButton }
            };

            var saveButton = new Button
            {
                Content = "保存",
                Width = 80,
                Height = 30,
                FontSize = 13,
                Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            saveButton.Click += (_, _) => onSave();

            var bottomBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Height = 38,
                Margin = new Thickness(10, 0, 10, 6),
                Children = { saveButton }
            };

            return new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        [DockPanel.DockProperty] = Dock.Top,
                        Child = tabBar
                    },
                    new Border
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Child = bottomBar
                    },
                    contentHost
                }
            };
        }



        private Control BuildLuaSourceEditor(string filePath, Action onTextChanged, out TextEditor? editorRef)
        {
            var editor = new TextEditor
            {
                IsReadOnly = false,
                FontFamily = new FontFamily("Consolas, Courier New, monospace"),
                FontSize = 14,
                ShowLineNumbers = true,
                WordWrap = false,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Foreground = new SolidColorBrush(Color.Parse("#D4D4D4")),
                LineNumbersForeground = new SolidColorBrush(Color.Parse("#858585")),
                Text = File.ReadAllText(filePath)
            };

            editor.TextChanged += (_, _) => onTextChanged();

            var registryOptions = new RegistryOptions(ThemeName.DarkPlus);
            var textMate = editor.InstallTextMate(registryOptions);
            textMate.SetGrammar(registryOptions.GetScopeByLanguageId(
                registryOptions.GetLanguageByExtension(".lua").Id));

            InitLuaCompletion(editor);
            InitLuaDiagnostics(editor);

            editorRef = editor;
            return editor;
        }

        private async Task<bool> ShowLuaExitConfirmDialog(Window owner)
        {
            bool confirmed = false;

            Window dialog = new Window
            {
                Title = "警告",
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
                Text = "文件未保存，继续退出吗？",
                Foreground = Brushes.White,
                FontWeight = FontWeight.Bold,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 18)
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
                    messageText,
                    buttons
                }
            };

            dialog.Content = content;

            await dialog.ShowDialog(owner);
            return confirmed;
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

        private GizmoHandle HitTestGizmo(Point mousePt)
        {
            if (_selectedSceneObject == null)
                return GizmoHandle.None;

            if (_currentGizmoMode == GizmoMode.None)
                return GizmoHandle.None;

            var hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return GizmoHandle.None;

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;

            float mx = (float)(mousePt.X * scaling / hostSize.Width) * 2f - 1f;
            float my = 1f - (float)(mousePt.Y * scaling / hostSize.Height) * 2f;
            SNVec2 mouseNdc = new SNVec2(mx, my);

            Double3 wp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject.Id);

            SNMat4 viewM = EditorHostBridge.GetCameraView();
            SNMat4 projM = EditorHostBridge.GetCameraProjection();

            Double3 camPos = _previewCameraEditorPosition;
            Double3 rel = new Double3(wp.X - camPos.X, wp.Y - camPos.Y, wp.Z - camPos.Z);
            SNVec3 centerRender = new SNVec3((float)rel.X, (float)rel.Y, (float)(-rel.Z));

            float fovHalfTan = 1f / projM.M22;
            const float ScreenRatio = 0.12f;
            double distance = System.Math.Sqrt(rel.X * rel.X + rel.Y * rel.Y + rel.Z * rel.Z);
            float worldScale = (float)distance * fovHalfTan * 2f * ScreenRatio;

            SNVec2 centerNdc = WorldToNdc(centerRender, viewM, projM);
            if (float.IsNaN(centerNdc.X))
                return GizmoHandle.None;

            bool checkTranslate = _currentGizmoMode == GizmoMode.Translate || _currentGizmoMode == GizmoMode.Transform;
            bool checkRotate = _currentGizmoMode == GizmoMode.Rotate || _currentGizmoMode == GizmoMode.Transform;
            bool checkScale = _currentGizmoMode == GizmoMode.Scale || _currentGizmoMode == GizmoMode.Transform;

            float bestDist = float.MaxValue;
            int bestType = -1;
            GizmoHandle bestHandle = GizmoHandle.None;

            const float HitNdc = 0.04f;
            SNVec3[] axisRender = { new SNVec3(1f, 0f, 0f), new SNVec3(0f, 1f, 0f), new SNVec3(0f, 0f, -1f) };

            if (checkRotate)
            {
                float midR = _currentGizmoMode == GizmoMode.Transform ? 1.75f : 1.35f;
                float halfThick = 0.05f;
                const int RingSamples = 32;
                for (int i = 0; i < 3; i++)
                {
                    SNVec3 perpA = Math.Abs(axisRender[i].X) < 0.9f
                        ? SNVec3.Normalize(System.Numerics.Vector3.Cross(axisRender[i], System.Numerics.Vector3.UnitX))
                        : SNVec3.Normalize(System.Numerics.Vector3.Cross(axisRender[i], System.Numerics.Vector3.UnitY));
                    SNVec3 perpB = System.Numerics.Vector3.Cross(perpA, axisRender[i]);

                    SNVec2 refInner = WorldToNdc(centerRender + perpA * ((midR - halfThick) * worldScale), viewM, projM);
                    SNVec2 refOuter = WorldToNdc(centerRender + perpA * ((midR + halfThick) * worldScale), viewM, projM);
                    float ringHalfThickNdc = float.IsNaN(refInner.X) || float.IsNaN(refOuter.X)
                        ? HitNdc
                        : SNVec2.Distance(refInner, refOuter) * 0.5f;
                    float tolerance = ringHalfThickNdc + HitNdc;

                    float closestDist = float.MaxValue;
                    int closestIdx = -1;
                    for (int s = 0; s < RingSamples; s++)
                    {
                        float ang = MathF.PI * 2f * s / RingSamples;
                        SNVec3 d = perpA * MathF.Cos(ang) + perpB * MathF.Sin(ang);
                        SNVec3 ringPt = centerRender + d * (midR * worldScale);
                        SNVec2 ringNdc = WorldToNdc(ringPt, viewM, projM);
                        if (float.IsNaN(ringNdc.X))
                            continue;

                        float dist = SNVec2.Distance(mouseNdc, ringNdc);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestIdx = s;
                        }
                    }

                    if (closestIdx >= 0 && closestDist < tolerance && closestDist < bestDist)
                    {
                        bestDist = closestDist;
                        bestType = 1;
                        bestHandle = (GizmoHandle)i;
                    }
                }
            }

            if (checkScale)
            {
                float scaleCubeSize = (_currentGizmoMode == GizmoMode.Transform) ? 0.16f : 0.18f;
                float centralCubeHalf = scaleCubeSize * 1.2f * 0.5f * worldScale;
                SNVec3 cubeCorner = centerRender + new SNVec3(centralCubeHalf, centralCubeHalf, centralCubeHalf);
                SNVec2 cubeCornerNdc = WorldToNdc(cubeCorner, viewM, projM);
                if (!float.IsNaN(cubeCornerNdc.X))
                {
                    float cubeNdcR = SNVec2.Distance(cubeCornerNdc, centerNdc);
                    float centerDist = SNVec2.Distance(mouseNdc, centerNdc);
                    if (centerDist < cubeNdcR + HitNdc && centerDist < bestDist)
                    {
                        bestDist = centerDist;
                        bestType = 2;
                        bestHandle = (GizmoHandle)3;
                    }
                }

                float scGap = (_currentGizmoMode == GizmoMode.Transform) ? 0.30f : 0.5f;
                float scShaftLen = (_currentGizmoMode == GizmoMode.Transform) ? 0.28f : 0.7f;
                float scTipTotal = (scGap + scShaftLen + 0.05f) * worldScale;
                float scBase = (scGap - 0.05f) * worldScale;

                for (int i = 0; i < 3; i++)
                {
                    SNVec3 tipRender = centerRender + axisRender[i] * scTipTotal;
                    SNVec3 baseRender = centerRender + axisRender[i] * scBase;
                    SNVec2 tipNdc = WorldToNdc(tipRender, viewM, projM);
                    SNVec2 baseNdc = WorldToNdc(baseRender, viewM, projM);
                    if (float.IsNaN(tipNdc.X) || float.IsNaN(baseNdc.X))
                        continue;

                    float dist = DistPointToSegment(mouseNdc, baseNdc, tipNdc);
                    if (dist < HitNdc && dist < bestDist)
                    {
                        bestDist = dist;
                        bestType = 2;
                        bestHandle = (GizmoHandle)i;
                    }
                }
            }

            if (checkTranslate)
            {
                float trGap = (_currentGizmoMode == GizmoMode.Transform) ? 1.95f : 0.5f;
                float trTotal = (trGap + 0.7f + 0.4f + 0.05f) * worldScale;
                float trBase = (trGap - 0.05f) * worldScale;

                for (int i = 0; i < 3; i++)
                {
                    SNVec3 tipRender = centerRender + axisRender[i] * trTotal;
                    SNVec3 baseRender = centerRender + axisRender[i] * trBase;
                    SNVec2 tipNdc = WorldToNdc(tipRender, viewM, projM);
                    SNVec2 baseNdc = WorldToNdc(baseRender, viewM, projM);
                    if (float.IsNaN(tipNdc.X) || float.IsNaN(baseNdc.X))
                        continue;

                    float dist = DistPointToSegment(mouseNdc, baseNdc, tipNdc);
                    if (dist < HitNdc && dist < bestDist)
                    {
                        bestDist = dist;
                        bestType = 0;
                        bestHandle = (GizmoHandle)i;
                    }
                }
            }

            _gizmoDragType = bestType;
            return bestHandle;
        }

        private void ApplyGizmoDrag(SNVec2 screenDelta)
        {
            if (_selectedSceneObject == null)
                return;

            if (_currentGizmoMode == GizmoMode.None)
                return;

            if (_gizmoDragType == 0)
            {
                ApplyTranslateDrag(screenDelta);
            }
            else if (_gizmoDragType == 1)
            {
                ApplyRotateDrag(screenDelta);
            }
            else if (_gizmoDragType == 2)
            {
                if ((int)_gizmoDragHandle == 3)
                    ApplyScaleUniformDrag(screenDelta);
                else
                    ApplyScaleAxisDrag(screenDelta);
            }

            Double3 newWp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject.Id);
            EditorHostBridge.SetGizmoState(newWp, (int)_currentGizmoMode, (int)_gizmoDragHandle, true);

            RefreshInspectorTransformValues();
        }

        private void RefreshInspectorTransformValues()
        {
            if (_isUpdatingSceneInspector)
                return;
            if (_selectedSceneObject == null)
                return;

            _isUpdatingSceneInspector = true;
            try
            {
                foreach (var action in _inspectorVector3RefreshActions)
                    action();
            }
            finally
            {
                _isUpdatingSceneInspector = false;
            }
        }

        private void ApplyTranslateDrag(SNVec2 screenDelta)
        {
            var hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            SNMat4 viewM = EditorHostBridge.GetCameraView();
            SNMat4 projM = EditorHostBridge.GetCameraProjection();

            Double3 wp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject!.Id);
            Double3 camPos = _previewCameraEditorPosition;
            Double3 rel = new Double3(wp.X - camPos.X, wp.Y - camPos.Y, wp.Z - camPos.Z);
            SNVec3 centerRender = new SNVec3((float)rel.X, (float)rel.Y, (float)(-rel.Z));

            SNVec3 axisRender = _gizmoDragHandle switch
            {
                GizmoHandle.X => new SNVec3(1f, 0f, 0f),
                GizmoHandle.Y => new SNVec3(0f, 1f, 0f),
                GizmoHandle.Z => new SNVec3(0f, 0f, -1f),
                _ => SNVec3.Zero
            };

            SNVec2 centerNdc = WorldToNdc(centerRender, viewM, projM);
            SNVec2 tipNdc = WorldToNdc(centerRender + axisRender, viewM, projM);
            SNVec2 axisNdc = tipNdc - centerNdc;
            float axisLen = axisNdc.Length();
            if (axisLen < 1e-6f)
                return;

            SNVec2 axisDir = axisNdc / axisLen;

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;
            SNVec2 ndcDelta = new SNVec2(
                screenDelta.X * (float)scaling * 2f / (float)hostSize.Width,
                -screenDelta.Y * (float)scaling * 2f / (float)hostSize.Height);

            float ndcMove = SNVec2.Dot(ndcDelta, axisDir);
            float worldMove = ndcMove / axisLen;

            Double3 worldAxisWorld = _gizmoDragHandle switch
            {
                GizmoHandle.X => new Double3(1, 0, 0),
                GizmoHandle.Y => new Double3(0, 1, 0),
                GizmoHandle.Z => new Double3(0, 0, 1),
                _ => Double3.Zero
            };

            Double3 worldOffset = new Double3(
                worldAxisWorld.X * worldMove,
                worldAxisWorld.Y * worldMove,
                worldAxisWorld.Z * worldMove);

            Double3 localOffset = EditorHostBridge.WorldDeltaToLocalDelta(
                EditorPreviewSceneId, _selectedSceneObject.Id, worldOffset);
            _gizmoDragStartLocalPos += localOffset;
            Double3 newLocalPos = _gizmoDragStartLocalPos;

            EditorHostBridge.SetSceneObjectLocalPosition(
                EditorPreviewSceneId, _selectedSceneObject.Id, newLocalPos);
            _selectedSceneObject.Transform!.LocalPosition = newLocalPos;
        }

        private void ApplyRotateDrag(SNVec2 screenDelta)
        {
            var hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            SNMat4 viewM = EditorHostBridge.GetCameraView();
            SNMat4 projM = EditorHostBridge.GetCameraProjection();

            Double3 wp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject!.Id);
            Double3 camPos = _previewCameraEditorPosition;
            Double3 rel = new Double3(wp.X - camPos.X, wp.Y - camPos.Y, wp.Z - camPos.Z);
            SNVec3 centerRender = new SNVec3((float)rel.X, (float)rel.Y, (float)(-rel.Z));

            SNVec3 axisRender = _gizmoDragHandle switch
            {
                GizmoHandle.X => new SNVec3(1f, 0f, 0f),
                GizmoHandle.Y => new SNVec3(0f, 1f, 0f),
                GizmoHandle.Z => new SNVec3(0f, 0f, -1f),
                _ => SNVec3.Zero
            };

            SNVec2 centerNdc = WorldToNdc(centerRender, viewM, projM);
            SNVec2 tipNdc = WorldToNdc(centerRender + axisRender, viewM, projM);
            SNVec2 axisNdc = tipNdc - centerNdc;
            float axisLen = axisNdc.Length();
            if (axisLen < 1e-6f)
                return;

            SNVec2 axisDirNdc = axisNdc / axisLen;
            SNVec2 tangentNdc = new SNVec2(-axisDirNdc.Y, axisDirNdc.X);

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;
            SNVec2 ndcDelta = new SNVec2(
                screenDelta.X * (float)scaling * 2f / (float)hostSize.Width,
                -screenDelta.Y * (float)scaling * 2f / (float)hostSize.Height);

            float ndcMove = SNVec2.Dot(ndcDelta, tangentNdc);

            float midR = _currentGizmoMode == GizmoMode.Transform ? 1.75f : 1.35f;
            float ringNdcR = axisLen * midR;
            if (ringNdcR < 1e-4f)
                return;

            float angleDegrees = ndcMove / ringNdcR * (180f / MathF.PI);

            Double3 rotDelta = _gizmoDragHandle switch
            {
                GizmoHandle.X => new Double3(angleDegrees, 0, 0),
                GizmoHandle.Y => new Double3(0, angleDegrees, 0),
                GizmoHandle.Z => new Double3(0, 0, angleDegrees),
                _ => Double3.Zero
            };

            _gizmoDragStartLocalRot += rotDelta;
            Double3 newLocalRot = _gizmoDragStartLocalRot;

            EditorHostBridge.SetSceneObjectLocalRotation(
                EditorPreviewSceneId, _selectedSceneObject.Id, newLocalRot);
            _selectedSceneObject.Transform!.LocalRotation = newLocalRot;
        }

        private void ApplyScaleAxisDrag(SNVec2 screenDelta)
        {
            var hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            SNMat4 viewM = EditorHostBridge.GetCameraView();
            SNMat4 projM = EditorHostBridge.GetCameraProjection();

            Double3 wp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject!.Id);
            Double3 camPos = _previewCameraEditorPosition;
            Double3 rel = new Double3(wp.X - camPos.X, wp.Y - camPos.Y, wp.Z - camPos.Z);
            SNVec3 centerRender = new SNVec3((float)rel.X, (float)rel.Y, (float)(-rel.Z));

            SNVec3 axisRender = _gizmoDragHandle switch
            {
                GizmoHandle.X => new SNVec3(1f, 0f, 0f),
                GizmoHandle.Y => new SNVec3(0f, 1f, 0f),
                GizmoHandle.Z => new SNVec3(0f, 0f, -1f),
                _ => SNVec3.Zero
            };

            SNVec2 centerNdc = WorldToNdc(centerRender, viewM, projM);
            SNVec2 tipNdc = WorldToNdc(centerRender + axisRender, viewM, projM);
            SNVec2 axisNdc = tipNdc - centerNdc;
            float axisLen = axisNdc.Length();
            if (axisLen < 1e-6f)
                return;

            SNVec2 axisDir = axisNdc / axisLen;

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;
            SNVec2 ndcDelta = new SNVec2(
                screenDelta.X * (float)scaling * 2f / (float)hostSize.Width,
                -screenDelta.Y * (float)scaling * 2f / (float)hostSize.Height);

            float ndcMove = SNVec2.Dot(ndcDelta, axisDir);
            float scaleDelta = ndcMove / axisLen * 2f;

            Double3 scaleOffset = _gizmoDragHandle switch
            {
                GizmoHandle.X => new Double3(scaleDelta, 0, 0),
                GizmoHandle.Y => new Double3(0, scaleDelta, 0),
                GizmoHandle.Z => new Double3(0, 0, scaleDelta),
                _ => Double3.Zero
            };

            _gizmoDragStartLocalScale += scaleOffset;
            Double3 newLocalScale = new Double3(
                Math.Max(0.01, _gizmoDragStartLocalScale.X),
                Math.Max(0.01, _gizmoDragStartLocalScale.Y),
                Math.Max(0.01, _gizmoDragStartLocalScale.Z));

            EditorHostBridge.SetSceneObjectLocalScale(
                EditorPreviewSceneId, _selectedSceneObject.Id, newLocalScale);
            _selectedSceneObject.Transform!.LocalScale = newLocalScale;
        }

        private void ApplyScaleUniformDrag(SNVec2 screenDelta)
        {
            var hostSize = GetSceneHostPixelSize();
            if (hostSize.Width <= 0 || hostSize.Height <= 0)
                return;

            SNMat4 viewM = EditorHostBridge.GetCameraView();
            SNMat4 projM = EditorHostBridge.GetCameraProjection();

            Double3 wp = EditorHostBridge.GetSceneObjectWorldPosition(
                EditorPreviewSceneId, _selectedSceneObject!.Id);
            Double3 camPos = _previewCameraEditorPosition;
            Double3 rel = new Double3(wp.X - camPos.X, wp.Y - camPos.Y, wp.Z - camPos.Z);
            SNVec3 centerRender = new SNVec3((float)rel.X, (float)rel.Y, (float)(-rel.Z));

            SNVec2 centerNdc = WorldToNdc(centerRender, viewM, projM);

            double scaling = TopLevel.GetTopLevel(_sceneHost)?.RenderScaling ?? 1.0;
            SNVec2 ndcDelta = new SNVec2(
                screenDelta.X * (float)scaling * 2f / (float)hostSize.Width,
                -screenDelta.Y * (float)scaling * 2f / (float)hostSize.Height);

            float ndcMove = (ndcDelta.X + ndcDelta.Y) * 0.5f;
            float scaleFactor = 1f + ndcMove * 3f;

            _gizmoDragStartLocalScale = new Double3(
                _gizmoDragStartLocalScale.X * scaleFactor,
                _gizmoDragStartLocalScale.Y * scaleFactor,
                _gizmoDragStartLocalScale.Z * scaleFactor);

            Double3 newLocalScale = new Double3(
                Math.Max(0.01, _gizmoDragStartLocalScale.X),
                Math.Max(0.01, _gizmoDragStartLocalScale.Y),
                Math.Max(0.01, _gizmoDragStartLocalScale.Z));

            EditorHostBridge.SetSceneObjectLocalScale(
                EditorPreviewSceneId, _selectedSceneObject.Id, newLocalScale);
            _selectedSceneObject.Transform!.LocalScale = newLocalScale;
        }

        private static SNVec2 WorldToNdc(SNVec3 renderPos, SNMat4 view, SNMat4 proj)
        {
            SNVec4 clip = SNVec4.Transform(new SNVec4(renderPos, 1f), view * proj);
            if (Math.Abs(clip.W) < 1e-6f)
                return new SNVec2(float.NaN);

            return new SNVec2(clip.X / clip.W, clip.Y / clip.W);
        }

        private static float DistPointToSegment(SNVec2 p, SNVec2 a, SNVec2 b)
        {
            SNVec2 ab = b - a;
            float lenSq = ab.LengthSquared();
            if (lenSq < 1e-8f)
                return SNVec2.Distance(p, a);

            float t = Math.Clamp(SNVec2.Dot(p - a, ab) / lenSq, 0f, 1f);
            return SNVec2.Distance(p, a + t * ab);
        }

        private sealed class EditorConfigData
        {
            public string AssetType { get; set; } = "EditorConfig";
            public string Target { get; set; } = "";
            public Dictionary<string, JsonElement> Values { get; set; } = new();
        }

        private const string EditorConfigFileSuffix = ".editor.json";

        private static bool IsEditorConfigFile(string filePath)
        {
            return filePath.EndsWith(EditorConfigFileSuffix, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildEditorConfigPath(string assetPath)
        {
            string extension = Path.GetExtension(assetPath);
            string withoutExtension = string.IsNullOrEmpty(extension)
                ? assetPath
                : assetPath[..^extension.Length];

            return withoutExtension + EditorConfigFileSuffix;
        }

        private string BuildAnimationAssetKey(string assetPath)
        {
            string rootPath = _projectAssetRootPath ?? "";

            if (string.IsNullOrWhiteSpace(rootPath))
                return Path.GetFileNameWithoutExtension(assetPath);

            string key = Path.GetRelativePath(rootPath, assetPath).Replace('\\', '/');
            string extension = Path.GetExtension(key);

            if (!string.IsNullOrEmpty(extension))
                key = key[..^extension.Length];

            return key;
        }

        private EditorConfigData LoadEditorConfig(string assetPath)
        {
            EditorConfigData config = new EditorConfigData();
            string configPath = BuildEditorConfigPath(assetPath);

            if (!File.Exists(configPath))
                return config;

            try
            {
                string json = File.ReadAllText(configPath);
                EditorConfigData? loaded = JsonSerializer.Deserialize<EditorConfigData>(json, SceneJsonReadOptions);

                if (loaded != null)
                    config = loaded;
            }
            catch
            {
            }

            return config;
        }

        private void SaveEditorConfig(string assetPath, EditorConfigData config)
        {
            string configPath = BuildEditorConfigPath(assetPath);

            try
            {
                config.AssetType = "EditorConfig";
                config.Target = BuildAnimationAssetKey(assetPath);
                string json = JsonSerializer.Serialize(config, SceneJsonWriteOptions);
                File.WriteAllText(configPath, json);
            }
            catch
            {
            }
        }

        private void RemoveEditorConfig(string assetPath)
        {
            string configPath = BuildEditorConfigPath(assetPath);

            try
            {
                if (File.Exists(configPath))
                    File.Delete(configPath);
            }
            catch
            {
            }
        }

        private void MoveEditorConfig(string sourceAssetPath, string targetAssetPath)
        {
            string sourceConfigPath = BuildEditorConfigPath(sourceAssetPath);
            string targetConfigPath = BuildEditorConfigPath(targetAssetPath);

            try
            {
                if (File.Exists(sourceConfigPath))
                    File.Move(sourceConfigPath, targetConfigPath, true);
            }
            catch
            {
            }
        }

        private static void WriteAssetJsonFile(string filePath, string assetType, object body)
        {
            JsonObject root = new JsonObject();
            root["assetType"] = assetType;

            JsonNode? node = JsonSerializer.SerializeToNode(body, SceneJsonWriteOptions);

            if (node is JsonObject bodyObject)
            {
                foreach (KeyValuePair<string, JsonNode?> property in bodyObject)
                    root[property.Key] = property.Value?.DeepClone();
            }

            File.WriteAllText(filePath, root.ToJsonString(SceneJsonWriteOptions));
        }

        private void SaveAnimationClipFile(string filePath, AnimationClipData data)
        {
            WriteAssetJsonFile(filePath, "AnimationClip", data);
        }

        private void SaveAnimatorControllerFile(string filePath, AnimatorControllerData data)
        {
            WriteAssetJsonFile(filePath, "AnimatorController", data);
        }

        private async Task ConvertVrmaToClipAsync(string vrmaPath)
        {
            if (!VrmaConverter.TryConvert(vrmaPath, out AnimationClipData? clip, out string reason))
            {
                await ShowSimpleWarningDialogAsync("转换失败", reason);
                return;
            }

            string clipPath = Path.ChangeExtension(vrmaPath, ".json");

            if (File.Exists(clipPath))
            {
                await ShowSimpleWarningDialogAsync("转换失败", "目标已存在 " + Path.GetFileName(clipPath));
                return;
            }

            try
            {
                SaveAnimationClipFile(clipPath, clip!);

                string key = BuildAnimationAssetKey(clipPath);

                EditorHostBridge.RegisterAnimationClip(key, clip!);

                if (!string.IsNullOrWhiteSpace(_currentResourceDirectoryPath))
                    ShowResourceDirectory(_currentResourceDirectoryPath);

                await ShowSimpleWarningDialogAsync("转换完成", "已生成 " + Path.GetFileName(clipPath));
            }
            catch (Exception ex)
            {
                await ShowSimpleWarningDialogAsync("转换失败", ex.Message);
            }
        }

        private static string ReadEditorConfigValue(EditorConfigData config, string key)
        {
            if (config.Values.TryGetValue(key, out JsonElement element) && element.ValueKind == JsonValueKind.String)
                return element.GetString() ?? string.Empty;

            return string.Empty;
        }

        private static void WriteEditorConfigValue(EditorConfigData config, string key, string value)
        {
            config.Values[key] = JsonSerializer.SerializeToElement(value, SceneJsonWriteOptions);
        }

        private static int ResolveAnimationValueCount(string valueType)
        {
            switch (valueType)
            {
                case "float":
                case "bool":
                    return 1;

                case "vector2":
                    return 2;

                case "vector3":
                    return 3;

                case "quaternion":
                case "vector4":
                    return 4;

                default:
                    return 0;
            }
        }

        private static string BuildChannelLabel(AnimationChannel channel)
        {
            return channel.Binding + "/" + channel.Path + "." + channel.Property;
        }

        private static AnimationKeyframe? FindKeyframeAt(AnimationChannel channel, double time, double tolerance)
        {
            foreach (AnimationKeyframe keyframe in channel.Keys)
            {
                if (Math.Abs(keyframe.Time - time) <= tolerance)
                    return keyframe;
            }

            return null;
        }

        private static AnimationKeyframe InsertOrGetKeyframe(AnimationChannel channel, double time, double tolerance, int valueCount)
        {
            AnimationKeyframe? existing = FindKeyframeAt(channel, time, tolerance);

            if (existing != null)
                return existing;

            AnimationKeyframe keyframe = new AnimationKeyframe
            {
                Time = time,
                Value = new float[valueCount]
            };

            channel.Keys.Add(keyframe);
            channel.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));

            return keyframe;
        }

        private static double SampleChannelComponent(AnimationChannel channel, int component, double time)
        {
            List<AnimationKeyframe> keys = channel.Keys;

            if (keys.Count == 0)
                return 0.0;

            if (time <= keys[0].Time)
                return component < keys[0].Value.Length ? keys[0].Value[component] : 0.0;

            for (int i = 1; i < keys.Count; i++)
            {
                if (keys[i].Time < time)
                    continue;

                AnimationKeyframe from = keys[i - 1];
                AnimationKeyframe to = keys[i];
                double span = to.Time - from.Time;

                if (span <= 0.0)
                    return component < to.Value.Length ? to.Value[component] : 0.0;

                double ratio = (time - from.Time) / span;
                double fromValue = component < from.Value.Length ? from.Value[component] : 0.0;
                double toValue = component < to.Value.Length ? to.Value[component] : 0.0;

                if (string.Equals(channel.Interpolation, "step", StringComparison.OrdinalIgnoreCase))
                    return fromValue;

                return fromValue + (toValue - fromValue) * ratio;
            }

            AnimationKeyframe last = keys[keys.Count - 1];
            return component < last.Value.Length ? last.Value[component] : 0.0;
        }

        private static void ResolveDefaultKeyframeValue(AnimationChannel channel, AnimationKeyframe keyframe, int valueCount)
        {
            if (string.Equals(channel.Property, "rotation", StringComparison.OrdinalIgnoreCase))
            {
                keyframe.Value = valueCount >= 4
                    ? new[] { 0f, 0f, 0f, 1f }
                    : new[] { 0f, 0f, 0f };
                return;
            }

            if (string.Equals(channel.Property, "scale", StringComparison.OrdinalIgnoreCase))
            {
                keyframe.Value = new[] { 1f, 1f, 1f };
                return;
            }

            if (keyframe.Value.Length != valueCount)
            {
                float[] resized = keyframe.Value;
                Array.Resize(ref resized, valueCount);
                keyframe.Value = resized;
            }
        }

        private static List<double> BuildChannelDisplayValues(AnimationChannel channel, List<float> values, bool eulerMode)
        {
            List<double> result = new List<double>();

            foreach (float value in values)
                result.Add(value);

            if (!eulerMode)
                return result;

            if (!string.Equals(channel.Property, "rotation", StringComparison.OrdinalIgnoreCase))
                return result;

            if (values.Count < 4)
                return result;

            double[] euler = EditorHostBridge.QuaternionToEulerDegrees(values[0], values[1], values[2], values[3]);

            result.Clear();

            foreach (double value in euler)
                result.Add(value);

            return result;
        }

        private static void ApplyChannelDisplayValues(AnimationChannel channel, AnimationKeyframe keyframe, List<double> display, bool eulerMode)
        {
            if (eulerMode &&
                string.Equals(channel.Property, "rotation", StringComparison.OrdinalIgnoreCase) &&
                display.Count >= 3)
            {
                double[] quaternion = EditorHostBridge.EulerDegreesToQuaternion(display[0], display[1], display[2]);

                keyframe.Value = new[]
                {
                    (float)quaternion[0],
                    (float)quaternion[1],
                    (float)quaternion[2],
                    (float)quaternion[3]
                };

                return;
            }

            int count = display.Count;

            if (keyframe.Value.Length != count)
            {
                float[] resized = keyframe.Value;
                Array.Resize(ref resized, count);
                keyframe.Value = resized;
            }

            for (int i = 0; i < count; i++)
                keyframe.Value[i] = (float)display[i];
        }

        private void ShowAnimationClipWindow(string filePath)
        {
            AnimationClipData? clip = EditorHostBridge.LoadAnimationClip(filePath);

            if (clip == null)
            {
                _ = ShowSimpleWarningDialogAsync("打开失败", "无法解析动画剪辑 " + Path.GetFileName(filePath));
                return;
            }

            ShowAnimationClipWindow(filePath, clip, false);
        }

        private void ShowVrmaClipWindow(string filePath)
        {
            if (!VrmaConverter.TryConvert(filePath, out AnimationClipData? clip, out string reason))
            {
                _ = ShowSimpleWarningDialogAsync("打开失败", reason);
                return;
            }

            ShowAnimationClipWindow(filePath, clip!, true);
        }

        private void ShowAnimationClipWindow(string filePath, AnimationClipData clip, bool isVrmaSource)
        {
            string fileName = Path.GetFileName(filePath);
            EditorConfigData config = LoadEditorConfig(filePath);

            bool isDirty = false;
            bool isClosing = false;
            bool eulerMode = string.Equals(ReadEditorConfigValue(config, "rotationMode"), "euler", StringComparison.OrdinalIgnoreCase);
            bool curvesMode = string.Equals(ReadEditorConfigValue(config, "timelineMode"), "curves", StringComparison.OrdinalIgnoreCase);

            AnimationTimelineControl timeline = new AnimationTimelineControl
            {
                Clip = clip,
                CurvesMode = curvesMode
            };

            StackPanel rowPanel = new StackPanel { Orientation = Orientation.Vertical };
            List<Action> rowRefreshActions = new List<Action>();
            DispatcherTimer? playTimer = null;
            Window window = null!;

            TextBlock statusText = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.Parse("#9A9A9A")),
                VerticalAlignment = VerticalAlignment.Center
            };

            Button firstButton = CreateTextCommandButton("首帧", 64);
            Button previousFrameButton = CreateTextCommandButton("上一帧", 64);
            Button previousKeyButton = CreateTextCommandButton("上一关键帧", 96);
            Button playButton = CreateTextCommandButton("播放", 64);
            Button nextKeyButton = CreateTextCommandButton("下一关键帧", 96);
            Button nextFrameButton = CreateTextCommandButton("下一帧", 64);
            Button lastButton = CreateTextCommandButton("末帧", 64);

            TextBox positionBox = CreateInspectorTextBox("0");
            positionBox.Width = 96;

            TextBox frameRateBox = CreateInspectorTextBox(clip.FrameRate.ToString(CultureInfo.InvariantCulture));
            frameRateBox.Width = 64;

            TextBox durationBox = CreateInspectorTextBox(clip.Duration.ToString(CultureInfo.InvariantCulture));
            durationBox.Width = 96;

            CheckBox loopCheckBox = new CheckBox
            {
                Content = "循环",
                IsChecked = clip.Loop,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            CheckBox steppedCheckBox = new CheckBox
            {
                Content = "抽帧",
                IsChecked = string.Equals(clip.PlayMode, "stepped", StringComparison.OrdinalIgnoreCase),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            Button dopesheetButton = CreateTextCommandButton("关键帧", 80);
            Button curvesButton = CreateTextCommandButton("曲线", 80);
            Button addChannelButton = CreateTextCommandButton("加通道", 80);
            Button addKeyButton = CreateTextCommandButton("加关键帧", 96);
            Button deleteKeyButton = CreateTextCommandButton("删关键帧", 96);
            Button rotationModeButton = CreateTextCommandButton(eulerMode ? "欧拉" : "四元数", 80);
            Button saveButton = CreateTextCommandButton("保存", 80);

            void RefreshTitle()
            {
                window.Title = isDirty ? fileName + " *" : fileName;
            }

            void RefreshStatus()
            {
                statusText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "位置 {0:0.###} 选中 {1}",
                    timeline.PlayheadTime,
                    timeline.SelectedKeys.Count);
            }

            void RefreshRows()
            {
                foreach (Action action in rowRefreshActions)
                    action();
            }

            void MarkDirty()
            {
                isDirty = true;
                RefreshTitle();
            }

            void StopPlay()
            {
                if (playTimer == null)
                    return;

                playTimer.Stop();
                playTimer = null;
                playButton.Content = "播放";
            }

            void SeekTo(double time)
            {
                double duration = clip.Duration > 0.0 ? clip.Duration : 0.0;

                timeline.PlayheadTime = Math.Clamp(time, 0.0, duration);
                timeline.InvalidateVisual();

                positionBox.Text = timeline.PlayheadTime.ToString("0.###", CultureInfo.InvariantCulture);

                RefreshRows();
                RefreshStatus();
            }

            void SeekToKey(int direction)
            {
                double current = timeline.PlayheadTime;
                double best = direction < 0 ? -1.0 : double.MaxValue;

                foreach (AnimationChannel channel in clip.Channels)
                {
                    foreach (AnimationKeyframe keyframe in channel.Keys)
                    {
                        if (direction < 0)
                        {
                            if (keyframe.Time < current - 1e-6 && keyframe.Time > best)
                                best = keyframe.Time;
                        }
                        else
                        {
                            if (keyframe.Time > current + 1e-6 && keyframe.Time < best)
                                best = keyframe.Time;
                        }
                    }
                }

                if (direction < 0 && best < 0.0)
                    return;

                if (direction > 0 && Math.Abs(best - double.MaxValue) < 1e-6)
                    return;

                SeekTo(best);
            }

            void RebuildRows()
            {
                rowPanel.Children.Clear();
                rowRefreshActions.Clear();

                for (int index = 0; index < clip.Channels.Count; index++)
                {
                    AnimationChannel channel = clip.Channels[index];
                    rowPanel.Children.Add(BuildAnimationChannelRow(
                        channel,
                        index,
                        timeline,
                        () => eulerMode,
                        () =>
                        {
                            MarkDirty();
                            timeline.InvalidateVisual();
                        },
                        rowRefreshActions));
                }
            }

            void RefreshModeButtons()
            {
                dopesheetButton.Background = new SolidColorBrush(Color.Parse(curvesMode ? "#333333" : "#225588"));
                dopesheetButton.BorderBrush = new SolidColorBrush(Color.Parse(curvesMode ? "#555555" : "#4488CC"));
                curvesButton.Background = new SolidColorBrush(Color.Parse(curvesMode ? "#225588" : "#333333"));
                curvesButton.BorderBrush = new SolidColorBrush(Color.Parse(curvesMode ? "#4488CC" : "#555555"));
                rotationModeButton.Content = eulerMode ? "欧拉" : "四元数";
            }

            firstButton.Click += (_, _) => SeekTo(0.0);
            lastButton.Click += (_, _) => SeekTo(clip.Duration);
            previousKeyButton.Click += (_, _) => SeekToKey(-1);
            nextKeyButton.Click += (_, _) => SeekToKey(1);
            previousFrameButton.Click += (_, _) => SeekTo(timeline.PlayheadTime - 1.0 / Math.Max(clip.FrameRate, 1.0));
            nextFrameButton.Click += (_, _) => SeekTo(timeline.PlayheadTime + 1.0 / Math.Max(clip.FrameRate, 1.0));

            playButton.Click += (_, _) =>
            {
                if (playTimer != null)
                {
                    StopPlay();
                    return;
                }

                playTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16)
                };

                playTimer.Tick += (_, _) =>
                {
                    double duration = clip.Duration > 0.0 ? clip.Duration : 0.0;
                    double next = timeline.PlayheadTime + 0.016;

                    if (next >= duration)
                        next = clip.Loop ? 0.0 : duration;

                    SeekTo(next);

                    if (next >= duration && !clip.Loop)
                        StopPlay();
                };

                playTimer.Start();
                playButton.Content = "停止";
            };

            positionBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;

                if (!TryParseEditorDouble(positionBox.Text, out double value))
                {
                    positionBox.Text = timeline.PlayheadTime.ToString("0.###", CultureInfo.InvariantCulture);
                    return;
                }

                SeekTo(value);
                e.Handled = true;
            };

            frameRateBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;

                if (!TryParseEditorDouble(frameRateBox.Text, out double value) || value <= 0.0)
                {
                    frameRateBox.Text = clip.FrameRate.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                clip.FrameRate = (float)value;
                MarkDirty();
                e.Handled = true;
            };

            durationBox.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter)
                    return;

                if (!TryParseEditorDouble(durationBox.Text, out double value) || value < 0.0)
                {
                    durationBox.Text = clip.Duration.ToString(CultureInfo.InvariantCulture);
                    return;
                }

                clip.Duration = value;
                MarkDirty();
                timeline.InvalidateVisual();
                e.Handled = true;
            };

            loopCheckBox.Click += (_, _) =>
            {
                clip.Loop = loopCheckBox.IsChecked == true;
                MarkDirty();
            };

            steppedCheckBox.Click += (_, _) =>
            {
                clip.PlayMode = steppedCheckBox.IsChecked == true ? "stepped" : "normal";
                MarkDirty();
            };

            timeline.PlayheadMoved += () =>
            {
                positionBox.Text = timeline.PlayheadTime.ToString("0.###", CultureInfo.InvariantCulture);
                RefreshRows();
                RefreshStatus();
            };

            timeline.SelectionChanged += () => RefreshStatus();

            timeline.KeysEdited += () =>
            {
                MarkDirty();
                RefreshStatus();
            };

            dopesheetButton.Click += (_, _) =>
            {
                curvesMode = false;
                timeline.CurvesMode = false;
                WriteEditorConfigValue(config, "timelineMode", "dopesheet");
                SaveEditorConfig(filePath, config);
                RefreshModeButtons();
                RefreshStatus();
                timeline.InvalidateVisual();
            };

            curvesButton.Click += (_, _) =>
            {
                curvesMode = true;
                timeline.CurvesMode = true;
                WriteEditorConfigValue(config, "timelineMode", "curves");
                SaveEditorConfig(filePath, config);
                RefreshModeButtons();
                RefreshStatus();
                timeline.FrameAll();
            };

            rotationModeButton.Click += (_, _) =>
            {
                eulerMode = !eulerMode;
                WriteEditorConfigValue(config, "rotationMode", eulerMode ? "euler" : "quaternion");
                SaveEditorConfig(filePath, config);
                RefreshModeButtons();
                RefreshRows();
                timeline.InvalidateVisual();
            };

            addChannelButton.Click += (_, _) =>
            {
                clip.Channels.Add(new AnimationChannel
                {
                    Binding = "bone",
                    Path = "",
                    Property = "rotation",
                    ValueType = "quaternion",
                    Interpolation = "linear"
                });

                RebuildRows();
                MarkDirty();
                timeline.InvalidateVisual();
            };

            addKeyButton.Click += (_, _) =>
            {
                if (timeline.SelectedChannels.Count == 0)
                {
                    for (int index = 0; index < clip.Channels.Count; index++)
                        timeline.SelectedChannels.Add(index);
                }

                foreach (int channelIndex in timeline.SelectedChannels)
                {
                    if (channelIndex < 0 || channelIndex >= clip.Channels.Count)
                        continue;

                    AnimationChannel channel = clip.Channels[channelIndex];
                    int valueCount = ResolveAnimationValueCount(channel.ValueType);

                    if (valueCount <= 0)
                        continue;

                    AnimationKeyframe keyframe = InsertOrGetKeyframe(channel, timeline.PlayheadTime, 1e-6, valueCount);
                    ResolveDefaultKeyframeValue(channel, keyframe, valueCount);
                }

                MarkDirty();
                RefreshRows();
                RefreshStatus();
                timeline.InvalidateVisual();
            };

            deleteKeyButton.Click += (_, _) =>
            {
                foreach ((int Channel, int Key) entry in timeline.SelectedKeys.ToList())
                {
                    if (entry.Channel < 0 || entry.Channel >= clip.Channels.Count)
                        continue;

                    AnimationChannel channel = clip.Channels[entry.Channel];

                    if (entry.Key < 0 || entry.Key >= channel.Keys.Count)
                        continue;

                    channel.Keys.RemoveAt(entry.Key);
                }

                timeline.SelectedKeys.Clear();
                MarkDirty();
                RefreshRows();
                RefreshStatus();
                timeline.InvalidateVisual();
            };

            if (isVrmaSource)
                saveButton.Content = "转换为剪辑";

            saveButton.Click += (_, _) =>
            {
                if (isVrmaSource)
                {
                    _ = ConvertVrmaToClipAsync(filePath);
                    return;
                }

                try
                {
                    SaveAnimationClipFile(filePath, clip);
                    EditorHostBridge.RegisterAnimationClip(BuildAnimationAssetKey(filePath), clip);
                    SaveEditorConfig(filePath, config);
                    isDirty = false;
                    RefreshTitle();
                }
                catch (Exception ex)
                {
                    _ = ShowSimpleWarningDialogAsync("保存失败", ex.Message);
                }
            };

            StackPanel topBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(10, 6, 10, 6),
                Children =
                {
                    firstButton,
                    previousFrameButton,
                    previousKeyButton,
                    playButton,
                    nextKeyButton,
                    nextFrameButton,
                    lastButton,
                    positionBox,
                    frameRateBox,
                    durationBox,
                    loopCheckBox,
                    steppedCheckBox
                }
            };

            Border channelHeader = new Border
            {
                Height = AnimationTimelineRulerHeight,
                Background = new SolidColorBrush(Color.Parse("#252525")),
                BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = "通道",
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                }
            };

            Grid channelColumn = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };

            Grid.SetRow(channelHeader, 0);
            Grid.SetRow(rowPanel, 1);

            channelColumn.Children.Add(channelHeader);
            channelColumn.Children.Add(rowPanel);

            Grid bodyGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("300,*")
            };

            Grid.SetColumn(channelColumn, 0);
            Grid.SetColumn(timeline, 1);

            bodyGrid.Children.Add(channelColumn);
            bodyGrid.Children.Add(timeline);

            StackPanel bottomBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(10, 6, 10, 6),
                Children =
                {
                    dopesheetButton,
                    curvesButton,
                    addChannelButton,
                    addKeyButton,
                    deleteKeyButton,
                    rotationModeButton,
                    saveButton,
                    statusText
                }
            };

            Grid layout = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*,Auto")
            };

            Grid.SetRow(topBar, 0);
            Grid.SetRow(bodyGrid, 1);
            Grid.SetRow(bottomBar, 2);

            layout.Children.Add(topBar);
            layout.Children.Add(bodyGrid);
            layout.Children.Add(bottomBar);

            RebuildRows();
            RefreshModeButtons();
            RefreshStatus();

            window = new Window
            {
                Title = fileName,
                Width = 1180,
                Height = 700,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111")),
                Content = layout
            };

            window.Closing += (_, e) =>
            {
                if (!isDirty || isClosing)
                    return;

                e.Cancel = true;

                Dispatcher.UIThread.Post(async () =>
                {
                    bool confirmed = await ShowLuaExitConfirmDialog(window);

                    if (!confirmed)
                        return;

                    isClosing = true;
                    StopPlay();
                    window.Close();
                });
            };

            window.Closed += (_, _) => StopPlay();
            window.Show(this);
        }

        private Control BuildAnimationChannelRow(
            AnimationChannel channel,
            int channelIndex,
            AnimationTimelineControl timeline,
            Func<bool> isEulerMode,
            Action onEdited,
            List<Action> refreshActions)
        {
            TextBlock label = new TextBlock
            {
                Text = BuildChannelLabel(channel),
                Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 4, 0)
            };

            StackPanel valuePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            int valueCount = ResolveAnimationValueCount(channel.ValueType);
            List<TextBox> valueBoxes = new List<TextBox>();

            for (int component = 0; component < valueCount; component++)
            {
                TextBox box = CreateInspectorTextBox("0");
                box.Width = 62;
                box.Height = AnimationTimelineRowHeight - 4;
                box.MinHeight = AnimationTimelineRowHeight - 4;

                void Commit()
                {
                    int count = ResolveAnimationValueCount(channel.ValueType);

                    if (count <= 0)
                        return;

                    List<double> display = new List<double>();

                    foreach (TextBox textBox in valueBoxes)
                    {
                        if (!TryParseEditorDouble(textBox.Text, out double value))
                            return;

                        display.Add(value);
                    }

                    AnimationKeyframe keyframe = InsertOrGetKeyframe(channel, timeline.PlayheadTime, 1e-6, count);
                    ApplyChannelDisplayValues(channel, keyframe, display, isEulerMode());
                    onEdited();
                }

                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter)
                        return;

                    Commit();
                    e.Handled = true;
                };

                box.LostFocus += (_, _) => Commit();

                valueBoxes.Add(box);
                valuePanel.Children.Add(box);
            }

            void RefreshValues()
            {
                int count = ResolveAnimationValueCount(channel.ValueType);

                if (count <= 0)
                    return;

                AnimationKeyframe? keyframe = FindKeyframeAt(channel, timeline.PlayheadTime, 1e-4);
                List<float> sampled = new List<float>();

                for (int i = 0; i < count; i++)
                {
                    sampled.Add(keyframe != null && i < keyframe.Value.Length
                        ? keyframe.Value[i]
                        : (float)SampleChannelComponent(channel, i, timeline.PlayheadTime));
                }

                List<double> display = BuildChannelDisplayValues(channel, sampled, isEulerMode());

                for (int i = 0; i < valueBoxes.Count && i < display.Count; i++)
                    valueBoxes[i].Text = display[i].ToString("0.###", CultureInfo.InvariantCulture);
            }

            refreshActions.Add(RefreshValues);
            RefreshValues();

            Grid rowGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("172,*"),
                Height = AnimationTimelineRowHeight
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(valuePanel, 1);

            rowGrid.Children.Add(label);
            rowGrid.Children.Add(valuePanel);

            Border row = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = new SolidColorBrush(Color.Parse("#2A2A2A")),
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = rowGrid
            };

            row.PointerPressed += (_, e) =>
            {
                PointerPoint point = e.GetCurrentPoint(row);

                if (!point.Properties.IsLeftButtonPressed)
                    return;

                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    timeline.SelectedChannels.Clear();

                if (!timeline.SelectedChannels.Contains(channelIndex))
                    timeline.SelectedChannels.Add(channelIndex);

                row.Background = new SolidColorBrush(Color.Parse("#113355"));
                timeline.InvalidateVisual();
            };

            return row;
        }

        private static Dictionary<string, Point> LoadNodeLayout(EditorConfigData config, string layerName)
        {
            Dictionary<string, Point> result = new Dictionary<string, Point>(StringComparer.Ordinal);
            string prefix = layerName + "::";

            if (!config.Values.TryGetValue("nodeLayout", out JsonElement element) || element.ValueKind != JsonValueKind.Object)
                return result;

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!property.Name.StartsWith(prefix, StringComparison.Ordinal))
                    continue;

                if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() < 2)
                    continue;

                string stateName = property.Name[prefix.Length..];
                result[stateName] = new Point(
                    property.Value[0].GetDouble(),
                    property.Value[1].GetDouble());
            }

            return result;
        }

        private static void SaveNodeLayout(EditorConfigData config, string layerName, Dictionary<string, Point> positions)
        {
            Dictionary<string, double[]> merged = new Dictionary<string, double[]>(StringComparer.Ordinal);
            string prefix = layerName + "::";

            if (config.Values.TryGetValue("nodeLayout", out JsonElement element) && element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Name.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() < 2)
                        continue;

                    merged[property.Name] = new[]
                    {
                        property.Value[0].GetDouble(),
                        property.Value[1].GetDouble()
                    };
                }
            }

            foreach (KeyValuePair<string, Point> pair in positions)
                merged[prefix + pair.Key] = new[] { pair.Value.X, pair.Value.Y };

            config.Values["nodeLayout"] = JsonSerializer.SerializeToElement(merged, SceneJsonWriteOptions);
        }

        private void ShowAnimatorControllerWindow(string filePath)
        {
            AnimatorControllerData? controller = EditorHostBridge.LoadAnimatorController(filePath);

            if (controller == null)
            {
                _ = ShowSimpleWarningDialogAsync("打开失败", "无法解析动画器控制器 " + Path.GetFileName(filePath));
                return;
            }

            string fileName = Path.GetFileName(filePath);
            EditorConfigData config = LoadEditorConfig(filePath);

            bool isDirty = false;
            bool isClosing = false;
            int currentLayerIndex = 0;

            AnimatorStateData? selectedState = null;
            AnimatorTransitionData? selectedTransition = null;

            AnimatorGraphControl graph = new AnimatorGraphControl();
            StackPanel parameterPanel = new StackPanel { Orientation = Orientation.Vertical };
            StackPanel transitionPanel = new StackPanel { Orientation = Orientation.Vertical };
            StackPanel inspectorPanel = new StackPanel { Orientation = Orientation.Vertical };
            StackPanel layerButtonPanel = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
            Button addLayerButton = CreateTextCommandButton("加层", 80);
            Window window = null!;

            void RefreshTitle()
            {
                window.Title = isDirty ? fileName + " *" : fileName;
            }

            void MarkDirty()
            {
                isDirty = true;
                RefreshTitle();
            }

            AnimatorLayerData? CurrentLayer()
            {
                if (currentLayerIndex < 0 || currentLayerIndex >= controller.Layers.Count)
                    return null;

                return controller.Layers[currentLayerIndex];
            }

            void SyncGraphLayer()
            {
                AnimatorLayerData? layer = CurrentLayer();

                graph.Layer = layer;
                graph.NodePositions.Clear();

                if (layer == null)
                {
                    graph.InvalidateVisual();
                    return;
                }

                foreach (KeyValuePair<string, Point> pair in LoadNodeLayout(config, layer.Name))
                    graph.NodePositions[pair.Key] = pair.Value;

                graph.InvalidateVisual();
            }

            Control BuildTextRow(string label, string value, Action<string> apply)
            {
                TextBox box = CreateInspectorTextBox(value);
                TextBox captured = box;

                void Commit()
                {
                    apply(captured.Text ?? string.Empty);
                    MarkDirty();
                }

                box.KeyDown += (_, e) =>
                {
                    if (e.Key != Key.Enter)
                        return;

                    Commit();
                    e.Handled = true;
                };

                box.LostFocus += (_, _) => Commit();

                return CreatePropertyRow(label, box);
            }

            Control BuildCheckRow(string label, bool value, Action<bool> apply)
            {
                CheckBox checkBox = new CheckBox
                {
                    IsChecked = value,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                };

                checkBox.Click += (_, _) =>
                {
                    apply(checkBox.IsChecked == true);
                    MarkDirty();
                };

                return CreatePropertyRow(label, checkBox);
            }

            Control BuildComboRow(string label, string value, IReadOnlyList<string> options, Action<string> apply)
            {
                ComboBox comboBox = new ComboBox
                {
                    ItemsSource = options,
                    SelectedItem = value,
                    Height = 30,
                    MinHeight = 30,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };

                comboBox.SelectionChanged += (_, _) =>
                {
                    apply(comboBox.SelectedItem as string ?? string.Empty);
                    MarkDirty();
                };

                return CreatePropertyRow(label, comboBox);
            }

            void RebuildParameterPanel()
            {
                parameterPanel.Children.Clear();
                parameterPanel.Children.Add(CreateInspectorSectionHeader("参数"));

                for (int index = 0; index < controller.Parameters.Count; index++)
                {
                    AnimatorParameterData parameter = controller.Parameters[index];
                    AnimatorParameterData captured = parameter;

                    TextBox nameBox = CreateInspectorTextBox(captured.Name);
                    nameBox.Width = 120;

                    ComboBox typeBox = new ComboBox
                    {
                        ItemsSource = AnimatorParameterTypes,
                        SelectedItem = captured.Type,
                        Width = 80,
                        Height = 30,
                        MinHeight = 30,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    TextBox defaultBox = CreateInspectorTextBox(captured.Default.ToString(CultureInfo.InvariantCulture));
                    defaultBox.Width = 70;

                    nameBox.LostFocus += (_, _) =>
                    {
                        captured.Name = nameBox.Text ?? string.Empty;
                        MarkDirty();
                    };

                    typeBox.SelectionChanged += (_, _) =>
                    {
                        captured.Type = typeBox.SelectedItem as string ?? "float";
                        MarkDirty();
                    };

                    defaultBox.LostFocus += (_, _) =>
                    {
                        if (!TryParseEditorDouble(defaultBox.Text, out double value))
                            return;

                        captured.Default = value;
                        MarkDirty();
                    };

                    StackPanel parameterRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Margin = new Thickness(8, 2, 8, 2),
                        Children = { nameBox, typeBox, defaultBox }
                    };

                    parameterRow.ContextMenu = CreateAnimatorDeleteContextMenu("删除参数", () =>
                    {
                        controller.Parameters.Remove(captured);
                        RebuildParameterPanel();
                        MarkDirty();
                    });

                    parameterPanel.Children.Add(parameterRow);
                }
            }

            void RebuildTransitionPanel()
            {
                transitionPanel.Children.Clear();
                transitionPanel.Children.Add(CreateInspectorSectionHeader("过渡"));

                AnimatorLayerData? layer = CurrentLayer();

                if (layer == null)
                    return;

                foreach (AnimatorTransitionData transition in layer.Transitions)
                    AddTransitionEntry(layer, transition, false);

                foreach (AnimatorTransitionData transition in layer.AnyStateTransitions)
                    AddTransitionEntry(layer, transition, true);
            }

            void AddTransitionEntry(AnimatorLayerData layer, AnimatorTransitionData transition, bool isAnyState)
            {
                AnimatorTransitionData captured = transition;
                AnimatorLayerData capturedLayer = layer;

                string label = (isAnyState ? "AnyState → " : (string.IsNullOrWhiteSpace(transition.From) ? "→ " : transition.From + " → ")) + transition.To;

                Button entryButton = new Button
                {
                    Content = label,
                    Height = InspectorTextBoxHeight,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(6, 0, 6, 0),
                    Margin = new Thickness(8, 1, 8, 1),
                    Background = new SolidColorBrush(Color.Parse("#242424")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
                    BorderThickness = new Thickness(1),
                    Foreground = Brushes.White
                };

                entryButton.Click += (_, _) =>
                {
                    selectedState = null;
                    selectedTransition = captured;
                    graph.SelectedState = null;
                    graph.SelectedTransition = captured;
                    RebuildInspector();
                    graph.InvalidateVisual();
                };

                Button removeButton = CreateTextCommandButton("删", 36);
                removeButton.Height = InspectorTextBoxHeight;

                removeButton.Click += (_, _) =>
                {
                    capturedLayer.Transitions.Remove(captured);
                    capturedLayer.AnyStateTransitions.Remove(captured);

                    if (ReferenceEquals(selectedTransition, captured))
                        selectedTransition = null;

                    RebuildTransitionPanel();
                    RebuildInspector();
                    MarkDirty();
                };

                Grid entryGrid = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Margin = new Thickness(8, 1, 8, 1)
                };

                Grid.SetColumn(entryButton, 0);
                Grid.SetColumn(removeButton, 1);

                entryButton.Margin = new Thickness(0, 0, 4, 0);

                entryGrid.Children.Add(entryButton);
                entryGrid.Children.Add(removeButton);

                entryGrid.ContextMenu = CreateAnimatorDeleteContextMenu("删除过渡", () =>
                {
                    capturedLayer.Transitions.Remove(captured);
                    capturedLayer.AnyStateTransitions.Remove(captured);

                    if (ReferenceEquals(selectedTransition, captured))
                        selectedTransition = null;

                    RebuildTransitionPanel();
                    RebuildInspector();
                    MarkDirty();
                });

                transitionPanel.Children.Add(entryGrid);
            }

            void RebuildLayerButtons()
            {
                layerButtonPanel.Children.Clear();

                layerButtonPanel.Children.Add(addLayerButton);

                for (int index = 0; index < controller.Layers.Count; index++)
                {
                    int capturedIndex = index;
                    AnimatorLayerData layer = controller.Layers[index];

                    Button button = CreateTextCommandButton(
                        string.IsNullOrWhiteSpace(layer.Name) ? "层" + index.ToString(CultureInfo.InvariantCulture) : layer.Name,
                        160);

                    button.HorizontalAlignment = HorizontalAlignment.Stretch;

                    bool active = index == currentLayerIndex;

                    button.Background = new SolidColorBrush(Color.Parse(active ? "#225588" : "#333333"));
                    button.BorderBrush = new SolidColorBrush(Color.Parse(active ? "#4488CC" : "#555555"));

                    button.Click += (_, _) =>
                    {
                        currentLayerIndex = capturedIndex;
                        selectedState = null;
                        selectedTransition = null;
                        graph.SelectedState = null;
                        graph.SelectedTransition = null;
                        SyncGraphLayer();
                        graph.FrameAll();
                        RebuildLayerButtons();
                        RebuildTransitionPanel();
                        RebuildInspector();
                    };

                    button.ContextMenu = CreateAnimatorDeleteContextMenu("删除层", () =>
                    {
                        controller.Layers.Remove(layer);

                        if (currentLayerIndex >= controller.Layers.Count)
                            currentLayerIndex = Math.Max(0, controller.Layers.Count - 1);

                        selectedState = null;
                        selectedTransition = null;
                        graph.SelectedState = null;
                        graph.SelectedTransition = null;

                        SyncGraphLayer();
                        RebuildLayerButtons();
                        RebuildTransitionPanel();
                        RebuildInspector();
                        MarkDirty();
                    });

                    layerButtonPanel.Children.Add(button);
                }
            }

            void RebuildInspector()
            {
                inspectorPanel.Children.Clear();

                if (selectedState != null)
                {
                    BuildStateInspector(selectedState);
                    return;
                }

                if (selectedTransition != null)
                {
                    BuildTransitionInspector(selectedTransition);
                    return;
                }

                inspectorPanel.Children.Add(CreateInspectorSectionHeader("选择状态或过渡"));
            }

            void BuildStateInspector(AnimatorStateData state)
            {
                AnimatorStateData captured = state;

                inspectorPanel.Children.Add(CreateInspectorSectionHeader("状态"));

                inspectorPanel.Children.Add(BuildTextRow("名称", captured.Name, value => captured.Name = value));
                inspectorPanel.Children.Add(BuildTextRow("速度", captured.Speed.ToString(CultureInfo.InvariantCulture), value =>
                {
                    if (TryParseEditorDouble(value, out double parsed))
                        captured.Speed = parsed;
                }));

                inspectorPanel.Children.Add(BuildCheckRow("循环", captured.Loop, value => captured.Loop = value));
                inspectorPanel.Children.Add(BuildComboRow(
                    "播放模式",
                    string.IsNullOrWhiteSpace(captured.PlayMode) ? AnimatorPlayModes[0] : captured.PlayMode,
                    AnimatorPlayModes,
                    value => captured.PlayMode = string.Equals(value, AnimatorPlayModes[0], StringComparison.Ordinal) ? "" : value));

                inspectorPanel.Children.Add(CreateInspectorSectionHeader("运动"));

                inspectorPanel.Children.Add(BuildComboRow("类型", captured.Motion.Type, AnimatorMotionTypes, value =>
                {
                    captured.Motion.Type = value;
                    RebuildInspector();
                    graph.InvalidateVisual();
                }));

                if (string.Equals(captured.Motion.Type, "blend", StringComparison.OrdinalIgnoreCase))
                {
                    inspectorPanel.Children.Add(BuildComboRow("混合模式", captured.Motion.Mode, AnimatorBlendModes, value => captured.Motion.Mode = value));
                    inspectorPanel.Children.Add(BuildTextRow("衰减", captured.Motion.Falloff.ToString(CultureInfo.InvariantCulture), value =>
                    {
                        if (TryParseEditorDouble(value, out double parsed))
                            captured.Motion.Falloff = (float)parsed;
                    }));

                    inspectorPanel.Children.Add(CreateInspectorSectionHeader("混合参数"));

                    for (int parameterIndex = 0; parameterIndex < captured.Motion.Parameters.Count; parameterIndex++)
                    {
                        int capturedParameterIndex = parameterIndex;

                        ComboBox parameterComboBox = new ComboBox
                        {
                            ItemsSource = controller.Parameters.Select(item => item.Name).ToList(),
                            SelectedItem = captured.Motion.Parameters[capturedParameterIndex],
                            Height = 30,
                            MinHeight = 30,
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            VerticalAlignment = VerticalAlignment.Center
                        };

                        parameterComboBox.SelectionChanged += (_, _) =>
                        {
                            captured.Motion.Parameters[capturedParameterIndex] = parameterComboBox.SelectedItem as string ?? string.Empty;
                            MarkDirty();
                        };

                        Border parameterRow = new Border
                        {
                            Margin = new Thickness(12, 2, 12, 2),
                            Child = parameterComboBox,
                            ContextMenu = CreateAnimatorDeleteContextMenu("删除参数", () =>
                            {
                                captured.Motion.Parameters.RemoveAt(capturedParameterIndex);

                                foreach (AnimatorBlendEntryData entry in captured.Motion.Entries)
                                {
                                    if (entry.Position.Length <= capturedParameterIndex)
                                        continue;

                                    double[] resized = new double[entry.Position.Length - 1];

                                    for (int i = 0, w = 0; i < entry.Position.Length; i++)
                                    {
                                        if (i == capturedParameterIndex)
                                            continue;

                                        resized[w++] = entry.Position[i];
                                    }

                                    entry.Position = resized;
                                }

                                RebuildInspector();
                                MarkDirty();
                            })
                        };

                        inspectorPanel.Children.Add(parameterRow);
                    }

                    Button addParameterMotionButton = CreateTextCommandButton("加混合参数", 108);
                    addParameterMotionButton.Margin = new Thickness(12, 4, 12, 4);

                    addParameterMotionButton.Click += (_, _) =>
                    {
                        captured.Motion.Parameters.Add(string.Empty);

                        foreach (AnimatorBlendEntryData entry in captured.Motion.Entries)
                        {
                            double[] resized = new double[entry.Position.Length + 1];
                            Array.Copy(entry.Position, resized, entry.Position.Length);
                            entry.Position = resized;
                        }

                        RebuildInspector();
                        MarkDirty();
                    };

                    inspectorPanel.Children.Add(addParameterMotionButton);

                    inspectorPanel.Children.Add(CreateInspectorSectionHeader("混合表格"));

                    inspectorPanel.Children.Add(BuildBlendTable(captured));

                    Button addEntryButton = CreateTextCommandButton("加条目", 96);
                    addEntryButton.Margin = new Thickness(12, 4, 12, 4);

                    addEntryButton.Click += (_, _) =>
                    {
                        captured.Motion.Entries.Add(new AnimatorBlendEntryData
                        {
                            Clip = "",
                            Position = new double[captured.Motion.Parameters.Count],
                            Weights = Array.Empty<float>()
                        });

                        RebuildInspector();
                        MarkDirty();
                    };

                    inspectorPanel.Children.Add(addEntryButton);
                }
                else
                {
                    inspectorPanel.Children.Add(BuildTextRow("剪辑", captured.Motion.Clip, value =>
                    {
                        captured.Motion.Clip = value;
                        graph.InvalidateVisual();
                    }));
                }
            }

            Control BuildBlendTable(AnimatorStateData state)
            {
                AnimatorMotionData motion = state.Motion;
                int dimensionCount = motion.Parameters.Count;

                StackPanel table = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Margin = new Thickness(12, 0, 12, 0)
                };

                StackPanel headerRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Height = InspectorTextBoxHeight
                };

                headerRow.Children.Add(new TextBlock
                {
                    Text = "剪辑",
                    Width = 120,
                    Foreground = Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center
                });

                for (int i = 0; i < dimensionCount; i++)
                {
                    headerRow.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrWhiteSpace(motion.Parameters[i])
                            ? "参数" + i.ToString(CultureInfo.InvariantCulture)
                            : motion.Parameters[i],
                        Width = 74,
                        Foreground = Brushes.White,
                        VerticalAlignment = VerticalAlignment.Center
                    });
                }

                table.Children.Add(headerRow);

                for (int entryIndex = 0; entryIndex < motion.Entries.Count; entryIndex++)
                {
                    int capturedEntryIndex = entryIndex;
                    AnimatorBlendEntryData entry = motion.Entries[entryIndex];

                    StackPanel entryRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Height = InspectorTextBoxHeight,
                        Margin = new Thickness(0, 0, 0, 2)
                    };

                    TextBox clipBox = CreateInspectorTextBox(entry.Clip);
                    clipBox.Width = 120;
                    clipBox.Margin = new Thickness(0, 0, 4, 0);

                    clipBox.LostFocus += (_, _) =>
                    {
                        entry.Clip = clipBox.Text ?? string.Empty;
                        graph.InvalidateVisual();
                        MarkDirty();
                    };

                    entryRow.Children.Add(clipBox);

                    for (int i = 0; i < dimensionCount; i++)
                    {
                        int componentIndex = i;
                        double current = i < entry.Position.Length ? entry.Position[i] : 0.0;

                        TextBox positionBox = CreateInspectorTextBox(current.ToString(CultureInfo.InvariantCulture));
                        positionBox.Width = 74;
                        positionBox.Margin = new Thickness(0, 0, 4, 0);

                        positionBox.LostFocus += (_, _) =>
                        {
                            if (!TryParseEditorDouble(positionBox.Text, out double parsed))
                                return;

                            if (entry.Position.Length != motion.Parameters.Count)
                            {
                                double[] resized = new double[motion.Parameters.Count];
                                Array.Copy(entry.Position, resized, Math.Min(entry.Position.Length, resized.Length));
                                entry.Position = resized;
                            }

                            entry.Position[componentIndex] = parsed;
                            MarkDirty();
                        };

                        entryRow.Children.Add(positionBox);
                    }

                    Border entryHost = new Border
                    {
                        Child = entryRow,
                        Background = Brushes.Transparent,
                        ContextMenu = CreateAnimatorDeleteContextMenu("删除条目", () =>
                        {
                            motion.Entries.RemoveAt(capturedEntryIndex);
                            RebuildInspector();
                            MarkDirty();
                        })
                    };

                    table.Children.Add(entryHost);
                }

                return table;
            }

            void BuildTransitionInspector(AnimatorTransitionData transition)
            {
                AnimatorTransitionData captured = transition;
                AnimatorLayerData? layer = CurrentLayer();

                inspectorPanel.Children.Add(CreateInspectorSectionHeader("过渡"));

                List<string> stateNames = layer == null
                    ? new List<string>()
                    : layer.States.Select(item => item.Name).ToList();

                inspectorPanel.Children.Add(BuildComboRow("起点", captured.From, stateNames, value =>
                {
                    captured.From = value;
                    graph.InvalidateVisual();
                }));

                inspectorPanel.Children.Add(BuildComboRow("终点", captured.To, stateNames, value =>
                {
                    captured.To = value;
                    graph.InvalidateVisual();
                }));

                inspectorPanel.Children.Add(BuildCheckRow("退出时间", captured.HasExitTime, value => captured.HasExitTime = value));
                inspectorPanel.Children.Add(BuildTextRow("退出比例", captured.ExitTime.ToString(CultureInfo.InvariantCulture), value =>
                {
                    if (TryParseEditorDouble(value, out double parsed))
                        captured.ExitTime = parsed;
                }));

                inspectorPanel.Children.Add(BuildCheckRow("固定时长", captured.FixedDuration, value => captured.FixedDuration = value));
                inspectorPanel.Children.Add(BuildTextRow("时长", captured.Duration.ToString(CultureInfo.InvariantCulture), value =>
                {
                    if (TryParseEditorDouble(value, out double parsed))
                        captured.Duration = parsed;
                }));

                inspectorPanel.Children.Add(BuildTextRow("偏移", captured.Offset.ToString(CultureInfo.InvariantCulture), value =>
                {
                    if (TryParseEditorDouble(value, out double parsed))
                        captured.Offset = parsed;
                }));

                inspectorPanel.Children.Add(BuildComboRow("打断来源", captured.InterruptionSource, AnimatorInterruptionSources, value => captured.InterruptionSource = value));
                inspectorPanel.Children.Add(BuildCheckRow("有序打断", captured.OrderedInterruption, value => captured.OrderedInterruption = value));
                inspectorPanel.Children.Add(BuildCheckRow("可自过渡", captured.CanTransitionToSelf, value => captured.CanTransitionToSelf = value));

                inspectorPanel.Children.Add(CreateInspectorSectionHeader("条件"));

                List<string> parameterNames = controller.Parameters.Select(item => item.Name).ToList();

                foreach (AnimatorConditionData condition in captured.Conditions)
                {
                    AnimatorConditionData capturedCondition = condition;

                    ComboBox parameterBox = new ComboBox
                    {
                        ItemsSource = parameterNames,
                        SelectedItem = capturedCondition.Parameter,
                        Width = 96,
                        Height = 30,
                        MinHeight = 30,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    ComboBox modeBox = new ComboBox
                    {
                        ItemsSource = AnimatorConditionModes,
                        SelectedItem = capturedCondition.Mode,
                        Width = 90,
                        Height = 30,
                        MinHeight = 30,
                        VerticalAlignment = VerticalAlignment.Center
                    };

                    TextBox thresholdBox = CreateInspectorTextBox(capturedCondition.Threshold.ToString(CultureInfo.InvariantCulture));
                    thresholdBox.Width = 70;

                    parameterBox.SelectionChanged += (_, _) =>
                    {
                        capturedCondition.Parameter = parameterBox.SelectedItem as string ?? string.Empty;
                        MarkDirty();
                    };

                    modeBox.SelectionChanged += (_, _) =>
                    {
                        capturedCondition.Mode = modeBox.SelectedItem as string ?? "if";
                        MarkDirty();
                    };

                    thresholdBox.LostFocus += (_, _) =>
                    {
                        if (!TryParseEditorDouble(thresholdBox.Text, out double parsed))
                            return;

                        capturedCondition.Threshold = parsed;
                        MarkDirty();
                    };

                    StackPanel conditionRow = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 4,
                        Margin = new Thickness(12, 2, 12, 2),
                        Children = { parameterBox, modeBox, thresholdBox }
                    };

                    conditionRow.ContextMenu = CreateAnimatorDeleteContextMenu("删除条件", () =>
                    {
                        captured.Conditions.Remove(capturedCondition);
                        RebuildInspector();
                        MarkDirty();
                    });

                    inspectorPanel.Children.Add(conditionRow);
                }

                Button addConditionButton = CreateTextCommandButton("加条件", 96);
                addConditionButton.Margin = new Thickness(12, 4, 12, 4);

                addConditionButton.Click += (_, _) =>
                {
                    captured.Conditions.Add(new AnimatorConditionData
                    {
                        Parameter = "",
                        Mode = "if",
                        Threshold = 0.0
                    });

                    RebuildInspector();
                    MarkDirty();
                };

                inspectorPanel.Children.Add(addConditionButton);

                if (layer == null)
                    return;

                Button removeTransitionButton = CreateTextCommandButton("删过渡", 96);
                removeTransitionButton.Margin = new Thickness(12, 12, 12, 4);

                removeTransitionButton.Click += (_, _) =>
                {
                    layer.Transitions.Remove(captured);
                    layer.AnyStateTransitions.Remove(captured);
                    selectedTransition = null;
                    RebuildTransitionPanel();
                    RebuildInspector();
                    MarkDirty();
                };

                inspectorPanel.Children.Add(removeTransitionButton);
            }

            graph.SelectionChanged += () =>
            {
                selectedState = null;
                selectedTransition = null;

                AnimatorLayerData? layer = CurrentLayer();

                if (layer != null && !string.IsNullOrWhiteSpace(graph.SelectedState))
                {
                    foreach (AnimatorStateData state in layer.States)
                    {
                        if (state != null && string.Equals(state.Name, graph.SelectedState, StringComparison.Ordinal))
                        {
                            selectedState = state;
                            break;
                        }
                    }
                }

                RebuildInspector();
            };

            graph.LayoutChanged += () =>
            {
                AnimatorLayerData? layer = CurrentLayer();

                if (layer == null)
                    return;

                SaveNodeLayout(config, layer.Name, graph.NodePositions);
                SaveEditorConfig(filePath, config);
            };

            graph.ContextMenu = new ContextMenu
            {
                ItemsSource = new object[]
                {
                    new MenuItem { Header = "删除状态" }
                }
            };

            foreach (object item in graph.ContextMenu.ItemsSource)
            {
                if (item is not MenuItem deleteStateItem)
                    continue;

                deleteStateItem.Click += (_, _) =>
                {
                    AnimatorLayerData? layer = CurrentLayer();

                    if (layer == null || string.IsNullOrWhiteSpace(graph.SelectedState))
                        return;

                    string stateName = graph.SelectedState;

                    for (int index = layer.States.Count - 1; index >= 0; index--)
                    {
                        if (string.Equals(layer.States[index].Name, stateName, StringComparison.Ordinal))
                            layer.States.RemoveAt(index);
                    }

                    for (int index = layer.Transitions.Count - 1; index >= 0; index--)
                    {
                        AnimatorTransitionData transition = layer.Transitions[index];

                        if (string.Equals(transition.From, stateName, StringComparison.Ordinal) ||
                            string.Equals(transition.To, stateName, StringComparison.Ordinal))
                            layer.Transitions.RemoveAt(index);
                    }

                    for (int index = layer.AnyStateTransitions.Count - 1; index >= 0; index--)
                    {
                        AnimatorTransitionData transition = layer.AnyStateTransitions[index];

                        if (string.Equals(transition.To, stateName, StringComparison.Ordinal))
                            layer.AnyStateTransitions.RemoveAt(index);
                    }

                    if (string.Equals(layer.DefaultState, stateName, StringComparison.Ordinal))
                        layer.DefaultState = layer.States.Count > 0 ? layer.States[0].Name : "";

                    if (selectedState != null && string.Equals(selectedState.Name, stateName, StringComparison.Ordinal))
                        selectedState = null;

                    if (selectedTransition != null &&
                        (string.Equals(selectedTransition.From, stateName, StringComparison.Ordinal) ||
                         string.Equals(selectedTransition.To, stateName, StringComparison.Ordinal)))
                        selectedTransition = null;

                    graph.SelectedState = null;
                    graph.SelectedTransition = null;
                    graph.NodePositions.Remove(stateName);
                    graph.InvalidateVisual();

                    RebuildTransitionPanel();
                    RebuildInspector();
                    MarkDirty();
                };
            }

            ContextMenu graphContextMenu = graph.ContextMenu!;

            graphContextMenu.Opening += (_, _) =>
            {
                AnimatorLayerData? layer = CurrentLayer();

                bool hasState = layer != null && !string.IsNullOrWhiteSpace(graph.SelectedState);

                graphContextMenu.IsEnabled = hasState;
            };

            Button saveButton = CreateTextCommandButton("保存", 80);
            Button addStateButton = CreateTextCommandButton("加状态", 80);
            Button addTransitionButton = CreateTextCommandButton("加过渡", 80);
            Button addParameterButton = CreateTextCommandButton("加参数", 80);
            Button frameAllButton = CreateTextCommandButton("适应", 80);

            void SaveAll()
            {
                try
                {
                    SaveAnimatorControllerFile(filePath, controller);
                    EditorHostBridge.RegisterAnimatorController(BuildAnimationAssetKey(filePath), controller);
                    SaveEditorConfig(filePath, config);
                    isDirty = false;
                    RefreshTitle();
                }
                catch (Exception ex)
                {
                    _ = ShowSimpleWarningDialogAsync("保存失败", ex.Message);
                }
            }

            saveButton.Click += (_, _) => SaveAll();

            addLayerButton.Click += (_, _) =>
            {
                controller.Layers.Add(new AnimatorLayerData
                {
                    Name = "Layer" + controller.Layers.Count.ToString(CultureInfo.InvariantCulture),
                    Weight = 1.0,
                    BlendMode = "override"
                });

                currentLayerIndex = controller.Layers.Count - 1;
                SyncGraphLayer();
                RebuildLayerButtons();
                RebuildTransitionPanel();
                RebuildInspector();
                MarkDirty();
            };

            addStateButton.Click += (_, _) =>
            {
                AnimatorLayerData? layer = CurrentLayer();

                if (layer == null)
                    return;

                string name = "State" + layer.States.Count.ToString(CultureInfo.InvariantCulture);

                layer.States.Add(new AnimatorStateData
                {
                    Name = name,
                    Motion = new AnimatorMotionData()
                });

                if (string.IsNullOrWhiteSpace(layer.DefaultState))
                    layer.DefaultState = name;

                graph.EnsureNodePosition(name);
                SyncGraphLayer();
                RebuildInspector();
                MarkDirty();
            };

            addTransitionButton.Click += (_, _) =>
            {
                AnimatorLayerData? layer = CurrentLayer();

                if (layer == null || layer.States.Count == 0)
                    return;

                AnimatorTransitionData transition = new AnimatorTransitionData
                {
                    From = layer.States[0].Name,
                    To = layer.States[layer.States.Count - 1].Name
                };

                layer.Transitions.Add(transition);
                selectedState = null;
                selectedTransition = transition;
                graph.SelectedState = null;
                graph.SelectedTransition = transition;
                RebuildTransitionPanel();
                RebuildInspector();
                MarkDirty();
            };

            addParameterButton.Click += (_, _) =>
            {
                controller.Parameters.Add(new AnimatorParameterData
                {
                    Name = "Param" + controller.Parameters.Count.ToString(CultureInfo.InvariantCulture),
                    Type = "float",
                    Default = 0.0
                });

                RebuildParameterPanel();
                MarkDirty();
            };

            frameAllButton.Click += (_, _) => graph.FrameAll();

            StackPanel topBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Margin = new Thickness(10, 6, 10, 6),
                Children =
                {
                    addStateButton,
                    addTransitionButton,
                    addParameterButton,
                    frameAllButton,
                    saveButton
                }
            };

            Grid layout = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,*")
            };

            ScrollViewer layerScrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = layerButtonPanel
            };

            Border layerPanel = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#181818")),
                BorderBrush = new SolidColorBrush(Color.Parse("#333333")),
                BorderThickness = new Thickness(0, 0, 1, 0),
                Padding = new Thickness(8, 6, 8, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children =
                    {
                        CreateInspectorSectionHeader("层"),
                        layerScrollViewer
                    }
                }
            };

            ScrollViewer leftScrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Children = { parameterPanel, transitionPanel }
                }
            };

            ScrollViewer inspectorScrollViewer = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = inspectorPanel
            };

            Grid bodyGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("180,280,*,320")
            };

            Grid.SetColumn(layerPanel, 0);
            Grid.SetColumn(leftScrollViewer, 1);
            Grid.SetColumn(graph, 2);
            Grid.SetColumn(inspectorScrollViewer, 3);

            bodyGrid.Children.Add(layerPanel);
            bodyGrid.Children.Add(leftScrollViewer);
            bodyGrid.Children.Add(graph);
            bodyGrid.Children.Add(inspectorScrollViewer);

            Grid.SetRow(topBar, 0);
            Grid.SetRow(bodyGrid, 1);

            layout.Children.Add(topBar);
            layout.Children.Add(bodyGrid);

            RebuildParameterPanel();
            RebuildLayerButtons();
            SyncGraphLayer();
            RebuildTransitionPanel();
            RebuildInspector();

            window = new Window
            {
                Title = fileName,
                Width = 1180,
                Height = 700,
                CanMinimize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowDecorations = WindowDecorations.Full,
                ShowInTaskbar = false,
                Background = new SolidColorBrush(Color.Parse("#111111")),
                Content = layout
            };

            window.KeyDown += (_, e) =>
            {
                if (e.Key != Key.S || !HasPrimaryCommandModifier(e.KeyModifiers))
                    return;

                SaveAll();
                e.Handled = true;
            };

            window.Closing += (_, e) =>
            {
                if (!isDirty || isClosing)
                    return;

                e.Cancel = true;

                Dispatcher.UIThread.Post(async () =>
                {
                    bool confirmed = await ShowLuaExitConfirmDialog(window);

                    if (!confirmed)
                        return;

                    isClosing = true;
                    window.Close();
                });
            };

            window.Show(this);
            Dispatcher.UIThread.Post(() => graph.FrameAll(), DispatcherPriority.Background);
        }

        private static readonly string[] AnimatorParameterTypes = { "float", "int", "bool", "trigger" };

        private static readonly string[] AnimatorPlayModes = { "继承剪辑", "normal", "stepped" };

        private static readonly string[] AnimatorMotionTypes = { "clip", "blend" };

        private static readonly string[] AnimatorBlendModes = { "inverseDistance", "direct" };

        private static readonly string[] AnimatorInterruptionSources =
        {
            "none",
            "currentState",
            "nextState",
            "currentStateThenNextState",
            "nextStateThenCurrentState"
        };

        private static readonly string[] AnimatorConditionModes =
        {
            "if",
            "ifnot",
            "greater",
            "less",
            "equals",
            "notequal",
            "exitTime"
        };

        private ContextMenu CreateAnimatorDeleteContextMenu(string header, Action onDelete)
        {
            MenuItem deleteItem = new MenuItem { Header = header };

            deleteItem.Click += (_, _) => onDelete();

            return new ContextMenu
            {
                ItemsSource = new object[]
                {
                    deleteItem
                }
            };
        }

        private double[] ParseDoubleArray(string text)
        {
            List<double> values = new List<double>();

            foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseEditorDouble(part.Trim(), out double value))
                    values.Add(value);
            }

            return values.ToArray();
        }

        private float[] ParseFloatArray(string text)
        {
            List<float> values = new List<float>();

            foreach (string part in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (TryParseEditorDouble(part.Trim(), out double value))
                    values.Add((float)value);
            }

            return values.ToArray();
        }

        private Button CreateTextCommandButton(string text, double width)
        {
            return new Button
            {
                Content = text,
                Width = width,
                Height = 30,
                FontSize = 13,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.Parse("#333333")),
                BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                BorderThickness = new Thickness(1),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
        }

        private const double AnimationTimelineRowHeight = 22.0;
        private const double AnimationTimelineRulerHeight = 24.0;
        private const double AnimationKeyDiamondRadius = 5.0;

        private sealed class AnimationTimelineControl : Control
        {
            private static readonly IBrush TimelineBackgroundBrush = new SolidColorBrush(Color.Parse("#1A1A1A"));
            private static readonly IBrush TimelineRowBrush = new SolidColorBrush(Color.Parse("#1F1F1F"));
            private static readonly IBrush TimelineRulerBrush = new SolidColorBrush(Color.Parse("#252525"));
            private static readonly IBrush TimelineTextBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
            private static readonly IBrush PlayheadBrush = Brushes.White;
            private static readonly IBrush SteppedKeyBrush = new SolidColorBrush(Color.Parse("#FFB020"));
            private static readonly IBrush SmoothKeyBrush = new SolidColorBrush(Color.Parse("#569CD6"));

            private static readonly Pen TimelineGridPen = new Pen(new SolidColorBrush(Color.Parse("#2E2E2E")), 1);
            private static readonly Pen PlayheadPen = new Pen(PlayheadBrush, 1);
            private static readonly Pen SegmentPen = new Pen(new SolidColorBrush(Color.Parse("#666666")), 1);
            private static readonly Pen SelectedKeyPen = new Pen(PlayheadBrush, 1.5);

            private static readonly IBrush[] CurveBrushes =
            {
                new SolidColorBrush(Color.Parse("#569CD6")),
                new SolidColorBrush(Color.Parse("#4EC9B0")),
                new SolidColorBrush(Color.Parse("#CE9178")),
                new SolidColorBrush(Color.Parse("#C586C0"))
            };

            public AnimationClipData? Clip { get; set; }

            public List<int> SelectedChannels { get; } = new List<int>();

            public List<(int Channel, int Key)> SelectedKeys { get; } = new List<(int Channel, int Key)>();

            public bool CurvesMode { get; set; }

            public double PlayheadTime { get; set; }

            public double PixelsPerSecond { get; set; } = 160.0;

            public double ScrollSeconds { get; set; }

            public event Action? PlayheadMoved;

            public event Action? KeysEdited;

            public event Action? SelectionChanged;

            private int _dragChannel = -1;
            private int _dragKey = -1;
            private double _dragGrabOffset;
            private bool _dragPlayhead;
            private bool _panning;
            private double _panStartX;
            private double _panStartScroll;

            public AnimationTimelineControl()
            {
                ClipToBounds = true;
                Focusable = true;
                Cursor = new Cursor(StandardCursorType.Arrow);
            }

            private double TimeToX(double time)
            {
                return (time - ScrollSeconds) * PixelsPerSecond;
            }

            private double XToTime(double x)
            {
                return x / PixelsPerSecond + ScrollSeconds;
            }

            private static double RowTop(int channelIndex)
            {
                return AnimationTimelineRulerHeight + channelIndex * AnimationTimelineRowHeight;
            }

            private int RowAtY(double y)
            {
                if (Clip == null)
                    return -1;

                int index = (int)Math.Floor((y - AnimationTimelineRulerHeight) / AnimationTimelineRowHeight);

                if (index < 0 || index >= Clip.Channels.Count)
                    return -1;

                return index;
            }

            private bool IsKeySelected(int channelIndex, int keyIndex)
            {
                foreach ((int Channel, int Key) entry in SelectedKeys)
                {
                    if (entry.Channel == channelIndex && entry.Key == keyIndex)
                        return true;
                }

                return false;
            }

            private static FormattedText CreateTimelineText(string text)
            {
                return new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Normal),
                    11.0,
                    TimelineTextBrush);
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                Rect bounds = new Rect(Bounds.Size);

                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                context.DrawRectangle(TimelineBackgroundBrush, null, bounds);
                context.DrawRectangle(TimelineRulerBrush, null, new Rect(0, 0, bounds.Width, AnimationTimelineRulerHeight));

                DrawRuler(context, bounds);

                if (Clip != null)
                {
                    DrawTrackRows(context, bounds);

                    if (CurvesMode)
                        DrawCurves(context, bounds);
                }

                DrawPlayhead(context, bounds);
            }

            private void DrawRuler(DrawingContext context, Rect bounds)
            {
                double step = ResolveRulerStep();
                double endSeconds = XToTime(bounds.Width);
                double first = Math.Floor(ScrollSeconds / step) * step;

                for (double time = first; time <= endSeconds; time += step)
                {
                    if (time < 0.0)
                        continue;

                    double x = TimeToX(time);

                    context.DrawLine(TimelineGridPen, new Point(x, AnimationTimelineRulerHeight), new Point(x, bounds.Height));
                    context.DrawText(CreateTimelineText(time.ToString("0.##", CultureInfo.InvariantCulture)), new Point(x + 3.0, 5.0));
                }
            }

            private double ResolveRulerStep()
            {
                double[] candidates = { 0.02, 0.05, 0.1, 0.25, 0.5, 1.0, 2.0, 5.0, 10.0, 30.0 };

                foreach (double candidate in candidates)
                {
                    if (candidate * PixelsPerSecond >= 64.0)
                        return candidate;
                }

                return 60.0;
            }

            private void DrawTrackRows(DrawingContext context, Rect bounds)
            {
                int count = Clip!.Channels.Count;

                for (int channelIndex = 0; channelIndex < count; channelIndex++)
                {
                    double top = RowTop(channelIndex);

                    if (top > bounds.Height)
                        break;

                    if (channelIndex % 2 == 1)
                        context.DrawRectangle(TimelineRowBrush, null, new Rect(0, top, bounds.Width, AnimationTimelineRowHeight));

                    AnimationChannel channel = Clip.Channels[channelIndex];

                    if (!CurvesMode)
                        DrawDopesheetKeys(context, bounds, channelIndex, channel, top);
                }
            }

            private void DrawDopesheetKeys(DrawingContext context, Rect bounds, int channelIndex, AnimationChannel channel, double top)
            {
                double centerY = top + AnimationTimelineRowHeight / 2;

                for (int keyIndex = 0; keyIndex < channel.Keys.Count; keyIndex++)
                {
                    AnimationKeyframe keyframe = channel.Keys[keyIndex];
                    double x = TimeToX(keyframe.Time);

                    if (x < -12.0 || x > bounds.Width + 12.0)
                        continue;

                    if (keyIndex > 0)
                    {
                        double previousX = TimeToX(channel.Keys[keyIndex - 1].Time);

                        if (previousX >= -12.0)
                            context.DrawLine(SegmentPen, new Point(previousX, centerY), new Point(x, centerY));
                    }

                    DrawKeyDiamond(context, x, centerY, keyframe.Stepped ? SteppedKeyBrush : SmoothKeyBrush);

                    if (IsKeySelected(channelIndex, keyIndex))
                        context.DrawEllipse(null, SelectedKeyPen, new Point(x, centerY), 8.0, 8.0);
                }
            }

            private static void DrawKeyDiamond(DrawingContext context, double x, double y, IBrush brush)
            {
                StreamGeometry geometry = new StreamGeometry();

                using (StreamGeometryContext geometryContext = geometry.Open())
                {
                    geometryContext.BeginFigure(new Point(x, y - AnimationKeyDiamondRadius), true);
                    geometryContext.LineTo(new Point(x + AnimationKeyDiamondRadius, y));
                    geometryContext.LineTo(new Point(x, y + AnimationKeyDiamondRadius));
                    geometryContext.LineTo(new Point(x - AnimationKeyDiamondRadius, y));
                    geometryContext.EndFigure(true);
                }

                context.DrawGeometry(brush, null, geometry);
            }

            private void DrawCurves(DrawingContext context, Rect bounds)
            {
                if (Clip == null || SelectedChannels.Count == 0)
                    return;

                double top = RowTop(0);
                double height = bounds.Height - top;

                if (height <= 0.0)
                    return;

                foreach (int channelIndex in SelectedChannels)
                {
                    if (channelIndex < 0 || channelIndex >= Clip.Channels.Count)
                        continue;

                    AnimationChannel channel = Clip.Channels[channelIndex];
                    int valueCount = ResolveAnimationValueCount(channel.ValueType);

                    if (valueCount <= 0)
                        continue;

                    for (int component = 0; component < valueCount; component++)
                        DrawCurveComponent(context, channel, component, top, height, valueCount);
                }
            }

            private void DrawCurveComponent(
                DrawingContext context,
                AnimationChannel channel,
                int component,
                double top,
                double height,
                int valueCount)
            {
                if (channel.Keys.Count == 0)
                    return;

                double minimum = double.MaxValue;
                double maximum = double.MinValue;

                foreach (AnimationKeyframe keyframe in channel.Keys)
                {
                    double value = ComponentValue(keyframe, component);

                    if (value < minimum)
                        minimum = value;

                    if (value > maximum)
                        maximum = value;
                }

                if (maximum - minimum < 1e-6)
                {
                    minimum -= 1.0;
                    maximum += 1.0;
                }

                double padding = (maximum - minimum) * 0.1;
                minimum -= padding;
                maximum += padding;

                IBrush brush = CurveBrushes[component % CurveBrushes.Length];
                Pen pen = new Pen(brush, 1.5);

                StreamGeometry geometry = new StreamGeometry();

                using (StreamGeometryContext geometryContext = geometry.Open())
                {
                    double lastX = double.NaN;

                    for (double time = 0.0; time <= Math.Max(Clip!.Duration, 0.001); time += 1.0 / Math.Max(Clip.FrameRate, 1f))
                    {
                        double normalized = (SampleComponent(channel, component, time) - minimum) / (maximum - minimum);
                        double x = TimeToX(time);
                        double y = top + height - normalized * height;

                        if (double.IsNaN(lastX))
                            geometryContext.BeginFigure(new Point(x, y), false);
                        else
                            geometryContext.LineTo(new Point(x, y));

                        lastX = x;
                    }

                    if (!double.IsNaN(lastX))
                        geometryContext.EndFigure(false);
                }

                context.DrawGeometry(null, pen, geometry);

                foreach (AnimationKeyframe keyframe in channel.Keys)
                {
                    double normalized = (ComponentValue(keyframe, component) - minimum) / (maximum - minimum);
                    double x = TimeToX(keyframe.Time);
                    double y = top + height - normalized * height;

                    context.DrawEllipse(brush, null, new Point(x, y), 3.0, 3.0);
                }
            }

            private static double ComponentValue(AnimationKeyframe keyframe, int component)
            {
                return component < keyframe.Value.Length ? keyframe.Value[component] : 0.0;
            }

            private static double SampleComponent(AnimationChannel channel, int component, double time)
            {
                List<AnimationKeyframe> keys = channel.Keys;

                if (keys.Count == 0)
                    return 0.0;

                if (time <= keys[0].Time)
                    return ComponentValue(keys[0], component);

                for (int i = 1; i < keys.Count; i++)
                {
                    if (keys[i].Time < time)
                        continue;

                    AnimationKeyframe from = keys[i - 1];
                    AnimationKeyframe to = keys[i];
                    double span = to.Time - from.Time;

                    if (span <= 0.0)
                        return ComponentValue(to, component);

                    double ratio = (time - from.Time) / span;

                    if (string.Equals(channel.Interpolation, "step", StringComparison.OrdinalIgnoreCase))
                        return ComponentValue(from, component);

                    if (string.Equals(channel.Interpolation, "cubic", StringComparison.OrdinalIgnoreCase))
                        return CubicComponentValue(from, to, component, span, ratio);

                    double fromValue = ComponentValue(from, component);
                    double toValue = ComponentValue(to, component);
                    return fromValue + (toValue - fromValue) * ratio;
                }

                return ComponentValue(keys[keys.Count - 1], component);
            }

            private static double CubicComponentValue(
                AnimationKeyframe from,
                AnimationKeyframe to,
                int component,
                double span,
                double ratio)
            {
                double t = ratio;
                double t2 = t * t;
                double t3 = t2 * t;

                double h00 = 2.0 * t3 - 3.0 * t2 + 1.0;
                double h10 = t3 - 2.0 * t2 + t;
                double h01 = -2.0 * t3 + 3.0 * t2;
                double h11 = t3 - t2;

                double fromValue = ComponentValue(from, component);
                double toValue = ComponentValue(to, component);
                double fromTangent = component < from.OutTangent.Length ? from.OutTangent[component] : 0.0;
                double toTangent = component < to.InTangent.Length ? to.InTangent[component] : 0.0;

                return h00 * fromValue + h10 * span * fromTangent + h01 * toValue + h11 * span * toTangent;
            }

            private void DrawPlayhead(DrawingContext context, Rect bounds)
            {
                double x = TimeToX(PlayheadTime);

                if (x < 0.0 || x > bounds.Width)
                    return;

                context.DrawLine(PlayheadPen, new Point(x, 0), new Point(x, bounds.Height));
            }

            private int FindKeyAt(int channelIndex, double x, double tolerance)
            {
                if (Clip == null || channelIndex < 0 || channelIndex >= Clip.Channels.Count)
                    return -1;

                AnimationChannel channel = Clip.Channels[channelIndex];
                int hit = -1;
                double bestDistance = tolerance;

                for (int keyIndex = 0; keyIndex < channel.Keys.Count; keyIndex++)
                {
                    double distance = Math.Abs(TimeToX(channel.Keys[keyIndex].Time) - x);

                    if (distance > bestDistance)
                        continue;

                    bestDistance = distance;
                    hit = keyIndex;
                }

                return hit;
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);

                if (Clip == null)
                    return;

                PointerPoint point = e.GetCurrentPoint(this);
                Point position = point.Position;

                Focus();

                if (point.Properties.IsMiddleButtonPressed ||
                    (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
                {
                    _panning = true;
                    _panStartX = position.X;
                    _panStartScroll = ScrollSeconds;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }

                if (!point.Properties.IsLeftButtonPressed)
                    return;

                if (position.Y < AnimationTimelineRulerHeight)
                {
                    _dragPlayhead = true;
                    PlayheadTime = Math.Max(0.0, XToTime(position.X));
                    PlayheadMoved?.Invoke();
                    e.Pointer.Capture(this);
                    InvalidateVisual();
                    e.Handled = true;
                    return;
                }

                int channelIndex = RowAtY(position.Y);

                if (channelIndex < 0)
                    return;

                int keyIndex = FindKeyAt(channelIndex, position.X, AnimationKeyDiamondRadius + 4.0);

                if (keyIndex < 0)
                {
                    if (!CurvesMode)
                        MovePlayheadFromPointer(position.X);

                    e.Handled = true;
                    return;
                }

                bool additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                if (!additive)
                    SelectedKeys.Clear();

                if (!IsKeySelected(channelIndex, keyIndex))
                    SelectedKeys.Add((channelIndex, keyIndex));

                _dragChannel = channelIndex;
                _dragKey = keyIndex;
                _dragGrabOffset = Clip.Channels[channelIndex].Keys[keyIndex].Time - XToTime(position.X);

                SelectionChanged?.Invoke();
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);

                Point position = e.GetPosition(this);

                if (_panning)
                {
                    ScrollSeconds = Math.Max(0.0, _panStartScroll - (position.X - _panStartX) / PixelsPerSecond);
                    InvalidateVisual();
                    return;
                }

                if (_dragPlayhead)
                {
                    PlayheadTime = Math.Max(0.0, XToTime(position.X));
                    PlayheadMoved?.Invoke();
                    InvalidateVisual();
                    return;
                }

                if (_dragChannel < 0 || _dragKey < 0 || Clip == null)
                    return;

                if (_dragChannel >= Clip.Channels.Count)
                    return;

                List<AnimationKeyframe> keys = Clip.Channels[_dragChannel].Keys;

                if (_dragKey >= keys.Count)
                    return;

                double time = Math.Max(0.0, XToTime(position.X) + _dragGrabOffset);

                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    double frameRate = Math.Max(Clip.FrameRate, 1f);
                    time = Math.Round(time * frameRate) / frameRate;
                }

                keys[_dragKey].Time = time;
                keys.Sort((a, b) => a.Time.CompareTo(b.Time));

                KeysEdited?.Invoke();
                InvalidateVisual();
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);

                if (_panning)
                {
                    _panning = false;
                    e.Pointer.Capture(null);
                    return;
                }

                if (_dragPlayhead)
                {
                    _dragPlayhead = false;
                    e.Pointer.Capture(null);
                    return;
                }

                if (_dragChannel >= 0)
                {
                    _dragChannel = -1;
                    _dragKey = -1;
                    e.Pointer.Capture(null);
                    SelectionChanged?.Invoke();
                }
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                base.OnPointerWheelChanged(e);

                Point position = e.GetPosition(this);
                double anchorTime = XToTime(position.X);
                double factor = e.Delta.Y > 0 ? 1.2 : 1.0 / 1.2;

                PixelsPerSecond = Math.Clamp(PixelsPerSecond * factor, 8.0, 4000.0);
                ScrollSeconds = Math.Max(0.0, anchorTime - position.X / PixelsPerSecond);

                InvalidateVisual();
                e.Handled = true;
            }

            private void MovePlayheadFromPointer(double x)
            {
                PlayheadTime = Math.Max(0.0, XToTime(x));
                PlayheadMoved?.Invoke();
                InvalidateVisual();
            }

            public void FrameAll()
            {
                if (Clip == null)
                    return;

                double duration = Clip.Duration > 0.0 ? Clip.Duration : 1.0;
                double width = Bounds.Width > 0.0 ? Bounds.Width : 600.0;

                ScrollSeconds = 0.0;
                PixelsPerSecond = Math.Clamp(width / duration, 8.0, 4000.0);
                InvalidateVisual();
            }
        }

        private const double AnimatorNodeWidth = 156.0;
        private const double AnimatorNodeHeight = 56.0;

        private sealed class AnimatorGraphControl : Control
        {
            private static readonly IBrush GraphBackgroundBrush = new SolidColorBrush(Color.Parse("#161616"));
            private static readonly IBrush GraphGridBrush = new SolidColorBrush(Color.Parse("#212121"));
            private static readonly IBrush NodeFillBrush = new SolidColorBrush(Color.Parse("#262626"));
            private static readonly IBrush NodeSelectedFillBrush = new SolidColorBrush(Color.Parse("#113355"));
            private static readonly IBrush NodeDefaultFillBrush = new SolidColorBrush(Color.Parse("#1E3A1E"));
            private static readonly IBrush NodeBorderBrush = new SolidColorBrush(Color.Parse("#5A5A5A"));
            private static readonly IBrush NodeSelectedBorderBrush = new SolidColorBrush(Color.Parse("#4488CC"));
            private static readonly IBrush NodeTitleBrush = Brushes.White;
            private static readonly IBrush NodeCaptionBrush = new SolidColorBrush(Color.Parse("#9A9A9A"));
            private static readonly IBrush EntryFillBrush = new SolidColorBrush(Color.Parse("#303030"));
            private static readonly IBrush TransitionBrush = new SolidColorBrush(Color.Parse("#8A8A8A"));
            private static readonly IBrush TransitionSelectedBrush = new SolidColorBrush(Color.Parse("#FFB020"));

            private static readonly Pen GraphGridPen = new Pen(GraphGridBrush, 1);
            private static readonly Pen NodeBorderPen = new Pen(NodeBorderBrush, 1);
            private static readonly Pen NodeSelectedBorderPen = new Pen(NodeSelectedBorderBrush, 2);
            private static readonly Pen TransitionPen = new Pen(TransitionBrush, 1.5);
            private static readonly Pen TransitionSelectedPen = new Pen(TransitionSelectedBrush, 2);
            private static readonly Pen EntryPen = new Pen(TransitionBrush, 1, new DashStyle(new double[] { 4.0, 3.0 }, 0.0));

            public AnimatorLayerData? Layer { get; set; }

            public Dictionary<string, Point> NodePositions { get; } = new Dictionary<string, Point>(StringComparer.Ordinal);

            public string? SelectedState { get; set; }

            public AnimatorTransitionData? SelectedTransition { get; set; }

            public event Action? LayoutChanged;

            public event Action? SelectionChanged;

            private Point _viewOffset;
            private double _zoom = 1.0;
            private string? _dragState;
            private Point _dragGrabOffset;
            private bool _panning;
            private Point _panStartPoint;
            private Point _panStartOffset;

            public AnimatorGraphControl()
            {
                ClipToBounds = true;
                Focusable = true;
            }

            private Point NodeToScreen(Point nodePoint)
            {
                return new Point(nodePoint.X * _zoom + _viewOffset.X, nodePoint.Y * _zoom + _viewOffset.Y);
            }

            private Point ScreenToNode(Point screenPoint)
            {
                return new Point((screenPoint.X - _viewOffset.X) / _zoom, (screenPoint.Y - _viewOffset.Y) / _zoom);
            }

            public Point EnsureNodePosition(string stateName)
            {
                if (NodePositions.TryGetValue(stateName, out Point existing))
                    return existing;

                Point created = new Point(240.0, 40.0 + NodePositions.Count * 96.0);
                NodePositions[stateName] = created;

                return created;
            }

            private static Point EntryPosition()
            {
                return new Point(40.0, 40.0);
            }

            private Rect GetNodeRect(string stateName)
            {
                Point position = EnsureNodePosition(stateName);
                return new Rect(position.X, position.Y, AnimatorNodeWidth, AnimatorNodeHeight);
            }

            private static FormattedText CreateGraphText(string text, double fontSize, IBrush brush, FontWeight weight)
            {
                return new FormattedText(
                    text,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(FontFamily.Default, FontStyle.Normal, weight),
                    fontSize,
                    brush);
            }

            public override void Render(DrawingContext context)
            {
                base.Render(context);

                Rect bounds = new Rect(Bounds.Size);

                if (bounds.Width <= 0 || bounds.Height <= 0)
                    return;

                context.DrawRectangle(GraphBackgroundBrush, null, bounds);
                DrawGrid(context, bounds);

                if (Layer == null)
                    return;

                DrawTransitions(context);
                DrawEntryLink(context);

                foreach (AnimatorStateData state in Layer.States)
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Name))
                        continue;

                    DrawStateNode(context, state);
                }
            }

            private void DrawGrid(DrawingContext context, Rect bounds)
            {
                double step = 40.0 * _zoom;

                if (step < 8.0)
                    return;

                double startX = _viewOffset.X % step;
                double startY = _viewOffset.Y % step;

                for (double x = startX; x < bounds.Width; x += step)
                    context.DrawLine(GraphGridPen, new Point(x, 0), new Point(x, bounds.Height));

                for (double y = startY; y < bounds.Height; y += step)
                    context.DrawLine(GraphGridPen, new Point(0, y), new Point(bounds.Width, y));
            }

            private void DrawEntryLink(DrawingContext context)
            {
                if (Layer == null || string.IsNullOrWhiteSpace(Layer.DefaultState))
                    return;

                if (!HasState(Layer.DefaultState))
                    return;

                Point entryPosition = EntryPosition();
                Rect entryRect = new Rect(entryPosition.X, entryPosition.Y, 104.0, AnimatorNodeHeight);
                Rect targetRect = GetNodeRect(Layer.DefaultState);

                Point start = NodeToScreen(new Point(entryRect.Right, entryRect.Center.Y));
                Point end = NodeToScreen(new Point(targetRect.Left, targetRect.Center.Y));

                context.DrawLine(EntryPen, start, end);
                context.DrawRectangle(EntryFillBrush, NodeBorderPen, TransformRect(entryRect));
                context.DrawText(
                    CreateGraphText("Entry", 12.0, NodeTitleBrush, FontWeight.Bold),
                    NodeToScreen(new Point(entryRect.X + 10.0, entryRect.Y + 8.0)));
                context.DrawText(
                    CreateGraphText(Layer.DefaultState, 11.0, NodeCaptionBrush, FontWeight.Normal),
                    NodeToScreen(new Point(entryRect.X + 10.0, entryRect.Y + 28.0)));
            }

            private Rect TransformRect(Rect nodeRect)
            {
                Point topLeft = NodeToScreen(new Point(nodeRect.X, nodeRect.Y));
                return new Rect(topLeft.X, topLeft.Y, nodeRect.Width * _zoom, nodeRect.Height * _zoom);
            }

            private void DrawStateNode(DrawingContext context, AnimatorStateData state)
            {
                Rect nodeRect = GetNodeRect(state.Name);
                Rect screenRect = TransformRect(nodeRect);

                IBrush fill = NodeFillBrush;

                if (Layer != null && string.Equals(Layer.DefaultState, state.Name, StringComparison.Ordinal))
                    fill = NodeDefaultFillBrush;

                if (string.Equals(SelectedState, state.Name, StringComparison.Ordinal))
                    fill = NodeSelectedFillBrush;

                Pen border = string.Equals(SelectedState, state.Name, StringComparison.Ordinal)
                    ? NodeSelectedBorderPen
                    : NodeBorderPen;

                context.DrawRectangle(fill, border, screenRect, 4.0, 4.0);

                context.DrawText(
                    CreateGraphText(state.Name, 12.0, NodeTitleBrush, FontWeight.Bold),
                    NodeToScreen(new Point(nodeRect.X + 10.0, nodeRect.Y + 8.0)));

                context.DrawText(
                    CreateGraphText(ResolveMotionCaption(state.Motion), 11.0, NodeCaptionBrush, FontWeight.Normal),
                    NodeToScreen(new Point(nodeRect.X + 10.0, nodeRect.Y + 30.0)));
            }

            private static string ResolveMotionCaption(AnimatorMotionData motion)
            {
                if (motion == null)
                    return string.Empty;

                if (string.Equals(motion.Type, "blend", StringComparison.OrdinalIgnoreCase))
                    return "混合树 " + motion.Entries.Count.ToString(CultureInfo.InvariantCulture);

                return string.IsNullOrWhiteSpace(motion.Clip) ? "剪辑" : motion.Clip;
            }

            private void DrawTransitions(DrawingContext context)
            {
                if (Layer == null)
                    return;

                foreach (AnimatorTransitionData transition in Layer.Transitions)
                    DrawTransition(context, transition);

                foreach (AnimatorTransitionData transition in Layer.AnyStateTransitions)
                    DrawTransition(context, transition);
            }

            private void DrawTransition(DrawingContext context, AnimatorTransitionData transition)
            {
                if (transition == null || string.IsNullOrWhiteSpace(transition.To))
                    return;

                if (!HasState(transition.To))
                    return;

                Point start;
                Point end;

                Rect targetRect = GetNodeRect(transition.To);

                if (string.IsNullOrWhiteSpace(transition.From))
                {
                    Point entryPosition = EntryPosition();
                    start = NodeToScreen(new Point(entryPosition.X + 52.0, entryPosition.Y + AnimatorNodeHeight));
                    end = NodeToScreen(new Point(targetRect.Center.X, targetRect.Y));
                }
                else
                {
                    if (!HasState(transition.From))
                        return;

                    Rect sourceRect = GetNodeRect(transition.From);
                    start = NodeToScreen(new Point(
                        sourceRect.X + AnimatorNodeWidth * 0.5 + 20.0,
                        sourceRect.Y + AnimatorNodeHeight * 0.5 + 12.0));
                    end = NodeToScreen(new Point(targetRect.Left, targetRect.Center.Y));
                }

                bool selected = ReferenceEquals(transition, SelectedTransition);
                Pen pen = selected ? TransitionSelectedPen : TransitionPen;

                context.DrawLine(pen, start, end);

                Point arrowPoint = new Point(
                    start.X + (end.X - start.X) * 0.55,
                    start.Y + (end.Y - start.Y) * 0.55);

                DrawArrow(context, pen, start, arrowPoint);
            }

            private static void DrawArrow(DrawingContext context, Pen pen, Point start, Point end)
            {
                double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
                double length = 8.0;

                Point left = new Point(
                    end.X - length * Math.Cos(angle - 0.4),
                    end.Y - length * Math.Sin(angle - 0.4));

                Point right = new Point(
                    end.X - length * Math.Cos(angle + 0.4),
                    end.Y - length * Math.Sin(angle + 0.4));

                StreamGeometry geometry = new StreamGeometry();

                using (StreamGeometryContext geometryContext = geometry.Open())
                {
                    geometryContext.BeginFigure(end, true);
                    geometryContext.LineTo(left);
                    geometryContext.LineTo(right);
                    geometryContext.EndFigure(true);
                }

                context.DrawGeometry(pen.Brush, null, geometry);
            }

            private bool HasState(string stateName)
            {
                if (Layer == null)
                    return false;

                foreach (AnimatorStateData state in Layer.States)
                {
                    if (state != null && string.Equals(state.Name, stateName, StringComparison.Ordinal))
                        return true;
                }

                return false;
            }

            private string? HitTestNode(Point screenPoint)
            {
                if (Layer == null)
                    return null;

                foreach (AnimatorStateData state in Layer.States)
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Name))
                        continue;

                    if (TransformRect(GetNodeRect(state.Name)).Contains(screenPoint))
                        return state.Name;
                }

                return null;
            }

            protected override void OnPointerPressed(PointerPressedEventArgs e)
            {
                base.OnPointerPressed(e);

                PointerPoint point = e.GetCurrentPoint(this);
                Point position = point.Position;

                Focus();

                if (point.Properties.IsMiddleButtonPressed ||
                    (point.Properties.IsLeftButtonPressed && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
                {
                    _panning = true;
                    _panStartPoint = position;
                    _panStartOffset = _viewOffset;
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }

                if (point.Properties.IsRightButtonPressed)
                {
                    string? rightHit = HitTestNode(position);

                    if (rightHit == null)
                        return;

                    SelectedState = rightHit;
                    SelectedTransition = null;
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }

                if (!point.Properties.IsLeftButtonPressed)
                    return;

                string? hit = HitTestNode(position);

                if (hit == null)
                {
                    SelectedState = null;
                    SelectedTransition = null;
                    SelectionChanged?.Invoke();
                    InvalidateVisual();
                    return;
                }

                SelectedState = hit;
                SelectedTransition = null;
                _dragState = hit;

                Point nodePoint = ScreenToNode(position);
                Point nodePosition = EnsureNodePosition(hit);
                _dragGrabOffset = new Point(nodePoint.X - nodePosition.X, nodePoint.Y - nodePosition.Y);

                SelectionChanged?.Invoke();
                e.Pointer.Capture(this);
                InvalidateVisual();
                e.Handled = true;
            }

            protected override void OnPointerMoved(PointerEventArgs e)
            {
                base.OnPointerMoved(e);

                Point position = e.GetPosition(this);

                if (_panning)
                {
                    _viewOffset = new Point(
                        _panStartOffset.X + (position.X - _panStartPoint.X),
                        _panStartOffset.Y + (position.Y - _panStartPoint.Y));

                    InvalidateVisual();
                    return;
                }

                if (_dragState == null)
                    return;

                Point nodePoint = ScreenToNode(position);

                NodePositions[_dragState] = new Point(
                    nodePoint.X - _dragGrabOffset.X,
                    nodePoint.Y - _dragGrabOffset.Y);

                LayoutChanged?.Invoke();
                InvalidateVisual();
            }

            protected override void OnPointerReleased(PointerReleasedEventArgs e)
            {
                base.OnPointerReleased(e);

                if (_panning)
                {
                    _panning = false;
                    e.Pointer.Capture(null);
                    return;
                }

                if (_dragState != null)
                {
                    _dragState = null;
                    e.Pointer.Capture(null);
                }
            }

            protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
            {
                base.OnPointerWheelChanged(e);

                Point position = e.GetPosition(this);
                Point anchor = ScreenToNode(position);
                double factor = e.Delta.Y > 0 ? 1.15 : 1.0 / 1.15;

                _zoom = Math.Clamp(_zoom * factor, 0.3, 3.0);

                _viewOffset = new Point(
                    position.X - anchor.X * _zoom,
                    position.Y - anchor.Y * _zoom);

                InvalidateVisual();
                e.Handled = true;
            }

            public void FrameAll()
            {
                if (Layer == null)
                    return;

                double minimumX = double.MaxValue;
                double minimumY = double.MaxValue;
                double maximumX = double.MinValue;
                double maximumY = double.MinValue;

                foreach (AnimatorStateData state in Layer.States)
                {
                    if (state == null || string.IsNullOrWhiteSpace(state.Name))
                        continue;

                    Point position = EnsureNodePosition(state.Name);

                    minimumX = Math.Min(minimumX, position.X);
                    minimumY = Math.Min(minimumY, position.Y);
                    maximumX = Math.Max(maximumX, position.X + AnimatorNodeWidth);
                    maximumY = Math.Max(maximumY, position.Y + AnimatorNodeHeight);
                }

                if (minimumX > maximumX)
                {
                    _viewOffset = new Point(20.0, 20.0);
                    _zoom = 1.0;
                    InvalidateVisual();
                    return;
                }

                double width = Bounds.Width > 0.0 ? Bounds.Width : 600.0;
                double height = Bounds.Height > 0.0 ? Bounds.Height : 400.0;

                double contentWidth = Math.Max(maximumX - minimumX, 1.0);
                double contentHeight = Math.Max(maximumY - minimumY, 1.0);

                _zoom = Math.Clamp(Math.Min(width / (contentWidth + 80.0), height / (contentHeight + 80.0)), 0.3, 3.0);
                _viewOffset = new Point(
                    40.0 - minimumX * _zoom,
                    40.0 - minimumY * _zoom);

                InvalidateVisual();
            }
        }

        private static readonly string[] SceneObjectTypeOptions =
        {
            "Object",
            "Camera",
            "Light",
            "AudioListener",
            "AudioSource",
            "Avatar"
        };

        private static readonly string[] CameraRenderModeOptions =
        {
            "0 屏幕天空盒",
            "1 相机天空盒"
        };

        private static readonly string[] CameraProjectionTypeOptions =
        {
            "0 透视",
            "1 正交"
        };

        private static readonly string[] LightModeOptions =
        {
            "0 点光",
            "3 方向光"
        };

        private static readonly string[] AudioListenerOutputModeOptions =
        {
            "Direct",
            "AudioSource"
        };

        private static readonly string[] AudioAttenuationModelOptions =
        {
            "InverseDistanceClamped",
            "InverseDistance",
            "LinearDistance",
            "ExponentialDistance",
            "None"
        };

        private static readonly string[] PhysicsMotionTypeOptions =
        {
            "Static",
            "Dynamic",
            "Kinematic"
        };

        private static readonly string[] PhysicsShapeTypeOptions =
        {
            "Box",
            "Sphere",
            "Capsule",
            "Mesh"
        };

        private static readonly string[] MaterialTextureWrapOptions =
        {
            "Repeat",
            "Clamp"
        };

        private static readonly string[] AnimationClipPlayModeOptions =
        {
            "normal",
            "stepped"
        };

        private static readonly string[] SceneFogModeOptions =
        {
            "color",
            "texture",
            "skybox"
        };

        private static readonly string[] MaterialScalarParameterKeys =
        {
            "uNormalStrength",
            "uAmbientStrength",
            "uSpecularIntensity",
            "uSmoothness"
        };

        private Control CreateResourceViewerContent(string path)
        {
            if (IsEditorConfigFile(path))
                return CreateFileInfoViewer(path);

            string extension = Path.GetExtension(path).Trim().ToLowerInvariant();

            if (string.Equals(extension, ".json", StringComparison.Ordinal))
            {
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                    JsonElement root = doc.RootElement;

                    if (root.ValueKind == JsonValueKind.Object)
                    {
                        bool hasSceneId =
                            root.TryGetProperty("sceneId", out JsonElement sceneIdElement) &&
                            sceneIdElement.ValueKind == JsonValueKind.String &&
                            !string.IsNullOrWhiteSpace(sceneIdElement.GetString());

                        bool hasObjects =
                            root.TryGetProperty("objects", out JsonElement objectsElement) &&
                            objectsElement.ValueKind == JsonValueKind.Array;

                        if (hasSceneId && hasObjects)
                            return CreateSceneAssetViewer(path);

                        if (root.TryGetProperty("assetType", out JsonElement assetTypeElement) &&
                            assetTypeElement.ValueKind == JsonValueKind.String)
                        {
                            string assetType = assetTypeElement.GetString() ?? string.Empty;

                            if (string.Equals(assetType, "Material", StringComparison.OrdinalIgnoreCase))
                                return CreateMaterialAssetViewer(path);

                            if (string.Equals(assetType, "AnimationClip", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(assetType, "AnimatorController", StringComparison.OrdinalIgnoreCase))
                                return CreateAnimationAssetViewer(path, assetType);
                        }
                    }
                }
                catch
                {
                }
            }

            switch (extension)
            {
                case ".png":
                case ".jpg":
                case ".jpeg":
                case ".bmp":
                case ".gif":
                case ".webp":
                case ".tga":
                    return CreateImageAssetViewer(path);

                case ".lua":
                case ".vert":
                case ".frag":
                case ".vrma":
                    return CreateTextAssetViewer(path);
            }

            return CreateFileInfoViewer(path);
        }

        private Control CreateFileInfoViewer(string path)
        {
            FileInfo fileInfo = new FileInfo(path);

            return CreateViewerContent(
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

        private Control CreateTextAssetViewer(string path)
        {
            FileInfo fileInfo = new FileInfo(path);
            string text = string.Empty;

            try
            {
                text = File.ReadAllText(path);
            }
            catch
            {
            }

            int lineCount = string.IsNullOrEmpty(text)
                ? 0
                : text.Count(character => character == '\n') + 1;

            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));

            stack.Children.Add(CreateViewerInfoRow("路径", fileInfo.FullName));
            stack.Children.Add(CreateViewerInfoRow("大小", $"{fileInfo.Length} 字节"));
            stack.Children.Add(CreateViewerInfoRow("行数", lineCount.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(CreateViewerInfoRow("最后修改", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));

            Button openButton = CreateTextCommandButton("打开", 80);
            openButton.Margin = new Thickness(12, 8, 12, 12);
            openButton.HorizontalAlignment = HorizontalAlignment.Left;
            openButton.Click += (_, _) => OnResourceItemDoubleTapped(path, false);

            stack.Children.Add(openButton);

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private Control CreateImageAssetViewer(string path)
        {
            FileInfo fileInfo = new FileInfo(path);

            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));

            try
            {
                using FileStream stream = File.OpenRead(path);
                Bitmap bitmap = new Bitmap(stream);

                stack.Children.Add(new Border
                {
                    Margin = new Thickness(12, 4, 12, 4),
                    Padding = new Thickness(4),
                    Background = new SolidColorBrush(Color.Parse("#1A1A1A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#555555")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Child = new Image
                    {
                        Source = bitmap,
                        Stretch = Stretch.Uniform,
                        Width = 272,
                        Height = 272,
                        HorizontalAlignment = HorizontalAlignment.Center
                    }
                });

                stack.Children.Add(CreateViewerInfoRow("像素", $"{bitmap.PixelSize.Width} x {bitmap.PixelSize.Height}"));
            }
            catch (Exception ex)
            {
                stack.Children.Add(CreateViewerInfoRow("预览", ex.Message));
            }

            stack.Children.Add(CreateViewerInfoRow("路径", fileInfo.FullName));
            stack.Children.Add(CreateViewerInfoRow("大小", $"{fileInfo.Length} 字节"));
            stack.Children.Add(CreateViewerInfoRow("最后修改", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")));

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private Control CreateViewerTitle(string title)
        {
            return new Border
            {
                Margin = new Thickness(12, 12, 12, 4),
                Child = new TextBlock
                {
                    Text = title,
                    Foreground = Brushes.White,
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private Control CreateViewerInfoRow(string label, string value)
        {
            return new Border
            {
                Margin = new Thickness(12, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = $"{label}: {value}",
                    Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private Control CreateMaterialAssetViewer(string path)
        {
            JsonObject root;

            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                return CreateViewerContent(
                    "材质参数",
                    new (string Label, string Value)[]
                    {
                        ("路径", path),
                        ("错误", ex.Message)
                    });
            }

            root["assetType"] = "Material";

            if (root["parameters"] is not JsonObject)
                root["parameters"] = new JsonObject();

            JsonObject parameters = (JsonObject)root["parameters"]!;

            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));

            List<string> shaderOptions = CollectAssetKeys("*.vert", true);

            stack.Children.Add(CreateComboPropertyEditor(
                "shader",
                shaderOptions,
                () => TryReadJsonValue(root, "shader", out string value) ? value : string.Empty,
                value =>
                {
                    root["shader"] = string.IsNullOrWhiteSpace(value) ? null : value;
                    return true;
                }));

            stack.Children.Add(CreateColorPropertyEditor(
                "uColor",
                () => new[]
                {
                    ReadJsonArrayComponent(parameters, "uColor", 0, 1.0),
                    ReadJsonArrayComponent(parameters, "uColor", 1, 1.0),
                    ReadJsonArrayComponent(parameters, "uColor", 2, 1.0),
                    ReadJsonArrayComponent(parameters, "uColor", 3, 1.0)
                },
                components =>
                {
                    for (int index = 0; index < 4; index++)
                        WriteJsonArrayComponent(parameters, "uColor", index, 4, components[index]);

                    return true;
                },
                1.0,
                true));

            stack.Children.Add(CreateTextPropertyEditor(
                "uTexture",
                () => TryReadJsonValue(parameters, "uTexture", out string value) ? value : string.Empty,
                value =>
                {
                    parameters["uTexture"] = value;
                    return true;
                }));

            stack.Children.Add(CreateComboPropertyEditor(
                "uTextureWrap",
                MaterialTextureWrapOptions,
                () => TryReadJsonValue(parameters, "uTextureWrap", out string value) ? value : MaterialTextureWrapOptions[0],
                value =>
                {
                    parameters["uTextureWrap"] = value;
                    return true;
                }));

            for (int index = 0; index < 2; index++)
            {
                int component = index;
                string suffix = component == 0 ? ".u" : ".v";

                stack.Children.Add(CreateNumberPropertyEditor(
                    "uTextureUV" + suffix,
                    () => ReadJsonArrayComponent(parameters, "uTextureUV", component, 1.0),
                    value =>
                    {
                        WriteJsonArrayComponent(parameters, "uTextureUV", component, 2, value);
                        return true;
                    }));
            }

            stack.Children.Add(CreateCheckPropertyEditor(
                "uUseNormalTexture",
                () => TryReadJsonValue(parameters, "uUseNormalTexture", out int value) && value != 0,
                value =>
                {
                    parameters["uUseNormalTexture"] = value ? 1 : 0;
                    return true;
                }));

            stack.Children.Add(CreateTextPropertyEditor(
                "uNormalTexture",
                () => TryReadJsonValue(parameters, "uNormalTexture", out string value) ? value : string.Empty,
                value =>
                {
                    parameters["uNormalTexture"] = value;
                    return true;
                }));

            stack.Children.Add(CreateColorPropertyEditor(
                "uSpecularColor",
                () => new[]
                {
                    ReadJsonArrayComponent(parameters, "uSpecularColor", 0, 1.0),
                    ReadJsonArrayComponent(parameters, "uSpecularColor", 1, 1.0),
                    ReadJsonArrayComponent(parameters, "uSpecularColor", 2, 1.0)
                },
                components =>
                {
                    for (int index = 0; index < 3; index++)
                        WriteJsonArrayComponent(parameters, "uSpecularColor", index, 3, components[index]);

                    return true;
                },
                1.0,
                false));

            foreach (string key in MaterialScalarParameterKeys)
            {
                double maximum = string.Equals(key, "uNormalStrength", StringComparison.Ordinal) ? 10.0 : 1.0;
                double fallback = string.Equals(key, "uNormalStrength", StringComparison.Ordinal) ? 1.0 : 0.0;

                stack.Children.Add(CreateSliderPropertyEditor(
                    key,
                    () => TryReadJsonValue(parameters, key, out double value) ? value : fallback,
                    value =>
                    {
                        parameters[key] = value;
                        return true;
                    },
                    0.0,
                    maximum));
            }

            Button applyButton = CreateTextCommandButton("应用", 80);
            applyButton.Margin = new Thickness(12, 8, 12, 12);
            applyButton.HorizontalAlignment = HorizontalAlignment.Left;

            applyButton.Click += (_, _) =>
            {
                try
                {
                    File.WriteAllText(path, root.ToJsonString(SceneJsonWriteOptions), Encoding.UTF8);
                    EditorHostBridge.SetAssetRootAndReloadAssets(ResolveSceneAssetRoot(path));
                    RightDockSlot.Content = CreateMaterialAssetViewer(path);
                }
                catch (Exception ex)
                {
                    _ = ShowSimpleWarningDialogAsync("保存失败", ex.Message);
                }
            };

            stack.Children.Add(applyButton);

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private Control CreateSceneAssetViewer(string path)
        {
            JsonObject root;

            try
            {
                root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
            }
            catch (Exception ex)
            {
                return CreateViewerContent(
                    "场景属性",
                    new (string Label, string Value)[]
                    {
                        ("路径", path),
                        ("错误", ex.Message)
                    });
            }

            string sceneId = TryReadJsonValue(root, "sceneId", out string parsedSceneId) ? parsedSceneId : string.Empty;
            int objectCount = root["objects"] is JsonArray objects ? objects.Count : 0;

            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));
            stack.Children.Add(CreateViewerInfoRow("sceneId", sceneId));
            stack.Children.Add(CreateViewerInfoRow("物体数", objectCount.ToString(CultureInfo.InvariantCulture)));

            if (root["properties"] is not JsonObject)
                root["properties"] = new JsonObject();

            JsonObject properties = (JsonObject)root["properties"]!;

            if (properties["fog"] is not JsonObject)
                properties["fog"] = new JsonObject();

            JsonObject fog = (JsonObject)properties["fog"]!;

            if (properties["ambientLight"] is not JsonObject)
                properties["ambientLight"] = new JsonObject();

            JsonObject ambientLight = (JsonObject)properties["ambientLight"]!;

            if (properties["skybox"] is not JsonObject)
                properties["skybox"] = new JsonObject();

            JsonObject skybox = (JsonObject)properties["skybox"]!;

            stack.Children.Add(CreateInspectorSectionHeader("雾"));

            stack.Children.Add(CreateCheckPropertyEditor(
                "enabled",
                () => TryReadJsonValue(fog, "enabled", out bool value) && value,
                value =>
                {
                    fog["enabled"] = value;
                    return true;
                }));

            stack.Children.Add(CreateComboPropertyEditor(
                "mode",
                SceneFogModeOptions,
                () => TryReadJsonValue(fog, "mode", out string value) ? value : SceneFogModeOptions[0],
                value =>
                {
                    fog["mode"] = value;
                    return true;
                }));

            if (fog["color"] is not JsonObject)
                fog["color"] = new JsonObject();

            JsonObject fogColor = (JsonObject)fog["color"]!;

            stack.Children.Add(CreateColorPropertyEditor(
                "color",
                () => new[]
                {
                    TryReadJsonValue(fogColor, "r", out int red) ? (double)Math.Clamp(red, 0, 255) : 0.0,
                    TryReadJsonValue(fogColor, "g", out int green) ? (double)Math.Clamp(green, 0, 255) : 0.0,
                    TryReadJsonValue(fogColor, "b", out int blue) ? (double)Math.Clamp(blue, 0, 255) : 0.0,
                    TryReadJsonValue(fogColor, "a", out int alpha) ? (double)Math.Clamp(alpha, 0, 255) : 255.0
                },
                components =>
                {
                    fogColor["r"] = (int)Math.Round(components[0]);
                    fogColor["g"] = (int)Math.Round(components[1]);
                    fogColor["b"] = (int)Math.Round(components[2]);
                    fogColor["a"] = (int)Math.Round(components[3]);
                    return true;
                },
                255.0,
                true));

            stack.Children.Add(CreateSliderPropertyEditor(
                "start",
                () => TryReadJsonValue(fog, "start", out double value) ? value : 100.0,
                value =>
                {
                    fog["start"] = value;
                    return true;
                },
                0.0,
                5000.0));

            stack.Children.Add(CreateSliderPropertyEditor(
                "end",
                () => TryReadJsonValue(fog, "end", out double value) ? value : 1000.0,
                value =>
                {
                    fog["end"] = value;
                    return true;
                },
                0.0,
                20000.0));

            stack.Children.Add(CreateCheckPropertyEditor(
                "edgeTransitionToSkybox",
                () => TryReadJsonValue(fog, "edgeTransitionToSkybox", out bool value) && value,
                value =>
                {
                    fog["edgeTransitionToSkybox"] = value;
                    return true;
                }));

            stack.Children.Add(CreateInspectorSectionHeader("环境光"));

            stack.Children.Add(CreateColorPropertyEditor(
                "rgba",
                () => new[]
                {
                    TryReadJsonValue(ambientLight, "r", out int red) ? (double)Math.Clamp(red, 0, 255) : 128.0,
                    TryReadJsonValue(ambientLight, "g", out int green) ? (double)Math.Clamp(green, 0, 255) : 128.0,
                    TryReadJsonValue(ambientLight, "b", out int blue) ? (double)Math.Clamp(blue, 0, 255) : 128.0,
                    TryReadJsonValue(ambientLight, "a", out int alpha) ? (double)Math.Clamp(alpha, 0, 255) : 255.0
                },
                components =>
                {
                    ambientLight["r"] = (int)Math.Round(components[0]);
                    ambientLight["g"] = (int)Math.Round(components[1]);
                    ambientLight["b"] = (int)Math.Round(components[2]);
                    ambientLight["a"] = (int)Math.Round(components[3]);
                    return true;
                },
                255.0,
                true));

            stack.Children.Add(CreateSliderPropertyEditor(
                "intensity",
                () => TryReadJsonValue(ambientLight, "intensity", out double value) ? value : 1.0,
                value =>
                {
                    ambientLight["intensity"] = value;
                    return true;
                },
                0.0,
                10.0));

            stack.Children.Add(CreateInspectorSectionHeader("天空盒"));

            stack.Children.Add(CreateCheckPropertyEditor(
                "enabled",
                () => TryReadJsonValue(skybox, "enabled", out bool value) && value,
                value =>
                {
                    skybox["enabled"] = value;
                    return true;
                }));

            List<string> shaderOptions = CollectAssetKeys("*.vert", true);

            stack.Children.Add(CreateComboPropertyEditor(
                "shaderName",
                shaderOptions,
                () => TryReadJsonValue(skybox, "shaderName", out string value) ? value : string.Empty,
                value =>
                {
                    skybox["shaderName"] = value;
                    return true;
                }));

            Grid buttons = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto,8,Auto"),
                Margin = new Thickness(12, 8, 12, 12),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            Button openButton = CreateTextCommandButton("打开", 80);
            Button applyButton = CreateTextCommandButton("应用", 80);

            Grid.SetColumn(openButton, 0);
            Grid.SetColumn(applyButton, 2);

            openButton.Click += (_, _) => OnResourceItemDoubleTapped(path, false);

            applyButton.Click += (_, _) =>
            {
                try
                {
                    File.WriteAllText(path, root.ToJsonString(SceneJsonWriteOptions), Encoding.UTF8);

                    string? currentSceneOriginalPath = _currentSceneOriginalPath;

                    if (!string.IsNullOrWhiteSpace(currentSceneOriginalPath) && PathsEqual(currentSceneOriginalPath, path))
                        TryOpenSceneJson(path);
                }
                catch (Exception ex)
                {
                    _ = ShowSimpleWarningDialogAsync("保存失败", ex.Message);
                }
            };

            buttons.Children.Add(openButton);
            buttons.Children.Add(applyButton);

            stack.Children.Add(buttons);

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private Control CreateAnimationAssetViewer(string path, string assetType)
        {
            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));

            Button openButton = CreateTextCommandButton("打开", 80);
            openButton.Margin = new Thickness(12, 8, 12, 12);
            openButton.HorizontalAlignment = HorizontalAlignment.Left;
            openButton.Click += (_, _) => OnResourceItemDoubleTapped(path, false);

            if (string.Equals(assetType, "AnimationClip", StringComparison.OrdinalIgnoreCase))
            {
                AnimationClipData? clip = EditorHostBridge.LoadAnimationClip(path);

                if (clip == null)
                {
                    stack.Children.Add(CreateViewerInfoRow("状态", "无法解析动画剪辑"));
                    stack.Children.Add(openButton);

                    return new ScrollViewer
                    {
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                        Content = stack
                    };
                }

                stack.Children.Add(CreateInspectorSectionHeader("动画剪辑"));

                stack.Children.Add(CreateSliderPropertyEditor(
                    "frameRate",
                    () => clip.FrameRate,
                    value =>
                    {
                        if (Math.Abs(clip.FrameRate - (float)value) < 1e-9f)
                            return true;

                        clip.FrameRate = (float)value;
                        return true;
                    },
                    1.0,
                    120.0));

                stack.Children.Add(CreateNumberPropertyEditor(
                    "duration",
                    () => clip.Duration,
                    value =>
                    {
                        if (value < 0.0)
                            return false;

                        clip.Duration = value;
                        return true;
                    }));

                stack.Children.Add(CreateCheckPropertyEditor(
                    "loop",
                    () => clip.Loop,
                    value =>
                    {
                        clip.Loop = value;
                        return true;
                    }));

                stack.Children.Add(CreateComboPropertyEditor(
                    "playMode",
                    AnimationClipPlayModeOptions,
                    () => string.IsNullOrWhiteSpace(clip.PlayMode) ? AnimationClipPlayModeOptions[0] : clip.PlayMode,
                    value =>
                    {
                        clip.PlayMode = value;
                        return true;
                    }));

                stack.Children.Add(CreateViewerInfoRow("通道数", clip.Channels.Count.ToString(CultureInfo.InvariantCulture)));

                Button applyButton = CreateTextCommandButton("应用", 80);
                applyButton.Margin = new Thickness(12, 8, 12, 4);
                applyButton.HorizontalAlignment = HorizontalAlignment.Left;

                applyButton.Click += (_, _) =>
                {
                    try
                    {
                        SaveAnimationClipFile(path, clip);
                        EditorHostBridge.RegisterAnimationClip(BuildAnimationAssetKey(path), clip);
                    }
                    catch (Exception ex)
                    {
                        _ = ShowSimpleWarningDialogAsync("保存失败", ex.Message);
                    }
                };

                stack.Children.Add(applyButton);
                stack.Children.Add(openButton);

                return new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = stack
                };
            }

            AnimatorControllerData? controller = EditorHostBridge.LoadAnimatorController(path);

            if (controller == null)
            {
                stack.Children.Add(CreateViewerInfoRow("状态", "无法解析动画器控制器"));
                stack.Children.Add(openButton);

                return new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = stack
                };
            }

            StackPanel parameterPanel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            foreach (AnimatorParameterData parameter in controller.Parameters)
            {
                StackPanel parameterRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Margin = new Thickness(12, 0, 12, 0)
                };

                parameterRow.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(parameter.Name) ? "(未命名)" : parameter.Name,
                    Foreground = new SolidColorBrush(Color.Parse("#DDDDDD")),
                    Width = 120,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                parameterRow.Children.Add(new TextBlock
                {
                    Text = parameter.Type,
                    Foreground = new SolidColorBrush(Color.Parse("#9A9A9A")),
                    Width = 64,
                    VerticalAlignment = VerticalAlignment.Center
                });

                parameterRow.Children.Add(new TextBlock
                {
                    Text = parameter.Default.ToString(CultureInfo.InvariantCulture),
                    Foreground = new SolidColorBrush(Color.Parse("#9A9A9A")),
                    VerticalAlignment = VerticalAlignment.Center
                });

                parameterPanel.Children.Add(parameterRow);
            }

            stack.Children.Add(CreateInspectorSectionHeader("动画器"));

            int stateCount = controller.Layers.Sum(layer => layer.States.Count);
            int transitionCount = controller.Layers.Sum(layer => layer.Transitions.Count + layer.AnyStateTransitions.Count);

            stack.Children.Add(CreateViewerInfoRow("名称", controller.Name ?? string.Empty));
            stack.Children.Add(CreateViewerInfoRow("层数", controller.Layers.Count.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(CreateViewerInfoRow("状态数", stateCount.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(CreateViewerInfoRow("过渡数", transitionCount.ToString(CultureInfo.InvariantCulture)));
            stack.Children.Add(CreateViewerInfoRow("参数数", controller.Parameters.Count.ToString(CultureInfo.InvariantCulture)));

            stack.Children.Add(CreateInspectorSectionHeader("参数"));
            stack.Children.Add(parameterPanel);

            stack.Children.Add(openButton);

            return new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = stack
            };
        }

        private Control CreateComboPropertyEditor(
            string label,
            IEnumerable<string> options,
            Func<string> getter,
            Func<string, bool> apply)
        {
            List<string> optionList = options.ToList();
            string currentValue = getter();

            ComboBox comboBox = new ComboBox
            {
                ItemsSource = optionList,
                SelectedItem = optionList.Contains(currentValue) ? currentValue : optionList.FirstOrDefault(),
                Height = 30,
                MinHeight = 30,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool isUpdating = false;

            comboBox.SelectionChanged += (_, _) =>
            {
                if (isUpdating)
                    return;

                string selected = comboBox.SelectedItem as string ?? string.Empty;

                if (apply(selected))
                    return;

                isUpdating = true;
                string restored = getter();
                comboBox.SelectedItem = optionList.Contains(restored) ? restored : optionList.FirstOrDefault();
                isUpdating = false;
            };

            return CreatePropertyRow(label, comboBox);
        }

        private Control CreateSliderPropertyEditor(
            string label,
            Func<double> getter,
            Func<double, bool> apply,
            double minimum,
            double maximum)
        {
            double current = Math.Clamp(getter(), minimum, maximum);

            Slider slider = new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value = current,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBox valueBox = CreateInspectorTextBox(FormatDouble(current));
            valueBox.Width = 84;

            bool isUpdating = false;

            slider.PropertyChanged += (_, e) =>
            {
                if (e.Property != Slider.ValueProperty)
                    return;

                if (isUpdating)
                    return;

                double value = Math.Clamp(slider.Value, minimum, maximum);

                if (!apply(value))
                    return;

                isUpdating = true;
                valueBox.Text = FormatDouble(value);
                isUpdating = false;
            };

            void CommitText()
            {
                if (isUpdating)
                    return;

                if (!TryParseEditorDouble(valueBox.Text, out double parsed))
                {
                    isUpdating = true;
                    valueBox.Text = FormatDouble(slider.Value);
                    isUpdating = false;
                    return;
                }

                double clamped = Math.Clamp(parsed, minimum, maximum);

                isUpdating = true;
                slider.Value = clamped;
                valueBox.Text = FormatDouble(clamped);
                isUpdating = false;

                apply(clamped);
            }

            valueBox.LostFocus += (_, _) => CommitText();
            valueBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    CommitText();
            };

            Grid grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,6,Auto"),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(slider, 0);
            Grid.SetColumn(valueBox, 2);

            grid.Children.Add(slider);
            grid.Children.Add(valueBox);

            return CreatePropertyRow(label, grid);
        }

        private Control CreateNumberPropertyEditor(
            string label,
            Func<double> getter,
            Func<double, bool> apply)
        {
            TextBox textBox = CreateInspectorTextBox(FormatDouble(getter()));

            void ResetText()
            {
                _isUpdatingSceneInspector = true;
                textBox.Text = FormatDouble(getter());
                _isUpdatingSceneInspector = false;
            }

            void Commit()
            {
                if (_isUpdatingSceneInspector)
                    return;

                if (!TryParseEditorDouble(textBox.Text, out double value))
                {
                    ResetText();
                    return;
                }

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

        private Control CreateCheckPropertyEditor(
            string label,
            Func<bool> getter,
            Func<bool, bool> apply)
        {
            CheckBox checkBox = new CheckBox
            {
                IsChecked = getter(),
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool isUpdating = false;

            checkBox.IsCheckedChanged += (_, _) =>
            {
                if (isUpdating)
                    return;

                bool newValue = checkBox.IsChecked == true;

                if (apply(newValue))
                    return;

                isUpdating = true;
                checkBox.IsChecked = getter();
                isUpdating = false;
            };

            return CreatePropertyRow(label, checkBox);
        }

        private Control CreateColorPropertyEditor(
            string label,
            Func<double[]> getter,
            Func<double[], bool> apply,
            double maximum,
            bool hasAlpha)
        {
            int componentCount = hasAlpha ? 4 : 3;
            bool normalized = maximum <= 1.0;

            TextBox hexBox = CreateInspectorTextBox(string.Empty);
            hexBox.Width = 116;
            hexBox.TextAlignment = TextAlignment.Center;

            Border previewSwatch = new Border
            {
                Width = 28,
                Height = InspectorTextBoxHeight,
                MinHeight = InspectorTextBoxHeight,
                CornerRadius = new CornerRadius(3),
                BorderBrush = new SolidColorBrush(Color.Parse("#4A4A4A")),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };

            bool isUpdatingHex = false;

            void ToBytes(double[] components, out byte redByte, out byte greenByte, out byte blueByte, out byte alphaByte)
            {
                byte ToByte(double value)
                {
                    double scaled = normalized ? value * 255.0 : value;
                    return (byte)Math.Clamp(Math.Round(scaled), 0.0, 255.0);
                }

                redByte = ToByte(components.Length > 0 ? components[0] : 0.0);
                greenByte = ToByte(components.Length > 1 ? components[1] : 0.0);
                blueByte = ToByte(components.Length > 2 ? components[2] : 0.0);
                alphaByte = ToByte(hasAlpha && components.Length > 3 ? components[3] : maximum);
            }

            void RefreshPreview()
            {
                double[] components = getter();

                ToBytes(components, out byte redByte, out byte greenByte, out byte blueByte, out byte alphaByte);

                previewSwatch.Background = new SolidColorBrush(
                    Color.FromArgb(alphaByte, redByte, greenByte, blueByte));

                isUpdatingHex = true;
                hexBox.Text = hasAlpha
                    ? $"#{alphaByte:X2}{redByte:X2}{greenByte:X2}{blueByte:X2}"
                    : $"#{redByte:X2}{greenByte:X2}{blueByte:X2}";
                isUpdatingHex = false;
            }

            void CommitHex()
            {
                if (isUpdatingHex)
                    return;

                string raw = (hexBox.Text ?? string.Empty).Trim().TrimStart('#');

                bool parsed = hasAlpha
                    ? raw.Length == 8
                    : raw.Length == 6;

                if (parsed)
                {
                    foreach (char character in raw)
                    {
                        if (!Uri.IsHexDigit(character))
                        {
                            parsed = false;
                            break;
                        }
                    }
                }

                if (!parsed)
                {
                    RefreshPreview();
                    return;
                }

                byte alphaByte = 255;
                int offset = 0;

                if (hasAlpha)
                {
                    alphaByte = byte.Parse(raw[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    offset = 2;
                }

                byte redByte = byte.Parse(raw.Substring(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte greenByte = byte.Parse(raw.Substring(offset + 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                byte blueByte = byte.Parse(raw.Substring(offset + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

                double ToComponent(byte value)
                {
                    return normalized ? value / 255.0 : value;
                }

                double[] components = hasAlpha
                    ? new[] { ToComponent(redByte), ToComponent(greenByte), ToComponent(blueByte), ToComponent(alphaByte) }
                    : new[] { ToComponent(redByte), ToComponent(greenByte), ToComponent(blueByte) };

                if (!apply(components))
                {
                    RefreshPreview();
                    return;
                }

                RefreshPreview();
            }

            hexBox.LostFocus += (_, _) => CommitHex();
            hexBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    CommitHex();
            };

            Grid header = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,6,Auto,6,Auto"),
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock labelText = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Color.Parse("#CFCFCF")),
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(labelText, 0);
            Grid.SetColumn(hexBox, 2);
            Grid.SetColumn(previewSwatch, 4);

            header.Children.Add(labelText);
            header.Children.Add(hexBox);
            header.Children.Add(previewSwatch);

            StackPanel panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Spacing = InspectorPropertySpacing
            };

            panel.Children.Add(new Border
            {
                Margin = new Thickness(12, 0, 12, 0),
                Child = header
            });

            string[] channelLabels = hasAlpha
                ? new[] { "r", "g", "b", "a" }
                : new[] { "r", "g", "b" };

            for (int index = 0; index < componentCount; index++)
            {
                int component = index;

                panel.Children.Add(CreateSliderPropertyEditor(
                    channelLabels[component],
                    () => getter()[component],
                    value =>
                    {
                        double[] components = getter();
                        components[component] = value;

                        if (!apply(components))
                            return false;

                        RefreshPreview();
                        return true;
                    },
                    0.0,
                    maximum));
            }

            RefreshPreview();
            return panel;
        }

        private Control CreateSceneObjectDataSection(SceneObject obj)
        {
            if (string.Equals(obj.Type, "Camera", StringComparison.Ordinal))
                return CreateCameraDataSection(obj);

            if (string.Equals(obj.Type, "Light", StringComparison.Ordinal))
                return CreateLightDataSection(obj);

            if (string.Equals(obj.Type, "AudioListener", StringComparison.Ordinal))
                return CreateAudioListenerDataSection(obj);

            if (string.Equals(obj.Type, "AudioSource", StringComparison.Ordinal))
                return CreateAudioSourceDataSection(obj);

            if (string.Equals(obj.Type, "Avatar", StringComparison.Ordinal))
                return CreateAvatarDataSection(obj);

            return CreateCollapsibleDataEditor("Data", () => obj.Data ?? "", value =>
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
            });
        }

        private Control CreateCameraDataSection(SceneObject obj)
        {
            JsonObject data = ParseSceneObjectData(obj.Data);

            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            bool Commit()
            {
                return ApplySceneObjectData(obj, data);
            }

            JsonObject? ReadSubObject(string key)
            {
                return data[key] as JsonObject;
            }

            JsonObject GetOrCreateSubObject(string key)
            {
                if (data[key] is JsonObject existing)
                    return existing;

                JsonObject created = new JsonObject();
                data[key] = created;
                return created;
            }

            JsonObject GetOrCreateChildObject(JsonObject parent, string key)
            {
                if (parent[key] is JsonObject existing)
                    return existing;

                JsonObject created = new JsonObject();
                parent[key] = created;
                return created;
            }

            panel.Children.Add(CreateInspectorSectionHeader("相机"));

            panel.Children.Add(CreateComboPropertyEditor(
                "renderMode",
                CameraRenderModeOptions,
                () => TryReadJsonValue(data, "renderMode", out int value) && value == 1
                    ? CameraRenderModeOptions[1]
                    : CameraRenderModeOptions[0],
                value =>
                {
                    data["renderMode"] = string.Equals(value, CameraRenderModeOptions[1], StringComparison.Ordinal) ? 1 : 0;
                    return Commit();
                }));

            panel.Children.Add(CreateComboPropertyEditor(
                "projectionType",
                CameraProjectionTypeOptions,
                () => TryReadJsonValue(data, "projectionType", out int value) && value == 1
                    ? CameraProjectionTypeOptions[1]
                    : CameraProjectionTypeOptions[0],
                value =>
                {
                    data["projectionType"] = string.Equals(value, CameraProjectionTypeOptions[1], StringComparison.Ordinal) ? 1 : 0;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "fovOrSize",
                () => TryReadJsonValue(data, "fovOrSize", out double value) ? value : 75.0,
                value =>
                {
                    data["fovOrSize"] = value;
                    return Commit();
                },
                1.0,
                179.0));

            panel.Children.Add(CreateNumberPropertyEditor(
                "renderPlaneOffset.x",
                () =>
                {
                    JsonObject? offset = ReadSubObject("renderPlaneOffset");
                    return offset != null && TryReadJsonValue(offset, "x", out double value) ? value : 0.0;
                },
                value =>
                {
                    GetOrCreateSubObject("renderPlaneOffset")["x"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateNumberPropertyEditor(
                "renderPlaneOffset.y",
                () =>
                {
                    JsonObject? offset = ReadSubObject("renderPlaneOffset");
                    return offset != null && TryReadJsonValue(offset, "y", out double value) ? value : 0.0;
                },
                value =>
                {
                    GetOrCreateSubObject("renderPlaneOffset")["y"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "nearClip",
                () => TryReadJsonValue(data, "nearClip", out double value) ? value : 0.01,
                value =>
                {
                    data["nearClip"] = value;
                    return Commit();
                },
                0.01,
                10.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "farClip",
                () => TryReadJsonValue(data, "farClip", out double value) ? value : 1000.0,
                value =>
                {
                    data["farClip"] = value;
                    return Commit();
                },
                10.0,
                5000.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "isMainCamera",
                () => TryReadJsonValue(data, "isMainCamera", out bool value) && value,
                value =>
                {
                    data["isMainCamera"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateInspectorSectionHeader("后期"));

            panel.Children.Add(CreateCheckPropertyEditor(
                "postProcess.enabled",
                () =>
                {
                    JsonObject? postProcess = ReadSubObject("postProcess");
                    return postProcess != null && TryReadJsonValue(postProcess, "enabled", out bool value) && value;
                },
                value =>
                {
                    GetOrCreateSubObject("postProcess")["enabled"] = value;
                    return Commit();
                }));

            foreach (string key in new[] { "brightness", "contrast", "saturation" })
            {
                string capturedKey = key;

                panel.Children.Add(CreateCheckPropertyEditor(
                    capturedKey + ".enabled",
                    () =>
                    {
                        JsonObject? settings = ReadSubObject("postProcess")?[capturedKey] as JsonObject;
                        return settings != null && TryReadJsonValue(settings, "enabled", out bool value) && value;
                    },
                    value =>
                    {
                        JsonObject parent = GetOrCreateSubObject("postProcess");
                        GetOrCreateChildObject(parent, capturedKey)["enabled"] = value;
                        return Commit();
                    }));

                panel.Children.Add(CreateSliderPropertyEditor(
                    capturedKey + ".value",
                    () =>
                    {
                        JsonObject? settings = ReadSubObject("postProcess")?[capturedKey] as JsonObject;
                        return settings != null && TryReadJsonValue(settings, "value", out double value) ? value : 1.0;
                    },
                    value =>
                    {
                        JsonObject parent = GetOrCreateSubObject("postProcess");
                        GetOrCreateChildObject(parent, capturedKey)["value"] = value;
                        return Commit();
                    },
                    0.0,
                    3.0));
            }

            panel.Children.Add(CreateCheckPropertyEditor(
                "hue.enabled",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["hue"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "enabled", out bool value) && value;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "hue")["enabled"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "hue.degrees",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["hue"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "degrees", out double value) ? value : 0.0;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "hue")["degrees"] = value;
                    return Commit();
                },
                0.0,
                360.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "temperature.enabled",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["temperature"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "enabled", out bool value) && value;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "temperature")["enabled"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "temperature.value",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["temperature"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "value", out double value) ? value : 0.0;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "temperature")["value"] = value;
                    return Commit();
                },
                -100.0,
                100.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "bloom.enabled",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "enabled", out bool value) && value;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["enabled"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.threshold",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "threshold", out double value) ? value : 1.0;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["threshold"] = value;
                    return Commit();
                },
                0.0,
                10.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.softKnee",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "softKnee", out double value) ? value : 0.5;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["softKnee"] = value;
                    return Commit();
                },
                0.0,
                1.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.intensity",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "intensity", out double value) ? value : 0.5;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["intensity"] = value;
                    return Commit();
                },
                0.0,
                10.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.iterations",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "iterations", out int value) ? value : 16;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["iterations"] = (int)Math.Round(value);
                    return Commit();
                },
                1.0,
                32.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.downsample",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "downsample", out int value) ? value : 2;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["downsample"] = (int)Math.Round(value);
                    return Commit();
                },
                1.0,
                8.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "bloom.range",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["bloom"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "range", out double value) ? value : 100.0;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "bloom")["range"] = value;
                    return Commit();
                },
                0.0,
                500.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "smaa.enabled",
                () =>
                {
                    JsonObject? settings = ReadSubObject("postProcess")?["smaa"] as JsonObject;
                    return settings != null && TryReadJsonValue(settings, "enabled", out bool value) && value;
                },
                value =>
                {
                    JsonObject parent = GetOrCreateSubObject("postProcess");
                    GetOrCreateChildObject(parent, "smaa")["enabled"] = value;
                    return Commit();
                }));

            return panel;
        }

        private Control CreateLightDataSection(SceneObject obj)
        {
            JsonObject data = ParseSceneObjectData(obj.Data);

            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            bool Commit()
            {
                return ApplySceneObjectData(obj, data);
            }

            panel.Children.Add(CreateInspectorSectionHeader("灯光"));

            panel.Children.Add(CreateComboPropertyEditor(
                "lightMode",
                LightModeOptions,
                () => TryReadJsonValue(data, "lightMode", out int value) && value == 3
                    ? LightModeOptions[1]
                    : LightModeOptions[0],
                value =>
                {
                    data["lightMode"] = string.Equals(value, LightModeOptions[1], StringComparison.Ordinal) ? 3 : 0;
                    return Commit();
                }));

            panel.Children.Add(CreateColorPropertyEditor(
                "color",
                () => new[]
                {
                    ReadJsonArrayComponent(data, "color", 0, 1.0),
                    ReadJsonArrayComponent(data, "color", 1, 1.0),
                    ReadJsonArrayComponent(data, "color", 2, 1.0)
                },
                components =>
                {
                    for (int index = 0; index < 3; index++)
                        WriteJsonArrayComponent(data, "color", index, 3, components[index]);

                    return Commit();
                },
                1.0,
                false));

            panel.Children.Add(CreateSliderPropertyEditor(
                "intensity",
                () => TryReadJsonValue(data, "intensity", out double value) ? value : 1.0,
                value =>
                {
                    data["intensity"] = value;
                    return Commit();
                },
                0.0,
                10.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "range",
                () => TryReadJsonValue(data, "range", out double value) ? value : 1.0,
                value =>
                {
                    data["range"] = value;
                    return Commit();
                },
                0.1,
                100.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "attenuationCurve",
                () => TryReadJsonValue(data, "attenuationCurve", out double value) ? value : 0.5,
                value =>
                {
                    data["attenuationCurve"] = value;
                    return Commit();
                },
                0.0,
                1.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "castShadow",
                () => TryReadJsonValue(data, "castShadow", out bool value) && value,
                value =>
                {
                    data["castShadow"] = value;
                    return Commit();
                }));

            return panel;
        }

        private Control CreateAudioListenerDataSection(SceneObject obj)
        {
            JsonObject data = ParseSceneObjectData(obj.Data);

            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            bool Commit()
            {
                return ApplySceneObjectData(obj, data);
            }

            List<string> sourceOptions = new List<string> { string.Empty };

            if (_currentTreeScene != null)
            {
                foreach (SceneObject candidate in _currentTreeScene.Objects)
                {
                    if (!string.Equals(candidate.Type, "AudioSource", StringComparison.Ordinal))
                        continue;

                    if (!string.IsNullOrWhiteSpace(candidate.Id) && !sourceOptions.Contains(candidate.Id))
                        sourceOptions.Add(candidate.Id);
                }
            }

            panel.Children.Add(CreateInspectorSectionHeader("音频监听"));

            panel.Children.Add(CreateComboPropertyEditor(
                "outputMode",
                AudioListenerOutputModeOptions,
                () => TryReadJsonValue(data, "outputMode", out string value) ? value : AudioListenerOutputModeOptions[0],
                value =>
                {
                    data["outputMode"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateComboPropertyEditor(
                "targetSourceId",
                sourceOptions,
                () => TryReadJsonValue(data, "targetSourceId", out string value) ? value : string.Empty,
                value =>
                {
                    data["targetSourceId"] = string.IsNullOrWhiteSpace(value) ? null : value;
                    return Commit();
                }));

            panel.Children.Add(CreateCheckPropertyEditor(
                "mute",
                () => TryReadJsonValue(data, "mute", out bool value) && value,
                value =>
                {
                    data["mute"] = value;
                    return Commit();
                }));

            return panel;
        }

        private Control CreateAudioSourceDataSection(SceneObject obj)
        {
            JsonObject data = ParseSceneObjectData(obj.Data);

            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            bool Commit()
            {
                return ApplySceneObjectData(obj, data);
            }

            List<string> clipOptions = CollectAssetKeys("*.ogg", false);
            clipOptions.AddRange(CollectAssetKeys("*.wav", false));
            clipOptions.AddRange(CollectAssetKeys("*.mp3", false));
            clipOptions.Sort(StringComparer.OrdinalIgnoreCase);

            panel.Children.Add(CreateInspectorSectionHeader("音频源"));

            panel.Children.Add(CreateComboPropertyEditor(
                "clipId",
                clipOptions,
                () => TryReadJsonValue(data, "clipId", out string value) ? value : string.Empty,
                value =>
                {
                    data["clipId"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "volume",
                () => TryReadJsonValue(data, "volume", out double value) ? value : 1.0,
                value =>
                {
                    data["volume"] = value;
                    return Commit();
                },
                0.0,
                2.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "minDistance",
                () => TryReadJsonValue(data, "minDistance", out double value) ? value : 1.0,
                value =>
                {
                    data["minDistance"] = value;
                    return Commit();
                },
                0.01,
                100.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "maxDistance",
                () => TryReadJsonValue(data, "maxDistance", out double value) ? value : 100.0,
                value =>
                {
                    data["maxDistance"] = value;
                    return Commit();
                },
                0.01,
                1000.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "rolloffFactor",
                () => TryReadJsonValue(data, "rolloffFactor", out double value) ? value : 1.0,
                value =>
                {
                    data["rolloffFactor"] = value;
                    return Commit();
                },
                0.0,
                10.0));

            panel.Children.Add(CreateComboPropertyEditor(
                "attenuationModel",
                AudioAttenuationModelOptions,
                () => TryReadJsonValue(data, "attenuationModel", out string value) ? value : AudioAttenuationModelOptions[0],
                value =>
                {
                    data["attenuationModel"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "spatialBlend",
                () => TryReadJsonValue(data, "spatialBlend", out double value) ? value : 1.0,
                value =>
                {
                    data["spatialBlend"] = value;
                    return Commit();
                },
                0.0,
                1.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "loop",
                () => TryReadJsonValue(data, "loop", out bool value) && value,
                value =>
                {
                    data["loop"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateCheckPropertyEditor(
                "playOnAwake",
                () => TryReadJsonValue(data, "playOnAwake", out bool value) && value,
                value =>
                {
                    data["playOnAwake"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "dopplerFactor",
                () => TryReadJsonValue(data, "dopplerFactor", out double value) ? value : 1.0,
                value =>
                {
                    data["dopplerFactor"] = value;
                    return Commit();
                },
                0.0,
                10.0));

            panel.Children.Add(CreateNumberPropertyEditor(
                "referenceDbLevel",
                () => TryReadJsonValue(data, "referenceDbLevel", out double value) ? value : 0.0,
                value =>
                {
                    data["referenceDbLevel"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateNumberPropertyEditor(
                "cullDbThreshold",
                () => TryReadJsonValue(data, "cullDbThreshold", out double value) ? value : -80.0,
                value =>
                {
                    data["cullDbThreshold"] = value;
                    return Commit();
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "speedOfSound",
                () => TryReadJsonValue(data, "speedOfSound", out double value) ? value : 343.0,
                value =>
                {
                    data["speedOfSound"] = value;
                    return Commit();
                },
                1.0,
                2000.0));

            return panel;
        }

        private Control CreateAvatarDataSection(SceneObject obj)
        {
            JsonObject data = ParseSceneObjectData(obj.Data);

            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            void CommitToMemory()
            {
                obj.Data = data.ToJsonString(SceneJsonWriteOptions);
                _sceneHasUnsavedChanges = true;
                UpdateSceneDirtyVisualState();
            }

            panel.Children.Add(CreateInspectorSectionHeader("角色"));

            panel.Children.Add(CreateTextPropertyEditor(
                "avatarId",
                () => TryReadJsonValue(data, "avatarId", out string value) ? value : string.Empty,
                value =>
                {
                    string newValue = (value ?? string.Empty).Trim();
                    string oldValue = TryReadJsonValue(data, "avatarId", out string existing) ? existing : string.Empty;

                    if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                        return true;

                    data["avatarId"] = string.IsNullOrWhiteSpace(newValue) ? null : newValue;
                    CommitToMemory();

                    Dispatcher.UIThread.Post(() => ShowSceneObjectInspector(obj), DispatcherPriority.Background);
                    return true;
                }));

            string[] morphKeys = EditorHostBridge.GetAvatarMorphKeys(EditorPreviewSceneId, obj.Id);

            panel.Children.Add(CreateInspectorSectionHeader("型态键"));

            if (morphKeys.Length == 0)
            {
                panel.Children.Add(new Border
                {
                    Margin = new Thickness(12, 2, 12, 2),
                    Child = new TextBlock
                    {
                        Text = "该角色没有可用型态键",
                        Foreground = new SolidColorBrush(Color.Parse("#9A9A9A")),
                        TextWrapping = TextWrapping.Wrap
                    }
                });

                return panel;
            }

            foreach (string morphKey in morphKeys)
            {
                string capturedKey = morphKey;

                panel.Children.Add(CreateSliderPropertyEditor(
                    capturedKey,
                    () => ReadJsonObjectComponent(data, "expressions", capturedKey,
                        EditorHostBridge.GetAvatarMorphWeight(EditorPreviewSceneId, obj.Id, capturedKey)),
                    value =>
                    {
                        WriteJsonObjectComponent(data, "expressions", capturedKey, value);
                        EditorHostBridge.SetAvatarMorphWeight(EditorPreviewSceneId, obj.Id, capturedKey, (float)value);
                        CommitToMemory();
                        return true;
                    },
                    0.0,
                    1.0));
            }

            return panel;
        }

        private Control CreateSceneObjectPhysicsSection(SceneObject obj)
        {
            StackPanel panel = new StackPanel
            {
                Spacing = InspectorPropertySpacing
            };

            panel.Children.Add(CreateInspectorSectionHeader("物理"));

            PhysicsBody? body = obj.Physics;

            if (body == null)
            {
                Button addButton = CreateTextCommandButton("添加物理", 96);
                addButton.Margin = new Thickness(12, 2, 12, 2);
                addButton.HorizontalAlignment = HorizontalAlignment.Left;

                addButton.Click += (_, _) =>
                {
                    BeginSceneParameterChange();
                    obj.Physics = new PhysicsBody();
                    PersistSceneObjectChanges(obj, false);
                    ShowSceneObjectInspector(obj);
                };

                panel.Children.Add(addButton);
                return panel;
            }

            panel.Children.Add(CreateCheckPropertyEditor(
                "enabled",
                () => body.Enabled,
                value =>
                {
                    if (body.Enabled == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Enabled = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateComboPropertyEditor(
                "motionType",
                PhysicsMotionTypeOptions,
                () => string.IsNullOrWhiteSpace(body.MotionType) ? PhysicsMotionTypeOptions[0] : body.MotionType,
                value =>
                {
                    if (string.Equals(body.MotionType, value, StringComparison.Ordinal))
                        return true;

                    BeginSceneParameterChange();
                    body.MotionType = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateComboPropertyEditor(
                "shapeType",
                PhysicsShapeTypeOptions,
                () => string.IsNullOrWhiteSpace(body.ShapeType) ? PhysicsShapeTypeOptions[0] : body.ShapeType,
                value =>
                {
                    if (string.Equals(body.ShapeType, value, StringComparison.Ordinal))
                        return true;

                    BeginSceneParameterChange();
                    body.ShapeType = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateVector3PropertyEditor(
                "size",
                () => body.Size,
                value =>
                {
                    if (Double3Equals(body.Size, value))
                        return true;

                    BeginSceneParameterChange();
                    body.Size = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "radius",
                () => body.Radius,
                value =>
                {
                    if (body.Radius == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Radius = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.01,
                50.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "length",
                () => body.Length,
                value =>
                {
                    if (body.Length == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Length = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.0,
                50.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "mass",
                () => body.Mass,
                value =>
                {
                    if (body.Mass == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Mass = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.01,
                1000.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "friction",
                () => body.Friction,
                value =>
                {
                    if (body.Friction == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Friction = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.0,
                1.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "restitution",
                () => body.Restitution,
                value =>
                {
                    if (body.Restitution == value)
                        return true;

                    BeginSceneParameterChange();
                    body.Restitution = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.0,
                1.0));

            panel.Children.Add(CreateCheckPropertyEditor(
                "useGravity",
                () => body.UseGravity,
                value =>
                {
                    if (body.UseGravity == value)
                        return true;

                    BeginSceneParameterChange();
                    body.UseGravity = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateCheckPropertyEditor(
                "enableSpeculativeContacts",
                () => body.EnableSpeculativeContacts,
                value =>
                {
                    if (body.EnableSpeculativeContacts == value)
                        return true;

                    BeginSceneParameterChange();
                    body.EnableSpeculativeContacts = value;
                    return PersistSceneObjectChanges(obj, false);
                }));

            panel.Children.Add(CreateSliderPropertyEditor(
                "linearDamping",
                () => body.LinearDamping,
                value =>
                {
                    if (body.LinearDamping == value)
                        return true;

                    BeginSceneParameterChange();
                    body.LinearDamping = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.0,
                1.0));

            panel.Children.Add(CreateSliderPropertyEditor(
                "angularDamping",
                () => body.AngularDamping,
                value =>
                {
                    if (body.AngularDamping == value)
                        return true;

                    BeginSceneParameterChange();
                    body.AngularDamping = value;
                    return PersistSceneObjectChanges(obj, false);
                },
                0.0,
                1.0));

            Button removeButton = CreateTextCommandButton("移除物理", 96);
            removeButton.Margin = new Thickness(12, 2, 12, 2);
            removeButton.HorizontalAlignment = HorizontalAlignment.Left;

            removeButton.Click += (_, _) =>
            {
                BeginSceneParameterChange();
                obj.Physics = null;
                PersistSceneObjectChanges(obj, false);
                ShowSceneObjectInspector(obj);
            };

            panel.Children.Add(removeButton);

            return panel;
        }

        private List<string> CollectAssetKeys(string searchPattern, bool withoutExtension)
        {
            List<string> keys = new List<string>();

            string rootPath = !string.IsNullOrWhiteSpace(_projectAssetRootPath) && Directory.Exists(_projectAssetRootPath)
                ? _projectAssetRootPath
                : string.Empty;

            if (string.IsNullOrWhiteSpace(rootPath))
                return keys;

            try
            {
                foreach (string filePath in Directory.GetFiles(rootPath, searchPattern, SearchOption.AllDirectories))
                {
                    string key = Path.GetRelativePath(rootPath, filePath).Replace('\\', '/');

                    if (key.StartsWith(".lse-editor-runtime/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (withoutExtension)
                    {
                        string extension = Path.GetExtension(key);

                        if (!string.IsNullOrEmpty(extension))
                            key = key[..^extension.Length];
                    }

                    if (!keys.Contains(key))
                        keys.Add(key);
                }
            }
            catch
            {
            }

            keys.Sort(StringComparer.OrdinalIgnoreCase);
            return keys;
        }

        private JsonObject ParseSceneObjectData(string? data)
        {
            if (string.IsNullOrWhiteSpace(data))
                return new JsonObject();

            try
            {
                if (JsonNode.Parse(data) is JsonObject parsed)
                    return parsed;
            }
            catch
            {
            }

            return new JsonObject();
        }

        private bool ApplySceneObjectData(SceneObject obj, JsonObject data)
        {
            string json = data.ToJsonString(SceneJsonWriteOptions);

            if (string.Equals(obj.Data ?? string.Empty, json, StringComparison.Ordinal))
                return true;

            BeginSceneParameterChange();
            obj.Data = json;
            return PersistSceneObjectChanges(obj, false);
        }

        private static bool TryReadJsonValue<T>(JsonObject data, string key, out T value)
        {
            value = default!;

            if (!data.TryGetPropertyValue(key, out JsonNode? node))
                return false;

            if (node is not JsonValue jsonValue)
                return false;

            return jsonValue.TryGetValue(out value);
        }

        private static double ReadJsonArrayComponent(JsonObject data, string key, int index, double fallback)
        {
            if (data[key] is not JsonArray array)
                return fallback;

            if (index < 0 || index >= array.Count)
                return fallback;

            if (array[index] is JsonValue value && value.TryGetValue(out double parsed))
                return parsed;

            return fallback;
        }

        private static void WriteJsonArrayComponent(JsonObject data, string key, int index, int count, double value)
        {
            JsonArray array = data[key] as JsonArray ?? new JsonArray();

            while (array.Count < count)
                array.Add(JsonValue.Create(0.0));

            if (index >= 0 && index < array.Count)
                array[index] = JsonValue.Create(value);

            data[key] = array;
        }

        private static double ReadJsonObjectComponent(JsonObject data, string objectKey, string componentKey, double fallback)
        {
            if (data[objectKey] is not JsonObject container)
                return fallback;

            if (!container.TryGetPropertyValue(componentKey, out JsonNode? node))
                return fallback;

            if (node is JsonValue value && value.TryGetValue(out double parsed))
                return parsed;

            return fallback;
        }

        private static void WriteJsonObjectComponent(JsonObject data, string objectKey, string componentKey, double value)
        {
            JsonObject container = data[objectKey] as JsonObject ?? new JsonObject();

            container[componentKey] = value;
            data[objectKey] = container;
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