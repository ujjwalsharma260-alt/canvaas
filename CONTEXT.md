# CANVAAS — FULL PROJECT CONTEXT (READ THIS FIRST)

> **For any AI coding assistant:** this file is the single source of truth for the Canvaas project. Read all of it before changing anything. Keep it up to date: every ZIP/patch you deliver MUST include an updated copy of this file (sections 4, 5, 6, 10, 11 especially).
> **Last updated:** 2026-11-14 by opencode (v0.3.1: quote persistence fix in Settings — only Quote 1 was kept after a restart). Update this line every time.

---

## 1. Who the user is and how to work with them

- The user is a **non-programmer**. They use: the GitHub website, GitHub Desktop, File Explorer and ZIP extraction. They do **not** use terminals, PowerShell, or command-line Git, and cannot debug code or merge changes by hand.
- Use **simple English**; explain every technical term the first time.
- Give **one small milestone at a time**, as **numbered, click-by-click steps**. Ask for confirmation or a screenshot before any risky next step.
- Deliver changes as a **complete ZIP** whose contents sit at the repository root (no outer wrapper folder), plus a list of changed files. If a small edit on the GitHub website is easier, give the **exact file path** and the **complete file contents**, and say clearly whether the file is XAML/XML or C#. **Never mix them.**
- **Never claim a build or test succeeded unless you have a log proving it.** If you could not build (e.g. you are on Linux and WPF needs Windows), say so plainly and let GitHub Actions be the judge.
- When a build fails, read the real compiler output, find the root cause, fix it. Do not guess.
- Never delete user data silently. Never tell the user to delete `.git` or overwrite the whole repo. Do not pressure the user to keep using any code they distrust.

### How the user applies a ZIP (give these steps every time, adapted)
1. Download the ZIP and right-click → **Extract All**.
2. Open the extracted folder. Select **everything inside it** (not the folder itself) and copy.
3. Paste into `C:\Users\HP\Documents\GitHub\canvaas`, choose **Replace the files in the destination** if asked. Do not touch the hidden `.git` folder.
4. Open **GitHub Desktop** → check the **Changes** list shows the expected files → type a short summary → **Commit to main** → **Push origin**.
5. Open the repo on GitHub → **Actions** tab → open the newest run → wait for a green tick (red cross = send a screenshot).
6. Never upload a ZIP to GitHub expecting it to unpack itself.

---

## 2. Product

