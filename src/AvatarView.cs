// An avatar on screen (renderer, animations, framing, spin), and the thumbnail cache for the grids.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using AvatarStudio.Avatars;
using Microsoft.Xna.Framework.Graphics;
using JointPose = AvatarStudio.Avatars.Pose;

namespace AvatarStudio
{
    enum Framing { FullBody, Head, Upper, Legs, Feet }

    sealed class AvatarView : IDisposable
    {
        private readonly GraphicsDevice device;
        private AvatarRenderer current, pending;
        private AvatarAnimation anim;
        private AvatarAnimationPreset preset;
        private readonly Random rng = new Random();
        private bool female;
        private float idleTimer;

        private float headY = ReferenceHeadHeight;

        /// <summary>The LT / RT turntable's s, -1..1; the avatar turns pi * s.</summary>
        public float Spin;
        private float spinSpeed, snapBack;
        public float Yaw { get { return MathHelper.Pi * Spin; } }
        /// <summary>The spin in the avatar's world: none while the editor's ring plays (TurnSlot turns the pose).</summary>
        private float WorldSpin { get { return EditorMode && ring.Count > 0 ? 0f : Yaw; } }
        public Framing Framing = Framing.FullBody;
        public Vector2 ScreenOffset;            // NDC shift of the whole avatar
        public float Scale = 1f;

        /// <summary>The editor screen (EditorCamera id) the avatar stands on, or -1 for the studio's own framings.</summary>
        public int Screen
        {
            get { return screen; }
            set
            {
                screen = value;
                reframePending = false;
                if (value == EditorCamera.Earrings || value == EditorCamera.Wristwear || value == EditorCamera.Rings) return;
                cameraScreen = value;
                mirrored = false;
                snapBack = SnapBackSeconds;
            }
        }
        private int screen = -1, cameraScreen = -1;
        private readonly float[] camNow = new float[6], camTarget = new float[6];
        private bool camPlaced;

        /// <summary>An earring, wristwear or ring cell took the focus with its item worn: reframe once loaded.</summary>
        public void FocusItem(Guid item, bool ring)
        {
            reframeItem = item; reframeRing = ring; reframePending = true;
        }
        private Guid reframeItem; private bool reframeRing, reframePending, mirrored;
        public bool Mirrored { get { return mirrored; } }
        public static float LastMirrorX = float.NaN;   // the selftest logs the x the last test read
        public bool ReframePending { get { return reframePending; } }

        /// <summary>AE_ItemMirrorTest (sub_920DF5D0): whether the item's side calls for the mirrored camera.</summary>
        private static bool MirrorTest(AvatarRenderer r, Guid item, bool ring)
        {
            try
            {
                Avatar a = Parsed(r);
                if (a == null) return false;
                foreach (AvatarComponent c in a.Models)
                {
                    if (c.AssetId != item || c.TriangleBatches.Length == 0) continue;
                    Vector3[] v = c.TriangleBatches[0].Vertices.RawPositions;
                    if (v.Length == 0) return false;
                    LastMirrorX = v[0].X;
                    return ring == (v[0].X >= 0f);
                }
            }
            catch (Exception e) { Console.WriteLine("[studio] mirror test: " + e.Message); }
            return false;
        }

        // ---- the carryable ----------------------------------------------------------------------------

        /// <summary>The loader's built avatar, or null.</summary>
        private static Avatar Parsed(AvatarRenderer r) { return r == null ? null : r.Avatar; }

        private Guid carryId;                   // the carryable of the avatar drawn (B+4's GUID)
        private bool carryKnown;                // an avatar has been drawn since the view began
        private KeyframeAnimation carryOwn;   // its own block 1
        private byte[] carryLink;               // else its linked animation's STRB

        /// <summary>AE_Actor_CarryableHasAnimation (sub_920C7B28): the carryable has an animation.</summary>
        public bool CarryableHasAnimation { get { return carryOwn != null || carryLink != null; } }

        /// <summary>The state below the top is Props (28): the idle's Over Props rule.</summary>
        public bool OverProps;

        /// <summary>The carryable's animation as XAM finds it (xam 0x81966C98): its data's first Animation block.</summary>
        private void FindCarryableAnimation(Avatar a)
        {
            carryOwn = null; carryLink = null;
            if (a == null || a.Carryable == null) return;
            if (a.Carryable.Animation != null) { carryOwn = a.Carryable.Animation; return; }
            TocEntry t = a.Carryable.Model == null ? null : AvatarToc.Find(a.Carryable.Model.AssetId);
            if (t == null) return;
            foreach (Guid link in new[] { t.Linked, t.Linked2 })
            {
                if (link == Guid.Empty) continue;
                byte[] b = TocAssets.Get(link);
                if (b == null) continue;
                using (AvatarAnimation probe = EditorAnimations.FromAsset(b))
                    if (probe != null) { carryLink = b; return; }
            }
        }

        /// <summary>Procedural id 3: a fresh copy of the carryable's animation, or null.</summary>
        private AvatarAnimation CarryableAnimation()
        {
            if (carryLink != null) return EditorAnimations.FromAsset(carryLink);
            if (carryOwn == null) return null;
            return AvatarAnimation.FromClip(carryOwn);
        }

        /// <summary>The carryable's joints this frame, or null with nothing to pose.</summary>
        private IList<Matrix> CarryablePose()
        {
            if (lib == null || !lib.HasCarryable || !EditorMode || ring.Count == 0) return null;
            return carryBones;
        }

        /// <summary>Whether the carryable is drawn this frame.</summary>
        public bool CarryableDrawn { get { return lib != null && lib.CarryableVisible; } }
        /// <summary>The newest animation's length and cursor, in seconds.</summary>
        public float AnimationSeconds
        {
            get
            {
                if (EditorMode && ring.Count > 0) return ring[ring.Count - 1].Clip.Length;
                return anim == null ? 0f : (float)anim.Length.TotalSeconds;
            }
        }
        public float AnimationAt
        {
            get
            {
                if (EditorMode && ring.Count > 0) return ring[ring.Count - 1].Clip.Time;
                return anim == null ? 0f : (float)anim.CurrentPosition.TotalSeconds;
            }
        }
        public bool HasCarryable { get { return lib != null && lib.HasCarryable; } }

        private const float SnapBackSeconds = 1.2f;   // 0x92006C18, into behaviour object +4
        private float heightFactor;
        public bool Celebrate;                  // lineup: the chosen one raises its arms

        /// <summary>The lineup's actors: a record of their own instead of a screen's, through the lineup's projection.</summary>
        public float[] Placement
        {
            get { return placement; }
            set { placement = value; if (value != null) { Array.Copy(value, camNow, 6); camPlaced = true; } }
        }
        private float[] placement;
        public Vector4 KeyColour = LibraryAvatar.DefaultKeyColour, AmbientColour = LibraryAvatar.DefaultAmbient;
        public Vector3 KeyDirection = LibraryAvatar.DefaultKeyDirection;
        private Matrix Proj { get { return Placement != null ? EditorCamera.LineupProjection : EditorCamera.Projection; } }
        public bool Ready { get { return current != null && current.State == AvatarRendererState.Ready; } }

        // the console's own avatar renderer, the only one: without its shaders no avatar is drawn
        private static LibraryShaders libShaders;
        private static bool libTried;
        private LibraryAvatar lib;
        private bool libFrame;                  // this frame's shadow pass ran (BeginFrame)
        public bool UsesLibrary { get { return lib != null; } }

        public AvatarView(GraphicsDevice device) { this.device = device; }

        private LibraryShaders Shaders() { return SharedShaders(device); }

        /// <summary>The library's shaders, loaded once for every view and the gallery's pictures, or null.</summary>
        internal static LibraryShaders SharedShaders(GraphicsDevice device)
        {
            if (!libTried) { libTried = true; libShaders = LibraryShaders.TryLoad(device); }
            return libShaders;
        }

        public void Set(byte[] manifest, bool isFemale)
        {
            female = isFemale;
            heightFactor = EditorCamera.HeightFactor(manifest);
            if (pending != null) pending.Dispose();
            pending = new AvatarRenderer(new AvatarDescription(manifest));
            if (anim == null) StartAnimation(AvatarAnimationPreset.Stand0);
        }

        private void StartAnimation(AvatarAnimationPreset p)
        {
            if (anim != null) anim.Dispose();
            preset = p;
            anim = Presets.Create(p);                    // null when its asset is not to be had: nothing poses
            if (anim != null) anim.CurrentPosition = TimeSpan.Zero;
        }

