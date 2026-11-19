// The editor's controls' visuals, played as XUI plays them: the pop into focus, the nod, the presses.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    enum XuiType { Float, Vector, Quaternion, Bool, Int, Other }

    sealed class XuiKey
    {
        public float Frame; public int Curve, Mode; public float P1, P2; public object[] V;
    }

    sealed class XuiTrack
    {
        public string Target; public string[] Props; public XuiType[] Types;
        public int TargetIndex = -1;            // the target, found in its owner's subtree
        public readonly List<XuiKey> Keys = new List<XuiKey>();
    }

    sealed class XuiMark
    {
        public string Name, Command, Target; public int Frame;
    }

    sealed class XuiElementDef
    {
        public string Class, Id; public int Depth, Parent = -1;
        public Vector3 Position, Scale = Vector3.One, Pivot;
        public Quaternion Rotation = Quaternion.Identity;
        public float Opacity = 1f, Width, Height;
        public bool Show = true;
        public readonly Dictionary<string, string> Props = new Dictionary<string, string>();
        public readonly List<XuiTrack> Tracks = new List<XuiTrack>();     // the timelines it owns
        public readonly List<XuiMark> Marks = new List<XuiMark>();
    }

    /// <summary>One visual: element 0 is the visual itself; its marks and tracks drive the states.</summary>
    sealed class XuiVisualDef
    {
        public string Id;
        public readonly List<XuiElementDef> Elements = new List<XuiElementDef>();
        public List<XuiMark> Marks { get { return Elements[0].Marks; } }
        public List<XuiTrack> Tracks { get { return Elements[0].Tracks; } }

        /// <summary>A named frame's frame, the first of that name, or -1.</summary>
        public int Frame(string name)
        {
            foreach (XuiMark m in Marks) if (m.Name == name) return m.Frame;
            return -1;
        }

        public int Index(string id)
        {
            for (int i = 0; i < Elements.Count; i++) if (Elements[i].Id == id) return i;
            return -1;
        }

        /// <summary>The elements owning named frames (the visual first): each plays the same range by name.</summary>
        public readonly List<int> Owners = new List<int>();

        /// <summary>After loading: each track's target, the first element of that Id in its owner's subtree.</summary>
        public void Resolve()
        {
            Owners.Clear();
            for (int o = 0; o < Elements.Count; o++)
            {
                if (o == 0 || Elements[o].Marks.Count > 0) Owners.Add(o);
                foreach (XuiTrack t in Elements[o].Tracks)
                    for (int i = o + 1; i < Elements.Count && Elements[i].Depth > Elements[o].Depth; i++)
                        if (Elements[i].Id == t.Target) { t.TargetIndex = i; break; }
            }
        }
    }

    static class XuiVisuals
    {
        private static Dictionary<string, XuiVisualDef> all;
        private static readonly HashSet<int> warned = new HashSet<int>();

        public static bool Loaded { get { return all != null && all.Count > 0; } }

        /// <summary>visuals.txt from the tile folder, once.</summary>
        public static void Load(string dir)
        {
            all = new Dictionary<string, XuiVisualDef>();
            string path = dir == null ? null : Path.Combine(dir, "visuals.txt");
            if (path == null || !File.Exists(path))
            {
                Console.WriteLine("[studio] no visuals.txt: controls drawn settled (the setup installs their timelines)");
                return;
            }
            foreach (XuiVisualDef d in Parse(path))
                if (!all.ContainsKey(d.Id)) all[d.Id] = d;
            Console.WriteLine("[studio] visuals <- " + path + " (" + all.Count + ")");
        }

        /// <summary>Every block of a visuals.txt-format file, resolved, in the file's order.</summary>
        public static List<XuiVisualDef> Parse(string path)
        {
            List<XuiVisualDef> list = new List<XuiVisualDef>();
            XuiVisualDef cur = null;
            XuiElementDef owner = null;
            XuiTrack track = null;
            int[] parents = new int[64];
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                switch (f[0])
                {
                    case "visual":
                        cur = new XuiVisualDef { Id = f[2] };
                        owner = Element(f, 3, f[1], f[2], 0);
                        cur.Elements.Add(owner);
                        parents[0] = 0;
                        track = null;
                        break;
                    case "element":
                        if (cur == null) break;
                        int depth = int.Parse(f[1], CultureInfo.InvariantCulture);
                        owner = Element(f, 4, f[2], f[3], depth);
                        owner.Parent = depth > 0 && depth - 1 < parents.Length ? parents[depth - 1] : 0;
                        cur.Elements.Add(owner);
                        if (depth < parents.Length) parents[depth] = cur.Elements.Count - 1;
                        track = null;
                        break;
                    case "frame":
                        if (owner == null) break;
                        owner.Marks.Add(new XuiMark { Name = f[1], Frame = int.Parse(f[2], CultureInfo.InvariantCulture), Command = f[3], Target = f.Length > 4 ? f[4] : null });
                        break;
                    case "track":
                        if (owner == null) break;
                        track = new XuiTrack { Target = f[1], Props = f[2].Split(',') };
                        string[] types = f[3].Split(',');
                        track.Types = new XuiType[types.Length];
                        for (int i = 0; i < types.Length; i++) track.Types[i] = TypeOf(types[i]);
                        owner.Tracks.Add(track);
                        break;
                    case "key":
                        if (track == null) break;
                        XuiKey k = new XuiKey
                        {
                            Frame = int.Parse(f[1], CultureInfo.InvariantCulture),
                            Curve = int.Parse(f[2], CultureInfo.InvariantCulture),
                            Mode = int.Parse(f[3], CultureInfo.InvariantCulture),
                            V = new object[track.Props.Length],
                        };
                        if (f[4] != "-")
                        {
                            // XUI_EaseBytesToBezier (PageTransitions): signed ease bytes in, out, scale
                            string[] e = f[4].Split(',');
                            float ein = int.Parse(e[0], CultureInfo.InvariantCulture), eout = int.Parse(e[1], CultureInfo.InvariantCulture);
                            float s = int.Parse(e[2], CultureInfo.InvariantCulture) * 0.0099999998f;
                            k.P1 = s * (float)Math.Sin((1f - ein * 0.0099999998f) * 0.78539819f);
                            k.P2 = 1f - s * (float)Math.Sin((1f - eout * 0.0099999998f) * 0.78539819f);
                        }
                        for (int i = 0; i < track.Props.Length && 5 + i < f.Length; i++) k.V[i] = Parse(track.Types[i], f[5 + i]);
                        track.Keys.Add(k);
                        break;
                    case "end":
                        if (cur != null) { cur.Resolve(); list.Add(cur); }
                        cur = null; owner = null; track = null;
                        break;
                }
            }
            return list;
        }

        public static XuiVisualDef Get(string id)
        {
            XuiVisualDef v;
            return all != null && id != null && all.TryGetValue(id, out v) ? v : null;
        }

        private static XuiElementDef Element(string[] f, int from, string cls, string id, int depth)
        {
            XuiElementDef e = new XuiElementDef { Class = cls, Id = id == "-" ? null : id, Depth = depth };
            for (int i = from; i < f.Length; i++)
            {
                int eq = f[i].IndexOf('=');
                if (eq > 0) e.Props[f[i].Substring(0, eq)] = f[i].Substring(eq + 1);
            }
            string v;
            if (e.Props.TryGetValue("Position", out v)) e.Position = Vec(v);
            if (e.Props.TryGetValue("Scale", out v)) e.Scale = Vec(v);
            if (e.Props.TryGetValue("Pivot", out v)) e.Pivot = Vec(v);
            if (e.Props.TryGetValue("Rotation", out v)) e.Rotation = Quat(v);
            if (e.Props.TryGetValue("Opacity", out v)) e.Opacity = Flt(v);
            if (e.Props.TryGetValue("Width", out v)) e.Width = Flt(v);
            if (e.Props.TryGetValue("Height", out v)) e.Height = Flt(v);
            if (e.Props.TryGetValue("Show", out v)) e.Show = v != "0";
            return e;
        }

        private static XuiType TypeOf(string t)
        {
            switch (t)
            {
                case "float": return XuiType.Float;
                case "vector": return XuiType.Vector;
                case "quaternion": return XuiType.Quaternion;
                case "bool": return XuiType.Bool;
                case "integer": case "unsigned": return XuiType.Int;
            }
            return XuiType.Other;
        }

        private static object Parse(XuiType t, string s)
        {
            if (s == "-") return null;
            switch (t)
            {
                case XuiType.Float: return Flt(s);
                case XuiType.Vector: return Vec(s);
                case XuiType.Quaternion: return Quat(s);
                case XuiType.Bool: return s != "0";
                case XuiType.Int: int i; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i) ? (object)i : null;
            }
            return s;
        }

        private static float Flt(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        private static Vector3 Vec(string s)
        {
            string[] p = s.Split(',');
            return new Vector3(Flt(p[0]), p.Length > 1 ? Flt(p[1]) : 0f, p.Length > 2 ? Flt(p[2]) : 0f);
        }
        private static Quaternion Quat(string s)
        {
            string[] p = s.Split(',');
            return new Quaternion(Flt(p[0]), Flt(p[1]), Flt(p[2]), Flt(p[3]));
        }

        /// <summary>A track property's value at `frame` (XUI_Timeline_Interpolate).</summary>
        public static object Sample(XuiTrack t, int prop, float frame)
        {
            List<XuiKey> keys = t.Keys;
            if (keys.Count == 0) return null;
            const float Tolerance = 0.1f / 16.666666f;           // 0.1 ms, in frames
            int k = -1;
            for (int i = 0; i < keys.Count; i++) { if (keys[i].Frame <= frame + Tolerance) k = i; else break; }
            if (k < 0) return keys[0].V[prop];
            XuiKey a = keys[k];
            if (k + 1 >= keys.Count || a.Curve == 1) return a.V[prop];
            XuiKey b = keys[k + 1];
            float u = MathHelper.Clamp((frame - a.Frame) / (b.Frame - a.Frame), 0f, 1f);
            float e;
            switch (a.Curve)
            {
                case 0: e = u; break;
                case 2: e = Ease(a, u); break;
                default:
                    if (warned.Add(a.Curve)) Console.WriteLine("[studio] visuals: curve " + a.Curve + " not built, taken as linear");
                    e = u; break;
            }
            object va = a.V[prop], vb = b.V[prop];
            if (va == null || vb == null) return va;
            switch (t.Types[prop])
            {
                case XuiType.Float: return MathHelper.Lerp((float)va, (float)vb, e);
                case XuiType.Vector: return Vector3.Lerp((Vector3)va, (Vector3)vb, e);
                case XuiType.Quaternion: return Quaternion.Slerp((Quaternion)va, (Quaternion)vb, e);
            }
            return va;
        }

        /// <summary>XUI_Ease's modes over the Bezier: 0 in f(t), 1 out 1 - f(1 - t), 2 in-out.</summary>
        private static float Ease(XuiKey k, float t)
        {
            switch (k.Mode)
            {
                case 1: return 1f - Bezier(k, 1f - t);
                case 2: return t < 0.5f ? Bezier(k, 2f * t) / 2f : 1f - Bezier(k, 2f - 2f * t) / 2f;
                case 3: return 0f;
            }
            return Bezier(k, t);
        }

        private static float Bezier(XuiKey k, float t)
        {
            float u = 1f - t;
            return 3f * ((u * k.P1 + k.P2 * t) * u) * t + t * t * t;    // XUI_EaseCurve case 2
        }

        /// <summary>XUI's state pairs and their fallbacks (0x92041B1C: start, end, fallback row).</summary>
        public static readonly string[][] FallbackPairs =
        {
            new[] { "Normal", "EndNormal" }, new[] { "Focus", "EndFocus" }, new[] { "KillFocus", "EndNormal" },
            new[] { "NormalDisable", "EndNormalDisable" }, new[] { "FocusDisable", "EndFocusDisable" },
            new[] { "Press", "EndPress" }, new[] { "NormalPress", "EndNormalPress" },
            new[] { "NormalCheck", "EndNormalCheck" }, new[] { "FocusCheck", "EndFocusCheck" },
            new[] { "NormalCheckDisable", "EndNormalCheckDisable" }, new[] { "FocusCheckDisable", "EndFocusCheckDisable" },
            new[] { "NormalSel", "EndNormalSel" }, new[] { "NormalSelCheck", "EndNormalSelCheck" },
            new[] { "NormalSelDisable", "EndNormalSelDisable" }, new[] { "NormalSelCheckDisable", "EndNormalSelCheckDisable" },
            new[] { "ScrollMore", "EndScrollMore" }, new[] { "Scrolling", "EndScrolling" }, new[] { "ScrollMoreDisable", "EndScrollMoreDisable" },
            new[] { "InitFocus", "EndInitFocus" }, new[] { "InitFocusDisable", "EndInitFocusDisable" },
            new[] { "InitFocusCheck", "EndInitFocusCheck" }, new[] { "InitFocusCheckDisable", "EndInitFocusCheckDisable" },
            new[] { "PressDisable", "EndPressDisable" }, new[] { "KillFocus", "EndKillFocus" },
        };
        public static readonly int[] Fallback = { -1, 0, 0, 0, 1, 1, 5, 0, 7, 7, 8, 0, 11, 11, 13, 0, 15, 15, 1, 4, 8, 10, 6, 2 };
    }

    /// <summary>The evaluated state of one element.</summary>
    struct XuiElementState
    {
        public Vector3 Position, Scale, Pivot; public Quaternion Rotation; public float Opacity; public bool Show;
        public float Width, Height;

        /// <summary>XUI_Element_LocalMatrix: T(-pivot) S R T(pivot) T(position).</summary>
        public Matrix Local
        {
            get
            {
                return Matrix.CreateTranslation(-Pivot) * Matrix.CreateScale(Scale.X, Scale.Y, 1f)
                     * Matrix.CreateFromQuaternion(Rotation) * Matrix.CreateTranslation(Pivot) * Matrix.CreateTranslation(Position);
            }
        }
    }

    /// <summary>One control's visual, playing (XUI_Control_PlayVisualState and the player above).</summary>
    sealed class XuiPlayer
    {
        public readonly XuiVisualDef Def;
        public bool Focused, Enabled = true, Pressed;
        private int endFrame = -1;
        private bool endPosted;
        private Action pending;
        public string Range { get; private set; }

        /// <summary>One owner's playhead (the visual's, and each child that owns named frames).</summary>
        private sealed class Lane
        {
            public int Owner; public List<XuiMark> Marks; public float Head; public bool Playing, MarksAtHead;

            public int Find(string name) { foreach (XuiMark m in Marks) if (m.Name == name) return m.Frame; return -1; }

            public bool Set(string start, string end)
            {
                int a = Find(start), b = Find(end);
                if (a < 0 || b < 0) return false;
                Head = a; Playing = true; MarksAtHead = true;
                return true;
            }

            /// <summary>`frames` on, running each named frame's command; `reached` may end the advance.</summary>
            public void Advance(float frames, Func<int, bool> reached)
            {
                float left = frames;
                for (int guard = 0; guard < 64 && Playing; guard++)
                {
                    XuiMark next = null;
                    foreach (XuiMark m in Marks)
                    {
                        bool ahead = m.Frame > Head || (MarksAtHead && m.Frame == Head);
                        if (ahead && (next == null || m.Frame < next.Frame)) next = m;
                    }
                    if (next == null || next.Frame - Head > left) { Head += left; return; }
                    left -= next.Frame - Head;
                    Head = next.Frame;
                    MarksAtHead = false;
                    bool jumped = false;
                    foreach (XuiMark m in Marks)
                    {
                        if (m.Frame != Head) continue;
                        switch (m.Command)
                        {
                            case "Stop": if (!jumped) Playing = false; break;
                            case "GoTo": case "GoToAndPlay":
                                { int to = Find(m.Target); if (to >= 0) { Head = to; jumped = true; } }
                                break;
                            case "GoToAndStop":
                                { int to = Find(m.Target); if (to >= 0) Head = to; Playing = false; }
                                break;
                        }
                        if (jumped || !Playing) break;
                    }
                    if (reached != null && reached(next.Frame)) return;
                }
            }
        }

        private readonly Lane root;
        private readonly List<Lane> subs = new List<Lane>();

        /// <summary>The visual's own playhead, in frames.</summary>
        public float Head { get { return root.Head; } }
        public bool Playing { get { return root.Playing; } }

        public XuiPlayer(XuiVisualDef def)
        {
            Def = def;
            root = new Lane { Owner = 0, Marks = def.Marks };
            foreach (int o in def.Owners)
                if (o != 0) subs.Add(new Lane { Owner = o, Marks = def.Elements[o].Marks });
            // the visual's XuiSoundXAudio elements (Sounds.cs), from their own State and File
            for (int i = 0; i < def.Elements.Count; i++)
            {
                XuiElementDef e = def.Elements[i];
                if (e.Class != "XuiSoundXAudio") continue;
                string st, f;
                int s0;
                if (!e.Props.TryGetValue("State", out st) || !int.TryParse(st, out s0)) s0 = 0;
                e.Props.TryGetValue("File", out f);
                sounds.Add(i);
                voices.Add(new Sounds.Voice(s0, f));
            }
        }

        private readonly List<int> sounds = new List<int>();
        private readonly List<Sounds.Voice> voices = new List<Sounds.Voice>();

        /// <summary>Each sound element's State and File at the heads, applied (Sounds.Voice).</summary>
        private void UpdateSounds()
        {
            for (int k = 0; k < sounds.Count; k++)
            {
                int index = sounds[k], state = -1;
                string file = null;
                SampleSound(index, root, ref state, ref file);
                foreach (Lane l in subs) SampleSound(index, l, ref state, ref file);
                if (state >= 0) voices[k].Apply(state, file);
            }
        }

        private void SampleSound(int index, Lane lane, ref int state, ref string file)
        {
            foreach (XuiTrack t in Def.Elements[lane.Owner].Tracks)
            {
                if (t.TargetIndex != index) continue;
                for (int p = 0; p < t.Props.Length; p++)
                {
                    object v = XuiVisuals.Sample(t, p, lane.Head);
                    if (v == null) continue;
                    if (t.Props[p] == "State" && v is int) state = (int)v;
                    else if (t.Props[p] == "File") file = v as string;
                }
            }
        }

        /// <summary>The control built (message 0 -> 9 -> an implicit 2006, XUI_Control_MessageHandler).</summary>
        public void Build(bool enabled) { Enabled = enabled; Focused = false; State(false, false); }

        /// <summary>Focus moved to it or away (XUI_SetFocus: messages 12 / 13, an explicit 2006).</summary>
        public void SetFocus(bool focused, bool initFocus)
        {
            Focused = focused;
            State(true, initFocus);
        }

        /// <summary>Enabled or disabled (2003, an implicit 2006 on a change).</summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled == Enabled) return;
            Enabled = enabled;
            State(false, false);
        }

        /// <summary>XUI_Button_OnPress: true when taken; the action runs at the press range's end.</summary>
        public bool Press(Action action)
        {
            if (Pressed) return true;                          // pressed already (sub_9217D068)
            if (Enabled)
            {
                Pressed = true; pending = action;
                State(false, false);
                return true;
            }
            if (Def.Frame("PressDisable") >= 0 && Def.Frame("EndPressDisable") >= 0)
            {
                Pressed = true; pending = null;
                State(false, false);
                return true;
            }
            return false;
        }

        private void State(bool explicitMsg, bool initFocus)
        {
            if (Pressed)
            {
                // XUI_Button_PlayVisualState
                if (!Enabled) Play("PressDisable", "EndPressDisable");
                else if (Focused) Play("Press", "EndPress");
                else Play("NormalPress", "EndNormalPress");
                return;
            }
            if (Focused)
            {
                if (explicitMsg && initFocus) Play(Enabled ? "InitFocus" : "InitFocusDisable", Enabled ? "EndInitFocus" : "EndInitFocusDisable");
                else if (Enabled) Play("Focus", "EndFocus");
                else Play("FocusDisable", "EndFocusDisable");
            }
            else if (!Enabled) Play("NormalDisable", "EndNormalDisable");
            else if (!explicitMsg) Play("Normal", "EndNormal");
            else Play("KillFocus", "EndKillFocus");
        }

        /// <summary>XUI_Visual_PlayStateWithFallback: the pair, else its row's fallback chain.</summary>
        private void Play(string start, string end)
        {
            if (PlayPair(start, end)) return;
            string[][] t = XuiVisuals.FallbackPairs;
            for (int i = 0; i < t.Length; i++)
            {
                if (t[i][0] != start || t[i][1] != end) continue;
                for (int r = XuiVisuals.Fallback[i]; r >= 0; r = XuiVisuals.Fallback[r])
                    if (PlayPair(t[r][0], t[r][1])) return;
                return;
            }
        }

        /// <summary>The pair on the visual's timeline and on each child timeline that has it.</summary>
        private bool PlayPair(string start, string end)
        {
            if (!root.Set(start, end)) return false;
            endFrame = root.Find(end); endPosted = false;
            foreach (Lane l in subs) l.Set(start, end);
            Range = start;
            return true;
        }

        /// <summary>A range on element `owner`'s own timeline only (as the legend plays its buttons).</summary>
        public bool PlayOwn(int owner, string start, string end)
        {
            if (owner == 0) return root.Set(start, end);
            foreach (Lane l in subs) if (l.Owner == owner) return l.Set(start, end);
            return false;
        }

        /// <summary>The playheads, `seconds` on (XUI_Timeline_Advance, 60 frames a second).</summary>
        public void Advance(float seconds)
        {
            float frames = seconds * 60f;
            foreach (Lane l in subs) l.Advance(frames, null);
            root.Advance(frames, delegate(int f)
            {
                if (endPosted || endFrame < 0 || f < endFrame) return false;
                endPosted = true;
                return RangeEnded();
            });
            UpdateSounds();
        }

        /// <summary>Message 15 (XUI_Control_OnVisualTimelineEnd): a press ends.</summary>
        private bool RangeEnded()
        {
            if (!Pressed) return false;
            Pressed = false;
            Action a = pending; pending = null;
            if (Enabled && a != null) a();
            State(false, false);
            root.Head = endFrame; root.MarksAtHead = true;      // seek straight to its end frame
            return true;
        }

        /// <summary>Element `index`'s state at the head: its own properties, overridden by the tracks.</summary>
        public XuiElementState Evaluate(int index)
        {
            XuiElementDef d = Def.Elements[index];
            XuiElementState s = new XuiElementState
            {
                Position = d.Position, Scale = d.Scale, Pivot = d.Pivot, Rotation = d.Rotation,
                Opacity = d.Opacity, Show = d.Show, Width = d.Width, Height = d.Height,
            };
            if (d.Id == null) return s;
            Apply(ref s, index, root);
            foreach (Lane l in subs) Apply(ref s, index, l);
            return s;
        }

        /// <summary>The tracks of one owner that animate element `index`, at that owner's head.</summary>
        private void Apply(ref XuiElementState s, int index, Lane lane)
        {
            foreach (XuiTrack t in Def.Elements[lane.Owner].Tracks)
            {
                if (t.TargetIndex != index) continue;
                for (int p = 0; p < t.Props.Length; p++)
                {
                    object v = XuiVisuals.Sample(t, p, lane.Head);
                    if (v == null) continue;
                    switch (t.Props[p])
                    {
                        case "Position": s.Position = (Vector3)v; break;
                        case "Scale": s.Scale = (Vector3)v; break;
                        case "Pivot": s.Pivot = (Vector3)v; break;
                        case "Rotation": s.Rotation = (Quaternion)v; break;
                        case "Opacity": s.Opacity = (float)v; break;
                        case "Show": s.Show = (bool)v; break;
                        case "Width": s.Width = (float)v; break;
                        case "Height": s.Height = (float)v; break;
                    }
                }
            }
        }

        /// <summary>Element `index`'s matrix and opacity in the visual; false when hidden.</summary>
        public bool Global(int index, out Matrix m, out float opacity)
        {
            m = Matrix.Identity; opacity = 1f;
            for (int i = index; i >= 0; i = Def.Elements[i].Parent)
            {
                XuiElementState s = Evaluate(i);
                if (!s.Show) return false;
                m = m * s.Local;
                opacity *= s.Opacity;
                if (i == 0) break;
            }
            return true;
        }

        /// <summary>Global() by the element's Id; false when the visual has no such element.</summary>
        public bool Global(string id, out Matrix m, out float opacity)
        {
            int i = Def.Index(id);
            if (i < 0) { m = Matrix.Identity; opacity = 0f; return false; }
            return Global(i, out m, out opacity);
        }
    }

    /// <summary>One page's controls, each with its visual playing; a new page scene builds new ones.</summary>
    sealed class XuiControlSet
    {
        private readonly Dictionary<int, XuiPlayer> players = new Dictionary<int, XuiPlayer>();
        private long key = long.MinValue;
        private int focus = -1;
        private bool initFocus;

        /// <summary>Bring the set in line with the page: each control's visual, enabled state, and the focus.</summary>
        public void Sync(long sceneKey, bool firstPage, int count, Func<int, string> visual, Func<int, bool> enabled, int cursor)
        {
            if (sceneKey != key) { players.Clear(); key = sceneKey; focus = -1; initFocus = firstPage; }
            if (!XuiVisuals.Loaded) return;
            for (int i = 0; i < count; i++)
            {
                XuiPlayer p;
                bool on = enabled == null || enabled(i);
                string id = visual(i);
                if (players.TryGetValue(i, out p) && p.Def.Id != id) { players.Remove(i); p = null; if (focus == i) focus = -1; }
                if (p == null)
                {
                    XuiVisualDef d = XuiVisuals.Get(id);
                    if (d == null) continue;
                    p = new XuiPlayer(d);
                    p.Build(on);
                    players[i] = p;
                }
                else p.SetEnabled(on);
            }
            if (cursor != focus)
            {
                XuiPlayer p;
                if (focus >= 0 && players.TryGetValue(focus, out p)) p.SetFocus(false, false);
                if (cursor >= 0 && players.TryGetValue(cursor, out p)) p.SetFocus(true, initFocus);
                initFocus = false;
                focus = cursor;
            }
        }

        public void Advance(float seconds) { foreach (XuiPlayer p in players.Values) p.Advance(seconds); }

        public XuiPlayer this[int i] { get { XuiPlayer p; return players.TryGetValue(i, out p) ? p : null; } }

        /// <summary>A press on control i: the action at the press range's end; false when refused.</summary>
        public bool Press(int i, Action action)
        {
            XuiPlayer p = this[i];
            if (p == null) { action(); return true; }
            return p.Press(action);
        }

        /// <summary>A press is playing somewhere on the page.</summary>
        public bool Pressing { get { foreach (XuiPlayer p in players.Values) if (p.Pressed) return true; return false; } }
    }

    /// <summary>Drawing an element of a visual through XUI's transforms with the sprite batch.</summary>
    static class XuiDraw
    {
        /// <summary>A scene point to the screen: scnMain, then its perspective, then z flattened.</summary>
        public static readonly Matrix SceneToScreen = Build();

        private static Matrix Build()
        {
            Vector2 c = PageTransitions.Centre;
            Matrix p = Matrix.Identity; p.M34 = 0.001f;
            Matrix flat = Matrix.Identity; flat.M33 = 0f;
            return Matrix.CreateScale(Layout.SceneScale, Layout.SceneScale, 1f) * Matrix.CreateTranslation(Layout.SceneX, Layout.SceneY, 0f)
                 * Matrix.CreateTranslation(-c.X, -c.Y, 0f) * p * Matrix.CreateTranslation(c.X, c.Y, 0f) * flat;
        }

        /// <summary>Draws in `m`'s space until End (the batch is ended and begun again).</summary>
        public static void Begin(Ui ui, Matrix m)
        {
            ui.Batch.End();
            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp, DepthStencilState.None, RasterizerState.CullNone, null, m);
        }

        public static void End(Ui ui)
        {
            ui.Batch.End();
            ui.Batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        }

        /// <summary>A texture into (x, y, w, h) of the current space.</summary>
        public static void Image(Ui ui, Texture2D t, float x, float y, float w, float h, Color c)
        {
            ui.Batch.Draw(t, new Vector2(x, y), null, c, 0f, Vector2.Zero, new Vector2(w / t.Width, h / t.Height), SpriteEffects.None, 0f);
        }
    }
}
