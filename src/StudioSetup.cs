// The setup, laid out as the console's own setup wizard (dash.xex's oobe package): a pivot
// header of steps, then a page per step with a header, a body and tiles.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed partial class StudioGame
    {
        // Busy: the ring and the line (downloading or installing); Stopped: cancelled or failed
        private enum SetupPage { Busy, Done, Stopped, Options }
        private static readonly string[] SetupSteps = { "download", "install", "done" };

        private List<SetupPart> setupParts = new List<SetupPart>();
        private SetupArt setupArt;                          // the console's look, from the update found
        private SetupPage setupPage;
        private int setupStep;                               // the pivot's step under Busy / Stopped / Options
        private string setupUpdate;                          // the update folder found, downloaded or chosen
        private string setupUpdateWhere = "";                // how it was found
        private string setupOwn;                             // the player's own copy, for the next install
        private string setupFont;                            // the font file chosen
        private bool setupBusy;                              // installing
        private string setupLine = "";                       // the busy page's one line
        private string setupStopHeader = "", setupStopBody = "";   // the Stopped page's texts
        private bool setupStopTool;                          // a program blocked or missing: Windows Security, Try Again
        private SetupPage setupOptionsBack;                  // where B leaves More Options for
        private bool setupAuto;                              // opened by itself: Done goes on by itself
        private float setupPageSince;                        // the page's start: frame 0 of the ring
        private float setupBgSince;                          // the background's first frame (ImgBG's track)
        private bool setupNoArt;                             // --setup-shots: a new player's first page

        // the tiles: what each says and does, their focus and the frames they play
        private struct SetupTile { public string Label, Icon; public Action Act; }
        private readonly List<SetupTile> setupTiles = new List<SetupTile>();
        private int setupFocus;
        private int setupPressed = -1;                      // the tile whose press plays
        private readonly float[] tileFrom = new float[2], tileTo = new float[2], tileSince = new float[2];

        // ---- the numbers: oobeBackground.xur's ImgBG, the imgXenon logo, the _Twist pivot header ----
        private static readonly Rectangle SetupLogo = new Rectangle(1015, 54, 170, 35);
        private const float TwistX = 180f, TwistY = 76f, TwistH = 84f;
        // HubScene_Twist's grid: its columns alternate an item sized to its text and 36
        private const float TwistGap = 36f;
        // [ours] the field without the console's background: the scenes' dark (#FF0F0F0F)
        private static readonly Color SetupField = new Color(0x0F, 0x0F, 0x0F);

        // dash.xex sub_927AA928: the page at x = 96 + (1088 - width) / 2, y = 114
        private static Vector2 PageAt(float width) { return new Vector2(96f + (1088f - width) * 0.5f, 114f); }

        // oobeCompleteLive.xur: the header, the body and two 148 x 148 tiles
        private const float PageW = 1028f;
        private static readonly Vector4 HeaderBox = new Vector4(64f, 82f, 600f, 68f);
        private static readonly Vector4 BodyBox = new Vector4(63f, 150f, 600f, 200f);
        private static readonly Vector2[] TileAt = { new Vector2(700f, 148f), new Vector2(700f, 300f) };
        private const float TileSize = 148f;
        // OobeNetworkSelection.xur: the loading ring and its one line
        private const float BusyW = 1042f;

        // the dash skin's text styles (Segoe Light 28, 18, 20, 24)
        private static readonly Color OobeWhite = Color.White, OobeText = Palette.SceneText;

        // ---- opening, looking, choosing ---------------------------------------------------------

        /// <summary>Installs straight away when a part is missing, else shows Done; `auto` goes on by itself.</summary>
        private void OpenSetup(bool auto = false)
        {
            setupParts = SetupStatus.Read(FindAssetDir());
            setupAuto = auto;
            LookForUpdate();
            state = State.Setup;
            if (SetupStatus.Complete(setupParts)) ShowSetupPage(SetupPage.Done);
            else InstallSetup();
        }

        private void LookForUpdate()
        {
            if (setupArt != null || setupNoArt) return;
            string where;
            string found = SetupArt.LookFirst(out where);
            if (found != null) { UseUpdate(found); setupUpdateWhere = where; }
        }

        /// <summary>The update in `folder` (SetupArt.Resolve's answer): its look read once.</summary>
        private void UseUpdate(string folder)
        {
            setupUpdate = folder;
            if (setupArt != null && setupArt.Folder == folder) return;
            if (setupArt != null) setupArt.Dispose();
            setupArt = SetupArt.Load(GraphicsDevice, folder);
            setupBgSince = time;                                                    // the background is made now
            Console.WriteLine("[studio] " + setupArt.Status);
            foreach (string k in new[] { "Legend_A", "Legend_B", "Legend_X", "Legend_Y" }) tileArt.Lend(k, setupArt.Named(k));
            tileArt.Lend("outerRing", setupArt.Ring);
        }

        /// <summary>More Options' Use My Own Copy: the update's zip, or dash.xex in $SystemUpdate.</summary>
        private void ChooseOwnCopy()
        {
            if (input.Suppress) { Console.WriteLine("[studio] setup: no file dialog in a scripted run"); return; }
            string p = FileDialogs.PickUpdate();
            if (p == null) return;
            setupOwn = p;
            InstallSetup();
        }

        /// <summary>More Options' "Console Font": xenonjklatin.xtt or a zip holding it; installing starts.</summary>
        private void ChooseFont()
        {
            if (input.Suppress) { Console.WriteLine("[studio] setup: no file dialog in a scripted run"); return; }
            string p = FileDialogs.PickFontBase();
            if (p == null) return;
            setupFont = p;
            InstallSetup();
        }

        // the install runs on its own thread; the page reads its line and waits
        private volatile string setupWorkLine = "";
        private volatile int setupWorkStep;
        private volatile string setupWorkUpdate;              // the update the thread found, for the look
        private volatile bool setupWorkDone, setupWorkCancelled, setupCancel;
        private volatile string setupWorkFailed, setupWorkFailedStep;
        private volatile string setupWorkTool;                // ToolBlockedException's file, its Missing below
        private volatile bool setupWorkToolMissing;
        private bool setupWorking;                            // an install thread runs (not the shots' busy page)
        private bool setupInstalled;                          // something was installed: Go Play restarts

        /// <summary>The update when none is here, then the installers in order (SetupInstall.cs).</summary>
        private void InstallSetup()
        {
            // a scripted run never installs, except the install test, into scratch folders
            if (Scripted && !(setupInstallTest && SetupInstall.ScratchOnly()))
            { Console.WriteLine("[studio] setup: no install in a scripted run"); return; }
            string own = setupOwn;
            setupOwn = null;
            string update = own == null ? setupUpdate : null, font = setupFont;
            setupStep = setupWorkStep = update == null ? 0 : 1;
            ShowSetupPage(SetupPage.Busy);
            setupBusy = true;
            setupWorking = true;
            setupWorkDone = setupWorkCancelled = setupCancel = false;
            setupWorkFailed = setupWorkFailedStep = setupWorkTool = null;
            setupWorkUpdate = null;
            SetupInstall.Cancelled = () => setupCancel;
            Func<bool> cancelled = () => setupCancel;
            string have = FindAssetDir();
            bool items = have == null || !File.Exists(Path.Combine(have, "tocassetdependencies.strb"));   // as SetupStatus counts them
            setupLine = setupWorkLine = "getting ready";
            System.Threading.Thread t = new System.Threading.Thread(delegate ()
            {
                string step = "";
                Action check = delegate { if (setupCancel) throw new OperationCanceledException(); };
                try
                {
                    Action<string> say = s => Console.WriteLine("[studio] setup: " + s);
                    if (update == null)
                    {
                        step = own != null ? "your copy of the update" : "the Xbox 360 files";
                        if (own != null)
                        {
                            setupWorkLine = "reading your copy of the update";
                            update = SetupDownload.UseOwn(own, say, cancelled);
                            if (update == null) throw new InvalidDataException(Path.GetFileName(own) + " isn't an Xbox 360 system update");
                        }
                        else
                            update = SetupDownload.Fetch(s =>
                            {
                                say(s);
                                if (s.StartsWith("downloading", StringComparison.Ordinal) || s.StartsWith("unpacking", StringComparison.Ordinal)) setupWorkLine = s;
                            }, cancelled);
                        setupWorkUpdate = update;
                    }
                    setupWorkStep = 1;
                    if (items)
                    {
                        step = "the avatar items";
                        setupWorkLine = "downloading the avatar items";
                        List<string> failed = SetupInstall.Items(s =>
                        {
                            say(s);
                            if (s.StartsWith("downloading the avatar items", StringComparison.Ordinal)) setupWorkLine = s;
                        }, null, cancelled);
                        check();
                        if (failed.Count > 0) throw new IOException(failed.Count + " could not be downloaded, e.g. " + failed[0]);
                    }
                    // [ours] each step's line in plain words; the log keeps the detail
                    step = "the avatar items";
                    setupWorkLine = "setting up the avatar items";
                    SetupInstall.Catalogue(update, say);
                    check();
                    step = "the avatars";
                    setupWorkLine = "setting up the avatars";
                    SetupInstall.ConsoleTablesInstall(update, say);
                    check();
                    step = "the editor's pictures";
                    setupWorkLine = "setting up the editor's pictures and moves";
                    SetupInstall.EditorArt(update, say);
                    check();
                    if (font != null)
                    {
                        step = "your console font";
                        setupWorkLine = "setting up your console font";
                        SetupInstall.Font(font, update, say);
                        check();
                    }
                    step = "the lighting";
                    setupWorkLine = "setting up the lighting";
                    SetupShaders.Install(update, say);
                    check();
                    step = "the sounds";
                    setupWorkLine = "setting up the sounds";
                    SetupInstall.EditorSounds(update, s =>
                    {
                        say(s);
                        if (s.StartsWith("downloading a helper", StringComparison.Ordinal) || s.StartsWith("preparing the editor's sounds", StringComparison.Ordinal))
                            setupWorkLine = s;
                    });
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("[studio] setup: stopped during " + step);
                    setupWorkCancelled = true;
                }
                catch (ToolBlockedException e)
                {
                    Console.WriteLine("[studio] setup: " + step + " failed: " + e);
                    setupWorkFailedStep = step;
                    setupWorkFailed = e.Message;
                    setupWorkToolMissing = e.Missing;
                    setupWorkTool = e.Tool;
                }
                catch (Exception e)
                {
                    Console.WriteLine("[studio] setup: " + step + " failed: " + e);
                    setupWorkFailedStep = step;
                    setupWorkFailed = e.Message;
                }
                setupWorkDone = true;
            });
            t.IsBackground = true;
            t.Start();
        }

        /// <summary>Each frame while installing: the worker's line and step, and the install's end.</summary>
        private void PollInstall()
        {
            if (!setupWorking) return;
            setupLine = setupCancel ? "stopping" : setupWorkLine;
            setupStep = setupWorkStep;
            string u = setupWorkUpdate;
            if (u != null && u != setupUpdate) { UseUpdate(u); setupUpdateWhere = "downloaded"; }
            if (!setupWorkDone) return;
            setupWorking = false;
            setupBusy = false;
            if (setupStep == 1) setupInstalled = true;
            setupParts = SetupStatus.Read(FindAssetDir());
            setupStopTool = false;
            if (setupWorkCancelled)
            {
                setupStopHeader = "Setup stopped";
                setupStopBody = setupStep == 0
                    ? "What was downloaded is kept: Try Again carries on from there."
                    : "Try Again finishes it, or come back later with Y on the New Avatar card.";
                ShowSetupPage(SetupPage.Stopped);
            }
            else if (setupWorkTool != null) StopForTool(setupWorkTool, setupWorkToolMissing, setupWorkFailedStep);
            else if (setupWorkFailed != null)
            {
                bool net = setupWorkFailed.IndexOf("remote", StringComparison.OrdinalIgnoreCase) >= 0 || setupWorkFailed.IndexOf("connect", StringComparison.OrdinalIgnoreCase) >= 0
                        || setupWorkFailed.IndexOf("resolved", StringComparison.OrdinalIgnoreCase) >= 0 || setupWorkFailed.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0
                        || setupWorkFailed.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0;
                setupStopHeader = "Setup couldn't finish";
                // the error's own words go on More Options, for whoever helps the player
                setupStopBody = "Something went wrong with " + setupWorkFailedStep + "." +
                                (net ? " Check that this PC is connected to the internet, then choose Try Again." : " Choose Try Again, or More Options.");
                ShowSetupPage(SetupPage.Stopped);
            }
            else ShowSetupPage(SetupPage.Done);
        }

        /// <summary>The Stopped page for a program missing or blocked, with Windows Security and Try Again.</summary>
        private void StopForTool(string tool, bool missing, string step)
        {
            setupStopHeader = "Setup couldn't finish";
            setupStopBody = ToolStopBody(tool, missing, step);
            setupStopTool = true;
            ShowSetupPage(SetupPage.Stopped);
        }

        /// <summary>[ours] what happened and the way back, in Windows Security's own words (its Protection history)</summary>
        private static string ToolStopBody(string tool, bool missing, string step)
        {
            string what = tool + ", an open-source tool the setup needs for " + step;
            string fix = " If you got Avatar Studio from its GitHub page, choose Windows Security, open the entry that lists " + tool +
                         " and select Actions > Restore. Then choose Try Again.";
            return missing
                ? what + ", is missing; antivirus sometimes removes it by mistake." + fix
                : "Your antivirus blocked " + what + "; it sometimes mistakes the tool for a threat." + fix;
        }

        /// <summary>Windows Security's Protection history ("history", an entry point of its windowsdefender: URI).</summary>
        private void OpenWindowsSecurity()
        {
            if (Scripted || input.Suppress) { Console.WriteLine("[studio] setup: no Windows Security in a scripted run"); return; }
            foreach (string uri in new[] { "windowsdefender://history", "ms-settings:windowsdefender" })
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true });
                    return;
                }
                catch (Exception e) { Console.WriteLine("[studio] setup: could not open " + uri + ": " + e.Message); }
        }

        /// <summary>Go Play: the gallery, or a restart after an install, so everything installed loads.</summary>
        private void SetupGoPlay()
        {
            if (setupInstalled && !Scripted)
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.Reflection.Assembly.GetEntryAssembly().Location)
                    { WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory, UseShellExecute = false });
                    Exit();
                    return;
                }
                catch (Exception e) { Console.WriteLine("[studio] setup: could not start again: " + e.Message); }
            }
            EnterGallery();
        }

        private bool FontInstalled { get { return setupParts.Count > 5 && setupParts[5].Installed; } }

        private void ShowOptions()
        {
            setupOptionsBack = setupPage;
            ShowSetupPage(SetupPage.Options);
        }

        /// <summary>A page and its tiles; the first tile takes the focus.</summary>
        private void ShowSetupPage(SetupPage page)
        {
            setupPage = page;
            setupPageSince = time;
            setupTiles.Clear();
            setupPressed = -1;
            switch (page)
            {
                case SetupPage.Stopped:
                    if (setupStopTool) Tile("Windows Security", null, OpenWindowsSecurity);
                    Tile("Try Again", null, InstallSetup);
                    if (!setupStopTool) Tile("More Options", null, ShowOptions);
                    break;
                case SetupPage.Options:
                    Tile("Use My Own Copy", "ico_64x_AllDevices", ChooseOwnCopy);
                    Tile("Console Font", null, ChooseFont);
                    break;
                case SetupPage.Done:
                    Tile("Go Play", "ico_64x_CustomAvatar", SetupGoPlay);
                    Tile("More Options", null, ShowOptions);
                    break;
            }
            setupFocus = 0;
            for (int i = 0; i < 2; i++) { tileFrom[i] = tileTo[i] = 0f; tileSince[i] = time; }
            if (setupTiles.Count > 0) PlayTile(0, InitFocusFrom, InitFocusTo);
        }

        private void Tile(string label, string icon, Action act) { setupTiles.Add(new SetupTile { Label = label, Icon = icon, Act = act }); }

        // ---- input --------------------------------------------------------------------------------

        private void UpdateSetup()
        {
            PollInstall();
            TickInstallWait();
            if (setupBusy)
            {
                // [ours] B stops the install
                if (input.Back && setupWorking && !setupCancel) setupCancel = true;
                return;
            }
            if (setupPressed >= 0)
            {
                // XUI_Button_OnPress: the action waits for the press range's end (XuiVisuals.cs, PRESSING)
                if (TileFrame(setupPressed) >= PressTo) { int k = setupPressed; setupPressed = -1; PlayTile(k, FocusTo, FocusTo); setupTiles[k].Act(); }
                return;
            }
            // [ours] after opening by itself, Done goes on to the studio unless the player acts
            if (setupPage == SetupPage.Done && setupAuto && setupInstalled && time - setupPageSince > AutoGoPlay && !Scripted)
            { setupAuto = false; SetupGoPlay(); return; }
            int n = setupTiles.Count;
            for (int i = 0; i < n; i++)
                if (input.MouseIn(TileRect(i)))
                {
                    if (input.MouseMoved && i != setupFocus) { setupAuto = false; MoveSetupFocus(i); }
                    if (input.MouseClicked) { MoveSetupFocus(i); PressSetupTile(); return; }
                }
            if (n > 1 && input.Down && setupFocus < n - 1) { setupAuto = false; MoveSetupFocus(setupFocus + 1); }
            else if (n > 1 && input.Up && setupFocus > 0) { setupAuto = false; MoveSetupFocus(setupFocus - 1); }
            else if (input.Select && n > 0) PressSetupTile();
            else if (input.Back)
            {
                // [ours] More Options goes back where it came from; the rest leave for the gallery
                if (setupPage == SetupPage.Options) ShowSetupPage(setupOptionsBack);
                else if (setupPage != SetupPage.Done || !setupInstalled) EnterGallery();
            }
        }

        private const float AutoGoPlay = 3f;              // [ours] seconds the Done page shows before going on

        private void MoveSetupFocus(int i)
        {
            PlayTile(setupFocus, KillFocusFrom, KillFocusTo);
            setupFocus = i;
            PlayTile(i, FocusFrom, FocusTo);
        }

        private void PressSetupTile()
        {
            setupPressed = setupFocus;
            PlayTile(setupFocus, PressFrom, PressTo);
        }

        // ---- btn_LiveTile_oobe's timelines (the dash skin), 60 frames a second -----------------------
        // ranges: Normal 0..1, InitFocus 2..18, Focus 19..35, Press 36..55, KillFocus 56..70
        private const float InitFocusFrom = 2f, InitFocusTo = 18f, FocusFrom = 19f, FocusTo = 35f, PressFrom = 36f, PressTo = 55f,
                            KillFocusFrom = 56f, KillFocusTo = 70f;

        private void PlayTile(int i, float from, float to)
        {
            if (i < 0 || i > 1) return;
            tileFrom[i] = from; tileTo[i] = to; tileSince[i] = time;
        }

        private float TileFrame(int i) { return Math.Min(tileFrom[i] + (time - tileSince[i]) * 60f, tileTo[i]); }

        // keys: frame, curve, mode, values; Background: Scale and FillColor
        private static readonly float[][] TileBackground =
        {
            K(0, 1, 0, 1f, 0x00, 0x5A, 0x00), K(2, 3, 1, 1f, 0x00, 0x8A, 0x00), K(18, 4, 1, 1.04f, 0x00, 0x8A, 0x00),
            K(19, 4, 1, 1f, 0x00, 0x5A, 0x00), K(35, 4, 1, 1.04f, 0x00, 0x8A, 0x00), K(43, 4, 1, 1f, 0x00, 0x8A, 0x00),
            K(55, 4, 1, 1.04f, 0x00, 0x8A, 0x00), K(70, 1, 0, 1f, 0x00, 0x5A, 0x00),
        };
        // _slotGroup: Scale, Opacity
        private static readonly float[][] TileGroup =
        {
            K(0, 1, 0, 1f, 0.6f), K(2, 4, 1, 1f, 0.6f), K(18, 4, 1, 1.04f, 1f), K(19, 4, 1, 1f, 0.6f), K(35, 4, 1, 1.04f, 1f),
            K(43, 4, 1, 1f, 1f), K(55, 4, 1, 1.04f, 1f), K(70, 0, 0, 1f, 0.6f),
        };
        // Shadow: Opacity, Scale
        private static readonly float[][] TileShadow =
        {
            K(0, 1, 0, 0f, 1f), K(2, 4, 1, 0f, 1f), K(18, 4, 1, 0.4f, 1.04f), K(19, 4, 1, 0f, 1f), K(35, 4, 1, 0.4f, 1.04f),
            K(43, 4, 1, 0.1f, 1f), K(55, 4, 1, 0.4f, 1.04f), K(70, 1, 0, 0f, 1f),
        };

        // figFlash: Opacity, Scale (a solid #FFEBEBEB figure)
        private static readonly float[][] TileFlash =
        {
            K(0, 1, 0, 0f, 1f), K(2, 4, 1, 0f, 1f), K(9, 3, 1, 0.08f, 1.02f), K(18, 3, 1, 0f, 1.04f), K(19, 3, 1, 0f, 1f),
            K(26, 3, 1, 0.08f, 1.02f), K(35, 3, 1, 0f, 1.04f), K(43, 3, 1, 0.08f, 1f), K(55, 3, 1, 0f, 1.04f),
            K(62, 3, 1, 0.08f, 1.02f), K(70, 3, 1, 0f, 1f),
        };
        // imgCornerHighlight: Scale, Opacity (focus_state_white.png, stretched)
        private static readonly float[][] TileHighlight =
        {
            K(0, 1, 0, 1f, 0f), K(9, 3, 1, 1.02f, 0f), K(18, 3, 1, 1.04f, 0.44f), K(19, 1, 0, 1.02f, 0f), K(26, 3, 1, 1.02f, 0f),
            K(35, 3, 1, 1.04f, 0.44f), K(43, 3, 1, 1f, 0f), K(55, 3, 1, 1.04f, 0.44f), K(70, 1, 0, 1f, 0f), K(120, 0, 0, 1f, 0f),
        };

        // oobeBackground's ImgBG: Opacity
        private static readonly float[][] BgOpacity = { K(0, 1, 0, 1f), K(1, 0, 0, 0.5f), K(60, 1, 0, 1f), K(70, 1, 0, 1f) };

        private static float[] K(float frame, int curve, int mode, params float[] v)
        {
            float[] k = new float[3 + v.Length];
            k[0] = frame; k[1] = curve; k[2] = mode;
            Array.Copy(v, 0, k, 3, v.Length);
            return k;
        }

        /// <summary>XUI_Timeline_Interpolate: the key at or before the frame, eased to the next.</summary>
        private static float[] Sample(float[][] keys, float frame)
        {
            int a = 0;
            while (a + 1 < keys.Length && keys[a + 1][0] <= frame + 0.0001f) a++;
            float[] ka = keys[a], o = new float[ka.Length - 3];
            Array.Copy(ka, 3, o, 0, o.Length);
            if (a + 1 >= keys.Length || (int)ka[1] == 1) return o;
            float[] kb = keys[a + 1];
            float t = MathHelper.Clamp((frame - ka[0]) / (kb[0] - ka[0]), 0f, 1f);
            int curve = (int)ka[1];
            Func<float, float> f = u => curve == 3 ? u * u : curve == 4 ? u * u * u : u;
            float e = (int)ka[2] == 1 ? 1f - f(1f - t) : f(t);
            for (int i = 0; i < o.Length; i++) o[i] += (kb[3 + i] - o[i]) * e;
            return o;
        }

        // ---- drawing ----------------------------------------------------------------------------

        private bool SetupHasArt { get { return setupArt != null && !setupNoArt; } }

        /// <summary>The dashboard's "Segoe Light" (the update's SegoeXbox-Light.xtt), else Windows' own.</summary>
        private RuntimeFont SegoeLight(float pt, int style)
        {
            return ui.Font(SetupHasArt ? setupArt.SegoeLight : null, "Segoe UI", true, pt, style);
        }

        private Rectangle TileRect(int i)
        {
            Vector2 o = PageAt(PageW) + TileAt[i];
            return new Rectangle((int)o.X, (int)o.Y, (int)TileSize, (int)TileSize);
        }

        private void DrawSetup()
        {
            // the frame: the console's background and logo, or the field
            if (SetupHasArt && setupArt.Background != null)
            {
                float bgo = Sample(BgOpacity, (time - setupBgSince) * 60f)[0];
                ui.Rect(new Rectangle(0, 0, Layout.W, Layout.H), Color.Black);
                ui.Batch.Draw(setupArt.Background, new Rectangle(0, 0, Layout.W, Layout.H), Color.White * bgo);
            }
            else
                ui.Rect(new Rectangle(0, 0, Layout.W, Layout.H), SetupField);
            if (SetupHasArt && setupArt.Logo != null) DrawFitted(setupArt.Logo, SetupLogo, 1f);

            DrawTwist();

            if (setupPage == SetupPage.Busy) DrawBusyPage();
            else DrawSetupPage();
        }

        /// <summary>The pivot's current step: Done is the last, the others the step they are on.</summary>
        private int SetupStepShown { get { return setupPage == SetupPage.Done ? 2 : setupStep; } }

        /// <summary>The pivot header: the steps from the twist's left, 36 apart.</summary>
        private void DrawTwist()
        {
            string[] steps = SetupSteps;
            float x = TwistX;
            for (int i = 0; i < steps.Length; i++)
            {
                bool current = i == SetupStepShown;
                RuntimeFont f = current ? ui.Xui(24f, 0x1411) : SegoeLight(24f, 0x1411);
                float w = f.XuiWidth(steps[i]);
                Color shadow = current ? new Color(0x0F, 0x0F, 0x0F, 0x96) : new Color(0x0F, 0x0F, 0x0F, 0x5A);
                f.DrawXui(ui.Batch, steps[i], new Vector2(x, TwistY), 1f, w, TwistH, 0x1411, OobeText * (current ? 1f : 0.5f), shadow);
                x += w + TwistGap;
            }
        }

        private void DrawSetupPage()
        {
            Vector2 o = PageAt(PageW);
            string header, body;
            SetupTexts(out header, out body);
            RuntimeFont hf = SegoeLight(28f, 256);
            hf.DrawXui(ui.Batch, hf.WrapXui(header, HeaderBox.Z), o + new Vector2(HeaderBox.X, HeaderBox.Y), 1f, HeaderBox.Z, HeaderBox.W, 256,
                       OobeWhite, Palette.DropShadow, -2);
            RuntimeFont bf = SegoeLight(18f, 256);
            bf.DrawXui(ui.Batch, bf.WrapXui(body, BodyBox.Z), o + new Vector2(BodyBox.X, BodyBox.Y), 1f, BodyBox.Z, BodyBox.W, 256,
                       OobeWhite, Palette.DropShadow, -2);
            for (int i = 0; i < setupTiles.Count; i++) DrawSetupTile(i);
            if (setupPage == SetupPage.Done && setupInstalled) ui.Legend("A Select");
            else ui.Legend("A Select", "B Back");
        }

        /// <summary>[ours] what each page says, in words any player follows</summary>
        private void SetupTexts(out string header, out string body)
        {
            header = body = "";
            switch (setupPage)
            {
                case SetupPage.Stopped:
                    header = setupStopHeader;
                    body = setupStopBody;
                    break;
                case SetupPage.Options:
                    header = "More options";
                    body = "Already have the Xbox 360 system update (SystemUpdate_17559_USB.zip)? Use your own copy instead of downloading it." +
                           "\n\nThe console's own font, xenonjklatin.xtt, comes only from a console; if you have it, the editor's text uses it." +
                           (FontInstalled ? " Yours is installed." : "") +
                           (setupWorkFailed != null ? "\n\nWhat went wrong last time, for whoever helps you: " + setupWorkFailed : "");
                    break;
                case SetupPage.Done:
                    header = "You're ready to go";
                    List<string> missing = new List<string>();
                    foreach (SetupPart p in setupParts) if (!p.Installed && p != setupParts[5]) missing.Add(p.Name.ToLowerInvariant());
                    body = missing.Count == 0 ? "Everything Avatar Studio needs is installed. Have fun!"
                         : "Installed. Not in yet: " + string.Join(", ", missing.ToArray()) + ".";
                    break;
            }
        }

        /// <summary>btn_LiveTile_oobe at its frame: shadow, background figure, icon and label.</summary>
        private void DrawSetupTile(int i)
        {
            Rectangle r = TileRect(i);
            Vector2 c = new Vector2(r.X + TileSize / 2f, r.Y + TileSize / 2f);
            float frame = TileFrame(i);
            float[] bg = Sample(TileBackground, frame), grp = Sample(TileGroup, frame), sh = Sample(TileShadow, frame);

            // Shadow: common://hubslotshadow.png at (-31, -31) 210 x 210, offsets 31, NoCenter, ColorFactor #FF000000
            Texture2D shadow = SetupHasArt ? setupArt.Named("hubslotshadow") : null;
            if (shadow != null && sh[0] > 0f)
            {
                float s = 210f * sh[1];
                ui.NineGrid(shadow, new Rectangle((int)(c.X - s / 2f), (int)(c.Y - s / 2f), (int)s, (int)s), 31, 31, 31, 31, sh[1], Color.Black * sh[0], true);
            }
            // Background: 148 x 148, its FillColor
            float bs = TileSize * bg[0];
            ui.Rect(new Rectangle((int)Math.Round(c.X - bs / 2f), (int)Math.Round(c.Y - bs / 2f), (int)Math.Round(bs), (int)Math.Round(bs)),
                    new Color((int)bg[1], (int)bg[2], (int)bg[3]));
            // _slotGroup: the 48 x 48 icon and the label
            float gs = grp[0], go = grp[1];
            Vector2 tl = c - new Vector2(TileSize / 2f, TileSize / 2f) * gs;
            Texture2D icon = SetupHasArt && setupTiles[i].Icon != null ? setupArt.Named(setupTiles[i].Icon) : null;
            if (icon != null)
                DrawFitted(icon, new Rectangle((int)Math.Round(tl.X + 50f * gs), (int)Math.Round(tl.Y + 40f * gs), (int)Math.Round(48f * gs), (int)Math.Round(48f * gs)), go);
            RuntimeFont lf = ui.Xui(16f, 0x8004000);
            lf.DrawXui(ui.Batch, lf.WrapXui(setupTiles[i].Label, 128f), tl + new Vector2(10f, 86f) * gs, gs, 128f, 58f, 0x8004000,
                       OobeText * go, Palette.DropShadow, -4);
            // over them, in the visual's order: figFlash, then imgCornerHighlight (the focused tile's sheen)
            float[] fl = Sample(TileFlash, frame), hl = Sample(TileHighlight, frame);
            if (fl[0] > 0f)
            {
                float fs = TileSize * fl[1];
                ui.Rect(new Rectangle((int)Math.Round(c.X - fs / 2f), (int)Math.Round(c.Y - fs / 2f), (int)Math.Round(fs), (int)Math.Round(fs)), OobeText * fl[0]);
            }
            Texture2D sheen = SetupHasArt ? setupArt.Named("focus_state_white") : null;
            if (sheen != null && hl[1] > 0f)
            {
                float hs = TileSize * hl[0];
                ui.Batch.Draw(sheen, new Rectangle((int)Math.Round(c.X - hs / 2f), (int)Math.Round(c.Y - hs / 2f), (int)Math.Round(hs), (int)Math.Round(hs)),
                              Color.White * hl[1]);
            }
        }

        /// <summary>The busy page: the loading ring and its one line.</summary>
        private void DrawBusyPage()
        {
            Vector2 o = PageAt(BusyW);
            Texture2D ring = SetupHasArt ? setupArt.Ring : null;
            float frame = (time - setupPageSince) * 60f;
            float t = Math.Min(frame / 39f, 1f);
            const double E = 2.718281745910645;                                   // ProgressBox's ease, curve 10, mode 1
            float v = 1f - (float)((Math.Pow(E, 3.0 * (1f - t)) - 1.0) / (Math.Pow(E, 3.0) - 1.0));
            Vector2 centre = o + new Vector2(482f + 36f, 215f + 36f);
            if (!setupBusy) { }                                                     // [ours] stopped (failed): no ring
            else if (ring != null)
            {
                float angle = MathHelper.PiOver2 * (frame / 13f) % MathHelper.TwoPi;
                float s = (0.9f + 0.1f * v) * 72f / ring.Width;
                Vector2 org = new Vector2(ring.Width / 2f, ring.Height / 2f);
                ui.Batch.Draw(ring, centre + new Vector2(0f, 1f), null, Color.Black * (0.1f * v), angle, org, s, SpriteEffects.None, 0f);
                ui.Batch.Draw(ring, centre, null, Color.White * v, angle, org, s, SpriteEffects.None, 0f);
            }
            else ui.Spinner(centre, 22f, time, OobeText * v);                       // [ours] without the ring
            // labMetaHeader, centred across and down
            RuntimeFont f = SegoeLight(20f, 0x1400);
            f.DrawXui(ui.Batch, f.WrapXui(setupLine, 600f), o + new Vector2(219f, 292f), 1f, 600f, 101f, 0x1400, OobeText, Palette.DropShadow, -2);
            if (setupBusy && !setupCancel) ui.Legend("B Cancel");
            else ui.Legend();
        }

        /// <summary>SizeMode 16: the texture fitted into `box` keeping its aspect, centred.</summary>
        private void DrawFitted(Texture2D t, Rectangle box, float opacity)
        {
            float k = Math.Min(box.Width / (float)t.Width, box.Height / (float)t.Height);
            float w = t.Width * k, h = t.Height * k;
            ui.Batch.Draw(t, new Rectangle((int)Math.Round(box.X + (box.Width - w) / 2f), (int)Math.Round(box.Y + (box.Height - h) / 2f),
                                           (int)Math.Round(w), (int)Math.Round(h)), Color.White * opacity);
        }

        // ---- --setup-install-test: Install for real, hidden, into scratch folders ----------------
        private bool setupInstallTest;

        /// <summary>The setup as a new player meets it: every part missing, so it installs at once.</summary>
        private void BuildSetupInstallTest()
        {
            SetupArt.IgnoreRemembered = true;
            script.Add(P(1.0f, delegate
            {
                if (!SetupInstall.ScratchOnly()) { Console.WriteLine("[studio] setup install test: refused, the folders are not all scratch ones"); Exit(); return; }
                setupFont = Environment.GetEnvironmentVariable("AVATAR_STUDIO_TEST_FONT");
                setupOwn = Environment.GetEnvironmentVariable("AVATAR_STUDIO_TEST_OWN");
                if (string.IsNullOrEmpty(setupOwn)) setupOwn = null;
                OpenSetup(true);
                Console.WriteLine("[studio] setup install test: " + (setupOwn != null || setupWorking ? "installing" : "not installing (page " + setupPage + ")") +
                                  ", the update " + (setupUpdate != null ? "found (" + setupUpdateWhere + ")" : "to fetch"));
            }));
            // the next steps wait for the install (the busy page's look is --setup-shots')
            script.Add(P(2.0f, delegate { }));
            WaitThen(delegate
            {
                pendingShot = Path.Combine(shotsDir, "setup-install-done.png");
                Console.WriteLine("[studio] setup install test: " + (setupPage == SetupPage.Done ? "done page" : "stopped: " + setupStopHeader + ": " + setupStopBody));
                Console.WriteLine("[studio] setup install test: the update " + (setupUpdate ?? "(none)") + " (" + setupUpdateWhere + ")");
                foreach (SetupPart p in setupParts) Console.WriteLine("[studio] setup install test: " + (p.Installed ? "ok " : "-- ") + p.Name + "  " + p.Detail);
            });
        }

        /// <summary>Polls each frame until the install ends, then runs `then` and exits.</summary>
        private void WaitThen(Action then)
        {
            float started = -1f;
            bool busyShot = false, downloadShot = false;
            bool cancelTest = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_TEST_CANCEL"));
            int cancelStage = 0;                                     // 0 waiting, 1 cancelled, 2 tried again
            script.Add(P(2.1f, delegate { started = time; }));
            installWait = delegate
            {
                if (cancelTest && cancelStage == 0 && setupWorking && setupLine.StartsWith("downloading Xbox 360 files", StringComparison.Ordinal))
                {
                    int mb;
                    int open = setupLine.IndexOf('('), sp = open < 0 ? -1 : setupLine.IndexOf(' ', open);
                    if (sp > open && int.TryParse(setupLine.Substring(open + 1, sp - open - 1), out mb) && mb >= 20)
                    { cancelStage = 1; setupCancel = true; Console.WriteLine("[studio] setup install test: B at " + mb + " MB"); }
                }
                if (cancelTest && cancelStage == 1 && !setupWorking)
                {
                    cancelStage = 2;
                    Console.WriteLine("[studio] setup install test: " + setupPage + " (" + setupStopHeader + "), Try Again");
                    InstallSetup();
                    return false;
                }
                // the busy page once while the update downloads, once while ffmpeg does
                if (!downloadShot && setupWorking && setupLine.StartsWith("downloading Xbox 360 files", StringComparison.Ordinal) && !setupLine.Contains("(0 of"))
                { downloadShot = true; pendingShot = Path.Combine(shotsDir, "setup-download-busy.png"); }
                if (!busyShot && setupWorking && setupLine.StartsWith("downloading a helper", StringComparison.Ordinal) && !setupLine.Contains("(0 of"))
                { busyShot = true; pendingShot = Path.Combine(shotsDir, "setup-install-busy.png"); }
                if (setupWorking && time - started < 1800f) return false;
                then();
                script.Add(P(time + 0.5f, delegate { Console.WriteLine("[studio] setup install test done"); Exit(); }));
                return true;
            };
        }

        private Func<bool> installWait;

        /// <summary>Each frame in the install test: the wait, once the script has reached it.</summary>
        private void TickInstallWait()
        {
            if (installWait != null && scriptStep >= script.Count && installWait()) installWait = null;
        }

        // ---- --setup-shots: each page, hidden, for checking the look ------------------------------
        private bool setupShots;

        private void BuildSetupShots()
        {
            int n = 0;
            Action shot = delegate { pendingShot = Path.Combine(shotsDir, "setup-" + (n++) + ".png"); };
            SetupArt.IgnoreRemembered = true;
            // a page as the flow shows it (scripted runs never install)
            Action<SetupPage, int, string> page = (p, step, line) =>
            {
                state = State.Setup;
                setupParts = SetupStatus.Read(FindAssetDir());
                setupStep = step;
                setupStopTool = false;
                ShowSetupPage(p);
                setupBusy = p == SetupPage.Busy;
                setupLine = line ?? "";
            };
            // 0: a new player's first page: downloading, no console art yet
            script.Add(P(1.0f, delegate { setupNoArt = true; setupAuto = true; page(SetupPage.Busy, 0, "downloading Xbox 360 files from the Internet Archive (37 of 110 MB)"); }));
            script.Add(P(1.6f, shot));
            // 1: the update here: installing
            script.Add(P(1.7f, delegate
            {
                setupNoArt = false;
                LookForUpdate();
                if (setupUpdate == null) Console.WriteLine("[studio] setup shots: no update to look at (set AVATAR_STUDIO_UPDATE to a $SystemUpdate folder)");
                page(SetupPage.Busy, 1, "downloading the avatar items (450 of 1,092)");
            }));
            script.Add(P(2.9f, shot));                                                  // past the background's 60 frames
            // 2: stopped with B while downloading
            script.Add(P(3.0f, delegate
            {
                setupStopHeader = "Setup stopped";
                setupStopBody = "What was downloaded is kept: Try Again carries on from there.";
                page(SetupPage.Stopped, 0, null);
            }));
            script.Add(P(3.6f, shot));
            // 3: failed, offline, the second tile focused as the pad moves down
            script.Add(P(3.7f, delegate
            {
                setupStopHeader = "Setup couldn't finish";
                setupStopBody = "Something went wrong with the Xbox 360 files. Check that this PC is connected to the internet, then choose Try Again.";
                setupWorkFailed = "The remote name could not be resolved: 'archive.org'";   // More Options shows it
                page(SetupPage.Stopped, 0, null);
            }));
            script.Add(P(3.8f, delegate { MoveSetupFocus(1); }));
            script.Add(P(4.4f, shot));
            // 4: More Options
            script.Add(P(4.5f, delegate { ShowOptions(); }));
            script.Add(P(5.1f, shot));
            // 5: done after an install that opened by itself
            script.Add(P(5.2f, delegate { setupInstalled = true; page(SetupPage.Done, 1, null); }));
            script.Add(P(5.8f, shot));
            // 6: done reached from the gallery (nothing to install)
            script.Add(P(5.9f, delegate { setupInstalled = false; setupAuto = false; page(SetupPage.Done, 1, null); }));
            script.Add(P(6.5f, shot));
            // 7, 8: antivirus blocked the sounds' tool; the tool removed after its download
            script.Add(P(6.6f, delegate { page(SetupPage.Stopped, 1, null); StopForTool("ffmpeg.exe", false, "the sounds"); }));
            script.Add(P(7.2f, shot));
            script.Add(P(7.3f, delegate
            {
                Console.WriteLine("[studio] setup shots: blocked page tiles: " + SetupTileLabels() + " (Windows Security, Try Again expected)");
                OpenWindowsSecurity();                                      // refused: a scripted run opens nothing
                StopForTool("ffmpeg.exe", true, "the sounds");
            }));
            script.Add(P(7.9f, shot));
            script.Add(P(8.0f, delegate { LogSetupWrap(); }));
            script.Add(P(8.2f, delegate { Console.WriteLine("[studio] setup shots done"); Exit(); }));
        }

        private string SetupTileLabels()
        {
            List<string> l = new List<string>();
            foreach (SetupTile t in setupTiles) l.Add(t.Label);
            return string.Join(", ", l.ToArray());
        }

        /// <summary>The tool pages' texts wrapped as drawn, in the dashboard's face and Windows': all must fit the body's 600 x 200.</summary>
        private void LogSetupWrap()
        {
            string[] texts = { ToolStopBody("ffmpeg.exe", false, "the sounds"), ToolStopBody("ffmpeg.exe", true, "the sounds") };
            RuntimeFont[] faces = { SegoeLight(18f, 256), ui.Font(null, "Segoe UI", true, 18f, 256) };
            string[] names = { SetupHasArt ? "the dashboard's face" : "Windows' face (no update here)", "Windows' face" };
            for (int f = 0; f < faces.Length; f++)
            {
                int widest = 0, most = 0, lh = (int)Math.Ceiling(faces[f].LineHeight);
                foreach (string t in texts)
                {
                    string[] lines = faces[f].WrapXui(t, BodyBox.Z).Split('\n');
                    foreach (string l in lines) widest = Math.Max(widest, faces[f].XuiWidth(l));
                    most = Math.Max(most, lines.Length);
                }
                bool fits = widest <= BodyBox.Z && most * lh <= BodyBox.W;
                Console.WriteLine("[studio] setup shots: tool page texts in " + names[f] + ": widest line " + widest + " of " + BodyBox.Z + ", at most "
                    + most + " line(s) of " + lh + " in " + BodyBox.W + ": " + (fits ? "all fit" : "DO NOT FIT"));
            }
            // the tiles' labels: 128 x 58 (DrawSetupTile)
            RuntimeFont lf = ui.Xui(16f, 0x8004000);
            int lmost = 0, llh = (int)Math.Ceiling(lf.LineHeight);
            foreach (string label in new[] { "Windows Security", "Try Again", "More Options", "Use My Own Copy", "Console Font", "Go Play" })
                lmost = Math.Max(lmost, lf.WrapXui(label, 128f).Split('\n').Length);
            Console.WriteLine("[studio] setup shots: tile labels: at most " + lmost + " line(s) of " + llh + " in 58: " + (lmost * llh <= 58 ? "all fit" : "DO NOT FIT"));
        }
    }
}
