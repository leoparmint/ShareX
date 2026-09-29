using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ShareX;
using ShareX.AvaloniaUI.Integration;
using ShareX.ImageEditor.Integration;
using ShareX.ImageEditor.Presentation.ViewModels;
using ShareX.ImageEditor.Presentation.Views;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        AppBuilder.Configure<ShareXAvaloniaApplication>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        using Fixtures fixtures = new();
        (string Name, Action Run)[] checks =
        [
            ("switching images replaces real editor content", () => SwitchImages(fixtures)),
            ("missing and corrupt images preserve the current edit", () => FailedDecode(fixtures)),
            ("latest image request wins and disposes stale decoded content", () => LatestRequestWins(fixtures)),
            ("cancel preserves edits and discard changes image", () => CancelAndDiscard(fixtures)),
            ("save captures edits before changing image", () => SaveBeforeSwitch(fixtures)),
            ("cancelled or failed save preserves edits", () => FailedSave(fixtures)),
            ("a newer request supersedes a pending confirmation", () => SupersededConfirmation(fixtures)),
            ("modal dialogs and effect previews block document replacement", () => ModalGuards(fixtures)),
            ("clear and dispose invalidate pending loads", () => PendingLoadDisposal(fixtures)),
            ("Delete is scoped to editor focus", () => KeyboardScope(fixtures))
        ];

        int failures = 0;
        foreach ((string name, Action run) in checks)
        {
            try
            {
                run();
                Pump();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {exception}");
            }
        }

        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
        return failures == 0 ? 0 : 1;
    }

    private static void SwitchImages(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "first image opens");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(!probe.Workspace.IsDirty, "freshly opened image is clean");

        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "second image opens");
        AssertSource(probe.Workspace, fixtures.B, 72, 54, SKColors.Blue);
        Equal(0, probe.ConfirmCount, "clean switch needs no confirmation");
        Equal(0, probe.Errors.Count, "normal decoding has no errors");
    }

    private static void FailedDecode(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);

        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.Missing)), "missing image is rejected");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(probe.Workspace.IsDirty, "missing image leaves edits dirty");
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.Corrupt)), "corrupt image is rejected");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(probe.Workspace.IsDirty, "corrupt image leaves edits dirty");
        Equal(0, probe.ConfirmCount, "failed decoding does not ask to discard a valid document");
    }

    private static void LatestRequestWins(Fixtures fixtures)
    {
        TaskCompletionSource<SKBitmap?> first = NewCompletion<SKBitmap?>();
        TaskCompletionSource<SKBitmap?> second = NewCompletion<SKBitmap?>();
        using WorkspaceProbe probe = new(decode: path => path == fixtures.A ? first.Task : second.Task);
        Task<bool> firstRequest = probe.Workspace.OpenImageAsync(fixtures.A);
        Task<bool> secondRequest = probe.Workspace.OpenImageAsync(fixtures.B);

        second.SetResult(SKBitmap.Decode(fixtures.B));
        Check(Wait(secondRequest), "newest request commits first");
        SKBitmap stale = SKBitmap.Decode(fixtures.A);
        first.SetResult(stale);
        Check(!Wait(firstRequest), "older request cannot commit after a newer request");
        Equal(IntPtr.Zero, stale.Handle, "stale decoded bitmap is disposed");
        AssertSource(probe.Workspace, fixtures.B, 72, 54, SKColors.Blue);
        Check(!probe.Workspace.IsBusy, "completed overlapping loads clear busy state");
    }

    private static void CancelAndDiscard(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);
        probe.LeaveChoice = ImageWorkspaceLeaveChoice.Cancel;
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "cancel aborts switching");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(probe.Workspace.IsDirty, "cancel preserves dirty state");
        Check(probe.Workspace.ViewModel!.HasAnnotations, "cancel preserves annotations");

        probe.LeaveChoice = ImageWorkspaceLeaveChoice.Discard;
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "discard permits switching");
        AssertSource(probe.Workspace, fixtures.B, 72, 54, SKColors.Blue);
        Check(!probe.Workspace.IsDirty, "new document is clean after discard");
        Check(!probe.Workspace.ViewModel!.HasAnnotations, "discard clears old annotations");
        Equal(0, probe.SaveCount, "cancel and discard never write an image");
    }

    private static void SaveBeforeSwitch(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);
        probe.LeaveChoice = ImageWorkspaceLeaveChoice.Save;
        probe.Save = (snapshot, path, saveAs) =>
        {
            Equal(fixtures.A, path, "save targets the old document");
            Check(!saveAs, "switch confirmation uses ordinary save");
            Equal(96, snapshot.Width, "saved edit width");
            Equal(64, snapshot.Height, "saved edit height");
            Equal(SKColors.Lime, snapshot.GetPixel(12, 12), "saved snapshot contains the annotation");
            using SKImage image = SKImage.FromBitmap(snapshot);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(fixtures.Saved, png.ToArray());
            return Task.FromResult<string?>(fixtures.Saved);
        };

        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "successful save permits switching");
        Equal(1, probe.SaveCount, "old image saved exactly once");
        AssertSource(probe.Workspace, fixtures.B, 72, 54, SKColors.Blue);
        Check(!probe.Workspace.IsDirty, "new image starts clean after saving old edits");
        using SKBitmap saved = SKBitmap.Decode(fixtures.Saved);
        Equal(SKColors.Lime, saved.GetPixel(12, 12), "written PNG retains the edit");
    }

    private static void FailedSave(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);
        probe.LeaveChoice = ImageWorkspaceLeaveChoice.Save;
        probe.Save = (_, _, _) => Task.FromResult<string?>(null);
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "cancelled save prevents switching");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(probe.Workspace.IsDirty, "cancelled save leaves edits dirty");

        probe.Save = (_, _, _) => Task.FromException<string?>(new IOException("Injected test write failure"));
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "failed save prevents switching");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);
        Check(probe.Workspace.IsDirty, "failed save leaves edits dirty");
        Check(probe.Errors.Count > 0, "save failure is reported");
    }

    private static void SupersededConfirmation(Fixtures fixtures)
    {
        TaskCompletionSource<ImageWorkspaceLeaveChoice> confirmation = NewCompletion<ImageWorkspaceLeaveChoice>();
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);
        probe.Confirm = () => confirmation.Task;
        Task<bool> firstSwitch = probe.Workspace.OpenImageAsync(fixtures.B);
        PumpUntil(() => probe.ConfirmCount == 1, "first switch reaches its confirmation");
        Task<bool> latestSwitch = probe.Workspace.OpenImageAsync(fixtures.C);
        confirmation.SetResult(ImageWorkspaceLeaveChoice.Discard);
        Check(!Wait(firstSwitch), "superseded switch does not commit after confirmation");
        Check(Wait(latestSwitch), "newest switch survives serialized confirmation");
        AssertSource(probe.Workspace, fixtures.C, 48, 32, SKColors.Gold);
    }

    private static void PendingLoadDisposal(Fixtures fixtures)
    {
        foreach (bool dispose in new[] { false, true })
        {
            TaskCompletionSource<SKBitmap?> pending = NewCompletion<SKBitmap?>();
            using WorkspaceProbe probe = new(decode: _ => pending.Task);
            Task<bool> request = probe.Workspace.OpenImageAsync(fixtures.A);
            if (dispose) probe.Workspace.Dispose();
            else probe.Workspace.ClearImage();

            SKBitmap stale = SKBitmap.Decode(fixtures.A);
            pending.SetResult(stale);
            Check(!Wait(request), $"{(dispose ? "dispose" : "clear")} prevents pending load from committing");
            Equal(IntPtr.Zero, stale.Handle, "invalidated bitmap is disposed");
            Check(!probe.Workspace.HasImage, "invalidated load does not restore a document");
        }
    }

    private static void ModalGuards(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        MainViewModel viewModel = probe.Workspace.ViewModel!;
        viewModel.IsModalOpen = true;
        Check(probe.Workspace.IsBusy, "modal dialog marks the workspace busy");
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "modal dialog blocks image replacement");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);

        viewModel.IsModalOpen = false;
        viewModel.IsEffectsPanelOpen = true;
        Check(probe.Workspace.IsBusy, "effect preview marks the workspace busy");
        Check(!Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "effect preview blocks image replacement");
        AssertSource(probe.Workspace, fixtures.A, 96, 64, SKColors.Red);

        viewModel.IsEffectsPanelOpen = false;
        Check(!probe.Workspace.IsBusy, "closing dialogs clears busy state");
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.B)), "replacement succeeds after dialogs close");
        AssertSource(probe.Workspace, fixtures.B, 72, 54, SKColors.Blue);
        Equal(0, probe.ConfirmCount, "guarded clean documents do not prompt to discard edits");
    }

    private static void KeyboardScope(Fixtures fixtures)
    {
        using WorkspaceProbe probe = new();
        Check(Wait(probe.Workspace.OpenImageAsync(fixtures.A)), "initial image opens");
        Edit(probe.Workspace);
        MainViewModel viewModel = probe.Workspace.ViewModel!;
        int editorDeletes = 0;
        int filmstripDeletes = 0;
        viewModel.DeleteRequested += (_, _) => editorDeletes++;
        probe.Window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Delete && !e.Handled) filmstripDeletes++;
        };

        Check(probe.Filmstrip.Focus(), "filmstrip receives keyboard focus");
        RaiseDelete(probe.Filmstrip);
        Equal(0, editorDeletes, "filmstrip Delete does not reach editor command");
        Equal(1, filmstripDeletes, "filmstrip Delete remains available to task handler");
        Check(viewModel.HasAnnotations, "filmstrip Delete preserves document annotations");

        Check(probe.Workspace.Editor!.Focus(), "editor receives keyboard focus");
        RaiseDelete(probe.Workspace.Editor);
        Equal(1, editorDeletes, "editor Delete reaches annotation command");
        Equal(1, filmstripDeletes, "editor Delete does not escape to task handler");
        Check(!viewModel.HasAnnotations, "editor Delete removes selected annotation");
    }

    private static void RaiseDelete(Control source)
    {
        source.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete });
        Pump();
    }

    private static void Edit(ImageWorkspaceView workspace)
    {
        SKBitmap annotation = new(20, 20);
        annotation.Erase(SKColors.Lime);
        workspace.Editor!.InsertImageAnnotation(annotation, new Point(8, 8));
        Pump();
        Check(workspace.IsDirty, "real annotation marks the document dirty");
        Check(workspace.ViewModel!.HasAnnotations, "real annotation is retained");
    }

    private static void AssertSource(ImageWorkspaceView workspace, string path, int width, int height, SKColor color)
    {
        Pump();
        Check(workspace.HasImage, "workspace has an image");
        Equal(path, workspace.ImagePath, "active image path");
        using SKBitmap source = workspace.Editor!.GetSource() ?? throw new InvalidOperationException("Editor has no decoded image.");
        Equal(width, source.Width, "decoded image width");
        Equal(height, source.Height, "decoded image height");
        Equal(color, source.GetPixel(width / 2, height / 2), "decoded image pixels");
    }

    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static T Wait<T>(Task<T> task)
    {
        PumpUntil(() => task.IsCompleted, "asynchronous workspace operation finishes");
        T result = task.GetAwaiter().GetResult();
        Pump();
        return result;
    }

    private static void PumpUntil(Func<bool> completed, string description)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!completed())
        {
            Pump();
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(description);
            Thread.Sleep(2);
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class WorkspaceProbe : IDisposable
    {
        public ImageWorkspaceView Workspace { get; }
        public Window Window { get; }
        public Border Filmstrip { get; } = new() { Focusable = true, Height = 40 };
        public ImageWorkspaceLeaveChoice LeaveChoice { get; set; } = ImageWorkspaceLeaveChoice.Cancel;
        public Func<Task<ImageWorkspaceLeaveChoice>>? Confirm { get; set; }
        public Func<SKBitmap, string?, bool, Task<string?>>? Save { get; set; }
        public List<Exception> Errors { get; } = [];
        public int ConfirmCount { get; private set; }
        public int SaveCount { get; private set; }

        public WorkspaceProbe(Func<string, Task<SKBitmap?>>? decode = null)
        {
            Workspace = new ImageWorkspaceView(new ImageEditorOptions
            {
                AutoCopyImageToClipboard = false,
                ShowExitConfirmation = true
            }, new ImageWorkspaceServices
            {
                DecodeImageAsync = decode ?? (path => Task.Run<SKBitmap?>(() => SKBitmap.Decode(path))),
                ConfirmLeaveAsync = () =>
                {
                    ConfirmCount++;
                    return Confirm?.Invoke() ?? Task.FromResult(LeaveChoice);
                },
                SaveImageAsync = (bitmap, path, saveAs) =>
                {
                    SaveCount++;
                    return Save?.Invoke(bitmap, path, saveAs) ?? Task.FromResult(path);
                },
                ReportError = Errors.Add
            });
            Grid content = new() { RowDefinitions = new RowDefinitions("*,Auto") };
            content.Children.Add(Workspace);
            Grid.SetRow(Filmstrip, 1);
            content.Children.Add(Filmstrip);
            Window = new Window { Width = 1100, Height = 760, Content = content };
            Window.Show();
            Pump();
            Window.UpdateLayout();
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Window.Content = null;
            Window.Close();
            Pump();
        }
    }

    private sealed class Fixtures : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ShareX.ImageWorkspace.Tests-" + Guid.NewGuid().ToString("N"));
        public string A => Path.Combine(_directory, "red.png");
        public string B => Path.Combine(_directory, "blue.png");
        public string C => Path.Combine(_directory, "gold.png");
        public string Missing => Path.Combine(_directory, "missing.png");
        public string Corrupt => Path.Combine(_directory, "corrupt.png");
        public string Saved => Path.Combine(_directory, "edited.png");

        public Fixtures()
        {
            Directory.CreateDirectory(_directory);
            Write(A, 96, 64, SKColors.Red);
            Write(B, 72, 54, SKColors.Blue);
            Write(C, 48, 32, SKColors.Gold);
            File.WriteAllText(Corrupt, "This is intentionally not an image.");
        }

        private static void Write(string path, int width, int height, SKColor color)
        {
            using SKBitmap bitmap = new(width, height);
            bitmap.Erase(color);
            using SKImage image = SKImage.FromBitmap(bitmap);
            using SKData png = image.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(path, png.ToArray());
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
