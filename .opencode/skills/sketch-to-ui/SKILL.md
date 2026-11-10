---
name: Sketch to UI
description: Use when the user shares a hand-drawn UI sketch (photo of paper or whiteboard), a digital drawing, or a screenshot of another app as a reference, and wants Canvaas's interface changed or extended to match. Converts drawings into XAML/C# changes, respecting CONTEXT.md and the phase roadmap.
---

# Sketch to UI (Canvaas, WPF)

The user is a non-programmer. Communicate in simple English, explain every technical term the first time. This skill implements the Ask-first workflow agreed with the user on 2026-11-10.

## Step 0 — Project law first

- Read `CONTEXT.md` fully before doing anything. It outranks this skill. Where they conflict, CONTEXT.md wins and the user should be told.
- Know the current phase from CONTEXT.md section 5. UI ideas beyond the current phase are recorded in the roadmap discussion, not built silently.
- Canvaas is a native Windows WPF app (C#, .NET 10). UI = `MainWindow.xaml` (layout) + `.xaml.cs` (behavior). There is no HTML/CSS to find — never invent web files.

## Step 1 — Locate and read the drawing

- Sources accepted: photo of a paper/whiteboard drawing, digital drawing (Paint, Excalidraw, Canva, Notes), screenshot of another app used as a visual reference.
- The user will type a path, or say "it's in my sketches folder". Convention: `sketches/` at the repo root (create it if missing, and add `sketches/` to `.gitignore` only if the user agrees sketches are not project files).
- Read every image with the read tool. If unreadable (blurry, too dark), ask for a re-photo. Never guess at unreadable content.
- Several sketches joined by arrows = one flow, interpret together.

## Step 2 — Interpret the sketch

Map drawings to WPF concepts:

- A window drawn as the whole sheet = the app window. A slim bar at its top = title bar; a strip with squares = toolbar buttons; a large empty region = the InkCanvas area; a strip at the bottom = status bar.
- Small labeled boxes = buttons/menu items. Handwritten words are the literal control labels, copied exactly (ask before fixing spelling).
- Colors: written names, hex codes, or actually colored drawing. WPF uses hex like `#3478F6` — convert color words to hex, confirm with the user when inexact.
- Arrows = behavior: "clicking X opens/changes Y", not navigation between windows (unless drawn as two separate windows).
- Circled elements = primary/emphasized. Underlined = links (rare in this app). Small crossed elements = something to remove.
- For screenshots of other apps: capture layout, control types, spacing, palette. Never copy logos or text content.
- Respect what CONTEXT.md says NOT implemented (notebooks, pen colors in Phase 1, etc.). A sketch asking for a not-yet-existing feature becomes a roadmap discussion, not silent code.

## Step 3 — Ask first (mandatory, no exceptions)

- Before writing code, list every uncertain element as short numbered questions in plain English (is it a button or a label? exact colors? what should the button DO?).
- Wait for answers. No guessing on element type, behavior, text, or colors.

## Step 4 — Implement (existing files only)

- Edit `MainWindow.xaml` / `.xaml.cs` (or the current target file per CONTEXT.md layout). One class per file, XAML code-behind naming kept.
- Run `python3 tools/check_files.py` after edits (validates XAML as XML; checks event handlers exist in C#).
- Mind the conventions in CONTEXT.md section 10: explicit `using System.IO;`, `System.Windows.Controls`, etc. Keep code simple, with comments in plain English. Prefer fewer files.
- Never store user data without honoring rule 9 in CONTEXT.md (format versions, migration, backups).
- Deliver as a milestone: list every changed file, update `CONTEXT.md` (sections 5/6/10/11 as applicable), `CHANGELOG.md`, and the `<Version>` in `Canvaas.csproj` if it is a real feature change.

## Step 5 — Build and preview (be honest about what is verified)

- First try the local route (fast): run via Windows toolchain from WSL, e.g. `cmd.exe /c dotnet build src/Canvaas/Canvaas.csproj` from the repo root. If dotnet is not found on the Windows side, say exactly that.
- If local build succeeds, run the app (`cmd.exe /c dotnet run --project src/Canvaas/Canvaas.csproj`) so the window opens on the user's screen for their eyes-on test.
- If no local SDK exists: the fallback is the CI route — user commits via GitHub Desktop and pushes; watch the Actions run; on green, the artifact `Canvaas-windows-x64` contains the exe. Never claim build success without a proven log.
- End every milestone with a list of changed files and what still needs the user's hand test.

## Step 6 — Iterate and stay consistent

- "Make the button bigger" style tweaks are edited directly; keep the established style; small iterations, one message at a time.
- New or edited sketch for an already-built screen: re-read the image, summarize what will change in one short list, ask, then edit.
- Keep the toolbar/status-bar structure described in CONTEXT.md section 6 stable unless the user explicitly asks for a redesign.

## First-use behavior

On the first load in a session, confirm the sketch conventions with the user in two or three lines: box = region/button, arrow = "this opens that", written colors = colors, handwritten text = literal labels, circle = emphasized. Suggest good lighting + straight-on photos saved into `sketches/`.
