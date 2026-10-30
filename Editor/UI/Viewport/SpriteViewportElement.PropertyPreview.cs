using BetterSpriteEditor.Model;
using BetterSpriteEditor.Operations.Clipboard;
using UnityEngine;
using UnityEngine.UIElements;

namespace BetterSpriteEditor.UI.Viewport
{
    public partial class SpriteViewportElement
    {
        private SpritePropertyPastePlan propertyPreview;

        internal void SetPropertyPreview(SpritePropertyPastePlan plan)
        {
            propertyPreview = plan;
            MarkDirtyRepaint();
        }

        private void DrawPropertyPreview(Painter2D painter)
        {
            if (propertyPreview == null) return;
            var fields = propertyPreview.Settings;
            foreach (var entry in propertyPreview.Entries)
            {
                if (entry.Excluded || entry.Issue != null || entry.After == null) continue;
                var region = entry.After;
                if (fields.Includes(SpritePropertyFields.Outline))
                    DrawPropertyPaths(painter, region, ShapeKind.Outline, new Color(1f, 0.55f, 0.1f), true);
                if (fields.Includes(SpritePropertyFields.Physics))
                    DrawPropertyPaths(painter, region, ShapeKind.Physics, new Color(0.2f, 1f, 0.4f), false);
                if (fields.Includes(SpritePropertyFields.Border))
                {
                    painter.strokeColor = Color.yellow;
                    painter.lineWidth = 2;
                    painter.BeginPath();
                    AddBorderLinesPath(painter, region.Rect, region.Border);
                    painter.Stroke();
                }
                if (fields.Includes(SpritePropertyFields.Pivot))
                {
                    var point = viewTransform.TextureToView(region.GetPivotTexturePosition());
                    painter.strokeColor = new Color(0.2f, 0.65f, 1f);
                    painter.lineWidth = 2;
                    painter.BeginPath();
                    painter.MoveTo(point - Vector2.right * 7); painter.LineTo(point + Vector2.right * 7);
                    painter.MoveTo(point - Vector2.up * 7); painter.LineTo(point + Vector2.up * 7);
                    painter.Stroke();
                }
            }
        }

        private void DrawPropertyPaths(Painter2D painter, SpriteRegion region, ShapeKind kind, Color color, bool fill)
        {
            foreach (var path in region.GetShapes(kind))
            {
                if (path.Length < 3) continue;
                painter.strokeColor = color;
                painter.lineWidth = 2;
                painter.BeginPath();
                painter.MoveTo(viewTransform.TextureToView(region.ShapePointToTexture(path[0])));
                for (int i = 1; i <= path.Length; i++)
                {
                    var point = viewTransform.TextureToView(region.ShapePointToTexture(path[i % path.Length]));
                    painter.LineTo(point);
                    // Keep stroke tessellation bounded even for imported, densely traced paths.
                    if (path.Length >= maxBatchedPoints && i % maxBatchedPoints == 0)
                    {
                        painter.Stroke();
                        painter.BeginPath();
                        painter.MoveTo(point);
                    }
                }
                painter.Stroke();
                if (fill && path.Length < maxBatchedPoints)
                {
                    painter.ClosePath();
                    painter.fillColor = new Color(color.r, color.g, color.b, 0.22f);
                    painter.Fill(FillRule.OddEven);
                }
            }
        }
    }
}
