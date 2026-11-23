# Changelog

## v0.3.4

### Added
- **User-selectable canvas background colour** - 7 preset options (White, Paper Cream, Light Grey, Sage, Sky Blue, Navy, Dark Green) via the Settings overlay. Colours persist between sessions using `AppSettings.CanvasBackgroundColor`.

- **User-selectable quick color palette** - 4 preset colors (Black, Red, Blue, Green) plus "None" option via the Settings overlay. Colours persist between sessions using `AppSettings.QuickColorHex` array.

- **Canvas colour persistence** - Saved to and loaded from `%APPDATA%\Canvaas\settings.json` on every app startup.

### Added
- **Custom colour hex input** - Users can now type custom hex colour values (e.g. #FF0000) in the Settings overlay for both canvas background and quick colour palette, giving full control over colours beyond the preset options.
- **Canvas colour custom TextBox** - In Settings → Canvas colour, type any hex value to set custom canvas background colour.
- **Quick colour custom TextBox** - In Settings → Quick colour palette, type a hex value to set the first custom colour (index 0) in the palette.

### Changed
- Updated `AppSettings` class with `CanvasBackgroundColor` and `QuickColorHex` fields.
- Updated `LoadSettings()` and `SaveSettingsCore()` to persist new color settings.
- Updated `NewStyledPage()` to apply saved canvas background colour to new pages.
- Updated `CaptureStyle()` to save current canvas background colour.
- Updated `SyncSettingsUI()` to sync colour control states on startup.
- Updated `CanvasColorSwatch_Click` and `QuickColorSwatch_Click` event handlers to use preset colors only.
- Updated `ResetAllSettings_Click` to reset new colour settings to defaults.
- **Removed "Background colour" section from Settings overlay** - The canvas colour section now handles all canvas colour changes, simplifying the UI.
- XAML: Added "Canvas colour" section with 7 colour options in Settings overlay.

### Fixed
- Resolved build errors from incompatible `System.Windows.Forms` package version.
- Fixed XAML name references in `SyncSettingsUI()` to match actual XAML `x:Name` attributes.
- Removed `ColorDialog`/`ColorTranslator` custom color picker code that required the deleted package.
- Removed stale `Click="CanvasColorCustom_Click"` and `Click="QuickColorCustom_Click"` event handler references from XAML.

### Added
- **Add page before current page** - Users can now add a new page before the currently selected page via the context menu (Shift+P or Context Menu → "Add page before current page").
- **Merge notes feature** (planned) - Ability to combine pages from multiple .canvaas files into one notebook.

### Changed
- Added "Add page before current page" menu item in the Add Page context menu.

### Fixed
- Initial color palette implementation with preset options.