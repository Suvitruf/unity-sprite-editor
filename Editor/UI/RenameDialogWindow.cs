using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations;
using BetterSpriteEditor.Operations.Renaming;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Batch rename dialog: composable rules with a live old → new preview.
    /// Nothing is applied until Rename is pressed, and the whole rename is one history entry.
    /// </summary>
    public class RenameDialogWindow : EditorWindow
    {
        private static readonly Color conflictColor = new(1f, 0.45f, 0.4f);
        private static readonly Color unchangedColor = new(1f, 1f, 1f, 0.45f);

        private SpriteDocument document;
        private SelectionModel selection;
        private HistoryStack history;
        private EditorWindow owner;

        private List<SpriteRegion> orderedRegions = new();
        private readonly List<string> newNames = new();
        private bool hasConflicts;
        private string ruleError;

        private EnumField orderField;
        private Toggle replaceNameToggle;
        private TextField baseNameField;
        private TextField findField;
        private TextField replaceField;
        private Toggle regexToggle;
        private Toggle caseSensitiveToggle;
        private IntegerField stripFirstField;
        private IntegerField stripLastField;
        private TextField prefixField;
        private TextField suffixField;
        private EnumField caseModeField;
        private Toggle numberingToggle;
        private IntegerField numberStartField;
        private IntegerField numberStepField;
        private IntegerField numberPaddingField;
        private ScrollView previewList;
        private Label statusLabel;
        private Button renameButton;

        public static void Open(SpriteDocument document, SelectionModel selection, HistoryStack history,
            EditorWindow owner)
        {
            var window = CreateInstance<RenameDialogWindow>();
            window.document = document;
            window.selection = selection;
            window.history = history;
            window.owner = owner;
            window.titleContent = new GUIContent("Rename Regions");
            var size = new Vector2(430f, 620f);
            window.minSize = new Vector2(size.x, 420f);
            window.position = new Rect(owner.position.center - size * 0.5f, size);
            window.ShowUtility();
        }

        private void OnDestroy()
        {
            // The dialog steals focus; hand it back so the next Ctrl+Z reaches our history.
            var windowToFocus = owner;
            if (windowToFocus != null)
                EditorApplication.delayCall += () => windowToFocus.Focus();
        }

        private void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 8;

            orderField = new EnumField("Order", RenameOrder.RowMajor);
            orderField.tooltip = "Order the numbering counter follows.";
            orderField.RegisterValueChangedCallback(_ => RefreshPreview());
            root.Add(orderField);

            root.Add(MakeSectionLabel("Name"));
            replaceNameToggle = new Toggle("Replace Name")
            {
                tooltip = "Off: start from each region's current name. On: start from the text below."
            };
            replaceNameToggle.RegisterValueChangedCallback(evt =>
            {
                baseNameField.SetEnabled(evt.newValue);
                RefreshPreview();
            });
            root.Add(replaceNameToggle);

            baseNameField = MakeTextField("Base Name");
            baseNameField.tooltip = $"Supports {RenameRule.NameToken} (original name) and " +
                                    $"{RenameRule.NumberToken} (counter position).";
            baseNameField.SetEnabled(false);
            root.Add(baseNameField);

            root.Add(MakeSectionLabel("Find & Replace"));
            findField = MakeTextField("Find");
            root.Add(findField);
            replaceField = MakeTextField("Replace");
            root.Add(replaceField);
            var toggleRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            regexToggle = MakeToggle("Regex");
            regexToggle.tooltip = "Use regular expressions. Capture groups are available as $1, $2 …";
            toggleRow.Add(regexToggle);
            caseSensitiveToggle = MakeToggle("Case Sensitive");
            toggleRow.Add(caseSensitiveToggle);
            root.Add(toggleRow);

            root.Add(MakeSectionLabel("Trim & Affixes"));
            var stripRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            stripFirstField = MakeIntField("Strip First");
            stripRow.Add(stripFirstField);
            stripLastField = MakeIntField("Strip Last");
            stripRow.Add(stripLastField);
            root.Add(stripRow);
            prefixField = MakeTextField("Prefix");
            root.Add(prefixField);
            suffixField = MakeTextField("Suffix");
            root.Add(suffixField);

            caseModeField = new EnumField("Case", RenameCaseMode.Unchanged);
            caseModeField.RegisterValueChangedCallback(_ => RefreshPreview());
            root.Add(caseModeField);

            root.Add(MakeSectionLabel("Numbering"));
            numberingToggle = new Toggle("Add Number");
            numberingToggle.RegisterValueChangedCallback(evt =>
            {
                SetNumberingEnabled(evt.newValue);
                RefreshPreview();
            });
            root.Add(numberingToggle);
            var numberRow = new VisualElement { style = { flexDirection = FlexDirection.Row } };
            numberStartField = MakeIntField("Start");
            numberStepField = MakeIntField("Step");
            numberStepField.value = 1;
            numberPaddingField = MakeIntField("Padding");
            numberPaddingField.value = 1;
            numberRow.Add(numberStartField);
            numberRow.Add(numberStepField);
            numberRow.Add(numberPaddingField);
            root.Add(numberRow);
            SetNumberingEnabled(false);

            root.Add(MakeSectionLabel("Preview"));
            previewList = new ScrollView
            {
                style =
                {
                    flexGrow = 1,
                    minHeight = 120,
                    backgroundColor = new Color(0.18f, 0.18f, 0.18f),
                    marginBottom = 4
                }
            };
            root.Add(previewList);

            statusLabel = new Label { style = { whiteSpace = WhiteSpace.Normal, marginBottom = 4 } };
            root.Add(statusLabel);

            var buttonRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd }
            };
            buttonRow.Add(new Button(Close) { text = "Cancel" });
            renameButton = new Button(Commit) { text = "Rename" };
            buttonRow.Add(renameButton);
            root.Add(buttonRow);

            RefreshPreview();
        }

        private void SetNumberingEnabled(bool enabled)
        {
            numberStartField.SetEnabled(enabled);
            numberStepField.SetEnabled(enabled);
            numberPaddingField.SetEnabled(enabled);
        }

        private RenameRule BuildRule()
        {
            return new RenameRule
            {
                ReplaceBaseName = replaceNameToggle.value,
                BaseName = baseNameField.value,
                FindText = findField.value,
                ReplaceText = replaceField.value,
                UseRegex = regexToggle.value,
                CaseSensitive = caseSensitiveToggle.value,
                StripFirst = Mathf.Max(0, stripFirstField.value),
                StripLast = Mathf.Max(0, stripLastField.value),
                Prefix = prefixField.value,
                Suffix = suffixField.value,
                CaseMode = (RenameCaseMode)caseModeField.value,
                UseNumbering = numberingToggle.value,
                NumberStart = numberStartField.value,
                NumberStep = numberStepField.value,
                NumberPadding = Mathf.Max(1, numberPaddingField.value)
            };
        }

        private void RefreshPreview()
        {
            var selectedRegions = new List<SpriteRegion>();
            foreach (var region in document.Regions)
            {
                if (selection.IsSelected(region.Id))
                    selectedRegions.Add(region);
            }
            orderedRegions = RegionOrdering.Sort(
                selectedRegions, (RenameOrder)orderField.value, selection.OrderedIds);

            newNames.Clear();
            ruleError = null;
            var rule = BuildRule();
            for (int i = 0; i < orderedRegions.Count; i++)
            {
                try
                {
                    newNames.Add(rule.Apply(orderedRegions[i].Name, i));
                }
                catch (ArgumentException exception)
                {
                    ruleError = exception.Message;
                    newNames.Clear();
                    break;
                }
            }

            BuildPreviewRows();
        }

        private void BuildPreviewRows()
        {
            previewList.Clear();

            if (ruleError != null)
            {
                statusLabel.text = ruleError;
                statusLabel.style.color = conflictColor;
                renameButton.SetEnabled(false);
                return;
            }

            // A name is invalid if it is empty, collides with another new name, or collides with
            // a region that is not part of this rename.
            var untouchedNames = new HashSet<string>();
            foreach (var region in document.Regions)
            {
                if (!selection.IsSelected(region.Id))
                    untouchedNames.Add(region.Name);
            }
            var seen = new HashSet<string>();
            var duplicates = new HashSet<string>();
            foreach (var name in newNames)
            {
                if (!seen.Add(name))
                    duplicates.Add(name);
            }

            hasConflicts = false;
            int changedCount = 0;
            for (int i = 0; i < orderedRegions.Count; i++)
            {
                string oldName = orderedRegions[i].Name;
                string newName = newNames[i];
                bool empty = string.IsNullOrWhiteSpace(newName);
                bool conflict = empty || duplicates.Contains(newName) || untouchedNames.Contains(newName);
                bool changed = newName != oldName;
                if (conflict)
                    hasConflicts = true;
                if (changed && !conflict)
                    changedCount++;

                var row = new Label(changed ? $"{oldName}  →  {newName}" : oldName)
                {
                    style =
                    {
                        paddingLeft = 6,
                        paddingTop = 1,
                        paddingBottom = 1,
                        color = conflict ? conflictColor : changed ? StyleKeyword.Null : unchangedColor
                    }
                };
                if (conflict)
                    row.tooltip = empty ? "Name would be empty." : "Name is already used.";
                previewList.Add(row);
            }

            if (hasConflicts)
            {
                statusLabel.text = "Some names are empty or duplicated. Region names must be unique.";
                statusLabel.style.color = conflictColor;
            }
            else
            {
                statusLabel.text = $"{changedCount} of {orderedRegions.Count} regions will be renamed. " +
                                   "Sprite references are preserved.";
                statusLabel.style.color = StyleKeyword.Null;
            }
            renameButton.SetEnabled(!hasConflicts && changedCount > 0);
        }

        private void Commit()
        {
            if (hasConflicts || ruleError != null)
                return;

            var commands = new List<IEditorCommand>();
            string label = orderedRegions.Count == 1
                ? $"Rename '{orderedRegions[0].Name}'"
                : $"Rename {orderedRegions.Count} regions";

            for (int i = 0; i < orderedRegions.Count; i++)
            {
                var region = orderedRegions[i];
                if (region.Name == newNames[i])
                    continue;
                var before = region.Clone();
                region.Name = newNames[i];
                commands.Add(new ModifyRegionCommand(document, region.Id, before, region.Clone(), label));
            }
            if (commands.Count == 0)
            {
                Close();
                return;
            }

            history.PushApplied(commands.Count == 1 ? commands[0] : new CompositeCommand(label, commands));
            Close();
        }

        // --- UI helpers ---

        private TextField MakeTextField(string label)
        {
            var field = new TextField(label);
            field.RegisterValueChangedCallback(_ => RefreshPreview());
            return field;
        }

        private Toggle MakeToggle(string label)
        {
            var toggle = new Toggle(label) { style = { flexGrow = 1, flexBasis = 0 } };
            toggle.RegisterValueChangedCallback(_ => RefreshPreview());
            return toggle;
        }

        private IntegerField MakeIntField(string label)
        {
            var field = new IntegerField(label) { style = { flexGrow = 1, flexBasis = 0 } };
            field.labelElement.style.minWidth = 46;
            field.RegisterValueChangedCallback(_ => RefreshPreview());
            return field;
        }

        private static Label MakeSectionLabel(string text)
        {
            return new Label(text)
            {
                style =
                {
                    unityFontStyleAndWeight = FontStyle.Bold,
                    marginTop = 8,
                    marginBottom = 2
                }
            };
        }
    }
}
