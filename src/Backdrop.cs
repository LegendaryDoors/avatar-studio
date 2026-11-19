// The editor's background: the control pack's aura scene with the editor's background.jpg.
// Without the installed art a drawn stand-in takes its place.

using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class Backdrop
    {
        private readonly Texture2D stage;    // the stand-in: green field, horizon band, grey floor
        private readonly Texture2D field;    // the gallery's: the stand-in's field all the way down

        private Texture2D image;             // [xur] Back/ImgBG: the editor's background.jpg
        private Texture2D floor;             // Sphere/Color/floor's 128 texels, top to bottom
        private Texture2D vignetteTop, vignetteBottom;   // per screen row, 0..719

        /// <summary>The console's backdrop is in use (background.jpg installed).</summary>
        public bool IsConsoles { get { return image != null; } }

        public Backdrop(GraphicsDevice device)
        {
            int w = Layout.W, h = Layout.H;
            Color[] px = new Color[w * h];
            Color[] flat = new Color[w * h];
            Vector3 bright = new Vector3(0.62f, 0.86f, 0.18f);   // stand-in, by eye: upper-left yellow-green
            Vector3 deep = new Vector3(0.22f, 0.52f, 0.12f);   // stand-in, by eye: edge green
            Vector3 white = new Vector3(0.97f, 0.98f, 0.98f);
            Vector3 grey = new Vector3(0.80f, 0.81f, 0.83f);   // stand-in, by eye: light grey floor
            for (int y = 0; y < h; y++)
            {
                float fy = y / (float)h;
                for (int x = 0; x < w; x++)
                {
                    float fx = x / (float)w;
                    float d = (float)Math.Sqrt((fx - 0.28f) * (fx - 0.28f) * 1.4f + (fy - 0.18f) * (fy - 0.18f) * 2.2f);
                    Vector3 c = Vector3.Lerp(bright, deep, MathHelper.Clamp(d * 1.25f, 0, 1));
                    // halftone: a faint dot lattice, 6px pitch
                    float dx = (x % 6) - 2.5f, dy = (y % 6) - 2.5f;
                    float dot = MathHelper.Clamp(1.6f - (float)Math.Sqrt(dx * dx + dy * dy), 0, 1) * 0.10f;
                    c -= new Vector3(dot);
                    flat[y * w + x] = new Color(c);
                    // horizon band around 62%, floor below 70%
                    float band = MathHelper.Clamp((fy - 0.58f) / 0.06f, 0, 1);
                    c = Vector3.Lerp(c, white, band);
                    float fl = MathHelper.Clamp((fy - 0.70f) / 0.05f, 0, 1);
                    c = Vector3.Lerp(c, Vector3.Lerp(white, grey, MathHelper.Clamp((fy - 0.70f) / 0.30f, 0, 1)), fl);
                    px[y * w + x] = new Color(c);
                }
            }
            stage = new Texture2D(device, w, h);
            stage.SetData(px);
            field = new Texture2D(device, w, h);
            field.SetData(flat);
        }

        // [xur] AuraScene.xur's gradients: (straight '#AARRGGBB', position)
        private static readonly uint[] FloorColours = { 0x00CCCCCC, 0x000F0F0F, 0x00CCCCCC, 0xE1CCCCCC, 0xF5CCCCCC };
        private static readonly float[] FloorStops = { 0f, 0f, 0.26f, 0.75f, 1f };
        private static readonly Rectangle FloorRect = new Rectangle(-200, 0, 1680, 800);
        private static readonly uint[] BottomColours = { 0x000C0B12, 0x000F0F0F, 0x000C0B12, 0xBE0C0B12 };
        private static readonly float[] BottomStops = { 0f, 0f, 0.73f, 1f };
        private const float BottomOpacity = 0.4f;
        private static readonly uint[] TopColours = { 0x00000000, 0x06000000, 0xFF0F0F0F };
        private static readonly float[] TopStops = { 0f, 0.4f, 1f };
        private const float TopOpacity = 0.5f;

        /// <summary>The console's backdrop from the installed art; without it the stand-in stays.</summary>
        public void UseConsoles(GraphicsDevice device, string dir)
        {
            string path = dir == null ? null : Path.Combine(dir, "background.jpg");
            if (path == null || !File.Exists(path)) return;
            try
            {
                using (FileStream fs = File.OpenRead(path)) image = Texture2D.FromStream(device, fs);
            }
            catch (Exception e) { Console.WriteLine("[studio] background.jpg: " + e.Message); image = null; return; }

            floor = new Texture2D(device, 1, 128);
            floor.SetData(Bake128(FloorColours, FloorStops));
            Color[] top = new Color[Layout.H], bottom = new Color[Layout.H];
            for (int y = 0; y < Layout.H; y++)
            {
                float yc = y + 0.5f;                                  // the row's pixel centre
                top[y] = Shader(TopColours, TopStops, 1f - yc / 720f);
                bottom[y] = Shader(BottomColours, BottomStops, yc / 720f);
            }
            vignetteTop = new Texture2D(device, 1, Layout.H); vignetteTop.SetData(top);
            vignetteBottom = new Texture2D(device, 1, Layout.H); vignetteBottom.SetData(bottom);
        }

        private static Vector4 Channels(uint argb)
        {
            return new Vector4(((argb >> 16) & 255) / 255f, ((argb >> 8) & 255) / 255f, (argb & 255) / 255f, (argb >> 24) / 255f);
        }

        private static Color Straight(Vector4 c)
        {
            return new Color((byte)Math.Round(MathHelper.Clamp(c.X, 0f, 1f) * 255f), (byte)Math.Round(MathHelper.Clamp(c.Y, 0f, 1f) * 255f),
                             (byte)Math.Round(MathHelper.Clamp(c.Z, 0f, 1f) * 255f), (byte)Math.Round(MathHelper.Clamp(c.W, 0f, 1f) * 255f));
        }

        /// <summary>sub_921A88C0: a gradient baked into 128 texels.</summary>
        private static Color[] Bake128(uint[] colours, float[] stops)
        {
            Color[] px = new Color[128];
            int from = 0, to = 1;
            for (int i = 0; i < 128; i++)
            {
                float t = i / 127f, f;
                bool last = false;
                if (t > stops[to])
                {
                    from = to;
                    if (to + 1 >= stops.Length) last = true;
                    else to++;
                }
                if (last || to == from || stops[to] - stops[from] < 0.0001f) f = 1f;
                else f = t >= stops[from] ? (t - stops[from]) / (stops[to] - stops[from]) : 0f;
                Vector4 a = Channels(colours[from]), b = Channels(colours[last ? from : to]);
                px[i] = Straight(a + (b - a) * f);
            }
            return px;
        }

        /// <summary>Pixel shader mode 3: the gradient's colours lerped at u.</summary>
        private static Color Shader(uint[] colours, float[] stops, float u)
        {
            int n = stops.Length, s = u <= stops[1] ? 0 : (n > 2 && u <= stops[2]) || n == 3 ? 1 : 2;
            s = Math.Min(s, n - 2);
            float span = stops[s + 1] - stops[s];
            float f = MathHelper.Clamp(span > 0f ? (u - stops[s]) / span : 0f, 0f, 1f);
            Vector4 a = Channels(colours[s]), b = Channels(colours[s + 1]);
            return Straight(a + (b - a) * f);
        }

        /// <summary>The backdrop; `withFloor` false for the gallery's plain field.</summary>
        public void Draw(SpriteBatch sb, bool withFloor)
        {
            if (image == null)
            {
                sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
                sb.Draw(withFloor ? stage : field, new Rectangle(0, 0, Layout.W, Layout.H), Color.White);
                sb.End();
                return;
            }
            sb.Begin(SpriteSortMode.Deferred, BlendState.Opaque);
            sb.Draw(image, new Rectangle(0, 0, Layout.W, Layout.H), Color.White);       // Back/ImgBG
            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone);
            if (withFloor) sb.Draw(floor, FloorRect, Color.White);                          // Sphere/Color/floor
            sb.End();
            sb.Begin(SpriteSortMode.Deferred, BlendState.NonPremultiplied, SamplerState.PointClamp, DepthStencilState.None, RasterizerState.CullNone);
            Rectangle all = new Rectangle(0, 0, Layout.W, Layout.H);
            sb.Draw(vignetteBottom, all, new Color(1f, 1f, 1f, BottomOpacity));            // Front/Vignette_Bottom
            sb.Draw(vignetteTop, all, new Color(1f, 1f, 1f, TopOpacity));                  // Front/Vignette_Top
            sb.End();
        }
    }
}
