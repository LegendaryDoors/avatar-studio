// A SpriteBatch font rasterised at start-up: the console's face when installed, else GDI+'s.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace AvatarStudio
{
    sealed class RuntimeFont : IDisposable
    {
        private struct Glyph { public Rectangle Src; public float Advance; public int OffsetX, OffsetY; }

        private readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>();
        private Texture2D atlas;
        public float LineHeight { get; private set; }
        public float Size { get; private set; }

        /// <summary>True when this font draws the console's face (ConsoleFace.cs), not GDI+'s.</summary>
        public bool IsConsoleFace { get; private set; }

        /// <summary>`family` and `light` only choose the GDI+ fallback.</summary>
        public RuntimeFont(GraphicsDevice device, string family, float pixelSize, bool light) : this(device, family, pixelSize, light, 0) { }

        /// <summary>`providerFlags`: 1 shear, 2 embolden (TextStyle 0x2 / 0x4); GDI+ ignores them.</summary>
        public RuntimeFont(GraphicsDevice device, string family, float pixelSize, bool light, int providerFlags)
            : this(device, ConsoleFace.Load() ? ConsoleFace.Face : null, family, pixelSize, light, providerFlags) { }

        /// <summary>From `consoleFace` when not null, else GDI+'s `family`.</summary>
        public RuntimeFont(GraphicsDevice device, XuiFace consoleFace, string family, float pixelSize, bool light, int providerFlags)
        {
            Size = pixelSize;
            if (consoleFace != null) { BuildConsole(device, consoleFace, pixelSize, providerFlags); return; }
            FontStyle style = FontStyle.Regular;
            string face = light ? family + " Light" : family;
            Font font;
            try { font = new Font(face, pixelSize, style, GraphicsUnit.Pixel); }
            catch { font = new Font(FontFamily.GenericSansSerif, pixelSize, style, GraphicsUnit.Pixel); }
            if (light && !string.Equals(font.Name, face, StringComparison.OrdinalIgnoreCase))
            {
                // No "Light" cut installed: fall back to the regular weight of the same family.
                try { font = new Font(family, pixelSize, style, GraphicsUnit.Pixel); } catch { }
            }
            Build(device, font);
        }

        private void Build(GraphicsDevice device, Font font)
        {
            List<char> chars = new List<char>();
            for (int c = 32; c < 127; c++) chars.Add((char)c);
            for (int c = 160; c < 256; c++) chars.Add((char)c);

            int cell = (int)Math.Ceiling(font.GetHeight()) + 4;
            LineHeight = font.GetHeight();
            int cols = 16, rows = (chars.Count + cols - 1) / cols;
            int cellW = cell + (int)font.Size;           // wide glyphs (W, @) need room
            int atlasW = cols * cellW, atlasH = rows * cell;

            using (Bitmap bmp = new Bitmap(atlasW, atlasH, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(System.Drawing.Color.Transparent);
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    StringFormat fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    for (int i = 0; i < chars.Count; i++)
                    {
                        char ch = chars[i];
                        int x = (i % cols) * cellW, y = (i / cols) * cell;
                        string s = ch.ToString();
                        SizeF sz = g.MeasureString(s, font, 4096, fmt);
                        float adv = ch == ' ' ? sz.Width : sz.Width;
                        g.DrawString(s, font, Brushes.White, x + 2, y + 2, fmt);
                        Glyph gl;
                        gl.Src = new Rectangle(x, y, Math.Min(cellW, (int)Math.Ceiling(sz.Width) + 4), cell);
                        gl.Advance = adv;
                        gl.OffsetX = -2; gl.OffsetY = -2;
                        glyphs[ch] = gl;
                    }
                }
                BitmapData data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, atlasW, atlasH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] raw = new byte[atlasW * atlasH * 4];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, raw, 0, raw.Length);
                bmp.UnlockBits(data);
                // BGRA straight -> RGBA premultiplied (SpriteBatch's default AlphaBlend expects it).
                for (int i = 0; i < raw.Length; i += 4)
                {
                    byte b = raw[i], gch = raw[i + 1], r = raw[i + 2], a = raw[i + 3];
                    raw[i] = (byte)(r * a / 255); raw[i + 1] = (byte)(gch * a / 255); raw[i + 2] = (byte)(b * a / 255); raw[i + 3] = a;
                }
                atlas = new Texture2D(device, atlasW, atlasH, false, SurfaceFormat.Color);
                atlas.SetData(raw);
            }
            font.Dispose();
        }

        /// <summary>The atlas from the console's face: cells as XUI_Font_GetGlyph places them, advances whole.</summary>
        private void BuildConsole(GraphicsDevice device, XuiFace face, float px, int flags)
        {
            IsConsoleFace = true;
            LineHeight = ConsoleFace.LineHeight(face, px, flags);
            List<KeyValuePair<char, ConsoleFace.Cell>> cells = new List<KeyValuePair<char, ConsoleFace.Cell>>();
            for (int c = 32; c < 256; c++)
            {
                if (c >= 127 && c < 160) continue;
                ConsoleFace.Cell cell;
                if (ConsoleFace.Render(face, c, px, flags, out cell)) cells.Add(new KeyValuePair<char, ConsoleFace.Cell>((char)c, cell));
            }
            // shelf packing, one pixel apart
            const int atlasW = 512;
            int x = 0, y = 0, shelf = 0;
            Rectangle[] at = new Rectangle[cells.Count];
            for (int i = 0; i < cells.Count; i++)
            {
                ConsoleFace.Cell cl = cells[i].Value;
                if (x + cl.W > atlasW) { x = 0; y += shelf + 1; shelf = 0; }
                at[i] = new Rectangle(x, y, cl.W, cl.H);
                x += cl.W + 1; shelf = Math.Max(shelf, cl.H);
            }
            int atlasH = Math.Max(1, y + shelf);
            byte[] raw = new byte[atlasW * atlasH * 4];
            for (int i = 0; i < cells.Count; i++)
            {
                ConsoleFace.Cell cl = cells[i].Value;
                for (int r = 0; r < cl.H; r++)
                    for (int q = 0; q < cl.W; q++)
                    {
                        byte a = cl.Alpha[r * cl.W + q];
                        int o = ((at[i].Y + r) * atlasW + at[i].X + q) * 4;
                        raw[o] = raw[o + 1] = raw[o + 2] = raw[o + 3] = a;      // white, premultiplied
                    }
                Glyph g;
                g.Src = at[i]; g.Advance = cl.Advance; g.OffsetX = cl.OffsetX; g.OffsetY = cl.OffsetY;
                glyphs[cells[i].Key] = g;
            }
            atlas = new Texture2D(device, atlasW, atlasH, false, SurfaceFormat.Color);
            atlas.SetData(raw);
        }

        public Vector2 Measure(string text)
        {
            float w = 0, maxW = 0; int lines = 1;
            foreach (char ch in text)
            {
                if (ch == '\n') { lines++; maxW = Math.Max(maxW, w); w = 0; continue; }
                Glyph g; if (glyphs.TryGetValue(ch, out g)) w += g.Advance; else w += Size * 0.5f;
            }
            return new Vector2(Math.Max(maxW, w), lines * LineHeight);
        }

        public void Draw(SpriteBatch sb, string text, Vector2 pos, Color color)
        {
            float x = pos.X, y = pos.Y;
            foreach (char ch in text)
            {
                if (ch == '\n') { y += LineHeight; x = pos.X; continue; }
                Glyph g;
                if (!glyphs.TryGetValue(ch, out g)) { x += Size * 0.5f; continue; }
                if (g.Src.Width > 0) sb.Draw(atlas, new Vector2(x + g.OffsetX, y + g.OffsetY), g.Src, color);
                x += g.Advance;
            }
        }

        public void DrawCentred(SpriteBatch sb, string text, Vector2 centre, Color color)
        {
            Vector2 sz = Measure(text);
            Draw(sb, text, new Vector2((float)Math.Round(centre.X - sz.X / 2), (float)Math.Round(centre.Y - sz.Y / 2)), color);
        }

        // ---- XUI's drop shadow: one unit right and down, for TextStyle bit 0x1 (sub_921AD768) ----

        /// <summary>The text with XUI's drop shadow; `unit` is one of the text's units in the batch's space.</summary>
        public void DrawShadowed(SpriteBatch sb, string text, Vector2 pos, Color color, Color dropShadow, float unit)
        {
            Draw(sb, text, pos + new Vector2(unit, unit), ShadowOf(color, dropShadow));
            Draw(sb, text, pos, color);
        }

        public void DrawCentredShadowed(SpriteBatch sb, string text, Vector2 centre, Color color, Color dropShadow, float unit)
        {
            Vector2 sz = Measure(text);
            DrawShadowed(sb, text, new Vector2((float)Math.Round(centre.X - sz.X / 2), (float)Math.Round(centre.Y - sz.Y / 2)),
                         color, dropShadow, unit);
        }

        // ---- XUI's text layout (XUI_DrawTextFlags 0x921B07A8, XUI_Font_BreakLine) ----------------

        /// <summary>Wrapped `text` laid out as XUI_DrawTextFlags does, in a w x h element at `origin`.</summary>
        public void DrawXui(SpriteBatch sb, string text, Vector2 origin, float scale, float w, float h, int style,
                            Color color, Color dropShadow, int lineSpacing = 0)
        {
            string[] lines = text.Split('\n');
            int W = (int)w, H = (int)h;
            int lh = (int)Math.Ceiling(LineHeight) + lineSpacing;
            int top = 0;
            if ((style & 0x8001000) != 0)
            {
                int v = H - 1 - lines.Length * lh;
                if ((style & 0x1000) != 0) v /= 2;
                top = Math.Max(v, 0);
            }
            if ((style & 1) != 0) XuiPass(sb, lines, origin, scale, W, top, lh, style, ShadowOf(color, dropShadow), 1);
            XuiPass(sb, lines, origin, scale, W, top, lh, style, color, 0);
        }

        private void XuiPass(SpriteBatch sb, string[] lines, Vector2 origin, float scale, int W, int top, int lh, int style, Color color, int d)
        {
            for (int k = 0; k < lines.Length; k++)
            {
                int width = XuiWidth(lines[k]);
                int pen = (style & 0x400) != 0 ? (W - width) / 2 : (style & 0x200) != 0 ? W - width : 0;
                int y = top + k * lh;
                foreach (char ch in lines[k])
                {
                    Glyph g;
                    if (!glyphs.TryGetValue(ch, out g)) { pen += (int)(Size * 0.5f); continue; }
                    if (g.Src.Width > 0)
                        sb.Draw(atlas, origin + new Vector2(pen + g.OffsetX + d, y + g.OffsetY + d) * scale, g.Src, color,
                                0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
                    pen += (int)Math.Ceiling(g.Advance);
                }
            }
        }

        /// <summary>Wrapped to `w` as XUI_Font_BreakLine does: by the drawn, whole-pixel width.</summary>
        public string WrapXui(string text, float w)
        {
            string[] paras = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < paras.Length; i++)
            {
                if (paras[i].Length == 0) continue;
                List<string> lines = new List<string>();
                string cur = "";
                foreach (string word in paras[i].TrimEnd(' ').Split(' '))
                {
                    string t = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length == 0 || XuiWidth(t) <= w) cur = t;
                    else { lines.Add(cur); cur = word; }
                }
                if (cur.Length > 0) lines.Add(cur);
                paras[i] = string.Join("\n", lines.ToArray());
            }
            return string.Join("\n", paras);
        }

        /// <summary>A line's width as XUI sums it: whole-pixel advances.</summary>
        public int XuiWidth(string line)
        {
            int w = 0;
            foreach (char ch in line)
            {
                Glyph g;
                w += glyphs.TryGetValue(ch, out g) ? (int)Math.Ceiling(g.Advance) : (int)(Size * 0.5f);
            }
            return w;
        }

        /// <summary>The shadow's colour, premultiplied: DropShadowColor at its alpha x the text's (0x921ADAB8).</summary>
        public static Color ShadowOf(Color text, Color dropShadow)
        {
            float a = dropShadow.A / 255f * (text.A / 255f);
            return new Color(dropShadow.R / 255f * a, dropShadow.G / 255f * a, dropShadow.B / 255f * a, a);
        }

        /// <summary>Like DrawCentred, but each line centred on its own.</summary>
        public void DrawCentredBlock(SpriteBatch sb, string text, Vector2 centre, Color color)
        {
            string[] lines = text.Split('\n');
            float y = (float)Math.Round(centre.Y - lines.Length * LineHeight / 2f);
            foreach (string line in lines)
            {
                Draw(sb, line, new Vector2((float)Math.Round(centre.X - Measure(line).X / 2), y), color);
                y += LineHeight;
            }
        }

        public void DrawRight(SpriteBatch sb, string text, Vector2 rightTop, Color color)
        {
            Vector2 sz = Measure(text);
            Draw(sb, text, new Vector2((float)Math.Round(rightTop.X - sz.X), rightTop.Y), color);
        }

        /// <summary>Word-wrap into at most `maxLines` lines of `maxWidth` pixels.</summary>
        public string Wrap(string text, float maxWidth, int maxLines)
        {
            string[] words = text.Split(' ');
            List<string> lines = new List<string>();
            string cur = "";
            foreach (string w in words)
            {
                string t = cur.Length == 0 ? w : cur + " " + w;
                if (Measure(t).X <= maxWidth || cur.Length == 0) cur = t;
                else { lines.Add(cur); cur = w; }
            }
            if (cur.Length > 0) lines.Add(cur);
            if (lines.Count > maxLines) lines.RemoveRange(maxLines, lines.Count - maxLines);
            return string.Join("\n", lines.ToArray());
        }

        public void Dispose() { if (atlas != null) atlas.Dispose(); }
    }
}
