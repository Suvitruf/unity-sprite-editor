using System;
using System.Collections.Generic;
using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations.Pivots;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI
{
    /// <summary>
    /// Aligns pivots across the selected frames so an animation stops jittering.
    /// An overlay panel inside the editor window (not a separate window, which the OS could
    /// stack behind the editor): every setting change updates the viewport's onion-skin
    /// overlay, so the result is judged before anything is applied. The candidate pivot can
    /// also be dragged directly inside the onion-skin box.
    /// </summary>
    public class AlignPivotsPanelElement : VisualElement
    {
        private SpriteDocument document;
        private List<GUID> regionIds;
        private GUID referenceId;
        private Func<TextureAlphaMap> alphaMapProvider;
        private Action<Dictionary<GUID, Vector2>> onPreview;
        private Action<Dictionary<GUID, Vector2>> onApply;

        private Dictionary<GUID, Vector2> anchors;
        private bool alphaMapFailed;

        private readonly EnumField modeField;
        private readonly EnumField anchorField;
        private readonly Vector2Field customAnchorField;
        private readonly Label referenceLabel;
        private readonly Label warningLabel;
        private readonly Button applyButton;

        public AlignPivotsPanelElement()
        {
            style.position = Position.Absolute;
            style.top = 8;
            style.left = 8;
            style.width = 260;
            style.backgroundColor = new Color(0.22f, 0.22f, 0.22f, 0.98f);
            style.borderTopWidth = style.borderBottomWidth = 1;
            style.borderLeftWidth = style.borderRightWidth = 1;
            style.borderTopColor = style.borderBottomColor =
                style.borderLeftColor = style.borderRightColor = new Color(0.1f, 0.1f, 0.1f);
            style.paddingLeft = style.paddingRight = 8;
            style.paddingTop = 6;
            style.paddingBottom = 8;
            style.display = DisplayStyle.None;

            Add(new Label("Align Pivots")
            {
                style = { unityFontStyleAndWeight = FontStyle.Bold, marginBottom = 4 }
            });

            modeField = new EnumField("Mode", PivotAlignMode.InferredGrid);
            modeField.tooltip =
                "Inferred Grid recovers the sheet's cell layout from the rects (best for sheets " +
                "that were drawn on a grid). Content Bounds anchors to each frame's opaque pixels " +
                "(for packed sheets). Reference Frame spreads the active region's pivot to the rest.";
            modeField.RegisterValueChangedCallback(_ => Recompute());
            Add(modeField);

            anchorField = new EnumField("Anchor", SpriteAlignment.BottomCenter);
            anchorField.RegisterValueChangedCallback(_ => Recompute());
            Add(anchorField);

            customAnchorField = new Vector2Field("Custom") { value = new Vector2(0.5f, 0f) };
            customAnchorField.RegisterValueChangedCallback(_ => Recompute());
            Add(customAnchorField);

            referenceLabel = new Label { style = { marginTop = 2, opacity = 0.75f } };
            Add(referenceLabel);

            Add(new Label
            {
                style = { whiteSpace = WhiteSpace.Normal, opacity = 0.75f, marginTop = 4 },
                text = "The box in the viewport stacks all frames on the candidate pivot — " +
                       "when the art lines up, the animation is stable. Drag inside the box " +
                       "to place the pivot by eye."
            });

            warningLabel = new Label
            {
                style = { color = new Color(1f, 0.45f, 0.4f), whiteSpace = WhiteSpace.Normal }
            };
            Add(warningLabel);

            var buttonRow = new VisualElement
            {
                style = { flexDirection = FlexDirection.Row, justifyContent = Justify.FlexEnd, marginTop = 8 }
            };
            buttonRow.Add(new Button(Hide) { text = "Cancel" });
            applyButton = new Button(Apply) { text = "Apply" };
            buttonRow.Add(applyButton);
            Add(buttonRow);
        }

        public void Show(SpriteDocument newDocument, List<SpriteRegion> regions,
            SpriteRegion reference, Func<TextureAlphaMap> newAlphaMapProvider,
            Action<Dictionary<GUID, Vector2>> newOnPreview, Action<Dictionary<GUID, Vector2>> newOnApply)
        {
            if (document != null)
                document.Changed -= Recompute;

            document = newDocument;
            regionIds = regions.ConvertAll(region => region.Id);
            referenceId = reference?.Id ?? default;
            alphaMapProvider = newAlphaMapProvider;
            alphaMapFailed = false;
            onPreview = newOnPreview;
            onApply = newOnApply;

            document.Changed += Recompute;
            style.display = DisplayStyle.Flex;
            Recompute();
        }

        public void Hide()
        {
            if (style.display == DisplayStyle.None)
                return;
            style.display = DisplayStyle.None;
            if (document != null)
                document.Changed -= Recompute;
            document = null;
            anchors = null;
            onPreview?.Invoke(null);
        }

        /// <summary>
        /// The candidate pivot dragged in the viewport's onion-skin box, normalized to the
        /// stacked frames' bounds (y up). Switches the anchor to Custom so the numbers follow.
        /// </summary>
        public void SetAnchorFromViewportDrag(Vector2 normalized)
        {
            if (style.display == DisplayStyle.None || document == null)
                return;
            if ((PivotAlignMode)modeField.value == PivotAlignMode.ReferenceFrame)
                return;

            normalized.x = Mathf.Round(normalized.x * 10000f) / 10000f;
            normalized.y = Mathf.Round(normalized.y * 10000f) / 10000f;
            anchorField.SetValueWithoutNotify(SpriteAlignment.Custom);
            customAnchorField.SetValueWithoutNotify(normalized);
            Recompute();
        }

        private void Recompute()
        {
            if (document == null || style.display == DisplayStyle.None)
                return;

            // Regions can vanish while the panel is open (undo of a create, a split…).
            var regions = new List<SpriteRegion>();
            foreach (var id in regionIds)
            {
                var region = document.FindRegion(id);
                if (region != null)
                    regions.Add(region);
            }
            var reference = document.FindRegion(referenceId) ?? (regions.Count > 0 ? regions[0] : null);

            var mode = (PivotAlignMode)modeField.value;
            bool usesAnchor = mode != PivotAlignMode.ReferenceFrame;
            anchorField.style.display = usesAnchor ? DisplayStyle.Flex : DisplayStyle.None;
            customAnchorField.style.display =
                usesAnchor && (SpriteAlignment)anchorField.value == SpriteAlignment.Custom
                    ? DisplayStyle.Flex : DisplayStyle.None;
            referenceLabel.style.display = mode == PivotAlignMode.ReferenceFrame
                ? DisplayStyle.Flex : DisplayStyle.None;
            if (reference != null)
                referenceLabel.text = $"Reference: '{reference.Name}' (the active region).";

            string warning = null;
            if (regions.Count < 2)
                warning = "Select at least two regions.";

            TextureAlphaMap alphaMap = null;
            if (mode == PivotAlignMode.ContentBounds && warning == null)
            {
                alphaMap = TryGetAlphaMap();
                if (alphaMap == null)
                    warning = "Pixel data unavailable — Content Bounds mode cannot work here.";
            }

            if (warning == null)
            {
                anchors = PivotAlignment.ComputeAnchors(
                    regions, mode, ResolveAnchorNormalized(), reference, alphaMap, 0);
                if (anchors.Count == 0)
                    warning = "Could not compute anchors for these regions.";
            }
            else
            {
                anchors = null;
            }

            warningLabel.text = warning ?? string.Empty;
            applyButton.SetEnabled(warning == null);
            onPreview?.Invoke(anchors);
        }

        private Vector2 ResolveAnchorNormalized()
        {
            var alignment = (SpriteAlignment)anchorField.value;
            return alignment switch
            {
                SpriteAlignment.TopLeft => new Vector2(0f, 1f),
                SpriteAlignment.TopCenter => new Vector2(0.5f, 1f),
                SpriteAlignment.TopRight => new Vector2(1f, 1f),
                SpriteAlignment.LeftCenter => new Vector2(0f, 0.5f),
                SpriteAlignment.Center => new Vector2(0.5f, 0.5f),
                SpriteAlignment.RightCenter => new Vector2(1f, 0.5f),
                SpriteAlignment.BottomLeft => new Vector2(0f, 0f),
                SpriteAlignment.BottomCenter => new Vector2(0.5f, 0f),
                SpriteAlignment.BottomRight => new Vector2(1f, 0f),
                _ => customAnchorField.value
            };
        }

        private TextureAlphaMap TryGetAlphaMap()
        {
            if (alphaMapFailed)
                return null;
            try
            {
                return alphaMapProvider?.Invoke();
            }
            catch (Exception)
            {
                alphaMapFailed = true;
                return null;
            }
        }

        private void Apply()
        {
            if (anchors == null || anchors.Count == 0)
                return;
            var toApply = anchors;
            Hide();
            onApply?.Invoke(toApply);
        }
    }
}
