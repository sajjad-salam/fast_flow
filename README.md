# FastFlow

Save your Windows workspace — every open app, its window size/position, and the **virtual desktop** it lives on — as a named *workflow*, and restore it later with one click.

## How it works

1. **Capture** – Open and arrange your apps across your virtual desktops, then click **Capture new workflow** and give it a name.
   FastFlow records, for every app window on every desktop:
   - the executable (and its command-line arguments, or the AppUserModelID for Store/MSIX apps, or the folder for File Explorer windows),
   - the virtual desktop number,
   - the window's position, size and state (normal / maximized / minimized).
2. **Run** – Select a workflow and click **Run workflow** (or double-click it). FastFlow:
   - creates missing virtual desktops,
   - launches each app (or reuses an already-open window of it, if *Reuse windows that are already open* is checked),
   - waits for the window, sets its size/position/state, and moves it to its desktop,
   - does a second pass a moment later for apps that reposition themselves after starting.
3. **Edit** – The grid on the right shows the captured windows. You can edit arguments, positions or the desktop number, or uncheck *Enabled* to skip a window. Changes are saved automatically.

Workflows are stored in `%APPDATA%\FastFlow\workflows.json`.

### One-click from anywhere

Click **Copy run command** to get a command like:

```
"C:\Tools\FastFlow\FastFlow.exe" --run "Coding"
```

Use it as the target of a desktop/taskbar shortcut (you can assign a shortcut key in the shortcut's properties) to restore a workflow without opening the manager.

## Build

Requires the .NET 8 SDK on Windows.

```
dotnet build -c Release
dotnet run --project src/FastFlow
```

Or publish a single exe:

```
dotnet publish src/FastFlow/FastFlow.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

Every push also builds on GitHub Actions; the compiled app is attached to the run as the `FastFlow-win-x64` artifact.

## Supported Windows versions

Reading which desktop a window is on uses a documented Windows API and works everywhere. **Moving another app's window to a different desktop and creating desktops have no public API**, so FastFlow uses the same undocumented shell COM interfaces as tools like PowerToys/VirtualDesktop, whose IDs change between Windows builds. Currently supported:

| Windows | Builds |
|---|---|
| Windows 10 2004 – 22H2 | 19041 – 19045 |
| Windows 11 22H2 (updated) / 23H2 | 22621.2215+, 22631 |
| Windows 11 24H2 and later | 26100+ |

On other builds the status line shows *read-only*: capture still works and apps are still launched and positioned, but they open on the current desktop. If a future Windows update changes the interface IDs, add a new `Interop.*.cs` file in `src/FastFlow/VirtualDesktops` and a line in `VirtualDesktopService.CreateBackend`.

## Known limitations

- **Single-instance apps** (e.g. some chat or music apps) don't open a second window when launched again. Keep *Reuse windows that are already open* checked so the existing window is moved instead.
- **What's inside an app is not restored** – e.g. browser tabs or open documents are up to the app itself (most browsers and editors restore their own session). For apps that take a file/folder argument, you can edit *Arguments* in the grid.
- **Elevated (Run as administrator) apps** can't be moved or resized by a non-elevated FastFlow because of Windows security (UIPI). Run FastFlow as administrator if you need that.
- **Store (UWP) apps that are suspended** while capturing may be skipped, because Windows hides which app owns the window.
- Window coordinates are physical pixels; if you change your monitor setup or scaling, re-capture the workflow (**Re-capture (update)**).

## Project layout

```
src/FastFlow/
  Program.cs                     entry point, --run command line
  Models/                        Workflow / WindowEntry (what gets saved)
  Core/WindowEnumerator.cs       finds app windows on all desktops, their exe, args, AUMID
  Core/WorkflowCapturer.cs       builds a Workflow from the current screen
  Core/WorkflowRunner.cs         launches apps, finds their windows, restores placement and desktop
  Core/WorkflowStore.cs          JSON persistence
  VirtualDesktops/               documented + version-specific undocumented virtual desktop COM interop
  UI/                            WinForms main window
```
