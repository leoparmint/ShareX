# Image workspace

This fork adds a large editing canvas above a horizontal image strip in ShareX's main window. It uses ShareX's real image editor, including annotation tools, cropping, zoom, pan, undo, copy, Save, and Save As.

Source: [leoparmint/ShareX](https://github.com/leoparmint/ShareX/tree/image-combiner-grid).

## Use it

- Click **Open images…** or press **Ctrl+O** to add local image files. These imports do not run upload tasks. Recent captures also appear in the strip.
- Click an image in the bottom strip to show it in the editor. The active image has a stronger border; an asterisk beside its filename indicates unsaved edits.
- Use the previous/next buttons, or the left/right arrow keys while the strip has focus. The strip scrolls horizontally; drag the divider above it to change its height.
- Use the editor's zoom picker or **Ctrl+mouse wheel** to zoom; hold the middle mouse button to pan. Arrow keys in the editor continue to move annotations.
- Use **Save** to update the current file or **Save As** to create another file. Switching images, removing the active image from the list, and exiting the app check for unsaved edits. Cancelling the prompt or a save leaves the current edit open. Finish or cancel an open effect dialog before switching.
- Double-click a thumbnail for the separate full-screen viewer. Non-image tasks retain their existing open behavior.
- **Ctrl-click** or **Shift-click** thumbnails to select multiple images. Choose **Combine images** above the editor to open the [grid combiner](IMAGE_COMBINER_GRID.md).

The editor and strip use separate keyboard focus: Delete in the canvas deletes selected annotations; Delete in the strip removes selected task entries. Closing to the tray keeps the current edit in memory. Imported files follow ShareX's existing recent-items settings; unsaved edits are not persisted across application exits.

Dropping image files onto the strip or the main-window background imports them locally. Dropping images directly onto the editing canvas keeps ShareX's insert-image behavior. Other file/text drops retain ShareX's existing upload behavior.

## Portable build

Extract the entire workspace portable ZIP and run its `ShareX.exe`. Exit any other copy of ShareX first. The included `Portable` marker stores this copy's settings alongside the executable. This Windows 11 22H2-or-newer x64 build includes the earlier grid-combiner feature.

## Verification

With the .NET 10 SDK on Windows:

```powershell
dotnet restore ShareX/ShareX.csproj -p:Platform=x64 --source https://api.nuget.org/v3/index.json
dotnet build ShareX/ShareX.csproj -c Release -p:Platform=x64 --self-contained true --no-restore
dotnet restore tests/ShareX.ImageWorkspace.Tests -p:Platform=x64 --source https://api.nuget.org/v3/index.json
dotnet run --project tests/ShareX.ImageWorkspace.Tests -c Release -p:Platform=x64 --no-restore
dotnet run --project tests/ShareX.ImageCombiner.Tests -c Release -p:Platform=x64 --no-restore
```

The workspace tests run the real editor with headless rendering and synthetic images, including save-pixel verification, cancellation, failed saves, overlapping image loads, modal dialogs, keyboard focus, and resource disposal. Existing image-combiner regression checks remain available separately.
