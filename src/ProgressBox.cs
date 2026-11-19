// The editor's busy box: "Saving" after Save and Exit, "Loading items" while items load.

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    static class ProgressBox
    {
        private const float SceneX = -17f, SceneY = -85f;                              // scnGrid
        private static readonly Color BoxFill = new Color(0x0F, 0x0F, 0x0F);           // #FF0F0F0F
        private const float BoxOpacity = 0.3f;
        private static readonly Color TextColour = new Color(0xEB, 0xEB, 0xEB);        // #FFEBEBEB
        private const float Fps = 60f;                                                 // sub_92175208

        /// <summary>`seconds` since the box appeared: the spinner's frame 0.</summary>
        public static void Draw(Ui ui, TileArt art, string text, float seconds)
        {
            ui.Batch.Draw(ui.Pixel, Layout.SceneRect(SceneX + 345f, SceneY + 269f, 326f, 182f), BoxFill * BoxOpacity);

            // lblText through DialogText's presenter: 18 pt, 0x1404, laid out as XUI does (RuntimeFont.DrawXui)
            RuntimeFont f = ui.Xui(18f, 0x1404);
            f.DrawXui(ui.Batch, f.WrapXui(text, 316f), Layout.Scene(SceneX + 350f, SceneY + 278.5f), Layout.SceneScale, 316f, 79f, 0x1404,
                      TextColour, Palette.DropShadow);

            Texture2D ring = art.Named("outerRing");
            if (ring == null) return;
            float frame = seconds * Fps;
            float t = Math.Min(frame / 39f, 1f);
            float v = 1f - Exponential(1f - t, 3f);                                    // mode 1, ease-out
            float opacity = v, scale = 0.9f + 0.1f * v;
            float angle = MathHelper.PiOver2 * (frame / 13f) % MathHelper.TwoPi;
            Vector2 spin = Layout.Scene(SceneX + 472f + 36f, SceneY + 360f + 36f);    // its centre
            float s = Layout.SceneScale * scale;
            Vector2 origin = new Vector2(ring.Width / 2f, ring.Height / 2f);
            ui.Batch.Draw(ring, spin + new Vector2(0f, s), null, Color.Black * (0.1f * opacity), angle, origin, s * 72f / ring.Width, SpriteEffects.None, 0f);
            ui.Batch.Draw(ring, spin, null, Color.White * opacity, angle, origin, s * 72f / ring.Width, SpriteEffects.None, 0f);
        }

        /// <summary>sub_92174808 curve 10: (e^(p u) - 1) / (e^p - 1), e as the code has it (a float).</summary>
        private static float Exponential(float u, float p)
        {
            const double E = 2.718281745910645;
            return (float)((Math.Pow(E, p * u) - 1.0) / (Math.Pow(E, p) - 1.0));
        }
    }
}