        public void Update(float dt)
        {
            // promote the pending renderer once ready; draw the old one until then
            if (pending != null)
            {
                AvatarRendererState s = pending.State;
                if (s == AvatarRendererState.Ready)
                {
                    if (current != null) current.Dispose();
                    current = pending; pending = null;
                    headY = HeadHeight(current);
                    if (lib != null) { lib.Dispose(); lib = null; }
                    LibraryShaders ls = Shaders();
                    if (ls != null) lib = LibraryAvatar.TryBuild(ls, current);
                    if (lib != null && Placement != null) lib.UseOwnShadowMap();
                    OnLoaded();
                }
                else if (s == AvatarRendererState.Unavailable)
                {
                    Console.WriteLine("[studio] avatar renderer became Unavailable; keeping the previous one");
                    pending.Dispose(); pending = null;
                }
            }
            if (current != null) { AvatarRendererState s = current.State; }   // keep polling (conversion)

            if (reframePending && pending == null && current != null && current.State == AvatarRendererState.Ready)
            {
                mirrored = MirrorTest(current, reframeItem, reframeRing);
                cameraScreen = screen;
                snapBack = SnapBackSeconds;
                reframePending = false;
                if (AnimLog) Console.WriteLine("[studio] item reframe" + (mirrored ? ", mirrored" : ""));
            }
            if (Placement != null) { Array.Copy(Placement, camNow, 6); camPlaced = true; }
            else if (Screen >= 0 && EditorCamera.Has(cameraScreen))
            {
                EditorCamera.Target(cameraScreen, heightFactor, camTarget, mirrored);
                if (!camPlaced) { Array.Copy(camTarget, camNow, 6); camPlaced = true; }
                else EditorCamera.Ease(camNow, camTarget, EditorCamera.Blend(dt));
            }
            else camPlaced = false;

            if (EditorMode)
            {
                UpdateEditorAnimation(dt);
                // the renderer's root, in the console's space (the data is right-handed)
                Vector3 root = RootTranslation;
                ShadowRoot = new Vector3(root.X, root.Y, -root.Z);
            }
            else
            {
                requests.Clear(); clearQueued = false; ClearList(); ClearRing(); restOn = false; remaining = 0f;
                if (anim != null)
                {
                    anim.Update(TimeSpan.FromSeconds(dt), false);
                    if (anim.CurrentPosition >= anim.Length - TimeSpan.FromMilliseconds(30))
                    {
                        if (Celebrate) StartAnimation(AvatarAnimationPreset.Celebrate);
                        else StartAnimation(NextIdle());
                    }
                }
            }
        }

        // ---- the editor's animations (EditorAnimations.cs) ------------------------------------------

        /// <summary>The id of the newest slot in the blend ring, -1 outside the editor's scheme.</summary>
        private int NewestId { get { return ring.Count == 0 ? -1 : ring[ring.Count - 1].Id; } }
        private struct Req { public int Id; public bool Loop; public float Start; }
        private readonly List<Req> requests = new List<Req>();
        private float idleTime, cooldown = -1f, dizzyCountdown;
        private bool dizzy;

        /// <summary>The lineup's actors have no editor idle; LineupStage plays theirs.</summary>
        public bool NoIdle;

        /// <summary>AE_Actor_IsIdle (0x920F8328): the actor's countdown has run out and no request is pending.</summary>
        public bool Resting { get { return remaining <= 0f && requests.Count == 0; } }

        /// <summary>A new manifest is loading; the old avatar is still the one drawn.</summary>
        public bool Loading { get { return pending != null; } }

        /// <summary>The actor's root in the data's space: the ring's table entry 0 times the root joint's scale.</summary>
        public Vector3 RootTranslation
        {
            get
            {
                if (EditorMode && ring.Count > 0) return TableRoot;
                return anim == null ? Vector3.Zero : Pose[0].Translation;
            }
        }

        /// <summary>The pose drawn: the blend ring's (the editor's scheme), else the preset's.</summary>
        private IList<Matrix> Pose
        {
            get
            {
                if (EditorMode && ring.Count > 0 && bones != null) return bones;
                return anim.BoneTransforms;
            }
        }

        // ---- the rest: the CreatorLook blend by the gaze (RestGaze.cs) ------------------------------

        /// <summary>The editor's own avatar rests in the gaze blend (sub_920C2E60); lineup actors keep id 2.</summary>
        public bool GazeRest;
        /// <summary>The gaze, -1..1 on each axis, as sub_920C25B0 hands it to the rest (+48, +52).</summary>
        public Vector2 Gaze;
        private RestGaze restGaze;
        private bool restGazeTried, restOn;
        /// <summary>The newest slot is the gaze rest (its expression mode applies).</summary>
        public bool RestingInGaze { get { return restOn; } }
        /// <summary>The rest's nine frames and rates, or "none".</summary>
        public string RestShape { get { return restGaze == null ? "none" : restGaze.Shape; } }
        private Vector2 stickSmoothed;     // controller +100192 / +100196

        /// <summary>The pad as AE_Turntable_Update reads it, OR'd over the pads.</summary>
        public struct Pad { public Vector2 Stick; public bool StickMoved, LeftShoulder, RightShoulder, RightThumb, Triggers; }

        /// <summary>The editor avatar's behaviour from the pad, every frame (AE_Turntable_Update 0x920CA618).</summary>
        public void Look(float dt, Pad pad)
        {
            Voice = 0;                              // 0x920CAAB4 / 0x920CAC28, on either branch
            int mode = 0;
            if (modeTimer > 0f) { modeTimer -= dt; mode = modeStored; }
            else if (screen == EditorCamera.EyeShadowColor || screen == 62)
            {
                mode = 7;
                if (rng.Next(200) == 0) { modeStored = 0; modeTimer = rng.Next(50) * 0.01f + 0.2f; }
            }

            Vector2 stick = pad.Stick;
            if (GazeRest && EditorMode && Resting && screen != 3 && cooldown < 0f)
            {
                // the idle count against a fresh rand(20) + 10 each frame, and the look timers, stick at rest
                if (!pad.StickMoved)
                {
                    idleTime += dt;
                    // over Props: past 2 s and only with an animated carryable; elsewhere rand(20) + 10
                    bool pick = OverProps ? idleTime > 2f && CarryableHasAnimation : idleTime > rng.Next(20) + 10;
                    if (!NoIdle && pick) { PickIdle(); idleTime = 0f; }
                    LookTargets(dt, ref stick);
                }
                else idleTime = 0f;
                if (pad.RightThumb) { mode = 9; Burp(); }
                else
                {
                    Gestures();
                    if (gestureBlock <= 0f && (pad.LeftShoulder || pad.RightShoulder))
                        Shoulders(pad.RightShoulder && screen != 50, pad.LeftShoulder && CloseUp,
                                  pad.LeftShoulder && !CloseUp && !pad.StickMoved);
                    else faceChoice = 0;
                }
            }
            else if (GazeRest) idleTime = 0f;
            // the LB face choice, the triggers, LB, RB or the right thumb hold the count at 0
            if (GazeRest && (faceChoice != 0 || pad.Triggers || pad.LeftShoulder || pad.RightShoulder || pad.RightThumb)) idleTime = 0f;
            RestMode = mode;

            // +32: after the close-up gesture the stick is ignored for a second
            if (gestureBlock > 0f) { gestureBlock -= dt; stick = Vector2.Zero; }
            Record(dt, stick);

            float k = Math.Min(dt * 8f, 1f);
            stickSmoothed = k * stick + (1f - k) * stickSmoothed;
            Gaze = new Vector2(Math.Sign(stickSmoothed.X) * Math.Min(Math.Abs(stickSmoothed.X) * 0.000030516647f, 1f),
                               Math.Sign(stickSmoothed.Y) * Math.Min(Math.Abs(stickSmoothed.Y) * 0.000030516647f, 1f));
            if (pad.StickMoved)
            {
                idleTime = 0f;
                if (cooldown - dt < 0f && EditorMode)
                {
                    if (!Resting) ClearQueue();
                    lookCamera = 0f; lookTile = 0f;                    // +100200 / +100204
                }
            }
            // sub_920C9190: the state the look timers belong to; a screen change stops them
            lookState = screen;
        }

        // ---- the look timers (sub_920C87E8 starts them, sub_920C9AE8 runs them) ------------------

        private float lookCamera, lookTile;      // +100200 / +100204, seconds left
        private int lookState = -1;              // +100220

        /// <summary>The focused tile's centre on screen, set by the screen each frame, or null.</summary>
        public Vector2? FocusedTile;

        /// <summary>sub_920C9AE8: while a look timer runs, the gaze turns to the camera or the focused tile.</summary>
        private void LookTargets(float dt, ref Vector2 stick)
        {
            if (!(lookTile > 0f || lookCamera > 0f) || lookState != screen) { lookTile = 0f; lookCamera = 0f; return; }
            float yaw = 0f, pitch = 0f;
            float hf = (heightFactor + 1f) * 0.10000002f + 0.89999998f;
            Matrix world = Matrix.CreateFromYawPitchRoll(camNow[1], camNow[0], camNow[2]) * Matrix.CreateTranslation(camNow[3], camNow[4], camNow[5]);
            Matrix head = Matrix.CreateTranslation(0f, 1.2f * hf, 0f) * world;
            if (lookTile > 0f)
            {
                if (screen != 68 && FocusedTile.HasValue && camPlaced)
                {
                    float nx = (FocusedTile.Value.X - 640f) / 640f, ny = (FocusedTile.Value.Y - 360f) / 360f;
                    float depth = screen >= 69 && screen <= 72 ? 2f : 1f;
                    AngleTo(new Vector3(0.5f + nx, 0.75f - ny, depth - 2.5f), head, out yaw, out pitch);
                    yaw -= Spin * 3.1415927f;
                }
                lookTile -= dt;
            }
            if (lookCamera > 0f)
            {
                if (camPlaced)
                {
                    AngleTo(new Vector3(0.5f, 0.75f, -2.5f), head, out yaw, out pitch);
                    yaw -= Spin * 3.1415927f;
                }
                lookCamera -= dt;
            }
            double off = Math.Acos(Math.Cos(yaw) * Math.Cos(pitch));
            stick = off >= 1.134464 ? Vector2.Zero : new Vector2(yaw * -0.88147354f * 32769f, pitch * -0.88147354f * 32769f);
            LookYawPitch = new Vector2(yaw, pitch);
            history.Clear();
        }

