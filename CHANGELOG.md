# Changelog

All notable changes to this package are documented in this file. The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Selective property clipboard with pivot/border/outline/physics/tessellation transfer, coordinate mapping, cross-mode shape copying, per-target previews/exclusions, session persistence, and grouped undo/recovery.
- Frame Bounds panel with Trim Transparency and Uniform Frame Bounds, previews, alpha threshold/padding/custom size, pivot/shape/9-slice preservation, conflict reporting, and grouped undo/recovery.
- Relative pivot adjustments: Offset (px) and Shift Pivots add a pixel delta to every selected pivot, including mixed alignments and unequal frame sizes, as one undoable edit.
- Keep Pivot Position toggle in the region inspector: preserve each pivot's texture position during rectangle resizing, movement, nudging, and numeric multi-edit, with undo/redo and recovery support.

## [0.1.0] - 2026-10-08

Initial release.

### Added

- Standalone UI Toolkit editor window (`Window > 2D > Better Sprite Editor`) for sprite regions, pivots, borders, outlines, and physics shapes, built on Unity's public Sprite Editor Data Provider API.
- Multi-selection with group movement, nudging, and a mixed-value inspector.
- Region creation, resizing, combining, splitting, and batch renaming with live previews. Existing sprite IDs and name/fileId pairs are preserved during supported edits.
- Editable pivots and 9-slice borders, including pivot alignment across selected regions.
- Automatic, grid, and isometric slicing with non-destructive previews.
- Outline and physics-shape point editing and generation, with hole detection, outset, and shared shapes aligned by pivot.
- Browsable undo/redo history that survives Apply, plus session recovery of the document, selection, and history after assembly reloads.
- Unsaved edits are retained when switching textures or when Apply fails, and guarded on window close. External image or import-setting changes and asset moves are detected before saving.
- Import-mode and importer-capability checks: Single-mode textures allow pivot, border, and shape edits; creation, deletion, renaming, and rect edits require Multiple mode.
- Per-user presentation order for the region list, stored under `UserSettings` separately from importer data.
- Reference search in loaded scenes or supported project assets, reporting scan coverage, skipped files, and cancellation.
- Drag applied sprites into scenes, the hierarchy, or sprite fields.
- Configurable keyboard shortcuts and mouse gestures, zoom-to-cursor, pixel grid, and alpha view.
- EditMode regression tests covering importer round trips, recovery, slicing, shapes, and usage search.

### Compatibility

- Unity 2023.1 and newer. Native importer edit capabilities are honored on Unity 6000.1 and newer, and custom sprite metadata is preserved on Unity 6000.0 and newer; older editors use the standard TextureImporter permissions and reject unsupported custom metadata before saving.
- Automated checks pass on Unity 2022.3.61f1, 2023.1.22f1, 6000.0.71f1, 6000.1.4f1, and 6000.3.11f1. Unity 2023.2 is untested.
