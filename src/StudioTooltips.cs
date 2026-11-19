// The cells' tooltips, as the editor's tooltip panels (EditorSkin.xur) show them.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed partial class StudioGame
    {
        private sealed class Tip { public int State = 3; public float Hold, Opacity, Age; public string Text; }
        private readonly Dictionary<int, Tip> tips = new Dictionary<int, Tip>();
        private long tipsKey = long.MinValue;

        private int TipControls()
        {
            switch (state)
            {
                case State.Items: return Math.Min(pageItems.Count + RemoveSlots, Layout.CellsPerPage);
                case State.Colors: return page == 0 ? Math.Min(ColorRemoveSlots, Layout.ChipsPerPage) : 0;
                case State.ColourSets: return sets == null ? 0 : Math.Max(0, Math.Min(Layout.ChipsPerPage, sets.Sets.Length - page * Layout.ChipsPerPage));
                case State.Outfits: return OutfitsOnPage;
            }
            return 0;
        }

        /// <summary>Control i's text, or null when it has none.</summary>
        private string TipText(int i)
        {
            switch (state)
            {
                case State.Items:
                {
                    int slots = RemoveSlots;
                    if (i < slots) return RemoveLabelForLeaf();                         // Grid1x1Button_deleteitem
                    int k = i - slots;
                    return k < pageItems.Count ? pageItems[k].Name : null;              // Grid1x1ItemButton
                }
                case State.Colors:
                    return i < ColorRemoveSlots ? RemoveLabel(activeLeaf.Remove) : null; // only the remove cell
                case State.ColourSets:
                {
                    int m = sets.Shown;                                                  // sub_920DF4F8's visual
                    return m >= 1 && m <= 3 ? null : (setsItem == null ? null : setsItem.Name);
                }
                case State.Outfits:
                    return OutfitLabel(i);
            }
            return null;
        }

        /// <summary>Control i's place in the scene, and its visual's width.</summary>
        private Vector2 TipCell(int i, out float visualWidth)
        {
            visualWidth = 128f;
            switch (state)
            {
                case State.Items: return ItemCellScene(i);
                case State.Colors: case State.ColourSets: return ChipCellScene(i);
                case State.Outfits: visualWidth = 180f; return OutfitTileScene(i);
            }
            return Vector2.Zero;
        }

        /// <summary>Every frame, after the page's controls are in step.</summary>
        private void TickTooltips(float dt)
        {
            if (pageSceneKey != tipsKey) { tips.Clear(); tipsKey = pageSceneKey; }
            dt = Math.Min(dt, 0.05f);
            int n = TipControls();
            for (int i = 0; i < n; i++)
            {
                Tip t;
                if (!tips.TryGetValue(i, out t)) { t = new Tip(); tips[i] = t; }
                t.Age += dt;
                if (t.Age < 1f) continue;                                     // the first tick, 1000 ms after init
                string text = TipText(i);
                if (text == null || text.Trim().Length == 0) { t.Text = null; t.State = 3; t.Opacity = 0f; continue; }
                t.Text = text;
                bool show;
                if (i == cursor) { t.Hold = Math.Min(t.Hold + dt, 0.1f); show = t.Hold >= 0.1f || t.State != 3; }
                else { t.Hold = 0f; show = false; }
                switch (t.State)
                {
                    case 3: if (show) t.State = 0; break;
                    case 0: t.Opacity += 3f * dt; if (t.Opacity >= 1f) { t.Opacity = 1f; t.State = 1; } break;
                    case 1: if (!show) t.State = 2; break;
                    case 2: t.Opacity -= 6f * dt; if (t.Opacity <= 0f) { t.Opacity = 0f; t.State = 3; } break;
                }
            }
        }

        /// <summary>The focused cell's tooltip is drawn last.</summary>
        private void DrawTooltips()
        {
            for (int pass = 0; pass < 2; pass++)
                foreach (KeyValuePair<int, Tip> kv in tips)
                {
                    if ((kv.Key == cursor) != (pass == 1)) continue;
                    Tip t = kv.Value;
                    if (t.Opacity <= 0f || t.Text == null) continue;
                    DrawTooltip(kv.Key, t.Text, t.Opacity);
                }
        }

        private void DrawTooltip(int i, string text, float opacity)
        {
            float vw;
            Matrix ctrl = ControlAt(TipCell(i, out vw));
            bool right = Vector2.Transform(Vector2.Zero, ctrl).X >= 640f;
            RuntimeFont f = ui.Tooltip;
            float tw = Math.Min(500f, f.Measure(text).X / Layout.SceneScale);   // column `500-`
            float th = (float)Math.Ceiling(f.LineHeight / Layout.SceneScale) + 1f;
            float bx = right ? 15f - tw : (vw > 128f ? 155f : 103f), by = 5f;
            XuiDraw.Begin(ui, ctrl);
            TooltipBox(bx, by, 10f + tw, 10f + th, new Color(0x60, 0x60, 0x60) * (0.8f * opacity));
            XuiDraw.End(ui);
            Matrix glyphs = Matrix.CreateScale(1f / Layout.SceneScale) * Matrix.CreateTranslation(bx + 5f, by + 5f, 0f) * ctrl;
            XuiDraw.Begin(ui, glyphs);
            f.Draw(ui.Batch, text, Vector2.Zero, new Color(0xEB, 0xEB, 0xEB) * opacity);
            XuiDraw.End(ui);
        }

        /// <summary>A rounded box: quarter discs at the corners, the edges and the middle.</summary>
        private void TooltipBox(float x, float y, float w, float h, Color c)
        {
            const float k = 5f;
            Texture2D px = ui.Pixel, disc = ui.Circle;
            SpriteBatch b = ui.Batch;
            Action<float, float, float, float> rect = delegate(float rx, float ry, float rw, float rh)
            {
                if (rw > 0f && rh > 0f) b.Draw(px, new Vector2(rx, ry), null, c, 0f, Vector2.Zero, new Vector2(rw, rh), SpriteEffects.None, 0f);
            };
            rect(x + k, y, w - 2 * k, h);                                     // the middle column, top to bottom
            rect(x, y + k, k, h - 2 * k);                                     // the left edge
            rect(x + w - k, y + k, k, h - 2 * k);                             // the right edge
            int q = disc.Width / 2;
            Vector2 s = new Vector2(k / q, k / q);
            b.Draw(disc, new Vector2(x, y), new Rectangle(0, 0, q, q), c, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
            b.Draw(disc, new Vector2(x + w - k, y), new Rectangle(q, 0, q, q), c, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
            b.Draw(disc, new Vector2(x, y + h - k), new Rectangle(0, q, q, q), c, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
            b.Draw(disc, new Vector2(x + w - k, y + h - k), new Rectangle(q, q, q, q), c, 0f, Vector2.Zero, s, SpriteEffects.None, 0f);
        }
    }
}
