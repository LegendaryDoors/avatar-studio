// "Choose an avatar as a starting point" as the editor draws it: seven live avatars on the floor,
// running on and off as the pages turn (AE_Lineup_*).

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using AvatarStudio.Avatars;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class LineupStage : IDisposable
    {
        public const int Count = 7;                     // AvatarButton0..6 = slots -1..5

        // [run] AE_Lineup_UnprojectToFloor, AvatarButton1..5 (x, y, z, console space)
        private static readonly Vector3[] ButtonSpot =
        {
            new Vector3(-1.219685f, -0.175f, 1.987989f), new Vector3(-0.316104f, -0.175f, 1.759746f),
            new Vector3(0.5f, -0.175f, 1.578230f), new Vector3(1.316096f, -0.175f, 1.759701f),
            new Vector3(2.219665f, -0.175f, 1.987989f),
        };
        // [run] AE_Lineup_FaceCamera at those spots
        private static readonly float[] ButtonFacing = { -0.340918f, -0.164291f, 0.024999f, 0.214291f, 0.390914f };
        // [run] enterExitPosLeft / Right (AE_Lineup_Init into 0x92326D30 / 0x92326D40), to 4 places
        private static readonly Vector3 ExitLeft = new Vector3(-5.5672f, -0.175f, 5.1036f), ExitRight = new Vector3(6.5672f, -0.175f, 5.1036f);
        // unk_92326D50 / unk_92326D60: the dummies' spots (the constructor measures them)
        private readonly Vector3 dummyLeft, dummyRight;

        private readonly GraphicsDevice device;
        private Texture2D floorShadow;
        public Texture2D FloorShadow { set { floorShadow = value; foreach (AvatarView v in actors.Values) v.FloorShadow = value; } }

        // the actors, by manifest (reference: the lineup's hundred are distinct arrays)
        private readonly Dictionary<byte[], AvatarView> actors = new Dictionary<byte[], AvatarView>();

        // ---- the buttons: index i is AvatarButton i, slot i - 1; the offsets are the console's ------
        private enum Act { Waiting, Arriving, Standing, Floating, RunningOff, Chosen, Crossing, Gone }   // +24, 0..7
        private readonly Act[] act = new Act[Count];
        private readonly byte[][] manifest = new byte[Count][];                             // +20
        private readonly Vector3[] spot = new Vector3[Count];                              // +96
        private readonly Vector3[] basePos = new Vector3[Count], dest = new Vector3[Count]; // +48, +72
        private readonly float[] yaw = new float[Count], targetYaw = new float[Count];     // +40, +64
        private readonly float[] turnSpeed = new float[Count];                             // +128
        private readonly float[] delay = new float[Count], pickTimer = new float[Count];  // +32, +28
        private readonly int[] motion = new int[Count];                                    // +120: 0, 1 started, 2 measuring
        private readonly bool[] skipRunOn = new bool[Count], crossArmed = new bool[Count]; // +112, +140
        private readonly bool[] queued = new bool[Count];   // the timer queued this frame's draw (sub_920F92E8)

        private readonly Random rng = new Random();
        private IList<byte[]> lineup = new byte[0][];
        private int page;                               // dword_92326C74
        private bool fromRight = true;                  // byte_92312770: 1 at init and on Next
        private int chosen = -1;                        // dword_9231276C, 255 = none
        private int pickPlaying = -1, pickLast = -1;    // dword_92312760 / dword_92312764 (by button here, by slot there)
        private int leaving;                            // dword_92326C78
        private bool lastOut;                           // byte_92326C73, cleared at the scene tick's end
        private static readonly HashSet<float> arrivalLogged = new HashSet<float>();   // the first arrival per height scale is logged
        private int focus = -1;                         // the focused button, -1 none

        // 0x92312728 / 0x92312744: the run-on's start time per AvatarButton0..6, from the right / left
        private static readonly float[] StartFromRight = { 0f, 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f };
        private static readonly float[] StartFromLeft = { 1.5f, 1.25f, 1f, 0.75f, 0.5f, 0.25f, 0f };
        private const float ArriveRadius = 0.25f;                  // 0x92312708
        private const float TurnTop = 15.707964f, TurnRamp = 0.5f; // 0x9231270C, 0x92312710
        private const float CrossDegrees = 10f;                    // 0x92312724

        // [inferred] the buttons' timers fire in EditorStart.xur's order, AvatarButton6, 0, 5, 1, 4, 2, 3
        private static readonly int[] TimerOrder = { 6, 0, 5, 1, 4, 2, 3 };

        public LineupStage(GraphicsDevice device)
        {
            this.device = device;
            dummyLeft = DummySpot(ButtonSpot[4], EditorAnimations.CrossToBackRight, +CrossDegrees);
            dummyRight = DummySpot(ButtonSpot[0], EditorAnimations.CrossToBackLeft, -CrossDegrees);
            Console.WriteLine("[studio] lineup dummies at " + Fmt(dummyLeft) + " and " + Fmt(dummyRight));
            if (Environment.GetEnvironmentVariable("AVATAR_STUDIO_ANIMLOG") == "1")
                for (int id = 59; id <= 66; id++)
                {
                    AvatarAnimation a = EditorAnimations.Create(id);
                    if (a == null) continue;
                    a.CurrentPosition = TimeSpan.Zero; Vector3 r0 = a.BoneTransforms[0].Translation;
                    a.CurrentPosition = TimeSpan.FromSeconds(a.Length.TotalSeconds / 2); Vector3 rm = a.BoneTransforms[0].Translation;
                    a.CurrentPosition = a.Length; Vector3 r1 = a.BoneTransforms[0].Translation;
                    Console.WriteLine("[studio] animation " + id + ": " + a.Length.TotalSeconds.ToString("0.000") + " s, root " + Fmt(r0) + " .. " + Fmt(rm) + " .. " + Fmt(r1));
                    a.Dispose();
                }
            Start(new byte[0][]);
        }

        private static string Fmt(Vector3 v) { return "(" + v.X.ToString("0.0000") + ", " + v.Y.ToString("0.0000") + ", " + v.Z.ToString("0.0000") + ")"; }

        /// <summary>A dummy's spot (AE_Lineup_Init): a button's spot plus a cross-to-back displacement turned 10 degrees.</summary>
        private static Vector3 DummySpot(Vector3 from, int id, float degrees)
        {
            Vector3 d;
            if (!RootDisplacement(id, out d))
                return id == EditorAnimations.CrossToBackRight ? new Vector3(-3.263183f, -0.175f, 3.894226f) : new Vector3(4.263124f, -0.175f, 3.894126f);
            d.Z = -d.Z;
            return from + Vector3.Transform(d, Matrix.CreateRotationY(MathHelper.ToRadians(degrees)));
        }

        /// <summary>AE_Anim_RootDisplacement: the root's translation at the end minus at the start.</summary>
        public static bool RootDisplacement(int id, out Vector3 d)
        {
            d = Vector3.Zero;
            AvatarAnimation a = EditorAnimations.Create(id);
            if (a == null) return false;
            try
            {
                a.CurrentPosition = TimeSpan.Zero;
                Vector3 t0 = a.BoneTransforms[0].Translation;
                a.CurrentPosition = a.Length;
                d = a.BoneTransforms[0].Translation - t0;
                return true;
            }
            finally { a.Dispose(); }
        }

        /// <summary>[approx] the facing toward the camera from a point (exact only at the five spots)</summary>
        private static float Facing(Vector3 p) { return (float)Math.Atan((p.X - 0.5) / (p.Z + 2.5)) + 0.025f; }

        /// <summary>AE_Lineup_FaceCamera's angle: the run value at the five spots.</summary>
        private static float FacingAt(Vector3 p)
        {
            for (int k = 0; k < ButtonSpot.Length; k++) if (ButtonSpot[k] == p) return ButtonFacing[k];
            return Facing(p);
        }

        /// <summary>The height's scale on a cross-over (AE_Actor_RootOffset 0x920AE340), from the manifest's height.</summary>
        private static float HeightScale(byte[] m)
        {
            float h = EditorCamera.HeightFactor(m);
            float[] heights = { -1f, -0.5f, 0f, 0.5f, 1f }, scales = { 0.9f, 0.95f, 1f, 1.05f, 1.1f };
            for (int k = 0; k < heights.Length; k++) if (h == heights[k]) return scales[k];
            return scales[0];
        }

        private static bool IsFemale(byte[] manifest)
        {
            try { return AvatarManifest.Create(manifest).BodyType == AvatarGender.Female; }
            catch { return false; }
        }

        private static bool IsDummy(int i) { return i == 0 || i == Count - 1; }

        // ---- the actors -------------------------------------------------------------------------

        private AvatarView Actor(int i)
        {
            AvatarView v;
            return manifest[i] != null && actors.TryGetValue(manifest[i], out v) ? v : null;
        }

        /// <summary>sub_920F9258 + sub_920F8F98: a new actor for the manifest, added to the table.</summary>
        private AvatarView Create(int i)
        {
            byte[] m = manifest[i];
            AvatarView v = new AvatarView(device);
            v.Screen = 3;                               // the editor's animation queue (EditorMode)
            v.NoIdle = true;
            v.FloorShadow = floorShadow;
            v.Placement = Record(spot[i], 0f);          // placed: the renderer gets a shadow map of its own
            v.Set(m, IsFemale(m));
            actors[m] = v;
            return v;
        }

        /// <summary>sub_920F9040: the actor of a manifest out of the table, and gone.</summary>
        private void Destroy(byte[] m)
        {
            AvatarView v;
            if (m == null || !actors.TryGetValue(m, out v)) return;
            actors.Remove(m);
            v.Dispose();
        }

        private static float[] Record(Vector3 p, float yaw) { return new[] { 0f, yaw, 0f, p.X, p.Y, p.Z }; }

        // ---- entering, and the page turn ----------------------------------------------------------

        /// <summary>AE_Lineup_Init: the hundred, page 1, coming from the right; every button waiting.</summary>
        public void Start(IList<byte[]> list)
        {
            foreach (AvatarView v in actors.Values) v.Dispose();
            actors.Clear();
            lineup = list;
            page = 0; fromRight = true; chosen = -1; leaving = 0; lastOut = false;
            pickPlaying = pickLast = -1;
            AnyRunningOn = false;
            for (int i = 0; i < Count; i++)
            {
                act[i] = Act.Waiting; manifest[i] = null; motion[i] = 0;
                skipRunOn[i] = crossArmed[i] = queued[i] = false;
                yaw[i] = targetYaw[i] = turnSpeed[i] = delay[i] = 0f;
                spot[i] = IsDummy(i) ? Vector3.Zero : ButtonSpot[i - 1];   // a dummy's is set as it runs on
                basePos[i] = dest[i] = spot[i];
                // AE_LineupButton_Init: rand(200) x 0.1 (0x920010DC); AE_LineupDummy_Init: 0
                pickTimer[i] = IsDummy(i) ? 0f : rng.Next(200) * 0.1f;
            }
            turnClock = -1f;
        }

        /// <summary>Next or Previous (0x920B0508 / 0x920B0560) once every avatar stands, then the turn starts.</summary>
        public bool Turn(bool next, int newPage)
        {
            if (!AllStanding || chosen >= 0) return false;
            page = newPage;
            fromRight = next;
            leaving = Count;
            for (int i = 0; i < Count; i++) Leave(i);   // AvatarButton0..6, as the tick's loop
            turnClock = 0f; handedOver = 0;
            Console.WriteLine("[studio] lineup: " + (next ? "next" : "previous") + " page, " + (page + 1));
            return true;
        }

        /// <summary>AE_LineupButton_Leave: a floater drops first; one end crosses over, the rest run off.</summary>
        private void Leave(int i)
        {
            if (act[i] == Act.RunningOff || act[i] == Act.Crossing || act[i] == Act.Gone) return;
            if (act[i] == Act.Floating)
            {
                AvatarView v = Actor(i);
                if (v != null) { v.ClearQueue(); v.Request(EditorAnimations.PickMeOutOfFloat, false); }
            }
            bool cross = fromRight ? (i == 5 || i == 6) : (i == 1 || i == 0);
            act[i] = cross ? Act.Crossing : Act.RunningOff;
            delay[i] = rng.Next(100) * 0.01f + 0.05f;
        }

        // ---- the state the scene and the studio read ----------------------------------------------

        /// <summary>A button (not a dummy) has started running on since Start.</summary>
        public bool AnyRunningOn { get; private set; }

        public bool AllStanding
        {
            get
            {
                for (int i = 0; i < Count; i++) if (act[i] != Act.Standing && act[i] != Act.Floating) return false;
                return true;
            }
        }

        public int StandingCount
        {
            get { int k = 0; for (int i = 0; i < Count; i++) if (act[i] == Act.Standing || act[i] == Act.Floating) k++; return k; }
        }

        /// <summary>The focused avatar floats: navigation is live and A may choose (0x920B05B8).</summary>
        public bool FocusFloating { get { return focus >= 0 && act[focus] == Act.Floating; } }

        /// <summary>A on a floating avatar (AE_LineupButton_Choose): it celebrates, and 2150 ms later is chosen.</summary>
        public bool Choose()
        {
            if (!FocusFloating || chosen >= 0) return false;
            AvatarView v = Actor(focus);
            if (v != null) { v.ClearQueue(); v.Request(EditorAnimations.FloatingCelebrate, false); }
            chosen = focus;
            act[focus] = Act.Chosen;
            return true;
        }
        public const float ChooseSeconds = 2.15f;

        /// <summary>Focus on page slot 0..4, or -1.</summary>
        public void Focus(int slot)
        {
            int f = slot < 0 ? -1 : slot + 1;
            if (f == focus) return;
            focus = f;
            BeamTime = 0f;
        }

        /// <summary>Seconds the focused button's visual has been in Focus: the beam's timeline.</summary>
        public float BeamTime;
        private bool beamWas;

        /// <summary>The focused button is enabled (states 2 and 3), so its beam shows.</summary>
        public bool BeamVisible { get { return focus >= 0 && (act[focus] == Act.Standing || act[focus] == Act.Floating); } }

        // ---- every frame ---------------------------------------------------------------------------

        private float turnClock = -1f;                  // seconds since a turn started, for the log
        private int handedOver;

        public void Update(float dt)
        {
            // the buttons' timers: each update, then the motion, light and position, then the queued draw
            foreach (int i in TimerOrder)
            {
                queued[i] = false;
                UpdateButton(i, dt);
                AvatarView v = Actor(i);
                if (v == null || !v.Ready) continue;
                Motion(i, v, dt);
                Place(i, v);
                queued[i] = true;
            }
            // then the actors: the queue, the pose, and a cross-over's re-base (sub_920AFB10)
            foreach (AvatarView v in actors.Values) v.Update(dt);
            for (int i = 0; i < Count; i++)
            {
                AvatarView v = Actor(i);
                if (v != null && v.Rebased && act[i] == Act.Crossing) Rebase(i, v);
            }
            lastOut = false;                            // AE_Lineup_Tick's end

            bool beam = BeamVisible;
            BeamTime = beam && !beamWas ? 0f : BeamTime + dt;
            beamWas = beam;

            if (turnClock >= 0f)
            {
                turnClock += dt;
                if (AllStanding)
                {
                    Console.WriteLine("[studio] lineup: page " + (page + 1) + " standing " + turnClock.ToString("0.00") + " s after the turn, "
                        + handedOver + " avatars handed over, " + actors.Count + " actors");
                    turnClock = -1f;
                }
            }
        }

        private void UpdateButton(int i, float dt)
        {
            bool dummy = IsDummy(i);
            int slot = i - 1;
            AvatarView v;
            switch (act[i])
            {
                case Act.Waiting:
                    if (manifest[i] == null) manifest[i] = PickManifest(slot);
                    if (manifest[i] == null) return;
                    v = Actor(i) ?? Create(i);
                    if (!v.Ready || v.Loading) return;          // the actor's renderer (+1024 == 2)
                    v.ClearQueue();
                    if (skipRunOn[i])
                    {
                        // it crossed over and is already here (+112)
                        skipRunOn[i] = false;
                        act[i] = Act.Arriving;
                        handedOver++;
                        return;
                    }
                    if (dummy) spot[i] = i == 0 ? dummyLeft : dummyRight;
                    yaw[i] = targetYaw[i] = 0f;
                    basePos[i] = dest[i] = spot[i];
                    // queued with the cut flag: it replaces what the new actor holds at once, no blend
                    v.Request(fromRight ? EditorAnimations.RunOnTurnLeft : EditorAnimations.RunOnTurnRight, false,
                              fromRight ? StartFromRight[i] : StartFromLeft[i], true);
                    // the far side faces the eye at once, the rest at yaw 0 until they arrive
                    if (fromRight ? slot > 2 : slot < 2) FaceCamera(i, true);
                    motion[i] = 1;
                    act[i] = Act.Arriving;
                    if (!dummy) AnyRunningOn = true;                       // AE_LineupButton_Update's state 1 (the music's timer 1112)
                    return;

                case Act.Arriving:
                    if (motion[i] == 0) act[i] = Act.Standing;
                    return;

                case Act.Standing:
                    if (chosen >= 0) { act[i] = Act.Chosen; return; }
                    if (!dummy && i == focus)
                    {
                        // state 2 -> 3: into the float, then floating; the beam lands with sfxBeamSelect
                        v = Actor(i);
                        v.ClearQueue();
                        v.Request(EditorAnimations.PickMeIntoFloat, false);
                        v.Request(EditorAnimations.PickMeFloat, true);
                        act[i] = Act.Floating;
                        Sounds.Play("beam_select.xma");
                        Music.Activity();
                    }
                    else PickMe(i, dt);
                    return;

                case Act.Floating:
                    if (i != focus)
                    {
                        v = Actor(i);
                        v.ClearQueue();
                        v.Request(EditorAnimations.PickMeOutOfFloat, false);   // state 3 -> 2
                        act[i] = Act.Standing;
                    }
                    return;

                case Act.RunningOff:
                    if (delay[i] > 0f)
                    {
                        delay[i] -= dt;
                        if (delay[i] > 0f) return;
                        // run off on Next (62) or Previous (61), facing forward again
                        v = Actor(i);
                        if (v != null)
                        {
                            v.ClearQueue();
                            v.Request(fromRight ? EditorAnimations.RunOffTurnRight : EditorAnimations.RunOffTurnLeft, false);
                        }
                        targetYaw[i] = 0f; turnSpeed[i] = 0f;
                        motion[i] = 1;
                        return;
                    }
                    if (motion[i] != 0) return;
                    if (--leaving == 0) lastOut = true;
                    Destroy(manifest[i]);
                    manifest[i] = null;
                    // the one that will take back the avatar crossing to its side
                    skipRunOn[i] = fromRight ? slot == (dummy ? -1 : 0) : slot == (dummy ? 5 : 4);
                    act[i] = Act.Gone;
                    return;

                case Act.Crossing:
                    if (delay[i] > 0f)
                    {
                        delay[i] -= dt;
                        if (delay[i] > 0f) return;
                        int id; Vector3 to; float degrees;
                        if (!dummy)
                        {
                            id = fromRight ? EditorAnimations.CrossToBackRight : EditorAnimations.CrossToBackLeft;
                            to = fromRight ? dummyLeft : dummyRight;
                            degrees = fromRight ? CrossDegrees : -CrossDegrees;
                        }
                        else
                        {
                            // unk_92326D70 / unk_92326D80: AvatarButton1's / AvatarButton5's spot
                            id = fromRight ? EditorAnimations.CrossToFrontRight : EditorAnimations.CrossToFrontLeft;
                            to = fromRight ? ButtonSpot[0] : ButtonSpot[4];
                            degrees = fromRight ? -CrossDegrees : CrossDegrees;
                        }
                        v = Actor(i);
                        if (v != null) { v.ClearQueue(); v.Request(id, false); }
                        dest[i] = to;
                        targetYaw[i] = MathHelper.ToRadians(degrees); turnSpeed[i] = 0f;
                        motion[i] = 1;
                        return;
                    }
                    if (motion[i] != 0) return;
                    if (--leaving == 0) lastOut = true;
                    skipRunOn[i] = false;
                    act[i] = Act.Gone;
                    return;

                case Act.Gone:
                    // the middle three come back at once; the ends and the dummies once all have gone
                    bool wait = dummy || slot == 0 || slot == 4;
                    if (wait && (leaving != 0 || lastOut)) return;
                    if (skipRunOn[i])
                    {
                        // the avatar that crossed to this side is this button's now
                        manifest[i] = PickManifest(slot);
                        basePos[i] = dest[i] = spot[i];
                        FaceCamera(i, true);
                    }
                    else manifest[i] = null;
                    act[i] = Act.Waiting;
                    pickTimer[i] = rng.Next(200) * 0.1f;
                    delay[i] = 0f;
                    return;
            }
        }

        /// <summary>AE_Lineup_PickManifest (0x920B0498): entry 5 page + slot, wrapping.</summary>
        private byte[] PickManifest(int slot)
        {
            if (lineup.Count == 0) return null;
            int e = 5 * page + slot;
            if (e < 0) e = lineup.Count - 1; else if (e >= lineup.Count) e = 0;
            return lineup[e];
        }

        /// <summary>AE_Lineup_FaceCamera: the facing at the destination is the target (and the yaw, if immediate).</summary>
        private void FaceCamera(int i, bool immediate)
        {
            float f = FacingAt(dest[i]);
            targetYaw[i] = f;
            if (immediate) yaw[i] = f;
        }

        /// <summary>AE_Actor_RootOffset: the actor's root (x, z) in console space, unscaled on a cross-over if asked.</summary>
        private Vector3 Root(int i, AvatarView v, bool scaled)
        {
            Vector3 t = v.RootTranslation;
            Vector3 c = new Vector3(t.X, 0f, -t.Z);
            if (scaled && act[i] == Act.Crossing) c /= HeightScale(manifest[i]);
            return c;
        }

        /// <summary>AE_Lineup_Motion (0x920B0EA8): the measurement the state waits on, then the turn.</summary>
        private void Motion(int i, AvatarView v, float dt)
        {
            if (motion[i] == 2)
            {
                Vector3 c = Root(i, v, true);
                bool near = c.X * c.X + c.Z * c.Z < ArriveRadius * ArriveRadius;
                if (act[i] == Act.Arriving && near)
                {
                    motion[i] = 0; FaceCamera(i, false); turnSpeed[i] = 0f;
                    if (arrivalLogged.Add(HeightScale(manifest[i])))
                    {
                        // the root is the table track's entry 0 times the root joint's scale (AvatarView.TableRoot)
                        Vector3 j = v.RootJoint, k = v.RootJointScale, t = v.RootTranslation;
                        Console.WriteLine("[studio] lineup: AvatarButton" + i + " arrived, root (table entry 0) " + Fmt(t) + ", root joint " + Fmt(j)
                            + ", root joint scale " + k.X.ToString("0.000") + " (height scale " + HeightScale(manifest[i]).ToString("0.00") + ")");
                    }
                }
                else if (act[i] == Act.RunningOff)
                {
                    float x = spot[i].X + c.X;
                    if (fromRight ? x < ExitLeft.X : x > ExitRight.X)
                    {
                        motion[i] = 0;
                        basePos[i] = dest[i] = fromRight ? ExitLeft : ExitRight;
                    }
                }
                else if (act[i] == Act.Crossing && crossArmed[i] && near)
                {
                    motion[i] = 0; FaceCamera(i, false); turnSpeed[i] = 0f; crossArmed[i] = false;
                    if (AnimLog) Console.WriteLine("[studio] lineup: AvatarButton" + i + " crossed over, drawn root " + Fmt(DrawnRoot(i, v)) + ", its spot " + Fmt(dest[i]));
                }
            }
            else if (motion[i] == 1)
            {
                motion[i] = 2;
                if (act[i] == Act.Crossing) crossArmed[i] = false;
            }
            float d = targetYaw[i] - yaw[i];
            if (d * d < 0.000001f) return;
            turnSpeed[i] = MathHelper.Clamp(turnSpeed[i] + TurnTop / TurnRamp * dt, 0f, TurnTop);
            float step = Math.Min(Math.Abs(d), turnSpeed[i] * dt);
            yaw[i] += d > 0f ? step : -step;
        }

        /// <summary>The light and the world every timer tick (AE_Actor_SetLighting).</summary>
        private void Place(int i, AvatarView v)
        {
            Vector3 scaledRoot = Root(i, v, true), root = Root(i, v, false);
            Vector3 lit = act[i] == Act.Crossing && motion[i] == 2
                ? Vector3.Transform(scaledRoot, Matrix.CreateRotationY(yaw[i])) : scaledRoot;
            Vector3 p = basePos[i] + lit;
            float t = p.Z > 2f ? MathHelper.Clamp((p.Z - 2f) / 1.5f, 0f, 1f) : 0f;
            v.KeyDirection = Vector3.Transform(LibraryAvatar.DefaultKeyDirection, Matrix.CreateRotationY(Facing(p)));
            Vector4 k = LibraryAvatar.DefaultKeyColour, a = LibraryAvatar.DefaultAmbient;
            // towards (0, 0, 0, 1) (0x92000F60)
            v.KeyColour = new Vector4(k.X * (1f - t), k.Y * (1f - t), k.Z * (1f - t), k.W + (1f - k.W) * t);
            v.AmbientColour = new Vector4(a.X * (1f - t), a.Y * (1f - t), a.Z * (1f - t), a.W + (1f - a.W) * t);
            v.Placement = Record(basePos[i] + (scaledRoot - root), yaw[i]);
        }

        /// <summary>sub_920AFB10: on a cross-over's re-base the base moves to the destination; light and world again.</summary>
        private void Rebase(int i, AvatarView v)
        {
            Vector3 before = AnimLog ? DrawnRoot(i, v, rebasedBy: true) : Vector3.Zero;
            basePos[i] = dest[i];
            Place(i, v);
            crossArmed[i] = true;
            if (AnimLog) Console.WriteLine("[studio] lineup: AvatarButton" + i + " re-based, the drawn root moved " + (DrawnRoot(i, v) - before).Length().ToString("0.0000")
                + " (height scale " + HeightScale(manifest[i]).ToString("0.00") + ", yaw " + MathHelper.ToDegrees(yaw[i]).ToString("0.00") + " degrees, target "
                + MathHelper.ToDegrees(targetYaw[i]).ToString("0.00") + ", before " + Fmt(before) + ", after " + Fmt(DrawnRoot(i, v)) + ")");
        }

        private static readonly bool AnimLog = Environment.GetEnvironmentVariable("AVATAR_STUDIO_ANIMLOG") == "1";

        /// <summary>For the log: where the drawn root stands.</summary>
        private Vector3 DrawnRoot(int i, AvatarView v, bool rebasedBy = false)
        {
            float[] p = v.Placement;
            float s = HeightScale(manifest[i]);
            Vector3 t = v.RootTranslation;
            if (rebasedBy) { Vector3 d; if (RootDisplacement(v.EditorAnimationId, out d)) t += d; }
            Vector3 c = new Vector3(t.X, 0f, -t.Z) * s;
            return new Vector3(p[3], 0f, p[5]) + Vector3.Transform(c, Matrix.CreateRotationY(p[1]));
        }

        /// <summary>AE_Lineup_IdlePickMe (0x920B0390): one standing avatar at a time plays creator_pick_me.</summary>
        private void PickMe(int i, float dt)
        {
            AvatarView v = Actor(i);
            if (v == null) return;
            if (pickPlaying == i)
            {
                if (v.Resting) pickPlaying = -1;
            }
            else if (pickPlaying < 0 && pickLast != i)
            {
                pickTimer[i] -= dt;
                if (pickTimer[i] < 0f)
                {
                    if (v.Resting)
                    {
                        v.ClearQueue();
                        v.Request(EditorAnimations.PickMe, false);
                        pickLast = pickPlaying = i;
                    }
                    pickTimer[i] = rng.Next(200) * 0.1f;
                }
            }
        }

        // ---- drawing: what the timers queued, each actor once (AE_DrawAllAvatars) ------------------

        private readonly List<AvatarView> drawList = new List<AvatarView>();

        private List<AvatarView> Queued()
        {
            drawList.Clear();
            foreach (int i in TimerOrder)
            {
                AvatarView v = queued[i] ? Actor(i) : null;
                if (v != null && !drawList.Contains(v)) drawList.Add(v);
            }
            return drawList;
        }

        public void BeginFrame() { foreach (AvatarView v in Queued()) v.BeginFrame(); }

        public void Draw(float aspect) { foreach (AvatarView v in Queued()) v.Draw(aspect); }

        public void Dispose()
        {
            foreach (AvatarView v in actors.Values) v.Dispose();
            actors.Clear();
        }
    }

    /// <summary>The focused button's beam: EditorSkin.xur's AvatarSelectButton visual, BlendMode 5.</summary>
    static class LineupBeam
    {
        // EditorStart.xur AvatarButton1..5, top-left
        private static readonly Vector2[] Button =
        {
            new Vector2(203.476f, 220.37f), new Vector2(369.99f, 229.968f), new Vector2(536.5f, 238.368f),
            new Vector2(703.01f, 229.97f), new Vector2(869.52f, 220.37f),
        };

        // beam: frame, opacity, x scale (y scale 1.2 throughout)
        private static readonly float[][] Beam =
        {
            new[] { 0f, 0f, 0.6f }, new[] { 1f, 0f, 0f }, new[] { 10f, 1f, 0.6f }, new[] { 20f, 1f, 0.8f },
            new[] { 30f, 1f, 0.75f }, new[] { 60f, 1f, 0.7f }, new[] { 90f, 1f, 0.75f }, new[] { 120f, 1f, 0.7f },
            new[] { 150f, 1f, 0.75f }, new[] { 180f, 1f, 0.7f }, new[] { 210f, 1f, 0.75f }, new[] { 240f, 1f, 0.7f },
            new[] { 270f, 1f, 0.75f },
        };

        // particles: frame, opacity, x scale, y scale, x, y
        private static readonly float[][][] Star =
        {
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 115f, -25f }, new[] { 1f, 0f, 0f, 0f, 115f, -25f }, new[] { 30f, 0.6f, 0.7f, 0.7f, 115f, -25f },
                    new[] { 80f, 0f, 0.7f, 5f, 115f, -200f }, new[] { 165f, 0f, 0.7f, 0.7f, 115f, -25f }, new[] { 172f, 0.7f, 0.7f, 0.7f, 115f, -25f },
                    new[] { 270f, 0f, 0.7f, 5f, 115f, -150f } },
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 75f, -120f }, new[] { 1f, 0f, 0f, 0f, 75f, -120f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 75f, -120f },
                    new[] { 50f, 0f, 0.606667f, 0.606667f, 75f, -120f }, new[] { 56f, 0.6f, 0.7f, 0.7f, 75f, -120f }, new[] { 106f, 0f, 0.7f, 5f, 75f, -259f } },
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 97f, -100f }, new[] { 1f, 0f, 0f, 0f, 97f, -100f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 97f, -100f },
                    new[] { 98f, 0f, 0.606667f, 0.606667f, 97f, -100f }, new[] { 104f, 0.6f, 0.7f, 0.7f, 97f, -100f }, new[] { 170f, 0f, 0.7f, 5f, 97f, -275f } },
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 99f, -93f }, new[] { 1f, 0f, 0f, 0f, 99f, -93f }, new[] { 30f, 0f, 0.063636f, 0.063636f, 99f, -93f },
                    new[] { 210f, 0f, 0.666507f, 0.666507f, 99f, -93f }, new[] { 220f, 0.6f, 0.7f, 0.7f, 99f, -93f }, new[] { 270f, 0f, 0.7f, 5f, 99f, -258f } },
            new[] { new[] { 0f, 0f, 0f, 0f, 60f, -50f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 60f, -50f }, new[] { 39f, 0f, 0.606667f, 0.606667f, 60f, -50f },
                    new[] { 45f, 0.6f, 0.7f, 0.7f, 60f, -50f }, new[] { 120f, 0f, 0.7f, 5f, 60f, -241f } },
            new[] { new[] { 0f, 0f, 0f, 0f, 72f, -130f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 72f, -130f }, new[] { 74f, 0f, 0.606667f, 0.606667f, 72f, -130f },
                    new[] { 80f, 0.6f, 0.7f, 0.7f, 72f, -130f }, new[] { 195f, 0f, 0.7f, 5f, 72f, -279f } },
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 105f, -100f }, new[] { 1f, 0f, 0f, 0f, 105f, -100f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 105f, -100f },
                    new[] { 168f, 0f, 0.606667f, 0.606667f, 105f, -100f }, new[] { 174f, 0.6f, 0.7f, 0.7f, 105f, -100f }, new[] { 224f, 0f, 0.7f, 5f, 105f, -250f } },
            new[] { new[] { 0f, 0f, 0.7f, 0.7f, 80f, -106f }, new[] { 1f, 0f, 0f, 0f, 80f, -106f }, new[] { 30f, 0f, 0.295556f, 0.295556f, 80f, -106f },
                    new[] { 190f, 0f, 0.606667f, 0.606667f, 80f, -106f }, new[] { 196f, 0.6f, 0.7f, 0.7f, 80f, -106f }, new[] { 259f, 0f, 0.7f, 5f, 80f, -274.5f } },
        };

        /// <summary>The visual's frame `seconds` after the focus: 2..30, then 30..270 looping.</summary>
        public static float Frame(float seconds)
        {
            float f = 2f + seconds * 60f;
            return f < 270f ? f : 30f + (f - 30f) % 240f;
        }

        /// <summary>Linear between keys; before the first or after the last, that key's values.</summary>
        private static float[] At(float[][] keys, float frame)
        {
            if (frame <= keys[0][0]) return keys[0];
            for (int k = 1; k < keys.Length; k++)
                if (frame <= keys[k][0])
                {
                    float[] a = keys[k - 1], b = keys[k];
                    float t = (frame - a[0]) / (b[0] - a[0]);
                    float[] r = new float[a.Length];
                    for (int j = 0; j < a.Length; j++) r[j] = a[j] + (b[j] - a[j]) * t;
                    return r;
                }
            return keys[keys.Length - 1];
        }

        public static readonly BlendState Additive = new BlendState     // mode 5 on premultiplied art
        {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };

        /// <summary>Draw slot 0..4's beam (a SpriteBatch begun with Additive).</summary>
        public static void Draw(SpriteBatch batch, TileArt art, int slot, float seconds)
        {
            if (slot < 0 || slot >= Button.Length) return;
            Texture2D beam = art.Named("avatar_selection_beam"), star = art.Named("bg_star");
            float f = Frame(seconds);
            Vector2 o = Button[slot];
            if (beam != null)
            {
                float[] b = At(Beam, f);
                float w = 256f * b[2], h = 508f * 1.2f;
                Vector2 p = o + new Vector2(-27.2f + 128f - 128f * b[2], -263.5f);
                batch.Draw(beam, new Rectangle((int)Math.Round(p.X), (int)Math.Round(p.Y), (int)Math.Round(w), (int)Math.Round(h)), Color.White * b[1]);
            }
            if (star != null)
                foreach (float[][] keys in Star)
                {
                    float[] s = At(keys, f);
                    if (s[1] <= 0f || s[2] <= 0f || s[3] <= 0f) continue;
                    Vector2 c = o + new Vector2(s[4] + 16f, s[5] + 16f);
                    batch.Draw(star, c, null, Color.White * s[1], 0f, new Vector2(star.Width / 2f, star.Height / 2f),
                               new Vector2(32f * s[2] / star.Width, 32f * s[3] / star.Height), SpriteEffects.None, 0f);
                }
        }
    }
}
