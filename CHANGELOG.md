# Changelog

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
