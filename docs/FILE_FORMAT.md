# Canvaas note file format (.canvaas)

**Current format version: 1** (introduced in app version 0.1.0)

A `.canvaas` file is a normal **ZIP archive** with a different extension. You can rename a copy to `.zip` and look inside.

| File inside the ZIP | Required | Purpose |
|---|---|---|
| `manifest.json` | yes | Describes the file: format version, app version, save time. |
| `ink.isf` | no | All pen strokes in Microsoft's Ink Serialized Format (ISF). Missing = empty page. |

## manifest.json (version 1)
```json
{
  "formatVersion": 1,
  "app": "Canvaas",
  "appVersion": "0.1.0",
  "savedAtUtc": "2026-10-09T05:30:00.0000000Z"
}
```

## Rules
- `formatVersion` is a whole number. It goes up only when the layout changes in a way older apps cannot read.
- An app that sees a `formatVersion` higher than it knows must refuse to open the file and say so. It must never change or delete that file.
- Newer apps must be able to open all older versions (add a migration step that converts in memory; keep the user's original file untouched until they choose to save, and offer a backup copy before overwriting an older-format file).
- Saving writes `name.canvaas.tmp` first, then replaces `name.canvaas`, so a failed save never destroys the previous good copy.

## Planned changes (not built yet)
- Phase 2: notebooks with several pages (likely one `pages/NNN.isf` per page plus page list and template in `manifest.json`) -> format version 2.
- Phase 3: infinite canvas view settings.