        /// <summary>Start one of the look timers as the idle pick does.</summary>
        public void StartLook(bool camera, float seconds) { if (camera) lookCamera = seconds; else lookTile = seconds; }

        /// <summary>The last look's (yaw, pitch), radians (the selftest logs it).</summary>
        public Vector2 LookYawPitch;
        public bool Looking { get { return lookCamera > 0f || lookTile > 0f; } }

        /// <summary>AE_AngleToCamera (0x920FCB40): the yaw and pitch from the head to `target`, console space.</summary>
        private static void AngleTo(Vector3 target, Matrix head, out float yaw, out float pitch)
        {
            Vector3 d = target - head.Translation;
            Vector3 dxz = new Vector3(d.X, 0f, d.Z), dyz = new Vector3(0f, d.Y, d.Z);
            float s = Between(dxz, new Vector3(-head.M11, 0f, -head.M13)) >= 1.5707964f ? -1f : 1f;
            yaw = s * Between(dxz, new Vector3(-head.M31, 0f, -head.M33));
            float s2 = Between(dyz, new Vector3(0f, head.M22, head.M23)) >= 1.5707964f ? 1f : -1f;
            pitch = s2 * Between(dyz, new Vector3(0f, -head.M32, -head.M33));
        }

        /// <summary>XMVector3AngleBetweenVectors: acos of the cosine, clamped to -1..1.</summary>
        private static float Between(Vector3 a, Vector3 b)
        {
            double c = Vector3.Dot(a, b) / Math.Sqrt((double)a.LengthSquared() * b.LengthSquared());
            return (float)Math.Acos(Math.Max(-1.0, Math.Min(1.0, c)));
        }

        // ---- the right stick's gestures (sub_920C7EA0 records, sub_920C7F98 matches) ----------------

        private struct Step { public int Code; public float Dwell; }
        private readonly List<Step> history = new List<Step>();   // +100484.., newest first, at most 32
        private float gestureBlock;                                // +32

        /// <summary>sub_920C7EA0: each frame, the 3 x 3 cell the stick is in, recorded with its dwell.</summary>
        private void Record(float dt, Vector2 v)
        {
            int code = 4;
            if (v.X < -12000f) code = 3;
            if (v.X > 12000f) code++;
            if (v.Y > 12000f) code -= 3;
            if (v.Y < -12000f) code += 3;
            if (history.Count > 0 && history[0].Code == code)
            {
                Step s = history[0]; s.Dwell += dt; history[0] = s;
                return;
            }
            history.Insert(0, new Step { Code = code, Dwell = 0f });
            if (history.Count > 32) history.RemoveAt(32);
        }

        /// <summary>sub_920C7950: entry i against a pattern element (a column, a row or a cell), within maxTime.</summary>
        private bool Is(int i, int element, float maxTime)
        {
            if (i >= history.Count) return false;
            int c = history[i].Code;
            bool ok = element >= 9 && element <= 11 ? c % 3 == element - 9
                    : element >= 12 && element <= 14 ? c / 3 == element - 12
                    : c == element;
            return ok && (maxTime == 0f || history[i].Dwell <= maxTime);
        }

        /// <summary>sub_920C7A58: the pattern from entry `start` on, or (reversible) backwards.</summary>
        private bool Matches(int start, int[] pattern, float maxTime)
        {
            bool fwd = true;
            for (int k = 0; k < pattern.Length && fwd; k++) fwd = Is(start + k, pattern[k], maxTime);
            if (fwd) return true;
            for (int k = 0; k < pattern.Length; k++) if (!Is(start + pattern.Length - 1 - k, pattern[k], maxTime)) return false;
            return true;
        }

        private bool Col(int i, int v) { return i < history.Count && history[i].Code % 3 == v; }
        private bool Row(int i, int v) { return i < history.Count && history[i].Code / 3 == v; }
        private float Dwell(int i) { return history[i].Dwell; }

        private static readonly int[] Shake = { 9, 10, 11, 10, 9, 10, 11 }, Nod = { 12, 13, 14, 13, 12, 13, 14 };
        private static readonly int[] Circle = { 2, 1, 0, 3, 6, 7, 8, 5 };

        /// <summary>sub_920C7F98: the stick's gestures at rest (a shake, a spin, a flick), from the history.</summary>
        private void Gestures()
        {
            int a = history.Count > 0 && history[0].Code == 4 && history[0].Dwell <= 1f ? 1 : 0, b = a + 1, o = a + 2;
            if (Matches(a, Shake, 1f)) { modeTimer = 1f; modeStored = 2; }
            if (Matches(a, Nod, 1f)) { modeTimer = 1f; modeStored = 1; }
            bool circles = true;
            for (int k = 0; k < 3 && circles; k++) circles = Matches(a + 8 * k, Circle, 0.5f);
            if (circles) { modeStored = 10; modeTimer = 4f; }

            if ((Col(o, 2) && !(Dwell(o) <= 1f) && Col(b, 1) && Dwell(b) <= 0.1f && Col(a, 0))
                || (Col(b, 2) && !(Dwell(b) <= 1f) && Col(a, 0)))
            { Request(EditorAnimations.SpinMirrored, false); cooldown = 1.2f; history.Clear(); Voice = AvatarVoice.Spin; }
            if ((Col(o, 0) && !(Dwell(o) <= 1f) && Col(b, 1) && Dwell(b) <= 0.1f && Col(a, 2))
                || (Col(b, 0) && !(Dwell(b) <= 1f) && Col(a, 2)))
            { Request(EditorAnimations.Spin, false); cooldown = 1.2f; history.Clear(); Voice = AvatarVoice.Spin; }

            if (!CloseUp) return;
            bool flick = (Row(o, 0) && !(Dwell(o) <= 0.5f) && Row(b, 1) && Dwell(b) <= 0.1f && Row(a, 2) && !(Dwell(a) <= 0.125f))
                      || (Row(b, 0) && !(Dwell(b) <= 0.5f) && Row(a, 2) && !(Dwell(a) <= 0.125f));
            if (!flick) return;
            float facing = Yaw + camNow[1];
            if (facing > -1.0995574f && facing < 1.0995574f)
            {
                gestureBlock = 1f; cooldown = 1.2f; modeStored = 10; modeTimer = 2f;
                Voice = AvatarVoice.Bonk;
            }
            history.Clear();
        }

        /// <summary>The behaviour's +40, the avatar's voice: 8 a spin, 9 the flick, 10 dizzy, 1..7 a burp.</summary>
        public int Voice;

        // AE_Behaviour_Burp's tables per body: bottom row, left, centre, right, top row
        private static readonly int[] FemaleBurps = { 5, 4, 3, 2, 1 }, MaleBurps = { 7, 6, 5, 4, 3 };

        /// <summary>AE_Behaviour_Burp (0x920C8A38): with the right thumb held, the stick's last cell picks the burp.</summary>
        private void Burp()
        {
            if (history.Count == 0) return;
            int c = history[0].Code;
            int i = c / 3 == 0 ? 4 : c / 3 == 2 ? 0 : c == 4 ? 2 : c == 3 ? 1 : c == 5 ? 3 : -1;
            if (i >= 0) Voice = (female ? FemaleBurps : MaleBurps)[i];
        }

        // sub_920C8BC0's tables: the LB faces, and the reactions per body
        private static readonly int[] FaceModes = { 1, 2, 3, 4, 5, 6, 7, 10 };
        private static readonly int[] FemaleReactions = { 36, 37, 38, 39, 40 }, MaleReactions = { 46, 47, 48, 49, 50 };
        private int faceChoice;          // +100176

        /// <summary>sub_920C8BC0: RB opens the mouth while held; LB holds a random face on a close-up, else reacts.</summary>
        private void Shoulders(bool mouth, bool face, bool body)
        {
            if (mouth) { modeStored = 8; modeTimer = 0.01f; }
            else if (face)
            {
                if (faceChoice == 0)
                {
                    faceChoice = FaceModes[rng.Next(FaceModes.Length)];
                    Sounds.Play("Emote_trigger.xma");
                }
                modeStored = faceChoice; modeTimer = 1f;
            }
            else if (body)
            {
                int[] list = female ? FemaleReactions : MaleReactions;
                Request(list[rng.Next(list.Length)], false);
                Sounds.Play("Emote_trigger.xma");
                cooldown = 0.2f;
            }
        }

        /// <summary>The face close-ups (sub_920C9190): the avatar's origin within 1.75 of the eye.</summary>
        public bool CloseUp
        {
            get
            {
                if (!camPlaced) return false;
                float x = camNow[3] - 0.5f, y = camNow[4] - 0.75f, z = camNow[5] + 2.5f;
                return x * x + y * y + z * z < 3.0625f;
            }
        }

