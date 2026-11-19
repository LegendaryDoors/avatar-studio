// Drawing primitives, the shared palette, and the editor's text styles (XuiStyle).

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    static class Palette
    {
        public static readonly Color XboxGreen = new Color(107, 190, 40);        // [documented] Xbox 360 brand green #6BBE28
        public static readonly Color SelectGreen = new Color(140, 210, 50);   // by eye: highlighted cell fill
        public static readonly Color TileLavender = new Color(126, 132, 168);   // by eye: unselected tile
        public static readonly Color TileLabelPlate = new Color(92, 96, 122);   // by eye
        public static readonly Color TileDim = new Color(70, 74, 96);
        public static readonly Color CellGrey = new Color(214, 216, 220);   // by eye: thumbnail cell background
        public static readonly Color CellDim = new Color(214, 216, 220) * 0.35f;
        public static readonly Color Text = Color.White;
        public static readonly Color TextDim = new Color(200, 204, 210);
        public static readonly Color TextGrey = new Color(120, 124, 130);
        public static readonly Color Panel = new Color(78, 80, 86);   // by eye: grey modal
        public static readonly Color NamePlate = new Color(30, 30, 34) * 0.85f;
        public static readonly Color CardArt = new Color(46, 50, 60);
        public static readonly Color CardSubtext = new Color(178, 182, 194);   // [ours] the date line on a card's plate
        public static readonly Color GlyphA = new Color(88, 176, 40);
        public static readonly Color GlyphB = new Color(216, 48, 40);
        public static readonly Color GlyphX = new Color(40, 100, 200);
        public static readonly Color GlyphY = new Color(232, 176, 24);
        public static readonly Color RemoveRed = new Color(214, 40, 32);
        public static readonly Color Magenta = new Color(232, 64, 200);
        /// <summary>[xur] DropShadowColor #800F0F0F, as written (not premultiplied).</summary>
        public static readonly Color DropShadow = new Color(0x0F, 0x0F, 0x0F, 0x80);

        /// <summary>A skin colour as written ('#AARRGGBB') as SpriteBatch's premultiplied Color.</summary>
        public static Color Argb(uint argb)
        {
            float a = (argb >> 24) / 255f;
            return new Color(((argb >> 16) & 255) / 255f * a, ((argb >> 8) & 255) / 255f * a, (argb & 255) / 255f * a, a);
        }

        /// <summary>[xur] the skin's text colour, #FFEBEBEB.</summary>
        public static readonly Color SceneText = Argb(0xFFEBEBEB);
    }

    /// <summary>One of the editor's text styles (EditorSkin.xur): PointSize, TextStyle, TextColor.</summary>
    struct XuiStyle
    {
        public readonly float Points;
        public readonly int Bits;
        public readonly Color Colour;

        public XuiStyle(float points, int bits, Color colour) { Points = points; Bits = bits; Colour = colour; }

        /// <summary>[ours] the style aligned otherwise across: 0 left, 1 centred (0x400), 2 right (0x200).</summary>
        public XuiStyle Across(int a)
        {
            int b = Bits & ~0x600;
            if (a == 1) b |= 0x400; else if (a == 2) b |= 0x200;
            return new XuiStyle(Points, b, Colour);
        }

        /// <summary>[ours] the style's block centred down its box (0x1000).</summary>
        public XuiStyle Down() { return new XuiStyle(Points, Bits | 0x1000, Colour); }

        /// <summary>[ours] the style from its box's top left: no centring across or down.</summary>
        public XuiStyle TopLeft() { return new XuiStyle(Points, Bits & ~(0x1000 | 0x8000000 | 0x600), Colour); }

        // category_header, a page's title: 24 pt, 0x1111 (shadowed, centred down), #FFEBEBEB
        public static readonly XuiStyle CategoryHeader = new XuiStyle(24f, 0x1111, Palette.SceneText);
        /// <summary>category_page_number, "Page n of m": 16 pt, 0x1110 (centred down, one line), #FFFFFFFF.</summary>
        public static readonly XuiStyle PageNumber = new XuiStyle(16f, 0x1110, Color.White);
        /// <summary>GridTopLevel's XuiTextPresenter1, a tile's label: 17 pt, 0x1400 (centred across and down), #FFEBEBEB.</summary>
        public static readonly XuiStyle TileLabel = new XuiStyle(17f, 0x1400, Palette.SceneText);
        /// <summary>DialogText, the yes / no dialog's and the busy box's: 18 pt, 0x1404 (bold, centred across and down), #FFEBEBEB.</summary>
        public static readonly XuiStyle DialogText = new XuiStyle(18f, 0x1404, Palette.SceneText);
        /// <summary>DialogTextLeftAlign, the outfits' tutorial's: 18 pt, 0x4000104 (bold, from the top left), #FFEBEBEB.</summary>
        public static readonly XuiStyle DialogTextLeftAlign = new XuiStyle(18f, 0x4000104, Palette.SceneText);
        /// <summary>marketplace_category_page_number: 15 pt, 0x1411, #FFEBEBEB.</summary>
        public static readonly XuiStyle SmallText = new XuiStyle(15f, 0x1411, Palette.SceneText);
    }

    sealed class Ui
    {
        public readonly GraphicsDevice Device;
        public readonly SpriteBatch Batch;
        public readonly Texture2D Pixel;
        public readonly Texture2D Circle;     // 64x64 soft disc
        public readonly Texture2D Ring;       // 256x256 turntable ring
        public readonly Texture2D RoundRect;  // 64x64 rounded square, 12px radius
        public readonly Texture2D Check;      // 48x48 tick in a disc
        public readonly Texture2D Cross;      // 48x48 X
        /// <summary>The cells' tooltips: 14 pt at 96 dpi, 18.667 scene units.</summary>
        public readonly RuntimeFont Tooltip;

        private readonly System.Collections.Generic.Dictionary<long, RuntimeFont> xuiFonts = new System.Collections.Generic.Dictionary<long, RuntimeFont>();

        /// <summary>A scene text's font at its PointSize and TextStyle (96 x pt / 72 pixels to the em).</summary>
        public RuntimeFont Xui(float pointSize, int textStyle = 0)
        {
            int flags = ((textStyle & 2) != 0 ? 1 : 0) | ((textStyle & 4) != 0 ? 2 : 0);
            long key = ((long)BitConverter.ToInt32(BitConverter.GetBytes(pointSize), 0) << 2) | (uint)flags;
            RuntimeFont f;
            if (!xuiFonts.TryGetValue(key, out f))
                xuiFonts[key] = f = new RuntimeFont(Device, "Segoe UI", ConsoleFace.PixelSize(pointSize), false, flags);
            return f;
        }

        private readonly System.Collections.Generic.Dictionary<string, RuntimeFont> faceFonts = new System.Collections.Generic.Dictionary<string, RuntimeFont>();

        /// <summary>A text's font in another typeface: the dashboard's Segoe Light when read, else GDI+'s.</summary>
        public RuntimeFont Font(XuiFace face, string family, bool light, float pointSize, int textStyle = 0)
        {
            int flags = ((textStyle & 2) != 0 ? 1 : 0) | ((textStyle & 4) != 0 ? 2 : 0);
            string key = (face != null ? "face" + face.GetHashCode() : family + (light ? " Light" : "")) + "|" + pointSize.ToString("R") + "|" + flags;
            RuntimeFont f;
            if (!faceFonts.TryGetValue(key, out f))
                faceFonts[key] = f = new RuntimeFont(Device, face, family, ConsoleFace.PixelSize(pointSize), light, flags);
            return f;
        }

        /// <summary>[ours] The studio's own screens draw the editor's styles at scnMain's 0.88.</summary>
        public const float OwnScale = Layout.SceneScale;

        /// <summary>`text` in `s` in a screen-pixel box, wrapped unless the style keeps one line.</summary>
        public void Text(XuiStyle s, string text, float x, float y, float w, float h, Color? colour = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            RuntimeFont f = Xui(s.Points, s.Bits);
            float ew = w / OwnScale, eh = h / OwnScale;
            string t = (s.Bits & 0x30) != 0 ? text : f.WrapXui(text, ew);
            f.DrawXui(Batch, t, new Vector2(x, y), OwnScale, ew, eh, s.Bits, colour ?? s.Colour, Palette.DropShadow);
        }

        public void Text(XuiStyle s, string text, Rectangle box, Color? colour = null)
        {
            Text(s, text, box.X, box.Y, box.Width, box.Height, colour);
        }

        /// <summary>A short text centred on `c` (screen pixels), the style centred across and down.</summary>
        public void TextAt(XuiStyle s, string text, Vector2 c, Color? colour = null)
        {
            XuiStyle cs = s.Across(1).Down();
            Text(cs, text, c.X - 300f, c.Y - 50f, 600f, 100f, colour);
        }

        /// <summary>A line's width in screen pixels as `s` lays it out (whole-pixel advances).</summary>
        public float TextWidth(XuiStyle s, string line) { return Xui(s.Points, s.Bits).XuiWidth(line) * OwnScale; }

        /// <summary>One line's height in screen pixels in `s`.</summary>
        public float LineHeight(XuiStyle s) { return (float)Math.Ceiling(Xui(s.Points, s.Bits).LineHeight) * OwnScale; }

        public Ui(GraphicsDevice device)
        {
            Device = device;
            Batch = new SpriteBatch(device);
            Pixel = new Texture2D(device, 1, 1); Pixel.SetData(new[] { Color.White });
            // a filled disc
            Circle = Procedural(device, 64, 64, (x, y) => Soft(31f - Dist(x, y, 32, 32) + 0.5f));
            Ring = Procedural(device, 256, 256, (x, y) =>
            {
                float d = Dist(x, y, 128, 128) / 128f;
                float ring = Soft((0.98f - d) / 0.03f) * (1 - Soft((0.86f - d) / 0.03f));
                float disc = Soft((0.86f - d) / 0.02f) * 0.35f;
                return Math.Min(1f, ring + disc);
            });
            RoundRect = Procedural(device, 64, 64, (x, y) => RoundedMask(x, y, 64, 64, 12));
            Check = Procedural(device, 48, 48, (x, y) =>
            {
                float disc = Soft((23 - Dist(x, y, 24, 24)) / 1.5f);
                bool tick = Math.Abs((y - 26) - (x - 14) * 0.9f) < 3.2f && x >= 13 && x <= 22
                         || Math.Abs((y - 33) + (x - 22) * 1.15f) < 3.2f && x >= 21 && x <= 36;
                return disc * (tick ? 0f : 1f);
            });
            Cross = Procedural(device, 48, 48, (x, y) =>
            {
                float a = Math.Abs((x - 24) - (y - 24)), b = Math.Abs((x - 24) + (y - 24));
                bool inside = Math.Abs(x - 24) < 18 && Math.Abs(y - 24) < 18;
                return inside && (a < 4.5f || b < 4.5f) ? 1f : 0f;
            });
            Tooltip = new RuntimeFont(device, "Segoe UI", 96f * 14f / 72f * Layout.SceneScale, false);
        }

        // ---- primitives -----------------------------------------------------------------------
        public void Rect(Rectangle r, Color c) { Batch.Draw(Pixel, r, c); }

        public void Rounded(Rectangle r, Color c, int corner = 12)
        {
            // nine-slice of the 64x64 rounded mask with 12px corners, drawn `corner` px on screen
            const int k = 12;
            int d = corner;
            int x0 = r.X, x1 = r.X + d, x2 = r.Right - d, y0 = r.Y, y1 = r.Y + d, y2 = r.Bottom - d;
            Slice(new Rectangle(x0, y0, d, d), new Rectangle(0, 0, k, k), c);
            Slice(new Rectangle(x1, y0, x2 - x1, d), new Rectangle(k, 0, 64 - 2 * k, k), c);
            Slice(new Rectangle(x2, y0, d, d), new Rectangle(64 - k, 0, k, k), c);
            Slice(new Rectangle(x0, y1, d, y2 - y1), new Rectangle(0, k, k, 64 - 2 * k), c);
            Slice(new Rectangle(x1, y1, x2 - x1, y2 - y1), new Rectangle(k, k, 64 - 2 * k, 64 - 2 * k), c);
            Slice(new Rectangle(x2, y1, d, y2 - y1), new Rectangle(64 - k, k, k, 64 - 2 * k), c);
            Slice(new Rectangle(x0, y2, d, d), new Rectangle(0, 64 - k, k, k), c);
            Slice(new Rectangle(x1, y2, x2 - x1, d), new Rectangle(k, 64 - k, 64 - 2 * k, k), c);
            Slice(new Rectangle(x2, y2, d, d), new Rectangle(64 - k, 64 - k, k, k), c);
        }

        /// <summary>[ours] A keyboard key, drawn where the console shows a controller button.</summary>
        public float KeyCap(Vector2 at, float height, string label, float opacity, bool draw = true)
        {
            RuntimeFont f = Xui(12f);
            float w = Math.Max(height, (float)Math.Ceiling((double)f.XuiWidth(label)) + 12f);
            if (!draw) return w;
            Rectangle outer = new Rectangle((int)Math.Round(at.X), (int)Math.Round(at.Y), (int)Math.Round(w), (int)Math.Round(height));
            Rounded(outer, new Color(235, 235, 235) * (0.95f * opacity), 6);
            Rounded(new Rectangle(outer.X + 2, outer.Y + 2, outer.Width - 4, outer.Height - 4), new Color(38, 38, 38) * (0.92f * opacity), 5);
            f.DrawXui(Batch, label, new Vector2(outer.X, outer.Y), 1f, outer.Width, outer.Height, 0x1111 | 0x400, Color.White * opacity, Palette.DropShadow);
            return w;
        }
        private void Slice(Rectangle dst, Rectangle src, Color c) { if (dst.Width > 0 && dst.Height > 0) Batch.Draw(RoundRect, dst, src, c); }

        /// <summary>XuiNineGrid: corners unstretched, edges stretched one way, the centre both.</summary>
        public void NineGrid(Texture2D t, Rectangle dst, int l, int top, int r, int b, float scale, Color c, bool noCenter = false)
        {
            int dl = (int)Math.Round(l * scale), dr = (int)Math.Round(r * scale);
            int dt = (int)Math.Round(top * scale), db = (int)Math.Round(b * scale);
            if (dl + dr > dst.Width) { float k = dst.Width / (float)(dl + dr); dl = (int)(dl * k); dr = dst.Width - dl; }
            if (dt + db > dst.Height) { float k = dst.Height / (float)(dt + db); dt = (int)(dt * k); db = dst.Height - dt; }
            int[] sx = { 0, l, t.Width - r, t.Width }, sy = { 0, top, t.Height - b, t.Height };
            int[] dx = { dst.X, dst.X + dl, dst.Right - dr, dst.Right }, dy = { dst.Y, dst.Y + dt, dst.Bottom - db, dst.Bottom };
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    Rectangle d = new Rectangle(dx[i], dy[j], dx[i + 1] - dx[i], dy[j + 1] - dy[j]);
                    Rectangle s = new Rectangle(sx[i], sy[j], sx[i + 1] - sx[i], sy[j + 1] - sy[j]);
                    if (noCenter && i == 1 && j == 1) continue;                   // XuiNineGrid's NoCenter
                    if (d.Width > 0 && d.Height > 0 && s.Width > 0 && s.Height > 0) Batch.Draw(t, d, s, c);
                }
        }

        public void Outline(Rectangle r, int t, Color c)
        {
            Rect(new Rectangle(r.X, r.Y, r.Width, t), c);
            Rect(new Rectangle(r.X, r.Bottom - t, r.Width, t), c);
            Rect(new Rectangle(r.X, r.Y, t, r.Height), c);
            Rect(new Rectangle(r.Right - t, r.Y, t, r.Height), c);
        }

        public void Disc(Vector2 centre, float radius, Color c)
        {
            Batch.Draw(Circle, new Rectangle((int)(centre.X - radius), (int)(centre.Y - radius), (int)(radius * 2), (int)(radius * 2)), c);
        }

        public void Icon(Texture2D tex, Vector2 centre, float size, Color c)
        {
            Batch.Draw(tex, new Rectangle((int)(centre.X - size / 2), (int)(centre.Y - size / 2), (int)size, (int)size), c);
        }

        public void Spinner(Vector2 centre, float radius, float t, Color c)
        {
            // eight dots around a ring, one bright, chasing
            for (int i = 0; i < 8; i++)
            {
                float a = i / 8f * MathHelper.TwoPi;
                float phase = ((t * 1.6f) - i / 8f) % 1f; if (phase < 0) phase += 1f;
                float k = 0.25f + 0.75f * (1 - phase);
                Disc(centre + new Vector2((float)Math.Cos(a), (float)Math.Sin(a)) * radius, radius * 0.16f, c * k);
            }
        }

        /// <summary>Cleared while a screen is being drawn underneath a modal that has its own legend.</summary>
        public bool LegendEnabled = true;

        /// <summary>The button legend (LegendBar.cs).</summary>
        public LegendBar Bar;

        /// <summary>This frame's legend ("A Select", ...): recorded now, drawn once by StudioGame.</summary>
        public void Legend(params string[] items)
        {
            if (!LegendEnabled || Bar == null) return;
            Bar.Set(items);
        }

        public void BeginLegend() { if (Bar != null) Bar.Clear(); }

        public void DrawLegend() { if (Bar != null) Bar.Draw(this); }

        // ---- procedural textures ----------------------------------------------------------------
        private static Texture2D Procedural(GraphicsDevice d, int w, int h, Func<int, int, float> alpha)
        {
            Color[] px = new Color[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float a = MathHelper.Clamp(alpha(x, y), 0, 1);
                    px[y * w + x] = Color.White * a;                 // premultiplied
                }
            Texture2D t = new Texture2D(d, w, h);
            t.SetData(px);
            return t;
        }
        private static float Dist(int x, int y, float cx, float cy) { float dx = x + 0.5f - cx, dy = y + 0.5f - cy; return (float)Math.Sqrt(dx * dx + dy * dy); }
        private static float Soft(float v) { return MathHelper.Clamp(v, 0, 1); }
        private static float RoundedMask(int x, int y, int w, int h, int r)
        {
            float cx = MathHelper.Clamp(x + 0.5f, r, w - r), cy = MathHelper.Clamp(y + 0.5f, r, h - r);
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            return Soft(r - (float)Math.Sqrt(dx * dx + dy * dy) + 0.5f);
        }
    }
}
