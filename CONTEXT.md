# CANVAAS — FULL PROJECT CONTEXT (READ THIS FIRST)

> **For any AI coding assistant:** this file is the single source of truth for the Canvaas project. Read all of it before changing anything. Keep it up to date: every ZIP/patch you deliver MUST include an updated copy of this file (sections 4, 5, 6, 10, 11 especially).
> **Last updated:** 2026-10-09 by Claude (Phase 1 handwriting prototype). Update this line every time.

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

- **Phase 0: DONE and CONFIRMED.** GitHub Actions run #1 was green (52 s) and the user downloaded and opened `Canvaas.exe` (window showed "Canvaas ... Version 0.0.1"). Repo was recreated fresh and is public.
- **Phase 1 (v0.1.0): code delivered, NOT YET CONFIRMED.** Written without access to Windows or a .NET SDK, so only static checks were done (`tools/check_files.py`: XAML valid, handlers exist, classes match). The user must push it and report the Actions result, then test the app by hand. Do not call Phase 1 working until both are confirmed.
- Phase 1 features in the code: white writing page (InkCanvas); Pen and Eraser (erase whole stroke) tools; mouse works as fallback; Undo/Redo (Ctrl+Z / Ctrl+Y); Clear page with confirmation (undoable); New (Ctrl+N), Open (Ctrl+O), Save (Ctrl+S), Save As; `.canvaas` file format v1 (see `docs/FILE_FORMAT.md`); safe save via temp file; friendly error messages on open/save failure; unsaved-changes prompt on New/Open/close; status bar shows pressure info for the last stroke (to test the Deco tablet).
- NOT implemented (do not assume they exist): notebooks/pages/templates, infinite canvas, pen colour/thickness settings, point eraser, autosave/crash recovery, settings file, export (PNG/PDF), backup, installer.

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
│  ├─ App.xaml / App.xaml.cs      Application entry; StartupUri = MainWindow.xaml.
│  └─ MainWindow.xaml / .xaml.cs  Main window: toolbar, InkCanvas (InkArea), status bar. Holds tools, undo/redo history (stack of StrokeChange), save/open (ZIP: manifest.json + ink.isf), dirty tracking.
└─ tools/
   └─ check_files.py              Static checks for AI assistants (XML validity, event handlers exist, class names match).
```

Possible leftovers from the old attempt (safe to ignore or delete with the user's OK): `Canvaas.sln` or a `Canvaas/` wrapper folder. The workflow does not use them.

---

## 7. Roadmap (build one phase at a time; confirm green CI before moving on)

**Phase 0 — Verified foundation** *(DONE, confirmed 2026-10-09)*.

**Phase 1 — Usable handwriting prototype** *(current: code delivered, awaiting build + hand test)*: blank white canvas; pen input + mouse fallback; pen and stroke-eraser modes; undo/redo; clear page with confirmation; save/reopen in a documented local format; clear file-error messages.

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
- OPEN — Does the user's Deco tablet deliver pressure through Windows Ink in WPF? Test in Phase 1/4 and record here.
- OPEN — Infinite canvas approach (see section 4 risk). Prototype before committing.
- OPEN — Whether to keep the repo public; installer choice; code signing (probably not, to stay free).

---

## 12. Copy-paste prompt for starting a new AI session

> I am a non-programmer building **Canvaas**, a free, local-first Windows handwriting/study-notes app (WPF, .NET 10) for a pen tablet. The repo is https://github.com/ujjwalsharma260-alt/canvaas. Please read `CONTEXT.md` in the repo (I will paste it or attach it) fully before doing anything. Follow its rules: simple English, one milestone at a time, click-by-click steps, complete ZIPs with files at the repo root, no terminal use by me, never claim a build passed without proof, and include an updated `CONTEXT.md` and `CHANGELOG.md` in every ZIP. My request now is: ______
