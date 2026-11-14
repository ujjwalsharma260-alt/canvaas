# Changelog

## 0.3.3 — No jump when leaving fullscreen + menu no longer hides "Settings"
- Fixed: leaving fullscreen (toolbar button, ESC, or the F/F11 cycle) visibly switched modes. One frame showed WPF's remembered "restore" size (a tiny 720×480 window) before the maximized window appeared, and the taskbar could pop up over the still full-screen-sized window. Exit now restores `ResizeMode` first (WPF ignores `Maximized` while a window is `NoResize`), pre-sizes the window to the monitor work area, maximizes, and only drops "always on top" after the new layout has been laid out. The forced `SWP_FRAMECHANGED` repaint was removed from both directions.
- Fixed: with floating (unpinned) toolbars, opening the ☰ menu hid its first row — "Settings" was drawn behind the toolbar. The menu is now parked just below the visible title bar/toolbar each time it opens, and re-positioned if the bars change while it is open.
- Measured: the window rectangle sampled every ~10 ms around enter and exit shows a single change (1366×768 ↔ 1380×782 at −7,−7), no intermediate size; in floating-toolbar fullscreen the menu screenshot now shows "Settings" as the first visible row.

## 0.3.2 — Launch no longer flashes a windowed mode
- Fixed: clicking the app icon showed a maximized window with the Windows taskbar for a moment, then jumped to fullscreen — it looked like the app pressed the fullscreen key by itself. Cause: the window was created `Maximized`, and the switch to fullscreen ran later from the `Loaded` handler at `ApplicationIdle` priority, so the first layout and paint happened in the wrong mode. The fullscreen geometry is now applied in `OnSourceInitialized` (handle exists, nothing painted yet), and `MainWindow.xaml` starts as `WindowState="Normal"`, `ResizeMode="NoResize"`, `WindowStartupLocation="Manual"`. The delayed `WindowState = Maximized` + `ToggleFullscreen()` calls were removed from `Loaded`.
- Fullscreen size now comes from the real bounds of the monitor the window is on (`MonitorFromWindow` + `GetMonitorInfo`, converted to WPF units with the window's DPI) instead of `SystemParameters.PrimaryScreenWidth/Height`.
- Verified: Release build 0 errors (3 pre-existing warnings). A probe launched the built exe and sampled `GetWindowRect` every ~25 ms: a single state was observed, `left=0 top=0 size=1366x768` (screen 1366×768), with the taskbar already covered on the first visible frame.

## 0.3.1 — Quotes now survive a restart + real app icon
- Fixed: adding Quote 2, 3 or 4 in Settings only lasted until the app closed; after reopening, only Quote 1 remained. Cause: when the Settings panel filled the four quote boxes on startup, the first box triggered the "text changed" code while boxes 2–4 were still empty, which saved them as blank. A guard flag now stops automatic filling from being treated as typing.
- Settings loading now accepts an older quote list of any length (previously it required exactly four saved quotes).
- Saving always stores the four quote slots in a stable, padded form.
- The existing `canvaas-icon.ico` is now built into the app: it shows on `Canvaas.exe` in File Explorer and in the taskbar/title icon, so the app no longer depends on a desktop shortcut for its icon.
## 0.3.0 — Corner close + visual polish
- Hovering the very top-right screen edge in fullscreen reveals minimize / maximize / close buttons (no more pressing a shortcut just to close).
- Colour swatches are now glossy vector circles (sharp at any size); toolbar tool buttons are rounded chips with a blue tint for the active tool.
- Hand panning behaviour unchanged (a smoother pan experiment was removed at the user's request before release).
## 0.2.0 — Quality of life fixes (v0.2.0)
- App now starts directly in the friendly mode: full screen, toolbars visible, taskbar hidden (no need to press a key after launch).
- Each ink tool (pen, highlighter, calligraphy) remembers its own colour; switching tools restores that tool's colour instead of sharing one global colour.
- Removed the desktop snip feature (button, F9 shortcut and all its code).
- Screen-draw toolbar icon changed (was an emoji pen that looked like the pen tool).
- tools/check_files.py now scans all partial-class source files (fixes false "missing handler" reports).

## 0.1.0 — Phase 1 (handwriting prototype)  [fix1: added missing using System.IO; awaiting build + hand test]
- White writing page with pen and mouse input.
- Pen and stroke-eraser tools.
- Undo / Redo (Ctrl+Z, Ctrl+Y).
- Clear page with confirmation (can be undone).
- New, Open, Save, Save As (`.canvaas` files, format version 1, see `docs/FILE_FORMAT.md`).
- Safe saving (temp file then replace), clear error messages, unsaved-changes prompts.
- Status bar reports pressure information of the last stroke (for tablet testing).

## 0.0.1 — Phase 0 (foundation) — CONFIRMED
- Clean restart: minimal WPF app on .NET 10 that opens a window titled Canvaas.
- GitHub Actions builds on Windows and publishes a single self-contained `Canvaas.exe` artifact.
- Added `CONTEXT.md` and `tools/check_files.py`.
- Confirmed: green Actions run #1; user downloaded and opened the exe.
