#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Newtonsoft.Json;
using ShareX.HelpersLib;
using ShareX.ImageEditor.Integration;
using ShareX.ImageEditor.Presentation.ViewModels;
using ShareX.ImageEditor.Presentation.Views;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MessageBox = ShareX.AvaloniaUI.MessageBox;
using MessageBoxButtons = ShareX.AvaloniaUI.MessageBoxButtons;
using MessageBoxDefaultButton = ShareX.AvaloniaUI.MessageBoxDefaultButton;
using MessageBoxIcon = ShareX.AvaloniaUI.MessageBoxIcon;
using MessageBoxResult = ShareX.AvaloniaUI.DialogResult;

namespace ShareX;

internal enum ImageWorkspaceLeaveChoice { Save, Discard, Cancel }

internal sealed class ImageWorkspaceServices
{
    public Func<string, Task<SKBitmap?>>? DecodeImageAsync { get; init; }
    public Func<Task<ImageWorkspaceLeaveChoice>>? ConfirmLeaveAsync { get; init; }
    // The snapshot is borrowed and remains alive until this operation completes.
    public Func<SKBitmap, string?, bool, Task<string?>>? SaveImageAsync { get; init; }
    public Action<Exception>? ReportError { get; init; }
}

/// <summary>One editable image, with guarded replacement and ownership of its raster buffers.</summary>
public sealed class ImageWorkspaceView : UserControl, IDisposable
{
    private readonly ImageEditorOptions _options;
    private readonly ImageWorkspaceServices _services;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private EditorView? _editor;
    private MainViewModel? _viewModel;
    private Task<bool>? _pendingConfirmation;
    private Task<string?>? _pendingSave;
    private int _openVersion;
    private int _loadingCount;
    private bool _disposed;

    public string? ImagePath => _viewModel?.ImageFilePath;
    public bool HasImage => _viewModel?.HasPreviewImage == true;
    public bool IsDirty => _viewModel?.IsDirty == true;
    public bool IsBusy => _loadingCount > 0 || _pendingConfirmation != null || _pendingSave != null ||
        _viewModel?.IsModalOpen == true || _viewModel?.IsEffectsPanelOpen == true;
    public event Action<string>? ImageSaved;
    public event EventHandler? StateChanged;
    internal EditorView? Editor => _editor;
    internal MainViewModel? ViewModel => _viewModel;

    public ImageWorkspaceView(ImageEditorOptions? options = null)
        : this(options ?? new ImageEditorOptions(), new ImageWorkspaceServices())
    {
    }

    internal ImageWorkspaceView(ImageEditorOptions options, ImageWorkspaceServices services)
    {
        _options = JsonConvert.DeserializeObject<ImageEditorOptions>(JsonConvert.SerializeObject(options))!;
        _options.AutoCopyImageToClipboard = false;
        _options.AutoCloseEditorOnTask = false;
        _options.ZoomToFitOnOpen = true;
        _services = services;
        ImageEditorIntegration.Initialize();
        ClipToBounds = true;
    }