        /// <summary>The rest's expression mode (see Look).</summary>
        public int RestMode;
        private float modeTimer;
        private int modeStored;
        private int lastMode = -1, eyeStep;
        private float eyeClock;

        // sub_920AC900's switch: each mode's five expression ints (mouth, brows, eyes), -1 kept
        private static readonly int[][] ModeFaces =
        {
            null,
            new[] { 6, 4, 4, 6, 6 }, new[] { 2, 2, 2, 2, 2 }, new[] { 6, 0, 0, 6, 6 }, new[] { 1, 1, 1, 1, 1 },
            new[] { 3, 3, 3, 3, 3 }, new[] { 5, 3, 4, 5, 3 }, new[] { -1, -1, -1, 13, 13 }, new[] { 8, -1, -1, -1, -1 },
            new[] { 7, -1, -1, -1, -1 }, new[] { 3, 1, 1, -1, -1 },
        };
        private static readonly int[] Mode10Left = { 9, 11, 10, 12 }, Mode10Right = { 11, 10, 12, 9 };

        /// <summary>The expression drawn: the newest slot's, or the rest's mode in the gaze rest.</summary>
        private AvatarExpression Expression
        {
            get
            {
                AvatarExpression e;
                if (!(EditorMode && ring.Count > 0)) e = anim.Expression;
                else if (restOn) e = restGaze.Expression;
                else e = ring[ring.Count - 1].Clip.Expression;
                if (!restOn || RestMode <= 0 || RestMode >= ModeFaces.Length) return e;
                int[] f = ModeFaces[RestMode];
                int leftEye = f[3], rightEye = f[4];
                if (RestMode == 10) { leftEye = Mode10Left[eyeStep & 3]; rightEye = Mode10Right[eyeStep & 3]; }
                if (f[0] >= 0) e.Mouth = (AvatarMouth)f[0];
                if (f[1] >= 0) e.LeftEyebrow = (AvatarEyebrow)f[1];
                if (f[2] >= 0) e.RightEyebrow = (AvatarEyebrow)f[2];
                if (leftEye >= 0) e.LeftEye = SwapEye((AvatarEye)leftEye);
                if (rightEye >= 0) e.RightEye = (AvatarEye)rightEye;
                return e;
            }
        }

        private static AvatarEye SwapEye(AvatarEye a)
        {
            return a == AvatarEye.LookLeft ? AvatarEye.LookRight : a == AvatarEye.LookRight ? AvatarEye.LookLeft : a;
        }

        /// <summary>Mode 10's eye cycle, per rest evaluation (sub_920AC900 case 10, sub_920AC670).</summary>
        private void StepModeClock(float dt)
        {
            if (RestMode != lastMode) { lastMode = RestMode; eyeStep = 0; eyeClock = 0f; }
            if (RestMode != 10) return;
            eyeClock += dt;
            if (eyeClock > 0.1f) { eyeClock = 0f; eyeStep++; }
        }

        /// <summary>The rest's CreatorLook set, loaded on first use; null when not all there.</summary>
        private RestGaze Gazer()
        {
            if (!restGazeTried) { restGazeTried = true; restGaze = RestGaze.TryLoad(); if (restGaze == null) Console.WriteLine("[studio] the rest's CreatorLook set is not all there; resting on id 2"); }
            return restGaze;
        }

        private static readonly Dictionary<int, Vector3> crossDisplacement = new Dictionary<int, Vector3>();

        /// <summary>True in the update a cross-over re-based its root.</summary>
        public bool Rebased;

        /// <summary>The cross-over wrapper's displacement (AE_CrossWrapper_ctor 0x920AC300) for ids 63..66.</summary>
        private static Vector3 CrossDisplacement(int id)
        {
            Vector3 d;
            if (!crossDisplacement.TryGetValue(id, out d))
            {
                if (!LineupStage.RootDisplacement(id, out d)) d = Vector3.Zero;
                crossDisplacement[id] = d;
            }
            return d;
        }

        /// <summary>AE_Actor_ClearQueue (0x920F8300): the queue emptied; the next update sets the rest alone.</summary>
        public void ClearQueue() { requests.Clear(); clearQueued = true; cutQueued = false; }
        private bool clearQueued;

        /// <summary>The floor shadow's root offset (x, z): AE_DrawAvatar's T(p.x, 0, p.z).</summary>
        public Vector3 ShadowRoot;

        /// <summary>On the editor's screens, when the editor's animations are installed.</summary>
        private bool EditorMode { get { return Screen >= 0 && EditorAnimations.Has(EditorAnimations.CreatorLookNeutral); } }

        /// <summary>A request for the avatar (sub_920C2508); one frame's requests become the next play list.</summary>
        public void Request(int id, bool loop) { Request(id, loop, 0f, false); }

        /// <summary>AE_Actor_Queue (0x920F82A8): at most four requests; `cut` replaces the ring at once.</summary>
        public void Request(int id, bool loop, float start, bool cut)
        {
            if (requests.Count >= ListRequests) return;
            requests.Add(new Req { Id = id, Loop = loop, Start = start });
            cutQueued = cut;
        }
        private bool cutQueued;

        /// <summary>The exit's reaction (AE_Turntable_Update): the request and the reaction cooldown.</summary>
        public void React(int id)
        {
            Request(id, false);
            cooldown = 5f;
        }

        /// <summary>One update of the actor, in the console's order: requests to list, the ring steps, due entries pushed.</summary>
        private void UpdateEditorAnimation(float dt)
        {
            if (ring.Count == 0) SeedRing();
            if (requests.Count > 0 || clearQueued) SetList();
            if (list.Count == 0) SetList();                         // [ours] the view's first update: the rest
            StepRing(dt);
            PushDue();
            Sample(dt);
            remaining -= dt;

            // the idle: a timer while the actor rests and the cooldown has run out
            cooldown -= dt;
            if (GazeRest) return;                  // the editor's avatar counts in Look, as the console does
            if (!Resting || cooldown >= 0f || NoIdle) idleTime = 0f;
            else
            {
                idleTime += dt;
                if (idleTime > rng.Next(20) + 10) { PickIdle(); idleTime = 0f; }
            }
        }

        // ---- the crossfade: the play list and the blend ring -----------------------------------------
        // The renderer keeps the list apart from a ring of at most four playing slots, crossfaded.

        private sealed class Entry
        {
            public int Id; public bool Loop, Rest;
            public bool Turned;                     // wrapped in the turntable wrapper (TurnSlot)
            public float Start;                     // the run-on wrapper's cursor offset
            public AvatarAnimation Anim;            // made as the list is built (AE_Actor_BuildQueue)
            public AnimClip Clip;                   // its keyframes, played as the console plays them
            public AnimTable Table;                 // its table track, or null
        }

        private sealed class Slot
        {
            public int Id; public bool Loop, Rest, Shifted, Turned;
            public AvatarAnimation Anim;            // id 2's asset for the rest (its clock, its length)
            public AnimClip Clip;                   // the cursor (+4) and the keyframes
            public AnimTable Table;                 // its table track (the rest: Neutral's, id 2's asset)
            public float T, Clock;                  // the blend's length (+44), the clock since the push (+40)
        }

        private readonly List<Entry> list = new List<Entry>();   // the play list (+788, count +820)
        private readonly List<Slot> ring = new List<Slot>();     // the ring, oldest first (+196 .. +192)
        private int listIndex, listFlags;                         // +824, +828
        private float threshold;                                  // +832
        private float remaining;                                  // the actor's countdown (+1076)
        private const float Crossfade = 0.30000001f;              // flt_92085D10
        private const int RingSlots = 4;                          // XAvBlendRing_Push: a fifth drops the oldest
        private const int ListRequests = 4;                       // AE_Actor_UpdateQueue: room for 5, the rest last

        // this frame's sample: the body's joints, the carryable's and the table's
        private JointPose[] acc, slotPoses, carryAcc, carryPoses;
        private readonly JointPose[] tableAcc = new JointPose[AnimTable.MaxEntries], tablePoses = new JointPose[AnimTable.MaxEntries];
        private Matrix[] bones, carryBones;

        /// <summary>The root joint's scale in the drawn skeleton (height scaling), or 1 before one is loaded.</summary>
        private Vector3 RootScale()
        {
            Skeletons();
            return rootScaleOf == null ? Vector3.One : rootScale;
        }
        private Vector3 rootScale = Vector3.One;
        private AvatarRenderer rootScaleOf;

        // the skeletons' joint 0 as the turntable wrapper reads them (the body's, the carryable's)
        private Vector3 bodyRootP, carryRootP;
        private Quaternion bodyRootQ = Quaternion.Identity, carryRootQ = Quaternion.Identity;
        private bool carryRoot;

