# Changelog

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
