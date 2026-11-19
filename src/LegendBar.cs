// The button legend ("A Select  B Back") as the console draws it: the control pack's legend
// scene, laid out and animated by its own class.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class LegendBar
    {
        // the place (sub_921D5928) in legendSceneParent, 1280 x 720 [xur EditorBG.xur], widescreen
        private const float ParentW = 1280f, ParentH = 720f;
        private const float SafeArea = 0.075000003f;           // 0x921D59D4's constant
        private const float FromBottom = 76f;                  // 0x9204C48C
        private const int PeriodMs = 20;                       // XUI_SetTimer(scene, 5001, 20)
        private const float TickSeconds = 0.02f;               // sub_921D6230 -> sub_921D5650(.., 0.02)
        private const float WaitRate = 4.5f, SlideRate = 10f, Gap = 48f;

        // [xur] LegendScene.xur, for the bar without legend.txt
        private const float FallbackGutter = -2f;
        private static readonly Vector2 FallbackImage = new Vector2(-10f + 4f, -4f + 4f);   // Images + Button_Image
        private static readonly Vector2 FallbackText = new Vector2(30f, 0f);
        private const float TextW = 512f, TextH = 32f, ImageSize = 32f;
        private const int FallbackStyle = 0x1111;

        private static readonly string[] GroupIds = { "AButton", "BButton", "XButton", "YButton" };
        private static readonly string[] Letters = { "A", "B", "X", "Y" };
        // [ours] the keys shown for A, B, X, Y while on the keyboard (Input.cs's key map)
        private static readonly string[] KeyNames = { "Enter", "Esc", "X", "Y" };
        private const float KeyHeight = 26f;
        private bool laidOutKeys;                          // the mode the last layout measured for
        private static readonly Color[] FallbackDisc = { Palette.GlyphA, Palette.GlyphB, Palette.GlyphX, Palette.GlyphY };   // [ours]

        private enum Step { Idle, Hide, HideWait, HideDone, LayOut, Slide, Snap, Show, ShowWait, ShowDone, PressWait }

        private sealed class Slot
        {
            public string Shown = "", Current = "", Pending = "";
            public float OldX, NewX, X;
            public int Group = -1, Images = -1, Text = -1;
            public bool Up;                                 // without timelines: Show played, Hide not since
        }

        private readonly Slot[] slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private readonly TileArt art;
        private readonly XuiVisualDef def;
        private readonly XuiPlayer player;
        private readonly float gutter;
        private Step step = Step.Idle;
        private float t;                                    // +2156
        private double clockMs;
        private uint lastFired;

        /// <summary>The scene's Show: false while the system's own UI is up (XamIsUIActive).</summary>
        public bool SystemUiActive;
        private bool visible = true;

        public string Status { get; private set; }

        public LegendBar(TileArt art)
        {
            this.art = art;
            string path = art.Dir == null ? null : Path.Combine(art.Dir, "legend.txt");
            if (path != null && File.Exists(path))
            {
                List<XuiVisualDef> list = XuiVisuals.Parse(path);
                def = list.Count > 0 ? list[0] : null;
            }
            gutter = FallbackGutter;
            if (def != null)
            {
                string g;
                gutter = def.Elements[0].Props.TryGetValue("LeftEdgeGutter", out g) ? Flt(g) : 0f;   // the property's default 0 (sub_9216F780)
                for (int s = 0; s < 4; s++)
                {
                    Slot sl = slots[s];
                    sl.Group = def.Index(GroupIds[s]);
                    if (sl.Group < 0) continue;
                    sl.Text = Child(sl.Group, "Text");
                    sl.Images = Child(sl.Group, "Images");
                }
                player = new XuiPlayer(def);
            }
            Status = def != null ? "legend <- " + path : "no legend.txt: the legend drawn settled with [ours] discs (the setup installs it)";
            Console.WriteLine("[studio] " + Status);
        }

        /// <summary>The first element called `id` under `parent` (sub_92169C10's search).</summary>
        private int Child(int parent, string id)
        {
            int d = def.Elements[parent].Depth;
            for (int i = parent + 1; i < def.Elements.Count && def.Elements[i].Depth > d; i++)
                if (def.Elements[i].Id == id) return i;
            return -1;
        }

        // ---- the texts ---------------------------------------------------------------------------

        /// <summary>Every slot's new text empty: a frame with no legend hides it.</summary>
        public void Clear() { foreach (Slot s in slots) s.Pending = ""; }

        /// <summary>This frame's legend; each item's letter picks its slot (A, B, X, Y).</summary>
        public void Set(string[] items)
        {
            Clear();
            foreach (string it in items)
            {
                if (string.IsNullOrEmpty(it)) continue;
                int s = Array.IndexOf(Letters, it.Substring(0, 1));
                if (s >= 0) slots[s].Pending = it.Length > 2 ? it.Substring(2) : "";
            }
        }

        // ---- time ------------------------------------------------------------------------------

        /// <summary>A button's key down: slot 0 A or Start, 1 B or Back, 2 X, 3 Y (sub_920B61C0).</summary>
        public void Press(int slot)
        {
            if (step != Step.PressWait) Play(slot, false, "Press", "PressEnd");
            if (step == Step.Idle) step = Step.PressWait;
        }

        /// <summary>The timelines `seconds` on (XUI_TimelinesTick, in the frame's update).</summary>
        public void Advance(float seconds) { if (player != null) player.Advance(seconds); }

        /// <summary>The 20 ms timer; the console runs it after the render, so it goes first in Update.</summary>
        public void Tick(float seconds, Ui ui)
        {
            clockMs += seconds * 1000.0;
            uint now = (uint)clockMs;
            if (now - lastFired < PeriodMs) return;
            lastFired = now;
            visible = !SystemUiActive;
            for (int guard = 0; guard < 32; guard++)
            {
                Step before = step;
                Run(ui);
                if (step == before) break;
            }
        }

        private void Run(Ui ui)
        {
            switch (step)
            {
                case Step.Idle:
                    {
                        bool changed = laidOutKeys != Input.KeysShown;      // [ours] keys and buttons measure differently
                        foreach (Slot s in slots) { s.Current = s.Pending; if (!Same(s.Shown, s.Current)) changed = true; }
                        if (changed) step = Step.Hide;
                        break;
                    }
                case Step.Hide:
                    {
                        bool any = false;
                        for (int i = 0; i < 4; i++)
                            if (slots[i].Current.Length == 0 && slots[i].Shown.Length > 0) { Play(i, true, "Hide", "HideEnd"); any = true; }
                        step = any ? Step.HideWait : Step.HideDone;
                        break;
                    }
                case Step.HideWait: Wait(Step.HideDone); break;
                case Step.HideDone: step = Step.LayOut; break;
                case Step.LayOut:
                    {
                        laidOutKeys = Input.KeysShown;
                        float oldX = 0f, newX = 0f;
                        bool slide = false;
                        for (int i = 0; i < 4; i++)
                        {
                            Slot s = slots[i];
                            s.OldX = oldX; s.NewX = newX;
                            if (s.Shown.Length > 0) oldX = (float)(Width(ui, i, s.Shown) + Gap) + oldX;
                            if (s.Current.Length > 0) newX = (float)(Width(ui, i, s.Current) + Gap) + newX;
                            if (s.Shown.Length > 0 && s.Current.Length > 0 && s.OldX != s.NewX) slide = true;
                        }
                        step = slide ? Step.Slide : Step.Snap;
                        break;
                    }
                case Step.Slide:
                    {
                        t = TickSeconds * SlideRate + t;
                        float u = MathHelper.Clamp(t, 0f, 1f);
                        float e = MathHelper.Clamp((float)Math.Sin(u * 1.5707964f), 0f, 1f);
                        foreach (Slot s in slots) s.X = (s.NewX - s.OldX) * e + s.OldX;
                        if (t >= 1f) { t = 0f; step = Step.Snap; }
                        break;
                    }
                case Step.Snap:
                    foreach (Slot s in slots) { s.OldX = s.NewX; s.X = s.NewX; }
                    step = Step.Show;
                    break;
                case Step.Show:
                    {
                        bool any = false;
                        for (int i = 0; i < 4; i++)
                        {
                            Slot s = slots[i];
                            if (s.Shown.Length == 0 && s.Current.Length > 0) { Play(i, true, "Show", "ShowEnd"); any = true; }
                            s.Shown = s.Current;                  // XUI_Text_SetText
                        }
                        step = any ? Step.ShowWait : Step.ShowDone;
                        break;
                    }
                case Step.ShowWait: Wait(Step.ShowDone); break;
                case Step.ShowDone: step = Step.Idle; break;
                case Step.PressWait: Wait(Step.Idle); break;
            }
        }

        /// <summary>sub_921D68B8 / sub_921D6030 / sub_921D6A20: t += 0.02 x 4.5; at 1, t = 0 and on.</summary>
        private void Wait(Step next)
        {
            t = TickSeconds * WaitRate + t;
            if (t >= 1f) { step = next; t = 0f; }
        }

        private static bool Same(string a, string b) { return (a ?? "") == (b ?? ""); }

        /// <summary>XUI_Text_Measure's width: whole-pixel advances in the element's font.</summary>
        private float Width(Ui ui, int slot, string text)
        {
            int e = slots[slot].Text;
            string v;
            int style = def != null && e > 0 && def.Elements[e].Props.TryGetValue("TextStyle", out v) ? int.Parse(v, CultureInfo.InvariantCulture) : FallbackStyle;
            return ui.Xui(PointSize(e), style).XuiWidth(text) + Extra(ui, slot);
        }

        /// <summary>[ours] How much wider than the button's square the key is.</summary>
        private float Extra(Ui ui, int slot)
        {
            // the text starts at least 6 px past the key
            return Input.KeysShown ? Math.Max(0f, ui.KeyCap(Vector2.Zero, KeyHeight, KeyNames[slot], 1f, false) + 6f - FallbackText.X) : 0f;
        }

        /// <summary>A range on the slot's group (Show / Hide) or its Images (Press).</summary>
        private void Play(int slot, bool group, string start, string end)
        {
            Slot s = slots[slot];
            if (player != null)
            {
                int owner = group ? s.Group : s.Images;
                if (owner > 0) player.PlayOwn(owner, start, end);
                return;
            }
            if (start == "Show") s.Up = true;
            else if (start == "Hide") s.Up = false;
        }

        // ---- drawing ---------------------------------------------------------------------------

        private Vector2 Origin { get { return new Vector2(ParentW * SafeArea + gutter, ParentH - FromBottom); } }

        /// <summary>The bar, in the batch's current (screen) space; leaves the batch as it found it.</summary>
        public void Draw(Ui ui)
        {
            if (!visible) return;
            if (player == null) { DrawSettled(ui); return; }
            // in the scene's order: each element of a bound group, under its parents' transforms
            for (int i = 1; i < def.Elements.Count; i++)
            {
                XuiElementDef e = def.Elements[i];
                if (e.Class != "XuiImage" && e.Class != "XuiText") continue;
                int slot = SlotOf(i);
                if (slot < 0) continue;
                Matrix m; float opacity;
                if (!Global(i, slot, out m, out opacity) || opacity <= 0f) continue;
                XuiElementState st = player.Evaluate(i);
                bool keys = Input.KeysShown;
                if (keys && e.Class == "XuiText") m = Matrix.CreateTranslation(Extra(ui, slot), 0f, 0f) * m;   // [ours] past the key
                XuiDraw.Begin(ui, m);
                if (e.Class == "XuiImage")
                {
                    string file;
                    Texture2D tex = e.Props.TryGetValue("ImagePath", out file) ? art.Named(Path.GetFileNameWithoutExtension(file)) : null;
                    if (keys) { if (e.Id == "Button_Image") ui.KeyCap(new Vector2(0f, (st.Height - KeyHeight) / 2f), KeyHeight, KeyNames[slot], opacity); }
                    else if (tex != null) XuiDraw.Image(ui, tex, 0f, 0f, st.Width, st.Height, Color.White * opacity);
                    else if (e.Id == "Button_Image") Disc(ui, slot, Vector2.Zero, opacity);
                }
                else
                    DrawText(ui, e, slots[slot].Shown, Vector2.Zero, st.Width, st.Height, opacity);
                XuiDraw.End(ui);
            }
        }

        /// <summary>[ours] The slot under p, or -1, so the mouse can click the legend.</summary>
        public int SlotAt(Vector2 p, Ui ui)
        {
            if (!visible) return -1;
            Vector2 o = Origin;
            for (int s = 0; s < 4; s++)
            {
                Slot sl = slots[s];
                if (sl.Shown.Length == 0 || (player == null && !sl.Up)) continue;
                float x0 = o.X + sl.X, x1 = x0 + FallbackText.X + Width(ui, s, sl.Shown);
                if (p.X >= x0 - 8f && p.X <= x1 + 8f && p.Y >= o.Y - 6f && p.Y <= o.Y + ImageSize + 6f) return s;
            }
            return -1;
        }

        /// <summary>The slot whose group holds element i, or -1 (the titles).</summary>
        private int SlotOf(int i)
        {
            for (int s = 0; s < 4; s++)
            {
                int g = slots[s].Group;
                if (g < 0 || i <= g) continue;
                int d = def.Elements[g].Depth, k = g + 1;
                while (k < def.Elements.Count && def.Elements[k].Depth > d) k++;
                if (i < k) return s;
            }
            return -1;
        }

        /// <summary>Element i's matrix to the screen and its opacity; false when hidden.</summary>
        private bool Global(int i, int slot, out Matrix m, out float opacity)
        {
            m = Matrix.Identity; opacity = 1f;
            int g = slots[slot].Group;
            for (int k = i; k > 0; k = def.Elements[k].Parent)
            {
                XuiElementState s = player.Evaluate(k);
                if (!s.Show) return false;
                if (k == g) s.Position = new Vector3(slots[slot].X, 0f, 0f);
                m = m * s.Local;
                opacity *= s.Opacity;
                if (k == g) break;
            }
            Vector2 o = Origin;
            m = m * Matrix.CreateTranslation(o.X, o.Y, 0f);
            return true;
        }

        private void DrawSettled(Ui ui)
        {
            Vector2 o = Origin;
            for (int s = 0; s < 4; s++)
            {
                if (!slots[s].Up) continue;
                Vector2 at = o + new Vector2(slots[s].X, 0f);
                XuiDraw.Begin(ui, Matrix.CreateTranslation(at.X, at.Y, 0f));
                if (Input.KeysShown)
                {
                    ui.KeyCap(FallbackImage + new Vector2(0f, (ImageSize - KeyHeight) / 2f), KeyHeight, KeyNames[s], 1f);
                    DrawText(ui, null, slots[s].Shown, FallbackText + new Vector2(Extra(ui, s), 0f), TextW, TextH, 1f);
                }
                else
                {
                    Disc(ui, s, FallbackImage, 1f);
                    DrawText(ui, null, slots[s].Shown, FallbackText, TextW, TextH, 1f);
                }
                XuiDraw.End(ui);
            }
        }

        /// <summary>[ours] A disc and its letter where the picture is not installed.</summary>
        private void Disc(Ui ui, int slot, Vector2 at, float opacity)
        {
            Texture2D pic = art.Named("Legend_" + Letters[slot]);
            if (pic != null)
            {
                ui.Batch.Draw(pic, new Rectangle((int)at.X, (int)at.Y, (int)ImageSize, (int)ImageSize), Color.White * opacity);
                return;
            }
            Vector2 c = at + new Vector2(ImageSize / 2f, ImageSize / 2f);
            ui.Disc(c, ImageSize * 0.375f, FallbackDisc[slot] * opacity);
            ui.Xui(14f).DrawXui(ui.Batch, Letters[slot], at, 1f, ImageSize, ImageSize, FallbackStyle | 0x400,
                                Color.White * opacity, Palette.DropShadow);
        }

        /// <summary>A button's Text in its element's style, laid out by DrawXui.</summary>
        private void DrawText(Ui ui, XuiElementDef e, string text, Vector2 at, float w, float h, float opacity)
        {
            if (string.IsNullOrEmpty(text)) return;
            int style = FallbackStyle;
            uint colour = 0xFFFFFFFF, shadow = 0x800F0F0F;
            string v;
            if (e != null)
            {
                style = e.Props.TryGetValue("TextStyle", out v) ? int.Parse(v, CultureInfo.InvariantCulture) : 0;
                if (e.Props.TryGetValue("TextColor", out v)) colour = Argb(v);
                if (e.Props.TryGetValue("DropShadowColor", out v)) shadow = Argb(v);
            }
            Color sh = new Color((int)((shadow >> 16) & 255), (int)((shadow >> 8) & 255), (int)(shadow & 255), (int)(shadow >> 24));
            ui.Xui(PointSize(e == null ? -1 : def.Elements.IndexOf(e)), style).DrawXui(ui.Batch, text, at, 1f, w, h, style,
                Palette.Argb(colour) * opacity, sh);
        }

        /// <summary>A Text's PointSize, or XuiText's default 14 (sub_9215BCF0); the buttons' Texts
        /// name none.</summary>
        private float PointSize(int element)
        {
            string v;
            if (def != null && element > 0 && def.Elements[element].Props.TryGetValue("PointSize", out v)) return Flt(v);
            return 14f;
        }

        private static uint Argb(string s)
        {
            s = s.TrimStart('#');
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static float Flt(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
    }
}