        /// <summary>The drawn avatar's skeleton roots and root scale, read once per renderer.</summary>
        private void Skeletons()
        {
            if (rootScaleOf == current) return;
            Avatar a = Parsed(current);
            if (a == null || a.Skeleton == null || a.Skeleton.Joints == null || a.Skeleton.Joints.Length == 0) return;
            var l = a.Skeleton.Joints[0].Local;
            rootScale = new Vector3(l.scale.X, l.scale.Y, l.scale.Z);
            bodyRootP = new Vector3(l.position.X, l.position.Y, l.position.Z);
            bodyRootQ = new Quaternion(l.rotation.X, l.rotation.Y, l.rotation.Z, l.rotation.W);
            carryRoot = a.Carryable != null && a.Carryable.Skeleton != null && a.Carryable.Skeleton.Joints != null && a.Carryable.Skeleton.Joints.Length > 0;
            if (carryRoot)
            {
                var c = a.Carryable.Skeleton.Joints[0].Local;
                carryRootP = new Vector3(c.position.X, c.position.Y, c.position.Z);
                carryRootQ = new Quaternion(c.rotation.X, c.rotation.Y, c.rotation.Z, c.rotation.W);
            }
            rootScaleOf = current;
        }

        /// <summary>flt_92326A64, the turntable's angle: s x 180 degrees, one for the whole editor.</summary>
        public static float TurntableAngle;

        /// <summary>The turntable wrapper (AE_TurntableWrapper_Sample 0x920AB238): a slot's sample turned about joint 0's bind.</summary>
        private void TurnSlot(Slot s, int nt, int ck)
        {
            Quaternion ry = Quaternion.CreateFromAxisAngle(Vector3.Up, -TurntableAngle);
            Skeletons();
            if (s.Clip.Joints > 0 && rootScaleOf != null) slotPoses[0] = TurnRoot(slotPoses[0], ry, bodyRootQ, bodyRootP);
            if (ck > 0 && carryRoot) carryPoses[0] = TurnRoot(carryPoses[0], ry, carryRootQ, carryRootP);
            for (int j = 0; j < AnimTable.MaxEntries; j++)
            {
                JointPose e = j < nt ? tablePoses[j] : CarryFill;
                Vector3 p = Vector3.Transform(new Vector3(e.position.X, e.position.Y, e.position.Z), ry);
                Quaternion q = ry * new Quaternion(e.rotation.X, e.rotation.Y, e.rotation.Z, e.rotation.W);
                e.position.X = p.X; e.position.Y = p.Y; e.position.Z = p.Z;
                e.rotation.X = q.X; e.rotation.Y = q.Y; e.rotation.Z = q.Z; e.rotation.W = q.W;
                tablePoses[j] = e;
            }
        }

        /// <summary>B^-1 . Ry . B . joint, with B = (lq, lp); the scale as it is.</summary>
        private static JointPose TurnRoot(JointPose j, Quaternion ry, Quaternion lq, Vector3 lp)
        {
            Quaternion lqi = Quaternion.Conjugate(lq);
            Quaternion q = lqi * ry * lq * new Quaternion(j.rotation.X, j.rotation.Y, j.rotation.Z, j.rotation.W);
            Vector3 p = Vector3.Transform(Vector3.Transform(Vector3.Transform(new Vector3(j.position.X, j.position.Y, j.position.Z), lq) + lp, ry) - lp, lqi);
            j.position.X = p.X; j.position.Y = p.Y; j.position.Z = p.Z;
            j.rotation.X = q.X; j.rotation.Y = q.Y; j.rotation.Z = q.Z; j.rotation.W = q.W;
            return j;
        }

        /// <summary>The root joint's scale, and the drawn pose's root joint translation.</summary>
        public Vector3 RootJointScale { get { return RootScale(); } }

        /// <summary>Where the turn is, the angle, the drawn root joint and the table root.</summary>
        public string TurnLog
        {
            get
            {
                Vector3 j = RootJoint, t = TableRoot;
                string s = (WorldSpin == 0f && EditorMode && ring.Count > 0 ? "in the pose" : "in the world") + ", angle " + TurntableAngle.ToString("0.000")
                    + " rad, " + (ring.Count > 0 && ring[ring.Count - 1].Turned ? "the newest slot turned" : "the newest slot NOT turned")
                    + ", root joint (" + j.X.ToString("0.0000") + ", " + j.Y.ToString("0.0000") + ", " + j.Z.ToString("0.0000") + "), table root ("
                    + t.X.ToString("0.0000") + ", " + t.Y.ToString("0.0000") + ", " + t.Z.ToString("0.0000") + ")";
                if (acc == null || rootScaleOf == null) return s;
                // the root as the skinning composes it, against the unturned root turned about the model's up axis
                Quaternion ry = Quaternion.CreateFromAxisAngle(Vector3.Up, -TurntableAngle);
                JointPose unturned = TurnRoot(acc[0], Quaternion.Conjugate(ry), bodyRootQ, bodyRootP);
                Matrix local = Matrix.CreateFromQuaternion(bodyRootQ) * Matrix.CreateScale(rootScale);
                local.Translation = bodyRootP;
                Vector3 drawn = (RestGaze.Compose(acc[0]) * local).Translation;
                Vector3 about = Vector3.Transform((RestGaze.Compose(unturned) * local).Translation, ry);
                return s + "; the root " + ((drawn - about).Length() * 1000f).ToString("0.000") + " mm from a turn about the model's up axis";
            }
        }

        /// <summary>The skeletons' joint 0 as parsed, or "none".</summary>
        public string SkeletonRoot
        {
            get
            {
                Avatar a = Parsed(current);
                if (a == null || a.Skeleton == null) return "none";
                string s = Root(a.Skeleton);
                if (a.Carryable != null && a.Carryable.Skeleton != null) s += "; the carryable's " + Root(a.Carryable.Skeleton);
                return s;
            }
        }

        private static string Root(Skeleton k)
        {
            if (k.Joints == null || k.Joints.Length == 0) return "none";
            var l = k.Joints[0].Local;
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "position ({0:0.#####}, {1:0.#####}, {2:0.#####}), rotation ({3:0.#####}, {4:0.#####}, {5:0.#####}, {6:0.#####})",
                l.position.X, l.position.Y, l.position.Z, l.rotation.X, l.rotation.Y, l.rotation.Z, l.rotation.W);
        }
        public Vector3 RootJoint { get { return EditorMode && ring.Count > 0 && bones != null ? bones[0].Translation : Vector3.Zero; } }

        /// <summary>The ring's table entry 0, scaled: the actor's root (XAvRenderer_GetTableEntry 0x9213E5A8).</summary>
        public Vector3 TableRoot { get { return new Vector3(tableAcc[0].position.X, tableAcc[0].position.Y, tableAcc[0].position.Z); } }

        /// <summary>Fills for unkeyed joints: the carryable's (0, 0, 0, 1), scale 0 so it is not drawn; the body's identity.</summary>
        private static readonly JointPose CarryFill = MakeFill(0f), BodyFill = MakeFill(1f);

        private static JointPose MakeFill(float scale)
        {
            JointPose p = new JointPose();
            p.rotation.W = 1f;
            p.scale.X = p.scale.Y = p.scale.Z = scale;
            return p;
        }

        /// <summary>A renderer's ring starts with the library's default animation, looping (XAvBlendRing_Init 0x9214E4B0).</summary>
        private void SeedRing()
        {
            AvatarAnimation a = EditorAnimations.Create(EditorAnimations.LibraryDefault);
            AnimClip clip = AnimClip.Of(a);
            if (clip == null) { if (a != null) a.Dispose(); return; }
            ring.Add(new Slot { Id = EditorAnimations.LibraryDefault, Loop = true, Anim = a, Clip = clip,
                                Table = AnimTable.Of(EditorAnimations.Asset(EditorAnimations.LibraryDefault)) });
        }

        /// <summary>AE_Actor_BuildQueue (0x920F8704) and XAvRenderer_SetPlayList: the requests become the list.</summary>
        private void SetList()
        {
            ClearList();
            remaining = 0f;
            bool looped = false, turned = GazeRest;
            for (int i = 0; i < requests.Count && i < ListRequests && !looped; i++)
            {
                Req r = requests[i];
                int id = r.Id == 3 && !CarryableHasAnimation ? EditorAnimations.CreatorLookNeutral : r.Id;
                Entry e = MakeEntry(id, r.Start);
                if (e == null) continue;                  // [ours] the asset is not installed
                if (id == EditorAnimations.FloatingCelebrate) turned = true;
                if (id == 4) turned = false;
                e.Loop = r.Loop; e.Turned = turned;
                list.Add(e);
                if (r.Loop) looped = true;
                else remaining += e.Clip.Length;
            }
            if (!looped)
            {
                // [not built] a lineup actor's appended rest after id 9 (never shows: the hand-off comes first)
                Entry rest = MakeEntry(EditorAnimations.CreatorLookNeutral, 0f);
                if (rest != null) { rest.Turned = turned; list.Add(rest); }
            }
            requests.Clear(); clearQueued = false;
            listIndex = 0; listFlags = cutQueued ? 6 : 2;
            threshold = ring.Count > 0 ? ring[ring.Count - 1].Clock : 0f;
        }

        /// <summary>An entry's animation: the carryable's for 3, the gaze rest for 2, else the editor's asset.</summary>
        private Entry MakeEntry(int id, float start)
        {
            AvatarAnimation a = id == 3 ? CarryableAnimation() : EditorAnimations.Create(id);
            if (a == null && id == EditorAnimations.CreatorLookNeutral) a = Presets.Create(AvatarAnimationPreset.Stand0);
            if (a == null) return null;
            AnimClip clip = AnimClip.Of(a);
            if (clip == null) { a.Dispose(); return null; }
            // the table track: the carryable's from its linked asset, the rest's from id 2's
            AnimTable table = id == 3 ? AnimTable.Of(carryLink) : AnimTable.Of(EditorAnimations.Asset(id));
            return new Entry { Id = id, Anim = a, Clip = clip, Start = start, Table = table,
                               Rest = id == EditorAnimations.CreatorLookNeutral && GazeRest && Gazer() != null };
        }

