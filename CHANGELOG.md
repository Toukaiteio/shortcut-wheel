# Changelog

All notable changes to ShortcutWheel are documented here.

## [0.3.1] - 2026-06-24

### Added
- "Run as administrator" per-shortcut option: toggle in the property panel to
  launch a specific shortcut with elevated privileges (uses the `runas` verb).
- "Close wheel after launching a shortcut" setting: when enabled, the wheel
  hides immediately after launching; when disabled, the wheel stays open so
  you can keep interacting.

### Changed
- Shortcut launch moved to a background thread (`LaunchAsync`) so the wheel
  closes instantly and shell resolution never blocks the UI render thread.
- File drag-and-drop from Explorer onto the config tree now correctly adds
  items under the folder node they were dropped on, instead of always adding
  to root.

## [0.3.0] - 2026-05-22

### Added
- Background image support: set any image as the wheel backdrop with adjustable
  opacity (default 0.4). Image is displayed as a perfect circle matching the
  wheel radius, with the same expand animation as the wheel itself.
- Background image also applies to the config window for a unified look.
- Settings page redesigned as a two-column responsive layout — no more large
  empty area on the right side.
- Update section in settings: current version display, manual "Check for
  updates" button with inline status, auto-update toggle, and silent update
  toggle (auto-download and install without any prompt).
- About card in settings with GitHub link.
- Fullscreen app detection: auto-update popup is deferred until the fullscreen
  app exits (polls every 5 min, gives up after 2 h).
- Wheel Opacity setting now actually applied to the OverlayWindow.

### Fixed
- .lnk shortcut import and launch no longer freezes/crashes the app. Replaced
  IShellLinkW COM calls (which block on slow/network targets) with WScript.Shell
  via reflection running on a background thread with a 3-second timeout.
- Startup migration: all existing .lnk TargetPaths in the config are
  automatically resolved to real exe paths in the background on first launch.
- Folder arrow indicator (›) was rendered at the wheel rim in random positions;
  now drawn inline to the right of the label text.
- Icon quality improved from 16×16 (SHGFI_SMALLICON) to 256×256 (SHIL_JUMBO
  via SHGetImageList), matching desktop icon sharpness.
- Items in the top wedge were rendered too high (outside the disc) due to a
  fixed upward offset on the icon; icon and label are now anchored together
  from a single radial point with ClampInsideWheel applied.
- Wedge label text now has a 1px dark shadow for readability over background
  images.
- Wheel and config window cards are semi-transparent when a background image
  is set, so the image shows through both surfaces consistently.
- OverlayWindow is pre-warmed at startup (invisible Show/Hide) so the
  background image renders correctly on the very first wheel open.
- Background image clip changed from EllipseGeometry/OpacityMask (both
  produced ellipses due to layout timing) to Ellipse+ImageBrush+ScaleTransform,
  which is always a perfect circle regardless of layout state.
- Deleting a shortcut then immediately clicking delete again did nothing because
  _selectedItem was overwritten after SelectedItemChanged fired during removal.
- Dragging a file onto the wheel from Explorer closed the wheel immediately
  (Window_Deactivated fired during drag); fixed with _externalDragActive guard.
- Config window TreeView now bound via ItemsSource to ObservableCollection,
  eliminating the parallel ShortcutTree.Items bookkeeping that caused
  duplicate-item and disappearing-item bugs on drag-and-drop.

## [0.2.3] - 2026-05-22

### Fixed
- Drag-and-drop a shortcut into a folder then drag it back out caused it to
  disappear. Root cause: WPF TreeView's ItemContainerGenerator cannot
  re-parent the same VM instance across hierarchy levels when bound via
  ItemsSource. PerformMove now always creates a fresh VM around the same
  underlying model for the destination, so the TreeView sees a clean
  insert/remove pair with no stale container references.
- Selecting a folder in the config tree made it impossible to deselect it,
  so "+ Add Folder / + Add File" always added inside the selected folder.
  Clicking on empty space in the tree now clears the selection and resets
  the property panel.
- Drag-drop duplicate-item bug: IsDescendant and self-drop guard now compare
  by model reference instead of VM reference, which is correct after the
  fresh-VM-per-move change above.

## [0.2.2] - 2026-05-22

### Added
- Auto-update from GitHub Releases. Checks 8 s after startup; also available
  manually from the tray menu (Check for updates / 检查更新). The update dialog
  shows current vs. latest version, the full release notes, download progress,
  and a one-click install that swaps the exe via a temp helper script and
  relaunches.

### Fixed
- `.lnk` import froze the UI thread because `ShortcutResolver.Resolve` was
  called synchronously from drag/drop and the file dialog. `LaunchService`
  already resolves at launch time, so imports now store the .lnk path directly.
- `IconExtractor` ran shell COM calls on a managed Task pool thread; could
  dead-lock with the WPF render thread via Dispatcher.Invoke. All shell icon
  work now happens on a dedicated long-lived STA worker thread.
- Deleting a shortcut collapsed every expanded folder (full TreeView rebuild).
  Now removes only the affected node so other folders stay expanded.
- Drag-drop into a folder created a duplicate ViewModel that shared the same
  model, causing "two items appear after rename, deleting one removes both".
  `PerformMove` no longer goes through `AddChild`; ConfigService also
  deduplicates by Id when loading to repair previously-corrupted configs.

## [0.2.1] - 2026-05-22

### Fixed
- Hotkey config (modifiers, key, mouse button) was never persisted because the modifier checkboxes and key/button ComboBoxes had no event handlers
- `LoadSettings` ignored the saved hotkey key and always reset it to `Space`
- Saving the hotkey config did not re-register the keyboard or mouse hook, so changes only applied on next app start
- `ReloadConfig` only re-registered the keyboard hotkey, never the mouse side-button hook
- ConfigWindow ComboBox text was unreadable on the dark theme — replaced default ComboBox template with a fully themed dark/gold one (selected item, dropdown items, hover & selection states all themed)

### Changed
- Mouse side button release no longer always dismisses the wheel. CSGO-style behaviour: releasing on a wedge launches it; releasing on the centre navigates back; releasing on empty space keeps the wheel open so the user can continue with the mouse

## [0.2.0] - 2026-05-22

### Fixed
- Sub-menu hover highlight not shown when mouse is stationary after entering a sub-menu
- Single-item sub-menus occupied the full 360° disc; items now occupy at most 90° (minimum 4 wedge slots)
- Wedge labels rendered outside the wheel disc on large-radius wheels
- Right-click on empty wheel area closed the window instead of navigating back one level

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
