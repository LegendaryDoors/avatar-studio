// The neighbouring pages' cells at the screen's edges, as the editor draws them.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class Ghosts
    {
        public struct Spec
        {
            public Rectangle Visual;     // the 128 x 128 visual on screen
            public string Key;           // what is composed in it
            public bool Colour;          // a colour item: group opacity 0.4, else 0.5
            public Color Fill;           // GhostColour of colour 0
            public Texture2D Picture;    // an item's picture (null while it renders)
            public bool Boxart;          // GriBoxArtHeightOffscreenButton (My Outfits), 180 x 235
        }

        private const int Size = 128;                       // GridOffscreenItemButton, 128 x 128
        private readonly GraphicsDevice device;
        private readonly Ui ui;
        private readonly TileArt art;
        private readonly Dictionary<string, RenderTarget2D> cells = new Dictionary<string, RenderTarget2D>();
        private readonly Dictionary<Texture2D, Texture2D> lavender = new Dictionary<Texture2D, Texture2D>();
        private readonly List<Spec> current = new List<Spec>();

        public Ghosts(GraphicsDevice device, Ui ui, TileArt art)
        {
            this.device = device; this.ui = ui; this.art = art;
        }

        /// <summary>sub_920CEB58: a colour cell's ghost tint, from its first colour.</summary>
        public static Color GhostColour(byte r, byte g, byte b)
        {
            float s = (float)((float)b + (float)g) + (float)r;
            int m = (int)(float)(s * 0.5f);
            if (m > 255) m = 255;
            if (b > m) { byte rg = (byte)(long)(float)(m * 0.75f); return new Color(rg, rg, b); }
            int t = (int)(float)(m * 1.3f);
            return new Color((byte)m, (byte)m, (byte)Math.Min(255, t));
        }

        /// <summary>Before the scene pass: compose every ghost not composed yet.</summary>
        public void Prepare(List<Spec> specs)
        {
            current.Clear();
            current.AddRange(specs);
            bool any = false;
            foreach (Spec s in specs) if (!cells.ContainsKey(s.Key)) { any = true; break; }
            if (!any) return;
            RenderTargetBinding[] saved = device.GetRenderTargets();
            foreach (Spec s in specs)
            {
                if (cells.ContainsKey(s.Key)) continue;
                if (cells.Count >= 48)
                {
                    // unbind first: FNA 26.10 refuses to dispose a bound target
                    device.SetRenderTarget(null);
                    Clear();
                }
                RenderTarget2D rt = s.Boxart ? new RenderTarget2D(device, 180, 235, false, SurfaceFormat.Color, DepthFormat.None)
                                             : new RenderTarget2D(device, Size, Size, false, SurfaceFormat.Color, DepthFormat.None);
                device.SetRenderTarget(rt);
                device.Clear(Color.Transparent);
                ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);   // premultiplied, as TileArt
                if (s.Boxart)
                {
                    Texture2D panel = art.Named("box_art_panel");
                    if (panel != null)
                    {
                        // SizeMode 16: aspect-fitted to 178 x 233, centred (XUI_Image_ComputeQuad)
                        float k = Math.Min(178f / panel.Width, 233f / panel.Height), w = panel.Width * k, h = panel.Height * k;
                        ui.Batch.Draw(panel, new Vector2(1f + (178f - w) / 2f, 1f + (233f - h) / 2f), null, Color.White, 0f, Vector2.Zero, k, SpriteEffects.None, 0f);
                    }
                    if (s.Picture != null)                                                                      // preview, stretched
                        ui.Batch.Draw(Lavender(s.Picture), new Vector2(19.75f, 20.5f), null, Color.White, 0f, Vector2.Zero,
                                      new Vector2(140.9f / s.Picture.Width, 193f / s.Picture.Height), SpriteEffects.None, 0f);
                    ui.Batch.End();
                    cells[s.Key] = rt;
                    continue;
                }
                ui.Batch.Draw(ui.Pixel, new Rectangle(14, 14, 100, 100), new Color(0xEB, 0xEB, 0xEB) * 0.4f);   // bg_box
                Texture2D frame = art.HasStates ? art.Named("colour_tile_unselected") : null;
                if (s.Colour)
                {
                    if (frame != null) ui.Batch.Draw(frame, new Rectangle(0, 0, Size, Size), Color.White);   // unselected
                    ui.Rounded(new Rectangle(16, 16, 95, 94), s.Fill);                                      // colour
                    if (frame != null) ui.Batch.Draw(frame, new Rectangle(0, 0, Size, Size), Color.White);   // ColourOverlay
                }
                else if (s.Picture != null)
                {
                    ui.Batch.Draw(Lavender(s.Picture), new Rectangle(14, 13, 100, 100), Color.White);       // preview
                }
                ui.Batch.End();
                cells[s.Key] = rt;
            }
            device.SetRenderTargets(saved);
        }

        /// <summary>Each composed cell, faded by its group's opacity.</summary>
        public void Draw()
        {
            foreach (Spec s in current)
            {
                RenderTarget2D rt;
                if (cells.TryGetValue(s.Key, out rt)) ui.Batch.Draw(rt, s.Visual, Color.White * (s.Colour ? 0.4f : 0.5f));
            }
        }

        public void Clear()
        {
            foreach (RenderTarget2D rt in cells.Values) rt.Dispose();
            cells.Clear();
            foreach (Texture2D t in lavender.Values) t.Dispose();
            lavender.Clear();
        }

        /// <summary>The picture in the editor's lavender, premultiplied again for our blend.</summary>
        private Texture2D Lavender(Texture2D src)
        {
            Texture2D t;
            if (lavender.TryGetValue(src, out t) && !t.IsDisposed) return t;
            Color[] px = new Color[src.Width * src.Height];
            src.GetData(px);
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                if (c.A > 0 && c.A < 255)
                    px[i] = new Color((byte)Math.Min(255, c.R * 255 / c.A), (byte)Math.Min(255, c.G * 255 / c.A), (byte)Math.Min(255, c.B * 255 / c.A), c.A);
            }
            UnselectedIcons.Apply(px);
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                px[i] = new Color((byte)(c.R * c.A / 255), (byte)(c.G * c.A / 255), (byte)(c.B * c.A / 255), c.A);
            }
            t = new Texture2D(device, src.Width, src.Height);
            t.SetData(px);
            lavender[src] = t;
            return t;
        }
    }
}
