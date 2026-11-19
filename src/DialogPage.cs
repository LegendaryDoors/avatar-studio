// The editor's yes / no dialog page (Dialog.xur): Choose New Avatar, an outfit's delete, the gallery's delete.

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    static class DialogPage
    {
        private static readonly Color BoxFill = new Color(0x0F, 0x0F, 0x0F);           // #FF0F0F0F
        private const float BoxOpacity = 0.3f;
        private static readonly Color TextColour = new Color(0xEB, 0xEB, 0xEB);        // #FFEBEBEB
        private const float FocusScale = 1.5f;                                         // Grid1x1Button, FocusLoop
        private static readonly Vector2[] Buttons = { new Vector2(115f, 260f), new Vector2(773f, 260f) };   // ItemButton1 / 2
        private const float ButtonSize = 128f;

        /// <summary>Entry i's button on screen, unscaled (ItemButton1 / 2), for the mouse.</summary>
        public static Rectangle ButtonRect(int i)
        {
            return Layout.SceneRect(Buttons[i].X, Buttons[i].Y, ButtonSize, ButtonSize);
        }

        /// <summary>`bases`: the entries' art; `focus` the focused one; `floorShadows` false for the gallery.</summary>
        public static void Draw(Ui ui, TileArt art, string text, string[] bases, int focus, XuiPlayer[] players = null, bool floorShadows = true)
        {
            ui.Batch.Draw(ui.Pixel, Layout.SceneRect(179f, 440.794f, 658f, 138f), BoxFill * BoxOpacity);

            for (int i = 0; i < 2; i++)
                Button(ui, art, bases[i], i == 0, i == focus, players == null ? null : players[i], Buttons[i]);

            // lblText through DialogText's presenter: 18 pt, 0x1404, laid out as XUI does (RuntimeFont.DrawXui)
            RuntimeFont f = ui.Xui(18f, 0x1404);
            f.DrawXui(ui.Batch, f.WrapXui(text, 640f), Layout.Scene(187f, 446.794f), Layout.SceneScale, 640f, 120f, 0x1404,
                      TextColour, Palette.DropShadow);

            if (!floorShadows) return;
            Chrome.FloorShadow(ui, art, 774f, 128f);                                   // shadowRight
            Chrome.FloorShadow(ui, art, 115f, 128f);                                   // shadowLeft
        }

        /// <summary>[ours] The message page's OK: Yes's place, beside the avatar the dialog's camera centres.</summary>
        public static readonly Vector2 OkButton = Buttons[0];

        public static Rectangle OkRect()
        {
            return Layout.SceneRect(OkButton.X, OkButton.Y, ButtonSize, ButtonSize);
        }

        /// <summary>[ours] The message page: Dialog.xur's box and text, and one focused OK button.</summary>
        public static void DrawMessage(Ui ui, TileArt art, string text, string baseName, XuiPlayer player)
        {
            ui.Batch.Draw(ui.Pixel, Layout.SceneRect(179f, 440.794f, 658f, 138f), BoxFill * BoxOpacity);
            Button(ui, art, baseName, true, true, player, OkButton);
            RuntimeFont f = ui.Xui(18f, 0x1404);
            f.DrawXui(ui.Batch, f.WrapXui(text, 640f), Layout.Scene(187f, 446.794f), Layout.SceneScale, 640f, 120f, 0x1404,
                      TextColour, Palette.DropShadow);
            Chrome.FloorShadow(ui, art, OkButton.X, ButtonSize);
        }

        /// <summary>A Grid1x1Button at `at`: its timeline, else settled, else a drawn plate with a tick or cross.</summary>
        public static void Button(Ui ui, TileArt art, string baseName, bool yes, bool focused, XuiPlayer p, Vector2 at, Matrix? parent = null)
        {
            Matrix up = parent ?? Matrix.Identity;
            if (p != null && DrawVisual(ui, art, baseName, p, at, up)) return;
            float s = focused ? FocusScale : 1f;
            Vector2 sc = Vector2.Transform(new Vector2(at.X + 64f, at.Y + 64f), up);
            Vector2 c = Layout.Scene(sc.X, sc.Y);
            float px = ButtonSize * Layout.SceneScale * s * up.M11;
            Rectangle v = new Rectangle((int)Math.Round(c.X - px / 2f), (int)Math.Round(c.Y - px / 2f), (int)Math.Round(px), (int)Math.Round(px));
            Texture2D t = art.Named(baseName + (focused ? "_selected" : "_unselected"));
            if (t != null) { ui.Batch.Draw(t, v, Color.White); return; }
            ui.Rounded(v, focused ? Palette.SelectGreen : Palette.CellGrey);
            ui.Icon(yes ? ui.Check : ui.Cross, new Vector2(v.Center.X, v.Center.Y), (int)(px * 0.45f), yes ? Palette.XboxGreen : Palette.RemoveRed);
        }

        /// <summary>Grid1x1Button through its timeline (unselected / selected); false without the art.</summary>
        private static bool DrawVisual(Ui ui, TileArt art, string baseName, XuiPlayer p, Vector2 at, Matrix parent)
        {
            Texture2D u = art.Named(baseName + "_unselected"), s = art.Named(baseName + "_selected");
            if (u == null || s == null) return false;
            Matrix ctrl = Matrix.CreateTranslation(at.X, at.Y, 0f) * parent * XuiDraw.SceneToScreen;
            string[] ids = { "unselected", "selected" };
            Texture2D[] tex = { u, s };
            for (int k = 0; k < 2; k++)
            {
                Matrix m; float o;
                if (!p.Global(ids[k], out m, out o) || o <= 0f) continue;
                XuiDraw.Begin(ui, m * ctrl);
                XuiDraw.Image(ui, tex[k], 0f, 0f, tex[k].Width, tex[k].Height, Color.White * o);
                XuiDraw.End(ui);
            }
            return true;
        }
    }
}