        private void ClearList()
        {
            for (int i = listIndex; i < list.Count; i++) if (list[i].Anim != null) list[i].Anim.Dispose();
            list.Clear(); listIndex = 0;
        }

        private void ClearRing()
        {
            foreach (Slot s in ring) s.Anim.Dispose();
            ring.Clear();
        }

        /// <summary>XAvBlendRing_Step (0x9214E5A8): clocks and cursors advance; a finished blend frees older slots.</summary>
        private void StepRing(float dt)
        {
            for (int i = 0; i < ring.Count; i++)
            {
                Slot s = ring[i];
                s.Clock += Math.Abs(dt);
                s.Clip.Advance(dt, s.Loop);
                if (i > 0 && s.Clock >= s.T)
                {
                    for (int k = 0; k < i; k++) ring[k].Anim.Dispose();
                    ring.RemoveRange(0, i);
                    i = 0;
                }
            }
        }

        /// <summary>XAvRenderer_UpdateAnim's push loop: while the newest slot's clock passes the threshold, push the next.</summary>
        private void PushDue()
        {
            while (listIndex < list.Count)
            {
                Slot n = ring.Count > 0 ? ring[ring.Count - 1] : null;
                float now = n == null ? 0f : n.Clock, due = threshold;
                if (n != null && (listFlags & 1) != 0)
                {
                    float len = n.Clip.Length;                // its vtable +20: (frames - 1) / rate
                    if (!n.Loop) due = Math.Max(threshold, len - Crossfade);
                    else if (len > 0f)
                        for (due = ((float)Math.Floor(now / len) + 1f) * len - Crossfade; due < threshold; due += len) { }
                }
                if (n != null && now < due) break;
                float t = listIndex == 0 && (listFlags & 4) != 0 && (listFlags & 1) == 0 ? 0f : Crossfade;
                if (n == null) t = 0f;                    // [ours] nothing to blend from yet: it cuts in
                threshold = 0f; listFlags |= 1;
                Entry e = list[listIndex];
                list[listIndex] = new Entry { Id = e.Id };   // the slot owns the animation now
                listIndex++;
                Push(e, now - due, t, listIndex == list.Count && (listFlags & 2) != 0);
            }
        }

        /// <summary>XAvBlendRing_Push (0x9214E678): T <= 0 cuts; a fifth slot drops the oldest.</summary>
        private void Push(Entry e, float elapsed, float t, bool loop)
        {
            if (elapsed < 0f) elapsed = 0f;
            if (t <= 0f) ClearRing();
            else if (ring.Count == RingSlots) { ring[0].Anim.Dispose(); ring.RemoveAt(0); }
            AnimClip c = e.Clip;
            c.Time = e.Start;
            if (elapsed > 0f) c.Advance(elapsed, loop);
            Slot was = ring.Count > 0 ? ring[ring.Count - 1] : null;
            if (was != null)
                LastPush = was.Id + " -> " + e.Id + " at " + was.Clock.ToString("0.00") + " of " + was.Clip.Length.ToString("0.00")
                         + " s, blend " + t.ToString("0.0") + " s";
            ring.Add(new Slot { Id = e.Id, Loop = loop, Rest = e.Rest, Turned = e.Turned, Anim = e.Anim, Clip = c, Table = e.Table, T = t, Clock = elapsed });
            MostSlots = Math.Max(MostSlots, ring.Count);
            if (AnimLog) Console.WriteLine("[studio] editor animation " + e.Id + (loop ? " (loop)" : "") + ", blend " + t.ToString("0.0") + " s, " + ring.Count + " slot(s)");
        }

        /// <summary>The last push over a slot, and the most slots the ring has held.</summary>
        public string LastPush = "none";
        public int MostSlots;

        /// <summary>A slot's weight: 1 for the oldest, else (1 - cos(pi min(t / T, 1))) / 2.</summary>
        private float Weight(int i)
        {
            if (i == 0) return 1f;
            Slot s = ring[i];
            float k = s.T > 0f ? Math.Min(s.Clock / s.T, 1f) : 1f;
            return (1f - (float)Math.Cos(k * 3.1415927f)) * 0.5f;
        }

        /// <summary>XAvPlayList_Sample: the ring oldest first into the joints, each slot mixed at its weight.</summary>
        private void Sample(float dt)
        {
            Rebased = false;
            restOn = false;
            if (ring.Count == 0) return;
            int nb = 0;
            foreach (Slot s in ring) nb = Math.Max(nb, s.Clip.Joints);
            if (nb == 0) return;
            if (acc == null || acc.Length != nb) { acc = new JointPose[nb]; slotPoses = new JointPose[nb]; bones = new Matrix[nb]; }
            int nc = lib != null && lib.HasCarryable ? Math.Max(1, lib.CarryableJointCount) : 0;
            if (nc > 0 && (carryAcc == null || carryAcc.Length != nc)) { carryAcc = new JointPose[nc]; carryPoses = new JointPose[nc]; carryBones = new Matrix[nc]; }

            for (int i = 0; i < ring.Count; i++)
            {
                Slot s = ring[i];
                float w = Weight(i);
                if (s.Rest && Gazer() != null) restGaze.EvaluatePoses(s.Clip.Time, Gaze, slotPoses);   // id 2's cursor: Neutral's
                else s.Clip.Sample(slotPoses);
                int keyed = s.Rest && restGaze != null ? nb : s.Clip.Joints;
                // the table: keyed entries mixed at the slot's weight, the rest filled unweighted
                int nt = s.Table == null ? 0 : Math.Min(s.Table.Entries, AnimTable.MaxEntries);
                if (nt > 0) s.Table.Sample(s.Clip.Time, tablePoses);
                // AE_CrossWrapper_Root: from time >= its length (vtable +20) x 0.5
                bool cross = s.Id >= EditorAnimations.CrossToFrontLeft && s.Id <= EditorAnimations.CrossToBackRight
                          && s.Clip.Time >= s.Clip.Length * 0.5f;
                if (cross)
                {
                    // AE_CrossWrapper_Root: from the root joint and from every table entry
                    Vector3 d = CrossDisplacement(s.Id);
                    slotPoses[0].position.X -= d.X; slotPoses[0].position.Y -= d.Y; slotPoses[0].position.Z -= d.Z;
                    for (int j = 0; j < nt; j++) { tablePoses[j].position.X -= d.X; tablePoses[j].position.Y -= d.Y; tablePoses[j].position.Z -= d.Z; }
                    if (!s.Shifted) Rebased = true;
                    s.Shifted = true;
                }
                int ck = nc > 0 && s.Clip.SampleCarryable(carryPoses) ? Math.Min(s.Clip.CarryableJoints, nc) : 0;
                // the turntable wrapper goes round the cross-over's; it turns all three table entries
                if (s.Turned) { TurnSlot(s, nt, ck); nt = AnimTable.MaxEntries; }
                Mix(acc, slotPoses, w);
                // unkeyed joints take the body fill, unweighted
                for (int j = keyed; j < nb; j++) acc[j] = BodyFill;
                for (int j = 0; j < AnimTable.MaxEntries; j++)
                    tableAcc[j] = j >= nt ? CarryFill : w >= 1f ? tablePoses[j] : RestGaze.Mix(tableAcc[j], tablePoses[j], w);
                if (nc > 0)
                {
                    for (int j = 0; j < ck; j++) carryAcc[j] = w >= 1f ? carryPoses[j] : RestGaze.Mix(carryAcc[j], carryPoses[j], w);
                    for (int j = ck; j < nc; j++) carryAcc[j] = CarryFill;              // past its keys, the fill, unweighted
                }
            }
            for (int j = 0; j < nb; j++) bones[j] = RestGaze.Compose(acc[j]);
            if (nc > 0) for (int j = 0; j < nc; j++) carryBones[j] = RestGaze.Compose(carryAcc[j]);
            // each entry's position times the root joint's scale (XAvRenderer_UpdateAnim 0x92140A94)
            Vector3 k = RootScale();
            for (int j = 0; j < AnimTable.MaxEntries; j++)
            {
                tableAcc[j].position.X *= k.X; tableAcc[j].position.Y *= k.Y; tableAcc[j].position.Z *= k.Z;
            }

            Slot newest = ring[ring.Count - 1];
            restOn = newest.Rest && restGaze != null;
            if (restOn) StepModeClock(dt);
        }

        /// <summary>One slot into the accumulated joints: written at weight 1, mixed under it.</summary>
        private static void Mix(JointPose[] into, JointPose[] slot, float w)
        {
            if (w >= 1f) { Array.Copy(slot, into, Math.Min(slot.Length, into.Length)); return; }
            for (int j = 0; j < into.Length && j < slot.Length; j++) into[j] = RestGaze.Mix(into[j], slot[j], w);
        }

