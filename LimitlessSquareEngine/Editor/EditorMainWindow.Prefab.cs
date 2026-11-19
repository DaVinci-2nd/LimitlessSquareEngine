using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using LimitlessSquareEngine.Engine;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LimitlessSquareEngine.Editor
{
    public sealed partial class EditorMainWindow : Window
    {
        private bool _isCurrentDocumentPrefab;
        private string? _currentPrefabFilePath;
        private string _currentPrefabId = "";
        private string _currentPrefabName = "";

        private bool TryOpenPrefabJson(string filePath)
        {
            try
            {
                PrefabData? prefab = EditorHostBridge.LoadPrefabFile(filePath);
                if (prefab == null)
                    return false;

                EditorHostBridge.SetAssetRootAndReloadAssets(ResolveSceneAssetRoot(filePath));

                List<SceneObject> objects = EditorHostBridge.CollectPrefabObjects(prefab.Objects);

                SceneData scene = new SceneData
                {
                    SceneId = EditorPreviewSceneId,
                    Objects = objects
                };

                string assetRootPath = ResolveSceneAssetRoot(filePath);
                string editorWorkingRoot = GetEditorWorkingRoot(assetRootPath);
                string treeCopyDirectory = Path.Combine(editorWorkingRoot, EditorTreeCopyDirectoryName);
                string previewDirectory = Path.Combine(editorWorkingRoot, EditorPreviewDirectoryName);

                Directory.CreateDirectory(treeCopyDirectory);
                Directory.CreateDirectory(previewDirectory);

                string treeCopyPath = BuildTreeCopyPath(treeCopyDirectory, filePath);
                string previewScenePath = Path.Combine(previewDirectory, EditorPreviewSceneFileName);

                _currentSceneOriginalPath = null;
                _currentTreeCopyPath = treeCopyPath;
                _currentPreviewScenePath = previewScenePath;
                _currentTreeScene = scene;
                _isCurrentDocumentPrefab = true;
                _currentPrefabFilePath = Path.GetFullPath(filePath);
                _currentPrefabId = prefab.PrefabId;
                _currentPrefabName = prefab.Name;
                ClearSelectedSceneObjectState();
                _sceneUndoStack.Clear();
                _sceneRedoStack.Clear();
                _isApplyingSceneHistory = false;
                _sceneHasUnsavedChanges = false;

                InitializePreviewCameraEditorState(scene);
                _currentPreviewScene = BuildPreviewScene(scene);
                SaveSceneData(_currentTreeCopyPath, _currentTreeScene);
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

        private bool TrySaveCurrentPrefabToOriginalFile()
        {
            if (!_isCurrentDocumentPrefab ||
                _currentTreeScene == null ||
                string.IsNullOrWhiteSpace(_currentPrefabFilePath) ||
                string.IsNullOrWhiteSpace(_currentPrefabId) ||
                string.IsNullOrWhiteSpace(_currentTreeCopyPath))
                return false;

            List<SceneObject> objects = EditorHostBridge.CollectPrefabObjects(_currentTreeScene.Objects);

            PrefabData prefab = new PrefabData
            {
                PrefabId = _currentPrefabId,
                Name = _currentPrefabName,
                RootObjectId = objects.FirstOrDefault(o => string.IsNullOrWhiteSpace(o.Transform?.ParentId))?.Id ?? "",
                Objects = objects
            };

            string json = JsonSerializer.Serialize(prefab, SceneJsonWriteOptions);
            File.WriteAllText(_currentPrefabFilePath, json);

            SaveSceneData(_currentTreeCopyPath, _currentTreeScene);

            _sceneHasUnsavedChanges = false;
            UpdateSceneDirtyVisualState();

            return true;
        }

        private bool TryCreatePrefabFromSceneObject(SceneObject target)
        {
            if (_isCurrentDocumentPrefab || _currentTreeScene == null || target == null)
                return false;

            if (string.IsNullOrWhiteSpace(_currentSceneOriginalPath))
                return false;

            try
            {
                string name = string.IsNullOrWhiteSpace(target.Name) ? "NewPrefab" : target.Name.Trim();

                PrefabData? prefab = EditorHostBridge.ExtractPrefabData(_currentTreeScene, target.Id, Guid.NewGuid().ToString("N"), name);
                if (prefab == null)
                    return false;

                string assetRootPath = ResolveSceneAssetRoot(_currentSceneOriginalPath);
                string prefabDirectory = Path.Combine(assetRootPath, "Prefabs");
                Directory.CreateDirectory(prefabDirectory);

                string fileName = GetUniqueResourceName(prefabDirectory, SanitizeFileName(name), ".json");
                string prefabPath = Path.Combine(prefabDirectory, fileName);

                string json = JsonSerializer.Serialize(prefab, SceneJsonWriteOptions);
                File.WriteAllText(prefabPath, json, Encoding.UTF8);

                ShowResourceDirectory(prefabDirectory);

                return true;
            }
            catch (Exception ex)
            {
                _ = ShowSimpleWarningDialogAsync("警告", ex.Message);
                return false;
            }
        }

        private bool TryInstantiatePrefabFileToCurrentScene(string prefabFilePath)
        {
            if (_currentTreeScene == null || _isCurrentDocumentPrefab)
            {
                _ = ShowSimpleWarningDialogAsync("提示", "请先打开一个场景文档，再实例化预制体。");
                return false;
            }

            if (string.IsNullOrWhiteSpace(prefabFilePath) || !File.Exists(prefabFilePath))
                return false;

            try
            {
                string? validateReason = EditorHostBridge.ValidatePrefabFile(prefabFilePath);
                if (!string.IsNullOrEmpty(validateReason))
                {
                    _ = ShowSimpleWarningDialogAsync("预制体无效", validateReason);
                    return false;
                }

                PrefabData? prefab = EditorHostBridge.LoadPrefabFile(prefabFilePath);
                if (prefab == null || prefab.Objects.Count == 0)
                {
                    _ = ShowSimpleWarningDialogAsync("预制体无效", "预制体不包含任何物体。");
                    return false;
                }

                string? rootId = EditorHostBridge.InstantiatePrefab(prefab, _currentTreeScene, null, prefab.Name);
                if (rootId == null)
                    return false;

                SceneObject? newRoot = _currentTreeScene.Objects.FirstOrDefault(
                    o => string.Equals(o.Id, rootId, StringComparison.Ordinal));

                if (newRoot == null)
                    return false;

                BeginSceneParameterChange();

                _selectedSceneObject = newRoot;
                PersistSceneObjectChanges(newRoot, true);

                return true;
            }
            catch (Exception ex)
            {
                _ = ShowSimpleWarningDialogAsync("警告", ex.Message);
                return false;
            }
        }

        private Control CreatePrefabAssetViewer(string path)
        {
            string prefabId = "";
            string name = "";
            int objectCount = 0;

            try
            {
                PrefabData? prefab = EditorHostBridge.LoadPrefabFile(path);
                if (prefab != null)
                {
                    prefabId = prefab.PrefabId;
                    name = prefab.Name;
                    objectCount = prefab.Objects.Count;
                }
            }
            catch
            {
            }

            StackPanel stack = new StackPanel
            {
                Spacing = 4
            };

            stack.Children.Add(CreateViewerTitle(Path.GetFileName(path)));
            stack.Children.Add(CreateViewerInfoRow("name", name));
            stack.Children.Add(CreateViewerInfoRow("prefabId", prefabId));
            stack.Children.Add(CreateViewerInfoRow("物体数", objectCount.ToString(CultureInfo.InvariantCulture)));

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
    }
}