    public async Task<bool> OpenImageAsync(string path)
    {
        if (_disposed) return false;
        int version = ++_openVersion;
        SKBitmap? bitmap = null;
        _loadingCount++;
        NotifyStateChanged();
        try
        {
            path = Path.GetFullPath(path);
            if (HasImage && string.Equals(path, ImagePath, StringComparison.OrdinalIgnoreCase)) return true;

            bitmap = await (_services.DecodeImageAsync?.Invoke(path) ?? Task.Run<SKBitmap?>(() =>
            {
                using var stream = File.OpenRead(path);
                return SKBitmap.Decode(stream);
            }));
            if (_disposed || version != _openVersion) return false;
            if (bitmap == null) throw new InvalidDataException(string.Format(Localization.Strings.MainWindow_WorkspaceLoadFailed, path));

            await _transitionGate.WaitAsync();
            try
            {
                if (_disposed || version != _openVersion || !await ConfirmLeaveAsync()) return false;
                if (_disposed || version != _openVersion) return false;

                MainViewModel vm = new(_options)
                {
                    ShowStartScreen = false,
                    ShowFileMenu = false,
                    ShowTaskButtons = false,
                    ShowBottomToolbar = true,
                    HasHostSaveHandler = true,
                    HasHostSaveAsHandler = true
                };
                EditorView editor = new()
                {
                    DataContext = vm,
                    UseBuiltInToolbars = true,
                    RestrictShortcutsToFocus = true,
                    AllowDocumentReplacement = false
                };
                try
                {
                    vm.SetHostToolbarFilter(item => item.IsVisible && !item.IsFileMenu);
                    // Embedded toolbars occupy fixed bands; fit the image within the unobstructed stage.
                    if (editor.FindControl<Grid>("EditorCanvasHost") is Grid canvasHost)
                    {
                        canvasHost.Margin = new Thickness(8, 104, 8, 56);
                    }
                    SKBitmap ownedBitmap = bitmap;
                    bitmap = null; // UpdatePreview takes ownership, including on failure.
                    vm.UpdatePreview(ownedBitmap);
                    vm.ImageFilePath = path;
                    vm.IsDirty = false;
                    vm.SaveRequested += () => SaveAsync(false);
                    vm.SaveAsRequested += () => SaveAsync(true);
                    vm.PinRequested += () => RunImageAction(image => TaskHelpers.PinToScreen(image));
                    vm.PrintRequested += () => RunImageAction(TaskHelpers.PrintImage);
                    vm.UploadRequested += () => RunImageAction(image => UploadManager.UploadImage(image));
                    vm.PropertyChanged += OnEditorStateChanged;
                }
                catch
                {
                    editor.DisposeWorkspace();
                    vm.Dispose();
                    throw;
                }

                ReleaseEditor();
                _viewModel = vm;
                _editor = editor;
                Content = editor;
                NotifyStateChanged();
                return true;
            }
            finally
            {
                _transitionGate.Release();
            }
        }
        catch (Exception ex)
        {
            if (!_disposed && version == _openVersion) ReportError(ex);
            return false;
        }
        finally
        {
            bitmap?.Dispose();
            _loadingCount--;
            NotifyStateChanged();
        }
    }

    public async Task<bool> ConfirmLeaveAsync()
    {
        if (_disposed) return true;
        if (_pendingConfirmation != null) return await _pendingConfirmation;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingConfirmation = completion.Task;
        NotifyStateChanged();
        try
        {
            if (_pendingSave != null) await _pendingSave;
            // Effect dialogs can own an uncommitted preview. Finish or cancel it before leaving.
            if (_viewModel?.IsModalOpen == true || _viewModel?.IsEffectsPanelOpen == true) return Complete(false);
            if (!IsDirty) return Complete(true);

            ImageWorkspaceLeaveChoice choice = _services.ConfirmLeaveAsync != null
                ? await _services.ConfirmLeaveAsync()
                : ShowLeaveConfirmation();
            if (_disposed) return Complete(false);
            if (choice == ImageWorkspaceLeaveChoice.Discard) return Complete(true);
            if (choice != ImageWorkspaceLeaveChoice.Save) return Complete(false);
            string? savedPath = await SaveAsync(false);
            return Complete(!string.IsNullOrEmpty(savedPath) && !IsDirty);
        }
        catch (Exception ex)
        {
            if (!_disposed) ReportError(ex);
            return Complete(false);
        }
        finally
        {
            completion.TrySetResult(false);
            _pendingConfirmation = null;
            NotifyStateChanged();
        }

        bool Complete(bool result)
        {
            completion.TrySetResult(result);
            return result;
        }
    }

    private ImageWorkspaceLeaveChoice ShowLeaveConfirmation() => MessageBox.Show(
        TopLevel.GetTopLevel(this) as Window,
        Localization.Strings.MainWindow_WorkspaceSaveChangesMessage,
        Localization.Strings.MainWindow_WorkspaceSaveChangesTitle,
        MessageBoxButtons.YesNoCancel,
        MessageBoxIcon.Question,
        MessageBoxDefaultButton.Button3) switch
    {
        MessageBoxResult.Yes => ImageWorkspaceLeaveChoice.Save,
        MessageBoxResult.No => ImageWorkspaceLeaveChoice.Discard,
        _ => ImageWorkspaceLeaveChoice.Cancel
    };

