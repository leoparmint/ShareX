# Image combiner regression checks

Run on Windows with the .NET 10 SDK:

```powershell
dotnet run --project tests/ShareX.ImageCombiner.Tests -c Release -p:Platform=x64
```

This console harness uses the production view model and image renderer without adding a test framework. It checks grid geometry, image order, spacing, incomplete rows, preview/export requests, saved settings, grid bounds, and legacy layout compatibility. It creates temporary PNG fixtures and removes them after the run. A failed check produces a nonzero exit code.
