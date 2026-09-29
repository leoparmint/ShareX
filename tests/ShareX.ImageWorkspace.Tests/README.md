# Embedded image workspace regression checks

Run on Windows with the .NET 10 SDK:

```powershell
dotnet restore tests/ShareX.ImageWorkspace.Tests -p:Platform=x64 --source https://api.nuget.org/v3/index.json
dotnet run --project tests/ShareX.ImageWorkspace.Tests -c Release -p:Platform=x64 --no-restore
```

The console harness runs the real editor in Avalonia Headless with Skia rendering. It uses temporary images and injected save/confirmation services; it does not load or modify the user's ShareX settings or files. Checks cover decoded image replacement, failure preservation, overlapping loads, edited-image Save/Discard/Cancel, cancelled and failed saves, modal and effect-preview guards, keyboard focus scoping, and pending-load disposal. Any failed check exits with a nonzero status.