    private async Task<string?> SaveAsync(bool saveAs)
    {
        if (_pendingSave != null) return await _pendingSave;
        if (_disposed || _editor == null || _viewModel == null) return null;

        MainViewModel vm = _viewModel;
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingSave = completion.Task;
        IsEnabled = false;
        NotifyStateChanged();
        try
        {
            using SKBitmap? snapshot = _editor.GetSnapshot();
            if (snapshot == null) return null;
            string? path = _services.SaveImageAsync != null
                ? await _services.SaveImageAsync(snapshot, vm.ImageFilePath, saveAs)
                : await SaveSnapshotAsync(snapshot, vm.ImageFilePath, saveAs);
            if (string.IsNullOrEmpty(path)) return null;
            if (!_disposed && ReferenceEquals(vm, _viewModel))
            {
                vm.ImageFilePath = path;
                vm.IsDirty = false;
                ImageSaved?.Invoke(path);
            }
            completion.TrySetResult(path);
            return path;
        }
        catch (Exception ex)
        {
            if (!_disposed) ReportError(ex);
            return null;
        }
        finally
        {
            completion.TrySetResult(null);
            _pendingSave = null;
            if (!_disposed) IsEnabled = true;
            NotifyStateChanged();
        }
    }

    private async Task<string?> SaveSnapshotAsync(SKBitmap snapshot, string? path, bool saveAs)
    {
        if (saveAs || string.IsNullOrEmpty(path))
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return null;
            IStorageFile? file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Localization.Strings.MainWindow_WorkspaceSaveAs,
                SuggestedFileName = string.IsNullOrEmpty(path) ? "image.png" : Path.GetFileName(path),
                DefaultExtension = "png",
                FileTypeChoices =
                [
                    new FilePickerFileType("PNG") { Patterns = ["*.png"] },
                    new FilePickerFileType("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
                    new FilePickerFileType("WebP") { Patterns = ["*.webp"] }
                ]
            });
            if (file == null) return null;
            path = file.Path.LocalPath;
        }

        string savePath = Path.GetFullPath(path);
        await Task.Run(() =>
        {
            string temporaryPath = Path.Combine(Path.GetDirectoryName(savePath)!, $".{Path.GetFileName(savePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                if (Path.GetExtension(savePath).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                {
                    using SKImage image = SKImage.FromBitmap(snapshot);
                    using SKData data = image.Encode(SKEncodedImageFormat.Webp, 100);
                    using FileStream stream = File.Create(temporaryPath);
                    data.SaveTo(stream);
                }
                else
                {
                    using var image = snapshot.ToBitmap();
                    image.Save(temporaryPath, ImageHelpers.GetImageFormat(savePath));
                }
                File.Move(temporaryPath, savePath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        });
        return savePath;
    }

    private void RunImageAction(Action<System.Drawing.Bitmap> action)
    {
        if (_editor == null) return;
        try
        {
            using SKBitmap? snapshot = _editor.GetSnapshot();
            if (snapshot != null) action(snapshot.ToBitmap());
        }
        catch (Exception ex)
        {
            ReportError(ex);
        }
    }

    public void ClearImage()
    {
        ++_openVersion;
        ReleaseEditor();
        NotifyStateChanged();
    }

    private void ReleaseEditor()
    {
        Content = null;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnEditorStateChanged;
        _editor?.DisposeWorkspace();
        if (_editor != null) _editor.DataContext = null;
        _viewModel?.Dispose();
        _editor = null;
        _viewModel = null;
    }

    private void OnEditorStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.ImageFilePath) or nameof(MainViewModel.IsDirty) or
            nameof(MainViewModel.HasPreviewImage) or nameof(MainViewModel.IsModalOpen) or nameof(MainViewModel.IsEffectsPanelOpen))
        {
            NotifyStateChanged();
        }
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void ReportError(Exception ex)
    {
        if (_services.ReportError != null) _services.ReportError(ex);
        else
        {
            DebugHelper.WriteException(ex);
            MessageBox.Show(TopLevel.GetTopLevel(this) as Window, ex.Message, Localization.Strings.Error,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ClearImage();
    }
}
