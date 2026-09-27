# Image combiner grid

This fork of ShareX adds a visible grid control to the image combiner. It builds on the existing horizontal wrapping renderer and preserves the original horizontal and vertical settings.

Source: [leoparmint/ShareX, image-combiner-grid branch](https://github.com/leoparmint/ShareX/tree/image-combiner-grid).

## Use it

1. Open this build of ShareX and choose **Tools → Image combiner**.
2. Add or drop the original individual images into the list. Use the arrow buttons to adjust their order.
3. Enable **Grid (rows and columns)**.
4. Set **Images per row**:

   | Images | Images per row | Result |
   | --- | --- | --- |
   | 15 | 3 | 5 rows × 3 columns |
   | 15 | 5 | 3 rows × 5 columns |

5. Check the preview, adjust spacing and alignment, and select **Combine images**. The result follows your configured ShareX image tasks.

Images are placed left to right, then top to bottom, in list order. An incomplete last row contains the remaining images. The row and column counts update when images are added or removed. Grid settings are remembered; disabling Grid restores the previous orientation and wrapping settings.

Original image sizes are preserved. Equal-sized images make a regular grid. Different-sized images use the existing combiner's row layout: alignment applies within each row, and column edges can differ between rows. This feature takes individual images; it does not split an already-combined strip.

## Portable build

Extract the entire portable ZIP into a writable folder and run `ShareX.exe` there. Exit another running copy of ShareX first so Windows opens this build. The included `Portable` marker keeps this copy's settings alongside the executable. This build is based on ShareX's current development source and targets Windows 11 22H2 or newer, x64.

## Build and verify

On Windows with the .NET 10 SDK, from the repository root:

```powershell
dotnet restore ShareX/ShareX.csproj -p:Platform=x64 --source https://api.nuget.org/v3/index.json
dotnet build ShareX/ShareX.csproj -c Release -p:Platform=x64 --self-contained true --no-restore
dotnet restore tests/ShareX.ImageCombiner.Tests -p:Platform=x64 --source https://api.nuget.org/v3/index.json
dotnet run --project tests/ShareX.ImageCombiner.Tests -c Release -p:Platform=x64 --no-restore
```

The regression harness checks output dimensions and pixels for both 15-image layouts, image order, spacing, incomplete rows, preview/export consistency, saved settings, column limits, and legacy layouts. The output executable is `ShareX/bin/Release/win-x64/ShareX.exe`.

ShareX and this fork retain the original license. See [LICENSE.txt](LICENSE.txt).