- **Name:** **Canvaas** (exact spelling, two a's). Never rename to StudyNotes or anything else.
- **What:** customizable native **Windows desktop** handwriting and study-notes app (notes, equations, diagrams, revision).
- **Input:** pen-first, especially a **Deco pen tablet** (XP-Pen Deco style). Mouse is a fallback/for navigation. **Test pressure sensitivity; never assume it works.**
- **Canvas modes:** fixed notebook pages **and** infinite canvas.
- **Storage:** local-first, usable without any account. Optional backup/cloud-folder support later (e.g. a OneDrive/Dropbox folder). No real-time multi-device sync early.
- **Budget:** zero. No paid APIs, subscriptions, or required paid cloud. Free toolchain only.
- **Distribution:** a downloadable Windows app (not just source). Currently: a self-contained single `Canvaas.exe` from GitHub Actions. Later: installer + GitHub Releases.
- **Customization:** configurable tools/settings (pen presets, shortcuts, toolbar).

## 3. Repository

- Public repo: https://github.com/ujjwalsharma260-alt/canvaas (may become private later). Branch: `main`.
- User's local folder: `C:\Users\HP\Documents\GitHub\canvaas`.

---

## 4. Technology decisions

| Decision | Choice | Why |
|---|---|---|
| Language/UI | **C# + WPF** | Native Windows, free, and has Windows Ink (`InkCanvas`) with pen/stylus + pressure support built in. Good for a first reliable prototype. |
| .NET version | **.NET 10 (LTS)**, `net10.0-windows` | Verified 2026-10-09: .NET 10 is LTS, released 2025-11-11, supported to Nov 2028. .NET 8 and 9 both reach end of support on **2026-11-10**, so do not use them. |
| Build/CI | **GitHub Actions**, `windows-latest`, `actions/setup-dotnet@v4` with `10.0.x` | Free, reproducible, produces downloadable artifacts. |
| Packaging (now) | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` | One `.exe`, no .NET install required on the user's PC. |
| Packaging (later) | Installer (e.g. free Inno Setup) + GitHub Releases | Phase 5. |
| Solution file | None for now; workflow builds the `.csproj` directly | A hand-written `.sln` is an easy place for errors. Add one only if a second project appears. |

Known trade-offs / risks to keep in mind:
- WPF's `InkCanvas` is great for pen input and stroke storage but is **not built for huge infinite canvases**; Phase 3 may need custom rendering (e.g. tiling, or a custom stroke layer with a transform) — decide with a small spike, not a guess.
- A self-contained single-file `.exe` is large (~150 MB+ is normal) and is **unsigned**, so Windows SmartScreen may warn. Acceptable for now.
- Pressure on some tablets depends on the driver (Windows Ink vs WinTab). Test with the user's Deco tablet and report findings here.

---

## 5. Current status (UPDATE THIS)

- **2026-11-14 (v0.3.1):** Fixed quote persistence. Symptom: the user could add Quote 2–4 in Settings and the popup rotation used them during the session, but after closing and reopening the app only Quote 1 remained. Cause: `SyncSettingsUI()` fills `WatermarkTextBox1..4` one at a time; setting box 1 raised `WatermarkText_TextChanged`, which rebuilt `_watermarkQuotes` from all four boxes while boxes 2–4 were still empty, then saved that blanked array. Fix: a `_suppressWatermarkTextChange` guard flag around the programmatic fill, a `NormalizeQuotes()` helper that always returns four padded slots, and `LoadSettings()` now accepts a saved quote list of any length (was `Length >= 4`). Also baked the user's existing `canvaas-icon.ico` into the build (`<ApplicationIcon>` + `Resource` + `Window Icon`), because the app icon previously came only from a Desktop/taskbar shortcut's "icon location" and the exe itself had no icon resources. `tools/check_files.py` passes; WPF Release build + self-contained publish verified locally from WSL with the Windows .NET 10 SDK: 0 errors, 3 pre-existing warnings; PE resource dump confirms the new exe contains RT_ICON/RT_GROUP_ICON. Needs the user's hand test after CI build. Files changed: MainWindow.xaml.cs, MainWindow.xaml, Canvaas.csproj, canvaas-icon.ico (new), CHANGELOG.md, CONTEXT.md.
- **2026-11-11 (v0.3.0):** corner hover-close overlay in fullscreen (CornerCloseCatcher/CornerCloseFlyout; hooking existing Minimize/Maximize/Close handlers). Visual pass: colour swatch buttons (QuickColorSquare, PopupSwatchButton) rebuilt as glossy ellipse templates; TopToolRadio rounded chip with active blue tint; TopIconButton radius 10. A pan-ghost experiment (RenderTargetBitmap snapshot while panning) was implemented and then fully reverted at the user's request — hand pan is back to the original behaviour. Files changed: MainWindow.xaml, MainWindow.xaml.cs, Canvaas.csproj, CHANGELOG.md, CONTEXT.md.
- **2026-11-11 (v0.2.0, built locally on the user's Windows machine):** startup now enters the user's preferred mode (fullscreen + toolbars visible + taskbar covered) automatically; per-tool colour memory implemented for pen/highlighter/calligraphy (each tool keeps its own colour, switch restores it); desktop snip feature fully removed (XAML button, F9 shortcut entry, dispatcher line, StartDesktopSnip and its Win32 helper block; Step 3 comment retitled); ScreenDraw toolbar icon changed from the emoji pen to a writing-hand glyph so it no longer looks like the pen tool; tools/check_files.py scans partial files (MainWindow.xaml.cs + partials) fixing false MISSING HANDLER reports. Local Release build succeeded (0 errors). Known pending: user's saved shortcuts have F bound to three actions (fullscreen, toggle_chrome, eraser) — needs user-approved rebinding.
- **Phase 1 (v0.1.0): CONFIRMED working in practice** via the user's ongoing use (80+ commits since, v1/v2/75-81 iterations, autosave, export, habit tracker added since this file's last full update — a full CONTEXT.md reconciliation pass is scheduled).
- **Phase 0: DONE and CONFIRMED.**

- **Phase 0: DONE and CONFIRMED.** GitHub Actions run #1 was green (52 s) and the user downloaded and opened `Canvaas.exe` (window showed "Canvaas ... Version 0.0.1"). Repo was recreated fresh and is public.
- **Phase 1 (v0.1.0): CONFIRMED working in practice.** (History: it was first delivered without a Windows build environment; it is now in daily use.) Phase 1 features in the code: white writing page (InkCanvas); Pen and Eraser (erase whole stroke) tools; mouse works as fallback; Undo/Redo (Ctrl+Z / Ctrl+Y); Clear page with confirmation (undoable); New (Ctrl+N), Open (Ctrl+O), Save (Ctrl+S), Save As; `.canvaas` file format v1 (see `docs/FILE_FORMAT.md`); safe save via temp file; friendly error messages on open/save failure; unsaved-changes prompt on New/Open/close; status bar shows pressure info for the last stroke (to test the Deco tablet).
- Built and extended since (by the user's later milestones, v1/v2/75–81): notebooks with many pages, infinite canvas, pen colour/thickness, point eraser, autosave + crash recovery, a settings file, PNG/PDF export, a habit tracker, screen draw and keyword auto-paste overlays. A full CONTEXT.md reconciliation pass with exact file inventories is scheduled.

### History of the first (failed) attempt — lessons
1. `CS0103: 'InkCanvasEditingMode' does not exist` — missing `using System.Windows.Controls;`.
2. `MC3000: Data at the root level is invalid` in `MainWindow.xaml` — non-XML text had been pasted into a XAML file via the GitHub website editor.
Phase 1 build failure #1 (2026-10-09): missing `using System.IO;` (see section 10). Fixed in the Phase 1 fix1 patch.
Lessons: never paste C# into `.xaml` or XML into `.cs`; validate XAML as XML; prefer ZIPs; always check CI after push.

---

## 6. Repository layout (UPDATE THIS when files change)

```
canvaas/
├─ CONTEXT.md                     This file. Keep current.
├─ README.md                      Short intro + how to download the app.
├─ CHANGELOG.md                   Version history, newest first.
├─ docs/
│  └─ FILE_FORMAT.md              Documented .canvaas save format (versioned). Update when the format changes.
├─ .gitignore                     Ignores bin/, obj/, publish/, .vs/ etc.
├─ .github/workflows/
│  └─ windows-build.yml           CI: build + publish single .exe + upload artifact "Canvaas-windows-x64".
├─ src/Canvaas/
│  ├─ Canvaas.csproj              WPF project, net10.0-windows, assembly name Canvaas, version 0.1.0.
│  ├─ canvaas-icon.ico            App icon; embedded in the exe and used by the window/taskbar.
│  ├─ App.xaml / App.xaml.cs      Application entry; StartupUri = MainWindow.xaml.
│  └─ MainWindow.xaml / .xaml.cs  Main window: toolbar, InkCanvas (InkArea), status bar. Holds tools, undo/redo history (stack of StrokeChange), save/open (ZIP: manifest.json + ink.isf), dirty tracking.
├─ .opencode/
│  └─ skills/sketch-to-ui/SKILL.md  Project skill for the opencode AI assistant: the agreed sketch-to-UI workflow (sketch/screenshot → questions → edit the project's files).
└─ tools/
   └─ check_files.py              Static checks for AI assistants (XML validity, event handlers exist, class names match).
```

Possible leftovers from the old attempt (safe to ignore or delete with the user's OK): `Canvaas.sln` or a `Canvaas/` wrapper folder. The workflow does not use them.

---

## 7. Roadmap (build one phase at a time; confirm green CI before moving on)

**Phase 0 — Verified foundation** *(DONE, confirmed 2026-10-09)*.

**Phase 1 — Usable handwriting prototype** *(DONE — confirmed in daily use)*: blank white canvas; pen input + mouse fallback; pen and stroke-eraser modes; undo/redo; clear page with confirmation; save/reopen in a documented local format; clear file-error messages.

**Phase 2 — Fixed notebook pages:** create/open notebooks; add/rename/reorder/delete pages safely; navigation; blank/ruled/grid/dot-grid templates; autosave + crash recovery; stable file format with version number and migration plan.

**Phase 3 — Infinite canvas:** smooth pan/zoom; large canvas without losing stroke accuracy; pen-first navigation; memory-efficient for big notes; clear page/infinite mode switch.

**Phase 4 — Study & customization:** configurable pen colour/thickness/presets; pressure sensitivity if supported; eraser modes; toolbar customization; keyboard shortcuts; equation/diagram/revision tools; PNG/PDF export; backup/restore; persistent settings.

**Phase 5 — Reliability & release:** clean-Windows test; easy installer; document requirements; GitHub Releases with version numbers + changelog; backup guidance before upgrades; confirm it runs without a dev environment.

Optional later: cloud-folder backup/sync (only after local storage is stable).

---

## 8. Engineering rules / quality gates for every change

1. List every changed file (a manifest) and use correct repo-relative paths.
2. Check every XAML event handler exists in C# (run `python3 tools/check_files.py`).
3. Validate XAML as XML.
4. Build in the same environment as CI (Windows + .NET 10) whenever possible; if impossible, say so.
5. Fix compile errors before asking the user to commit.
6. After the user pushes, confirm GitHub Actions is green before claiming success.
7. Confirm the artifact exists before saying it is downloadable.
8. Small milestones; one at a time; wait for the user.
9. Never silently remove or migrate user data. Any file-format change needs a version number, a migration, and a backup of the old file.
10. Keep code simple and commented in plain English; prefer fewer files; avoid clever tricks the user cannot maintain.
11. Update `CONTEXT.md`, `CHANGELOG.md`, and the `<Version>` in `Canvaas.csproj` with each milestone.

---

## 9. Build & CI details

Workflow `.github/workflows/windows-build.yml` runs on push to `main`, on pull requests, and manually (**Actions → Windows build → Run workflow**). Steps: checkout → install .NET 10 SDK → `dotnet --info` → `dotnet build src/Canvaas/Canvaas.csproj -c Release` → `dotnet publish ... -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish` → verify `publish/Canvaas.exe` exists → upload artifact **Canvaas-windows-x64**.

Where the user finds the app: GitHub → **Actions** → newest green run → **Artifacts** → `Canvaas-windows-x64` (a ZIP containing `Canvaas.exe`).

If a build fails: ask the user for a screenshot of the failing step (the red ✗ step, expanded), or the text of the lines starting with `error`. Diagnose from that.

---

## 10. Coding conventions

- C# with `Nullable` and `ImplicitUsings` enabled. **In WPF projects the implicit usings do NOT include `System.IO` or `System.Net.Http`** (this caused the first Phase 1 build failure: CS0246/CS0103 for File, FileStream, MemoryStream, FileMode, InvalidDataException) - add `using System.IO;` explicitly. WPF also needs explicit `using System.Windows;` (and `System.Windows.Controls`, `System.Windows.Ink`, `System.Windows.Input` when used).
- Root namespace `Canvaas`. One class per file; XAML code-behind named `<Name>.xaml.cs`.
- Keep UI logic in code-behind for early phases; when notebooks/settings arrive, put data in separate plain classes (model) so it can be tested and saved.
- Save format: see `docs/FILE_FORMAT.md` (implemented in Phase 1).
- Planned settings location: `%APPDATA%\Canvaas\settings.json`. Planned autosave/recovery: `%APPDATA%\Canvaas\recovery\`.

---

## 11. Decision log & open questions (append, newest last)

- 2026-10-09 — Restarted from scratch rather than reuse the failed scaffold. WPF + .NET 10 chosen (see section 4).
- 2026-10-09 — No `.sln` in Phase 0; CI builds the `.csproj` directly.
- 2026-10-09 — Phase 0 confirmed working. Repo recreated fresh (old one deleted by user).
- 2026-10-09 — Save format v1: ZIP with `manifest.json` + optional `ink.isf` (ISF = Microsoft's built-in ink format). Chosen because it is simple, inspectable, versionable, and extendable to multi-page in Phase 2.
- 2026-10-09 — Undo/redo built by hand (InkCanvas has none): StrokesChanged records added/removed strokes; a flag stops undo/redo from recording itself.
- 2026-11-10 — Added project-local opencode skill `sketch-to-ui` (`.opencode/skills/sketch-to-ui/SKILL.md`). Defines the agreed workflow for AI sessions: read CONTEXT.md first, accept sketches/photos/screenshots, ask-first (no guessing), edit the project's files directly, verify honestly (local build if possible, else CI), and keep CONTEXT.md/CHANGELOG/Version updated. No product code changed; version unchanged.
- 2026-11-11 — v0.3.0: pan-ghost was tried (RenderTargetBitmap snapshot while panning, no vanishing) but the user asked to revert hand pan to the original behaviour; reverted before release. Corner hover-close added for fullscreen. Glossy circle swatches replace square swatches. 3D colourful toolbar icons and a full dialogue redesign are acknowledged as the NEXT visual milestone (user to sketch icon ideas first). Lag on very large pages remains an OPEN topic; revisit with smarter approaches (e.g. caching layers) only with the user's approval.
- 2026-11-11 — v0.2.0 milestone built and verified locally on the user's Windows machine (WSL + local .NET 10 SDK): startup goes straight to fullscreen-with-visible-toolbars; per-tool colour memory for pen/highlighter/calligraphy; desktop snip feature removed completely; ScreenDraw toolbar icon changed; check_files.py now scans partial-class files. Changed files: src/Canvaas/MainWindow.xaml, src/Canvaas/MainWindow.xaml.cs, src/Canvaas/Canvaas.csproj, tools/check_files.py, CHANGELOG.md, CONTEXT.md.
- 2026-11-14 — v0.3.1: fixed only Quote 1 surviving a restart. Root cause was the programmatic fill of the four quote boxes triggering the TextChanged save path while boxes 2–4 were still empty. Guard flag + padded quote array chosen (simplest fix, no file-format change; settings.json shape unchanged).
- OPEN — Does the user's Deco tablet deliver pressure through Windows Ink in WPF? Test in Phase 1/4 and record here.
- OPEN — Infinite canvas approach (see section 4 risk). Prototype before committing.
- OPEN — Whether to keep the repo public; installer choice; code signing (probably not, to stay free).

---

## 12. How to start a NEW AI session (updated 2026-11-11)

The user now works with **opencode**, running on this machine (WSL on Windows). No ZIPs, no find-and-replace, no manual patching.

**The user's one-liner for any new session (in opencode, opened inside the repo):**

> Read `CONTEXT.md` fully first, then check `CHANGELOG.md` (newest entry = current state), `git log --oneline -10`, and follow all rules in this file. The project skill `sketch-to-ui` is loaded automatically. Here is my request: ______

**Rules for continuity (non-negotiable):**
1. CONTEXT.md + CHANGELOG.md + the `<Version>` in Canvaas.csproj are updated **every milestone** — they are the project's memory across sessions; a session that forgets to update them is breaking the memory chain.
2. The user does not code and does not use terminals. The AI runs builds itself (local .NET works; WSL (`dotnet build src/Canvaas/Canvaas.csproj`) is verified working) and never fakes success.
3. One milestone at a time; wait for the user's hand-test result before calling it done.
4. The user commits via **GitHub Desktop** (never the AI).
5. If CONTEXT.md seems stale relative to the code, say so and schedule a reconciliation pass before continuing.

### 12b. Legacy (pre-opencode) prompt — kept for compatibility

> I am a non-programmer building **Canvaas**, a free, local-first Windows handwriting/study-notes app (WPF, .NET 10) for a pen tablet. The repo is https://github.com/ujjwalsharma260-alt/canvaas. Please read `CONTEXT.md` in the repo (I will paste it or attach it) fully before doing anything. Follow its rules: simple English, one milestone at a time, click-by-click steps, complete ZIPs with files at the repo root, no terminal use by me, never claim a build passed without proof, and include an updated `CONTEXT.md` and `CHANGELOG.md` in every ZIP. My request now is: ______
