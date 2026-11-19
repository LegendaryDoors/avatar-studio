// Pieces of the editor's screens from its scene files: the turntable prompt, the page-turn
// handles and the floor shadow under each column.

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    static class Chrome
    {
        // ---- the turntable prompt: AvatarRotateHandle.xur ---------------------------------------

        public struct Host
        {
            public Vector2 Pos, Size, Pivot;
            public float Scale;
        }

        public static readonly Host PickerHost = new Host { Pos = new Vector2(0f, 580.72f), Size = new Vector2(1018f, 63f), Pivot = Vector2.Zero, Scale = 1f };        // [xur] Grid.xur
        public static readonly Host GridHost = new Host { Pos = new Vector2(4.33333f, 577.667f), Size = new Vector2(300f, 60f), Pivot = new Vector2(150f, 30f), Scale = 0.8f }; // [xur] GridMain.xur

        private const float HandleW = 480f;

        /// <summary>A point in AvatarRotateHandle's own space, on screen, for a given host.</summary>
        private static Vector2 Map(Host h, Vector2 p)
        {
            Vector2 c = p + new Vector2((h.Size.X - HandleW) / 2f, 0f);     // [inferred] centred, top-aligned
            c = h.Pivot + (c - h.Pivot) * h.Scale;
            return Layout.Scene(h.Pos.X + c.X, h.Pos.Y + c.Y);
        }

        private static Rectangle MapRect(Host h, float x, float y, float w, float hgt)
        {
            Vector2 a = Map(h, new Vector2(x, y)), b = Map(h, new Vector2(x + w, y + hgt));
            return new Rectangle((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y));
        }

        /// <summary>LegendTriggers' own transform: Scale 0.8 about (240, 37.5).</summary>
        private static Rectangle TabRect(Host h, float x)
        {
            Vector2 piv = new Vector2(240f, 37.5f);
            Vector2 a = piv + (new Vector2(x, 0f) - piv) * 0.8f;
            return MapRect(h, a.X, a.Y, 65f * 0.8f, 75f * 0.8f);
        }

        /// <summary>`opacity` is its scene's, where the prompt sits in a moving `items` (GridMain).</summary>
        public static void RotateHandle(Ui ui, TileArt art, Host h, bool glowLeft, bool glowRight, float opacity = 1f)
        {
            Texture2D bar = art.Named("rotate_bar");
            Texture2D lt = art.Named("legend_LT"), rt = art.Named("legend_RT"), glow = art.Named("trigger_glow");
            Rectangle band = MapRect(h, 53f + 22f, -26.4561f + 0.004395f + 19f, 328f, 72f);
            Rectangle l = TabRect(h, 80f), r = TabRect(h, 335f);
            Color c = Color.White * opacity;
            // [ours] on the keyboard the tabs are Q and E ([ and ] work too)
            bool keys = Input.KeysShown;
            if (bar == null || lt == null || rt == null)
            {
                // [ours] no art installed: the two words where the tabs go
                ui.TextAt(XuiStyle.SmallText, keys ? "Q" : "LT", new Vector2(l.Center.X, l.Center.Y), Palette.TextGrey * opacity);
                ui.TextAt(XuiStyle.SmallText, keys ? "E" : "RT", new Vector2(r.Center.X, r.Center.Y), Palette.TextGrey * opacity);
                return;
            }
            ui.Batch.Draw(bar, band, c * 0.75f);                            // [xur] `left` Opacity 0.75
            // Button_Highlight draws under Button_Image while that trigger is held; LT's is mirrored.
            if (glow != null && glowLeft) ui.Batch.Draw(glow, l, null, c, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 0f);
            if (glow != null && glowRight) ui.Batch.Draw(glow, r, c);
            if (keys)
            {
                const float kh = 30f;
                ui.KeyCap(new Vector2(l.Center.X - kh / 2f, l.Center.Y - kh / 2f), kh, "Q", opacity);
                ui.KeyCap(new Vector2(r.Center.X - kh / 2f, r.Center.Y - kh / 2f), kh, "E", opacity);
                return;
            }
            ui.Batch.Draw(lt, l, c);
            ui.Batch.Draw(rt, r, c);
        }

        // ---- page-turn handles: PageRightRadon / PageLeftRadon (EditorSkin.xur) ------------------

        public static Rectangle PageLeft { get { return Layout.PagePrevButton; } }
        public static Rectangle PageRight { get { return Layout.PageNextButton; } }

        /// <summary>`seconds` is the handles' scene's age; hidden while a page change runs.</summary>
        public static void PageHandles(Ui ui, TileArt art, bool hasPrev, bool hasNext, float seconds, float opacity = 1f)
        {
            PageHandles(ui, art, hasPrev, hasNext, seconds, opacity, PageLeft, PageRight);
        }

        /// <summary>The same at another scene's places (Grid2x2Tall.xur's PageTurnHandles group).</summary>
        public static void PageHandles(Ui ui, TileArt art, bool hasPrev, bool hasNext, float seconds, float opacity, Rectangle left, Rectangle right)
        {
            Texture2D t = art.Named("page_turn_handle_radon_right");
            if (t == null)
            {
                // [ours] without the art: plates with an arrow, as a tile's label
                if (hasPrev) { ui.Rounded(left, Palette.Panel * (0.8f * opacity)); ui.TextAt(XuiStyle.TileLabel, "<", new Vector2(left.Center.X, left.Center.Y), Palette.SceneText * opacity); }
                if (hasNext) { ui.Rounded(right, Palette.Panel * (0.8f * opacity)); ui.TextAt(XuiStyle.TileLabel, ">", new Vector2(right.Center.X, right.Center.Y), Palette.SceneText * opacity); }
                return;
            }
            float nudge = HandleNudge(seconds) * Layout.SceneScale;     // XuiImage1's x, through scnMain
            Rectangle l = left, r = right;
            l.Offset((int)Math.Round(-nudge), 0);                       // PageLeftRadon: -10
            r.Offset((int)Math.Round(nudge), 0);
            if (hasPrev) ui.Batch.Draw(t, l, null, Color.White * opacity, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 0f);
            if (hasNext) ui.Batch.Draw(t, r, Color.White * opacity);
        }

        /// <summary>The visual's Normal timeline: x held at 0 to frame 105, then nudged 10 and back.</summary>
        public static float HandleNudge(float seconds)
        {
            float f = (seconds * 60f) % 145f;
            if (f <= 105f) return 0f;
            float u = ((f - 105f) % 20f) / 10f;          // 0..2 over each 0 -> 10 -> 0
            return u <= 1f ? 10f * u : 10f * (2f - u);
        }

        /// <summary>The lineup's scroll handles (EditorStart.xur): 60 x 60 at Scale 0.8 about their centres.</summary>
        public static void LineupHandles(Ui ui, TileArt art, float seconds)
        {
            Texture2D t = art.Named("page_turn_handle_radon_right");
            if (t == null) return;
            float k = Layout.LineupHandleScale, nudge = HandleNudge(seconds) * k;
            Vector2 size = new Vector2(t.Width, t.Height) * k;
            Vector2 next = Layout.LineupNextHandle + new Vector2(nudge, 0f) - size / 2f;
            Vector2 prev = Layout.LineupPrevHandle + new Vector2(-nudge - 3f * k, 0f) - size / 2f;
            ui.Batch.Draw(t, new Rectangle((int)Math.Round(next.X), (int)Math.Round(next.Y), (int)Math.Round(size.X), (int)Math.Round(size.Y)), Color.White);
            ui.Batch.Draw(t, new Rectangle((int)Math.Round(prev.X), (int)Math.Round(prev.Y), (int)Math.Round(size.X), (int)Math.Round(size.Y)),
                          null, Color.White, 0f, Vector2.Zero, SpriteEffects.FlipHorizontally, 0f);
        }

        // ---- the floor shadow: shadow_item / shadow_EditorBoxart (EditorSkin.xur) ---------------
        // A nine-grid BackgroundShadow at y 619, 36 tall, of editor_mainUiShadow.png.

        public static void FloorShadow(Ui ui, TileArt art, float sceneX, float sceneW)
        {
            Texture2D t = art.Named("editor_mainUiShadow");
            if (t == null) return;
            Rectangle dst = Layout.SceneRect(sceneX, 619f, sceneW, 36f);
            ui.NineGrid(t, dst, 32, 16, 32, 16, Layout.SceneScale, Color.White);
        }
    }
}
