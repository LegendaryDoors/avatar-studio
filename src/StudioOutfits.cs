// My Outfits (state 31): the Save Outfit tile and the avatar's saved outfits, with the delete
// question, the busy box and the Introducing Outfits tutorial over it.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed partial class StudioGame
    {
        private readonly OutfitStore outfits = new OutfitStore();
        private readonly List<int> outfitEntries = new List<int>();   // -1 the Save Outfit tile, else an index into outfits.Items
        private int outfitFocused = -1;
        private float outfitFocusAt;
        private bool outfitPreviewed;
        private int deleteOutfitFocus;
        private Outfit deletingOutfit;
        private int overrideDepth;                 // the stack depth an override sits at (StackDepth)
        private bool outfitNaming;                 // the keyboard is up (store state 3)
        private string outfitNameText = "";
        private byte[] outfitPending;              // the manifest being saved
        private string outfitBusyText = "Saving";
        private float outfitBusyAt;                // controller +580: when the box was pushed
        private bool outfitBusyDeleting;
        private bool tutorialSeen, settingsLoaded;

        private const int OutfitsPerPage = 4;
        private static readonly Vector2 OutfitTiles = new Vector2(114f - 0.224976f, 109f);
        private const float OutfitTextX = 132.837f - 0.224976f;           // the Text group
        private const string DeleteOutfitText = "Are you sure you want to delete this outfit?";   // Strings.xus 13

        private static Vector2 OutfitTileScene(int i) { return OutfitTiles + new Vector2(603f * (i / 2), 230f * (i % 2)); }
        private static Rectangle OutfitTileRect(int i) { Vector2 s = OutfitTileScene(i); return Layout.SceneRect(s.X, s.Y, 180f, 230f); }

        /// <summary>sub_920BE090: every worn component present in the catalogue or the asset folder.</summary>
        private bool OutfitAssetsPresent(byte[] manifest)
        {
            foreach (Guid g in OutfitOps.ComponentIds(manifest))
            {
                CatalogItem it = catalog == null ? null : catalog.Find(g);
                if (it == null || !it.OnDisk) return false;
            }
            return true;
        }

        private void BuildOutfitEntries()
        {
            outfitEntries.Clear();
            outfitEntries.Add(-1);
            for (int i = 0; i < outfits.Items.Count; i++)
                if (OutfitAssetsPresent(outfits.Items[i].Manifest)) outfitEntries.Add(i);
        }

        private int OutfitPages { get { return Math.Max(1, (outfitEntries.Count + OutfitsPerPage - 1) / OutfitsPerPage); } }
        private int OutfitsOnPage { get { return Math.Max(0, Math.Min(OutfitsPerPage, outfitEntries.Count - page * OutfitsPerPage)); } }
        private int OutfitEntry(int c)
        {
            int g = page * OutfitsPerPage + c;
            return g >= 0 && g < outfitEntries.Count ? outfitEntries[g] : -2;
        }

        /// <summary>From the My Style grid (its tile pushes 31); the first time, the tutorial.</summary>
        private void EnterOutfits()
        {
            if (!settingsLoaded) LoadStudioSettings();
            state = State.Outfits;
            stage.Screen = ScreenFor(activeLeaf);
            BuildOutfitEntries();
            OutfitInitialFocus();
            if (!tutorialSeen)
            {
                tutorialSeen = true;                                           // sub_920F6EA8, at once
                OpenOverride(State.OutfitTutorial);
            }
        }

        /// <summary>AE_OutfitsScreen_InitialFocus: the worn outfit's tile when saved and shown, else Save Outfit.</summary>
        private void OutfitInitialFocus()
        {
            int worn = outfits.Find(session.CommittedBytes);
            int g = worn >= 0 ? Math.Max(0, outfitEntries.IndexOf(worn)) : 0;
            page = g / OutfitsPerPage;
            cursor = g % OutfitsPerPage;
            outfitFocused = -1;
        }

        /// <summary>An override over the current screen (sub_920ED818): the depth stays, the camera changes.</summary>
        private void OpenOverride(State s)
        {
            overrideDepth = StackDepth();
            state = s;
            stage.Screen = EditorCamera.Override;
        }

        /// <summary>Back to My Outfits from an override; `refocus` finds the initial focus again.</summary>
        private void ReturnToOutfits(bool refocus)
        {
            state = State.Outfits;
            stage.Screen = ScreenFor(activeLeaf);
            BuildOutfitEntries();
            if (refocus) OutfitInitialFocus();
            else
            {
                page = Math.Min(page, OutfitPages - 1);
                cursor = (int)MathHelper.Clamp(cursor, 0, Math.Max(0, OutfitsOnPage - 1));
                outfitFocused = -1;
            }
        }

        /// <summary>AE_SaveOutfitEntry_State: 1 the worn manifest is saved, 0 there is room, 2 full.</summary>
        private int SaveOutfitState()
        {
            if (outfits.Find(session.WorkingBytes) >= 0) return 1;
            return outfits.HasRoom ? 0 : 2;
        }

        private string OutfitLabel(int c)
        {
            int e = OutfitEntry(c);
            if (e == -1)
            {
                int st = SaveOutfitState();
                return st == 1 ? "This outfit has been saved" : st == 2 ? "Outfits storage is full" : "Save Outfit";
            }
            return e >= 0 ? outfits.Items[e].WearLabel : null;
        }

        private void UpdateOutfits(float dt)
        {
            int n = OutfitsOnPage;
            if (input.Up && cursor % 2 == 1) cursor--;
            if (input.Down && cursor % 2 == 0 && cursor + 1 < n) cursor++;
            if (input.Left && cursor >= 2) cursor -= 2;
            if (input.Right && cursor < 2 && cursor + 2 < n) cursor += 2;
            for (int i = 0; i < n; i++)
                if (input.MouseIn(OutfitTileRect(i)))
                {
                    if (input.MouseMoved || input.MouseClicked) cursor = i;
                    if (input.MouseClicked) { PressOutfit(); return; }
                }
            int pages = OutfitPages;
            if ((input.PageLeft || (input.MouseClicked && input.MouseIn(OutfitHandleRect(false)))) && page > 0) { page--; cursor = 0; }
            if ((input.PageRight || (input.MouseClicked && input.MouseIn(OutfitHandleRect(true)))) && page < pages - 1) { page++; cursor = 0; }
            cursor = (int)MathHelper.Clamp(cursor, 0, Math.Max(0, OutfitsOnPage - 1));
            if (input.Select) { PressOutfit(); return; }
            if (input.X && OutfitEntry(cursor) >= 0) { OpenDeleteOutfit(); return; }
            if (input.Back) { BackPressed(); return; }
            OutfitFocusTick();
        }

        /// <summary>The focused tile's action: Save Outfit restores the snapshot; an outfit dresses after 0.3 s.</summary>
        private void OutfitFocusTick()
        {
            int g = page * OutfitsPerPage + cursor;
            int e = OutfitEntry(cursor);
            if (g != outfitFocused)
            {
                outfitFocused = g; outfitFocusAt = time; outfitPreviewed = false;
                if (e == -1) { session.Revert(); outfitPreviewed = true; }
            }
            if (!outfitPreviewed && e >= 0 && time - outfitFocusAt >= 0.3f)
            {
                outfitPreviewed = true;
                session.PreviewOutfit(outfits.Items[e].Manifest);
            }
        }

        private void PressOutfit()
        {
            int i = cursor, e = OutfitEntry(i);
            if (e < -1) return;
            controls.Press(i, delegate
            {
                if (state != State.Outfits) return;
                if (e == -1)
                {
                    if (SaveOutfitState() != 0) return;
                    BeginSaveOutfit();
                    return;
                }
                session.PreviewOutfit(outfits.Items[e].Manifest);           // wear it and leave
                session.Commit();
                Music.Activity();                                            // AE_OutfitEntry_Press 0x920E03D4
                GoBack();
            });
        }

        // ---- saving: the keyboard over the "Saving" box (override 78) ------------------------------

        private void BeginSaveOutfit()
        {
            outfitPending = (byte[])session.WorkingBytes.Clone();
            outfitNameText = outfits.NextName;                              // the keyboard's default
            outfitNaming = true;
            outfitBusyText = "Saving"; outfitBusyDeleting = false;          // Strings.xus 184
            outfitBusyAt = time;
            OpenOverride(State.OutfitBusy);
        }

        private void UpdateOutfitBusy()
        {
            if (outfitNaming)
            {
                foreach (char c in PopTyped())
                {
                    if (c == '\b') { if (outfitNameText.Length > 0) outfitNameText = outfitNameText.Substring(0, outfitNameText.Length - 1); }
                    else if (c == '\r' || c == '\n') { ConfirmOutfitName(); return; }
                    else if (outfitNameText.Length < OutfitStore.MaxName && c >= ' ') outfitNameText += c;
                }
                // A or Enter confirms, X is the pad's backspace
                if (input.Select || input.Start) { ConfirmOutfitName(); return; }
                if (input.X && outfitNameText.Length > 0) outfitNameText = outfitNameText.Substring(0, outfitNameText.Length - 1);
                if (input.Back) CancelOutfitName();
                return;
            }
            // sub_920C6370: the box clears 2 s after its push
            if (time - outfitBusyAt > 2f) ClearOutfitBusy();
        }

        /// <summary>The name is settled and the outfit written; the counter rises (Outfits.cs).</summary>
        private void ConfirmOutfitName()
        {
            if (!outfitNaming) return;
            outfitNaming = false;
            Outfit o = outfits.Add(outfitPending, outfitNameText);
            outfitPending = null;
            Console.WriteLine("[studio] outfit saved: \"" + o.Name + "\"");
            // the write completed (sub_920BE330): EditorBG's sfxSave
            Sounds.Play("saving_an_outfit.xma");
        }

        /// <summary>A cancelled keyboard (sub_920C7400): the outfit dropped, the box cleared.</summary>
        private void CancelOutfitName()
        {
            outfitNaming = false;
            outfitPending = null;
            ReturnToOutfits(true);
        }

        private void ClearOutfitBusy()
        {
            if (!outfitBusyDeleting) { ReturnToOutfits(true); return; }
            // after a delete: back to the grid My Outfits was opened from
            state = State.Grid;
            page = 0;
            cursor = 0;
            GridStage();
        }

        private void DrawOutfitName()
        {
            // [ours] the system keyboard's stand-in (Strings.xus 199 and 200)
            DrawNamePanel("Outfit Name", "Enter a name for your outfit.", outfitNameText, null,
                          "type a name, then press Enter or A (Esc or B cancels)");
        }

        // ---- the delete question, override state 83 (Dialog.xur): focus starts on Yes ------------
        private void OpenDeleteOutfit()
        {
            int e = OutfitEntry(cursor);
            if (e < 0) return;
            deletingOutfit = outfits.Items[e];
            deleteOutfitFocus = 0;
            OpenOverride(State.DeleteOutfit);
        }

        private void UpdateDeleteOutfit()
        {
            if (input.Left) deleteOutfitFocus = 0;
            else if (input.Right) deleteOutfitFocus = 1;
            for (int i = 0; i < 2; i++)
                if (input.MouseIn(DialogPage.ButtonRect(i)))
                {
                    if (input.MouseMoved || input.MouseClicked) deleteOutfitFocus = i;
                    if (input.MouseClicked) { PressDeleteOutfit(i); return; }
                }
            if (input.Select) { PressDeleteOutfit(deleteOutfitFocus); return; }
            if (input.Back) PressDeleteOutfit(2);
        }

        private void PressDeleteOutfit(int i)
        {
            controls.Press(i, delegate
            {
                if (state != State.DeleteOutfit) return;
                Outfit o = deletingOutfit;
                deletingOutfit = null;
                if (i != 0 || o == null) { ReturnToOutfits(false); return; }   // No, B
                outfits.Delete(o);
                Console.WriteLine("[studio] outfit deleted: \"" + o.Name + "\"");
                Sounds.Play(BackSound);                                        // AE_Back's sfxBackRadon
                state = State.Outfits;
                PopScreen();                                                   // restores the snapshot; My Outfits popped
                outfitBusyText = "Deleting"; outfitBusyDeleting = true;        // Strings.xus 175
                outfitNaming = false;
                outfitBusyAt = time;
                OpenOverride(State.OutfitBusy);
            });
        }

        private void DrawDeleteOutfit()
        {
            DialogPage.Draw(ui, tileArt, DeleteOutfitText, ChooseNewArt, deleteOutfitFocus, new[] { controls[0], controls[1] });
            ui.Legend(EditorLegend(83));
        }

        // ---- Introducing Outfits, override 92 (Tutorial.xur, sub_920DABC8) ----------------------
        private const string TutorialTitle = "Introducing Outfits";                                   // Strings.xus 198
        private const string TutorialText = "Got a great look? Save what your avatar is currently wearing as an outfit.\r\n\r\n"
                                          + "Select Outfits to quickly swap your outfit or remove outfits you don't want anymore.";   // 197
        private static readonly Vector2 TutorialPivot = new Vector2(508f, 360f);
        private const float TutorialScale = 1.136f;

        /// <summary>A point of Tutorial.xur's scnGrid (items at its origin) in scene units.</summary>
        private static Vector2 TutorialPoint(Vector2 p) { return TutorialPivot + (p - TutorialPivot) * TutorialScale; }

        private static Matrix TutorialMatrix
        {
            get
            {
                return Matrix.CreateTranslation(-TutorialPivot.X, -TutorialPivot.Y, 0f) * Matrix.CreateScale(TutorialScale, TutorialScale, 1f)
                     * Matrix.CreateTranslation(TutorialPivot.X, TutorialPivot.Y, 0f);
            }
        }

        private void UpdateOutfitTutorial()
        {
            Rectangle b = Layout.SceneRect(TutorialPoint(new Vector2(215.021f, 368.394f)).X, TutorialPoint(new Vector2(215.021f, 368.394f)).Y,
                                           128f * TutorialScale, 128f * TutorialScale);
            if (input.Select || (input.MouseClicked && input.MouseIn(b))) { PressTutorial(0); return; }
            if (input.Back) PressTutorial(1);                                 // ItemButton2: hidden, PressKey 22593
        }

        private void PressTutorial(int i)
        {
            controls.Press(i, delegate { if (state == State.OutfitTutorial) ReturnToOutfits(false); });
        }

        private void DrawOutfitTutorial()
        {
            Fixed(delegate { ui.Batch.Draw(ui.Pixel, new Rectangle(0, 0, Layout.W, Layout.H), new Color(0x0F, 0x0F, 0x0F) * (0xC8 / 255f)); });
            Matrix scn = TutorialMatrix * XuiDraw.SceneToScreen;
            XuiDraw.Begin(ui, scn);
            XuiDraw.Image(ui, ui.Pixel, 158f, 43.2817f, 700f, 559.415f, new Color(0x6D, 0x6D, 0x6D));
            Texture2D img = tileArt.Named("outfits_tutorial");
            if (img != null)                                                     // SizeMode 2: centred, its own size
                XuiDraw.Image(ui, img, 194f + (628f - img.Width) / 2f, 93f + 4.95775f + (247.366f - img.Height) / 2f, img.Width, img.Height, Color.White);
            XuiDraw.End(ui);

            // [xur] the title and the text, each scaled 0.88 about its pivot
            XuiDraw.Begin(ui, Scaled(150.35f, 49.773f, 363.65f, 35.225f) * scn);
            ui.Xui(24f).DrawXui(ui.Batch, TutorialTitle, Vector2.Zero, 1f, 628f, 70.45f, 0x1111, Palette.SceneText, Palette.DropShadow);
            XuiDraw.End(ui);
            RuntimeFont body = ui.Xui(18f, 0x4000104);
            XuiDraw.Begin(ui, Scaled(338.464f, 331.746f, 363.65f, 104.32f) * scn);
            body.DrawXui(ui.Batch, body.WrapXui(TutorialText, 501.447f), Vector2.Zero, 1f, 501.447f, 277.205f, 0x4000104,
                         Palette.SceneText, Palette.DropShadow);
            XuiDraw.End(ui);

            DialogPage.Button(ui, tileArt, "option_dialog_yes", true, true, controls[0], new Vector2(215.021f, 368.394f), TutorialMatrix);
            ui.Legend(EditorLegend(92));
        }

        /// <summary>An element at `pos` with Scale 0.88 about its `pivot` (XUI_Element_LocalMatrix).</summary>
        private static Matrix Scaled(float x, float y, float px, float py)
        {
            return Matrix.CreateTranslation(-px, -py, 0f) * Matrix.CreateScale(0.88f, 0.88f, 1f) * Matrix.CreateTranslation(px, py, 0f)
                 * Matrix.CreateTranslation(x, y, 0f);
        }

        // ---- settings (the tutorial's flag) -------------------------------------------------------

        private string SettingsPath { get { return Path.Combine(AvatarLibrary.Directory, AvatarLibrary.SettingsFile); } }

        private void LoadStudioSettings()
        {
            settingsLoaded = true;
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string line in File.ReadAllLines(SettingsPath))
                    if (line.Trim() == "outfits_tutorial_seen=1") tutorialSeen = true;
            }
            catch (Exception e) { Console.WriteLine("[studio] settings: " + e.Message); }
        }

        private void SaveStudioSettings()
        {
            if (!settingsLoaded || !tutorialSeen) return;
            try
            {
                Directory.CreateDirectory(AvatarLibrary.Directory);
                File.WriteAllText(SettingsPath, "outfits_tutorial_seen=1\r\n");
            }
            catch (Exception e) { Console.WriteLine("[studio] settings: " + e.Message); }
        }

        // ---- drawing ----------------------------------------------------------------------------
        private Rectangle OutfitHandleRect(bool next)
        {
            return next ? Layout.SceneRect(69.0931f + 836.769f - 0.224976f, 313.417f, 57f, 54f)
                        : Layout.SceneRect(69.0931f - 17f - 0.224976f, 313.417f + 0.852051f, 57f, 54f);
        }

        /// <summary>Grid2x2Tall's ghosts: the previous page's cells 2 / 3, the next page's 0 / 1.</summary>
        private List<Ghosts.Spec> OutfitGhostSpecs()
        {
            List<Ghosts.Spec> specs = new List<Ghosts.Spec>();
            float[] lx = { -48.08f - 28f, 911.52f };
            float[][] ly = { new[] { 80f + 31f, 80f + 260f }, new[] { 80f + 31f, 80f + 259f } };
            for (int side = 0; side < 2; side++)
            {
                int p = side == 0 ? page - 1 : page + 1;
                if (p < 0) continue;
                for (int k = 0; k < 2; k++)
                {
                    int cell = side == 0 ? 2 + k : k;
                    int g = p * OutfitsPerPage + cell;
                    if (g < 0 || g >= outfitEntries.Count || outfitEntries[g] < 0) continue;
                    Outfit o = outfits.Items[outfitEntries[g]];
                    Ghosts.Spec s = new Ghosts.Spec();
                    s.Boxart = true;
                    s.Picture = cards.Get(o.Manifest, Framing.FullBody);
                    s.Key = "o:" + o.Seq + ":" + (s.Picture == null ? "-" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.Picture).ToString());
                    s.Visual = Layout.SceneRect(lx[side] - 0.224976f, ly[side][k], 180f, 235f);
                    specs.Add(s);
                }
            }
            return specs;
        }

        private void DrawOutfits()
        {
            // the Text group, beside `items`
            Vector2 header = Layout.Scene(OutfitTextX, -26f);                 // [xur] Grid2x2Tall: "Category name" at the Text group's origin
            Gated(false, delegate(float o, float age) { CategoryHeader("My Outfits", header, 1f); });   // Strings.xus 176 (category_header: shadowed)
            int pages = OutfitPages, n = OutfitsOnPage;
            string pageText = "Page " + (page + 1) + " of " + pages;
            Vector2 pageAt = Layout.Scene(OutfitTextX - 3f, -26f + 595f);      // [xur] "Item page number" at (-3, 595) in it
            Gated(false, delegate(float o, float age) { PageNumber(pageText, pageAt, 1f); });
            ghosts.Draw();                      // Left/RightOffscreenItems: the neighbouring pages (Ghosts.cs)
            // shadowLeft / shadowRight, per side once the page settles (sub_920D5C90)
            bool sl = true, sr = true;
            if (shadowsSettled) { sl = n >= 1; sr = n >= 3; }
            if (sl) Chrome.FloorShadow(ui, tileArt, 114f - 0.224976f, 180f);
            if (sr) Chrome.FloorShadow(ui, tileArt, 717f - 0.224976f, 180f);
            if (ScreenTakesTriggers)
            {
                bool lt = input.TriggerLeft != 0, rt = input.TriggerRight != 0;
                Gated(false, delegate(float o, float age) { Chrome.RotateHandle(ui, tileArt, Chrome.PickerHost, lt, rt); });
            }
            bool prev = page > 0, next = page < pages - 1;
            Rectangle hl = OutfitHandleRect(false), hr = OutfitHandleRect(true);
            Gated(true, delegate(float o, float age) { Chrome.PageHandles(ui, tileArt, prev, next, age, o, hl, hr); });
            // `items`' tiles in the file's order (ItemButton4 first), the focused one last
            for (int pass = 0; pass < 2; pass++)
                for (int i = 3; i >= 0; i--)
                {
                    if (i >= n || (i == cursor) != (pass == 1)) continue;
                    DrawOutfitTile(i);
                }
            DrawTooltips();                     // the tiles' tooltips (StudioTooltips.cs)
            ui.Legend(EditorLegend(stage.Screen, OutfitEntry(cursor) >= 0));
        }

        /// <summary>One tile through GridEditorBoxartButton: the panel, the preview and the chosen tick.</summary>
        private void DrawOutfitTile(int i)
        {
            int e = OutfitEntry(i);
            Texture2D panelU = tileArt.Named("box_art_panel"), panelS = tileArt.Named("box_art_panel_selected");
            Texture2D pic = e == -1 ? tileArt.Named("saveoutfit_selected") : cards.Get(outfits.Items[e].Manifest, Framing.FullBody);
            bool chosen = e >= 0 && outfits.Find(session.CommittedBytes) == e;
            XuiPlayer p = controls[i];
            if (p != null && panelU != null && panelS != null)
            {
                Matrix ctrl = ControlAt(OutfitTileScene(i));
                Elem(p, ctrl, "button bg unselected", delegate(float o) { Fit(panelU, 178f, 233f, o); });
                Elem(p, ctrl, "button bg selected", delegate(float o) { Fit(panelS, 178f, 233f, o); });
                Elem(p, ctrl, "button bg no select", delegate(float o) { Fit(panelS, 178f, 233f, o); });
                Elem(p, ctrl, "preview", delegate(float o)
                {
                    if (pic != null) Fit(pic, 140.9f, 193f, o);
                    else ui.Spinner(new Vector2(70.45f, 96.5f), 12, time, Palette.TextGrey * o);
                });
                if (chosen) Elem(p, ctrl, "chosen", delegate(float o) { OwnSize(tileArt.Named("item_selected_tick"), o); });
                return;
            }
            // [ours] settled, without the art or the visuals
            Rectangle r = OutfitTileRect(i);
            bool focused = i == cursor;
            ui.Rounded(r, focused ? Palette.SelectGreen : Palette.CellGrey);
            if (pic != null) ui.Batch.Draw(pic, new Rectangle(r.X + 14, r.Y + 14, r.Width - 28, r.Height - 28), Color.White);
            else ui.TextAt(XuiStyle.TileLabel, e == -1 ? "+" : "...", new Vector2(r.Center.X, r.Center.Y), Palette.SceneText);   // [ours], as a tile's label
        }

        /// <summary>SizeMode 16: fitted into (0, 0, w, h) keeping its aspect, centred.</summary>
        private void Fit(Texture2D t, float w, float h, float o)
        {
            float s = Math.Min(w / t.Width, h / t.Height);
            float fw = t.Width * s, fh = t.Height * s;
            XuiDraw.Image(ui, t, (w - fw) / 2f, (h - fh) / 2f, fw, fh, Color.White * o);
        }
    }
}
