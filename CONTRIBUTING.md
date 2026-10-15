# Contributing

## Development setup

Use Unity 2023.1 or newer. Keep a checkout outside the host project's Assets folder and install its `package.json` through Package Manager's **Install package from disk** action. Do not install a second copy of the package in the same project.

The package has no runtime feature assembly. Production code belongs in the editor-only `Editor/` assembly. `Tests/Fixtures/` contains a test-only component assembly used to exercise serialized scene references.

Use public Unity APIs and the public Sprite Editor Data Provider interfaces. Preserve existing sprite IDs and synchronize name/fileID pairs when editing regions. Group writes so each changed texture is reimported once per Apply. Package folders may be read-only; store personal settings under UserSettings and temporary recovery data under Library.

## Tests

Install Unity Test Framework in the host project. Add this entry to the root object of `Packages/manifest.json`, preserving its existing dependencies and other settings:

```json
"testables": ["com.apanasik.better-sprite-editor"]
```

If `testables` already exists, append the package name to its array. Open **Window > General > Test Runner**, select **EditMode**, and run the package tests. Importer tests create and remove disposable fixture assets.

The package targets Unity 2023.1 and newer. Automated compatibility checks pass on Unity 2022.3.61f1 (38 tests), 2023.1.22f1 (38 tests), 6000.0.71f1 (38 tests), 6000.1.4f1 (46 tests), and 6000.3.11f1 (46 tests). Inspection of the Sprite package bundled with 2023.1.22f1 confirms that it exposes neither `SpriteRect.customData` nor the frame edit capability API, so the older API branch applies to 2023.1. Unity 2023.2 is not installed in the validation environment, so direct tests on that editor remain pending; manual viewport checks have been done only on Unity 6000.3. The 2022 run exercises the older API branch; it does not extend the declared support range. Use an editor-compatible Test Framework: the matrix used 1.1.33 with NUnit 1.0.6 on 2022.3 and 2023.1, 1.4.5 with NUnit 2.0.3 on 6000.0/6000.1, and 1.6.0 with NUnit 2.0.3 on 6000.3. Headless tests do not cover all viewport interactions, native dialogs, focus transitions, or GPU memory behavior.

Keep newer Sprite APIs inside `SpriteProviderCompatibility`; the built-in Sprite package reports version 1.0.0 across different editor API versions. UI and model code use package-owned capability flags. When adding an editor version, verify both compilation and importer behavior using its own built-in Sprite dependency.

## Manual release checks

- Install the package in a clean Unity project and confirm it compiles without relying on another project or a writable package directory.
- Exercise Single and Multiple sprite modes, slicing previews, editing, Apply/Revert, and sprite-reference preservation after renaming.
- Verify Save/Discard/Cancel on texture switching and window close, including a failed Apply.
- Verify undo/redo in text fields and region tools, domain-reload recovery, and external texture changes or moves.
- Exercise viewport dragging, capture/focus changes, shape previews, and drag-out on each platform advertised as tested.
- Check cancelled and incomplete usage scans, ensuring they do not claim unused sprites.
- Profile large atlases and repeated open/slice/close cycles for responsiveness and native texture growth.
- Review the manifest, changelog, user documentation, and license before creating a release tag.

## Reporting issues

Include the Unity version, package revision, operating system, texture import mode, reproduction steps, and relevant Console errors. A small reproducible project or texture is helpful when it can be shared.

## Documentation style

Write each prose paragraph and each list-item paragraph on a single source line. Do not insert manual line breaks to meet a column width; let the editor wrap visually. Preserve structural line breaks in headings, separate list items, tables, code blocks, and intentional Markdown hard breaks.
