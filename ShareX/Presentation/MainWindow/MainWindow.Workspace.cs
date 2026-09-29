#nullable enable

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX;

public partial class MainWindow
{
    private ImageWorkspaceView _imageWorkspace = null!;
    private ThumbnailItemViewModel? _activeImageItem;
    private ThumbnailItemViewModel? _lastAutomaticImage;
    private bool _importingWorkspaceImages;
    private int _workspaceSelectionVersion;
    private int _workspaceSelectionInteraction;

    public bool HasWorkspaceImage => _imageWorkspace?.HasImage == true;
    public string WorkspaceTitle => _imageWorkspace?.ImagePath is string path
        ? Path.GetFileName(path) + (_imageWorkspace.IsDirty ? " *" : string.Empty)
        : Strings.MainWindow_WorkspaceImages;
    public int WorkspaceImageCount => ThumbnailItems.Count(IsWorkspaceImage);
    public bool CanCombineWorkspaceImages => ThumbnailItems.Count(x => x.IsSelected && IsWorkspaceImage(x)) > 1;
    public bool CanShowPreviousWorkspaceImage => GetAdjacentWorkspaceImage(-1) != null;
    public bool CanShowNextWorkspaceImage => GetAdjacentWorkspaceImage(1) != null;

    private void InitializeImageWorkspace()
    {
        _imageWorkspace = new ImageWorkspaceView(ApplicationState.DefaultTaskSettings.ToolsSettingsReference.ImageEditorOptions);
        WorkspaceHost.Content = _imageWorkspace;
        _imageWorkspace.StateChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasWorkspaceImage));
            OnPropertyChanged(nameof(WorkspaceTitle));
        };
        _imageWorkspace.ImageSaved += path =>
        {
            if (!ThumbnailItems.Any(x => string.Equals(x.Task.Info?.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                WorkerTask task = WorkerTask.CreateHistoryTask(new RecentTask { FilePath = path });
                TaskManager.Start(task);
                AddTask(task);
                TaskManager.RecentManager.Add(task);
            }
            foreach (ThumbnailItemViewModel item in ThumbnailItems.Where(x =>
                string.Equals(x.Task.Info?.FilePath, path, StringComparison.OrdinalIgnoreCase)))
            {
                item.Refresh();
            }
            ThumbnailItemViewModel savedItem = ThumbnailItems.First(x =>
                string.Equals(x.Task.Info?.FilePath, path, StringComparison.OrdinalIgnoreCase));
            SetActiveImageItem(savedItem);
            SelectOnly(savedItem);
            _lastAutomaticImage = ThumbnailItems.FirstOrDefault(IsWorkspaceImage);
            OnPropertyChanged(nameof(WorkspaceTitle));
        };
    }

    internal static bool IsWorkspaceImage(ThumbnailItemViewModel item) =>
        item.Task.Info?.FilePath is string path && FileHelpers.IsImageFile(path) && File.Exists(path);

    private void SetActiveImageItem(ThumbnailItemViewModel? item)
    {
        if (_activeImageItem != null) _activeImageItem.IsActive = false;
        _activeImageItem = item;
        if (item != null) item.IsActive = true;
        NotifyWorkspaceNavigation();
    }

    private void NotifyWorkspaceNavigation()
    {
        OnPropertyChanged(nameof(CanShowPreviousWorkspaceImage));
        OnPropertyChanged(nameof(CanShowNextWorkspaceImage));
        OnPropertyChanged(nameof(CanCombineWorkspaceImages));
    }

    private async Task ActivateWorkspaceImageAsync(ThumbnailItemViewModel item)
    {
        if (_disposed || !ThumbnailItems.Contains(item) || !IsWorkspaceImage(item)) return;
        int version = ++_workspaceSelectionVersion;
        int interaction = _workspaceSelectionInteraction;
        bool opened = await _imageWorkspace.OpenImageAsync(item.Task.Info.FilePath);
        if (_disposed || version != _workspaceSelectionVersion) return;
        if (opened && !ThumbnailItems.Contains(item))
        {
            _imageWorkspace.ClearImage();
            SetActiveImageItem(null);
            _lastAutomaticImage = null;
            TryActivateRecentImage();
            return;
        }
        if (opened && ThumbnailItems.Contains(item))
        {
            SetActiveImageItem(item);
            if (interaction == _workspaceSelectionInteraction)
            {
                SelectOnly(item);
                FilmstripItems.ContainerFromItem(item)?.BringIntoView();
                FilmstripPanel.Focus();
            }
        }
        else if (_activeImageItem != null && interaction == _workspaceSelectionInteraction)
        {
            SelectOnly(_activeImageItem);
        }
    }

    private void TryActivateRecentImage()
    {
        if (_disposed || !IsLoaded || _importingWorkspaceImages || _imageWorkspace.IsDirty || _imageWorkspace.IsBusy) return;
        ThumbnailItemViewModel? candidate = ThumbnailItems.FirstOrDefault(IsWorkspaceImage);
        if (candidate == null || ReferenceEquals(candidate, _lastAutomaticImage)) return;
        _lastAutomaticImage = candidate;
        _ = ActivateWorkspaceImageAsync(candidate);
    }

    private ThumbnailItemViewModel? GetAdjacentWorkspaceImage(int direction)
    {
        ThumbnailItemViewModel[] images = ThumbnailItems.Where(IsWorkspaceImage).ToArray();
        int index = Array.IndexOf(images, _activeImageItem);
        int next = index < 0 ? 0 : index + direction;
        return next >= 0 && next < images.Length ? images[next] : null;
    }

    private async Task NavigateWorkspaceAsync(int direction)
    {
        ThumbnailItemViewModel? item = GetAdjacentWorkspaceImage(direction);
        if (item != null) await ActivateWorkspaceImageAsync(item);
    }

    private async void OnPreviousWorkspaceImage(object? sender, RoutedEventArgs e) => await NavigateWorkspaceAsync(-1);
    private async void OnNextWorkspaceImage(object? sender, RoutedEventArgs e) => await NavigateWorkspaceAsync(1);

    private async void OnOpenWorkspaceImages(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Strings.MainWindow_WorkspaceOpenImages,
            AllowMultiple = true,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });
        await ImportWorkspaceImagesAsync(files.Select(x => x.TryGetLocalPath()).OfType<string>());
    }

    internal async Task ImportWorkspaceImagesAsync(IEnumerable<string> paths)
    {
        string[] files = paths.Where(x => File.Exists(x) && FileHelpers.IsImageFile(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _importingWorkspaceImages = true;
        try
        {
            // Add in reverse because history items are inserted at the front of the strip.
            foreach (string path in files.Reverse())
            {
                if (ThumbnailItems.Any(x => string.Equals(x.Task.Info?.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
                WorkerTask task = WorkerTask.CreateHistoryTask(new RecentTask { FilePath = path });
                TaskManager.Start(task); // History tasks never run capture/upload actions.
                AddTask(task);
                TaskManager.RecentManager.Add(task);
            }
        }
        finally
        {
            _importingWorkspaceImages = false;
        }

        ThumbnailItemViewModel? first = ThumbnailItems.FirstOrDefault(x =>
            string.Equals(x.Task.Info?.FilePath, files.FirstOrDefault(), StringComparison.OrdinalIgnoreCase));
        if (first != null)
        {
            _lastAutomaticImage = ThumbnailItems.FirstOrDefault(IsWorkspaceImage);
            await ActivateWorkspaceImageAsync(first);
        }
    }

    private void OnCombineWorkspaceImages(object? sender, RoutedEventArgs e)
    {
        _uploadInfoManager.UpdateSelectedItems(GetSelectedItems().Select(x => x.Task));
        _uploadInfoManager.CombineImages();
    }

    private void EditSelectedWorkspaceImage()
    {
        ThumbnailItemViewModel? item = GetSelectedItems().FirstOrDefault(IsWorkspaceImage);
        if (item != null) _ = ActivateWorkspaceImageAsync(item);
    }

    private async Task<bool> ConfirmRemovingWorkspaceItemsAsync(IReadOnlyCollection<ThumbnailItemViewModel> items)
    {
        if (_imageWorkspace.HasImage && items.Any(item => ReferenceEquals(item, _activeImageItem) ||
            string.Equals(item.Task.Info?.FilePath, _imageWorkspace.ImagePath, StringComparison.OrdinalIgnoreCase)))
        {
            if (!await _imageWorkspace.ConfirmLeaveAsync()) return false;
            ++_workspaceSelectionVersion;
            _imageWorkspace.ClearImage();
            SetActiveImageItem(null);
            _lastAutomaticImage = null;
        }
        return true;
    }

    internal Task<bool> ConfirmWorkspaceExitAsync()
    {
        if (_imageWorkspace.IsDirty || _imageWorkspace.IsBusy) ShowAndActivate();
        return _imageWorkspace.ConfirmLeaveAsync();
    }
}
