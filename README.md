# FastFlow

Save your Windows workspace — every open app, its window size/position, and the **virtual desktop** it lives on — as a named *workflow*, and restore it later with one click.

## How it works

1. **Capture** – Open and arrange your apps across your virtual desktops, then click **Capture new workflow** and give it a name.
   FastFlow records, for every app window on every desktop:
   - the executable (and its command-line arguments, or the AppUserModelID for Store/MSIX apps, or the folder for File Explorer windows),
   - for browser windows (Chrome, Edge, Brave, Vivaldi, Opera, Firefox, ...), the URLs of the open tabs,
   - the virtual desktop number,
   - the window's position, size and state (normal / maximized / minimized). The position is where the window actually is on screen, so snapped layouts (Win+Arrow keys, Snap Layouts, side-by-side windows) and windows on other monitors are restored exactly as arranged.
2. **Run** – Select a workflow and click **Run workflow** (or double-click it, or press its shortcut). FastFlow:
   - closes every open app on every virtual desktop (when *Close all open apps before running* is checked – the default), so you start the workflow from a clean slate,
   - creates missing virtual desktops,
   - launches each app (or reuses an already-open window of it, if *Reuse windows that are already open* is checked),
   - waits for the window, sets its size/position/state, and moves it to its desktop,
   - once the apps are open, switches you to the desktop chosen with **Desktop after run...** – by default, the workflow's desktop if all of its windows are on one virtual desktop (workflows that span several desktops leave you where you are),
   - checks again a few seconds later and puts back any window that an app moved or resized itself after starting (browsers and Electron apps often restore their own last position).
   - when everything is open, shows a small notification that slides up from the bottom of the screen ("*Work* is ready") for a few seconds. It doesn't take focus; click it to dismiss it.
3. **Edit** – The grid on the right shows the captured windows. You can edit arguments, tab URLs, positions or the desktop number, or uncheck *Enabled* to skip a window. Changes are saved automatically.

Workflows captured with an older version of FastFlow saved the window's pre-snap size, so a snapped window comes back at its old floating size. Arrange your windows and click **Re-capture (update)** to save the exact layout.

Workflows are stored in `%APPDATA%\FastFlow\workflows.json`, options in `settings.json` next to it.

### Browser tabs

When a workflow is captured, FastFlow reads the tabs of every browser window. Browsers only show the address of the active tab, so FastFlow briefly clicks through each tab and then reselects the one that was active. When the workflow runs, every browser window is opened as a new window with those tabs (Chromium browsers are started with `--new-window url1 url2 ...`), so e.g. a *Social* workflow can bring back Brave with Instagram and TikTok. Browser windows with saved tabs always open a new window, even when *Reuse windows that are already open* is checked.

The *Tabs* column holds the URLs separated by spaces – edit it to add or remove tabs, or clear it to just start the browser as before. Internal pages (new tab, settings, `about:`) are not saved. If your browser is set to restore the previous session on startup, it will open those windows as well.

Firefox-based browsers get the URLs as plain arguments: when Firefox is not running yet (the default, with *Close all open apps* checked) they open as tabs of one window, but a second Firefox window in the same workflow opens its tabs in the first one.

### Switching between workflows

Apps are closed the same way as clicking their X button, so they can save their session or ask about unsaved work. FastFlow waits up to 10 seconds for them; a window that is still open after that (e.g. a pending "Save changes?" prompt) is left alone and the workflow starts anyway. FastFlow itself is never closed.

To leave specific apps running (e.g. the MEmu Android emulator), click **Apps to keep open...** next to the option and add them, one per line, by executable name (`MEmu` or `MEmu.exe`), or pick one from the list of apps that are open right now. Their windows are skipped on every desktop. Some apps use several executables (MEmu's multi-instance manager is `MEmuConsole`), so add each one whose window should stay open.

Uncheck *Close all open apps* to keep the old behaviour of adding the workflow's apps to what is already open.

### Keyboard shortcuts

Select a workflow and click **Set shortcut...**, then press the combination you want – Ctrl, Alt, Shift and/or Win plus one key, e.g. `Ctrl+Alt+1` or `Ctrl+Alt+Shift+F5` (F1–F24 also work on their own). The shortcut is shown next to the workflow name and works from any app, on any desktop.

Shortcuts work while FastFlow is running, so closing the window keeps it in the notification area (tray); right-click the tray icon to run a workflow or **Exit**. Check *Start with Windows* (also in the tray menu) to start FastFlow hidden in the tray at sign-in (`FastFlow.exe --tray`), so your shortcuts are always available. If a combination is already taken by Windows or another app, the log says so – pick another one.

### Desktop after run

Select a workflow and click **Desktop after run...** to choose which virtual desktop you end up on once all of its apps are open:

- **Automatic** (the default) – the workflow's desktop if all of its windows are on one desktop; otherwise you stay where you are.
- **Stay on the current desktop** – never switch.
- **Desktop 1, 2, ...** – always switch to that desktop (it is created if it doesn't exist yet), e.g. so a workflow that spans three desktops always leaves you on desktop 2.

The choice is saved with the workflow and kept when you re-capture it.

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
- **What's inside an app is not restored**, apart from browser tabs – e.g. open documents are up to the app itself (most editors restore their own session). For apps that take a file/folder argument, you can edit *Arguments* in the grid.
- **Elevated (Run as administrator) apps** can't be moved or resized by a non-elevated FastFlow because of Windows security (UIPI). Run FastFlow as administrator if you need that.
- **Store (UWP) apps that are suspended** while capturing may be skipped, because Windows hides which app owns the window.
- Window coordinates are physical pixels; if you change your monitor setup or scaling, re-capture the workflow (**Re-capture (update)**).

## Project layout

```
src/FastFlow/
  Program.cs                     entry point, --run / --tray command line, single instance
  Models/                        Workflow / WindowEntry (what gets saved)
  Core/WindowEnumerator.cs       finds app windows on all desktops, their exe, args, AUMID
  Core/WorkflowCapturer.cs       builds a Workflow from the current screen
  Core/BrowserTabs.cs            reads browser tab URLs (UI Automation) and reopens them
  Core/WorkflowRunner.cs         launches apps, finds their windows, restores placement and desktop
  Core/AppCloser.cs              closes all open apps on all desktops before a workflow runs
  Core/WorkflowStore.cs          JSON persistence
  VirtualDesktops/               documented + version-specific undocumented virtual desktop COM interop
  UI/                            WinForms main window, tray icon, global shortcuts, shortcut recorder
```
