# Changelog

## v0.5.2

### Fixed
- **Preset colour palettes actually removed** - the fixed swatch rows in Settings are gone. Canvas colours and quick-palette colours are now plain lists you own: click a colour to use it, right-click a colour to delete it, and "+ Add colour…" opens the RGB picker to add a new one. Any number of colours is allowed (one minimum).
- **Quick colour square follows your palette** - the floating square on the canvas now shows exactly the colours in your palette list (any count), instead of four hard-coded ones. Right-clicking a square still stores the current pen colour in that slot.
- **Theme switching no longer overwrites your palette** - switching between light and dark pages used to replace your quick colours with built-in sets. It no longer touches them.
- **Version label** - the library sidebar now shows the running version (e.g. `v0.5.2`) so it is obvious which build is open.

### Changed
- Removed the duplicate "Background colour" swatch row; "Canvas colour" is the single place to manage page background colours.
- `Reset all settings` restores a single white canvas colour and the four default palette colours.

## v0.5.1

### Fixed
- **Menu flyout (☰) from library**: Added AppMenuButton in the library header; clicking it or the ☰ now opens Settings. Improved PositionMainMenuFlyout to handle library mode (fixed-position menu).
- **New notebook from library**: Creating a new notebook no longer automatically opens the canvas; the library view stays active and the new notebook appears in the selected shelf.
- **Auto-save consistency**: Auto-save now occurs when opening Settings (OpenSettingsPanel), returning to library (BackToLibrary_Click), and on window close (ConfirmDiscardChanges / SaveSettings).
- **Shelf scrolling**: Added VerticalScrollBarVisibility="Auto" to the library notebook grid; shelves now support scrolling when there are many notebooks.

### Changed
- Library header now shows: AppMenuButton (☰), + New notebook, Background colour button, Settings (⚙), and "My notebooks" title.
- LibraryNew_Click no longer navigates to the canvas; it refreshes the library view.
- OpenSettingsPanel auto-saves the current notebook before showing the overlay.
- Window_Closing keep the existing save-prompt behaviour; BackToLibrary_Click and OpenSettingsPanel now auto-save silently.

## v0.5.0

### Added
- **RGB colour picker** - "Pick colour (RGB)…" buttons in Settings for canvas background and quick palette colour, with live RGB sliders and hex input.
- **Shelves (categories)** - Left sidebar in the library: create, rename, delete shelves (e.g. Chemistry, Biology).
- **Notebook thumbnails** - Cards show a preview of the first page; refreshed when you press Back to library.
- **PDF badge** - Notebook cards show "PDF" if the notebook contains imported PDF pages.
- **Library background colour** - Colour button in the library header changes the bookshelf background (persisted).

## v0.4.0

### Added
- **In-app notebook library ("bookshelf")** - Canvaas now opens to a library of your notebooks. Create, open, rename (right-click) and delete notebooks without touching files. Notebooks are stored inside the app's folder (%APPDATA%\\Canvaas\\Library).
- **Auto-save on return** - The "Back to my notebooks" button saves the current notebook and returns to the library.
- **New notebook button** in the library creates and opens a fresh notebook.

### Changed
- Version bumped to 0.4.0.

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