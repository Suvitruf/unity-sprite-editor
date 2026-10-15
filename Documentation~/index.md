# Better Sprite Editor

An editor-only sprite metadata tool targeting Unity 2023.1 and newer, using the public 2D Sprite Data Provider API. It opens in its own UI Toolkit window.

## Features

- Multi-select of sprite regions with Unity-style multi-edit inspector.
- Create, move, resize, combine, split, rename, pivot, and border tools.
- Automatic, grid, and isometric slicing with preview before commit.
- Outline and physics-shape editing, generation, and shared shapes.
- Undo/redo history (Ctrl+Z / Ctrl+Y), including after Apply.
- Searchable region list and reference search in loaded scenes or project assets.
- Customizable hotkeys for every action and gesture.
- Drag applied sprites into the scene, hierarchy, or sprite fields.

## Installation

In Package Manager, choose **Install package from Git URL** and enter `https://github.com/Suvitruf/unity-sprite-editor.git`.

For a local UPM installation, place the package folder outside the project's Assets folder, then use Package Manager's **Install package from disk** action and select its `package.json`. The package depends on `com.unity.2d.sprite`. An Assets-based development copy can be used directly; do not install a second copy through Package Manager in the same project.

## Getting started

Open a texture in the editor via one of:

- `Window > 2D > Better Sprite Editor` (uses the currently selected texture),
- right-click a texture in the Project window → `Open in Better Sprite Editor`,
- the texture importer inspector's context menu (⋮) → `Open in Better Sprite Editor`.

## Saving and recovery

Apply (Ctrl+S) writes sprite metadata and reimports the texture. Revert reloads the asset and discards unapplied edits and history. Closing or switching textures offers Save/Discard/Cancel; opening the same texture keeps the current session. A failed save keeps the document open.

If the source image or importer metadata changes externally, a clean document reloads automatically. A dirty document is retained and Apply refuses to overwrite the external version. Use Revert when you are ready to discard local edits and reload. Asset moves are tracked by GUID.

Recovery snapshots are written before assembly reload and every 30 seconds while dirty, under `Library/BetterSpriteEditor/`. They include selection and up to 100 undo and 100 redo commands. Damaged, legacy, or conflicting snapshots are renamed with a `.retained-…` suffix; the Console reports their location for inspection. Recovery is not a substitute for source control or backups.

Dragging rows changes this editor's presentation order. Apply saves that order under `UserSettings/BetterSpriteEditor/`, per project and user. It does not reorder Unity's sprite picker or importer array. Keep the package's UserSettings data if you want to retain personal ordering.

## Supported operations and limits

- Single-mode textures support pivot, border, and available shape edits. Rect editing, renaming, adding, deleting, splitting, combining, and slicing require Multiple mode. Change import mode in Unity's texture inspector and reload before using those tools.
- Custom importers must expose the corresponding public providers and editing capabilities.
- Grid-by-count uses whole-pixel cells and exactly the requested count when it fits. Remainder pixels are left at the right and bottom edges; impossible grids generate no cells.
- Shared shapes retain fractional and outside-frame pivots. The resulting polygon must fit every selected frame without clipping; otherwise generation reports the incompatible frame and leaves existing shapes untouched. Use larger aligned frame bounds, reduce Outset, or disable Shared Shape.
- Point Target is a simplification request, not a hard cap: disconnected paths and preserved geometry can require more points. Preview reports the actual result.
- Project usage search covers supported text assets under Assets. “No references found” applies only to the selected scope. Cancelled scans, unreadable/binary assets, and unapplied new sprites have unknown status. Package assets and dynamic loading are outside this scan.
- Secondary-texture editing, batch operations across textures, and animation preview/export are not implemented. Sprite IDs are retained for existing sprites; deleting/replacing a region can invalidate references to that region.

## Version compatibility

The package targets Unity 2023.1 and newer. Automated compatibility checks pass on Unity 2022.3.61f1 (38 tests), 2023.1.22f1 (38 tests), 6000.0.71f1 (38 tests), 6000.1.4f1 (46 tests), and 6000.3.11f1 (46 tests). Inspection of the Sprite package bundled with 2023.1.22f1 confirms that it exposes neither `SpriteRect.customData` nor the frame edit capability API, so the older API branch applies to 2023.1. Unity 2023.2 is not installed in the validation environment, so direct tests on that editor remain pending; manual viewport checks have been done only on Unity 6000.3. The 2022 run exercises the older API branch; it does not extend the declared support range.

Standard TextureImporter sprites use a compatibility fallback on older editors, with the same Single/Multiple mode restrictions. Native importer capabilities are respected on Unity 6000.1 and newer; unknown custom importers without a capability provider remain restricted. Custom sprite metadata is read and written through the public API on Unity 6000.0 and newer. Older branches reject nonempty custom metadata carried in a document instead of silently discarding it.

## Tests

Install Unity Test Framework and run the package's EditMode tests in Test Runner. For UPM installs, add `"com.apanasik.better-sprite-editor"` to the host manifest's `testables` array. Importer tests create temporary assets and remove them afterwards. The tests also cover recovery, grid sizing, shared pivots, reference scans, and save conflict detection.
