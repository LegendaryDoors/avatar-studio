// The studio's window, state machine and every screen's update and draw:
// the gallery (the avatar library) and the editor (the 2010 dashboard editor's screens).

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    enum State { Loading, Gallery, CardMenu, Confirm, NameEntry, Lineup, MainMenu, ChooseNew, Message, Grid, Items, Colors, ColourSets, BodySize, Outfits, DeleteOutfit, OutfitBusy, OutfitTutorial, Saving, Exiting, Setup }

    /// <summary>What the name screen collects, so Enter knows where to go next.</summary>
    enum NamePurpose { NewAvatar, Rename, Imported }

    sealed partial class StudioGame : Game
    {
        public static int ExitCode;

        private readonly GraphicsDeviceManager gdm;
        private readonly string[] args;
        private Ui ui;
        private Backdrop backdrop;
        private Input input;
        private AvatarView stage;
        private ThumbnailCache thumbs;       // 128px, the item picker's cells
        private ThumbnailCache cards;        // 256px, the gallery's cards and the picture stored in a .avatar
        private LineupStage lineupStage;     // the starting-avatar lineup's live avatars (LineupStage.cs)
        private Catalog catalog;
        private TileArt tileArt;             // the editor's own category pictures, when the player has them
        private EditSession session;
        private RenderTarget2D scene;      // 1280x720 target the whole UI draws into, then scaled to the window
        private DisplayGamma gamma;        // the console's display gamma ramp on that frame (DisplayGamma.cs)
        private Ghosts ghosts;             // the neighbouring pages' cells (Ghosts.cs)
        private int gammaCheckIn = 90;     // selftest: frames until the ramp's read-back check
        private Mirror mirror;   // the My Style mirror

        private State state = State.Loading;
        private string statusLine = "";          // standing condition, shown until it stops being true
        private string notice = "";              // a one-off result ("saved ...", "could not ...")
        private float noticeAt = -1000f;
        private const float NoticeSeconds = 8f;
        private bool assetsReady;
        private bool allowLaunch = true;
        private float time;

        // the library and the games that can use one of its avatars
        private readonly AvatarLibrary library = new AvatarLibrary();
        private List<IGameTarget> targets = new List<IGameTarget>();
        private IGameTarget target;              // the one game we offer actions for (the first found)
        private AvatarRecord record;             // the avatar being edited, or null outside the editor
        private int galleryCursor, galleryPage;
        private int menuCursor;
        private List<string> menuItems = new List<string>();

        // confirmation prompt (delete, discard); Yes runs confirmYes, B / No just returns
        private string confirmText = "";
        private Action confirmYes, confirmNo;
        private int confirmCount;                        // the selftest's library count at the delete question
        private State modalBase = State.Gallery;         // the screen a modal is sitting on top of

        // gallery card art: the PNG stored in each .avatar, decoded once
        private readonly Dictionary<string, Texture2D> cardThumbs = new Dictionary<string, Texture2D>();

        // the exit: the Saving box's and the exit's countdown, scnFade's opacity, the save's wait
        private float exitTimer, fade, boxTime, saveWait;
        private bool saveDone;

        // name entry
        private string nameText = "";
        private NamePurpose namePurpose = NamePurpose.NewAvatar;
        private string nameError;

        // navigation
        private Node root;                 // current menu's tree root (Features/Style) or null on main menu
        private Node node;                 // current submenu node
        private readonly Stack<Node> navStack = new Stack<Node>();
        private int cursor;                // highlighted index on the current screen
        private int page;
        private Node activeLeaf;           // the Items/Color/BodySize node being edited
        private List<CatalogItem> pageItems = new List<CatalogItem>();
        private Colorb[] palette;
        private CatalogItem setsItem;      // the garment the colour-set screen colours (dword_945BCCB8)
        private ColourSetList sets;
        private int itemsPage, itemsCursor; // the item screen's page and focus, for B out of a follow-on
        private int pendingExamine;        // the examine a pick asked for (controller +0x3B8), 0 for none
        private readonly Random pressRng = new Random();   // sub_920FC178(100) in sub_920DEE00
        private bool wasFemaleFace;

        // lineup
        private readonly List<byte[]> lineup = new List<byte[]>();
        private int lineupIndex;

        // main-menu tiles: seven, in GridMain's 4 x 2 block, with the editor's own strings
        private static readonly string[] MainTiles = { "Change My Features", "Change My Style", "Marketplace", "Save and Exit",
                                                       "Awards", "Gamer Picture", "Choose New Avatar" };

        // --selftest drives the screens on a timer, writes a PNG of each to --shots DIR, then exits.
        private bool selftest;
        private string shotsDir = "";
        private int fill;                  // --fill N: see FillLibrary
        private readonly List<KeyValuePair<float, Action>> script = new List<KeyValuePair<float, Action>>();
        private int scriptStep;
        private string pendingShot;

        public StudioGame(string[] args)
        {
            this.args = args;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--no-launch") allowLaunch = false;
                else if (args[i] == "--selftest") { selftest = true; allowLaunch = false; }
                else if (args[i] == "--handoff-test") handoffTest = true;
                else if (args[i] == "--fuzz" && i + 1 < args.Length) { int.TryParse(args[++i], out fuzzFrames); allowLaunch = false; }
                else if (args[i] == "--seed" && i + 1 < args.Length) int.TryParse(args[++i], out fuzzSeed);
                else if (args[i] == "--fuzz-start" && i + 1 < args.Length)
                {
                    fuzzStart = args[++i];
                    fuzzStartSets = fuzzStart == "sets";
                }
                else if (args[i] == "--shots" && i + 1 < args.Length) shotsDir = args[++i];
                else if (args[i] == "--fill" && i + 1 < args.Length) int.TryParse(args[++i], out fill);
                else if (args[i] == "--import-test") { importTest = true; allowLaunch = false; }
                else if (args[i] == "--setup-shots") { setupShots = true; allowLaunch = false; }   // StudioSetup.cs
                else if (args[i] == "--setup-install-test") { setupShots = true; setupInstallTest = true; allowLaunch = false; }
                // a file named on the command line (or dropped on the exe) is imported at start
                else if (!args[i].StartsWith("--") && File.Exists(args[i])) openFiles.Add(Path.GetFullPath(args[i]));
            }
            gdm = new GraphicsDeviceManager(this);
            gdm.PreferredBackBufferWidth = Layout.W;
            gdm.PreferredBackBufferHeight = Layout.H;
            gdm.PreferMultiSampling = true;
            Content.RootDirectory = "Content";
            IsMouseVisible = true;
            IsFixedTimeStep = true;                       // 60 Hz is plenty for a menu
            Window.Title = "Avatar Studio";
            Window.AllowUserResizing = true;
        }

        protected override void Initialize()
        {
            base.Initialize();     // calls LoadContent()
            input = new Input(delegate { return new Point(Window.ClientBounds.Width, Window.ClientBounds.Height); });
            input.Suppress = Scripted;   // scripted runs drive themselves; nothing plugged in may
            closeSaves = !Scripted;      // held from here: a fuzz run's end clears what Scripted reads (FuzzDone)
            if (fuzzFrames > 0) { fuzzRng = new Random(fuzzSeed); input.Synthetic = FuzzStep; Input.ForceKeys = fuzzSeed % 2 == 0; }   // even seeds: the keyboard's legend
            base.Window.ClientSizeChanged += delegate { };
            Unattended.BeforeShow(Window.Handle);   // a scripted run's window stays off the screen
        }

        protected override void EndDraw()
        {
            base.EndDraw();
            Unattended.AfterPresent(Window.Handle);
        }

        protected override void EndRun()
        {
            // unbind first: FNA 26.10 refuses to dispose a bound target
            try { GraphicsDevice.SetRenderTarget(null); } catch (Exception e) { Console.WriteLine("[studio] unbinding at exit: " + e.Message); }
            base.EndRun();
        }

        protected override void LoadContent()
        {
            // the studio's avatar engine on the installed tables, catalogue and items (AvatarStack.cs)
            AvatarStack.Bind(FindAssetDir());
            ui = new Ui(GraphicsDevice);
            Console.WriteLine("[studio] ui face: " + ConsoleFace.Status);   // the console's, if installed (ConsoleFace.cs)
            backdrop = new Backdrop(GraphicsDevice);
            stage = new AvatarView(GraphicsDevice);
            stage.GazeRest = true;                                   // the editor's own avatar (RestGaze.cs)
            thumbs = new ThumbnailCache(GraphicsDevice, 128);
            cards = new ThumbnailCache(GraphicsDevice, 256, 256, true);     // the editor's look (ThumbnailCache)
            // 4x multisampled with a stencil (the mirror's mask), as the editor's back buffer (0x920B3CC0)
            scene = new RenderTarget2D(GraphicsDevice, Layout.W, Layout.H, false, SurfaceFormat.Color, DepthFormat.Depth24Stencil8,
                                       4, RenderTargetUsage.DiscardContents);
            mirror = new Mirror(GraphicsDevice);
            gamma = new DisplayGamma(GraphicsDevice, Layout.W, Layout.H);
            Console.WriteLine("[studio] display gamma ramp: " + gamma.Status);

            assetsReady = AvatarStack.Ready && AssetDirHasToc();
            AvatarToc.Load();   // the console's catalogue, if installed
            Console.WriteLine("[studio] avatar toc: " + AvatarToc.Status);
            catalog = new Catalog(FindAssetDir());
            tileArt = new TileArt(GraphicsDevice);
            backdrop.UseConsoles(GraphicsDevice, tileArt.Dir);   // the aura scene with background.jpg (Backdrop.cs)
            Console.WriteLine("[studio] backdrop: " + (backdrop.IsConsoles ? "the console's (background.jpg)" : "the stand-in (no background.jpg)"));
            XuiVisuals.Load(tileArt.Dir);       // the controls' timelines (XuiVisuals.cs), from the same install
            ui.Bar = new LegendBar(tileArt);    // the control pack's legend scene (LegendBar.cs), likewise
            ghosts = new Ghosts(GraphicsDevice, ui, tileArt);
            stage.FloorShadow = tileArt.Named("AvatarShadow");   // the editor's floor shadow (AvatarView)
            lineupStage = new LineupStage(GraphicsDevice);   // the starting lineup's seven
            lineupStage.FloorShadow = tileArt.Named("AvatarShadow");

            // The library is the tool's content; the games are optional consumers of it.
            library.Load();
            targets = GameTargets.Discover(selftest || fuzzFrames > 0 || importTest || setupShots);
            target = targets.Count > 0 ? targets[0] : null;
            ImportFromGame();
            FillLibrary();
            ReloadLibrary();                      // re-read so an import lands in name order

            // one edit session for the run; the placeholder keeps every draw path non-null
            session = new EditSession(AvatarStack.Random(AvatarGender.Both, 1)[0]);
            session.Changed += delegate { RefreshStage(); };

            state = State.Gallery;
            galleryCursor = 0;
            // [ours] in a player's words: what is wrong and what to do
            statusLine = assetsReady ? "" : "Avatar Studio isn't set up yet. On the New Avatar card, choose Y Setup to finish setting it up.";
            Console.WriteLine("[studio] avatar assets: " + (assetsReady ? catalog.Items.Count + " items" : "not found"));

            if (importTest) BuildImportTest();
            else foreach (string f in openFiles) ImportFile(f);
            // the setup at start while essentials are missing; never in a scripted run
            if (!Scripted && !SetupStatus.Complete(SetupStatus.Read(FindAssetDir()))) OpenSetup(true);
            if (setupInstallTest) BuildSetupInstallTest();
            else if (setupShots) BuildSetupShots();
            if (selftest) BuildScript();
            if (handoffTest) BuildHandoffScript();
            if (fuzzFrames > 0 && fuzzStartSets) BuildFuzzStart();
            if (fuzzFrames > 0 && fuzzStart == "lineup") BuildFuzzLineup();
        }

        /// <summary>--fuzz-start lineup: into the lineup as a new avatar gets there, then random presses.</summary>
        private void BuildFuzzLineup()
        {
            fuzzWarmup = true;
            script.Add(P(0.8f, delegate { galleryCursor = 0; GalleryActivate(); }));
            script.Add(P(1.4f, delegate { nameText = "Fuzz"; ConfirmName(); fuzzWarmup = false; }));
        }

        /// <summary>--fuzz-start sets: onto a colourable top's colour sets, then random presses.</summary>
        private void BuildFuzzStart()
        {
            fuzzWarmup = true;
            script.Add(P(0.8f, delegate { galleryCursor = 0; GalleryActivate(); }));
            script.Add(P(1.4f, delegate { nameText = "Fuzz"; ConfirmName(); }));
            script.Add(P(5.0f, delegate { AcceptLineup(); }));
            script.Add(P(5.2f, delegate { cursor = 1; ActivateMain(); Pick("Tops"); ColourSetTour(true); fuzzWarmup = false; }));
        }

        // --handoff-test: the one scripted run allowed to write the player avatar;
        // it refuses without a scratch library and a scratch player folder.
        private bool handoffTest;

        private void BuildHandoffScript()
        {
            string lib = Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBRARY");
            string player = Environment.GetEnvironmentVariable("AVATAR_STUDIO_PLAYER");
            string real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "player");
            if (string.IsNullOrEmpty(lib) || string.IsNullOrEmpty(player)
                || string.Equals(Path.GetFullPath(player).TrimEnd('\\', '/'), real.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[studio] --handoff-test needs AVATAR_STUDIO_LIBRARY and AVATAR_STUDIO_PLAYER set to scratch folders; refusing");
                ExitCode = 3; Exit(); return;
            }
            // X on the second avatar, then X on the last: the player folder follows each
            script.Add(P(2.0f, delegate { galleryCursor = 2; Console.WriteLine("[studio] handoff: X on \"" + GalleryRecord().Name + "\""); UseWithGame(); }));
            script.Add(P(2.4f, delegate { ReportHandoff("X"); }));
            script.Add(P(2.8f, delegate
            {
                galleryCursor = GalleryCount - 1;
                Console.WriteLine("[studio] handoff: X on \"" + GalleryRecord().Name + "\"");
                UseWithGame();
            }));
            script.Add(P(3.2f, delegate { ReportHandoff("second X"); Console.WriteLine("[studio] handoff done"); Exit(); }));
        }

        // files to import at start: named on the command line, or dropped on the exe
        private readonly List<string> openFiles = new List<string>();

        // --import-test FILE...: each file imported into a scratch library and items folder; logs, exits.
        private bool importTest;

        /// <summary>A scripted run: real input dropped, targets read-only, no file dialog, no setup by itself.</summary>
        private bool Scripted { get { return selftest || handoffTest || fuzzFrames > 0 || importTest || setupShots; } }

        private void BuildImportTest()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBRARY"))
                || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_ITEMS")))
            {
                Console.WriteLine("[studio] --import-test needs AVATAR_STUDIO_LIBRARY and AVATAR_STUDIO_ITEMS set to scratch folders; refusing");
                ExitCode = 3; Exit(); return;
            }
            foreach (string f in openFiles)
            {
                string why;
                ImportedAvatar a = AvatarFiles.Read(f, out why);
                Console.WriteLine("[studio] import-test: " + Path.GetFileName(f) + ": " + (a == null ? "no avatar (" + why + ")"
                    : "\"" + a.Name + "\", manifest md5 " + Md5Hex(a.Manifest) + ", " + a.ItemsWritten + " non-stock item(s) unpacked"
                      + (a.NameFromProfile ? ", name to ask" : "") + (a.Png != null ? ", picture " + a.Png.Length + " bytes" : "")));
                ImportFile(f);
            }
            script.Add(P(1.0f, delegate
            {
                Console.WriteLine("[studio] import-test: library " + library.Records.Count + " avatar(s), screen " + state
                    + (state == State.NameEntry ? " (\"" + nameText + "\", " + namePurpose + ")" : ""));
                if (string.IsNullOrEmpty(shotsDir)) { Exit(); return; }
                EnterGallery();                                 // --shots DIR: the gallery's cards, once rendered
            }));
            if (string.IsNullOrEmpty(shotsDir)) return;
            script.Add(P(6.0f, delegate { pendingShot = Path.Combine(shotsDir, "import-test-gallery.png"); }));
            script.Add(P(6.5f, delegate { Exit(); }));
        }

        private static string Md5Hex(byte[] b)
        {
            using (System.Security.Cryptography.MD5 md5 = System.Security.Cryptography.MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(b)).Replace("-", "").ToLowerInvariant();
        }

        // --fuzz N [--seed S]: random presses and clicks for N frames; any exception ends the run.
        private int fuzzFrames, fuzzFrame, fuzzPresses, fuzzSeed = 1;
        private Random fuzzRng;
        private readonly HashSet<State> fuzzSeen = new HashSet<State>();
        private bool fuzzStartSets, fuzzWarmup;
        private string fuzzStart;

        private void FuzzStep(Input i)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBRARY")))
            {
                Console.WriteLine("[studio] --fuzz needs AVATAR_STUDIO_LIBRARY set to a scratch folder; refusing");
                ExitCode = 3; Exit(); fuzzFrames = 0; return;
            }
            if (fuzzWarmup) return;
            fuzzSeen.Add(state);
            if (state == State.Loading || fuzzRng.NextDouble() < 0.55) return;   // let screens settle between presses
            // a pressed control acts when its press ends; wait, as a person does, but not always
            if ((controls.Pressing || backAt >= 0f) && fuzzRng.Next(10) != 0) return;
            fuzzPresses++;
            if (fuzzStartSets && state == State.Grid && node != null && node.Name == "My Style" && fuzzRng.NextDouble() < 0.2)
            {
                Pick("Tops"); ColourSetTour(false);
                return;
            }
            // My Outfits and its delete question sit deep; steer there now and then
            if (state == State.Grid && node != null && node.Name == "My Style" && fuzzRng.NextDouble() < 0.1) { Pick("My Outfits"); return; }
            if (state == State.Outfits && OutfitEntry(cursor) >= 0 && fuzzRng.NextDouble() < 0.2) { i.X = true; return; }
            // the outfit's name panel: confirm it now and then
            if (state == State.OutfitBusy && outfitNaming && fuzzRng.NextDouble() < 0.3) { ConfirmOutfitName(); return; }
            if (fuzzStart == "lineup")
            {
                if (state == State.MainMenu && fuzzRng.NextDouble() < 0.2) { cursor = Array.IndexOf(MainTiles, "Choose New Avatar"); ActivateMain(); return; }
                if (state == State.ChooseNew && fuzzRng.NextDouble() < 0.5) { i.Select = true; return; }   // mostly Yes: the dialog opens on it
                if (state == State.NameEntry && fuzzRng.NextDouble() < 0.3) { nameText = "Fuzz"; ConfirmName(); return; }
                if (state == State.Lineup)
                {
                    // the lineup's own controls, B rarely: its navigation waits on the avatars running in
                    switch (fuzzRng.Next(24))
                    {
                        case 0: if (lineupStage.AllStanding && fuzzRng.Next(10) == 0) i.Back = true; break;
                        case 1: case 2: case 3: case 4: i.Left = true; break;
                        case 5: case 6: case 7: case 8: i.Right = true; break;
                        case 9: if (fuzzRng.Next(8) == 0) i.Select = true; break;
                        case 10: if (lineupStage.AllStanding && fuzzRng.Next(4) == 0) i.Select = true; break;
                        case 11: case 12: case 13: i.PageLeft = true; break;
                        case 14: case 15: case 16: i.PageRight = true; break;
                        case 17: case 18: i.SetMouse(new Vector2(fuzzRng.Next(Layout.W), fuzzRng.Next(Layout.H)), false, false); break;
                        default: i.SetMouse(new Vector2(fuzzRng.Next(Layout.W), fuzzRng.Next(Layout.H)), true, false); break;
                    }
                    return;
                }
            }
            switch (fuzzRng.Next(13))
            {
                case 12:   // the right stick and the held buttons the resting avatar answers, a frame's worth
                    i.RightStick = new Vector2(fuzzRng.Next(-32768, 32768), fuzzRng.Next(-32768, 32768));
                    i.StickMoved = Math.Abs(i.RightStick.X) > 8689f || Math.Abs(i.RightStick.Y) > 8689f;
                    i.LeftShoulderHeld = fuzzRng.Next(3) == 0; i.RightShoulderHeld = fuzzRng.Next(3) == 0; i.RightThumbHeld = fuzzRng.Next(4) == 0;
                    break;
                case 0: i.Left = true; break;
                case 1: i.Right = true; break;
                case 2: i.Up = true; break;
                case 3: i.Down = true; break;
                case 4: case 5: i.Select = true; break;
                case 6: if (state != State.Gallery) i.Back = true; break;
                case 7: i.PageLeft = true; break;
                case 8: i.PageRight = true; break;
                case 9: i.Y = true; break;
                case 10: i.SetMouse(new Vector2(fuzzRng.Next(Layout.W), fuzzRng.Next(Layout.H)), false, false); break;
                default: i.SetMouse(new Vector2(fuzzRng.Next(Layout.W), fuzzRng.Next(Layout.H)), true, false); break;
            }
        }

        private void FuzzDone()
        {
            List<string> seen = new List<string>();
            foreach (State st in fuzzSeen) seen.Add(st.ToString());
            seen.Sort();
            Console.WriteLine("[studio] fuzz done: seed " + fuzzSeed + ", " + fuzzFrame + " frames, " + fuzzPresses
                + " presses, " + seen.Count + " screens: " + string.Join(" ", seen.ToArray())
                + (fuzzStart == "lineup" ? "; lineup: " + lineupTurns + " page turns, " + lineupChosen + " chosen" : ""));
            fuzzFrames = 0;
            Exit();
        }

        private void ReportHandoff(string when)
        {
            AvatarRecord r = GalleryRecord();
            bool same = r != null && target != null && AvatarLibrary.SameBytes(r.Manifest, target.CurrentManifest);
            Console.WriteLine("[studio] handoff " + when + ": player avatar " + (same ? "HOLDS" : "does not hold")
                + " \"" + (r == null ? "?" : r.Name) + "\" (player name " + (target == null ? "?" : target.CurrentName) + ")");
        }

        /// <summary>--fill N: a library to look at in the gallery; refuses unless AVATAR_STUDIO_LIBRARY is set.</summary>
        private void FillLibrary()
        {
            if (fill <= 0) return;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBRARY")))
            {
                Console.WriteLine("[studio] --fill needs AVATAR_STUDIO_LIBRARY set to a scratch folder; ignored");
                return;
            }
            // names of assorted lengths, one at the 15-character cap
            string[] names = { "Hero", "Villain", "Knight", "Sorceress", "Bo", "Ranger", "Tinkerbell",
                               "Maximilian III", "Scout", "Blacksmith", "Nix", "Queen of Cups",
                               "Wanderer", "Oracle", "Pip", "Longnamedude15" };
            for (int i = 0; i < fill; i++)
            {
                try
                {
                    string n = library.UniqueName(names[i % names.Length]);
                    library.Save(library.Create(n, AvatarStack.Random(AvatarGender.Both, 1)[0].SaveToBinary()));
                }
                catch (Exception e) { Console.WriteLine("[studio] --fill: " + e.Message); break; }
            }
            Console.WriteLine("[studio] --fill wrote " + fill + " avatars");
        }

        /// <summary>A player avatar not in the library (another copy of the studio set it): a copy into the library.</summary>
        private void ImportFromGame()
        {
            if (target == null) return;
            byte[] m = null;
            string who = null;
            try { m = target.CurrentManifest; who = target.CurrentName; }
            catch (Exception e) { Console.WriteLine("[studio] could not read " + target.Name + "'s avatar: " + e.Message); }
            if (m == null || library.Matching(m) != null) return;
            try
            {
                AvatarRecord r = library.Create(library.UniqueName(who ?? "Imported"), m);
                library.Save(r);
                Console.WriteLine("[studio] imported " + r.Name + " from " + target.Name);
            }
            catch (Exception e) { Console.WriteLine("[studio] import failed: " + e.Message); }
        }

        private void BuildScript()
        {
            if (string.IsNullOrEmpty(shotsDir)) shotsDir = Path.Combine("..", "..", "screenshots", "studio");
            string prefix = fill > 0 ? "studio-gallery-" : "studio-";
            Action shot = delegate { pendingShot = Path.Combine(shotsDir, prefix + scriptStep.ToString("00") + "-" + state + ".png"); };
            if (fill > 0) { BuildGalleryTour(shot); return; }
            // the waits are for real render work: shot too early and the screen is all spinners
            script.Add(P(0.6f, shot));                                              // gallery
            script.Add(P(0.62f, delegate                                            // the exe's icon on the window (WindowIcon.cs)
            {
                string r;
                bool ok = WindowIcon.Check(out r);
                Console.WriteLine("[studio] selftest: window icon: " + r + (ok ? "" : " (NOT the exe's: the exe's icon expected)"));
            }));
            script.Add(P(0.8f, delegate { galleryCursor = 0; GalleryActivate(); }));// -> new avatar, name entry
            script.Add(P(1.2f, shot));                                              // name entry
            script.Add(P(1.4f, delegate { nameText = "Hero"; ConfirmName(); }));    // -> lineup
            script.Add(P(3.0f, shot));                                              // lineup, running in
            script.Add(P(5.0f, shot));                                              // lineup, seven live avatars
            script.Add(P(5.1f, delegate { Console.WriteLine("[studio] selftest: lineup " + lineupStage.StandingCount + " of 7 standing, focus " + (lineupStage.FocusFloating ? "floating" : "not floating")); }));
            script.Add(P(5.2f, delegate { AcceptLineup(); }));                      // -> main menu
            script.Add(P(6.0f, shot));                                              // main menu
            script.Add(P(6.02f, delegate { LogVisuals(); }));
            // focus moves to Change My Style; 8 frames in, a shot of the pop and the nod
            float moved = 0f;
            script.Add(P(6.05f, delegate { cursor = 1; moved = time; }));
            script.Add(P(6.05f + 8f / 60f, shot));
            script.Add(P(6.05f + 8.5f / 60f, delegate { LogTiles(time - moved); }));
            script.Add(P(6.15f, delegate { cursor = 0; }));
            // the library renderer's state
            script.Add(P(6.1f, delegate
            {
                Console.WriteLine("[studio] selftest: library renderer " + (stage.UsesLibrary ? "on" : "OFF (no avatar drawn)"));
            }));
            script.Add(P(6.2f, delegate { cursor = 0; ActivateMain(); }));          // -> Features grid
            // the bonus hair colours: the clock put just short of a minute on My Features
            script.Add(P(6.6f, delegate { bonus.TestClock(59.9f); }));
            script.Add(P(7.0f, shot));                                              // features grid
            script.Add(P(7.02f, delegate
            {
                Console.WriteLine("[studio] selftest: bonus colours: My Features at " + bonus.Clock.ToString("0.00") + " s, window " + (bonus.Window ? "open" : "SHUT")
                    + ", the Hairstyles tile " + (bonusSpinAt >= 0f ? "turning, frame " + (511f + (time - bonusSpinAt) * 60f).ToString("0") : "NOT turning"));
            }));
            // the editor's animations: how many load, and the dizzy pair's queue (17, then 18 looping)
            script.Add(P(7.05f, delegate
            {
                int n = 0, total = 0;
                for (int id = 1; id <= 66; id++) { if (id == 3 || id == 4) continue; total++; if (EditorAnimations.Has(id)) n++; }
                Console.WriteLine("[studio] selftest: editor animations loadable " + n + " of " + total);
                stage.Request(EditorAnimations.BalanceInto, false); stage.Request(EditorAnimations.BalanceLoop, true);
            }));
            script.Add(P(7.15f, delegate { Console.WriteLine("[studio] selftest: editor animation playing " + stage.EditorAnimationId + " (17 expected)"); }));
            script.Add(P(7.2f, delegate { cursor = 0; ActivateGrid(); }));          // -> Hairstyles item picker
            // mid-transition (PageTransitions): Grid.xur's ForwardIn over GridMain's BackwardOut
            script.Add(P(7.27f, shot));
            script.Add(P(7.28f, delegate
            {
                LogTransition();
                Colorb h = session.Color(DynamicColorType.Hair);
                Console.WriteLine("[studio] selftest: bonus colours: Hairstyles pressed in the window, hair dyed " + bonusDyed + " time(s), now "
                    + h.red.ToString("X2") + h.green.ToString("X2") + h.blue.ToString("X2") + " (F1A6A0 expected), sfxBonusUnlocked " + bonus.Played + " time(s), unlocked " + bonus.Unlocked);
            }));
            // GridMain's BackwardOut is 9 frames: by 0.3 s the change is over
            script.Add(P(7.5f, delegate { LogSettled(); }));
            // LT held a quarter second, after the 1.2 s snap-back: s ~ 0.25
            script.Add(P(9.0f, delegate { input.Synthetic = delegate(Input i) { i.TriggerLeft = 255; }; }));
            // the page handles' nudge: two seconds in, halfway down the first 10 -> 0
            script.Add(P(9.2f, delegate
            {
                float age = time - pageSceneAt;
                Console.WriteLine("[studio] selftest: Hairstyles' page " + age.ToString("0.00") + " s old, handle nudge " + Chrome.HandleNudge(age).ToString("0.0") + " (about 5 at 2 s)");
            }));
            script.Add(P(9.25f, delegate
            {
                input.Synthetic = null;
                Console.WriteLine("[studio] selftest: spin after LT " + stage.Spin.ToString("0.000") + "; the skeleton's root (the turntable wrapper's B) " + stage.SkeletonRoot);
                Console.WriteLine("[studio] selftest: the turntable: " + stage.TurnLog);
            }));
            // the focused cell's tooltip: shown by now, on the side away from the screen's middle
            script.Add(P(9.6f, shot));
            script.Add(P(9.62f, delegate
            {
                Tip tip;
                float vw;
                bool right = Vector2.Transform(Vector2.Zero, ControlAt(TipCell(cursor, out vw))).X >= 640f;
                Console.WriteLine("[studio] selftest: tooltip on cell " + cursor + ": "
                    + (tips.TryGetValue(cursor, out tip) ? "\"" + tip.Text + "\" state " + tip.State + " opacity " + tip.Opacity.ToString("0.00") : "none")
                    + " (state 1, opacity 1 expected), ToolTip" + (right ? "Right" : "Left") + "; " + tips.Count + " cells tracked");
            }));
            // the cells' timelines: focus one cell on, a shot 8 frames later, then back
            int cellFrom = 0; float cellMoved = 0f;
            script.Add(P(10.2f, delegate { cellFrom = cursor; cursor = cursor == 0 ? 1 : cursor - 1; cellMoved = time; }));
            script.Add(P(10.2f + 8f / 60f, shot));
            script.Add(P(10.2f + 8.5f / 60f, delegate { LogCells(cellFrom, cursor, time - cellMoved); }));
            script.Add(P(10.45f, delegate { cursor = cellFrom; }));
            script.Add(P(10.6f, shot));                                             // Hairstyles: no remove cell; turned by LT
            script.Add(P(10.62f, delegate
            {
                // the screen opened on its first chosen cell, the worn style (FocusFirstChosenItem)
                Guid worn = session.EquippedComponent(AvatarComponentMasks.Hair);
                bool on = CursorItem() != null && CursorItem().Id == worn;
                Console.WriteLine("[studio] selftest: Hairstyles opened on page " + (page + 1) + ", cell " + cursor + (on ? ", the worn style" : ", NOT the worn style"));
            }));
            script.Add(P(10.65f, delegate { cursor = 2; CommitItem(); }));          // a colourable style: its colour screen follows
            script.Add(P(10.9f, shot));                                             // Hair Color, reached from the pick
            script.Add(P(10.92f, delegate
            {
                bool first = palette != null && palette.Length > BonusColours.Colours.Length;
                for (int k = 0; first && k < BonusColours.Colours.Length; k++)
                {
                    Colorb b = BonusColours.At(k);
                    first = palette[k].red == b.red && palette[k].green == b.green && palette[k].blue == b.blue;
                }
                Console.WriteLine("[studio] selftest: bonus colours: Hair Color reached from the pick, " + (palette == null ? 0 : palette.Length) + " chips, "
                    + (first ? "the nine first" : "NOT the nine first") + ", focus on chip " + (page * Layout.ChipsPerPage + cursor) + " (the worn F1A6A0 is chip 0)");
            }));
            // the page turns at the grid's edge: right from the right column, left from the left
            int edgeFrom = 0;
            script.Add(P(10.93f, delegate { edgeFrom = page; cursor = Layout.ChipRows; input.Synthetic = delegate(Input i) { i.Right = true; input.Synthetic = null; }; }));
            script.Add(P(10.95f, delegate
            {
                Console.WriteLine("[studio] selftest: right from the right column: page " + (edgeFrom + 1) + " -> " + (page + 1) + " (" + (edgeFrom + 2) + " expected), cell " + cursor + " (0 expected)");
                input.Synthetic = delegate(Input i) { i.Left = true; input.Synthetic = null; };
            }));
            script.Add(P(10.97f, delegate { Console.WriteLine("[studio] selftest: left from the left column: page " + (page + 1) + " (" + (edgeFrom + 1) + " expected)"); }));
            // B twice, then the Color submenu with the facial hair off, so its entry is disabled
            script.Add(P(11.0f, delegate
            {
                GoBack();
                Console.WriteLine("[studio] selftest: B out of the follow-on lands on " + state + " (Items expected)");
                GoBack(); session.PreviewRemove(AvatarManifestEditor.RemovableComponents.FacialHair); session.Commit(); Pick("Color");
            }));
            script.Add(P(11.5f, shot));                                             // eight tiles; Facial Hair Color dimmed
            script.Add(P(11.55f, delegate { cursor = 5; PressFocused(ActivateGrid); }));   // A on it: refused (PressDisable)
            script.Add(P(11.6f, shot));                                             // PressDisable: _pressdisable art, shaking
            script.Add(P(11.8f, delegate { Pick("Hair Color"); cursor = 2; PreviewCursorColor(); }));
            script.Add(P(12.6f, shot));                                             // Hair Color, a chip focused
            script.Add(P(12.8f, delegate { GoBack(); GoBack(); Pick("Face"); Pick("Eye Shadow"); PreviewCursorColor(); }));
            script.Add(P(13.6f, shot));                                             // Eye Shadow Color, remove focused
            script.Add(P(13.65f, delegate { Console.WriteLine("[studio] selftest: Eye Shadow Color, the rest's expression mode " + stage.RestMode + " (7 expected, 0 for a moment now and then), " + (stage.RestingInGaze ? "resting" : "busy") + ", editor animation " + stage.EditorAnimationId); }));
            script.Add(P(13.8f, delegate { cursor = 1; PreviewCursorColor(); }));
            script.Add(P(14.6f, shot));                                             // a shadow colour on
            // My Style: the mirror eases in (about 1.3 s)
            script.Add(P(14.8f, delegate { GoBack(); GoBack(); GoBack(); cursor = 1; ActivateMain(); }));
            script.Add(P(16.4f, shot));                                             // My Style with the mirror
            // The garments' colour-set screen (ColourSets.cs): a colourable top, picked from Tops.
            script.Add(P(16.6f, delegate { Pick("Tops"); ColourSetTour(true); }));
            script.Add(P(17.6f, shot));                                             // the colour sets, focus on the chosen set
            script.Add(P(17.7f, delegate
            {
                if (state != State.ColourSets) return;
                // within this page: the chosen set may open a later page with fewer cells
                int onPage = Math.Min(Layout.ChipsPerPage, sets.Sets.Length - page * Layout.ChipsPerPage);
                cursor = Math.Max(0, Math.Min(4, onPage - 1)); PreviewCursorSet();
                uint[] want = sets.Sets[page * Layout.ChipsPerPage + cursor];
                uint[] got = ColourSets.Worn(session.Working, setsItem.Id);
                Console.WriteLine("[studio] selftest: set " + cursor + " on the avatar, colours read back " + (ColourSets.Same(want, got) ? "exact" : "DIFFERENT"));
            }));
            script.Add(P(18.5f, shot));                                             // another set focused and worn
            script.Add(P(18.6f, delegate
            {
                if (state != State.ColourSets) return;
                string item = setsItem.Name;
                CommitColourSet();
                Console.WriteLine("[studio] selftest: picked a set of " + item + "; back on " + state + " (Grid expected)");
            }));
            // the examine is a coin flip (sub_920FC178(100) < 50); without it the current one carries on
            script.Add(P(18.8f, delegate { Console.WriteLine("[studio] selftest: editor animation playing " + stage.EditorAnimationId + " (13 when the press asked for the examine, else the one already playing)"); }));
            const float m = 1.8f + 3.0f;                                            // the steps below, after the mirror's and the sets' visits
            script.Add(P(14.8f + m, delegate { Pick("Accessories"); Pick("Glasses"); page = 0; RebuildPage(); cursor = 0; PreviewCursorItem(); }));
            script.Add(P(18.4f + m, shot));                                         // Glasses: remove cell focused
            script.Add(P(18.6f + m, delegate { page = 1; RebuildPage(); cursor = 3; PreviewCursorItem(); }));
            // this shot with the keyboard's legend and turntable keys (Input.KeysShown), then back
            script.Add(P(18.65f + m, delegate { Input.ForceKeys = true; }));
            script.Add(P(19.4f + m, shot));                                         // Glasses page 2: eight items, no remove cell; an item focused, name plate
            script.Add(P(19.45f + m, delegate { Input.ForceKeys = false; }));
            // Earrings keeps the Accessories camera until an item cell takes the focus with the item on
            script.Add(P(19.6f + m, delegate { GoBack(); Pick("Earrings"); page = 0; RebuildPage(); cursor = 0; PreviewCursorItem(); }));
            script.Add(P(20.4f + m, shot));                                         // Earrings, remove focused: still the Accessories shot
            script.Add(P(20.5f + m, delegate { cursor = 1; PreviewCursorItem(); }));
            script.Add(P(21.5f + m, shot));                                         // an earring focused: Earrings' own record
            script.Add(P(21.55f + m, delegate
            {
                Console.WriteLine("[studio] selftest: earring focused, camera " + (stage.ReframePending ? "NOT reframed" : "reframed")
                    + ", first vertex x " + AvatarView.LastMirrorX.ToString("0.0000") + ", " + (stage.Mirrored ? "mirrored" : "not mirrored"));
            }));
            // B: the hidden back button's 19-frame press, then AE_Back; and the music's mode
            float bAt = 0f;
            script.Add(P(21.56f + m, delegate { bool once = false; bAt = time; input.Synthetic = delegate(Input i) { if (!once) { i.Back = true; once = true; } }; }));
            script.Add(P(21.56f + m + 10f / 60f, delegate { Console.WriteLine("[studio] selftest: B, " + ((time - bAt) * 60f).ToString("0") + " frames on: " + state + " (Items expected: the press range runs)"); }));
            script.Add(P(21.56f + m + 25f / 60f, delegate
            {
                input.Synthetic = null;
                Console.WriteLine("[studio] selftest: B, " + ((time - bAt) * 60f).ToString("0") + " frames on: " + state + " (Grid expected); music mode " + Music.Current
                    + " (0 expected: the lineup's; the script adopts its avatar without the pick that sets 2), "
                    + (Music.Playing ? "playing" : "between tracks") + "; sounds started " + Sounds.Started);
            }));
            // My Outfits: the tutorial, save the outfit, change the shirt, wear it back, delete it, the counter
            const float o0 = 22.1f + m;
            byte[] savedOutfit = null;
            Func<string> camera = delegate
            {
                Vector2? lt = FocusedTileCentre();
                return "camera record " + (stage.Screen == EditorCamera.Override ? "Override (z 0.116544)" : stage.Screen.ToString())
                     + ", triggers " + (TriggersSpin ? "ON" : "off") + ", look target " + (lt.HasValue ? lt.Value.ToString() : "none");
            };
            script.Add(P(o0, delegate { GoBack(); GoBack(); GoBack(); cursor = 1; ActivateMain(); Pick("My Outfits"); }));
            script.Add(P(o0 + 0.5f, shot));                                         // Introducing Outfits
            script.Add(P(o0 + 0.52f, delegate
            {
                Console.WriteLine("[studio] selftest: first visit to My Outfits: " + state + " (OutfitTutorial expected), " + camera());
                PressTutorial(0);
            }));
            script.Add(P(o0 + 0.95f, delegate
            {
                Console.WriteLine("[studio] selftest: after A on the tutorial, " + state + " (Outfits expected), on " + (OutfitEntry(cursor) == -1 ? "Save Outfit" : "an outfit")
                    + " (Save Outfit expected), \"" + OutfitLabel(cursor) + "\", " + outfits.Items.Count + " outfits");
                PressOutfit();
            }));
            script.Add(P(o0 + 1.4f, shot));                                         // the name panel over the Saving box
            script.Add(P(o0 + 1.42f, delegate
            {
                Console.WriteLine("[studio] selftest: after A on Save Outfit, " + state + (outfitNaming ? " naming" : "") + " (OutfitBusy naming expected), box \""
                    + outfitBusyText + "\", default name \"" + outfitNameText + "\" (Outfit 1 expected), " + camera());
                ConfirmOutfitName();
            }));
            script.Add(P(o0 + 3.5f, delegate
            {
                savedOutfit = outfits.Items.Count > 0 ? outfits.Items[0].Manifest : null;
                Console.WriteLine("[studio] selftest: after the Saving box (" + (time - outfitBusyAt).ToString("0.00") + " s), " + state + " (Outfits expected), "
                    + outfits.Items.Count + " outfit(s) (1 expected), focus \"" + OutfitLabel(cursor) + "\" (Wear Outfit 1 expected), counter " + outfits.Counter);
                GoBack();
                session.PreviewItem(ConsoleTables.DefaultRequired(session.Body, 2).AssetId);   // the body's default shirt
                session.Commit();
                Console.WriteLine("[studio] selftest: shirt changed: the worn manifest is " + (savedOutfit != null && OutfitOps.Same(session.CommittedBytes, savedOutfit) ? "STILL the outfit" : "not the outfit"));
            }));
            script.Add(P(o0 + 3.65f, delegate { Pick("My Outfits"); Console.WriteLine("[studio] selftest: My Outfits again, " + state + ", focus on " + (OutfitEntry(cursor) == -1 ? "Save Outfit (expected)" : "an outfit")); }));
            script.Add(P(o0 + 4.15f, delegate { cursor = 1; }));                   // the outfit; its preview comes 0.3 s on
            script.Add(P(o0 + 4.55f, shot));                                        // the outfit tile focused, the avatar dressed in it
            script.Add(P(o0 + 4.57f, delegate
            {
                Console.WriteLine("[studio] selftest: outfit focused, \"" + OutfitLabel(cursor) + "\", previewing it: "
                    + (savedOutfit != null && OutfitOps.Same(session.WorkingBytes, savedOutfit) ? "yes" : "NO") + "; legend " + string.Join(", ", EditorLegend(stage.Screen, OutfitEntry(cursor) >= 0)));
                PressOutfit();                                                      // wear it
            }));
            script.Add(P(o0 + 5.0f, delegate
            {
                Console.WriteLine("[studio] selftest: after A on the outfit, " + state + " (Grid expected), wearing it: "
                    + (savedOutfit != null && OutfitOps.Same(session.CommittedBytes, savedOutfit) ? "yes" : "NO"));
                Pick("My Outfits");
                Console.WriteLine("[studio] selftest: My Outfits again, focus on " + (OutfitEntry(cursor) >= 0 ? "the worn outfit (expected)" : "Save Outfit"));
            }));
            script.Add(P(o0 + 5.45f, delegate { OpenDeleteOutfit(); }));           // X
            script.Add(P(o0 + 5.85f, shot));                                        // the delete question, nearer
            script.Add(P(o0 + 5.87f, delegate
            {
                Console.WriteLine("[studio] selftest: X on the outfit: " + state + " (DeleteOutfit expected), " + camera());
                PressDeleteOutfit(0);
            }));
            script.Add(P(o0 + 6.3f, shot));                                         // the Deleting box over My Style
            script.Add(P(o0 + 6.32f, delegate
            {
                Console.WriteLine("[studio] selftest: Yes: " + state + " \"" + outfitBusyText + "\" (OutfitBusy \"Deleting\" expected) over " + (node == null ? "?" : node.Name)
                    + " (My Style expected), " + outfits.Items.Count + " outfit(s) (0 expected), direction " + curDir + " (Backward expected)");
            }));
            script.Add(P(o0 + 8.4f, delegate
            {
                Console.WriteLine("[studio] selftest: after the Deleting box, " + state + " " + (node == null ? "?" : node.Name) + " (Grid My Style expected), tile " + cursor + " (0 expected), direction " + curDir + " (Forward expected)");
                Pick("My Outfits");
            }));
            script.Add(P(o0 + 8.6f, delegate { PressOutfit(); }));                 // Save Outfit again
            script.Add(P(o0 + 9.0f, delegate
            {
                Console.WriteLine("[studio] selftest: Save Outfit after the delete: default name \"" + outfitNameText + "\" (Outfit 2 expected: the counter is not lowered)");
                CancelOutfitName();                                                 // B on the keyboard
                Console.WriteLine("[studio] selftest: name cancelled: " + state + " (Outfits expected), " + outfits.Items.Count + " outfit(s) (0 expected), on "
                    + (OutfitEntry(cursor) == -1 ? "Save Outfit (expected)" : "an outfit"));
                // the ghosts: four outfits straight into the store (not the flow), then page 2
                for (int k = 0; k < 4; k++) outfits.Add(session.CommittedBytes, "Ghost " + (k + 1));
                BuildOutfitEntries();
                page = 1; cursor = 0;
            }));
            script.Add(P(o0 + 9.6f, shot));                                         // page 2: one tile, Left3 / Left4 the ghosts of page 1's right column
            script.Add(P(o0 + 9.62f, delegate
            {
                List<Ghosts.Spec> g = OutfitGhostSpecs();
                Console.WriteLine("[studio] selftest: My Outfits page " + (page + 1) + " of " + OutfitPages + " (2 of 2 expected), " + OutfitsOnPage + " tile(s), "
                    + g.Count + " ghost(s) (2 expected, the previous page's cells 3 and 4), counter " + outfits.Counter + " (1 expected)");
                GoBack();
            }));
            // the carryable: TeddyBear Sleep put on plays its linked Interact animation once (id 3)
            Guid teddy = new Guid("00001000-044d-0003-c1c8-f109a19cb2e0");
            script.Add(P(o0 + 9.8f, delegate { session.PreviewItem(teddy); session.Commit(); }));
            script.Add(P(o0 + 11.2f, shot));                                        // the prop, mid-animation
            script.Add(P(o0 + 11.22f, delegate
            {
                Console.WriteLine("[studio] selftest: TeddyBear Sleep put on: carryable " + (stage.HasCarryable ? "built" : "NOT built")
                    + ", animation " + (stage.CarryableHasAnimation ? "found (its Interact link)" : "NOT found") + "; playing id " + stage.EditorAnimationId
                    + " (3 expected) at " + stage.AnimationAt.ToString("0.00") + " of " + stage.AnimationSeconds.ToString("0.00") + " s, prop "
                    + (stage.CarryableDrawn ? "drawn" : "NOT drawn") + " (drawn expected); the skeletons' roots (the turntable wrapper's B) " + stage.SkeletonRoot);
            }));
            script.Add(P(o0 + 15.6f, delegate
            {
                Console.WriteLine("[studio] selftest: later: playing id " + stage.EditorAnimationId + ", prop " + (stage.CarryableDrawn ? "drawn" : "not drawn")
                    + " (not drawn once id 3 has ended: no carryable keys elsewhere)");
                // the crossfade: the rest goes in 0.3 s before id 3's end, blending 0.3 s
                Console.WriteLine("[studio] selftest: the crossfade: last push " + stage.LastPush + " (3 -> 2 at its length - 0.30 expected), "
                    + stage.MostSlots + " slot(s) at most");
            }));
            const float mo = m + 16.7f;                                              // the steps below, after the outfits' visit
            script.Add(P(21.6f + mo, delegate { GoBack(); GoBack(); GoBack(); cursor = 0; ActivateMain(); Pick("Body Size"); }));
            // Body Size's page comes in by frame 560: 0.83 s after the pick it is at rest
            script.Add(P(22.45f + mo, shot));                                        // body size
            script.Add(P(22.47f + mo, delegate
            {
                // the axes start at the manifest's own values' indices (AE_SizeEntry_IndexFromManifest)
                byte[] mb = session.CommittedBytes;
                Console.WriteLine("[studio] selftest: Body Size starts at height index " + heightStep + " (manifest +8 = " + ManifestFloat(mb, 8).ToString("0.###")
                    + "), width index " + widthStep + " (+4 = " + ManifestFloat(mb, 4).ToString("0.###") + "); accept " + (controls[0] == null ? "settled" : controls[0].Range)
                    + "; look target " + (FocusedTileCentre().HasValue ? FocusedTileCentre().Value.ToString() : "none"));
                // the mouse on the rails: each rail's first and last clasp place reads back its value
                XuiVisualDef rl = XuiVisuals.Get("AvatarHeightWidthButton");
                Texture2D cl = tileArt.Named("clasp_width");
                string got = "";
                if (rl != null && cl != null)
                    foreach (bool hr in new[] { true, false })
                        foreach (int v in new[] { 0, SizeSteps.Length - 1 })
                        {
                            Vector2 at = Vector2.Transform(new Vector2(RailMarkerX(rl, v) + cl.Width / 2f, cl.Height / 2f), (hr ? HeightRailMatrix : WidthRailMatrix) * XuiDraw.SceneToScreen);
                            input.SetMouse(at, false, false);
                            got += (hr ? " height " : " width ") + v + "->" + RailIndexAt(hr);
                        }
                input.SetMouse(new Vector2(-100f, -100f), false, false);
                Console.WriteLine("[studio] selftest: Body Size, the mouse on the rails:" + (got.Length > 0 ? got : " no rail art") + " (each the same expected); markers at "
                    + (rl == null ? "?" : RailMarkerX(rl, 0).ToString("0") + " " + RailMarkerX(rl, 1).ToString("0") + " " + RailMarkerX(rl, 2).ToString("0") + " "
                       + RailMarkerX(rl, 3).ToString("0") + " " + RailMarkerX(rl, 4).ToString("0")));
            }));
            // the exit: the Saving box for 2 s or the save, then 5 s celebrating, fading in the last
            script.Add(P(22.5f + mo, delegate { SaveAndClose(); }));                 // -> the Saving box
            script.Add(P(23.4f + mo, shot));                                         // the box, its spinner faded in
            script.Add(P(23.45f + mo, delegate { Console.WriteLine("[studio] selftest: after Save and Exit, " + state + " (Saving expected)"); }));
            script.Add(P(26.0f + mo, shot));                                         // the empty page, the avatar celebrating
            script.Add(P(26.05f + mo, delegate { Console.WriteLine("[studio] selftest: then " + state + ", editor animation " + stage.EditorAnimationId + " (Exiting, 8 expected)"); }));
            const float x = 8.0f;                                                   // the exit, with room for a slow save
            script.Add(P(23.6f + mo + x, shot));                                     // gallery with the new avatar
            script.Add(P(23.8f + mo + x, delegate { galleryCursor = 1; OpenCardMenu(); }));  // the per-avatar menu
            script.Add(P(24.4f + mo + x, shot));                                     // card menu
            script.Add(P(24.6f + mo + x, delegate { menuCursor = menuItems.IndexOf("Delete"); ActivateCardMenu(); }));
            script.Add(P(25.2f + mo + x, shot));                                     // the delete confirmation
            // No, keep the avatar, through the dialog's button
            script.Add(P(25.3f + mo + x, delegate { confirmCount = library.Records.Count; PressConfirm(1); }));
            script.Add(P(25.58f + mo + x, delegate
            {
                Console.WriteLine("[studio] selftest: No on the delete question: " + state + " (Gallery expected), "
                    + library.Records.Count + " avatars (" + confirmCount + " expected)");
            }));
            // a page turn on the lineup: the old page runs off, the new one runs in
            const float t = 25.6f + mo + x;
            script.Add(P(t, delegate { galleryCursor = 0; GalleryActivate(); }));  // -> new avatar, name entry
            script.Add(P(t + 0.4f, delegate { nameText = "Turn"; ConfirmName(); })); // -> lineup
            script.Add(P(t + 6.4f, delegate
            {
                bool all = lineupStage.AllStanding;
                TurnLineupPage(1);
                Console.WriteLine("[studio] selftest: lineup " + (all ? "all standing" : "NOT all standing") + ", next page: " + (lineupPage + 1) + " (2 expected)");
            }));
            script.Add(P(t + 7.2f, shot));                                          // running off, the end ones crossing
            script.Add(P(t + 8.2f, shot));
            script.Add(P(t + 9.4f, shot));
            script.Add(P(t + 11.0f, shot));                                         // the new page running in
            script.Add(P(t + 18.0f, shot));                                         // page 2 standing
            script.Add(P(t + 18.05f, delegate { Console.WriteLine("[studio] selftest: after the turn, lineup " + lineupStage.StandingCount + " of 7 standing"); }));
            // the rest's gaze: the right stick held up and to the right on the main menu
            script.Add(P(t + 18.2f, delegate { AcceptLineup(); }));                // -> main menu, the new avatar
            script.Add(P(t + 20.5f, delegate { input.Synthetic = delegate(Input i) { i.RightStick = new Vector2(30000f, 20000f); i.StickMoved = true; }; }));
            script.Add(P(t + 22.0f, shot));                                         // looking up and to its left
            script.Add(P(t + 22.05f, delegate
            {
                Console.WriteLine("[studio] selftest: right stick held, gaze (" + stage.Gaze.X.ToString("0.000") + ", " + stage.Gaze.Y.ToString("0.000")
                    + "), " + (stage.RestingInGaze ? "resting in the gaze blend" : "NOT in the gaze blend") + ", editor animation " + stage.EditorAnimationId
                    + "; the rest's nine: " + stage.RestShape + ", on Neutral's cursor");
                input.Synthetic = null;
            }));
            // LB while resting, not a close-up: one of the body's five reactions (sub_920C8BC0)
            script.Add(P(t + 22.3f, delegate { input.Synthetic = delegate(Input i) { i.LeftShoulderHeld = true; }; }));
            script.Add(P(t + 22.4f, delegate { input.Synthetic = null; }));
            script.Add(P(t + 22.6f, delegate
            {
                int id = stage.EditorAnimationId;
                Console.WriteLine("[studio] selftest: LB on the main menu, editor animation " + id + " ("
                    + ((id >= 36 && id <= 40) || (id >= 46 && id <= 50) ? "a reaction" : "NOT a reaction") + "), close-up " + stage.CloseUp);
            }));
            // the stick held left past a second, then flicked right: closet_spin (sub_920C7F98)
            script.Add(P(t + 22.8f, delegate { input.Synthetic = delegate(Input i) { i.RightStick = new Vector2(-30000f, 0f); i.StickMoved = true; }; }));
            script.Add(P(t + 24.3f, delegate { input.Synthetic = delegate(Input i) { i.RightStick = new Vector2(30000f, 0f); i.StickMoved = true; }; }));
            script.Add(P(t + 24.5f, delegate
            {
                input.Synthetic = null;
                Console.WriteLine("[studio] selftest: stick held left then flicked right, editor animation " + stage.EditorAnimationId + " (15 expected, closet_spin)");
            }));
            // Choose New Avatar: it opens on Yes; B goes back to the main menu, A on Yes to a lineup
            script.Add(P(t + 24.6f, delegate { cursor = Array.IndexOf(MainTiles, "Choose New Avatar"); ActivateMain(); }));
            // the spin's +40 (8) through scnPlaySoundSpin: its sound 51 frames on (AvatarVoice.cs)
            script.Add(P(t + 25.35f, delegate
            {
                Console.WriteLine("[studio] selftest: the avatar's voice after closet_spin: " + voice.Started + " sound(s), the last "
                    + (voice.Last ?? "none") + " (character_spinning_on_spot.xma expected)");
            }));
            script.Add(P(t + 25.4f, shot));                                         // the dialog over the avatar, centred
            script.Add(P(t + 25.45f, delegate
            {
                Console.WriteLine("[studio] selftest: Choose New Avatar: " + state + ", focus " + (chooseNewFocus == 0 ? "Yes" : "No")
                    + ", editor animation " + stage.EditorAnimationId + " (ChooseNew, Yes, " + (session.Body == AvatarGender.Female ? 40 : 50) + " expected)");
                input.Synthetic = delegate(Input i) { i.Back = true; input.Synthetic = null; };
            }));
            // B presses the hidden ItemButton3, which acts when its Press range ends (13 frames)
            script.Add(P(t + 25.75f, delegate
            {
                Console.WriteLine("[studio] selftest: B on the dialog: " + state + " on " + MainTiles[cursor] + " (MainMenu on Choose New Avatar expected)");
            }));
            // [ours] Awards and Gamer Picture open a message page; A, then B close it
            script.Add(P(t + 26.2f, delegate { cursor = Array.IndexOf(MainTiles, "Awards"); ActivateMain(); }));
            script.Add(P(t + 26.8f, shot));                                         // the message over the avatar
            script.Add(P(t + 26.85f, delegate
            {
                Console.WriteLine("[studio] selftest: Awards: " + state + ", \"" + messageText + "\" (Message expected)");
                LogDialogWrap();
                input.Synthetic = delegate(Input i) { i.Select = true; input.Synthetic = null; };
            }));
            script.Add(P(t + 27.3f, delegate
            {
                Console.WriteLine("[studio] selftest: A on OK: " + state + " on " + MainTiles[cursor] + " (MainMenu on Awards expected)");
                cursor = Array.IndexOf(MainTiles, "Gamer Picture"); ActivateMain();
            }));
            script.Add(P(t + 27.9f, delegate
            {
                Console.WriteLine("[studio] selftest: Gamer Picture: " + state + " (Message expected)");
                input.Synthetic = delegate(Input i) { i.Back = true; input.Synthetic = null; };
            }));
            script.Add(P(t + 28.4f, delegate
            {
                Console.WriteLine("[studio] selftest: B on the message: " + state + " on " + MainTiles[cursor] + " (MainMenu on Gamer Picture expected)");
                cursor = Array.IndexOf(MainTiles, "Choose New Avatar");
            }));
            // the right thumb held, resting, the stick centred: a burp by the centre cell; let go, it stops
            int burpWas = 0;
            script.Add(P(t + 30.0f, delegate { burpWas = voice.Started; input.Synthetic = delegate(Input i) { i.RightThumbHeld = true; }; }));
            script.Add(P(t + 30.2f, delegate
            {
                Console.WriteLine("[studio] selftest: right thumb held: voice " + stage.Voice + " (" + (session.Body == AvatarGender.Female ? 3 : 5) + " expected), "
                    + (voice.Started - burpWas) + " sound(s), the last " + (voice.Last ?? "none") + " (" + (session.Body == AvatarGender.Female ? "Burp_high" : "Burp_medium")
                    + ".xma expected), rest mode " + stage.RestMode + " (9)");
                input.Synthetic = null;
            }));
            script.Add(P(t + 30.3f, delegate { Console.WriteLine("[studio] selftest: right thumb let go: voice " + stage.Voice + " (0 expected: Idle stops the burp)"); }));
            // the look timers: the camera, then the focused tile, each logged once the gaze eased in
            script.Add(P(t + 31.0f, delegate { stage.StartLook(true, 5f); }));
            script.Add(P(t + 32.5f, shot));                                         // looking at the camera
            script.Add(P(t + 32.55f, delegate { LogLook("the camera"); }));
            script.Add(P(t + 33.0f, delegate { stage.StartLook(false, 5f); }));     // the camera's still runs: it wins
            script.Add(P(t + 36.5f, shot));                                         // the camera's over: the tile
            script.Add(P(t + 36.55f, delegate { LogLook("the focused tile"); }));
            // [ours] closing the window keeps the open avatar: "Turn", never saved, with a preview on.
            // The file read back must hold the picks, not the preview.
            script.Add(P(t + 36.56f, delegate
            {
                bool wasNew = record == null;
                byte[] picks = AvatarLibrary.ToManifest(session.CommittedBytes);
                session.PreviewItem(teddy);
                bool previewOn = !AvatarLibrary.SameBytes(AvatarLibrary.ToManifest(session.WorkingBytes), picks);
                string did = SaveOnClose();
                AvatarRecord back = record == null || !File.Exists(record.Path) ? null : AvatarLibrary.ReadFile(record.Path);
                session.Revert();
                Console.WriteLine("[studio] selftest: closing the window on the main menu (" + (wasNew ? "a new avatar" : "NOT a new avatar")
                    + ", a preview on: " + (previewOn ? "yes" : "NO") + "): " + did + "; the file holds the picks, not the preview: "
                    + (back != null && AvatarLibrary.SameBytes(back.Manifest, picks) ? "yes" : "NO"));
            }));
            script.Add(P(t + 36.6f, delegate
            {
                ActivateMain();
                input.Synthetic = delegate(Input i) { i.Select = true; input.Synthetic = null; };
            }));
            // the press acts when Yes's Press range ends (Grid1x1Button 209..222, 13 frames)
            script.Add(P(t + 37.0f, delegate
            {
                Console.WriteLine("[studio] selftest: A on Yes: " + state + " (Lineup expected)");
                Console.WriteLine("[studio] selftest: closing the window on the lineup: " + SaveOnClose() + " (no avatar open (Lineup) expected)");
            }));
            script.Add(P(t + 37.2f, delegate { Console.WriteLine("[studio] selftest done"); Exit(); }));
        }

        private void LogTransition()
        {
            Vector3 p; float o; bool e;
            float tn = time - pageSceneAt;
            string s = "[studio] selftest: page transition " + (tn * 60f).ToString("0.0") + " frames in: new " + curScene + " " + curDir + "In";
            if (PageTransitions.Evaluate(curScene, curDir, true, tn, out p, out o, out e))
                s += " (x " + p.X.ToString("0.0") + ", z " + p.Z.ToString("0.0") + ", opacity " + o.ToString("0.00") + ")";
            if (oldActive && PageTransitions.Evaluate(oldScene, oldDir, false, time - oldStart, out p, out o, out e))
                s += "; old " + oldScene + " " + oldDir + "Out (x " + p.X.ToString("0.0") + ", z " + p.Z.ToString("0.0") + ", opacity " + o.ToString("0.00") + ")";
            else s += "; no old scene";
            s += "; page change " + (pageChanging ? "up (header, page number, handles, prompt hidden; triggers " + (TriggersSpin ? "ON" : "off") + ")" : "down");
            Console.WriteLine(s);
        }

        /// <summary>GridTopLevel played on its own (XuiVisuals.cs): the pop, the nod, the press.</summary>
        private void LogVisuals()
        {
            XuiVisualDef d = XuiVisuals.Get("GridTopLevel");
            if (d == null) { Console.WriteLine("[studio] selftest: no visuals installed (visuals.txt): controls settled"); return; }
            int u = d.Index("unselected");
            XuiPlayer p = new XuiPlayer(d);
            p.Build(true);
            p.SetFocus(true, false);
            string s = "[studio] selftest: GridTopLevel focus (" + p.Range + "), unselected scale at frames";
            int at = 0;
            foreach (int f in new[] { 0, 3, 6, 9, 13, 60, 120 })
            {
                for (; at < f; at++) p.Advance(1f / 60f);
                s += " " + f + ":" + p.Evaluate(u).Scale.X.ToString("0.000");
            }
            p.SetFocus(false, false);
            s += "; unfocused (" + p.Range + "), nod about x at frames";
            at = 0;
            foreach (int f in new[] { 5, 10, 20, 31, 45 })
            {
                for (; at < f; at++) p.Advance(1f / 60f);
                Quaternion q = p.Evaluate(u).Rotation;
                s += " " + f + ":" + MathHelper.ToDegrees(2f * (float)Math.Asin(MathHelper.Clamp(q.X, -1f, 1f))).ToString("0.00");
            }
            XuiPlayer b = new XuiPlayer(d);
            b.Build(true);
            b.SetFocus(true, false);
            int fired = -1, n = 0;
            b.Press(delegate { fired = n; });
            for (n = 1; n <= 60 && fired < 0; n++) b.Advance(1f / 60f);
            s += "; press acts after " + fired + " frames, then " + b.Range + " at frame " + b.Head.ToString("0");
            Console.WriteLine(s);
        }

        /// <summary>Two item cells mid-move: the one left and the one focused.</summary>
        private void LogCells(int from, int to, float since)
        {
            string s = "[studio] selftest: " + (since * 60f).ToString("0.0") + " frames after a cell move:";
            foreach (int i in new[] { from, to })
            {
                XuiPlayer p = controls[i];
                if (p == null) { s += " cell " + i + " has no visual;"; continue; }
                XuiElementState e = p.Evaluate(p.Def.Index("button bg selected"));
                s += " cell " + i + " (" + p.Def.Id + ") " + p.Range + " frame " + p.Head.ToString("0.0") + " frame scale " + e.Scale.X.ToString("0.000")
                   + " nod " + MathHelper.ToDegrees(2f * (float)Math.Asin(MathHelper.Clamp(e.Rotation.X, -1f, 1f))).ToString("0.00") + ";";
            }
            Console.WriteLine(s);
        }

        /// <summary>The main menu's first two tiles, mid-move.</summary>
        private void LogTiles(float since)
        {
            string s = "[studio] selftest: " + (since * 60f).ToString("0.0") + " frames after the move:";
            for (int i = 0; i < 2; i++)
            {
                XuiPlayer p = controls[i];
                if (p == null) { s += " tile " + i + " has no visual;"; continue; }
                XuiElementState e = p.Evaluate(p.Def.Index("unselected"));
                s += " tile " + i + " " + p.Range + " frame " + p.Head.ToString("0.0") + " scale " + e.Scale.X.ToString("0.000")
                   + " nod " + MathHelper.ToDegrees(2f * (float)Math.Asin(MathHelper.Clamp(e.Rotation.X, -1f, 1f))).ToString("0.00") + ";";
            }
            // the focused tile's visual keys State 2 on Focus (61): its sound (Sounds.cs)
            s += " sounds started " + Sounds.Started + ", last " + (Sounds.Last ?? "none") + " (selecting_an_item.xma expected)";
            Console.WriteLine(s);
        }

        /// <summary>After the change: the flag down, the header and the rest back, the shadows per side.</summary>
        private void LogSettled()
        {
            int entries = Math.Min(pageItems.Count + RemoveSlots, Layout.CellsPerPage);
            bool sl, sr;
            ShadowSides(entries, false, out sl, out sr);
            Console.WriteLine("[studio] selftest: " + ((time - pageSceneAt) * 60f).ToString("0") + " frames in: page change " + (pageChanging ? "STILL UP" : "down")
                + ", old scene " + (oldActive ? "STILL PLAYING" : "gone") + ", gated draws " + (gatedMove.Count + gatedFixed.Count)
                + ", triggers " + (TriggersSpin ? "on" : "OFF") + ", shadows " + (sl ? "L" : "-") + (sr ? "R" : "-") + " for " + entries + " entries");
        }

        private void LogLook(string what)
        {
            Console.WriteLine("[studio] selftest: look at " + what + ": " + (stage.Looking ? "running" : "NOT running")
                + ", yaw " + stage.LookYawPitch.X.ToString("0.0000") + ", pitch " + stage.LookYawPitch.Y.ToString("0.0000")
                + ", gaze (" + stage.Gaze.X.ToString("0.000") + ", " + stage.Gaze.Y.ToString("0.000") + "), "
                + (stage.RestingInGaze ? "resting in the gaze blend" : "NOT in the gaze blend") + ", editor animation " + stage.EditorAnimationId);
        }

        /// <summary>On Tops: the first colourable top's file table against the catalogue, then pick its sets.</summary>
        private void ColourSetTour(bool log)
        {
            if (state != State.Items) { Console.WriteLine("[studio] selftest: not on an item screen for the colour sets"); return; }
            AvatarGender body = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
            List<CatalogItem> all = catalog.For(activeLeaf.Category, body, onDiskOnly: true);
            int i = all.FindIndex(delegate(CatalogItem c) { return c.Toc != null && c.Toc.Colourable; });
            if (i < 0) { Console.WriteLine("[studio] selftest: no colourable top for this body (TOC: " + AvatarToc.Status + ")"); return; }
            CatalogItem it = all[i];
            if (!log)
            {
                int e0 = i + (HasRemoveCell ? 1 : 0);
                page = e0 / Layout.CellsPerPage; RebuildPage(); cursor = e0 % Layout.CellsPerPage;
                CommitItem();
                return;
            }
            ColourSetList file = ColourSets.FromAsset(it.Id, catalog.AssetDir);
            uint[][] toc = it.Toc.Sets;
            bool reversed = file != null && file.Sets.Length == toc.Length;
            for (int k = 0; reversed && k < toc.Length; k++) reversed = ColourSets.Same(file.Sets[k], toc[toc.Length - 1 - k]);
            Console.WriteLine("[studio] selftest: " + it.Name + ": " + toc.Length + " TOC sets; the file's table " +
                              (file == null ? "unreadable" : file.Sets.Length + " sets, " + (reversed ? "the TOC's in reverse order" : "NOT the TOC's reversed")));
            int e = i + (HasRemoveCell ? 1 : 0);
            page = e / Layout.CellsPerPage; RebuildPage(); cursor = e % Layout.CellsPerPage;
            CommitItem();
            Console.WriteLine("[studio] selftest: picked " + it.Name + "; now on " + state + " (ColourSets expected)");
        }

        /// <summary>Selftest: move to the child called `name` on the current grid and open it.</summary>
        private void Pick(string name)
        {
            int i = node == null ? -1 : node.Children.FindIndex(delegate(Node c) { return c.Name == name; });
            if (i < 0) { Console.WriteLine("[studio] selftest: no \"" + name + "\" here"); return; }
            cursor = i; ActivateGrid();
        }

        /// <summary>--selftest --fill N: look at the gallery with a library in it, both pages.</summary>
        private void BuildGalleryTour(Action shot)
        {
            // this pass shows the keyboard's legend (Input.KeysShown; the editor pass the controller's)
            script.Add(P(0.05f, delegate { Input.ForceKeys = true; }));
            script.Add(P(4.0f, shot));                                              // page 1, thumbnails rendered
            script.Add(P(4.2f, delegate { galleryCursor = 3; }));
            script.Add(P(4.6f, shot));                                              // a card selected
            // right from the page's last column turns to the next page (MoveCardCursor)
            script.Add(P(4.65f, delegate { galleryCursor = Layout.CardCols - 1; input.Synthetic = delegate(Input i) { i.Right = true; input.Synthetic = null; }; }));
            script.Add(P(4.72f, delegate
            {
                Console.WriteLine("[studio] selftest: gallery right from the last column: page " + (galleryCursor / Layout.CardsPerPage + 1) + " (2 expected), card "
                    + galleryCursor + "; legend keys " + (Input.KeysShown ? "shown" : "NOT shown"));
            }));
            script.Add(P(4.8f, delegate { galleryCursor = Layout.CardsPerPage; })); // page 2
            script.Add(P(8.0f, shot));
            script.Add(P(8.2f, delegate { OpenCardMenu(); }));
            script.Add(P(8.8f, shot));                                              // menu over a full gallery
            script.Add(P(9.0f, delegate { state = State.Gallery; FilesRoundTrip(); }));
            script.Add(P(9.4f, shot));                                              // the imports' name screen
            script.Add(P(12.0f, delegate { FilesRoundTripPicture(); Console.WriteLine("[studio] gallery tour done"); Exit(); }));
        }

        // the import and export through the dialogs' own calls, in a folder inside the scratch library
        private string roundTripDir, roundTripPng;

        private void FilesRoundTrip()
        {
            AvatarRecord r = GalleryRecord();
            if (r == null) { Console.WriteLine("[studio] selftest: files: no avatar highlighted"); return; }
            roundTripDir = Path.Combine(AvatarLibrary.Directory, "selftest-files");
            Directory.CreateDirectory(roundTripDir);
            string bin = Path.Combine(roundTripDir, "Exported.bin"), av = Path.Combine(roundTripDir, "Exported.avatar");
            roundTripPng = Path.Combine(roundTripDir, "Exported.png");
            ExportFile(r, bin, AvatarFiles.Format.Manifest);
            ExportFile(r, av, AvatarFiles.Format.Avatar);
            ExportFile(r, roundTripPng, AvatarFiles.Format.Png);
            string why;
            ImportedAvatar fromBin = AvatarFiles.Read(bin, out why), fromAv = AvatarFiles.Read(av, out why);
            bool binOk = fromBin != null && AvatarLibrary.SameBytes(fromBin.Manifest, r.Manifest) && fromBin.Name == "Exported";
            bool avOk = fromAv != null && AvatarLibrary.SameBytes(fromAv.Manifest, r.Manifest) && fromAv.Name == r.Name
                        && (r.ThumbnailPng == null) == (fromAv.Png == null);
            int before = library.Records.Count;
            AvatarRecord again = ImportFile(bin);                       // already here: nothing added
            int afterSame = library.Records.Count;
            // a manifest the library does not hold, as a bare file: imported under the file's name
            byte[] other = AvatarStack.Random(AvatarGender.Both, 1)[0].SaveToBinary();
            string otherPath = Path.Combine(roundTripDir, "Stranger.bin");
            File.WriteAllBytes(otherPath, other);
            AvatarRecord stranger = ImportFile(otherPath);
            Console.WriteLine("[studio] selftest: files: \"" + r.Name + "\" out as avatar.bin " + (binOk ? "ok" : "WRONG")
                + ", as .avatar " + (avOk ? "ok" : "WRONG") + "; imported back " + (again == null && afterSame == before ? "refused (already here)" : "ADDED A COPY")
                + "; a new manifest imported as " + (stranger == null ? "NOTHING" : "\"" + stranger.Name + "\"")
                + ", library " + before + " -> " + library.Records.Count);
            // and a profile's way in: the name screen over a stand-in name
            if (stranger != null) { record = stranger; namePurpose = NamePurpose.Imported; nameText = stranger.Name; nameError = null; state = State.NameEntry; }
        }

        private void FilesRoundTripPicture()
        {
            bool written = roundTripPng != null && File.Exists(roundTripPng);
            string size = "";
            if (written)
                using (FileStream fs = File.OpenRead(roundTripPng))
                using (Texture2D t = Texture2D.FromStream(GraphicsDevice, fs)) size = " " + t.Width + " x " + t.Height;
            Console.WriteLine("[studio] selftest: files: picture " + (written ? "written," + size : "NOT written"));
        }

        private static KeyValuePair<float, Action> P(float t, Action a) { return new KeyValuePair<float, Action>(t, a); }

        private void RefreshStage()
        {
            stage.Set(session.WorkingBytes, session.Body == AvatarGender.Female);
        }

        // ================================ UPDATE ================================================
        protected override void Update(GameTime gt)
        {
            // a scripted shot holds the clock until its Draw, so catch-up Updates cannot move the scene
            if (pendingShot != null) { base.Update(gt); return; }
            float dt = (float)gt.ElapsedGameTime.TotalSeconds;
            time += dt;
            while ((selftest || handoffTest || fuzzStart != null || importTest || setupShots) && scriptStep < script.Count && time >= script[scriptStep].Key)
            {
                script[scriptStep].Value(); scriptStep++;
            }
            input.TextEntryActive = state == State.NameEntry || (state == State.OutfitBusy && outfitNaming);
            input.Update(dt);
            // the legend: its 20 ms timer, this frame's key downs, its timelines; hidden by the keyboard panel
            ui.Bar.SystemUiActive = state == State.OutfitBusy && outfitNaming;
            ui.Bar.Tick(dt, ui);
            // [ours] a click on the legend is its button; random clicks never quit from the gallery
            if (input.MouseClicked)
            {
                int slot = ui.Bar.SlotAt(input.MousePos, ui);
                if (slot >= 0 && !(fuzzFrames > 0 && state == State.Gallery && slot == 1))
                {
                    input.ConsumeClick();
                    if (slot == 0) input.Select = true; else if (slot == 1) input.Back = true; else if (slot == 2) input.X = true; else input.Y = true;
                }
            }
            if (input.Select || input.Start) ui.Bar.Press(0);
            if (input.Back) ui.Bar.Press(1);
            if (input.X) ui.Bar.Press(2);
            if (input.Y) ui.Bar.Press(3);
            ui.Bar.Advance(dt);
            if (fuzzFrames > 0 && ++fuzzFrame >= fuzzFrames) FuzzDone();
            // the gaze, every frame (AE_Turntable_Update)
            stage.FocusedTile = FocusedTileCentre();
            // the state below the top is Props (28): a colour screen pushed from a Props item
            stage.OverProps = (state == State.Colors || state == State.ColourSets) && followOnFrom != null && ScreenFor(followOnFrom) == EditorCamera.Props;
            stage.Look(dt, new AvatarView.Pad
            {
                Stick = input.RightStick, StickMoved = input.StickMoved && state != State.Lineup,
                LeftShoulder = input.LeftShoulderHeld, RightShoulder = input.RightShoulderHeld, RightThumb = input.RightThumbHeld,
                Triggers = TriggersSpin && (input.TriggerLeft > 0x20 || input.TriggerRight > 0x20),
            });
            // the turntable runs every frame; the triggers reach it only where the screen takes them
            stage.Turntable(dt, TriggersSpin ? input.SpinInput : 0f);
            voice.Tick(time, stage.Voice);          // EditorBG's sound scenes on the behaviour's +40 (AvatarVoice.cs)
            stage.Update(dt);
            thumbs.Update();
            cards.Update();
            UpdatePicture();
            if (state == State.Lineup)
            {
                lineupStage.Focus(lineupIndex);
                lineupStage.Update(dt);
            }
            // the mirror's timer (AvatarMirrorParentSceneOverride): out except on My Style (22)
            mirror.Tick(state == State.Grid && stage.Screen == EditorCamera.Style);
            // scnFade's opacity: in the exit 1 - timer under 1 s, else falling by the frame time
            fade = state == State.Exiting ? (exitTimer <= 1f ? 1f - exitTimer : 0f) : fade - dt;
            fade = MathHelper.Clamp(fade, 0f, 1f);

            // a page built by the script or last frame has its buttons before input reaches them
            PageTick();
            switch (state)
            {
                case State.Gallery: UpdateGallery(); break;
                case State.CardMenu: UpdateCardMenu(); break;
                case State.Confirm: UpdateConfirm(); break;
                case State.NameEntry: UpdateName(dt); break;
                case State.Lineup: UpdateLineup(dt); break;
                case State.MainMenu: UpdateMainMenu(); break;
                case State.ChooseNew: UpdateChooseNew(); break;
                case State.Message: UpdateMessage(); break;
                case State.Grid: UpdateGrid(); break;
                case State.Items: UpdateItems(); break;
                case State.Colors: UpdateColors(); break;
                case State.ColourSets: UpdateColourSets(); break;
                case State.BodySize: UpdateBodySize(dt); break;
                case State.Outfits: UpdateOutfits(dt); break;
                case State.DeleteOutfit: UpdateDeleteOutfit(); break;
                case State.OutfitBusy: UpdateOutfitBusy(); break;
                case State.OutfitTutorial: UpdateOutfitTutorial(); break;
                case State.Saving: UpdateSaving(dt); break;
                case State.Exiting: UpdateExit(dt); break;
                case State.Setup: UpdateSetup(); break;
            }
            // the bonus hair colours' window and sound, and the Hairstyles tile's hint (AE_Behaviour_StateUpdate)
            if (session != null && bonus.Update(dt, stage.Screen, page, session.Color(DynamicColorType.Hair)))
                Sounds.Play("Feature_unlocked.xma");                       // EditorBG's sfxBonusUnlocked
            bool hint = bonus.Window && stage.Screen == EditorCamera.Features && state == State.Grid;
            if (hint != bonusHint) { bonusHint = hint; bonusSpinAt = hint ? time : -1f; }
            // the page scene's age and its transition: a new scene on every state change and page turn
            PageTick();
            controls.Advance(dt);
            BackTick();
            PageSounds();
            TickTooltips(dt);
            LineupMusicTick();
            Music.Update(dt);
            base.Update(gt);
        }

        /// <summary>The page scene and its buttons, before the screen reads input and again after.</summary>
        private void PageTick()
        {
            long sceneKey = ((long)state * 1000 + stage.Screen) * 100000 + page;
            if (sceneKey != pageSceneKey) { float born = pageSceneAt; pageSceneKey = sceneKey; pageSceneAt = time; NewPageScene(born); }
            else SettlePage();
            SyncControls();
        }
        private long pageSceneKey = -1;
        private float pageSceneAt;

        // the bonus hair colours (BonusColours.cs): the behaviour's flags, the Hairstyles tile's spin
        private readonly BonusColours bonus = new BonusColours();
        private readonly AvatarVoice voice = new AvatarVoice();
        private bool bonusHint;
        private float bonusSpinAt = -1f;
        private int bonusDyed;

        // ================================ PAGE TRANSITIONS =====================================
        // Each editor page is drawn in two layers, moving and fixed, composited as the console slides and fades them.

        private RenderTarget2D pageMove, pageFixed, oldMove, oldFixed;
        private PageScene curScene, oldScene;
        private PageDir curDir, oldDir;
        private float oldStart, oldBorn;
        private bool oldActive, pageFresh, lastWasPage;
        private State lastPageState;
        private int lastPageScreen = -2, lastPage, lastDepth = -1;
        private bool collectingFixed;
        private readonly List<Action> fixedDraws = new List<Action>();
        private bool pageChanging, shadowsSettled, pageFirst;
        private readonly XuiControlSet controls = new XuiControlSet();     // the page's buttons' visuals (XuiVisuals.cs)
        private readonly List<Action<float, float>> gatedMove = new List<Action<float, float>>(), gatedFixed = new List<Action<float, float>>();

        private static bool IsPageState(State s)
        {
            switch (s)
            {
                case State.MainMenu: case State.ChooseNew: case State.Message: case State.Grid: case State.Items: case State.Colors:
                case State.ColourSets: case State.BodySize: case State.Outfits: case State.DeleteOutfit: case State.Saving: case State.Exiting:
                case State.OutfitBusy: case State.OutfitTutorial:
                    return true;
            }
            return false;
        }

        /// <summary>The scene each editor page is (GridMain, Grid, Grid2x3, HeightAndWidth, Grid2x2Tall, Dialog, Progress).</summary>
        private static PageScene PageSceneFor(State s)
        {
            switch (s)
            {
                case State.MainMenu: case State.Grid: return PageScene.GridMain;
                case State.Items: return PageScene.Grid;
                case State.Colors: case State.ColourSets: return PageScene.Grid2x3;
                case State.BodySize: return PageScene.HeightAndWidth;
                case State.Outfits: return PageScene.Grid2x2Tall;
                case State.ChooseNew: case State.DeleteOutfit: case State.Message: return PageScene.Dialog;
                case State.Saving: case State.OutfitBusy: return PageScene.Progress;
                case State.OutfitTutorial: return PageScene.Tutorial;
            }
            return PageScene.None;
        }

        /// <summary>The console's state stack depth for the screen: the main menu is its root.</summary>
        private int StackDepth()
        {
            switch (state)
            {
                case State.MainMenu: return 0;
                case State.ChooseNew: case State.Message: case State.Saving: return 1;
                case State.Exiting: return 2;
                case State.Grid: return 1 + navStack.Count;
                case State.Items: case State.BodySize: case State.Outfits: return 2 + navStack.Count;
                // overrides leave the depth as it is (sub_920ED818)
                case State.DeleteOutfit: case State.OutfitBusy: case State.OutfitTutorial: return overrideDepth;
                case State.Colors: case State.ColourSets: return (followOnFrom != null ? 3 : 2) + navStack.Count;
            }
            return 0;
        }

        /// <summary>A new page scene: the direction (AE_SceneHost_Timer) and the old scene's Out.</summary>
        private void NewPageScene(float born)
        {
            bool isPage = IsPageState(state);
            int depth = StackDepth();
            if (isPage)
            {
                PageDir dir;
                bool turn = lastWasPage && state == lastPageState && stage.Screen == lastPageScreen;
                if (turn)
                    dir = page >= lastPage ? PageDir.Right : PageDir.Left;               // a page turn
                else
                    dir = !lastWasPage || depth >= lastDepth ? PageDir.Forward : PageDir.Backward;
                if (lastWasPage)
                {
                    // the last frame's layers are the old scene's (unless no frame was drawn since)
                    if (pageFresh) { Swap(ref pageMove, ref oldMove); Swap(ref pageFixed, ref oldFixed); pageFresh = false; }
                    oldScene = curScene; oldDir = PageTransitions.Opposite(dir); oldStart = time; oldBorn = born;
                    oldActive = oldMove != null;
                    if (oldVoice != null) oldVoice.Stop();                 // a scene destroyed before its Out ended
                    oldVoice = curVoice;
                }
                else { oldActive = false; oldVoice = null; }
                curScene = PageSceneFor(state); curDir = dir;
                curVoice = new Sounds.Voice(4, PageTransitions.SoundFile(curScene));   // XuiSoundXAudio1: State 4
                // the music's activity flag: AE_OnStatePushed_Camera / _Popped_Camera, AE_SceneHost_PageNotify
                Music.Activity();
                if (!turn) pageChanging = true;                 // sub_920ECE28: a push or a pop
                shadowsSettled = false;                         // a new instance: the scene's own shadows
                pageFirst = !oldActive;                         // no old scene: XUI_HostAddScene with 253 (XuiControlSet)
            }
            else { oldActive = false; pageChanging = false; curVoice = null; oldVoice = null; }   // [not read] into the lineup or the studio's own screens
            lastWasPage = isPage; lastPageState = state; lastPageScreen = stage.Screen; lastPage = page; lastDepth = depth;
            SettlePage();
        }

        private Sounds.Voice curVoice, oldVoice;     // each page scene's XuiSoundXAudio1

        /// <summary>Each scene's sound element at its head: the In's on the new scene, the Out's on the old.</summary>
        private void PageSounds()
        {
            int s; string f;
            if (curVoice != null && PageTransitions.SoundAt(curScene, curDir, true, time - pageSceneAt, out s, out f)) curVoice.Apply(s, f);
            if (oldActive && oldVoice != null && PageTransitions.SoundAt(oldScene, oldDir, false, time - oldStart, out s, out f)) oldVoice.Apply(s, f);
            Sounds.Update();
        }

        /// <summary>The old scene's Out over (AE_SceneHost_TimelineEnd): the flag down, the shadows per side.</summary>
        private void SettlePage()
        {
            Vector3 p; float o; bool ended;
            if (oldActive && PageTransitions.Evaluate(oldScene, oldDir, false, time - oldStart, out p, out o, out ended) && !ended) return;
            oldActive = false;
            pageChanging = false;
            shadowsSettled = true;
        }

        /// <summary>The page's buttons and their visuals (XuiControlSet).</summary>
        private void SyncControls()
        {
            switch (state)
            {
                case State.MainMenu:
                    controls.Sync(pageSceneKey, pageFirst, MainTiles.Length, delegate(int i) { return "GridTopLevel"; }, null, cursor);
                    break;
                case State.Grid:
                    if (node == null) goto default;
                    List<Node> kids = node.Children;
                    controls.Sync(pageSceneKey, pageFirst, kids.Count, delegate(int i) { return "GridTopLevel"; },
                                  delegate(int i) { return ColorEntryEnabled(kids[i]); }, cursor);
                    break;
                case State.Items:
                {
                    // Grid1x1ItemButton (0x92006118, the item entries), Grid1x1Button_deleteitem for the remove cell
                    int slots = RemoveSlots, total = Math.Min(pageItems.Count + slots, Layout.CellsPerPage);
                    controls.Sync(pageSceneKey, pageFirst, total, delegate(int i) { return i < slots ? "Grid1x1Button_deleteitem" : "Grid1x1ItemButton"; }, null, cursor);
                    break;
                }
                case State.Colors:
                {
                    // Grid1x1OneColourButton (0x9200886C, AE_BuildColourChips), the remove cell Grid1x1Button_deleteitem
                    int slots = ColorRemoveSlots, baseIdx = page * Layout.ChipsPerPage;
                    int n = Math.Max(0, Math.Min(Layout.ChipsPerPage, ColorEntries - baseIdx));
                    controls.Sync(pageSceneKey, pageFirst, n, delegate(int i) { return baseIdx + i < slots ? "Grid1x1Button_deleteitem" : "Grid1x1OneColourButton"; }, null, cursor);
                    break;
                }
                case State.ColourSets:
                {
                    // by the entry's colour count (sub_920DF4F8): One / Two / ThreeColourButton, else Grid1x1ItemButton
                    int baseIdx = page * Layout.ChipsPerPage, n = Math.Max(0, Math.Min(Layout.ChipsPerPage, sets.Sets.Length - baseIdx));
                    int m = sets.Shown;
                    string v = m == 1 ? "Grid1x1OneColourButton" : m == 2 ? "Grid1x1TwoColourButton" : m == 3 ? "Grid1x1ThreeColourButton" : "Grid1x1ItemButton";
                    controls.Sync(pageSceneKey, pageFirst, n, delegate(int i) { return v; }, null, cursor);
                    break;
                }
                case State.ChooseNew:
                    // the entries' Grid1x1Button (0x9200ABB0) on ItemButton1..3; ItemButton3 is hidden (not drawn)
                    controls.Sync(pageSceneKey, pageFirst, 3, delegate(int i) { return "Grid1x1Button"; }, null, chooseNewFocus);
                    break;
                case State.Message:
                    // [ours] OK, focused, and the hidden button B presses (as the dialog's ItemButton3)
                    controls.Sync(pageSceneKey, pageFirst, 2, delegate(int i) { return "Grid1x1Button"; }, null, 0);
                    break;
                case State.Outfits:
                    // GridEditorBoxartButton on every tile; Save Outfit enabled while unsaved and there is room
                    controls.Sync(pageSceneKey, pageFirst, OutfitsOnPage, delegate(int i) { return "GridEditorBoxartButton"; },
                                  delegate(int i) { return OutfitEntry(i) >= 0 || SaveOutfitState() == 0; }, cursor);
                    break;
                case State.DeleteOutfit:
                    controls.Sync(pageSceneKey, pageFirst, 3, delegate(int i) { return "Grid1x1Button"; }, null, deleteOutfitFocus);
                    break;
                case State.Confirm:
                    // the gallery's delete question: no scene before it, so its focus plays InitFocus ([ours])
                    controls.Sync(pageSceneKey, true, 3, delegate(int i) { return "Grid1x1Button"; }, null, Math.Min(menuCursor, 1));
                    break;
                case State.OutfitTutorial:
                    // both entries are sub_920DD788's Grid1x1Button (0x9200ABB0); ItemButton2 is hidden (B)
                    controls.Sync(pageSceneKey, pageFirst, 2, delegate(int i) { return "Grid1x1Button"; }, null, 0);
                    break;
                case State.BodySize:
                    // the accept, always focused; the rails have no states
                    controls.Sync(pageSceneKey, pageFirst, 1, delegate(int i) { return "Grid1x1Button"; }, null, 0);
                    break;
                default:
                    controls.Sync(pageSceneKey, pageFirst, 0, null, null, -1);
                    break;
            }
        }

        /// <summary>A draw the page change hides: queued with the scene's moving or fixed part, drawn at the composite.</summary>
        private void Gated(bool moving, Action<float, float> a)
        {
            if (collectingFixed) (moving ? gatedMove : gatedFixed).Add(a);
            else if (!pageChanging) a(1f, time - pageSceneAt);
        }

        /// <summary>A page's header: category_header, 24 pt, 0x1111, #FFEBEBEB, in its 695.31 x 61.9334 box.</summary>
        private void CategoryHeader(string title, Vector2 at, float o)
        {
            ui.Xui(24f).DrawXui(ui.Batch, title, at, Layout.SceneScale, Layout.CategoryW, Layout.CategoryH, 0x1111,
                                Palette.SceneText * o, Palette.DropShadow);
        }

        /// <summary>A page's "Page n of m": category_page_number, 16 pt, 0x1110, #FFFFFFFF.</summary>
        private void PageNumber(string text, Vector2 at, float o)
        {
            ui.Xui(16f).DrawXui(ui.Batch, text, at, Layout.SceneScale, Layout.PageNumberW, Layout.PageNumberH, 0x1110,
                                Color.White * o, Palette.DropShadow);
        }

        /// <summary>sub_920D5C90: which floor shadows show on a two-column page; both before it settles.</summary>
        private void ShadowSides(int entries, bool columnMajor, out bool left, out bool right)
        {
            if (!shadowsSettled) { left = right = true; return; }
            left = entries >= 1;
            right = columnMajor ? entries > Layout.ChipRows : entries >= 2;
        }

        private static void Swap(ref RenderTarget2D a, ref RenderTarget2D b) { RenderTarget2D t = a; a = b; b = t; }

        /// <summary>A draw of the scene's fixed part: queued while the moving layer is drawn.</summary>
        private void Fixed(Action a) { if (collectingFixed) fixedDraws.Add(a); else a(); }

        private RenderTarget2D PageTarget()
        {
            return new RenderTarget2D(GraphicsDevice, Layout.W, Layout.H, false, SurfaceFormat.Color, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        }

        /// <summary>Before the scene pass: the page's moving layer, then its fixed one; the legend after.</summary>
        private void RenderPageLayers()
        {
            if (pageMove == null) { pageMove = PageTarget(); pageFixed = PageTarget(); oldMove = PageTarget(); oldFixed = PageTarget(); }
            GraphicsDevice.SetRenderTarget(pageMove);
            GraphicsDevice.Clear(Color.Transparent);
            collectingFixed = true;
            fixedDraws.Clear();
            gatedMove.Clear(); gatedFixed.Clear();
            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            switch (state)
            {
                case State.MainMenu: DrawMainMenu(); break;
                case State.ChooseNew: DrawChooseNew(); break;
                case State.Message: DrawMessage(); break;
                case State.Grid: DrawGrid(); break;
                case State.Items: DrawItems(); break;
                case State.Colors: DrawColors(); break;
                case State.ColourSets: DrawColourSets(); break;
                case State.BodySize: DrawBodySize(); break;
                case State.Outfits: DrawOutfits(); break;
                case State.DeleteOutfit: DrawDeleteOutfit(); break;
                case State.OutfitBusy: ProgressBox.Draw(ui, tileArt, outfitBusyText, time - outfitBusyAt); break;   // override 78: no legend
                case State.OutfitTutorial: DrawOutfitTutorial(); break;
                case State.Saving: ProgressBox.Draw(ui, tileArt, "Saving", boxTime); break;   // Strings.xus 184
                case State.Exiting: break;                  // state 1: empty.xur, no legend
            }
            ui.Batch.End();
            collectingFixed = false;
            GraphicsDevice.SetRenderTarget(pageFixed);
            GraphicsDevice.Clear(Color.Transparent);
            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            foreach (Action a in fixedDraws) a();
            ui.Batch.End();
            fixedDraws.Clear();
            pageFresh = true;
        }

        /// <summary>In the scene pass: the old scene's layers while its Out plays, then the new one's.</summary>
        private void CompositePage()
        {
            if (!IsPageState(state) || pageMove == null) return;
            if (oldActive && !DrawPageLayers(oldMove, oldFixed, oldScene, oldDir, false, time - oldStart, time - oldBorn)) oldActive = false;
            DrawPageLayers(pageMove, pageFixed, curScene, curDir, true, time - pageSceneAt, time - pageSceneAt);
        }

        /// <summary>One scene's layers at its timeline's offset, depth and opacity; false when its Out has ended.</summary>
        private bool DrawPageLayers(RenderTarget2D move, RenderTarget2D fix, PageScene scene, PageDir dir, bool incoming, float seconds, float age)
        {
            Vector3 pos; float opacity; bool ended;
            if (!PageTransitions.Evaluate(scene, dir, incoming, seconds, out pos, out opacity, out ended))
            {
                if (!incoming) return false;
                pos = Vector3.Zero; opacity = 1f;
            }
            else if (!incoming && ended) return false;           // message 15: the host destroys it
            Vector2 topLeft; float scale;
            PageTransitions.Place(pos, out topLeft, out scale);
            bool behind = PageTransitions.FixedBehind(scene);
            if (behind) ui.Batch.Draw(fix, Vector2.Zero, Color.White * PageTransitions.FixedOpacity(scene, dir, incoming, seconds));
            ui.Batch.Draw(move, topLeft, null, Color.White * opacity, 0f, Vector2.Zero, scale, SpriteEffects.None, 0f);
            if (!pageChanging && gatedMove.Count > 0)
            {
                ui.Batch.End();
                ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, null, null, null, null,
                               Matrix.CreateScale(scale) * Matrix.CreateTranslation(topLeft.X, topLeft.Y, 0f));
                foreach (Action<float, float> a in gatedMove) a(opacity, age);
                ui.Batch.End();
                ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            }
            if (!behind) ui.Batch.Draw(fix, Vector2.Zero, Color.White * PageTransitions.FixedOpacity(scene, dir, incoming, seconds));
            if (!pageChanging) foreach (Action<float, float> a in gatedFixed) a(1f, age);
            return true;
        }

        // ================================ GALLERY ===============================================
        // The home screen: card 0 is New Avatar, cards 1..N the library, eight to a page.

        private int GalleryCount { get { return library.Records.Count + 1; } }
        private int GalleryPages { get { return Math.Max(1, (GalleryCount + Layout.CardsPerPage - 1) / Layout.CardsPerPage); } }

        /// <summary>The highlighted avatar, or null when the highlight is on the "New Avatar" card.</summary>
        private AvatarRecord GalleryRecord()
        {
            int i = galleryCursor - 1;
            return i >= 0 && i < library.Records.Count ? library.Records[i] : null;
        }

        private void EnterGallery()
        {
            state = State.Gallery;
            record = null;
            Music.Stop();                                   // [ours] the gallery has no music: the editor is gone
            stage.Celebrate = false;
            galleryCursor = (int)MathHelper.Clamp(galleryCursor, 0, GalleryCount - 1);
        }

        private void ReloadLibrary()
        {
            foreach (Texture2D t in cardThumbs.Values) if (t != null) t.Dispose();
            cardThumbs.Clear();
            library.Load();
        }

        private void SelectInGallery(string name)
        {
            for (int i = 0; i < library.Records.Count; i++)
                if (string.Equals(library.Records[i].Name, name, StringComparison.CurrentCultureIgnoreCase))
                { galleryCursor = i + 1; return; }
        }

        private void UpdateGallery()
        {
            galleryPage = galleryCursor / Layout.CardsPerPage;
            int count = GalleryCount;
            MoveCardCursor(count);
            int hovered;
            if (MouseOverCard(count, out hovered)) { galleryCursor = hovered; if (input.MouseClicked) { GalleryActivate(); return; } }
            if (input.PageLeft && galleryCursor >= Layout.CardsPerPage) galleryCursor -= Layout.CardsPerPage;
            if (input.PageRight && galleryCursor + Layout.CardsPerPage < count) galleryCursor += Layout.CardsPerPage;
            if (input.Select) { GalleryActivate(); return; }
            if (input.X) { if (GalleryRecord() == null) ImportDialog(); else UseWithGame(); return; }   // X on New Avatar: Import
            if (input.Y) { if (GalleryRecord() == null) OpenSetup(); else OpenCardMenu(); return; }   // Y on New Avatar: Setup
            if (input.Start) { PlayGame(); return; }
            if (input.Back) { ExitCode = 0; Exit(); }
        }

        private void GalleryActivate()
        {
            AvatarRecord r = GalleryRecord();
            if (r == null) NewAvatar(); else EditAvatar(r);
        }

        private void NewAvatar()
        {
            record = null;
            namePurpose = NamePurpose.NewAvatar;
            nameText = library.UniqueName("Avatar");
            nameError = null;
            state = State.NameEntry;
        }

        private void EditAvatar(AvatarRecord r)
        {
            if (!assetsReady) { Notice("Avatars can't be changed until Avatar Studio is set up: on the New Avatar card, choose Y Setup."); return; }
            try { session.Replace(AvatarManifest.Create(r.Manifest)); }
            catch (Exception e) { Notice("could not open \"" + r.Name + "\": " + e.Message); return; }
            record = r;
            session.Gamertag = r.Name;
            OpenEditor();
            EnterMainMenu();
            // the launch (sub_920ED998, after AE_PushState(2)): ApplicationIntro, then the shuffle
            Music.SetMode(Music.ModeEditorStart, 0.01f);
        }

        /// <summary>Into the editor from the gallery (the editor's launch): scnFade starts opaque and falls.</summary>
        private void OpenEditor()
        {
            fade = 1f;
            outfits.Open(record == null ? null : record.Path);           // the avatar's own (Outfits.cs)
        }

        /// <summary>Make the highlighted avatar the one games use.</summary>
        private void UseWithGame()
        {
            AvatarRecord r = GalleryRecord();
            if (r == null || target == null) return;
            try
            {
                target.Apply(r.Name, r.Manifest);
                Notice(target.Name + ": " + target.Status);
            }
            catch (Exception e)
            {
                Notice("could not set the avatar: " + e.Message);
                Console.WriteLine("[studio] apply failed: " + e.Message);
            }
        }

        /// <summary>[ours] The avatar games use was edited or renamed: the player avatar takes the new one.</summary>
        private void FollowInGame(AvatarRecord r)
        {
            try
            {
                target.Apply(r.Name, r.Manifest);
                Console.WriteLine("[studio] " + target.Name + " follows \"" + r.Name + "\"");
            }
            catch (Exception e) { Console.WriteLine("[studio] " + target.Name + " not updated: " + e.Message); }
        }

        /// <summary>Play as the highlighted avatar: set it if it is not set already, then launch.</summary>
        private void PlayGame()
        {
            if (target == null || !allowLaunch || !target.CanLaunch) return;
            AvatarRecord r = GalleryRecord();
            if (r != null && !AvatarLibrary.SameBytes(r.Manifest, target.CurrentManifest)) UseWithGame();
            if (target.Launch()) { ExitCode = 0; Exit(); }
        }

        // ---- the per-avatar menu (Y) -----------------------------------------------------------
        private void OpenCardMenu()
        {
            AvatarRecord r = GalleryRecord();
            if (r == null) return;
            menuItems = new List<string>();
            menuItems.Add("Edit");
            if (target != null) menuItems.Add(UseLabel);
            menuItems.Add("Rename");
            menuItems.Add("Duplicate");
            menuItems.Add("Export");
            menuItems.Add("Delete");
            menuItems.Add("Cancel");
            menuCursor = 0;
            modalBase = State.Gallery;
            state = State.CardMenu;
        }

        private string UseLabel { get { return target == null ? "" : "Use with " + target.Name; } }

        private void UpdateCardMenu()
        {
            if (input.Up && menuCursor > 0) menuCursor--;
            if (input.Down && menuCursor < menuItems.Count - 1) menuCursor++;
            for (int i = 0; i < menuItems.Count; i++)
                if (input.MouseIn(MenuRowRect(i))) { menuCursor = i; if (input.MouseClicked) { ActivateCardMenu(); return; } }
            if (input.Select) { ActivateCardMenu(); return; }
            if (input.Back || input.Y) state = State.Gallery;
        }

        private void ActivateCardMenu()
        {
            string choice = menuItems[menuCursor];
            AvatarRecord r = GalleryRecord();
            state = State.Gallery;
            if (r == null || choice == "Cancel") return;
            if (choice == "Edit") { EditAvatar(r); return; }
            if (choice == UseLabel) { UseWithGame(); return; }
            if (choice == "Rename")
            {
                record = r;
                namePurpose = NamePurpose.Rename;
                nameText = r.Name;
                nameError = null;
                state = State.NameEntry;
                return;
            }
            if (choice == "Duplicate") { Duplicate(r); return; }
            if (choice == "Export") { ExportDialog(r); return; }
            if (choice == "Delete") { AskDelete(r); return; }
        }

        // ---- import and export (AvatarFiles.cs): file dialogs only for a person -------------------

        private void ImportDialog()
        {
            if (input.Suppress) { Console.WriteLine("[studio] import: no file dialog in a scripted run"); return; }
            string[] files = FileDialogs.OpenAvatars();
            AvatarRecord last = null;
            foreach (string f in files) { AvatarRecord r = ImportFile(f); if (r != null) last = r; }
            if (files.Length > 1) Notice("imported " + files.Length + " files");
            if (last != null) SelectInGallery(last.Name);
        }

        /// <summary>One file into the library, or null (with a notice); a profile's avatar asks for a name.</summary>
        private AvatarRecord ImportFile(string path)
        {
            string why;
            ImportedAvatar a = AvatarFiles.Read(path, out why);
            if (a == null) { Notice("could not import " + Path.GetFileName(path) + ": " + why); return null; }
            AvatarRecord same = library.Matching(a.Manifest);
            if (same != null)
            {
                SelectInGallery(same.Name);
                Notice("\"" + same.Name + "\" is already in your library" + ItemsNote(a));
                return null;
            }
            try
            {
                AvatarRecord r = library.Create(library.UniqueName(a.Name), a.Manifest);
                r.ThumbnailPng = a.Png;
                library.Save(r);
                string name = r.Name;
                ReloadLibrary();
                SelectInGallery(name);
                r = library.Find(name);
                Notice("imported \"" + name + "\"" + ItemsNote(a));
                if (a.NameFromProfile && r != null)
                {
                    record = r;
                    namePurpose = NamePurpose.Imported;
                    nameText = r.Name;
                    nameError = null;
                    state = State.NameEntry;
                }
                return r;
            }
            catch (Exception e) { Notice("could not import " + Path.GetFileName(path) + ": " + e.Message); return null; }
        }

        private static string ItemsNote(ImportedAvatar a)
        {
            return a.ItemsWritten == 0 ? "" : " (" + a.ItemsWritten + " item" + (a.ItemsWritten == 1 ? "" : "s") + " it wears added)";
        }

        private void ExportDialog(AvatarRecord r)
        {
            if (input.Suppress) { Console.WriteLine("[studio] export: no file dialog in a scripted run"); return; }
            AvatarFiles.Format format;
            string path = FileDialogs.SaveAvatar(r.Name, out format);
            if (path != null) ExportFile(r, path, format);
        }

        private void ExportFile(AvatarRecord r, string path, AvatarFiles.Format format)
        {
            if (format == AvatarFiles.Format.Png)
            {
                // written once its render is ready (UpdatePicture)
                if (pictures == null) pictures = new ThumbnailCache(GraphicsDevice, PictureSize, PictureSize, true);
                pictureOf = r; picturePath = path; pictureSince = time;
                return;
            }
            try
            {
                AvatarFiles.Write(r, path, format);
                Notice("exported \"" + r.Name + "\" to " + path);
            }
            catch (Exception e) { Notice("could not export: " + e.Message); }
        }

        // [ours] the exported picture: a full-body render at 512 x 512 on a clear background
        private const int PictureSize = 512;
        private ThumbnailCache pictures;
        private AvatarRecord pictureOf;
        private string picturePath;
        private float pictureSince;

        private void UpdatePicture()
        {
            if (pictureOf == null) return;
            pictures.Update();
            Texture2D t = pictures.Get(pictureOf.Manifest, Framing.FullBody);
            if (t == null && !pictures.IsFailed(pictureOf.Manifest, Framing.FullBody) && time - pictureSince < 10f) return;
            byte[] png = t == null ? null : Encode(t);
            if (png == null) Notice("could not export the picture: the avatar did not render");
            else
            {
                try { File.WriteAllBytes(picturePath, png); Notice("exported a picture of \"" + pictureOf.Name + "\" to " + picturePath); }
                catch (Exception e) { Notice("could not export: " + e.Message); }
            }
            pictureOf = null; picturePath = null;
        }

        private void Duplicate(AvatarRecord r)
        {
            try
            {
                AvatarRecord copy = library.Create(library.UniqueName(r.Name), r.Manifest);
                copy.ThumbnailPng = r.ThumbnailPng;
                library.Save(copy);
                string name = copy.Name;
                ReloadLibrary();
                SelectInGallery(name);
            }
            catch (Exception e) { Notice("could not duplicate: " + e.Message); }
        }

        private void AskDelete(AvatarRecord r)
        {
            // [ours] worded as the outfit's question (Strings.xus 13); focus starts on No
            Ask("Are you sure you want to delete \"" + r.Name + "\"?", false, delegate
            {
                try { library.Delete(r); ReloadLibrary(); }
                catch (Exception e) { Notice("could not delete: " + e.Message); }
                EnterGallery();
            }, null);
        }

        // ---- confirmation ----------------------------------------------------------------------
        private void Ask(string text, bool defaultYes, Action yes, Action no)
        {
            confirmText = text; confirmYes = yes; confirmNo = no;
            menuCursor = defaultYes ? 0 : 1;
            modalBase = state;
            state = State.Confirm;
        }

        /// <summary>The dialog's controls, as the editor's: left / right, A presses, B the hidden No; [ours] the mouse.</summary>
        private void UpdateConfirm()
        {
            if (input.Left) menuCursor = 0;
            else if (input.Right) menuCursor = 1;
            for (int i = 0; i < 2; i++)
                if (input.MouseIn(DialogPage.ButtonRect(i)))
                {
                    if (input.MouseMoved || input.MouseClicked) menuCursor = i;
                    if (input.MouseClicked) { PressConfirm(i); return; }
                }
            if (input.Select) { PressConfirm(menuCursor); return; }
            if (input.Back) PressConfirm(2);
        }

        /// <summary>ItemButton(i+1) pressed: its answer once the press has played.</summary>
        private void PressConfirm(int i)
        {
            controls.Press(i, delegate { if (state == State.Confirm) { menuCursor = i; ResolveConfirm(); } });
        }

        /// <summary>The answer: Yes (0) runs confirmYes, No (1) and B (2) confirmNo.</summary>
        private void ResolveConfirm()
        {
            Action a = menuCursor == 0 ? confirmYes : confirmNo;
            state = modalBase;
            if (a != null) a();
        }

        // ================================ NAME ==================================================
        private void UpdateName(float dt)
        {
            foreach (char c in PopTyped())
            {
                if (c == '\b') { if (nameText.Length > 0) { nameText = nameText.Substring(0, nameText.Length - 1); nameError = null; } }
                else if (c == '\r' || c == '\n') { ConfirmName(); return; }
                else if (nameText.Length < AvatarLibrary.MaxNameLength && c >= ' ') { nameText += c; nameError = null; }
            }
            // Select is A or Enter; X is the pad's backspace
            if (input.Select || input.Start) { ConfirmName(); return; }
            if (input.X && nameText.Length > 0) { nameText = nameText.Substring(0, nameText.Length - 1); nameError = null; }
            if (input.Back) EnterGallery();
        }

        private void ConfirmName()
        {
            string name = AvatarLibrary.Clip(nameText);
            AvatarRecord clash = library.Find(name);
            if (clash != null && clash != record)
            {
                nameError = "There is already an avatar called \"" + name + "\".";
                return;
            }
            nameText = name;
            nameError = null;

            if (namePurpose == NamePurpose.Rename || namePurpose == NamePurpose.Imported)
            {
                AvatarRecord r = record;
                if (r != null)
                {
                    bool inGame = target != null && AvatarLibrary.SameBytes(r.Manifest, target.CurrentManifest)
                                  && target.CurrentName != name;
                    r.Name = name;
                    try
                    {
                        library.Save(r);
                        if (inGame) FollowInGame(r);       // the name is the game's profile name too
                        ReloadLibrary(); SelectInGallery(name);
                    }
                    catch (Exception e) { Notice("could not rename: " + e.Message); }
                }
                EnterGallery();
                return;
            }

            // a new avatar with no assets: a random one, saved straight away (what a game does)
            session.Gamertag = name;
            if (!assetsReady)
            {
                try
                {
                    AvatarRecord r = library.Create(name, AvatarStack.Random(AvatarGender.Both, 1)[0].SaveToBinary());
                    library.Save(r);
                    ReloadLibrary();
                    SelectInGallery(name);
                }
                catch (Exception e) { Notice("could not save: " + e.Message); }
                EnterGallery();
                return;
            }
            BuildLineup();
            OpenEditor();                                   // a new avatar's editor opens on the lineup
            state = State.Lineup;
        }

        // the lineup: five candidates and two dummies; the focus moves in a ring, its ends turn the page
        private int lineupPage;            // 0 .. LineupPages-1
        private int LineupIndexForSlot(int slot) { return lineupPage * Layout.LineupVisible + slot; }
        private int LineupFocus { get { return LineupIndexForSlot(lineupIndex); } }

        private void BuildLineup()
        {
            lineup.Clear();
            lineupPage = 0;
            lineupIndex = Layout.LineupVisible / 2;       // AvatarButton3, the scene's DefaultFocus
            EnsureLineup(Layout.LineupVisible * Layout.LineupPages);   // all hundred, as AE_Lineup_Init makes them
            lineupStage.Start(lineup);                    // page 1, in from the right (byte_92312770 = 1)
            lineupChoose = -1f;
            // AE_ChooseNew_Yes_Press: the music fades out, then the footsteps; [ours] the same for a new avatar
            Music.SetMode(Music.ModeQuiet, 0.6f);
            Sounds.Play("Avatars_more_choices.xma");
            lineupMusicAt = -1f;
        }

        /// <summary>The lineup's music (timer 1112, armed when the first button runs on); -1 until armed.</summary>
        private float lineupMusicAt = -1f;

        private void LineupMusicTick()
        {
            if (state != State.Lineup) return;
            if (lineupMusicAt < 0f && lineupStage.AnyRunningOn) lineupMusicAt = time + 1.4f;
            if (lineupMusicAt >= 0f && time >= lineupMusicAt && lineupMusicAt != float.MaxValue)
            {
                Music.SetMode(Music.ModeLineup, 0.1f);
                lineupMusicAt = float.MaxValue;                            // once per lineup (byte_92326C71)
            }
        }

        private float lineupChoose = -1f;    // seconds left of the chosen one's celebration (timer 1113)

        /// <summary>The candidates, made as the editor makes its hundred (AE_Lineup_MakeManifests 0x920F7158).</summary>
        private void EnsureLineup(int count)
        {
            int max = Layout.LineupVisible * Layout.LineupPages;
            while (lineup.Count < Math.Min(count, max))
            {
                int page = lineup.Count / Layout.LineupVisible, slot = lineup.Count % Layout.LineupVisible;
                AvatarGender body = slot < (page % 2 == 0 ? 3 : 2) ? AvatarGender.Male : AvatarGender.Female;
                lineup.Add(AvatarStack.Random(body, 1)[0].SaveToBinary());
            }
        }

        private void UpdateLineup(float dt)
        {
            // A chose: the celebration, then after 2150 ms the avatar is taken
            if (lineupChoose >= 0f)
            {
                lineupChoose -= dt;
                if (lineupChoose < 0f) AcceptLineup();
                return;
            }
            int n = Layout.LineupVisible;
            // sub_920B05B8: left / right only once the focused avatar floats and all stand; the ends turn the page
            bool live = lineupStage.FocusFloating && lineupStage.AllStanding;
            if (live && input.Left) { if (lineupIndex == 0) TurnLineupPage(-1); lineupIndex = (lineupIndex + n - 1) % n; }
            else if (live && input.Right) { if (lineupIndex == n - 1) TurnLineupPage(1); lineupIndex = (lineupIndex + 1) % n; }
            // [ours] the shoulders and a click on a handle turn the page too
            bool paging = lineupStage.AllStanding;
            if (paging && (input.PageLeft || (input.MouseIn(HandleRect(Layout.LineupPrevHandle)) && input.MouseClicked))) TurnLineupPage(-1);
            else if (paging && (input.PageRight || (input.MouseIn(HandleRect(Layout.LineupNextHandle)) && input.MouseClicked))) TurnLineupPage(1);
            // [ours] hovering a slot focuses it; clicking the focused one is A
            for (int s = 0; s < n; s++)
                if (input.MouseIn(LineupSlotRect(s)))
                {
                    if (input.MouseClicked && s == lineupIndex) { ChooseLineup(); return; }
                    if (live && (input.MouseMoved || input.MouseClicked)) lineupIndex = s;
                }

            if (input.Select) { ChooseLineup(); return; }
            // B returns where the lineup was entered from: the name screen, or the editor
            if (input.Back)
            {
                if (record == null) state = State.NameEntry;
                else EnterMainMenu();
            }
        }

        /// <summary>The scroll handles (sub_920B0508 / sub_920B0560): the page wraps, 20 back to 1.</summary>
        private void TurnLineupPage(int delta)
        {
            int n = Layout.LineupPages;
            int next = ((lineupPage + delta) % n + n) % n;
            if (!lineupStage.Turn(delta > 0, next)) return;
            lineupPage = next;
            lineupTurns++;
            // AE_Lineup_NextPage 0x920B0558: sfxFootstepsMore (8); AE_Lineup_PrevPage 0x920B05B0: sfxFootsteps (7)
            Sounds.Play(delta > 0 ? "Avatars_more_choices.xma" : "Avatars_run_in.xma");
        }
        private int lineupTurns, lineupChosen;   // counted for --fuzz-lineup's report

        /// <summary>A on the lineup (sub_920B0010): only on a floating avatar.</summary>
        private void ChooseLineup()
        {
            if (lineupStage.Choose())
            {
                lineupChoose = LineupStage.ChooseSeconds; lineupChosen++;
                // AE_LineupButton_Choose 0x920B00A0: the editing music (mode 2, 0.8 s) and sfxBeam (6)
                Music.SetMode(Music.ModeEditing, 0.8f);
                Sounds.Play("Transition_select_to_editor.xma");
            }
        }
        private static Rectangle LineupSlotRect(int slot)
        {
            return new Rectangle(Layout.LineupSlotX[slot], Layout.LineupSlotY[slot], Layout.LineupSlotW, Layout.LineupSlotH);
        }
        private static Rectangle HandleRect(Vector2 centre)
        {
            int k = Layout.LineupHandleSize;
            return new Rectangle((int)centre.X - k / 2, (int)centre.Y - k / 2, k, k);
        }

        private void AcceptLineup()
        {
            EnsureLineup(LineupFocus + 1);
            session.Replace(AvatarManifest.Create(lineup[LineupFocus]));
            EnterMainMenu();
        }

        private void EnterMainMenu()
        {
            state = State.MainMenu; root = null; node = null; navStack.Clear();
            cursor = 0; page = 0;
            // the main menu's record stands the avatar left of the tiles
            stage.Screen = EditorCamera.MainMenu;
            RefreshStage();
        }

        private void UpdateMainMenu()
        {
            int cols = 4;
            MoveGridCursor(MainTiles.Length, cols);
            int hovered;
            if (MouseOverTile(MainTiles.Length, Layout.MainTilesOrigin, cols, out hovered)) { cursor = hovered; if (input.MouseClicked) { PressFocused(ActivateMain); return; } }
            if (input.Select) { PressFocused(ActivateMain); return; }
            // B on the main menu saves and exits, as the Save and Exit tile does (sub_920C59E0)
            if (input.Back) BackPressed(SaveAndClose);
        }

        private void ActivateMain()
        {
            if (cursor < 0 || cursor >= MainTiles.Length) return;
            switch (MainTiles[cursor])
            {
                case "Change My Features": root = Tree.Features(); node = root; EnterGrid(); break;
                case "Change My Style": root = Tree.Style(); node = root; EnterGrid(); break;
                case "Choose New Avatar": OpenChooseNew(); break;
                case "Save and Exit": SaveAndClose(); break;
                // the store is gone; the editor's own message for it (Strings.xus 34)
                case "Marketplace": OpenMessage(MarketplaceText); break;
                // not built yet: Awards and Gamer Picture (the console's scenes and art exist for both)
                case "Awards": OpenMessage(AwardsText); break;
                case "Gamer Picture": OpenMessage(GamerPictureText); break;
            }
        }

        // ---- Choose New Avatar?, state 81 (DialogPage.cs): Yes to the lineup, No and B back -----
        private int chooseNewFocus;                                       // 0 Yes, 1 No
        private static readonly string[] ChooseNewArt = { "option_dialog_yes", "option_dialog_no" };   // 0x9200AB8C, 0x92009B3C
        private const string ChooseNewText = "Are you sure you want to choose a new avatar?\r\nThis will delete your existing one.";   // Strings.xus 14

        private void OpenChooseNew()
        {
            state = State.ChooseNew;
            chooseNewFocus = 0;
            stage.Screen = EditorCamera.ChooseNew;
            stage.Request(session.Body == AvatarGender.Female ? 40 : 50, false);   // e1 +4, ShockedSurprised
        }

        private void UpdateChooseNew()
        {
            if (input.Left) chooseNewFocus = 0;
            else if (input.Right) chooseNewFocus = 1;
            for (int i = 0; i < 2; i++)
                if (input.MouseIn(DialogPage.ButtonRect(i)))
                {
                    if (input.MouseMoved || input.MouseClicked) chooseNewFocus = i;
                    if (input.MouseClicked) { PressChooseNewButton(i); return; }
                }
            if (input.Select) { PressChooseNewButton(chooseNewFocus); return; }
            if (input.Back) PressChooseNewButton(2);                      // ItemButton3, PressKey 22593
        }

        /// <summary>ItemButton(i+1) pressed: entry i acts when the press range ends; B waits for the hidden one's.</summary>
        private void PressChooseNewButton(int i)
        {
            controls.Press(i, delegate { if (state == State.ChooseNew) PressChooseNew(i); });
        }

        /// <summary>Entry i's press: Yes the lineup, No and B back to the main menu.</summary>
        private void PressChooseNew(int entry)
        {
            if (entry == 0) { BuildLineup(); state = State.Lineup; return; }
            state = State.MainMenu;                                       // the menu's cursor as it was
            stage.Screen = EditorCamera.MainMenu;
        }

        private void DrawChooseNew()
        {
            DialogPage.Draw(ui, tileArt, ChooseNewText, ChooseNewArt, chooseNewFocus, new[] { controls[0], controls[1] });
            ui.Legend(EditorLegend(EditorCamera.ChooseNew));
        }

        // ---- a message page: what the studio cannot do yet, and the store's ------------------------
        // [ours] the dialog's page with one OK; A, a click or B closes it on the same tile.
        private string messageText = "";
        private const string MarketplaceText = "Sorry, the Xbox Avatar Store isn't available right now. Please check back later.";   // Strings.xus 34
        private const string AwardsText = "Awards aren't in Avatar Studio yet.";
        private const string GamerPictureText = "Gamer Picture isn't in Avatar Studio yet. For a picture of your avatar, press Y on its card in your avatars, then choose Export.";

        /// <summary>Every dialog page text wrapped as drawn, in the console's face and the fallback: all must fit 640 x 120.</summary>
        private void LogDialogWrap()
        {
            string[] texts = { ChooseNewText, DeleteOutfitText, MarketplaceText, AwardsText, GamerPictureText };
            RuntimeFont fallback = new RuntimeFont(GraphicsDevice, null, "Segoe UI", ConsoleFace.PixelSize(18f), false, 2);
            try
            {
                RuntimeFont[] faces = { ui.Xui(18f, 0x1404), fallback };
                string[] names = { ui.Xui(18f, 0x1404).IsConsoleFace ? "the console's face" : "the fallback (no console face installed)", "the fallback" };
                for (int f = 0; f < faces.Length; f++)
                {
                    int widest = 0, most = 0;
                    bool fits = true;
                    foreach (string t in texts)
                    {
                        string[] lines = faces[f].WrapXui(t, 640f).Split('\n');
                        foreach (string l in lines) widest = Math.Max(widest, faces[f].XuiWidth(l));
                        most = Math.Max(most, lines.Length);
                        fits &= lines.Length * (int)Math.Ceiling(faces[f].LineHeight) <= 120;
                    }
                    fits &= widest <= 640;
                    Console.WriteLine("[studio] selftest: dialog texts in " + names[f] + ": widest line " + widest + " of 640, at most " + most
                        + " line(s) of " + (int)Math.Ceiling(faces[f].LineHeight) + " in 120: " + (fits ? "all fit" : "DO NOT FIT"));
                }
            }
            finally { fallback.Dispose(); }
        }

        private void OpenMessage(string text)
        {
            messageText = text;
            Console.WriteLine("[studio] " + text);
            state = State.Message;
            stage.Screen = EditorCamera.ChooseNew;                       // the dialog's camera, record 81
        }

        private void UpdateMessage()
        {
            if (input.MouseIn(DialogPage.OkRect()) && input.MouseClicked) { PressMessage(0); return; }
            if (input.Select) { PressMessage(0); return; }
            if (input.Back) PressMessage(1);
        }

        private void PressMessage(int i)
        {
            controls.Press(i, delegate
            {
                if (state != State.Message) return;
                state = State.MainMenu;                                   // the menu's cursor as it was
                stage.Screen = EditorCamera.MainMenu;
            });
        }

        private void DrawMessage()
        {
            DialogPage.DrawMessage(ui, tileArt, messageText, ChooseNewArt[0], controls[0]);
            ui.Legend("A OK", "B Back");                                  // OK: Strings.xus 194
        }

        private void EnterGrid()
        {
            state = State.Grid; cursor = 0; page = 0;
            GridStage();
        }

        /// <summary>The grid's own screen record, also on the way back to a grid.</summary>
        private void GridStage()
        {
            stage.Screen = ScreenFor(node);
        }

        /// <summary>A node's editor screen id, as the console's menu builders give each tile.</summary>
        private static int ScreenFor(Node n)
        {
            if (n == null) return -1;
            if (n.Kind == NodeKind.Color)
            {
                switch (n.Color)
                {
                    case DynamicColorType.Skin: return EditorCamera.SkinColor;
                    case DynamicColorType.Hair: return EditorCamera.HairColor;
                    case DynamicColorType.Mouth: return EditorCamera.LipColor;
                    case DynamicColorType.FacialHair: return EditorCamera.FacialHairColor;
                    case DynamicColorType.Iris: return EditorCamera.EyeColor;
                    case DynamicColorType.Eyebrow: return EditorCamera.EyebrowColor;
                    case DynamicColorType.EyeShadow: return EditorCamera.EyeShadowColor;
                    case DynamicColorType.SkinFeatures1: return EditorCamera.FacialFeatureColor;
                }
                return -1;
            }
            switch (n.Name)
            {
                case "My Features": return EditorCamera.Features;
                case "My Style": return EditorCamera.Style;
                case "Eyes and Eyebrows": return EditorCamera.EyesAndEyebrows;
                case "Face": return EditorCamera.Face;
                case "Mouth and Chin": return EditorCamera.MouthAndChin;
                case "Color": return EditorCamera.Color;
                case "Accessories": return EditorCamera.Accessories;
                case "Hairstyles": return EditorCamera.Hair;
                case "Eyes": return EditorCamera.Eyes;
                case "Eyebrows": return EditorCamera.Eyebrows;
                case "Facial Hair": return EditorCamera.FacialHair;
                case "Facial Features": return EditorCamera.FacialFeatures;
                case "Body Size": return EditorCamera.BodySize;
                case "Ears": return EditorCamera.Ears;
                case "Nose": return EditorCamera.Nose;
                case "Mouth": return EditorCamera.Mouth;
                case "Chin": return EditorCamera.Chin;
                case "Tops": return EditorCamera.Tops;
                case "Headwear": return EditorCamera.Headwear;
                case "Glasses": return EditorCamera.Glasses;
                case "Wristwear": return EditorCamera.Wristwear;
                case "Gloves": return EditorCamera.Gloves;
                case "Rings": return EditorCamera.Rings;
                case "Earrings": return EditorCamera.Earrings;
                case "Props": return EditorCamera.Props;
                case "Bottoms": return EditorCamera.Bottoms;
                case "Shoes": return EditorCamera.Shoes;
                case "Dress Up": return EditorCamera.DressUp;
                case "My Outfits": return EditorCamera.Outfits;
            }
            return -1;
        }

        private void UpdateGrid()
        {
            List<Node> kids = node.Children;
            int cols = 4;
            cursor = ClampGridCursor(cursor);
            MoveGridCursor(kids.Count, cols);
            int hovered;
            if (MouseOverTile(kids.Count, Layout.GridTilesOrigin, cols, out hovered)) { cursor = hovered; if (input.MouseClicked) PressFocused(ActivateGrid); }
            if (input.Select) PressFocused(ActivateGrid);
            if (input.Back) BackPressed();
        }

        /// <summary>A press on the focused control: its action runs when the press range ends.</summary>
        private void PressFocused(Action activate)
        {
            int i = cursor;
            State s = state;
            controls.Press(i, delegate { if (state != s) return; cursor = i; activate(); });
        }

        private void ActivateGrid()
        {
            if (node == null || node.Children.Count == 0) return;
            cursor = ClampGridCursor(cursor);
            Node child = node.Children[cursor];
            followOnFrom = null;
            if (!ColorEntryEnabled(child))
            {
                // a disabled entry refuses the press (sub_920CDDC8) and plays PressDisable
                refusedIndex = cursor; refusedAt = time;
                return;
            }
            // Hairstyles in the bonus window: the hair takes the first of the nine (sub_920E22C8)
            if (stage.Screen == EditorCamera.Features && child.Kind == NodeKind.Items && child.Category == ComponentCategories.Hair
                && bonus.Window && !BonusColours.Contains(session.Color(DynamicColorType.Hair)))
            {
                session.PreviewColor(DynamicColorType.Hair, BonusColours.At(0));
                session.Commit();
                bonusDyed++;
            }
            switch (child.Kind)
            {
                case NodeKind.Submenu: navStack.Push(node); node = child; cursor = 0; GridStage(); break;
                case NodeKind.Items: activeLeaf = child; EnterItems(); break;
                case NodeKind.Color: activeLeaf = child; EnterColors(); break;
                case NodeKind.BodySize: activeLeaf = child; state = State.BodySize; stage.Screen = ScreenFor(child); InitSizeSteps(); break;
                case NodeKind.Outfits: activeLeaf = child; EnterOutfits(); break;
            }
        }

        // ---- the Color submenu's enabled tests ----------------------------------------------------
        private int refusedIndex = -1;
        private float refusedAt = -10f;

        /// <summary>Whether a Color submenu entry is enabled, re-tested on every fetch (sub_920E9F60).</summary>
        private bool ColorEntryEnabled(Node n)
        {
            if (n.ColorMenuIndex < 0) return true;
            var m = session.Committed;
            switch (n.ColorMenuIndex)
            {
                case 4: case 7: return true;
                case 5:
                    n.Enabled = m.GetReplacementTexture(DynamicTextureType.FacialHair).TextureAssetId != Guid.Empty;
                    return n.Enabled;
            }
            if (!AvatarToc.Loaded) return n.Enabled;          // sub_92216938: the table is not ready
            Guid g;
            bool hasNoneRecord = false;
            switch (n.ColorMenuIndex)
            {
                case 0: g = m.GetComponentInfo(AvatarComponentMasks.Hair, AssetMatchingMode.Exact).AssetId;
                        if (g == Guid.Empty) return n.Enabled;           // sub_9213BB00 finds no hair
                        break;
                case 1: g = m.GetReplacementTexture(DynamicTextureType.SkinFeatures).TextureAssetId; hasNoneRecord = true; break;
                case 2: g = m.GetReplacementTexture(DynamicTextureType.Eyebrow).TextureAssetId; break;
                case 3: g = m.GetReplacementTexture(DynamicTextureType.Mouth).TextureAssetId; break;
                default: g = m.GetReplacementTexture(DynamicTextureType.Eye).TextureAssetId; break;
            }
            if (g == Guid.Empty)
            {
                if (hasNoneRecord) n.Enabled = false;
                return n.Enabled;
            }
            TocEntry t = AvatarToc.Find(g);
            if (t != null) n.Enabled = t.Colourable;
            return n.Enabled;
        }

        /// <summary>`cursor` is shared by every screen: clamp it to this grid.</summary>
        private int ClampGridCursor(int c)
        {
            int n = node == null ? 0 : node.Children.Count;
            return n == 0 ? 0 : Math.Max(0, Math.Min(c, n - 1));
        }

        /// <summary>Leave the screen; `pick` is a pick's pop, which goes on popping past screens that skip.</summary>
        private const string BackSound = "general_button_press.xma";

        /// <summary>B on an editor screen: the back handle's press runs AE_Back, which pops and plays BackSound.</summary>
        private void BackPressed() { BackPressed(delegate { Sounds.Play(BackSound); GoBack(); }); }

        /// <summary>B reaches EditorMain.xur's hidden back button; its press range plays, then `act` runs.</summary>
        private void BackPressed(Action act)
        {
            if (backAt >= 0f) return;
            backAt = time + 19f / 60f;
            backAction = act;
            backState = state;
        }
        private float backAt = -1f;
        private Action backAction;
        private State backState;

        private void BackTick()
        {
            if (backAt < 0f || time < backAt) return;
            backAt = -1f;
            Action a = backAction;
            backAction = null;
            if (a != null && state == backState) a();
        }

        private void GoBack(bool pick = false)
        {
            int play = pick ? pendingExamine : 0;
            pendingExamine = 0;
            if (!pick && followOnFrom != null && (state == State.Colors || state == State.ColourSets))
                ReturnToItems();
            else
                PopScreen();
            if (play != 0) stage.Request(play, false);           // sub_920C2508(controller, id, 0, 0)
        }

        /// <summary>B out of a follow-on: the item screen again, on its page and cell.</summary>
        private void ReturnToItems()
        {
            session.Revert();
            RefreshStage();
            activeLeaf = followOnFrom;
            followOnFrom = null;
            setsItem = null; sets = null;
            state = State.Items;
            stage.Screen = ScreenFor(activeLeaf);
            page = itemsPage;
            RebuildPage();
            cursor = Math.Max(0, Math.Min(itemsCursor, pageItems.Count + RemoveSlots - 1));
        }

        private void PopScreen()
        {
            // [ours] back on a grid, the highlight returns to the tile that was opened
            if (state == State.Items || state == State.Colors || state == State.ColourSets || state == State.BodySize || state == State.Outfits)
            {
                // leaving a follow-on colour screen lands on the item's grid, on the item
                if ((state == State.Colors || state == State.ColourSets) && followOnFrom != null) { activeLeaf = followOnFrom; }
                followOnFrom = null;
                setsItem = null; sets = null;
                session.Revert();
                RefreshStage();
                state = State.Grid;
                cursor = ClampGridCursor(activeLeaf == null ? 0 : node.Children.IndexOf(activeLeaf));
                GridStage();
                return;
            }
            if (navStack.Count > 0)
            {
                Node from = node;
                node = navStack.Pop();
                cursor = ClampGridCursor(node.Children.IndexOf(from));
                state = State.Grid;
                GridStage();
                return;
            }
            EnterMainMenu();
        }

        // ---- item picker ----------------------------------------------------------------------
        private void EnterItems()
        {
            state = State.Items; cursor = 0; page = 0;
            // the category's own camera record; the thumbnails keep the studio's framings
            stage.Screen = ScreenFor(activeLeaf);
            FocusFirstChosenItem();
            RebuildPage();
        }

        /// <summary>A screen opens on its first chosen cell, else its first (sub_920DB5C0 / sub_920DAFC0).</summary>
        private void FocusFirstChosenItem()
        {
            AvatarGender body = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
            List<CatalogItem> all = catalog.For(activeLeaf.Category, body, onDiskOnly: true);
            int slots = HasRemoveCell ? 1 : 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!session.Committed.IsAssetPresent(all[i].Id)) continue;
                int e = i + slots;                               // the remove cell is entry 0
                page = e / Layout.CellsPerPage;
                cursor = e % Layout.CellsPerPage;
                return;
            }
        }

        /// <summary>Whether the category has a remove cell: the ten RemovableComponents categories.</summary>
        private bool HasRemoveCell
        {
            get { AvatarManifestEditor.RemovableComponents k; return activeLeaf != null && RemovableFor(activeLeaf.Category, out k); }
        }

        /// <summary>1 on the page with the remove cell (entry 0 of the list, so page 0), else 0.</summary>
        private int RemoveSlots { get { return HasRemoveCell && page == 0 ? 1 : 0; } }

        private int ItemPages(int count)
        {
            int entries = count + (HasRemoveCell ? 1 : 0);
            return Math.Max(1, (entries + Layout.CellsPerPage - 1) / Layout.CellsPerPage);
        }

        /// <summary>The list index of page p's first item (the remove cell takes entry 0).</summary>
        private int ItemStart(int p) { return Math.Max(0, p * Layout.CellsPerPage - (HasRemoveCell ? 1 : 0)); }

        private void RebuildPage()
        {
            AvatarGender body = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
            List<CatalogItem> all = catalog.For(activeLeaf.Category, body, onDiskOnly: true);
            itemPages = ItemPages(all.Count);
            page = (int)MathHelper.Clamp(page, 0, itemPages - 1);
            int start = ItemStart(page);
            int n = Math.Max(0, Math.Min(Layout.CellsPerPage - RemoveSlots, all.Count - start));
            pageItems = all.GetRange(Math.Min(start, all.Count), n);
        }

        private int itemPages = 1;                     // the item screen's page count (RebuildPage)

        private void UpdateItems()
        {
            int total = pageItems.Count + RemoveSlots;
            int turn = MoveLinear(total);
            // hover
            for (int i = 0; i < total; i++)
                if (input.MouseIn(CellRect(i))) { if (cursor != i) { cursor = i; PreviewCursorItem(); } if (input.MouseClicked) { PressFocused(CommitItem); } }
            if (input.Left || input.Right || input.Up || input.Down) PreviewCursorItem();
            // a turned page takes focus at its first cell (Grid.xur's DefaultFocus, ItemButton1)
            if ((input.PageLeft || turn < 0) && page > 0) { page--; RebuildPage(); cursor = 0; PreviewCursorItem(); }
            else if ((input.PageRight || turn > 0) && page + 1 < itemPages) { page++; RebuildPage(); cursor = 0; PreviewCursorItem(); }
            if (input.MouseIn(Layout.PagePrevButton) && input.MouseClicked && page > 0) { page--; RebuildPage(); cursor = 0; }
            if (input.MouseIn(Layout.PageNextButton) && input.MouseClicked && page + 1 < itemPages) { page++; RebuildPage(); cursor = 0; }
            if (input.Select) PressFocused(CommitItem);
            if (input.Back) BackPressed();
        }

        private void PreviewCursorItem()
        {
            if (cursor < RemoveSlots) { PreviewRemoveCurrent(); return; }
            int i = cursor - RemoveSlots;
            if (i < 0 || i >= pageItems.Count) return;
            PreviewItemCell(pageItems[i]);
            // Earrings, Wristwear and Rings: an item cell takes the screen's own record once the item is on
            int s = stage.Screen;
            if ((s == EditorCamera.Earrings || s == EditorCamera.Wristwear || s == EditorCamera.Rings) && session.Wears(pageItems[i].Id))
                stage.FocusItem(pageItems[i].Id, s == EditorCamera.Rings);
        }

        /// <summary>An item pick keeps the item and leaves: to its colour screen when colourable, else back (sub_920DBCA0).</summary>
        private void CommitItem()
        {
            PreviewCursorItem();
            CatalogItem it = CursorItem();
            if (it != null && !session.Committed.IsAssetPresent(it.Id) && pressRng.Next(100) < 50)
                pendingExamine = ColourSets.ExamineFor(it.Id);
            session.Commit();
            ColourSetList list;
            Node colour = FollowOnColour(out list);
            if (colour != null || list != null)
            {
                itemsPage = page; itemsCursor = cursor;
                followOnFrom = activeLeaf;
                pendingExamine = 0;                              // the push clears it
                if (list != null) { EnterColourSets(it, list); return; }
                activeLeaf = colour;
                EnterColors();
                return;
            }
            GoBack(true);
        }

        private CatalogItem CursorItem()
        {
            int i = cursor - RemoveSlots;
            return cursor < RemoveSlots || i < 0 || i >= pageItems.Count ? null : pageItems[i];
        }

        /// <summary>An item cell's preview: the asset with its cell's colours (set 0 or the worn set).</summary>
        private void PreviewItemCell(CatalogItem it)
        {
            uint[][] s = ColourSets.TocSets(it);
            int k = ColourSets.ItemCellSet(s, session.Committed, it.Id);
            if (k < 0) session.PreviewItem(it.Id);
            else session.PreviewItemColors(it.Id, ColourSets.ToColors(s[k]));
        }

        private Node followOnFrom;       // the item node a follow-on colour screen was opened from

        /// <summary>The follow-on of a pick: a colour screen, or a garment's colour sets; [ours] back without a table.</summary>
        private Node FollowOnColour(out ColourSetList list)
        {
            list = null;
            CatalogItem it = CursorItem();
            if (it == null) return null;
            TocEntry t = it.Toc;
            if (t == null || !t.Colourable) return null;
            if (activeLeaf.ColorForItems == DynamicColorType.Count)
            {
                list = ColourSets.For(it, catalog.AssetDir);
                return null;
            }
            return ColourNode(root, activeLeaf.ColorForItems);
        }

        /// <summary>The Color submenu's entry for a colour (the follow-on screens are those states).</summary>
        private static Node ColourNode(Node n, DynamicColorType c)
        {
            if (n == null) return null;
            if (n.Kind == NodeKind.Color && n.ColorMenuIndex >= 0 && n.Color == c) return n;
            foreach (Node k in n.Children) { Node hit = ColourNode(k, c); if (hit != null) return hit; }
            return null;
        }

        private void PreviewRemoveCurrent()
        {
            AvatarManifestEditor.RemovableComponents kind;
            if (RemovableFor(activeLeaf.Category, out kind)) session.PreviewRemove(kind);
        }

        // ---- colour picker --------------------------------------------------------------------
        private void EnterColors()
        {
            state = State.Colors; cursor = 0; page = 0;
            palette = Palettes.For(activeLeaf.Color);
            // Hair Color (56): the nine first while they are unlocked (sub_920DA028, BonusColours.cs)
            if (activeLeaf.Color == DynamicColorType.Hair) palette = bonus.HairList(palette);
            // The colour's own record (EditorCamera.cs, ids 55-63).
            stage.Screen = ScreenFor(activeLeaf);
            // the first chosen chip takes the focus (sub_920DAFC0)
            for (int e = 0; e < ColorEntries; e++)
                if (ChipChosen(e)) { page = e / Layout.ChipsPerPage; cursor = e % Layout.ChipsPerPage; break; }
        }

        /// <summary>Whether colour entry e is applied: the chip equal to the avatar's colour (sub_920DD498).</summary>
        private bool ChipChosen(int e)
        {
            bool eyeShadow = activeLeaf.Color == DynamicColorType.EyeShadow;
            bool off = eyeShadow && session.EquippedTexture(DynamicTextureType.EyeShadow) == Guid.Empty;
            if (e < ColorRemoveSlots) return off;
            int idx = e - ColorRemoveSlots;
            if (off || idx < 0 || idx >= palette.Length) return false;
            Colorb applied = session.Color(activeLeaf.Color), cb = palette[idx];
            return cb.red == applied.red && cb.green == applied.green && cb.blue == applied.blue;
        }

        // ---- the garments' colour-set screen, states 65 / 66 (ColourSets.cs): Grid2x3, six a page --

        private void EnterColourSets(CatalogItem it, ColourSetList list)
        {
            state = State.ColourSets;
            setsItem = it; sets = list;
            page = 0; cursor = 0;
            for (int s = 0; s < sets.Sets.Length; s++)
                if (SetChosen(s)) { page = s / Layout.ChipsPerPage; cursor = s % Layout.ChipsPerPage; break; }
            Console.WriteLine("[studio] colour sets for " + it.Name + ": " + sets.Sets.Length + " from the " + sets.Source + ", " + sets.Shown + " shown a cell");
        }

        /// <summary>sub_920DF4A8: the set equal to the item's worn colours; set 0 when none is.</summary>
        private bool SetChosen(int s)
        {
            uint[] worn = ColourSets.Worn(session.Committed, setsItem.Id);
            if (ColourSets.Same(sets.Sets[s], worn)) return true;
            if (s != 0) return false;
            foreach (uint[] other in sets.Sets) if (ColourSets.Same(other, worn)) return false;
            return true;
        }

        private void UpdateColourSets()
        {
            int total = sets.Sets.Length;
            int baseIdx = page * Layout.ChipsPerPage;
            int onPage = Math.Max(0, Math.Min(Layout.ChipsPerPage, total - baseIdx));
            int turn = MoveColumns(onPage);
            for (int i = 0; i < onPage; i++)
                if (input.MouseIn(ChipRect(i))) { if (cursor != i) { cursor = i; PreviewCursorSet(); } if (input.MouseClicked) { PressFocused(CommitColourSet); return; } }
            if (input.Left || input.Right || input.Up || input.Down) PreviewCursorSet();
            if ((input.PageLeft || turn < 0) && page > 0) { page--; cursor = 0; PreviewCursorSet(); }
            else if ((input.PageRight || turn > 0) && (page + 1) * Layout.ChipsPerPage < total) { page++; cursor = 0; PreviewCursorSet(); }
            if (input.Select) { PressFocused(CommitColourSet); return; }
            if (input.Back) BackPressed();
        }

        /// <summary>Focus puts the item on with the set (sub_920DF098).</summary>
        private void PreviewCursorSet()
        {
            int s = page * Layout.ChipsPerPage + cursor;
            if (s < 0 || s >= sets.Sets.Length) return;
            session.PreviewItemColors(setsItem.Id, ColourSets.ToColors(sets.Sets[s]));
        }

        /// <summary>The cell's press (sub_920DEE00): applied, the examine on a coin flip, then the pops.</summary>
        private void CommitColourSet()
        {
            int s = page * Layout.ChipsPerPage + cursor;
            if (s < 0 || s >= sets.Sets.Length) return;
            bool chosen = SetChosen(s);
            PreviewCursorSet();
            if (!chosen && pressRng.Next(100) < 50) pendingExamine = ColourSets.ExamineFor(setsItem.Id);
            session.Commit();
            GoBack(true);
        }

        /// <summary>1 when this colour screen also takes the feature off (Eye Shadow): its first cell.</summary>
        private int ColorRemoveSlots
        {
            get { return activeLeaf != null && activeLeaf.Remove != AvatarManifestEditor.RemovableComponents.NotRemovable ? 1 : 0; }
        }
        private int ColorEntries { get { return palette.Length + ColorRemoveSlots; } }

        private void UpdateColors()
        {
            int total = ColorEntries;
            int baseIdx = page * Layout.ChipsPerPage;
            int onPage = Math.Max(0, Math.Min(Layout.ChipsPerPage, total - baseIdx));
            int turn = MoveColumns(onPage);
            for (int i = 0; i < onPage; i++)
                if (input.MouseIn(ChipRect(i))) { if (cursor != i) { cursor = i; PreviewCursorColor(); } if (input.MouseClicked) { PressFocused(CommitColor); return; } }
            if (input.Left || input.Right || input.Up || input.Down) PreviewCursorColor();
            if ((input.PageLeft || turn < 0) && page > 0) { page--; cursor = 0; PreviewCursorColor(); }
            else if ((input.PageRight || turn > 0) && (page + 1) * Layout.ChipsPerPage < total) { page++; cursor = 0; PreviewCursorColor(); }
            if (input.Select) { PressFocused(CommitColor); return; }
            if (input.Back) BackPressed();
        }

        /// <summary>A colour pick applies the colour and pops (sub_920DD1E0, sub_920DCF90).</summary>
        private void CommitColor()
        {
            PreviewCursorColor();
            session.Commit();
            GoBack(true);
        }

        private void PreviewCursorColor()
        {
            int e = page * Layout.ChipsPerPage + cursor;
            bool eyeShadow = activeLeaf.Color == DynamicColorType.EyeShadow;   // its own cells, EditSession
            if (e < ColorRemoveSlots)
            {
                if (eyeShadow) session.PreviewEyeShadowRemove(); else session.PreviewRemove(activeLeaf.Remove);
                return;
            }
            int idx = e - ColorRemoveSlots;
            if (idx < 0 || idx >= palette.Length) return;
            if (eyeShadow) session.PreviewEyeShadowColor(palette[idx]); else session.PreviewColor(activeLeaf.Color, palette[idx]);
            Music.Activity();                               // AE_ColourCell_Apply 0x920DD03C raises the activity flag
        }

        // ---- body size, state 5: HeightAndWidth.xur, two rails and an accept ------------------
        private int heightStep = 2, widthStep = 2;
        private static readonly float[] SizeSteps = { -1f, -0.5f, 0f, 0.5f, 1f };

        /// <summary>The axes' starting indices from the manifest (AE_SizeEntry_IndexFromManifest).</summary>
        private void InitSizeSteps()
        {
            byte[] m = session.CommittedBytes;
            heightStep = SizeIndex(ManifestFloat(m, 8));
            widthStep = SizeIndex(ManifestFloat(m, 4));
        }

        private static int SizeIndex(float v)
        {
            for (int i = 0; i < SizeSteps.Length; i++) if (v == SizeSteps[i]) return i;
            return 0;
        }

        private static float ManifestFloat(byte[] m, int at)
        {
            if (m == null || m.Length < at + 4) return 0f;
            byte[] b = { m[at], m[at + 1], m[at + 2], m[at + 3] };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }

        private void UpdateBodySize(float dt)
        {
            if (input.Up) SizeStep(ref heightStep, true, "Height");
            if (input.Down) SizeStep(ref heightStep, false, "Height");
            if (input.Right) SizeStep(ref widthStep, true, "Width");
            if (input.Left) SizeStep(ref widthStep, false, "Width");
            BodySizeMouse();
            if (input.Select || (input.MouseClicked && input.MouseIn(Layout.SceneRect(172.227f, 248.4f, 120f, 120f))))
                controls.Press(0, delegate { if (state == State.BodySize) { session.Commit(); GoBack(); } });
            if (input.Back) BackPressed();
        }
        private void ApplySize() { session.PreviewSize(SizeSteps[heightStep], SizeSteps[widthStep]); }

        /// <summary>A step (0x920D32A8): sfx<Axis>Increase / Decrease with the new index, or sfxFail.</summary>
        private void SizeStep(ref int index, bool up, string axis)
        {
            int was = index;
            if (up && index < SizeSteps.Length - 1) index++;
            else if (!up && index > 0) index--;
            if (index == was) { Sounds.Play("general_button_press_back.xma"); return; }
            ApplySize();
            Music.Activity();                               // AE_SizeEntry_Apply 0x920DCC40
            int n = up ? index : index + 1;                                    // 1..4
            Sounds.Play(axis.ToLowerInvariant() + (up ? "_increase_0" : "_decrease_0") + n + ".xma");
        }

        // ================================ SAVE =================================================
        // The editor saves into the library, never into a game: a game takes an avatar from the gallery.
        // Save and Exit: the Saving box (2 s at least), the celebration, the fade, back to the gallery.

        private void SaveAndClose()
        {
            Sounds.Play(BackSound);                         // AE_Back from state 2 (B, and the tile's press 0x920DDC90)
            session.Commit();
            SaveStudioSettings();                           // AE_Back from state 2 writes the profile's settings (sub_920F6FD0)
            state = State.Saving;
            stage.Screen = EditorCamera.Saving;
            exitTimer = 2f;                                 // +620, 0x92005AE0
            saveDone = false; saveWait = 0f; boxTime = 0f;
        }

        private void UpdateSaving(float dt)
        {
            boxTime += dt;
            exitTimer -= dt;
            if (!saveDone) saveDone = SaveStep(dt);
            if (saveDone && exitTimer < 0f)
            {
                if (selftest) Console.WriteLine("[studio] selftest: the Saving box shown " + boxTime.ToString("0.00") + " s (2 s at least)");
                state = State.Exiting;
                stage.Screen = EditorCamera.Exit;
                exitTimer = 5f;                             // 0x92006C34
                stage.React(EditorAnimations.Celebration);
                Music.SetMode(Music.ModeExit, 0.5f);        // AE_BeginExit 0x920C390C: the Outro
                UpdateExit(dt);
            }
        }

        private void UpdateExit(float dt)
        {
            exitTimer -= dt;
            if (exitTimer < 0f)
            {
                if (selftest) Console.WriteLine("[studio] selftest: exit done, fade " + fade.ToString("0.00") + " on its last frame");
                EnterGallery();
            }
        }

        /// <summary>One frame of the studio's save; true once it is written (or failed).</summary>
        private bool SaveStep(float dt)
        {
            saveWait += dt;
            byte[] manifest = session.CommittedBytes;
            // [ours] opened and left as it was: not rewritten (a visit is not an edit)
            if (record != null && record.ThumbnailPng != null
                && AvatarLibrary.SameBytes(record.Manifest, AvatarLibrary.ToManifest(manifest))
                && record.Name == AvatarLibrary.Clip(session.Gamertag))
            {
                Console.WriteLine("[studio] \"" + record.Name + "\" unchanged; not rewritten");
                SelectInGallery(record.Name);
                return true;
            }
            // the game's avatar before this save (FollowInGame)
            bool inGame = record != null && target != null && AvatarLibrary.SameBytes(record.Manifest, target.CurrentManifest);
            byte[] png = null;
            if (assetsReady)
            {
                Texture2D t = cards.Get(manifest, Framing.FullBody);
                if (t == null && saveWait < 3f && !cards.IsFailed(manifest, Framing.FullBody)) return false;
                png = t == null ? null : Encode(t);
            }
            try
            {
                if (record == null) record = library.Create(session.Gamertag, manifest);
                else { record.Name = AvatarLibrary.Clip(session.Gamertag); record.Manifest = AvatarLibrary.ToManifest(manifest); }
                if (png != null) record.ThumbnailPng = png;
                library.Save(record);
                outfits.Attach(record.Path);                    // a new avatar's outfits go beside its file
                if (inGame) FollowInGame(record);
                string name = record.Name;
                ReloadLibrary();
                SelectInGallery(name);
                Notice("saved \"" + name + "\" to " + AvatarLibrary.Directory);
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] save failed: " + e.Message);
                Notice("save failed: " + e.Message);
            }
            return true;
        }

        // ---- closing the window ([ours]) -------------------------------------------------------
        // Closing keeps the open avatar as Save and Exit would: the console's editor saves whenever it is left.
        // OnExiting runs once FNA's loop ends, the device still up; never in a scripted run.
        private bool closeSaves;

        protected override void OnExiting(object sender, EventArgs args)
        {
            if (closeSaves)
            {
                try { Console.WriteLine("[studio] closing: " + SaveOnClose()); }
                catch (Exception e) { Console.WriteLine("[studio] closing: " + e.Message); }
            }
            base.OnExiting(sender, args);
        }

        /// <summary>The open avatar written as Save and Exit writes it: the picks (not a preview), its outfits;
        /// no picture wait. Nothing from the lineup, the gallery or the name screen.</summary>
        private string SaveOnClose()
        {
            switch (state)
            {
                case State.MainMenu: case State.ChooseNew: case State.Message: case State.Grid: case State.Items:
                case State.Colors: case State.ColourSets: case State.BodySize: case State.Outfits:
                case State.DeleteOutfit: case State.OutfitBusy: case State.OutfitTutorial:
                    break;
                case State.Saving:
                    if (saveDone) return "already saved";
                    break;
                default:
                    return "no avatar open (" + state + ")";
            }
            byte[] manifest = session.CommittedBytes;
            string name = AvatarLibrary.Clip(session.Gamertag);
            byte[] look = AvatarLibrary.ToManifest(manifest);
            bool changed = record == null || !AvatarLibrary.SameBytes(record.Manifest, look);
            if (!changed && record.Name == name) return "\"" + record.Name + "\" unchanged; not rewritten";
            bool inGame = record != null && target != null && AvatarLibrary.SameBytes(record.Manifest, target.CurrentManifest);
            Texture2D t = assetsReady ? cards.Get(manifest, Framing.FullBody) : null;   // only one already drawn
            byte[] png = t == null ? null : Encode(t);
            SaveStudioSettings();
            if (record == null) record = library.Create(session.Gamertag, manifest);
            else { record.Name = name; record.Manifest = look; }
            if (png != null) record.ThumbnailPng = png;
            else if (changed) record.ThumbnailPng = null;                // the old picture would show it as it was
            library.Save(record);
            outfits.Attach(record.Path);                                 // a new avatar's outfits go beside its file
            if (inGame) FollowInGame(record);
            return "saved \"" + record.Name + "\" to " + record.Path;
        }

        private static byte[] Encode(Texture2D t)
        {
            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    t.SaveAsPng(ms, t.Width, t.Height);
                    return ms.ToArray();
                }
            }
            catch (Exception e) { Console.WriteLine("[studio] thumbnail encode: " + e.Message); return null; }
        }

        // ================================ DRAW =================================================
        protected override void Draw(GameTime gt)
        {
            // 0) the avatar library's shadow pass and the ghost cells, before the scene's target
            if (ShowsStage) stage.BeginFrame();
            if (state == State.Lineup) lineupStage.BeginFrame();
            ui.BeginLegend();               // this frame's screens set it (Ui.Legend); none: the buttons go
            ghosts.Prepare(GhostSpecs());
            // the editor page's two layers, each in its own target (PageTransitions.cs)
            if (IsPageState(state)) RenderPageLayers();

            // 1) render the UI at the fixed 1280x720 design size
            GraphicsDevice.SetRenderTarget(scene);
            GraphicsDevice.Clear(Color.Black);
            float aspect = Layout.W / (float)Layout.H;

            // EditorBG's scnDynamicBG; the gallery gets the field without the floor
            backdrop.Draw(ui.Batch, !IsGalleryScreen);

            // the My Style mirror: part of EditorBG, the background scene, so before the avatar
            if (ShowsStage && stage.Screen >= 0) mirror.Draw(tileArt, stage);

            // EditorBG's scnRainbowBlur: on the Color menu and the colour screens (sub_920C1F30)
            if (ShowsRainbow)
            {
                Texture2D rainbow = tileArt.Named("rainbow_blur");
                if (rainbow != null)
                {
                    ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);   // TileArt premultiplies
                    ui.Batch.Draw(rainbow, new Vector2(41.9679f, 4.54079f), null, Color.White * 0.8f, 0f, Vector2.Zero, 3f, SpriteEffects.None, 0f);
                    ui.Batch.End();
                }
            }

            // avatar in world space between backdrop and UI
            if (ShowsStage)
            {
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
                stage.Draw(aspect);
            }
            // the lineup's seven: EditorStart's scnAvatar, before its buttons (the beams) and text
            if (state == State.Lineup)
            {
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.BlendState = BlendState.Opaque;
                GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
                lineupStage.Draw(aspect);
            }

            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            switch (state)
            {
                case State.Gallery: DrawGallery(); ui.DrawLegend(); break;
                case State.CardMenu: DrawBehindModal(); DrawCardMenu(); ui.DrawLegend(); break;
                case State.Confirm: DrawBehindModal(); DrawConfirm(); ui.DrawLegend(); break;
                case State.NameEntry: DrawName(); ui.DrawLegend(); break;
                case State.Setup: DrawSetup(); ui.DrawLegend(); break;
                case State.Lineup: DrawLineup(); ui.DrawLegend(); break;
                default:
                    // an editor page: the old scene under the new one, then the legend
                    CompositePage();
                    ui.DrawLegend();
                    if (state == State.OutfitBusy && outfitNaming) DrawOutfitName();    // the system keyboard's stand-in, over everything
                    break;
            }
            // [ours] the notice strip below the legend
            string strip = time - noticeAt < NoticeSeconds ? notice : statusLine;
            if (strip.Length > 0 && state != State.Saving && state != State.Exiting && state != State.Setup)   // the setup says it itself
                ui.Text(XuiStyle.PageNumber, strip, 20f, 692f, 1240f, 26f, Palette.TextGrey);
            // scnFade: over everything, #FF0F0F0F at its opacity (sub_920CB0F0)
            if (fade > 0f) ui.Batch.Draw(ui.Pixel, new Rectangle(0, 0, Layout.W, Layout.H), new Color(0x0F, 0x0F, 0x0F) * fade);
            ui.Batch.End();

            // 2) the display gamma ramp on the design-size frame, then the letterboxed blit
            RenderTarget2D shown = gamma.Apply(scene);
            GraphicsDevice.SetRenderTarget(null);
            GraphicsDevice.Clear(Color.Black);
            Rectangle dst = Letterbox();
            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullCounterClockwise);
            ui.Batch.Draw(shown, dst, Color.White);
            ui.Batch.End();
            if (selftest && gamma.On && gammaCheckIn > 0 && --gammaCheckIn == 0) CheckGamma(shown, dst);

            if (pendingShot != null) { SaveShot(pendingShot); pendingShot = null; }
            base.Draw(gt);
        }

        /// <summary>Every ramped pixel is the ramp of the frame's, and the window shows it once.</summary>
        private void CheckGamma(RenderTarget2D shown, Rectangle dst)
        {
            Color[] a = new Color[Layout.W * Layout.H], b = new Color[Layout.W * Layout.H];
            scene.GetData(a);
            shown.GetData(b);
            int bad = 0;
            for (int i = 0; i < a.Length; i++)
                if (b[i].R != gamma.Map[a[i].R] || b[i].G != gamma.Map[a[i].G] || b[i].B != gamma.Map[a[i].B]) bad++;
            string window = "window not 1:1, not compared";
            PresentationParameters pp = GraphicsDevice.PresentationParameters;
            if (dst == new Rectangle(0, 0, Layout.W, Layout.H) && pp.BackBufferWidth == Layout.W && pp.BackBufferHeight == Layout.H)
            {
                Color[] c = new Color[Layout.W * Layout.H];
                GraphicsDevice.GetBackBufferData(c);
                int same = 0;
                for (int i = 0; i < c.Length; i++) if (c[i].R == b[i].R && c[i].G == b[i].G && c[i].B == b[i].B) same++;
                window = "window " + same + " of " + c.Length + " pixels equal to it";
            }
            Console.WriteLine("[studio] selftest: display gamma ramp, " + (a.Length - bad) + " of " + a.Length +
                              " pixels exact (level 128 -> " + gamma.Map[128] + "); " + window);
        }

        private void SaveShot(string path)
        {
            try
            {
                Color[] px = new Color[Layout.W * Layout.H];
                scene.GetData(px);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                using (Texture2D t = new Texture2D(GraphicsDevice, Layout.W, Layout.H))
                using (FileStream fs = File.Create(path))
                {
                    t.SetData(px);
                    t.SaveAsPng(fs, Layout.W, Layout.H);
                }
                Console.WriteLine("[studio] shot: " + Path.GetFullPath(path));
            }
            catch (Exception e) { Console.WriteLine("[studio] shot failed: " + e.Message); }
        }

        private Rectangle Letterbox()
        {
            int cw = Window.ClientBounds.Width, ch = Window.ClientBounds.Height;
            float target = Layout.W / (float)Layout.H, have = cw / (float)ch;
            int w, h;
            if (have > target) { h = ch; w = (int)(ch * target); } else { w = cw; h = (int)(cw / target); }
            return new Rectangle((cw - w) / 2, (ch - h) / 2, w, h);
        }

        /// <summary>The gallery, its modals and the name screen: no floor, no avatar ([ours]).</summary>
        private bool IsGalleryScreen
        {
            get
            {
                return state == State.Gallery || state == State.CardMenu || state == State.NameEntry || state == State.Setup
                    || (state == State.Confirm && modalBase == State.Gallery);
            }
        }

        /// <summary>LT / RT turn the avatar (sub_920C40D0): a screen that takes them, no dialog, no page change.</summary>
        private bool TriggersSpin { get { return ScreenTakesTriggers && !pageChanging; } }

        /// <summary>AE_TriggersSpin's state test alone.</summary>
        private bool ScreenTakesTriggers
        {
            get
            {
                switch (state)
                {
                    case State.MainMenu: case State.Grid:
                    case State.Items: case State.Colors: case State.ColourSets: case State.BodySize: case State.Outfits:
                        return EditorCamera.TriggersSpin(stage.Screen);
                    // overrides: AE_TriggersSpin refuses them
                    default:
                        return false;
                }
            }
        }

        /// <summary>The focused tile's centre on screen, for the look timer (sub_920C9AE8).</summary>
        private Vector2? FocusedTileCentre()
        {
            switch (state)
            {
                case State.MainMenu: case State.Grid:
                    if (cursor < 0 || cursor > 7) return null;
                    float y = cursor == 0 ? 66.77f : cursor < 4 ? 66.7675f : 323.968f;
                    return Layout.Scene(280f + 175f * (cursor % 4) + 85f, y + 252.733f / 2f);
                case State.Items:
                    if (cursor < 0 || cursor >= Layout.CellsPerPage) return null;
                    return Layout.Scene(129f + 631f * (cursor % 2) + 60f, 80f + 120f * (cursor / 2) + 60f);
                case State.Colors: case State.ColourSets:
                    if (cursor < 0 || cursor >= Layout.ChipsPerPage) return null;
                    return Layout.Scene(129f + 631f * (cursor / Layout.ChipRows) + 60f, 139f + 120f * (cursor % Layout.ChipRows) + 60f);
                case State.ChooseNew:
                    return Layout.Scene((chooseNewFocus == 0 ? 115f : 773f) + 64f, 260f + 64f);
                case State.Message:
                    return Layout.Scene(DialogPage.OkButton.X + 64f, DialogPage.OkButton.Y + 64f);
                case State.DeleteOutfit:
                    return Layout.Scene((deleteOutfitFocus == 0 ? 115f : 773f) + 64f, 260f + 64f);
                case State.Outfits:
                {
                    if (cursor < 0 || cursor >= OutfitsOnPage) return null;
                    Vector2 s = OutfitTileScene(cursor);                    // Grid2x2Tall's 180 x 230
                    return Layout.Scene(s.X + 90f, s.Y + 115f);
                }
                case State.BodySize:
                    return Layout.Scene(172.227f + 60f, 248.4f + 60f);
                case State.OutfitTutorial:
                {
                    // the tutorial's ItemButton1, 128 x 128 at (215.021, 368.394), through scnGrid
                    Vector2 c = TutorialPoint(new Vector2(215.021f + 64f, 368.394f + 64f));
                    return Layout.Scene(c.X, c.Y);
                }
            }
            return null;
        }

        /// <summary>sub_920C1F30's test: the Color menu, the colour screens and the colour sets.</summary>
        private bool ShowsRainbow
        {
            get
            {
                return (state == State.Grid && stage.Screen == EditorCamera.Color) ||
                       state == State.Colors || state == State.ColourSets;
            }
        }

        private bool ShowsStage
        {
            get
            {
                switch (state)
                {
                    // Not Lineup: it draws its five candidates itself, in the scene's own slots.
                    case State.MainMenu: case State.ChooseNew: case State.Message: case State.Grid:
                    case State.Items: case State.Colors: case State.ColourSets: case State.BodySize: case State.Outfits:
                    case State.DeleteOutfit: case State.OutfitBusy: case State.OutfitTutorial:      // overrides: scnAvatar draws on
                    case State.Saving: case State.Exiting:
                        return true;
                    case State.Confirm:
                        return modalBase != State.Gallery;
                    default:
                        return false;
                }
            }
        }

        /// <summary>The screen a modal sits on, drawn without its button legend (the modal has its own).</summary>
        private void DrawBehindModal()
        {
            ui.LegendEnabled = false;
            if (modalBase == State.Gallery) DrawGallery(); else DrawMainMenu();
            ui.LegendEnabled = true;
        }

        private void Notice(string s)
        {
            notice = s; noticeAt = time;
            Console.WriteLine("[studio] " + s);
        }

        // ---- gallery: the studio's own screens, [ours] places, the editor's text styles ---------
        private void DrawGallery()
        {
            // the title: category_header at the pages' header height
            ui.Text(XuiStyle.CategoryHeader, "Avatar Studio", Layout.GalleryTitlePos.X, Layout.GalleryTitlePos.Y,
                    Layout.CategoryW * Ui.OwnScale, Layout.CategoryH * Ui.OwnScale);

            int n = library.Records.Count;
            string sub = n == 0 ? "your library is empty" : n + (n == 1 ? " avatar" : " avatars");
            if (target != null) sub += "     ·     " + target.Name + ": " + target.Status;
            // the count and the game: category_page_number, in its label's height
            float ph = Layout.PageNumberH * Ui.OwnScale;
            ui.Text(XuiStyle.PageNumber, sub, Layout.GallerySubtitlePos.X, Layout.GallerySubtitlePos.Y, 820f, ph);

            int count = GalleryCount;
            int page = galleryCursor / Layout.CardsPerPage;
            // [ours] page numbers top right, clear of the legend
            if (GalleryPages > 1)
                ui.Text(XuiStyle.PageNumber.Across(2), "Page " + (page + 1) + " of " + GalleryPages + (Input.KeysShown ? "    PgUp / PgDn" : "    LB / RB"),
                        Layout.GalleryPageTextPos.X - 300f, Layout.GalleryPageTextPos.Y, 300f, ph);

            byte[] inGame = target == null ? null : target.CurrentManifest;
            for (int slot = 0; slot < Layout.CardsPerPage; slot++)
            {
                int i = page * Layout.CardsPerPage + slot;
                if (i >= count) break;
                Rectangle cell = CardRect(slot);
                bool sel = i == galleryCursor;

                // [ours] a ring, not a size change: a card's name plate should not jump
                if (sel)
                {
                    int k = Layout.CardRing;
                    ui.Rounded(new Rectangle(cell.X - k, cell.Y - k, cell.Width + 2 * k, cell.Height + 2 * k), Color.White);
                }
                ui.Rounded(cell, sel ? Palette.SelectGreen : Palette.TileLavender);

                int inset = Layout.CardInset;
                Rectangle plate = new Rectangle(cell.X, cell.Bottom - Layout.CardPlateH, cell.Width, Layout.CardPlateH);
                Rectangle art = new Rectangle(cell.X + inset, cell.Y + inset,
                                              cell.Width - 2 * inset, cell.Height - Layout.CardPlateH - 2 * inset);

                if (i == 0)
                {
                    ui.Rect(new Rectangle(art.Center.X - 32, art.Center.Y - 5, 64, 10), Palette.Text);
                    ui.Rect(new Rectangle(art.Center.X - 5, art.Center.Y - 32, 10, 64), Palette.Text);
                    ui.Rect(plate, Palette.TileLabelPlate);
                    ui.Text(XuiStyle.TileLabel, "New Avatar", plate);                // a tile's label
                    continue;
                }

                AvatarRecord r = library.Records[i - 1];
                ui.Rect(art, Palette.CardArt);          // the same panel under every card, selected or not
                DrawCardArt(r, art);
                if (inGame != null && AvatarLibrary.SameBytes(r.Manifest, inGame)) DrawInGamePill(art);
                ui.Rect(plate, Palette.TileLabelPlate);
                // the name as a tile's label; the date under it in the small style
                ui.Text(XuiStyle.TileLabel, Fit(XuiStyle.TileLabel, r.Name, plate.Width - 16), plate.X, plate.Y + 1, plate.Width, 28);
                ui.Text(XuiStyle.SmallText, Edited(r), plate.X, plate.Y + 26, plate.Width, 22, Palette.CardSubtext);
            }

            // DialogTextLeftAlign, beside the New Avatar card
            if (n == 0)
                ui.Text(XuiStyle.DialogTextLeftAlign, "No avatars yet. Choose New Avatar to make one, or press X to import one: "
                        + "an avatar.bin, a .avatar file or an Xbox 360 profile.", Layout.GalleryEmptyPos.X, Layout.GalleryEmptyPos.Y, 620f, 200f);

            bool onRecord = GalleryRecord() != null;
            List<string> legend = new List<string>();
            legend.Add(onRecord ? "A Edit" : "A New Avatar");
            if (!onRecord) legend.Add("X Import");
            if (onRecord && target != null) legend.Add("X Use in " + target.ShortName);
            legend.Add(onRecord ? "Y Options" : "Y Setup");
            legend.Add("B Exit");
            ui.Legend(legend.ToArray());
            // the small style, right-aligned ([ours]) on the legend's line
            if (onRecord && target != null && allowLaunch && target.CanLaunch)
                ui.Text(XuiStyle.SmallText.Across(2), "Start / F5    Play " + target.ShortName, 784f, 648f, 400f, 32f);
        }

        /// <summary>A green flag on the card the game will use.</summary>
        private void DrawInGamePill(Rectangle art)
        {
            const string text = "In game";
            int w = (int)ui.TextWidth(XuiStyle.SmallText, text) + 22;
            Rectangle pill = new Rectangle(art.Right - 8 - w, art.Y + 8, w, 26);
            ui.Rounded(pill, Palette.XboxGreen);
            ui.Text(XuiStyle.SmallText, text, pill, Color.White);
        }

        private static string Edited(AvatarRecord r)
        {
            DateTime d = r.Modified.ToLocalTime();
            DateTime today = DateTime.Now.Date;
            if (d.Date == today) return "edited today";
            if (d.Date == today.AddDays(-1)) return "edited yesterday";
            return "edited " + d.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Trim to a card's plate. Names are capped at 15 characters, but fonts vary.</summary>
        private string Fit(XuiStyle s, string text, float width)
        {
            if (ui.TextWidth(s, text) <= width) return text;
            for (int i = text.Length - 1; i > 1; i--)
            {
                string t = text.Substring(0, i) + "...";
                if (ui.TextWidth(s, t) <= width) return t;
            }
            return text;
        }

        /// <summary>The card's picture: a live render through the console's renderer, the stored PNG until ready.</summary>
        private void DrawCardArt(AvatarRecord r, Rectangle art)
        {
            int side = Math.Min(art.Width, art.Height);
            Rectangle box = new Rectangle(art.Center.X - side / 2, art.Bottom - side, side, side);
            Texture2D t;
            if (cards.UsesLibrary) t = cards.Get(r.Manifest, Framing.FullBody) ?? StoredThumb(r);
            else t = StoredThumb(r) ?? cards.Get(r.Manifest, Framing.FullBody);
            if (t != null) ui.Batch.Draw(t, box, Color.White);
            else if (!assetsReady) ui.TextAt(XuiStyle.SmallText, "no preview", new Vector2(art.Center.X, art.Center.Y), Palette.TextGrey);
            else ui.Spinner(new Vector2(art.Center.X, art.Center.Y), 14, time, Palette.TextDim);
        }

        private Texture2D StoredThumb(AvatarRecord r)
        {
            if (r.ThumbnailPng == null) return null;
            string k = r.Path ?? r.Name;
            Texture2D t;
            if (cardThumbs.TryGetValue(k, out t)) return t;
            try { using (MemoryStream ms = new MemoryStream(r.ThumbnailPng)) t = Texture2D.FromStream(GraphicsDevice, ms); }
            catch (Exception e) { Console.WriteLine("[studio] stored thumbnail: " + e.Message); t = null; }
            cardThumbs[k] = t;
            return t;
        }

        private void DrawCardMenu()
        {
            AvatarRecord r = GalleryRecord();
            Rectangle m = Layout.CardMenu;
            ui.Rect(new Rectangle(0, 0, Layout.W, Layout.H), Color.Black * 0.5f);
            ui.Rounded(m, Palette.Panel);
            // the avatar's name in DialogTextLeftAlign; the rows as tiles' labels, left-aligned ([ours])
            ui.Text(XuiStyle.DialogTextLeftAlign, r == null ? "" : r.Name, m.X + 20, m.Y + 16, m.Width - 40, 36);
            for (int i = 0; i < menuItems.Count; i++)
            {
                Rectangle row = MenuRowRect(i);
                if (i == menuCursor) ui.Rounded(row, Palette.SelectGreen);
                ui.Text(XuiStyle.TileLabel.Across(0), menuItems[i], row.X + 14, row.Y, row.Width - 28, row.Height);
            }
            ui.Legend("A Select", "B Back");
        }

        /// <summary>The delete question: the editor's dialog over the dimmed gallery, no floor shadows ([ours]).</summary>
        private void DrawConfirm()
        {
            ui.Rect(new Rectangle(0, 0, Layout.W, Layout.H), Color.Black * 0.5f);
            DialogPage.Draw(ui, tileArt, confirmText, ChooseNewArt, menuCursor, new[] { controls[0], controls[1] }, false);
            ui.Legend("A Select", "B Back");
        }

        // ---- name: [ours] a panel, as the outfit's name ------------------------------------------
        private void DrawName()
        {
            bool rename = namePurpose == NamePurpose.Rename;
            string about = namePurpose == NamePurpose.Imported
                ? "An Xbox profile keeps its gamertag locked away, so type the name this avatar goes by. It is the profile name a game shows, up to "
                  + AvatarLibrary.MaxNameLength + " characters."
                : "This name goes with the avatar wherever it is used, and is the profile name a game shows. It can contain up to "
                  + AvatarLibrary.MaxNameLength + " characters.";
            DrawNamePanel(rename ? "Rename Avatar" : "Name Your Avatar", about, nameText, nameError, "type a name, then press Enter or A");
            ui.Legend("A Select", "B Back", "X Backspace");
        }

        /// <summary>The name panel (the avatar's, the outfit's): its title, what it is about, the field
        /// with the text and the caret, an error, the hint.</summary>
        private void DrawNamePanel(string title, string about, string text, string error, string hint)
        {
            Rectangle p = Layout.NamePanel, f = Layout.NameField;
            ui.Rounded(p, Palette.Panel);
            ui.Text(XuiStyle.CategoryHeader, title, p.X + 32, p.Y + 14, Layout.CategoryW * Ui.OwnScale, Layout.CategoryH * Ui.OwnScale);
            ui.Text(XuiStyle.DialogTextLeftAlign, about, p.X + 32, p.Y + 78, p.Width - 64, 120f);
            ui.Rect(f, Color.White);
            ui.Text(XuiStyle.TileLabel.Across(0), text, f.X + 10, f.Y, f.Width - 20, f.Height, Color.Black);
            if (((int)(time * 2)) % 2 == 0)
            {
                float cx = f.X + 10 + ui.TextWidth(XuiStyle.TileLabel, text) + 1;
                ui.Rect(new Rectangle((int)cx, f.Y + 7, 2, 26), Color.Black);
            }
            if (error != null)
                ui.TextAt(XuiStyle.SmallText, error, new Vector2(640, f.Bottom + 14), Palette.GlyphB);
            ui.TextAt(XuiStyle.SmallText, hint, new Vector2(640, f.Bottom + 40));
        }

        // ---- lineup: the buttons (the focused one's beam), then the text --------------------------
        private void DrawLineup()
        {
            if (lineupStage.BeamVisible)
            {
                ui.Batch.End();
                ui.Batch.Begin(SpriteSortMode.Deferred, LineupBeam.Additive);
                LineupBeam.Draw(ui.Batch, tileArt, lineupIndex, lineupStage.BeamTime);
                ui.Batch.End();
                ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            }
            // [xur] EditorStart.xur's XuiText1: 816 x 104 at (231.976, 55.73), 24 pt, 0x1401
            ui.Xui(24f).DrawXui(ui.Batch, "Choose an avatar as a starting point", new Vector2(231.976f, 55.73f), 1f, 816f, 104f, 0x1401,
                                Palette.Argb(0xEBEBEBEB), Palette.DropShadow);

            // the scroll handles show once every avatar stands; the pages wrap
            if (lineupStage.AllStanding)
            {
                if (lineupHandlesSince < 0f) lineupHandlesSince = time;
                Chrome.LineupHandles(ui, tileArt, time - lineupHandlesSince);
            }
            else lineupHandlesSince = -1f;
            // [xur] groupIndex: 640 x 54 at (320, 625), 15 pt, 0x1411
            ui.Xui(15f).DrawXui(ui.Batch, "Page " + (lineupPage + 1) + " of " + Layout.LineupPages, new Vector2(320f, 625f), 1f, 640f, 54f, 0x1411,
                                Palette.SceneText, Palette.DropShadow);
            ui.Legend(EditorLegend(3));
        }

        /// <summary>AE_Legend_Update (0x920B5EC0): A, B, X and Y for the state on top (Strings.xus ids).</summary>
        private static string[] EditorLegend(int s, bool outfitDeletable = false)
        {
            if (s < 0) return new[] { "A Select", "B Back" };   // [ours] a studio screen with no console id
            if (s <= 1 || (s >= 76 && s <= 78)) return new string[0];
            if (s == 2 || s == 3 || (s >= 91 && s <= 94)) return new[] { "A Select" };
            if (s == 31) return outfitDeletable ? new[] { "A Select", "B Back", "X Delete Outfit" } : new[] { "A Select", "B Back" };
            if (s == 50) return new[] { "A Confirm Preview", "B Back", "X Basic Mode" };
            if (s == 52) return new[] { "A Select", "B Back", "X Advanced Mode" };
            if (s == 54) return new[] { "A Select" };
            if (s == 89 || s == 90) return new[] { "B Back" };
            return new[] { "A Select", "B Back" };
        }

        private float lineupHandlesSince = -1f;   // when the lineup's scroll handles last appeared

        // ---- main menu -------------------------------------------------------------------------
        private void DrawMainMenu()
        {
            // [xur] GridMain's Category name shows the gamertag on the main menu (0x920D21E0)
            string gamertag = session.Gamertag;
            // laid out as XUI does: 24 pt, 0x1111, shadowed
            Gated(true, delegate(float o, float age) { CategoryHeader(gamertag, Layout.GridCategoryAt, o); });
            DrawTileScreenChrome();
            int cols = 4;
            // the focused tile grows over its neighbours, so it is drawn last
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < MainTiles.Length; i++)
                    if ((i == cursor) == (pass == 1))
                        DrawTile(TileRect(i, Layout.MainTilesOrigin, cols), MainTiles[i], MainTiles[i], i == cursor, false, -1f, i);
            ui.Legend(EditorLegend(EditorCamera.MainMenu));   // the 2010 footage agrees: A alone
        }

        private void DrawGrid()
        {
            string title = node.Name;
            Gated(true, delegate(float o, float age) { CategoryHeader(title, Layout.GridCategoryAt, o); });   // GridMain's `Category name`, in `items` (category_header: shadowed)
            DrawTileScreenChrome();
            int cols = 4;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if ((i == cursor) != (pass == 1)) continue;
                    Node c = node.Children[i];
                    string label = c.Name;
                    if (c.Name == "Face" && session.Body == AvatarGender.Female) label = "Facial Features";
                    float pressT = i == refusedIndex ? time - refusedAt : -1f;
                    DrawTile(TileRect(i, Layout.GridTilesOrigin, cols), c.Art ?? c.Name, label, i == cursor,
                             !ColorEntryEnabled(c), pressT, i);
                }
            ui.Legend(EditorLegend(stage.Screen));
        }

        /// <summary>GridMain.xur's furniture: the shadows under the tile columns and the turntable prompt.</summary>
        private void DrawTileScreenChrome()
        {
            foreach (float x in Layout.TileShadowSceneX) Chrome.FloorShadow(ui, tileArt, x, Layout.TileShadowSceneW);
            if (!ScreenTakesTriggers) return;
            bool lt = input.TriggerLeft != 0, rt = input.TriggerRight != 0;
            Gated(true, delegate(float o, float age) { Chrome.RotateHandle(ui, tileArt, Chrome.GridHost, lt, rt, o); });
        }

        /// <summary>The pickers' furniture: the column shadows, the turntable prompt and the page handles.</summary>
        private void DrawPickerChrome(int pages, int entries, bool columnMajor)
        {
            bool sl, sr;
            ShadowSides(entries, columnMajor, out sl, out sr);
            if (sl) Chrome.FloorShadow(ui, tileArt, Layout.CellShadowSceneX[0], Layout.CellShadowSceneW);     // shadowLeft
            if (sr) Chrome.FloorShadow(ui, tileArt, Layout.CellShadowSceneX[1], Layout.CellShadowSceneW);     // shadowRight
            // RotateHandleContainer is scnGrid's own child, not `items`'s: it stays put in a transition
            if (ScreenTakesTriggers)
            {
                bool lt = input.TriggerLeft != 0, rt = input.TriggerRight != 0;
                Gated(false, delegate(float o, float age) { Chrome.RotateHandle(ui, tileArt, Chrome.PickerHost, lt, rt); });
            }
            // the handles are in `items`; each scene's own Normal runs from its appearance
            bool prev = page > 0, next = page < pages - 1;
            Gated(true, delegate(float o, float age) { Chrome.PageHandles(ui, tileArt, prev, next, age, o); });
        }

        /// <summary>`artKey` is the node's name (TileArt's key); `label` is what the screen shows ("Face" differs per body).</summary>
        private const float DisabledOpacity = 0.35f, DisabledFocusOpacity = 0.5f;
        private const float PressDisableSeconds = (307 - 289) / 60f;

        private static float PressDisableShake(float t)
        {
            float f = t * 60f / 3f;                    // in steps of three frames from frame 289
            int step = (int)Math.Floor(f);
            float frac = f - step;
            return 10f * ((step & 1) == 0 ? frac : 1f - frac);
        }

        private void DrawTile(Rectangle r, string artKey, string label, bool selected, bool disabled = false, float pressT = -1f, int index = -1)
        {
            XuiPlayer player = index >= 0 ? controls[index] : null;
            if (player != null && DrawTileVisual(player, index, artKey, label)) return;
            // [xur] GridTopLevel's Focus settles at Scale 1.25 about the button's centre; a disabled tile's stays 1
            Rectangle rr = r;
            if (selected && !disabled)
            {
                int w = (int)Math.Round(r.Width * 1.25f), h = (int)Math.Round(r.Height * 1.25f);
                rr = new Rectangle(r.Center.X - w / 2, r.Center.Y - h / 2, w, h);
            }
            bool pressing = disabled && selected && pressT >= 0f && pressT < PressDisableSeconds;
            if (pressing) rr.Offset((int)Math.Round(PressDisableShake(pressT)), 0);
            float alpha = !disabled ? 1f : selected ? DisabledFocusOpacity : DisabledOpacity;

            // the label as GridTopLevel's presenter sets it (XuiStyle.TileLabel), in the plate
            string text = ui.Xui(17f).Wrap(label, (rr.Width - 16) / Ui.OwnScale, 2);
            bool female = session.Body == AvatarGender.Female;
            // the picture carries its own caption strip, so it is the tile; unfocused, its _unselected art
            Texture2D art = pressing ? tileArt.PressDisable(artKey, female) ?? tileArt.For(artKey, female, true)
                                     : tileArt.For(artKey, female, selected);
            bool standIn = false;
            if (art == null && !selected)
            {
                art = tileArt.For(artKey, female, true);
                standIn = art != null;
            }
            Rectangle plate;
            if (art != null)
            {
                ui.Batch.Draw(art, rr, Color.White * ((standIn ? 0.72f : 1f) * alpha));
                int strip = (int)(rr.Height * TileArt.CaptionFraction);
                plate = new Rectangle(rr.X, rr.Bottom - strip, rr.Width, strip);
            }
            else
            {
                ui.Rounded(rr, (selected ? Palette.SelectGreen : Palette.TileLavender) * alpha);
                // the label plate fits however many lines the label wrapped to
                int ph = 14 + (text.IndexOf('\n') >= 0 ? 2 : 1) * (int)ui.LineHeight(XuiStyle.TileLabel);
                plate = new Rectangle(rr.X, rr.Bottom - ph, rr.Width, ph);
                ui.Rect(plate, Palette.TileLabelPlate * 0.9f);
            }
            ui.Text(XuiStyle.TileLabel, text, plate.X + 8, plate.Y, plate.Width - 16, plate.Height);
        }

        /// <summary>A GridMain tile through its visual, GridTopLevel, as XUI plays it; false without the art.</summary>
        private bool DrawTileVisual(XuiPlayer p, int i, string artKey, string label)
        {
            bool female = session.Body == AvatarGender.Female;
            Texture2D unsel = tileArt.For(artKey, female, false), sel = tileArt.For(artKey, female, true);
            if (unsel == null || sel == null) return false;
            if (p.Pressed && !p.Enabled) sel = tileArt.PressDisable(artKey, female) ?? sel;
            Matrix ctrl = Matrix.CreateTranslation(280f + 175f * (i % 4), i == 0 ? 66.77f : i < 4 ? 66.7675f : 323.968f, 0f) * XuiDraw.SceneToScreen;
            if (i == 0) ctrl = BonusSpin() * ctrl;                // ItemButton1's wrapper scene (BonusColours.cs)
            FitElem(p, ctrl, "unselected", unsel);
            FitElem(p, ctrl, "selected", sel);
            Matrix m; float o;
            int ti = p.Def.Index("XuiTextPresenter1");
            if (ti >= 0 && p.Global(ti, out m, out o) && o > 0f)
            {
                // [xur] GridTopLevel's XuiTextPresenter1: 17 pt, 0x1400, #FFEBEBEB
                XuiElementDef td = p.Def.Elements[ti];
                RuntimeFont f = ui.Xui(17f);
                string text = f.Wrap(label, td.Width, 4);
                XuiDraw.Begin(ui, m * ctrl);
                f.DrawXui(ui.Batch, text, Vector2.Zero, 1f, td.Width, td.Height, 0x1400, Palette.SceneText * o, Palette.DropShadow);
                XuiDraw.End(ui);
            }
            return true;
        }

        /// <summary>GridMain.xur's ItemButton1 wrapper playing BonusColors: the tile's spin (frames 511..550).</summary>
        private Matrix BonusSpin()
        {
            if (bonusSpinAt < 0f) return Matrix.Identity;
            float f = BonusFrames[0] + (time - bonusSpinAt) * 60f;      // XUI timelines: 60 frames a second
            int k = 0;
            while (k < BonusFrames.Length - 2 && f >= BonusFrames[k + 1]) k++;
            if (f >= BonusFrames[BonusFrames.Length - 1]) return Matrix.Identity;
            float u = (f - BonusFrames[k]) / (BonusFrames[k + 1] - BonusFrames[k]);
            Quaternion q = Quaternion.Slerp(BonusKeys[k], BonusKeys[k + 1], u);
            return Matrix.CreateTranslation(-85f, -126.367f, 0f) * Matrix.CreateFromQuaternion(q) * Matrix.CreateTranslation(85f, 126.367f, 0f);
        }

        // [xur] GridMain.xur, scnGrid / items / ItemButton1 (the wrapper) / ItemButton1: Rotation
        private static readonly int[] BonusFrames = { 511, 517, 523, 530, 536, 543, 550 };
        private static readonly Quaternion[] BonusKeys =
        {
            new Quaternion(0f, 0f, 0f, -1f), new Quaternion(0f, 0f, -0.866021f, -0.499986f), new Quaternion(0f, 0f, 0.866021f, -0.499986f),
            new Quaternion(0f, 0f, 0f, -1f), new Quaternion(0f, 0f, -0.866023f, -0.499992f), new Quaternion(0f, 0f, 0.866023f, -0.499994f),
            new Quaternion(0f, 0f, 0f, -1f),
        };

        // ---- item picker -----------------------------------------------------------------------
        private void DrawItems()
        {
            string title = activeLeaf.Name;
            Gated(false, delegate(float o, float age) { CategoryHeader(title, Layout.CategoryAt, 1f); });   // Grid.xur's Text group (category_header: shadowed)
            AvatarGender body0 = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
            int slots = RemoveSlots;
            int total = pageItems.Count + slots;
            DrawPickerChrome(ItemPages(catalog.For(activeLeaf.Category, body0, true).Count), Math.Min(total, Layout.CellsPerPage), false);

            ghosts.Draw();                      // Left/RightOffscreenItems: the neighbouring pages (Ghosts.cs)

            // The focused cell grows over its neighbours, so it is drawn last.
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < Layout.CellsPerPage; i++)
                {
                    bool sel = i == cursor;
                    if (sel != (pass == 1)) continue;
                    if (i >= total) { if (pass == 0) ui.Rounded(CellRect(i), Palette.CellDim); continue; }
                    if (i < slots) DrawRemoveCell(i, sel);
                    else DrawItemCell(i, pageItems[i - slots], sel);
                }
            DrawTooltips();                     // the cells' tooltips (StudioTooltips.cs)
            int per = Layout.CellsPerPage - slots;
            AvatarGender body = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
            int totalItems = catalog.For(activeLeaf.Category, body, true).Count;
            int pages = Math.Max(1, (totalItems + per - 1) / per);
            string pageText = "Page " + (page + 1) + " of " + pages;
            Gated(false, delegate(float o, float age) { PageNumber(pageText, Layout.PageNumberAt, 1f); });
            // no Y on the pickers offline: 17559's "Avatar Store" shows only when signed in
            ui.Legend(EditorLegend(stage.Screen));
        }

        private void DrawItemThumb(CatalogItem it, Rectangle cell, float opacity = 1f)
        {
            byte[] preview = PreviewManifest(it);
            Framing f = FramingFor(activeLeaf.Category);
            Texture2D t = thumbs.Get(preview, f);
            if (t != null) ui.Batch.Draw(t, cell, Color.White * opacity);
            else if (thumbs.IsFailed(preview, f)) ui.TextAt(XuiStyle.SmallText, "?", new Vector2(cell.Center.X, cell.Center.Y), Palette.TextGrey * opacity);   // [ours]
            else ui.Spinner(new Vector2(cell.Center.X, cell.Center.Y), 12, time, Palette.TextGrey * opacity);
        }

        // ---- the cells through their visuals (XuiVisuals.cs) ---------------------------------------

        private static Vector2 ItemCellScene(int i) { return new Vector2(129f + 631f * (i % 2), 80f + 120f * (i / 2)); }
        private static Vector2 ChipCellScene(int i) { return new Vector2(129f + 631f * (i / Layout.ChipRows), 139f + 120f * (i % Layout.ChipRows)); }
        private static Matrix ControlAt(Vector2 scene) { return Matrix.CreateTranslation(scene.X, scene.Y, 0f) * XuiDraw.SceneToScreen; }

        /// <summary>Element `id` drawn by `draw` in its own space; false when hidden or absent.</summary>
        private bool Elem(XuiPlayer p, Matrix ctrl, string id, Action<float> draw)
        {
            Matrix m; float o;
            if (!p.Global(id, out m, out o) || o <= 0f) return false;
            XuiDraw.Begin(ui, m * ctrl);
            draw(o);
            XuiDraw.End(ui);
            return true;
        }

        /// <summary>A SizeMode 16 presenter: its texture fitted into the element's box, centred.</summary>
        private void FitElem(XuiPlayer p, Matrix ctrl, string id, Texture2D t)
        {
            int k = p.Def.Index(id);
            if (k < 0 || t == null) return;
            XuiElementDef d = p.Def.Elements[k];
            Elem(p, ctrl, id, delegate(float o) { Fit(t, d.Width, d.Height, o); });
        }

        /// <summary>An image at its own size (SizeMode 1, and the 128 x 128 art of these visuals).</summary>
        private void OwnSize(Texture2D t, float o) { if (t != null) XuiDraw.Image(ui, t, 0f, 0f, t.Width, t.Height, Color.White * o); }

        /// <summary>Grid1x1ItemButton: its backgrounds, the preview, the tick and the award badge.</summary>
        private bool DrawItemVisual(XuiPlayer p, Matrix ctrl, Action<Rectangle, float> preview, bool chosen, bool award)
        {
            Texture2D bgU = tileArt.Named("border_nofill_bgunselected"), bgS = tileArt.Named("border_nofill_bg");
            if (bgU == null || bgS == null) return false;
            Elem(p, ctrl, "button bg unselected", delegate(float o) { OwnSize(bgU, o); });
            Elem(p, ctrl, "button bg selected", delegate(float o) { OwnSize(bgS, o); });
            Elem(p, ctrl, "preview", delegate(float o) { preview(new Rectangle(0, 0, 100, 100), o); });
            if (chosen) Elem(p, ctrl, "chosen", delegate(float o) { OwnSize(tileArt.Named("item_selected_tick"), o); });
            if (award) Elem(p, ctrl, "newitem", delegate(float o) { OwnSize(tileArt.Named("awardeditem"), o); });
            return true;
        }

        /// <summary>Grid1x1Button_deleteitem: the remove picture, then the delete x; false without the art.</summary>
        private bool DrawRemoveVisualXui(XuiPlayer p, Matrix ctrl, AvatarManifestEditor.RemovableComponents kind)
        {
            bool female = session.Body == AvatarGender.Female;
            Texture2D picU = tileArt.Remove(kind, female, false), picS = tileArt.Remove(kind, female, true);
            Texture2D xU = tileArt.Named("delete_item_x_unselected"), xS = tileArt.Named("delete_item_x_selected");
            if (picS == null || xU == null || xS == null) return false;
            Elem(p, ctrl, "unselected", delegate(float o) { OwnSize(picU ?? picS, o); });
            Elem(p, ctrl, "selected", delegate(float o) { OwnSize(picS, o); });
            Elem(p, ctrl, "delete_x_unselected", delegate(float o) { OwnSize(xU, o); });
            Elem(p, ctrl, "delete_x_selected", delegate(float o) { OwnSize(xS, o); });
            return true;
        }

        /// <summary>Grid1x1One / Two / ThreeColourButton: each colour in its square, then the frame.</summary>
        private bool DrawColourVisual(XuiPlayer p, Matrix ctrl, string artBase, Color[] colours, bool chosen)
        {
            Texture2D frameS = tileArt.Named(artBase + "_selected");
            Texture2D frameU = tileArt.HasStates ? tileArt.Named(artBase + "_unselected") : null;
            if (frameS == null) return false;
            for (int k = 0; k < colours.Length; k++)
            {
                float size = SetSquares[k][2];
                Color c = colours[k];
                Elem(p, ctrl, "bg" + (k + 1), delegate(float o) { ui.Rounded(new Rectangle(0, 0, (int)size, (int)size), c * o); });
            }
            if (frameU != null) Elem(p, ctrl, "unselected", delegate(float o) { OwnSize(frameU, o); });
            Elem(p, ctrl, "selected", delegate(float o) { OwnSize(frameS, o); });
            if (chosen) Elem(p, ctrl, "chosen", delegate(float o) { OwnSize(tileArt.Named("item_selected_tick"), o); });
            return true;
        }

        private readonly Dictionary<string, byte[]> previewCache = new Dictionary<string, byte[]>();

        /// <summary>An item cell's thumbnail: the asset with its cell's colours (sub_920E0B10).</summary>
        private byte[] PreviewManifest(CatalogItem it)
        {
            uint[][] s = ColourSets.TocSets(it);
            int k = ColourSets.ItemCellSet(s, session.Committed, it.Id);
            return PreviewManifest(it, k < 0 ? null : s[k]);
        }

        private byte[] PreviewManifest(CatalogItem it, uint[] set)
        {
            string key = set == null ? it.Id.ToString() : it.Id + ":" + set[0].ToString("X8") + set[1].ToString("X8") + set[2].ToString("X8");
            byte[] cached;
            if (previewCache.TryGetValue(key, out cached)) return cached;
            byte[] result;
            try
            {
                AvatarManifest m = AvatarManifest.Create(session.CommittedBytes);
                AvatarManifestEditor ed = AvatarStack.Editor(m);
                ed.ReplaceAsset(set == null ? new EditorAssetInfo(it.Id) : new EditorAssetInfo(it.Id, ColourSets.ToColors(set)));
                result = m.SaveToBinary();
            }
            catch { result = session.CommittedBytes; }
            previewCache[key] = result;
            return result;
        }

        /// <summary>The editor's own names for the remove cells (Strings.xus 204-213).</summary>
        private static string RemoveLabel(AvatarManifestEditor.RemovableComponents k)
        {
            switch (k)
            {
                case AvatarManifestEditor.RemovableComponents.FacialHair: return "Remove Facial Hair";
                case AvatarManifestEditor.RemovableComponents.Prop: return "Remove Prop";
                case AvatarManifestEditor.RemovableComponents.Earrings: return "Remove Earrings";
                case AvatarManifestEditor.RemovableComponents.EyeShadow: return "Remove Eye Shadow";
                case AvatarManifestEditor.RemovableComponents.SkinFeatures: return "Remove Facial Feature";
                case AvatarManifestEditor.RemovableComponents.Glasses: return "Remove Glasses";
                case AvatarManifestEditor.RemovableComponents.Gloves: return "Remove Gloves";
                case AvatarManifestEditor.RemovableComponents.Hat: return "Remove Headwear";
                case AvatarManifestEditor.RemovableComponents.Ring: return "Remove Ring";
                case AvatarManifestEditor.RemovableComponents.Wristwear: return "Remove Wristwear";
                default: return null;
            }
        }
        private string RemoveLabelForLeaf()
        {
            AvatarManifestEditor.RemovableComponents k;
            return RemovableFor(activeLeaf.Category, out k) ? RemoveLabel(k) : null;
        }

        // ---- the 1 x 1 grid cells (EditorSkin.xur): a 128 x 128 visual on a 120 x 120 button ------
        private const float ItemFocusScale = 1.3f;     // [xur] Grid1x1ItemButton, Focus -> frame 74
        private const float RemoveFocusScale = 1.5f;   // [xur] Grid1x1Button_deleteitem, FocusLoop
        private const float ChipFocusScale = 1.5f;     // [xur] Grid1x1OneColourButton, FocusLoop

        private static Rectangle ButtonRect(int i)
        {
            return new Rectangle(Layout.CellColumnX[i % 2], Layout.CellsTop + (i / 2) * Layout.CellRowPitch, Layout.CellSize, Layout.CellSize);
        }
        private static Rectangle ChipButtonRect(int i)
        {
            return new Rectangle(Layout.CellColumnX[i / Layout.ChipRows], Layout.ChipsTop + (i % Layout.ChipRows) * Layout.ChipRowPitch, Layout.ChipSize, Layout.ChipSize);
        }

        /// <summary>The visual's 128 x 128 box for a button, scaled about its centre.</summary>
        private static Rectangle VisualRect(Rectangle button, float scale)
        {
            float k = button.Width / 120f;                       // 0.88 on screen
            float px = 128f * k * scale;
            float cx = button.X + 64f * k, cy = button.Y + 64f * k;
            return new Rectangle((int)Math.Round(cx - px / 2), (int)Math.Round(cy - px / 2), (int)Math.Round(px), (int)Math.Round(px));
        }

        /// <summary>A child at (x, y, w, h) in the visual's own 128 x 128 space.</summary>
        private static Rectangle InVisual(Rectangle v, float x, float y, float w, float h)
        {
            float k = v.Width / 128f;
            return new Rectangle((int)Math.Round(v.X + x * k), (int)Math.Round(v.Y + y * k), (int)Math.Round(w * k), (int)Math.Round(h * k));
        }

        /// <summary>Grid1x1ItemButton without the visuals: background, preview and tick, settled.</summary>
        private void DrawItemCell(int i, CatalogItem it, bool focused)
        {
            bool equipped = session.EquippedComponent(ToMask(activeLeaf.Category)) == it.Id;
            XuiPlayer xp = controls[i];
            if (xp != null && DrawItemVisual(xp, ControlAt(ItemCellScene(i)), delegate(Rectangle r, float o) { DrawItemThumb(it, r, o); },
                                             equipped, it.TitleId != null)) return;
            Texture2D frame = tileArt.Named(focused ? "border_nofill_bg" : "border_nofill_bgunselected");
            if (frame == null)
            {
                Rectangle cell = CellRect(i);
                Rectangle rr = focused ? new Rectangle(cell.X - 5, cell.Y - 5, cell.Width + 10, cell.Height + 10) : cell;
                ui.Rounded(rr, focused ? Palette.SelectGreen : Palette.CellGrey);
                DrawItemThumb(it, rr);
                if (equipped) ui.Icon(ui.Check, new Vector2(rr.Right - 18, rr.Bottom - 18), 30, Palette.XboxGreen);
                if (it.TitleId != null) ui.Disc(new Vector2(rr.X + 16, rr.Y + 16), 9, Palette.GlyphY);
                return;
            }
            Rectangle v = VisualRect(ButtonRect(i), focused ? ItemFocusScale : 1f);
            ui.Batch.Draw(frame, v, Color.White);
            DrawItemThumb(it, InVisual(v, 14, 14, 100, 100));
            if (equipped) DrawNamed("item_selected_tick", v);
            // `newitem`: award items carry the awardeditem.png badge
            if (it.TitleId != null) DrawNamed("awardeditem", v);
        }

        /// <summary>Grid1x1Button_deleteitem without the visuals: the remove picture, then the delete x.</summary>
        private void DrawRemoveCell(int i, bool focused)
        {
            AvatarManifestEditor.RemovableComponents kind;
            RemovableFor(activeLeaf.Category, out kind);
            XuiPlayer xp = controls[i];
            if (xp != null && DrawRemoveVisualXui(xp, ControlAt(ItemCellScene(i)), kind)) return;
            DrawRemoveVisual(ButtonRect(i), CellRect(i), kind, focused);
        }

        private void DrawRemoveVisual(Rectangle button, Rectangle cell, AvatarManifestEditor.RemovableComponents kind, bool focused)
        {
            Texture2D pic = tileArt.Remove(kind, session.Body == AvatarGender.Female, focused);
            Texture2D x = tileArt.Named(focused ? "delete_item_x_selected" : "delete_item_x_unselected");
            if (pic == null || x == null)
            {
                Rectangle rr = focused ? new Rectangle(cell.X - 5, cell.Y - 5, cell.Width + 10, cell.Height + 10) : cell;
                ui.Rounded(rr, focused ? Palette.SelectGreen : Palette.CellGrey);
                ui.Icon(ui.Cross, new Vector2(rr.Center.X, rr.Center.Y), 48, Palette.RemoveRed);
                return;
            }
            Rectangle v = VisualRect(button, focused ? RemoveFocusScale : 1f);
            ui.Batch.Draw(pic, v, Color.White);
            ui.Batch.Draw(x, v, Color.White);
        }

        /// <summary>Grid1x1OneColourButton: the colour in its rounded square, then the frame.</summary>
        private void DrawChip(int i, Colorb cb, bool focused, bool chosen)
        {
            Color c = new Color(cb.red, cb.green, cb.blue);
            XuiPlayer xp = controls[i];
            if (xp != null && DrawColourVisual(xp, ControlAt(ChipCellScene(i)), "colour_tile", new[] { c }, chosen)) return;
            Texture2D frame = tileArt.Named("colour_tile_selected");
            Texture2D idle = tileArt.HasStates ? tileArt.Named("colour_tile_unselected") : null;
            if (frame == null)
            {
                Rectangle cell = ChipRect(i);
                Rectangle rr = focused ? new Rectangle(cell.X - 5, cell.Y - 5, cell.Width + 10, cell.Height + 10) : cell;
                if (focused) ui.Rounded(new Rectangle(rr.X - 3, rr.Y - 3, rr.Width + 6, rr.Height + 6), Color.White);
                ui.Rounded(rr, c);
                if (chosen) ui.Icon(ui.Check, new Vector2(rr.Right - 16, rr.Bottom - 16), 26, Palette.XboxGreen);
                return;
            }
            Rectangle v = VisualRect(ChipButtonRect(i), focused ? ChipFocusScale : 1f);
            ui.Rounded(InVisual(v, 14, 14, 100, 100), c);
            if (focused) ui.Batch.Draw(frame, v, Color.White);
            else if (idle != null) ui.Batch.Draw(idle, v, Color.White);
            if (chosen) DrawNamed("item_selected_tick", v);
        }

        private void DrawNamed(string name, Rectangle v)
        {
            Texture2D t = tileArt.Named(name);
            if (t != null) ui.Batch.Draw(t, v, Color.White);
        }

        // ---- the colour-set cells: one, two or three colour squares by m (sub_920DF4F8) ----------
        private static readonly float[][] SetSquares =
        {
            new[] { 14f, 14f, 100f },               // bg1 / r1
            new[] { 35f, 35f, 58f },                // bg2 / r2
            new[] { 50f, 50f, 28f },                // bg3 / r3
        };

        private static Color SetColour(uint c) { return new Color((byte)(c >> 16), (byte)(c >> 8), (byte)c); }

        private void DrawSetCell(int i, uint[] set, bool focused, bool chosen)
        {
            int m = sets.Shown;
            if (m < 1 || m > 3) { DrawSetItemCell(i, set, focused, chosen); return; }
            string art = m <= 1 ? "colour_tile" : "colour_tile_three";
            XuiPlayer xp = controls[i];
            if (xp != null)
            {
                Color[] cs = new Color[m];
                for (int k = 0; k < m; k++) cs[k] = SetColour(set[k]);
                if (DrawColourVisual(xp, ControlAt(ChipCellScene(i)), art, cs, chosen)) return;
            }
            Texture2D frame = tileArt.Named(art + "_selected");
            Texture2D idle = tileArt.HasStates ? tileArt.Named(art + "_unselected") : null;
            Rectangle v = VisualRect(ChipButtonRect(i), focused ? ChipFocusScale : 1f);
            // [ours] without the tile art a white outline stands in for the focused frame
            if (frame == null && focused) ui.Rounded(InVisual(v, 11, 11, 106, 106), Color.White);
            for (int k = 0; k < m; k++)
                ui.Rounded(InVisual(v, SetSquares[k][0], SetSquares[k][1], SetSquares[k][2], SetSquares[k][2]), SetColour(set[k]));
            if (focused && frame != null) ui.Batch.Draw(frame, v, Color.White);
            else if (!focused && idle != null) ui.Batch.Draw(idle, v, Color.White);
            if (chosen)
            {
                if (tileArt.Named("item_selected_tick") != null) DrawNamed("item_selected_tick", v);
                else ui.Icon(ui.Check, new Vector2(v.Right - 24, v.Bottom - 24), 26, Palette.XboxGreen);
            }
        }

        /// <summary>Grid1x1ItemButton for a set: the item's preview with the set's colours.</summary>
        private void DrawSetItemCell(int i, uint[] set, bool focused, bool chosen)
        {
            XuiPlayer xp = controls[i];
            if (xp != null)
            {
                byte[] pm = PreviewManifest(setsItem, set);
                Framing pf = FramingFor(followOnFrom != null ? followOnFrom.Category : setsItem.Category);
                if (DrawItemVisual(xp, ControlAt(ChipCellScene(i)), delegate(Rectangle r, float o)
                    {
                        Texture2D pt = thumbs.Get(pm, pf);
                        if (pt != null) ui.Batch.Draw(pt, r, Color.White * o);
                        else ui.Spinner(new Vector2(r.Center.X, r.Center.Y), 12, time, Palette.TextGrey * o);
                    }, chosen, false)) return;
            }
            Rectangle v = VisualRect(ChipButtonRect(i), focused ? ItemFocusScale : 1f);
            Texture2D frame = tileArt.Named(focused ? "border_nofill_bg" : "border_nofill_bgunselected");
            if (frame != null) ui.Batch.Draw(frame, v, Color.White);
            else ui.Rounded(InVisual(v, 8, 8, 112, 112), focused ? Palette.SelectGreen : Palette.CellGrey);
            byte[] preview = PreviewManifest(setsItem, set);
            Framing f = FramingFor(followOnFrom != null ? followOnFrom.Category : setsItem.Category);
            Texture2D t = thumbs.Get(preview, f);
            Rectangle thumb = InVisual(v, 14, 14, 100, 100);
            if (t != null) ui.Batch.Draw(t, thumb, Color.White);
            else ui.Spinner(new Vector2(thumb.Center.X, thumb.Center.Y), 12, time, Palette.TextGrey);
            if (chosen) DrawNamed("item_selected_tick", v);
        }

        private void DrawColourSets()
        {
            // no header: the screen's title is empty (Strings.xus 193)
            ghosts.Draw();                      // the neighbouring pages (Ghosts.cs)
            int total = sets.Sets.Length;
            int baseIdx = page * Layout.ChipsPerPage;
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < Layout.ChipsPerPage; i++)
                {
                    bool sel = i == cursor;
                    if (sel != (pass == 1)) continue;
                    int s = baseIdx + i;
                    if (s >= total) continue;
                    DrawSetCell(i, sets.Sets[s], sel, SetChosen(s));
                }
            // the cells' tooltips (StudioTooltips.cs)
            DrawTooltips();
            int pages = Math.Max(1, (total + Layout.ChipsPerPage - 1) / Layout.ChipsPerPage);   // sub_920E6850
            string pageText = "Page " + (page + 1) + " of " + pages;
            Gated(false, delegate(float o, float age) { PageNumber(pageText, Layout.PageNumberAt, 1f); });   // Grid2x3.xur's Text group
            DrawPickerChrome(pages, Math.Max(0, Math.Min(Layout.ChipsPerPage, total - baseIdx)), true);   // Grid2x3.xur's shadows, turntable handle and page handles
            ui.Legend(EditorLegend(stage.Screen));
        }

        // ---- colours ---------------------------------------------------------------------------
        private void DrawColors()
        {
            string title = activeLeaf.Title ?? activeLeaf.Name + " Color";
            Gated(false, delegate(float o, float age) { CategoryHeader(title, Layout.CategoryAt, 1f); });   // Grid2x3.xur's Text group (category_header: shadowed)
            ghosts.Draw();                      // the neighbouring pages (Ghosts.cs)
            int baseIdx = page * Layout.ChipsPerPage;
            int slots = ColorRemoveSlots;
            // no eye shadow chip is chosen while the shadow is off (sub_920DD498)
            for (int pass = 0; pass < 2; pass++)
                for (int i = 0; i < Layout.ChipsPerPage; i++)
                {
                    bool sel = i == cursor;
                    if (sel != (pass == 1)) continue;
                    int e = baseIdx + i;
                    if (e >= ColorEntries) continue;
                    if (e < slots)
                    {
                        XuiPlayer xp = controls[i];
                        if (xp == null || !DrawRemoveVisualXui(xp, ControlAt(ChipCellScene(i)), activeLeaf.Remove))
                            DrawRemoveVisual(ChipButtonRect(i), ChipRect(i), activeLeaf.Remove, sel);
                        continue;
                    }
                    DrawChip(i, palette[e - slots], sel, ChipChosen(e));
                }
            DrawTooltips();                     // the remove cell's (the chips' visual has no panel)
            int pages = Math.Max(1, (ColorEntries + Layout.ChipsPerPage - 1) / Layout.ChipsPerPage);
            string pageText = "Page " + (page + 1) + " of " + pages;
            Gated(false, delegate(float o, float age) { PageNumber(pageText, Layout.PageNumberAt, 1f); });
            DrawPickerChrome(pages, Math.Max(0, Math.Min(Layout.ChipsPerPage, ColorEntries - baseIdx)), true);   // Grid2x3.xur has the same shadows, handle and page handles
            ui.Legend(EditorLegend(stage.Screen));
        }

        // ---- body size -------------------------------------------------------------------------
        private void DrawBodySize()
        {
            Gated(false, delegate(float o, float age) { CategoryHeader("Body Size", Layout.CategoryAt, 1f); });   // HeightAndWidth.xur's Text group (AvatarCategoryLabelOverride; category_header: shadowed)
            // `items`, in order: ItemButton3 the accept, ItemButton2 the width rail, ItemButton1 the height rail
            DialogPage.Button(ui, tileArt, "option_dialog_yes", true, true, controls[0], new Vector2(172.227f, 248.4f));
            Texture2D bg = tileArt.Named("width_rail_bg"), clasp = tileArt.Named("clasp_width");
            XuiVisualDef rail = XuiVisuals.Get("AvatarHeightWidthButton");
            if (bg != null && clasp != null && rail != null)
            {
                DrawRail(rail, bg, clasp, WidthRailMatrix, widthStep);
                DrawRail(rail, bg, clasp, HeightRailMatrix, heightStep);
                ui.Legend(EditorLegend(stage.Screen));
                return;
            }
            // [ours] without the art: plain rails
            ui.Rounded(Layout.HeightRail, Palette.Panel);
            ui.Rounded(Layout.WidthRail, Palette.Panel);
            float hy = Layout.HeightRail.Bottom - (heightStep / 4f) * Layout.HeightRail.Height;
            ui.Disc(new Vector2(Layout.HeightRail.Center.X, hy), 18, Palette.SelectGreen);
            float wx = Layout.WidthRail.X + (widthStep / 4f) * Layout.WidthRail.Width;
            ui.Disc(new Vector2(wx, Layout.WidthRail.Center.Y), 18, Palette.SelectGreen);
            // [ours] their captions, in the small style
            ui.TextAt(XuiStyle.SmallText, "taller / shorter", new Vector2(Layout.HeightRail.Center.X, Layout.HeightRail.Y - 16), Palette.TextDim);
            ui.TextAt(XuiStyle.SmallText, "thinner / wider", new Vector2(Layout.WidthRail.Center.X, Layout.WidthRail.Y - 16), Palette.TextDim);
            ui.Legend(EditorLegend(stage.Screen));
        }

        /// <summary>A rail, AvatarHeightWidthButton: width_rail_bg.png, then the clasp at value0..4's x.</summary>
        private static readonly Matrix WidthRailMatrix = Matrix.CreateTranslation(350.227f, 540f, 0f);
        private static readonly Matrix HeightRailMatrix = Matrix.CreateFromQuaternion(new Quaternion(0f, 0f, -0.707107f, 0.707105f))
                                                          * Matrix.CreateTranslation(713.627f, 464.5f, 0f);

        /// <summary>The marker's x along its rail at value `index` (the visual's frame "value<i>").</summary>
        private static float RailMarkerX(XuiVisualDef rail, int index)
        {
            float x = 1f;
            int frame = rail.Frame("value" + index);
            foreach (XuiTrack t in rail.Tracks)
                if (t.Target == "marker" && frame >= 0)
                    for (int p = 0; p < t.Props.Length; p++)
                        if (t.Props[p] == "Position") { object v = XuiVisuals.Sample(t, p, frame); if (v != null) x = ((Vector3)v).X; }
            return x;
        }

        /// <summary>[ours] The mouse on the rails: held down, the clasp steps toward the value under the pointer.</summary>
        private void BodySizeMouse()
        {
            if (!input.MouseHeld) return;
            int h = RailIndexAt(true), w = RailIndexAt(false);
            if (h >= 0 && h != heightStep) SizeStep(ref heightStep, h > heightStep, "Height");
            else if (w >= 0 && w != widthStep) SizeStep(ref widthStep, w > widthStep, "Width");
        }

        /// <summary>The value (0..4) nearest the pointer along a rail, or -1 off it.</summary>
        private int RailIndexAt(bool height)
        {
            XuiVisualDef rail = XuiVisuals.Get("AvatarHeightWidthButton");
            Texture2D bg = tileArt.Named("width_rail_bg"), clasp = tileArt.Named("clasp_width");
            if (rail == null || bg == null || clasp == null)
            {
                // the plain rails ([ours], DrawBodySize without the art)
                Rectangle r = height ? Layout.HeightRail : Layout.WidthRail;
                Rectangle hit = new Rectangle(r.X - 20, r.Y - 20, r.Width + 40, r.Height + 40);
                if (!input.MouseIn(hit)) return -1;
                float t = height ? (r.Bottom - input.MousePos.Y) / r.Height : (input.MousePos.X - r.X) / r.Width;
                return (int)Math.Round(MathHelper.Clamp(t, 0f, 1f) * 4f);
            }
            // the pointer back into the rail's space: SceneToScreen flattens z, so invert its 2-D affine part
            Matrix m = (height ? HeightRailMatrix : WidthRailMatrix) * XuiDraw.SceneToScreen;
            float det = m.M11 * m.M22 - m.M12 * m.M21;
            if (Math.Abs(det) < 1e-6f) return -1;
            float sx = input.MousePos.X - m.M41, sy = input.MousePos.Y - m.M42;
            Vector2 p = new Vector2((m.M22 * sx - m.M21 * sy) / det, (-m.M12 * sx + m.M11 * sy) / det);
            float first = RailMarkerX(rail, 0), last = RailMarkerX(rail, SizeSteps.Length - 1) + clasp.Width;
            if (p.Y < 0f || p.Y > clasp.Height || p.X < first - 20f || p.X > last + 20f) return -1;
            int best = 0;
            for (int i = 1; i < SizeSteps.Length; i++)
                if (Math.Abs(RailMarkerX(rail, i) + clasp.Width / 2f - p.X) < Math.Abs(RailMarkerX(rail, best) + clasp.Width / 2f - p.X)) best = i;
            return best;
        }

        private void DrawRail(XuiVisualDef rail, Texture2D bg, Texture2D clasp, Matrix button, int index)
        {
            float x = RailMarkerX(rail, index);
            XuiDraw.Begin(ui, button * XuiDraw.SceneToScreen);
            XuiDraw.Image(ui, bg, 19f, 26f, bg.Width, bg.Height, Color.White);
            XuiDraw.Image(ui, clasp, x, 0f, clasp.Width, clasp.Height, Color.White);
            XuiDraw.End(ui);
        }

        // ---- shared chrome -----------------------------------------------------------------------

        // ---- geometry helpers ------------------------------------------------------------------
        private static Rectangle CardRect(int slot)
        {
            int r = slot / Layout.CardCols, c = slot % Layout.CardCols;
            return new Rectangle(Layout.CardsOrigin.X + c * (Layout.CardW + Layout.CardGap),
                                 Layout.CardsOrigin.Y + r * (Layout.CardH + Layout.CardGap),
                                 Layout.CardW, Layout.CardH);
        }
        private static Rectangle MenuRowRect(int i)
        {
            Rectangle m = Layout.CardMenu;
            return new Rectangle(m.X + 12, m.Y + 56 + i * 44, m.Width - 24, 38);
        }

        /// <summary>Grid movement on the gallery's page; [ours] the edge columns turn the page, as the editor's grids do.</summary>
        private void MoveCardCursor(int count)
        {
            int page = galleryCursor / Layout.CardsPerPage;
            int slot = galleryCursor % Layout.CardsPerPage;
            int col = slot % Layout.CardCols, row = slot / Layout.CardCols;
            bool turned = false;
            if (input.Left && col == 0 && page > 0) { page--; col = Layout.CardCols - 1; turned = true; }
            else if (input.Left && col > 0) col--;
            if (input.Right && col == Layout.CardCols - 1 && (page + 1) * Layout.CardsPerPage < count) { page++; col = 0; turned = true; }
            else if (input.Right && col < Layout.CardCols - 1) col++;
            if (input.Up && row > 0) row--;
            if (input.Down && row < Layout.CardRows - 1) row++;
            int i = page * Layout.CardsPerPage + row * Layout.CardCols + col;
            if (i < count) galleryCursor = i;
            else if (turned) galleryCursor = count - 1;
        }

        private bool MouseOverCard(int count, out int index)
        {
            int page = galleryCursor / Layout.CardsPerPage;
            for (int slot = 0; slot < Layout.CardsPerPage; slot++)
            {
                int i = page * Layout.CardsPerPage + slot;
                if (i >= count) break;
                if (input.MouseIn(CardRect(slot))) { index = i; return input.MouseMoved || input.MouseClicked; }
            }
            index = -1; return false;
        }

        private Rectangle TileRect(int i, Point origin, int cols)
        {
            int r = i / cols, c = i % cols;
            return new Rectangle(origin.X + c * (Layout.TileW + Layout.TileGapX),
                                 origin.Y + r * (Layout.TileH + Layout.TileGapY), Layout.TileW, Layout.TileH);
        }
        /// <summary>Cell i in Grid.xur's ItemButton order, alternating sides down the rows.</summary>
        private static Rectangle CellRect(int i)
        {
            int k = Layout.CellInset;
            return new Rectangle(Layout.CellColumnX[i % 2] + k, Layout.CellsTop + (i / 2) * Layout.CellRowPitch + k,
                                 Layout.CellSize - 2 * k, Layout.CellSize - 2 * k);
        }

        /// <summary>The ghosts the screen shows: the previous page's right-hand cells left, the next page's left right.</summary>
        private List<Ghosts.Spec> GhostSpecs()
        {
            if (state == State.Outfits) return OutfitGhostSpecs();
            List<Ghosts.Spec> specs = new List<Ghosts.Spec>();
            bool items = state == State.Items, chips = state == State.Colors && palette != null,
                 setsOn = state == State.ColourSets && sets != null;
            if (!items && !chips && !setsOn) return specs;
            List<CatalogItem> all = null;
            Framing framing = default(Framing);
            if (items)
            {
                AvatarGender body = session.Body == AvatarGender.Unknown ? AvatarGender.Both : session.Body;
                all = catalog.For(activeLeaf.Category, body, true);
                framing = FramingFor(activeLeaf.Category);
            }
            int rows = items ? Layout.CellRows : Layout.ChipRows;
            for (int side = 0; side < 2; side++)
            {
                int p = side == 0 ? page - 1 : page + 1;
                if (p < 0) continue;
                for (int row = 0; row < rows; row++)
                {
                    // Grid.xur: Left2/4/6/8, Right1/3/5/7; Grid2x3.xur: Left4/5/6, Right1/2/3
                    int c = items ? row * 2 + (1 - side) : (side == 0 ? Layout.ChipRows + row : row);
                    Ghosts.Spec s = new Ghosts.Spec();
                    if (items)
                    {
                        int slots = HasRemoveCell && p == 0 ? 1 : 0;
                        if (c < slots) continue;
                        int item = ItemStart(p) + c - slots;
                        if (item < 0 || item >= all.Count) continue;
                        s.Picture = thumbs.Get(PreviewManifest(all[item]), framing);
                        s.Key = "i:" + all[item].Id + ":" + framing + ":" +
                                (s.Picture == null ? "-" : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(s.Picture).ToString());
                        s.Visual = VisualRect(new Rectangle(Layout.CellGhostX[side], Layout.CellsTop + row * Layout.CellRowPitch, Layout.CellSize, Layout.CellSize), 1f);
                    }
                    else
                    {
                        Color c0;
                        if (chips)
                        {
                            int idx = p * Layout.ChipsPerPage + c - ColorRemoveSlots;
                            if (idx < 0 || idx >= palette.Length) continue;
                            c0 = Ghosts.GhostColour(palette[idx].red, palette[idx].green, palette[idx].blue);
                        }
                        else
                        {
                            int idx = p * Layout.ChipsPerPage + c;
                            if (idx < 0 || idx >= sets.Sets.Length) continue;
                            uint u = sets.Sets[idx][0];                // the item's colour 0
                            c0 = Ghosts.GhostColour((byte)(u >> 16), (byte)(u >> 8), (byte)u);
                        }
                        s.Colour = true;
                        s.Fill = c0;
                        s.Key = "c:" + c0.PackedValue.ToString("X8");
                        s.Visual = VisualRect(new Rectangle(Layout.CellGhostX[side], Layout.ChipsTop + row * Layout.ChipRowPitch, Layout.ChipSize, Layout.ChipSize), 1f);
                    }
                    specs.Add(s);
                }
            }
            return specs;
        }
        /// <summary>Grid2x3.xur's cell i: ItemButton1..3 down the left column, 4..6 down the right.</summary>
        private static Rectangle ChipRect(int i)
        {
            int k = Layout.CellInset;
            return new Rectangle(Layout.CellColumnX[i / Layout.ChipRows] + k, Layout.ChipsTop + (i % Layout.ChipRows) * Layout.ChipRowPitch + k,
                                 Layout.ChipSize - 2 * k, Layout.ChipSize - 2 * k);
        }

        /// <summary>Grid2x3.xur's navigation: up / down in a column, left / right to the other; the edges turn the page.</summary>
        private int MoveColumns(int count)
        {
            int rows = Layout.ChipRows;
            int turn = input.Left && cursor < rows ? -1 : input.Right && cursor >= rows ? 1 : 0;
            if (input.Up && cursor % rows > 0) cursor--;
            if (input.Down && cursor % rows < rows - 1 && cursor + 1 < count) cursor++;
            if (input.Left && cursor >= rows) cursor -= rows;
            if (input.Right && cursor < rows && cursor + rows < count) cursor += rows;
            cursor = (int)MathHelper.Clamp(cursor, 0, Math.Max(0, count - 1));
            return turn;
        }

        private void MoveGridCursor(int count, int cols)
        {
            if (input.Left && cursor % cols > 0) cursor--;
            if (input.Right && cursor % cols < cols - 1 && cursor + 1 < count) cursor++;
            if (input.Up && cursor - cols >= 0) cursor -= cols;
            if (input.Down && cursor + cols < count) cursor += cols;
        }
        private int MoveLinear(int count)
        {
            // Grid.xur: up / down a row in the column, left / right across; the edges turn the page
            int turn = input.Left && cursor % 2 == 0 ? -1 : input.Right && cursor % 2 == 1 ? 1 : 0;
            if (input.Up && cursor - 2 >= 0) cursor -= 2;
            if (input.Down && cursor + 2 < count) cursor += 2;
            if (input.Left && cursor % 2 == 1) cursor--;
            if (input.Right && cursor % 2 == 0 && cursor + 1 < count) cursor++;
            cursor = (int)MathHelper.Clamp(cursor, 0, Math.Max(0, count - 1));
            return turn;
        }
        private bool MouseOverTile(int count, Point origin, int cols, out int index)
        {
            for (int i = 0; i < count; i++)
                if (input.MouseIn(TileRect(i, origin, cols))) { index = i; return input.MouseMoved || input.MouseClicked; }
            index = -1; return false;
        }

        private IEnumerable<char> PopTyped()
        {
            // typed characters, filled by the TextInput handler
            List<char> outp = typedBuffer;
            typedBuffer = new List<char>();
            return outp;
        }
        private List<char> typedBuffer = new List<char>();
        protected override void OnActivated(object sender, EventArgs a) { base.OnActivated(sender, a); }

        // FNA text input
        private bool textHooked;
        private void HookText()
        {
            if (textHooked) return; textHooked = true;
            // This FNA build exposes typed characters through TextInputEXT, not GameWindow.TextInput.
            Microsoft.Xna.Framework.Input.TextInputEXT.TextInput += delegate (char c) { typedBuffer.Add(c); };
        }

        protected override void BeginRun() { HookText(); base.BeginRun(); }

        // ---- mapping helpers -------------------------------------------------------------------
        private static Framing FramingFor(ComponentCategories c)
        {
            switch (c)
            {
                case ComponentCategories.Hair: case ComponentCategories.Eyes: case ComponentCategories.Eyebrows:
                case ComponentCategories.Nose: case ComponentCategories.Chin: case ComponentCategories.Ears:
                case ComponentCategories.Mouth: case ComponentCategories.FacialHair: case ComponentCategories.FacialOther:
                case ComponentCategories.EyeShadow: case ComponentCategories.Hat: case ComponentCategories.Glasses:
                case ComponentCategories.Earrings:
                    return Framing.Head;
                case ComponentCategories.Shirt: case ComponentCategories.Gloves: case ComponentCategories.Wristwear: case ComponentCategories.Ring:
                    return Framing.Upper;
                case ComponentCategories.Trousers:
                    return Framing.Legs;
                case ComponentCategories.Shoes:
                    return Framing.Feet;
                default:
                    return Framing.FullBody;
            }
        }
        private static AvatarComponentMasks ToMask(ComponentCategories c)
        {
            int v = (int)c;
            return v <= (int)AvatarComponentMasks.All ? (AvatarComponentMasks)v : AvatarComponentMasks.None;
        }
        private static bool RemovableFor(ComponentCategories c, out AvatarManifestEditor.RemovableComponents kind)
        {
            switch (c)
            {
                case ComponentCategories.Hat: kind = AvatarManifestEditor.RemovableComponents.Hat; return true;
                case ComponentCategories.Glasses: kind = AvatarManifestEditor.RemovableComponents.Glasses; return true;
                case ComponentCategories.Gloves: kind = AvatarManifestEditor.RemovableComponents.Gloves; return true;
                case ComponentCategories.Wristwear: kind = AvatarManifestEditor.RemovableComponents.Wristwear; return true;
                case ComponentCategories.Ring: kind = AvatarManifestEditor.RemovableComponents.Ring; return true;
                case ComponentCategories.Earrings: kind = AvatarManifestEditor.RemovableComponents.Earrings; return true;
                case ComponentCategories.FacialHair: kind = AvatarManifestEditor.RemovableComponents.FacialHair; return true;
                case ComponentCategories.FacialOther: kind = AvatarManifestEditor.RemovableComponents.SkinFeatures; return true;
                case ComponentCategories.EyeShadow: kind = AvatarManifestEditor.RemovableComponents.EyeShadow; return true;
                case ComponentCategories.Carryable: kind = AvatarManifestEditor.RemovableComponents.Prop; return true;
                default: kind = AvatarManifestEditor.RemovableComponents.NotRemovable; return false;
            }
        }

        private bool AssetDirHasToc()
        {
            string d = FindAssetDir();
            return d != null && File.Exists(Path.Combine(d, "tocassetdependencies.strb"));
        }
        private static string FindAssetDir() { return SetupInstall.ExistingItemsFolder(); }

        protected override void UnloadContent()
        {
            Sounds.Shutdown();                              // before FNA's audio device goes (Sounds.cs)
            if (stage != null) stage.Dispose();
            if (thumbs != null) thumbs.Dispose();
            if (cards != null) cards.Dispose();
            if (tileArt != null) tileArt.Dispose();
            if (setupArt != null) setupArt.Dispose();
            if (lineupStage != null) lineupStage.Dispose();
            foreach (Texture2D t in cardThumbs.Values) if (t != null) t.Dispose();
            cardThumbs.Clear();
            base.UnloadContent();
        }
    }
}
