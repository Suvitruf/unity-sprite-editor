# Better Sprite Editor

> Developed while working on [Pocket Cat Desktop](https://store.steampowered.com/app/4865920/Pocket_Cat_Desktop/?utm_source=github&utm_medium=web&utm_campaign=better_sprite_editor&utm_content=suvitruf), this tool grew out of the sprite-editing needs of that project.

A standalone Unity editor window for sprite metadata workflows: multi-select, batch renaming, region combining and splitting, browsable undo history, usage search, and tunable outline and physics-shape generation. Built on Unity's public Sprite Editor Data Provider API.

![Better Sprite Editor](https://projects.cdn.aapanasik.com/better-sprite-editor/better-sprite-editor.png)

## Requirements

- Unity 2023.1 or newer (compatibility target; see validation status below).
- Unity's 2D Sprite package (`com.unity.2d.sprite`), declared as a package dependency.

### Compatibility validation

The package targets Unity 2023.1 and newer. Automated compatibility checks pass on Unity 2022.3.61f1 (38 tests), 2023.1.22f1 (38 tests), 6000.0.71f1 (38 tests), 6000.1.4f1 (46 tests), and 6000.3.11f1 (46 tests). Inspection of the Sprite package bundled with 2023.1.22f1 confirms that it exposes neither `SpriteRect.customData` nor the frame edit capability API, so the older API branch applies to 2023.1. Unity 2023.2 is not installed in the validation environment, so direct tests on that editor remain pending; manual viewport checks have been done only on Unity 6000.3. The 2022 run exercises the older API branch; it does not extend the declared support range.

## Installation

### OpenUPM (recommended)

[![openupm](https://img.shields.io/npm/v/com.suvitruf.better-sprite-editor?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.suvitruf.better-sprite-editor/)

Via the [openupm-cli](https://openupm.com/):

```
openupm add com.suvitruf.better-sprite-editor
```

Or add the scoped registry manually — **Edit → Project Settings → Package Manager → Scoped
Registries**, add:

- **Name**: `package.openupm.com`
- **URL**: `https://package.openupm.com`
- **Scope(s)**: `com.suvitruf`

then install **Better Sprite Editor** from **Window → Package Manager → My Registries**.

### Git URL

Alternatively, in the Package Manager choose **Add package from git URL** and paste:


```text
https://github.com/Suvitruf/unity-sprite-editor.git
```

For a local checkout, keep this folder outside your project's `Assets` directory, choose **Install package from disk**, and select `package.json`. Install only one copy of the package in a project.

## Quick start

1. Select a texture imported as **Sprite (2D and UI)**. Use **Multiple** sprite mode for slicing and region editing.
2. Open **Window > 2D > Better Sprite Editor**, or right-click the texture and choose **Open in Better Sprite Editor**.
3. Select or create regions, then adjust their properties in the inspector. Use Slice to preview a grid, automatic, or isometric layout before committing it to the working document.
4. Click **Apply** (`Ctrl+S`) to save and reimport, or **Revert** to discard unapplied edits.

## Features

### Editing

- **Select and Create tools** (`M` / `U`) with move, resize, arrow-key nudge, and edge/corner handles that have proper grab buffers and resize cursors.
- **Multi-select everywhere** — Ctrl+click toggles, Shift+click adds, marquee selects; move, nudge or delete whole groups. Group moves clamp so every region stays in bounds with relative offsets intact.
- **Unity-style multi-edit inspector** — shared values show normally, differing values show the mixed dash `—`; committing a value writes it to every selected region as one undo step.
- **Interactive 9-slice borders** — drag the mid-edge points; border lines stay visible on every bordered region, selected or not.
- **Draggable pivots** — a blue ring on every selected region. Grabbing a preset pivot (e.g. BottomCenter) converts it to Custom *in place*, so it never jumps. Pivot values can be shown in normalized units or pixels.
- **Keep Pivot Position** — an optional toggle under Rect keeps pivots anchored to the artwork when moving or resizing regions, including numeric multi-edit and nudging. Presets become Custom only when needed; undo restores both the rect and pivot.
- **Relative pivot offsets** — enter X/Y in pixels and click **Shift Pivots** to move every selected pivot from its current position, preserving differences across frame sizes and alignments. Each click is one undo step.
- **Pivot alignment** — align pivots across selected regions.
- **Live inspector** — values update while you drag, not on mouse release.

### Region operations

- **Trim and uniform frame bounds** — the Bounds panel previews transparency trimming (alpha threshold and padding) or common frame sizes around existing pivots. Preserves sprite IDs, outline/collider positions, and 9-slice lines; reports conflicts before applying the batch as one undo step.
- **Combine** (`Ctrl+E`) — merge selected regions into one covering their union rect, via a dialog for the new name and pivot. One undo restores the originals.
- **Split** — divide a region into a grid (by cell count or size), skipping empty cells, with children named `<base name>0, 1, 2…` and validated against name collisions.
- **Batch rename** (`F2`) — composable rules in the style of PowerRename and Blender: base name with `{name}`/`{n}` tokens, find & replace (plain or regex with capture groups), strip, prefix/suffix, case change, numbering with start/step/padding. Counter order is selectable: row-major, column-major, selection order, or existing name — so `walk_0…walk_7` lines up with the strip. Live old → new preview with conflicts blocked before they happen.
- **Renaming preserves sprite identity** — sprite GUIDs are preserved and name↔fileId pairs rewritten, so scene and prefab references survive.

### History

- **Undo/redo** (`Ctrl+Z` / `Ctrl+Y`) for sprite edits, independent of the editor-wide undo queue.
- **Photoshop-style history panel** — every action listed by name; click any row to jump straight to that state. History survives Apply.
- Unsaved changes are guarded on window close and texture switching. Opening the same texture keeps its working document.
- Recovery preserves document, selection, and the latest 100 undo/100 redo entries. Incompatible or corrupt snapshots are retained for inspection.
- Region list order is saved with Apply in per-user `UserSettings/BetterSpriteEditor/` files; it does not change Unity's importer or picker order.

### Slicing

- **Four methods**: automatic (alpha connected-components), grid by cell size, grid by cell count, isometric (staggered diamond grid with a diamond-shaped emptiness test).
- **Non-destructive preview** — orange ghost rects update live as you tune parameters; nothing is written until you commit, and the whole slice is one undo step. Grid-by-count creates exactly the requested count when it fits; indivisible remainder pixels stay at the right/bottom edges.
- Skip-empty-cells, offset/padding, pivot presets, and a keep-existing policy that protects hand-edited regions.

### Custom Outline & Physics Shape modes

Switch between Sprite Editor, Custom Outline, and Custom Physics Shape modes. Drag points, click edges to insert points, marquee-select points to move or delete them together, and use Ctrl+drag to add paths.

The **shape generator** includes:

- Contours trace **pixel edges**, preserving the pixels included by the alpha threshold as you simplify the shape.
- **Detail** slider adjusts the shape from a coarse enclosing polygon to a detailed contour along the pixel edges.
- **Outset** — adjust the distance from the art. Narrow gaps close before contours are traced, avoiding crossing edges from expanding those gaps.
- **Hole detection** — rings and frames get real cut-outs with correct winding.
- **Alpha tolerance** (per-mode defaults matching stock: 0 outline / 200 physics), minimum blob size, snap to pixels, Box and Convex Hull presets.
- **Shared Shape** — generate one shape from several animation frames combined, aligned by pivot, using exact pivots. The shared geometry must fit every selected frame; incompatible bounds are reported before committing.
- **Point target** — request lower collider complexity. Preview reports when preserving the geometry requires more points.
- **Live preview** while adjusting settings, with Generate / Generate All / Clear.

### Find usages

- Search **loaded scenes** (component + property) or the **whole project** (prefabs, scenes, animations, ScriptableObjects, atlases) for uses of selected sprites — per *region*, not per texture, via fileID matching.
- Results grouped by object or by sprite, with thumbnails, Ping / Open / Select on every row.
- **No-reference results** are listed separately and outlined orange in the viewport. The list tooltip identifies the scan scope; cancelled, skipped, or unresolved results remain unknown. Dynamic loading and package assets are outside the scan.

### Getting sprites out

- **Drag the inspector thumbnail** — or **Alt+drag a region** — straight into the Scene, Hierarchy, or any `Sprite` field. Multi-select drags all selected sprites (Unity will offer to create an animation).

### Fully customizable input

Every shortcut **and mouse gesture** (including pan: RMB / MMB / Space+LMB by default) is rebindable at **Preferences → Better Sprite Editor**, with conflict detection and per-binding reset. Defaults apply until you override; overrides live in `UserSettings/` per project.

### Viewport

Zoom-to-cursor, three pan gestures, pixel grid at high zoom, alpha view, checkerboard background, and crisp point-filtered rendering at any zoom.

## Default shortcuts

| Action | Default |
|---|---|
| Select / Create | `M` / `U` |
| Undo / Redo | `Ctrl+Z` / `Ctrl+Y` |
| Apply | `Ctrl+S` |
| Delete selection | `Delete` |
| Combine / Rename | `Ctrl+E` / `F2` |
| Find usages | `Ctrl+F` |
| Slice panel | `Shift+S` |
| Fit / 1:1 / Alpha view | `F` / `1` / `A` |
| Pan | Right mouse, middle mouse, or `Space` + left mouse drag |
| Drag sprite out | `Alt` + left mouse drag |

Bindings can be changed in **Preferences > Better Sprite Editor**.

## Scope and limitations

This is an early release focused on single-texture editing. Single-mode textures support pivot, border, and available shape edits; region creation, deletion, renaming, and rect editing require Multiple mode. Custom importers must expose the required public providers and capabilities.

Existing sprite IDs are retained during supported edits. Deleting or replacing regions can invalidate references. Apply detects external image or import-setting changes and refuses to overwrite conflicting edits.

Usage searches do not prove that a sprite is unused: dynamic loading and package assets are outside their scope, and skipped or unresolved results remain unknown. Shared shapes must fit every selected frame. Point Target is a simplification request, not a guaranteed maximum.

Presentation order is saved per user under `UserSettings/BetterSpriteEditor/`; it does not change Unity's sprite picker order. Secondary-texture editing, multi-texture batch operations, and animation preview/export are not implemented.

See the [user guide](Documentation~/index.md) for saving, recovery, and detailed limitations, and the [changelog](CHANGELOG.md) for changes.

## Development

Source is in `Editor/`, and regression tests are in `Tests/`. See [Contributing](CONTRIBUTING.md) for setup, tests, and release checks.

## Support & community

**Join on [Discord](https://discord.gg/3aDtVmNRMY)** — chat about the project, follow progress, ask questions, share ideas, or help test.

**Support development** — this is a free, open-source labour of love built in spare time. If you'd like to help it move along faster, you can support me on [Patreon](https://www.patreon.com/apanasik).

## License

[MIT](LICENSE) — Copyright (c) 2026 Andrei Apanasik.