        /// <summary>sub_920C87E8's weighted pick of the next idle: a look timer or an animation.</summary>
        private void PickIdle()
        {
            // AE_PickIdle's weights: over Props the look timers 0 and id 3 1000
            int look = screen == 50 || OverProps ? 0 : 20;
            int carry = CarryableHasAnimation ? (OverProps ? 1000 : 30) : 0;
            int body = OverProps ? 1 : 50;
            int r = rng.Next(look + look + carry + body);
            if (r < look) { lookCamera = rng.Next(5) + 5f; return; }
            if (r < 2 * look) { lookTile = rng.Next(5) + 5f; return; }
            if (r < 2 * look + carry) { Request(3, false); cooldown = 0.2f; return; }
            int[] list = female ? EditorAnimations.FemaleIdles : EditorAnimations.MaleIdles;
            Request(list[rng.Next(list.Length)], false);
            cooldown = 0.2f;
        }

        private static readonly bool AnimLog = Environment.GetEnvironmentVariable("AVATAR_STUDIO_ANIMLOG") == "1";

        /// <summary>The newest slot's editor animation id, or -1 (the selftest logs it).</summary>
        public int EditorAnimationId { get { return EditorMode ? NewestId : -1; } }

        /// <summary>AE_Actor_OnLoaded (0x920F8B70): a changed carryable plays its animation once.</summary>
        private void OnLoaded()
        {
            Avatar a = Parsed(current);
            Guid c = a == null || a.Carryable == null || a.Carryable.Model == null ? Guid.Empty : a.Carryable.Model.AssetId;
            bool changed = carryKnown && c != carryId;
            carryId = c; carryKnown = true;
            FindCarryableAnimation(a);
            if (!changed || !EditorMode || NoIdle) return;
            if (CarryableHasAnimation) Request(3, false);                    // AE_Actor_Queue(this, 3, 0, 0)
        }

        /// <summary>The dizzy trio past a turntable speed of 2 (closet_balance_left).</summary>
        private void Dizzy(float dt)
        {
            bool fast = spinSpeed > 2f;
            dizzyCountdown -= dt;
            if (fast == dizzy || dizzyCountdown >= 0f) return;
            dizzyCountdown = 0.2f;
            dizzy = fast;
            if (fast) { Request(EditorAnimations.BalanceInto, false); Request(EditorAnimations.BalanceLoop, true); Voice = AvatarVoice.Wobble; }   // 0x920CAE2C
            else Request(EditorAnimations.BalanceOutOf, false);
        }

        /// <summary>The LT / RT turntable, one frame, as the editor's avatar update does it (sub_920CA618).</summary>
        public void Turntable(float dt, float input)
        {
            float rate;
            if (snapBack > 0f && (snapBack -= dt) > 0f)
                rate = -(Math.Abs(Spin) * 8f + 1f) * Spin;
            else
            {
                spinSpeed = Math.Abs(input) > 0.8f ? Math.Min(spinSpeed + dt, 3f) : Math.Max(spinSpeed - dt, 1f);
                rate = input * spinSpeed;
            }
            Spin += dt * rate;
            if (Spin > 1f) Spin -= 2f;
            if (Spin < -1f) Spin += 2f;
            float degrees = Spin * 180f;                       // AE_Turntable_Update: (+100208) x 180.0
            TurntableAngle = degrees * 0.017453292f;           // AE_SetSpinAngleDeg 0x920A9A90
            if (EditorMode) Dizzy(dt);
        }

        private AvatarAnimationPreset NextIdle()
        {
            // the stand presets plus the body's own idles, at random
            AvatarAnimationPreset[] male = { AvatarAnimationPreset.Stand0, AvatarAnimationPreset.Stand1, AvatarAnimationPreset.Stand2, AvatarAnimationPreset.Stand3,
                AvatarAnimationPreset.MaleIdleLookAround, AvatarAnimationPreset.MaleIdleShiftWeight, AvatarAnimationPreset.MaleIdleStretch, AvatarAnimationPreset.MaleIdleCheckHand };
            AvatarAnimationPreset[] fem = { AvatarAnimationPreset.Stand0, AvatarAnimationPreset.Stand1, AvatarAnimationPreset.Stand2, AvatarAnimationPreset.Stand3,
                AvatarAnimationPreset.FemaleIdleLookAround, AvatarAnimationPreset.FemaleIdleShiftWeight, AvatarAnimationPreset.FemaleIdleCheckNails, AvatarAnimationPreset.FemaleIdleFixShoe };
            AvatarAnimationPreset[] set = female ? fem : male;
            return set[rng.Next(set.Length)];
        }

        public void PlayOnce(AvatarAnimationPreset p) { StartAnimation(p); }

