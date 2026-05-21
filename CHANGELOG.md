# Changelog

All notable changes to ShortcutWheel are documented here.

## [0.1.0] - 2026-05-21

### Added
- CSGO-style radial wheel with dark wedges, gold trim, and number indicators
- Hotkey activation (`Ctrl+Alt+Space`) via message-only HWND (no taskbar window)
- Mouse side-button hold activation (XButton1, configurable hold delay)
- Drag-and-drop shortcuts from Explorer onto the wheel
- Auto-resolve `.lnk` shortcut files to real target path + arguments
- URI / protocol support: `steam://`, `https://`, `ms-settings:`, `spotify:`, etc.
- Folder / sub-menu navigation with back via Backspace / Shift / right-click
- Pagination: up to 6 items per page; scroll wheel or arrow keys to page
- Animated wheel open/close (ease-out cubic, ~250 ms)
- CSGO-style dark config window with drag-and-drop tree reordering
- Startup Toast notification (bottom-right, slide-in, bypasses Focus Assist)
- System tray icon with context menu (Configure, Reload, Help, Exit)
- Schema migration: auto-upgrades legacy configs to current defaults
- Single-file publish (framework-dependent, win-x64)

### Fixed
- `SHGetFileInfo` declared in wrong DLL (`user32` → `shell32`)
- Hidden `MainWindow` leaked a title bar in the desktop bottom-left corner
- Wheel rendering crash on first animation frame (negative `Rect` size)
- Config window data corruption: selecting a tree item blanked other items' `TargetPath`
- `IsFolder` incorrectly returned `false` for newly-created empty folders
- Wedge hit-test off by half a wedge (missing `wedgeAngle/2` rotation)
- Wheel did not auto-close after launching a shortcut
- `ESC` now always closes the wheel (previously navigated back)
