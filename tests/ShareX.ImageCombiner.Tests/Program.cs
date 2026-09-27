using Newtonsoft.Json;
using ShareX.HelpersLib;
using ShareX.Tools;
using System.Drawing.Imaging;
using CombinerAlignment = ShareX.Tools.ImageCombinerAlignment;
using DrawingAlignment = ShareX.HelpersLib.ImageCombinerAlignment;

internal static class Program
{
    private const int ImageWidth = 10;
    private const int ImageHeight = 6;

    private static async Task<int> Main()
    {
        using ImageFixtures fixtures = new ImageFixtures(15);
        (string Name, Func<Task> Run)[] checks =
        [
            ("15 images form both requested grids", () => RequestedGridsAsync(fixtures)),
            ("incomplete rows, spacing, and background", () => Run(() => IncompleteRows(fixtures))),
            ("one column and excess columns", () => Run(() => ExtremeLayouts(fixtures))),
            ("grid counts follow added and removed images", () => Run(() => CollectionCounts(fixtures))),
            ("grid toggles preserve legacy options", () => Run(() => LegacyCompatibility(fixtures))),
            ("old and new saved settings", () => Run(() => SavedSettings(fixtures))),
            ("loaded and edited columns stay bounded", () => Run(() => ColumnBounds(fixtures)))
        ];

        int failures = 0;
        foreach ((string name, Func<Task> run) in checks)
        {
            try
            {
                await run();
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

    private static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    private static async Task RequestedGridsAsync(ImageFixtures fixtures)
    {
        ImageCombinerOptions options = GridOptions(3);
        using RequestProbe probe = new RequestProbe(options, fixtures.Paths);
        Equal(5, probe.ViewModel.GridRows, "three-column row count");
        Equal(3, probe.ViewModel.GridColumnCount, "three-column column count");
        string firstSummary = probe.ViewModel.GridSizeText;
        Check(!string.IsNullOrWhiteSpace(firstSummary), "grid summary is visible");
        VerifyGrid(probe.Preview, 3, 5, fixtures);

        probe.ViewModel.GridColumns = 5;
        Equal(3, probe.ViewModel.GridRows, "five-column row count");
        Equal(5, probe.ViewModel.GridColumnCount, "five-column column count");
        Check(firstSummary != probe.ViewModel.GridSizeText, "summary changes with layout");
        VerifyGrid(probe.Preview, 5, 3, fixtures);

        await probe.ViewModel.CombineCommand.ExecuteAsync(null);
        ImageCombineRequest export = probe.Export ?? throw new InvalidOperationException("Combine did not submit an export request.");
        Equal(probe.Preview.Options.Orientation, export.Options.Orientation, "preview/export orientation");
        Equal(probe.Preview.Options.WrapAfter, export.Options.WrapAfter, "preview/export columns");
        Equal(probe.Preview.Options.Space, export.Options.Space, "preview/export spacing");
        Check(probe.Preview.ImageFiles.SequenceEqual(export.ImageFiles), "preview/export image order");
        VerifyGrid(export, 5, 3, fixtures);
    }

    private static void IncompleteRows(ImageFixtures fixtures)
    {
        using RequestProbe probe = new RequestProbe(GridOptions(3), fixtures.Paths.Take(7));
        Equal(3, probe.ViewModel.GridRows, "partial final row count");
        VerifyGrid(probe.Preview, 3, 3, fixtures);
        using (Bitmap output = Render(probe.Preview))
        {
            Equal(0, output.GetPixel(10, 0).A, "horizontal spacing is transparent");
            Equal(0, output.GetPixel(0, 6).A, "vertical spacing is transparent");
            Equal(0, output.GetPixel(output.Width - 1, output.Height - 1).A, "unoccupied final cell is transparent");
        }

        probe.ViewModel.AutoFillBackground = true;
        using Bitmap filledOutput = Render(probe.Preview);
        Equal(fixtures.Colors[0].ToArgb(), filledOutput.GetPixel(10, 0).ToArgb(), "spacing uses first image background");
        Equal(fixtures.Colors[0].ToArgb(), filledOutput.GetPixel(filledOutput.Width - 1, filledOutput.Height - 1).ToArgb(), "unoccupied final cell uses background");
    }

    private static void ExtremeLayouts(ImageFixtures fixtures)
    {
        using RequestProbe probe = new RequestProbe(GridOptions(1), fixtures.Paths.Take(7));
        Equal(7, probe.ViewModel.GridRows, "single-column row count");
        Equal(1, probe.ViewModel.GridColumnCount, "single-column column count");
        VerifyGrid(probe.Preview, 1, 7, fixtures);

        probe.ViewModel.GridColumns = 1000;
        Equal(1, probe.ViewModel.GridRows, "excess-columns row count");
        Equal(7, probe.ViewModel.GridColumnCount, "only occupied columns count");
        VerifyGrid(probe.Preview, 7, 1, fixtures);
    }

    private static void CollectionCounts(ImageFixtures fixtures)
    {
        using RequestProbe probe = new RequestProbe(GridOptions(3), []);
        Equal(0, probe.ViewModel.GridRows, "empty grid row count");
        Equal(0, probe.ViewModel.GridColumnCount, "empty grid column count");
        Check(!probe.ViewModel.CanCombine, "empty grid cannot combine");

        HashSet<string?> notifications = [];
        probe.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        probe.ViewModel.AddFiles(fixtures.Paths.Take(2));
        Equal(1, probe.ViewModel.GridRows, "two images row count");
        Equal(2, probe.ViewModel.GridColumnCount, "two images column count");
        Check(notifications.Contains(nameof(ImageCombinerViewModel.GridRows)), "adding images notifies rows");
        Check(notifications.Contains(nameof(ImageCombinerViewModel.GridColumnCount)), "adding images notifies columns");
        Check(notifications.Contains(nameof(ImageCombinerViewModel.GridSizeText)), "adding images notifies summary");

        probe.ViewModel.AddFiles(fixtures.Paths.Skip(2).Take(3));
        Equal(2, probe.ViewModel.GridRows, "five images row count");
        Equal(3, probe.ViewModel.GridColumnCount, "five images column count");

        probe.ViewModel.SetSelectedImages(fixtures.Paths.Skip(1).Take(4));
        probe.ViewModel.RemoveCommand.Execute(null);
        Equal(1, probe.ViewModel.GridRows, "removing images updates rows");
        Equal(1, probe.ViewModel.GridColumnCount, "removing images updates columns");
        Check(!probe.ViewModel.CanCombine, "one image cannot combine");
    }

    private static void LegacyCompatibility(ImageFixtures fixtures)
    {
        ImageCombinerOptions options = new ImageCombinerOptions
        {
            Orientation = ImageCombinerOrientation.Vertical,
            WrapAfter = 4,
            GridColumns = 3,
            Alignment = CombinerAlignment.RightOrBottom,
            Space = 2
        };
        using RequestProbe probe = new RequestProbe(options, fixtures.Paths);
        Equal(ImageCombinerOrientation.Vertical, probe.Preview.Options.Orientation, "legacy direction");
        Equal(4, probe.Preview.Options.WrapAfter, "legacy wrapping");

        probe.ViewModel.UseGrid = true;
        Equal(ImageCombinerOrientation.Horizontal, probe.Preview.Options.Orientation, "grid direction");
        Equal(3, probe.Preview.Options.WrapAfter, "grid wrapping");
        Equal(ImageCombinerOrientation.Vertical, options.Orientation, "saved legacy direction survives grid");
        Equal(4, options.WrapAfter, "saved legacy wrapping survives grid");

        probe.ViewModel.GridColumns = 5;
        probe.ViewModel.UseGrid = false;
        Equal(ImageCombinerOrientation.Vertical, probe.Preview.Options.Orientation, "legacy direction restored");
        Equal(4, probe.Preview.Options.WrapAfter, "legacy wrapping restored");
        Equal(CombinerAlignment.RightOrBottom, probe.Preview.Options.Alignment, "alignment preserved");
        Equal(5, options.GridColumns, "chosen grid width retained");

        using Bitmap legacyOutput = Render(probe.Preview);
        Equal(46, legacyOutput.Width, "legacy wrapped-column output width");
        Equal(30, legacyOutput.Height, "legacy wrapped-column output height");
    }

    private static void SavedSettings(ImageFixtures fixtures)
    {
        ImageCombinerOptions oldOptions = JsonConvert.DeserializeObject<ImageCombinerOptions>("{\"Orientation\":1,\"WrapAfter\":4}")
            ?? throw new InvalidOperationException("Could not read old settings.");
        Check(!oldOptions.UseGrid, "old settings keep legacy layout");
        Equal(3, oldOptions.GridColumns, "old settings receive useful grid default");

        using RequestProbe probe = new RequestProbe(oldOptions, fixtures.Paths);
        probe.ViewModel.UseGrid = true;
        probe.ViewModel.GridColumns = 5;
        ImageCombinerOptions restored = JsonConvert.DeserializeObject<ImageCombinerOptions>(JsonConvert.SerializeObject(oldOptions))
            ?? throw new InvalidOperationException("Could not restore grid settings.");
        Check(restored.UseGrid, "grid mode persists");
        Equal(5, restored.GridColumns, "grid width persists");
        Equal(ImageCombinerOrientation.Vertical, restored.Orientation, "legacy direction persists");
        Equal(4, restored.WrapAfter, "legacy wrapping persists");
        using RequestProbe reopened = new RequestProbe(restored, fixtures.Paths);
        Equal(3, reopened.ViewModel.GridRows, "restored grid row count");
        Equal(5, reopened.Preview.Options.WrapAfter, "restored grid render width");
    }

    private static void ColumnBounds(ImageFixtures fixtures)
    {
        foreach ((int value, int expected) in new[] { (-10, 1), (0, 1), (1001, 1000), (int.MaxValue, 1000) })
        {
            using RequestProbe probe = new RequestProbe(GridOptions(value), fixtures.Paths);
            Equal(expected, probe.Preview.Options.WrapAfter, $"loaded column bound for {value}");
        }

        using RequestProbe edited = new RequestProbe(GridOptions(3), fixtures.Paths);
        foreach ((decimal value, int expected) in new[] { (-7m, 1), (0m, 1), (1001m, 1000), (decimal.MaxValue, 1000) })
        {
            edited.ViewModel.GridColumns = value;
            Equal(expected, edited.Preview.Options.WrapAfter, $"edited column bound for {value}");
            Equal(expected, edited.Options.GridColumns, $"persisted column bound for {value}");
        }
    }

    private static ImageCombinerOptions GridOptions(int columns) => new ImageCombinerOptions
    {
        UseGrid = true,
        GridColumns = columns,
        Space = 2,
        AutoFillBackground = false
    };

    private static void VerifyGrid(ImageCombineRequest request, int columns, int rows, ImageFixtures fixtures)
    {
        Equal(ImageCombinerOrientation.Horizontal, request.Options.Orientation, "grid renders across rows");
        using Bitmap output = Render(request);
        int space = request.Options.Space;
        Equal(columns * ImageWidth + (columns - 1) * space, output.Width, "output width");
        Equal(rows * ImageHeight + (rows - 1) * space, output.Height, "output height");
        for (int index = 0; index < request.ImageFiles.Count; index++)
        {
            int fixtureIndex = Array.IndexOf(fixtures.Paths, request.ImageFiles[index]);
            int x = index % columns * (ImageWidth + space);
            int y = index / columns * (ImageHeight + space);
            Equal(fixtures.Colors[fixtureIndex].ToArgb(), output.GetPixel(x + ImageWidth / 2, y + ImageHeight / 2).ToArgb(), $"image {index + 1} row-major position");
        }
    }

    private static Bitmap Render(ImageCombineRequest request) => ImageHelpers.CombineImages(
        request.ImageFiles,
        (Orientation)request.Options.Orientation,
        (DrawingAlignment)request.Options.Alignment,
        request.Options.Space,
        request.Options.WrapAfter,
        request.Options.AutoFillBackground) ?? throw new InvalidOperationException("Renderer returned no image.");

    private static void Equal<T>(T expected, T actual, string description)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{description}: expected {expected}, got {actual}.");
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }

    private sealed class RequestProbe : IDisposable
    {
        private ImageCombineRequest? _preview;
        public ImageCombinerOptions Options { get; }
        public ImageCombinerViewModel ViewModel { get; }
        public ImageCombineRequest Preview => _preview ?? throw new InvalidOperationException("No preview request was created.");
        public ImageCombineRequest? Export { get; private set; }

        public RequestProbe(ImageCombinerOptions options, IEnumerable<string> files)
        {
            Options = options;
            ViewModel = new ImageCombinerViewModel(options, new ImageCombinerServices
            {
                CreatePreviewAsync = request =>
                {
                    _preview = request;
                    return Task.FromResult<byte[]?>(null);
                },
                ProcessAsync = request =>
                {
                    Export = request;
                    return Task.CompletedTask;
                }
            }, files);
        }

        public void Dispose() => ViewModel.Dispose();
    }

    private sealed class ImageFixtures : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "ShareX.ImageCombiner.Tests-" + Guid.NewGuid().ToString("N"));
        public string[] Paths { get; }
        public Color[] Colors { get; }

        public ImageFixtures(int count)
        {
            Directory.CreateDirectory(_directory);
            Paths = new string[count];
            Colors = new Color[count];
            for (int index = 0; index < count; index++)
            {
                Paths[index] = Path.Combine(_directory, $"{index + 1:D2}.png");
                Colors[index] = Color.FromArgb(255, 20 + index * 11, 40 + index * 7, 60 + index * 5);
                using Bitmap bitmap = new Bitmap(ImageWidth, ImageHeight);
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.Clear(Colors[index]);
                bitmap.Save(Paths[index], ImageFormat.Png);
            }
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
