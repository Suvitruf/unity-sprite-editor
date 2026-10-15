using System.Collections.Generic;
using BetterSpriteEditor.Operations.Usages;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Lists everywhere the chosen regions' sprites are referenced, in the open scene or across
    /// the project, with Ping and Open on every row.
    /// </summary>
    /// <summary>How results are grouped: by the asset holding the reference, or by the sprite.</summary>
    public enum UsageGrouping
    {
        Objects,
        Sprites
    }

    public class SpriteUsagesWindow : EditorWindow
    {
        private static readonly Color assetHeaderColor = new(1f, 1f, 1f, 0.08f);
        private static readonly Color unusedColor = new(1f, 0.6f, 0.15f);

        /// <summary>
        /// Publishes searched sprite IDs, IDs with no references in the completed scope,
        /// and the scope. Cancelled or incomplete scans publish no negative conclusions.
        /// </summary>
        public static event System.Action<string, IReadOnlyCollection<string>, IReadOnlyCollection<string>, UsageScope>
            SearchCompleted;

        /// <summary>Raised when the window closes, so the unused highlight can be cleared.</summary>
        public static event System.Action Closed;

        [SerializeField] private string texturePath;
        [SerializeField] private string[] regionNames;
        [SerializeField] private string[] regionIds;
        [SerializeField] private bool documentDirty;
        [SerializeField] private UsageScope scope = UsageScope.CurrentScene;
        [SerializeField] private UsageGrouping grouping = UsageGrouping.Objects;

        private readonly List<SpriteUsage> usages = new();
        private readonly Dictionary<string, Sprite> spritesByName = new();
        private bool searched;
        private UsageSearchResult searchResult;

        private EnumField scopeField;
        private EnumField groupingField;
        private Label summaryLabel;
        private HelpBox warningBox;
        private ScrollView resultList;

        public static void Open(string texturePath, IReadOnlyList<string> regionNames, bool documentDirty, IReadOnlyList<string> regionIds = null)
        {
            var window = GetWindow<SpriteUsagesWindow>();
            window.titleContent = new GUIContent("Sprite Usages");
            window.minSize = new Vector2(420f, 240f);
            window.texturePath = texturePath;
            window.regionNames = new List<string>(regionNames).ToArray();
            window.documentDirty = documentDirty;
            window.regionIds = regionIds == null ? null : new List<string>(regionIds).ToArray();
            window.searched = false;
            window.Show();
            window.RefreshHeader();
            window.Search();
        }

        private void OnDestroy()
        {
            Closed?.Invoke();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;

            var toolbar = new Toolbar();
            root.Add(toolbar);

            scopeField = new EnumField(scope) { style = { width = 130 } };
            scopeField.RegisterValueChangedCallback(evt =>
            {
                scope = (UsageScope)evt.newValue;
                Search();
            });
            toolbar.Add(scopeField);

            groupingField = new EnumField(grouping) { style = { width = 100 } };
            groupingField.tooltip = "Group results by the asset that holds the reference, or by sprite.";
            groupingField.RegisterValueChangedCallback(evt =>
            {
                grouping = (UsageGrouping)evt.newValue;
                BuildResultRows(); // regrouping is a view change, no need to search again
            });
            toolbar.Add(groupingField);

            toolbar.Add(new ToolbarButton(Search) { text = "Search" });
            toolbar.Add(new ToolbarSpacer { style = { flexGrow = 1 } });
            summaryLabel = new Label { style = { unityTextAlign = TextAnchor.MiddleRight, marginRight = 6 } };
            toolbar.Add(summaryLabel);

            warningBox = new HelpBox(string.Empty, HelpBoxMessageType.Warning)
            {
                style = { display = DisplayStyle.None }
            };
            root.Add(warningBox);

            resultList = new ScrollView { style = { flexGrow = 1 } };
            root.Add(resultList);

            RefreshHeader();
            if (!searched && !string.IsNullOrEmpty(texturePath))
                Search();
            else
                BuildResultRows();
        }

        private void RefreshHeader()
        {
            if (scopeField == null)
                return;
            scopeField.SetValueWithoutNotify(scope);
            groupingField.SetValueWithoutNotify(grouping);
        }

        private void Search()
        {
            if (string.IsNullOrEmpty(texturePath) || regionNames == null || regionNames.Length == 0)
                return;
            if (resultList == null)
                return; // CreateGUI has not run yet

            usages.Clear();
            searched = true;
            searchResult = new UsageSearchResult();

            var sprites = SpriteUsageFinder.LoadSprites(texturePath, regionNames, regionIds);
            spritesByName.Clear();
            foreach (var pair in sprites)
            {
                if (pair.Key is Sprite sprite)
                    spritesByName[pair.Value] = sprite;
            }
            if (sprites.Count == 0)
            {
                BuildResultRows();
                return;
            }

            if (scope == UsageScope.CurrentScene)
            {
                usages.AddRange(SpriteUsageFinder.FindInOpenScenes(sprites));
                searchResult.Completed = true;
            }
            else
            {
                try
                {
                    searchResult = SpriteUsageFinder.FindInProject(texturePath, sprites,
                        (progress, path) => !EditorUtility.DisplayCancelableProgressBar(
                            "Searching project", path, progress));
                    usages.AddRange(searchResult.Usages);
                }
                finally
                {
                    EditorUtility.ClearProgressBar();
                }
            }

            BuildResultRows();
        }

        private void BuildResultRows()
        {
            resultList.Clear();
            RefreshWarning();

            var searched = regionNames ?? System.Array.Empty<string>();
            var usedNames = new HashSet<string>();
            foreach (var usage in usages)
                usedNames.Add(usage.RegionName);

            var unusedNames = new List<string>();
            foreach (var name in searched)
            {
                if (!usedNames.Contains(name) && spritesByName.ContainsKey(name)
                    && searchResult is { CanReportNoReferences: true })
                    unusedNames.Add(name);
            }

            var checkedIds = new List<string>();
            var noReferenceIds = new List<string>();
            for (int i = 0; i < searched.Length && regionIds != null && i < regionIds.Length; i++)
            {
                checkedIds.Add(regionIds[i]);
                if (unusedNames.Contains(searched[i]))
                    noReferenceIds.Add(regionIds[i]);
            }
            SearchCompleted?.Invoke(texturePath, checkedIds, noReferenceIds, scope);

            int unknown = searched.Length - usedNames.Count - unusedNames.Count;
            summaryLabel.text = $"{usedNames.Count} referenced · {unusedNames.Count} no hits · {unknown} unknown";
            if (unknown > 0)
                resultList.Add(new HelpBox("Some sprites were not fully checked or have no applied sprite asset. " +
                    "No-hit results do not account for dynamic loading.", HelpBoxMessageType.Info));

            if (usages.Count > 0)
            {
                // Only label the sections when there is something to distinguish.
                if (unusedNames.Count > 0)
                    resultList.Add(MakeSectionHeader($"Used ({usedNames.Count})", StyleKeyword.Null));

                if (grouping == UsageGrouping.Objects)
                    BuildObjectGroups();
                else
                    BuildSpriteGroups(searched);
            }

            if (unusedNames.Count == 0)
                return;

            // A single region with no hits reads better as a sentence than as a list of one.
            if (searched.Length == 1)
            {
                resultList.Add(new Label(scope == UsageScope.CurrentScene
                    ? "No references found in the loaded scenes."
                    : "No references found in scanned project assets.")
                {
                    style = { marginLeft = 8, marginTop = 8, opacity = 0.7f, whiteSpace = WhiteSpace.Normal }
                });
                return;
            }

            resultList.Add(MakeSectionHeader($"No references found ({unusedNames.Count})", unusedColor));
            resultList.Add(new Label(scope == UsageScope.CurrentScene
                ? "Scope: loaded scenes. References elsewhere and dynamic loading are not checked."
                : "Scope: supported text assets under Assets/. Packages and dynamic loading are not checked.")
            {
                style = { marginLeft = 8, marginBottom = 4, opacity = 0.7f, whiteSpace = WhiteSpace.Normal }
            });
            foreach (var name in unusedNames)
            {
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 16 }
                };
                row.Add(MakeSpriteIcon(name));
                row.Add(new Label(name)
                {
                    style = { flexGrow = 1, color = unusedColor, overflow = Overflow.Hidden }
                });
                resultList.Add(row);
            }
        }

        /// <summary>
        /// Thumbnail of the region itself. UI Toolkit's Image handles the sprite's rect within
        /// the atlas, so no per-region texture copies are needed. Always occupies the same space,
        /// even when the sprite is missing, so the text stays aligned.
        /// </summary>
        private VisualElement MakeSpriteIcon(string regionName)
        {
            var icon = new Image
            {
                scaleMode = ScaleMode.ScaleToFit,
                style = { width = 20, height = 20, marginRight = 6, flexShrink = 0 }
            };
            if (spritesByName.TryGetValue(regionName, out var sprite))
            {
                icon.sprite = sprite;
                icon.tooltip = regionName;
            }
            return icon;
        }

        /// <summary>One block per asset, listing the sprites it references.</summary>
        private void BuildObjectGroups()
        {
            var byAsset = new Dictionary<string, List<SpriteUsage>>();
            var assetOrder = new List<string>();
            foreach (var usage in usages)
            {
                if (!byAsset.TryGetValue(usage.AssetPath, out var list))
                {
                    list = new List<SpriteUsage>();
                    byAsset[usage.AssetPath] = list;
                    assetOrder.Add(usage.AssetPath);
                }
                list.Add(usage);
            }
            foreach (var assetPath in assetOrder)
                resultList.Add(BuildAssetGroup(assetPath, byAsset[assetPath]));
        }

        /// <summary>One block per sprite, listing the assets that reference it.</summary>
        private void BuildSpriteGroups(IReadOnlyList<string> searchOrder)
        {
            var byRegion = new Dictionary<string, List<SpriteUsage>>();
            foreach (var usage in usages)
            {
                if (!byRegion.TryGetValue(usage.RegionName, out var list))
                {
                    list = new List<SpriteUsage>();
                    byRegion[usage.RegionName] = list;
                }
                list.Add(usage);
            }

            // Follow the order the regions were searched in, so the list matches the sheet.
            foreach (var regionName in searchOrder)
            {
                if (byRegion.TryGetValue(regionName, out var regionUsages))
                    resultList.Add(BuildSpriteGroup(regionName, regionUsages));
            }
        }

        private VisualElement BuildSpriteGroup(string regionName, List<SpriteUsage> regionUsages)
        {
            var group = new VisualElement { style = { marginBottom = 4 } };

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    backgroundColor = assetHeaderColor,
                    paddingLeft = 6,
                    paddingTop = 2,
                    paddingBottom = 2
                }
            };
            header.Add(MakeSpriteIcon(regionName));
            header.Add(new Label(regionName)
            {
                style = { flexGrow = 1, unityFontStyleAndWeight = FontStyle.Bold, overflow = Overflow.Hidden }
            });
            header.Add(new Label($"{regionUsages.Count} reference(s)")
            {
                style = { opacity = 0.7f, marginRight = 4 }
            });
            group.Add(header);

            foreach (var usage in regionUsages)
            {
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 16 }
                };
                var icon = AssetDatabase.GetCachedIcon(usage.AssetPath);
                if (icon != null)
                {
                    row.Add(new Image
                    {
                        image = icon,
                        style = { width = 16, height = 16, marginRight = 4, flexShrink = 0 }
                    });
                }
                row.Add(new Label($"{usage.AssetPath}  —  {usage.DisplayLocation}")
                {
                    style = { flexGrow = 1, overflow = Overflow.Hidden }
                });
                row.Add(new Button(() => SpriteUsageFinder.Ping(usage)) { text = "Ping" });
                row.Add(new Button(() => Reveal(usage))
                {
                    text = usage.SceneObject != null ? "Select" : "Open"
                });
                group.Add(row);
            }

            return group;
        }

        private static Label MakeSectionHeader(string text, StyleColor color)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 6,
                    marginLeft = 6,
                    marginBottom = 2,
                    color = color
                }
            };
        }

        private VisualElement BuildAssetGroup(string assetPath, List<SpriteUsage> assetUsages)
        {
            var group = new VisualElement { style = { marginBottom = 4 } };

            var header = new VisualElement
            {
                style =
                {
                    flexDirection = FlexDirection.Row,
                    alignItems = Align.Center,
                    backgroundColor = assetHeaderColor,
                    paddingLeft = 6,
                    paddingTop = 2,
                    paddingBottom = 2
                }
            };
            var icon = AssetDatabase.GetCachedIcon(assetPath);
            if (icon != null)
            {
                header.Add(new Image
                {
                    image = icon,
                    style = { width = 16, height = 16, marginRight = 4, flexShrink = 0 }
                });
            }
            header.Add(new Label(assetPath)
            {
                style = { flexGrow = 1, unityFontStyleAndWeight = FontStyle.Bold, overflow = Overflow.Hidden }
            });
            header.Add(new Button(() => SpriteUsageFinder.Ping(assetUsages[0])) { text = "Ping" });
            header.Add(new Button(() => Reveal(assetUsages[0])) { text = "Open" });
            group.Add(header);

            foreach (var usage in assetUsages)
            {
                var row = new VisualElement
                {
                    style = { flexDirection = FlexDirection.Row, alignItems = Align.Center, paddingLeft = 16 }
                };
                row.Add(MakeSpriteIcon(usage.RegionName));
                row.Add(new Label($"{usage.RegionName}  —  {usage.DisplayLocation}")
                {
                    style = { flexGrow = 1, overflow = Overflow.Hidden }
                });
                if (usage.SceneObject != null)
                    row.Add(new Button(() => Reveal(usage)) { text = "Select" });
                group.Add(row);
            }

            return group;
        }

        private void Reveal(SpriteUsage usage)
        {
            if (!SpriteUsageFinder.Reveal(usage))
                return;
            // Opening a scene invalidates live object references from the previous one.
            if (usage.IsScene && usage.SceneObject == null && scope == UsageScope.CurrentScene)
                Search();
        }

        private void RefreshWarning()
        {
            var messages = new List<string>();
            if (documentDirty)
                messages.Add("The editor has unapplied changes — results reflect the last applied state.");
            if (scope == UsageScope.WholeProject && !SpriteUsageFinder.ProjectUsesTextSerialization)
                messages.Add("Project serialization is not Force Text. Binary assets cannot be checked by this scan.");
            if (searchResult is { Completed: false })
                messages.Add("Search incomplete: no-hit sprites have unknown status.");
            if (searchResult is { SkippedAssets: > 0 })
                messages.Add($"{searchResult.SkippedAssets} assets could not be scanned; no-hit sprites have unknown status.");

            if (messages.Count == 0)
            {
                warningBox.style.display = DisplayStyle.None;
                return;
            }
            warningBox.text = string.Join("\n", messages);
            warningBox.style.display = DisplayStyle.Flex;
        }
    }
}