        /// <summary>At the start of the frame: the renderer's shadow pass, before any scene target is set.</summary>
        public void BeginFrame()
        {
            libFrame = false;
            AvatarRenderer r = current;
            if (lib == null || r == null || r.State != AvatarRendererState.Ready || anim == null || !(Screen >= 0 && camPlaced)) return;
            try
            {
                lib.KeyColour = KeyColour; lib.AmbientColour = AmbientColour; lib.KeyDirection = KeyDirection;
                lib.SetCarryablePose(CarryablePose());
                lib.ShadowPass(EditorCamera.ShadowWorld(camNow, 0f), EditorCamera.LibraryWorld(camNow, WorldSpin),
                               EditorCamera.View, Proj, Pose);
                libFrame = true;
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] library renderer failed, this avatar is no longer drawn: " + e);
                lib.Dispose(); lib = null;
            }
        }

        /// <summary>The avatar in the My Style mirror (AE_DrawAvatarReflected sub_920C5580): a reflected view.</summary>
        public void DrawReflection(Matrix reflection)
        {
            if (!libFrame || lib == null || !(Screen >= 0 && camPlaced) || anim == null) return;
            Matrix view = reflection * EditorCamera.View;
            RasterizerState raster = device.RasterizerState;
            DepthStencilState depth = device.DepthStencilState;
            BlendState blend = device.BlendState;
            DrawFloorShadow(ShadowAt(), view, EditorCamera.Projection, true);      // AE_DrawAvatar's, root and all
            lib.DrawReflected(Expression, view);
            device.RasterizerState = raster; device.DepthStencilState = depth; device.BlendState = blend;
        }

        public void Draw(float aspect)
        {
            AvatarRenderer r = current;
            if (r == null || r.State != AvatarRendererState.Ready || anim == null) return;
            if (libFrame && lib != null && Screen >= 0 && camPlaced)
            {
                DrawFloorShadow(ShadowAt(), EditorCamera.View, Proj);
                RasterizerState raster = device.RasterizerState;
                DepthStencilState depth = device.DepthStencilState;
                BlendState blend = device.BlendState;
                lib.Draw(Expression);
                device.RasterizerState = raster; device.DepthStencilState = depth; device.BlendState = blend;
                return;
            }
            // [ours] no other renderer: an avatar off the editor's camera records is not drawn
        }

        /// <summary>The floor shadow's world: RotY(pi s) x T(p.x, 0, p.z) x the record's world.</summary>
        private Matrix ShadowAt()
        {
            return Matrix.CreateRotationY(Yaw) * Matrix.CreateTranslation(ShadowRoot.X, 0f, ShadowRoot.Z) * EditorCamera.ShadowWorld(camNow, 0f);
        }

        /// <summary>AvatarShadow.png from the installed art, or null: no shadow.</summary>
        public Texture2D FloorShadow;
        private BasicEffect shadowFx;
        private VertexPositionTexture[] shadowQuad;

        /// <summary>The editor's floor shadow, drawn before the avatar (AE_DrawAvatar, sub_920FA380).</summary>
        private static readonly DepthStencilState DepthReadStencil = new DepthStencilState
        {
            DepthBufferEnable = true, DepthBufferWriteEnable = false,
            StencilEnable = true, StencilFunction = CompareFunction.LessEqual, ReferenceStencil = 255,
            StencilPass = StencilOperation.Keep, StencilFail = StencilOperation.Keep, StencilDepthBufferFail = StencilOperation.Keep,
        };

        private void DrawFloorShadow(Matrix world, Matrix view, Matrix proj) { DrawFloorShadow(world, view, proj, false); }

        private void DrawFloorShadow(Matrix world, Matrix view, Matrix proj, bool stencil)
        {
            if (FloorShadow == null) return;
            if (shadowFx == null)
            {
                shadowFx = new BasicEffect(device) { TextureEnabled = true, LightingEnabled = false, VertexColorEnabled = false };
                shadowQuad = new VertexPositionTexture[4];
                for (int i = 0; i < 4; i++)
                    shadowQuad[i] = new VertexPositionTexture(new Vector3(i % 2 - 0.5f, 0f, i / 2 - 0.5f), new Vector2(i % 2, i / 2));
            }
            BlendState blend = device.BlendState;
            DepthStencilState depth = device.DepthStencilState;
            RasterizerState raster = device.RasterizerState;
            SamplerState sampler = device.SamplerStates[0];
            device.BlendState = BlendState.AlphaBlend;
            device.DepthStencilState = stencil ? DepthReadStencil : DepthStencilState.DepthRead;
            device.RasterizerState = RasterizerState.CullNone;
            device.SamplerStates[0] = SamplerState.LinearClamp;
            shadowFx.Texture = FloorShadow;
            shadowFx.World = world; shadowFx.View = view; shadowFx.Projection = proj;
            shadowFx.CurrentTechnique.Passes[0].Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleStrip, shadowQuad, 0, 2);
            device.BlendState = blend; device.DepthStencilState = depth;
            device.RasterizerState = raster; device.SamplerStates[0] = sampler;
        }

        /// <summary>Where this avatar's head is, in model units: the head joint up the bind pose chain.</summary>
        public static float HeadHeight(AvatarRenderer r)
        {
            try
            {
                System.Collections.ObjectModel.ReadOnlyCollection<Matrix> bind = r.BindPose;
                System.Collections.ObjectModel.ReadOnlyCollection<int> parents = r.ParentBones;
                Matrix m = Matrix.Identity;
                int i = 19;                                              // the head joint (XNA's AvatarBone.Head)
                int guard = 0;
                while (i >= 0 && i < bind.Count && guard++ < 64)
                {
                    m = m * bind[i];                 // bind[i] is local to its parent, so walk upward
                    int parent = parents[i];
                    if (parent == i || parent < 0) break;
                    i = parent;
                }
                float y = m.Translation.Y;
                return y > 0.2f && y < 6f ? y : ReferenceHeadHeight;    // nonsense means "use the default"
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] head height unavailable: " + e.Message);
                return ReferenceHeadHeight;
            }
        }

        /// <summary>The head height the framings were chosen against; they scale by the real one.</summary>
        public const float ReferenceHeadHeight = 1.45f;   // [ours]

        /// <summary>Camera per framing. The avatar's origin is at its feet, about 1.7 units tall.</summary>
        public static void Camera(Framing f, float aspect, float yaw, float scale, Vector2 offset, float headY, out Matrix world, out Matrix view, out Matrix proj)
        {
            world = Matrix.CreateRotationY(MathHelper.Pi + yaw);
            Vector3 target, eye; float fov;
            switch (f)
            {
                case Framing.Head:  target = new Vector3(0, 1.45f, 0); eye = new Vector3(0, 1.48f, 0.95f); fov = 32f; break;
                case Framing.Upper: target = new Vector3(0, 1.15f, 0); eye = new Vector3(0, 1.20f, 1.9f); fov = 36f; break;
                case Framing.Legs:  target = new Vector3(0, 0.55f, 0); eye = new Vector3(0, 0.75f, 2.0f); fov = 36f; break;
                case Framing.Feet:  target = new Vector3(0, 0.18f, 0); eye = new Vector3(0, 0.55f, 1.4f); fov = 34f; break;
                default:            target = new Vector3(0, 0.86f, 0); eye = new Vector3(0, 0.98f, 3.0f); fov = 38f; break;
            }
            float k = headY > 0f ? headY / ReferenceHeadHeight : 1f;
            target.Y *= k; eye.Y *= k; eye.Z *= k;     // taller avatar: look higher, stand further back
            view = Matrix.CreateLookAt(eye, target, Vector3.Up);
            proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(fov), aspect, 0.05f, 50f)
                 * Matrix.CreateScale(scale, scale, 1f)
                 * Matrix.CreateTranslation(offset.X, offset.Y, 0);
        }

        public void Dispose()
        {
            if (lib != null) lib.Dispose();
            if (current != null) current.Dispose();
            if (pending != null) pending.Dispose();
            if (anim != null) anim.Dispose();
            ClearList(); ClearRing();
            if (shadowFx != null) shadowFx.Dispose();
            if (restGaze != null) restGaze.Dispose();
            current = pending = null; anim = null; lib = null; shadowFx = null; restGaze = null;
        }
    }

    /// <summary>Renders "the avatar wearing option X" into small textures, a few at a time.</summary>
    sealed class ThumbnailCache : IDisposable
    {
        private sealed class Entry
        {
            public AvatarRenderer Renderer;
            public RenderTarget2D Texture;
            public Framing Framing;
            public bool Failed;
            public float HeadY;
        }

        private readonly GraphicsDevice device;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private readonly List<string> queue = new List<string>();
        private readonly Dictionary<string, byte[]> queued = new Dictionary<string, byte[]>();
        private AvatarAnimation pose;
        private const int MaxInFlight = 6;

        /// <summary>Render size: each caller gets its own (cells, cards, lineup portraits).</summary>
        public readonly int Width, Height;

        public ThumbnailCache(GraphicsDevice device, int size) : this(device, size, size) { }

        public ThumbnailCache(GraphicsDevice device, int width, int height) : this(device, width, height, false) { }

        /// <summary>`library`: draw through the console's renderer when its shaders are installed.</summary>
        public ThumbnailCache(GraphicsDevice device, int width, int height, bool library)
        {
            this.device = device; Width = width; Height = height; this.library = library;
        }

        private readonly bool library;

        /// <summary>The pictures come from the console's renderer.</summary>
        public bool UsesLibrary { get { return AvatarView.SharedShaders(device) != null; } }

        public string Key(byte[] manifest, Framing f)
        {
            // FNV-1a over the bytes; collisions are irrelevant at this scale
            ulong h = 14695981039346656037UL;
            foreach (byte b in manifest) { h ^= b; h *= 1099511628211UL; }
            return f + ":" + h.ToString("x16");
        }

        /// <summary>The texture if ready, else null (and queued).</summary>
        public Texture2D Get(byte[] manifest, Framing f)
        {
            string k = Key(manifest, f);
            Entry e;
            if (entries.TryGetValue(k, out e)) return e.Texture;
            if (!queued.ContainsKey(k)) { queued[k] = manifest; queue.Add(k); }
            return null;
        }

        public bool IsFailed(byte[] manifest, Framing f)
        {
            Entry e; return entries.TryGetValue(Key(manifest, f), out e) && e.Failed;
        }

        /// <summary>Drop queued requests that are no longer wanted (page changed).</summary>
        public void Prune(HashSet<string> wanted)
        {
            for (int i = queue.Count - 1; i >= 0; i--)
                if (!wanted.Contains(queue[i])) { queued.Remove(queue[i]); queue.RemoveAt(i); }
        }

        public void Update()
        {
            if (pose == null) { pose = Presets.Create(AvatarAnimationPreset.Stand0); if (pose == null) return; pose.CurrentPosition = TimeSpan.Zero; }
            int inFlight = 0;
            foreach (Entry e in entries.Values) if (e.Renderer != null) inFlight++;
            while (inFlight < MaxInFlight && queue.Count > 0)
            {
                string k = queue[0]; queue.RemoveAt(0);
                byte[] m = queued[k]; queued.Remove(k);
                Entry e = new Entry();
                e.Framing = (Framing)Enum.Parse(typeof(Framing), k.Substring(0, k.IndexOf(':')));
                try { e.Renderer = new AvatarRenderer(new AvatarDescription(m)); }
                catch (Exception ex) { Console.WriteLine("[studio] thumbnail renderer failed: " + ex.Message); e.Failed = true; }
                entries[k] = e;
                if (e.Renderer != null) inFlight++;
            }
            foreach (KeyValuePair<string, Entry> kv in entries)
            {
                Entry e = kv.Value;
                if (e.Renderer == null) continue;
                AvatarRendererState s = e.Renderer.State;
                if (s == AvatarRendererState.Ready) { e.HeadY = AvatarView.HeadHeight(e.Renderer); Render(e); }
                else if (s == AvatarRendererState.Unavailable) { e.Renderer.Dispose(); e.Renderer = null; e.Failed = true; }
            }
        }

        private void Render(Entry e)
        {
            Matrix w, v, p;
            AvatarView.Camera(e.Framing, Width / (float)Height, 0f, 1f, Vector2.Zero, e.HeadY, out w, out v, out p);
            // the console's renderer draws in the editor's left-handed space, as the editor does
            LibraryAvatar lib = null;
            if (UsesLibrary)
            {
                lib = LibraryAvatar.TryBuild(AvatarView.SharedShaders(device), e.Renderer);
                if (lib != null)
                {
                    Matrix z = Matrix.CreateScale(1f, 1f, -1f);
                    try { lib.ShadowPass(Matrix.Identity, Matrix.CreateRotationY(MathHelper.Pi) * w * z, z * v * z, z * p, pose.BoneTransforms); }
                    catch (Exception ex) { Console.WriteLine("[studio] picture through the library renderer failed: " + ex.Message); lib.Dispose(); lib = null; }
                }
            }
            RenderTarget2D rt = new RenderTarget2D(device, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
            device.SetRenderTarget(rt);
            device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Transparent, 1f, 0);
            device.DepthStencilState = DepthStencilState.Default;
            device.BlendState = BlendState.Opaque;
            device.RasterizerState = RasterizerState.CullCounterClockwise;
            if (lib != null)
            {
                lib.Draw(pose.Expression);
                lib.Dispose();
            }
            else e.Failed = true;                                  // no renderer without the console's lighting
            device.SetRenderTarget(null);
            e.Texture = rt;
            e.Renderer.Dispose(); e.Renderer = null;
        }

        public void Dispose()
        {
            foreach (Entry e in entries.Values) { if (e.Renderer != null) e.Renderer.Dispose(); if (e.Texture != null) e.Texture.Dispose(); }
            entries.Clear();
            if (pose != null) pose.Dispose();
        }
    }
}
