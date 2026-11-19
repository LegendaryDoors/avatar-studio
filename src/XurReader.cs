// Reads XUI's compiled scenes (XUR version 8) and writes the editor's visuals.txt and legend.txt.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace AvatarStudio
{
    sealed class XurObject
    {
        public string Class;
        public readonly Dictionary<string, object> Props = new Dictionary<string, object>();
        public readonly List<XurObject> Children = new List<XurObject>();
        public List<string[]> NamedFrames;                  // name, frame, command, target or null
        public List<XurTimeline> Timelines;
    }

    sealed class XurTimeline
    {
        public string Target;
        public readonly List<string> Paths = new List<string>(), Types = new List<string>();
        public readonly List<XurKey> Keys = new List<XurKey>();
    }

    sealed class XurKey
    {
        public int Frame, Curve, Mode;
        public int[] Ease;                                  // curve 2's three signed bytes, else null
        public object[] Values;                             // null for a key past KEYD (skipped)
    }

    sealed class XurReader
    {
        // ---- the XUI schema: classes and their properties ----
        private const string Schema = @"
XuiElement||Id:s Width:f Height:f Position:v Scale:v Rotation:q Opacity:f Anchor:u Pivot:v Show:b BlendMode:u DisableTimelineRecursion:b DesignTime:b ColorWriteFlags:u ClipChildren:b EnableEffects:b DisableFocusRecursion:b GripTarget:b Hittable:b LayoutLineBreak:b LayoutFloat:b Column:u Row:u ColumnSpan:u RowSpan:u ColorFactor:c CenterPivot:b
XuiCanvas|XuiElement|
XuiGroup|XuiElement|
XuiVisual|XuiElement|
XuiFigure|XuiElement|Stroke:o Fill:o Closed:b Points:x
XuiFigureFill||FillType:u FillColor:c TextureFileName:s Gradient:o Translation:v Scale:v Rotation:f WrapX:u WrapY:u BrushFlags:u TransformVersion:u
XuiFigureFillGradient||Radial:b NumStops:i StopColor:C StopPos:F
XuiFigureStroke||StrokeWidth:f StrokeColor:c
XuiControl|XuiElement|ClassOverride:s Visual:s Enabled:b UnfocusedInput:b NavLeft:s NavRight:s NavUp:s NavDown:s NavTabForward:s NavTabBackward:s Text:s PointSize:f ImagePath:s HasContextMenu:b SizeToText:b UseNuiAsMouse:b AutoId:s HoverSelectTimer:f QuickInput:b
XuiScene|XuiControl|DefaultFocus:s TransFrom:s TransTo:s TransBackFrom:s TransBackTo:s InterruptTransitions:u IgnorePresses:b RecurseTransitions:b
XuiTabScene|XuiScene|TabCount:u Wrap:b UserInterrupt:b VerticalTabs:b NoAutoHide:b DefaultTab:u
XuiLabel|XuiControl|MaxFlowLines:u
XuiEdit|XuiControl|TextLimit:i AllowedChars:s PasswordChar:s ReadOnly:b Multiline:b SmoothScroll:b
XuiButton|XuiControl|PressKey:u PressAnimObject:s PressAnimStartFrame:s PressAnimEndFrame:s FocusAnimObject:s FocusAnimStartFrame:s FocusAnimEndFrame:s
XuiMessageBoxButton|XuiButton|MessageBoxCenter:b MessageBoxVisual:s MessageBoxTitle:s MessageBoxText:s MessageBoxButtons:s
XuiNavButton|XuiButton|PressPath:s StayVisible:b SrcTransIndex:u DestTransIndex:u
XuiBackButton|XuiButton|
XuiCheckbox|XuiControl|PressKey:u
XuiRadioButton|XuiControl|PressKey:u
XuiProgressBar|XuiControl|RangeMin:i RangeMax:i Value:i
XuiSlider|XuiControl|RangeMin:i RangeMax:i Value:i Step:i Vertical:b AccelInc:i AccelTime:u
XuiList|XuiControl|Wrap:b WrapBump:b
XuiCommonList|XuiList|ItemsText:s ItemsImage:s ItemsNavPath:s
XuiComboBox|XuiControl|ComboBoxStyle:u MaxVisibleVertItems:i
XuiImage|XuiElement|SizeMode:u ImagePath:s BrushFlags:u TextureSurfaceElement:s LoadType:u
XuiImagePresenter|XuiElement|SizeMode:u DataAssociation:u BrushFlags:u LoadType:u
XuiText|XuiElement|Text:s TextColor:c DropShadowColor:c PointSize:f Font:s TextStyle:u LineSpacingAdjust:i TextScale:f
XuiTextPresenter|XuiElement|TextColor:c DropShadowColor:c PointSize:f Font:s TextStyle:u LineSpacingAdjust:i DataAssociation:u TextScale:f
XuiNineGrid|XuiElement|TextureFileName:s LeftOffset:u TopOffset:u RightOffset:u BottomOffset:u NoCenter:b
XuiRadioGroup|XuiControl|
XuiScrollEnd|XuiControl|Direction:u
XuiScrollBar|XuiControl|Direction:u MinThumbSize:u
XuiVideo|XuiElement|File:s SizeMode:u Pause:b Loop:b Volume:f
XuiSound|XuiElement|State:u Loop:b Finish:b Volume:f
XuiSoundXAudio|XuiSound|File:s
XuiSoundXACT|XuiSound|Cue:s SoundBank:s WaveBank:s
XuiListItem|XuiCheckbox|Layout:u Checkable:b SelectedSize:v KeepSizeUnfocused:b InterItemSpacing:v SmoothScroll:b SmoothScrollBaseSpeed:f SmoothScrollMaxSpeed:f SmoothScrollAcceleration:f
XuiCaret|XuiControl|
XuiHtmlElement|XuiElement|TeletypeCount:i
XuiHtmlControl|XuiControl|TeletypeCount:i
XuiHtmlPresenter|XuiElement|DataAssociation:i
XuiTextureSurface|XuiGroup|Offscreen:b DepthStencil:b PreRender:b Buffered:b
XuiGridPanel|XuiElement|Columns:s Rows:s CellSpacing:f Param0:f Param1:f Param2:f Param3:f
ControlPackLegendScene|XuiScene|LeftEdgeGutter:f RightEdgeGutter:f
ControlPackAuraControl|XuiElement|ThemeImageIndex:u BackgroundImage:s SurfaceSphere:b BannerImage:s BackgroundImagePanScale:f SurfaceVignette:b
ControlPackLegacyControl|XuiControl|
XuiTwist|XuiControl|Wrap:b Center:b
ControlPackNuiHoverButton|XuiButton|Handedness:u Magnetism:b NuiFastAttachRadius:f NuiSlowAttachRadius:f NuiSlowDetachRadius:f NuiApproachRadius:f MagnetOffset:v
ControlPackVuiBling|XuiControl|
ControlPackNuiBack|XuiControl|AutoDisableHandle:b
MultiImage|XuiControl|ForegroundImgDataAssociation:u BackgroundImgDataAssociation:u
XuiTouch|XuiControl|PositionOffset:v HorizontalScrub:b HorizontalFling:b HorizontalRubberBand:b HorizontalClamp:b VerticalScrub:b VerticalFling:b VerticalRubberBand:b VerticalClamp:b CenterSmallCanvas:b StretchToFit:b EnableBandBreak:b Hollow:b
XuiAvatar|XuiControl|UseActiveUserAvatar:b EnableUserControl:b UseCustomView:b RenderView:u CustomViewEyePosition:v CustomViewFocusPosition:v CustomViewUpDirection:v ShowSelfShadow:b ShowShadow:b ShowCarryable:b ShowHat:b FreezeAvatar:b UseAvateering:b FullBodyAvateering:b AvateeringTrackingID:u
";

        private sealed class PropDef { public string Name, Type; public bool Indexed; }
        private sealed class ClassDef { public string Base; public PropDef[] Props; }
        private static readonly Dictionary<string, ClassDef> Classes = LoadSchema();
        private static readonly Dictionary<string, string> CompoundFor = new Dictionary<string, string>
        { { "Stroke", "XuiFigureStroke" }, { "Fill", "XuiFigureFill" }, { "Gradient", "XuiFigureFillGradient" } };

        private static Dictionary<string, ClassDef> LoadSchema()
        {
            Dictionary<string, string> types = new Dictionary<string, string>
            {
                { "s", "string" }, { "f", "float" }, { "v", "vector" }, { "q", "quaternion" }, { "c", "colour" },
                { "b", "bool" }, { "u", "unsigned" }, { "i", "integer" }, { "o", "object" }, { "x", "custom" },
            };
            Dictionary<string, ClassDef> d = new Dictionary<string, ClassDef>();
            foreach (string raw in Schema.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                string[] f = line.Split(new[] { '|' }, 3);
                List<PropDef> props = new List<PropDef>();
                foreach (string item in f[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int c = item.LastIndexOf(':');
                    string code = item.Substring(c + 1);
                    props.Add(new PropDef { Name = item.Substring(0, c), Indexed = code != code.ToLowerInvariant(), Type = types[code.ToLowerInvariant()] });
                }
                d[f[0]] = new ClassDef { Base = f[1].Length > 0 ? f[1] : null, Props = props.ToArray() };
            }
            return d;
        }

        private static List<string> Hierarchy(string name)
        {
            List<string> chain = new List<string>();
            for (string cur = name; cur != null; cur = Classes[cur].Base)
            {
                if (!Classes.ContainsKey(cur)) throw new InvalidDataException("unknown XUI class '" + cur + "' (schema gap)");
                chain.Add(cur);
            }
            chain.Reverse();
            return chain;
        }

        // ---- the file ----------------------------------------------------------------------------
        private readonly byte[] d;
        private int p, end;
        private readonly List<string> strings = new List<string> { "" };
        private readonly List<double[]> vect = new List<double[]>(), quat = new List<double[]>();
        private readonly List<double> flot = new List<double>();
        private readonly List<string> colr = new List<string>();
        private readonly List<int> custOffsets = new List<int>();
        private readonly List<string[]> named = new List<string[]>();
        private readonly List<long> keyp = new List<long>();
        private readonly List<XurKey> keydKeys = new List<XurKey>();
        private readonly List<long> keydBase = new List<long>();
        private readonly List<Dictionary<string, object>> propertyLists = new List<Dictionary<string, object>>();
        private readonly List<KeyValuePair<string, Dictionary<string, object>>> compounds = new List<KeyValuePair<string, Dictionary<string, object>>>();
        public XurObject Root;

        private static readonly string[] Commands = { "Play", "Stop", "GoTo", "GoToAndPlay", "GoToAndStop" };

        public XurReader(byte[] data)
        {
            d = data;
            if (d.Length < 0x14 || d[0] != 'X' || d[1] != 'U' || d[2] != 'I' || d[3] != 'B') throw new InvalidDataException("not a XUR");
            if (U32(4) != 8) throw new InvalidDataException("XUR version " + U32(4) + ", this reads 8");
            if (U32(0x0E) != d.Length) throw new InvalidDataException("header file size " + U32(0x0E) + " != " + d.Length);
            int sections = (d[0x12] << 8) | d[0x13];
            p = 0x14; end = d.Length;
            for (int i = 0; i < 12; i++) Packed();
            int table = p;
            Dictionary<string, int[]> sec = new Dictionary<string, int[]>();
            for (int i = 0; i < sections; i++)
            {
                string magic = Encoding.ASCII.GetString(d, table + i * 12, 4);
                sec[magic] = new[] { (int)U32(table + i * 12 + 4), (int)U32(table + i * 12 + 8) };
            }
            int[] s;
            if (sec.TryGetValue("STRN", out s)) ReadStrn(s[0], s[1]);
            if (sec.TryGetValue("VECT", out s)) ReadFloats(s[0], s[1], 3, vect);
            if (sec.TryGetValue("QUAT", out s)) ReadFloats(s[0], s[1], 4, quat);
            if (sec.TryGetValue("FLOT", out s)) { List<double[]> f = new List<double[]>(); ReadFloats(s[0], s[1], 1, f); foreach (double[] v in f) flot.Add(v[0]); }
            if (sec.TryGetValue("COLR", out s))
                for (int i = 0; i + 4 <= s[1]; i += 4)
                    colr.Add(string.Format("#{0:X2}{1:X2}{2:X2}{3:X2}", d[s[0] + i], d[s[0] + i + 1], d[s[0] + i + 2], d[s[0] + i + 3]));
            if (sec.TryGetValue("CUST", out s))
            {
                // only the offsets are kept; figures are not exported
                p = s[0]; end = s[0] + s[1];
                while (p < end)
                {
                    custOffsets.Add(p - s[0]);
                    p += 16 + (int)U32(p + 12) * 24;
                }
            }
            if (sec.TryGetValue("NAME", out s))
            {
                p = s[0]; end = s[0] + s[1];
                while (p < end)
                {
                    string name = Str(Packed());
                    long frame = Packed();
                    int cmd = d[p++];
                    string target = cmd == 2 || cmd == 3 || cmd == 4 ? Str(Packed()) : null;
                    named.Add(new[] { name, frame.ToString(CultureInfo.InvariantCulture), cmd < 5 ? Commands[cmd] : "unknown(" + cmd + ")", target });
                }
            }
            if (sec.TryGetValue("KEYP", out s)) { p = s[0]; end = s[0] + s[1]; while (p < end) keyp.Add(Packed()); }
            if (sec.TryGetValue("KEYD", out s))
            {
                p = s[0]; end = s[0] + s[1];
                while (p < end)
                {
                    XurKey k = new XurKey { Frame = (int)Packed() };
                    int fb = d[p++];
                    k.Curve = fb & 0x3F; k.Mode = fb >> 6;
                    if (k.Curve == 2) { k.Ease = new int[3]; for (int i = 0; i < 3; i++) k.Ease[i] = (sbyte)d[p++]; }
                    else if (k.Curve == 7 || k.Curve == 10 || k.Curve == 11 || k.Curve == 12) Packed();
                    keydBase.Add(Packed());
                    keydKeys.Add(k);
                }
            }
            if (sec.TryGetValue("DATA", out s))
            {
                p = s[0]; end = s[0] + s[1];
                Root = ReadObject();
                if (p != end) throw new InvalidDataException((end - p) + " DATA bytes left over after the tree");
            }
        }

        private void ReadStrn(int off, int size)
        {
            int n = (d[off + 4] << 8) | d[off + 5];
            int total = (int)U32(off), q = off + 6, units = 0;
            for (int i = 0; i < n; i++)
            {
                int e = Array.IndexOf(d, (byte)0, q);
                string t = new UTF8Encoding(false, false).GetString(d, q, e - q);
                strings.Add(t);
                units += t.Length + 1;                     // UTF-16 units, the NUL included (sub_9218C8E8)
                q = e + 1;
            }
            if (q != off + size) throw new InvalidDataException("STRN: " + (off + size - q) + " bytes left over");
            if (total != units) throw new InvalidDataException("STRN: declared length " + total + " != " + units + " UTF-16 units");
        }

        private void ReadFloats(int off, int size, int n, List<double[]> into)
        {
            if (size % (4 * n) != 0) throw new InvalidDataException("float section not a multiple of " + n);
            for (int i = 0; i < size / (4 * n); i++)
            {
                double[] v = new double[n];
                for (int k = 0; k < n; k++) v[k] = F32(off + (i * n + k) * 4);
                into.Add(v);
            }
        }

        private XurObject ReadObject()
        {
            XurObject o = new XurObject { Class = Str(Packed()) };
            int flags = d[p++];
            if ((flags & 0x01) != 0)
            {
                long count = Packed(), got = 0;
                Dictionary<string, object> props = new Dictionary<string, object>();
                foreach (string cname in Hierarchy(o.Class))
                {
                    PropDef[] defs = Classes[cname].Props;
                    long mask = Packed();
                    for (int bit = 0; bit < 64 && (mask >> bit) != 0; bit++)
                    {
                        if (((mask >> bit) & 1) == 0) continue;
                        if (bit >= defs.Length) throw new InvalidDataException(cname + " in " + o.Class + ": mask bit " + bit + " past schema");
                        int n;
                        props[defs[bit].Name] = ReadProperty(defs[bit], out n);
                        got += n;
                    }
                }
                if (got != count) throw new InvalidDataException(o.Class + ": " + got + " properties read, header said " + count);
                propertyLists.Add(props);
                foreach (KeyValuePair<string, object> kv in props) o.Props[kv.Key] = kv.Value;
            }
            else if ((flags & 0x08) != 0)
                foreach (KeyValuePair<string, object> kv in propertyLists[(int)Packed()]) o.Props[kv.Key] = kv.Value;
            if ((flags & 0x02) != 0)
            {
                long n = Packed();
                for (long i = 0; i < n; i++) o.Children.Add(ReadObject());
            }
            if ((flags & 0x04) != 0)
            {
                long nf = Packed();
                o.NamedFrames = new List<string[]>();
                if (nf != 0)
                {
                    int b = (int)Packed();
                    for (int i = b; i < b + nf && i < named.Count; i++) o.NamedFrames.Add(named[i]);
                }
                if ((flags & 0x02) != 0)
                {
                    long tl = Packed();
                    if (tl != 0)
                    {
                        o.Timelines = new List<XurTimeline>();
                        for (long i = 0; i < tl; i++) o.Timelines.Add(ReadTimeline(o));
                    }
                }
            }
            return o;
        }

        private object ReadProperty(PropDef pd, out int n)
        {
            if (pd.Indexed)
            {
                n = d[p++];
                List<object> l = new List<object>();
                for (int i = 0; i < n; i++) l.Add(ReadValue(pd));
                return l;
            }
            n = 1;
            return ReadValue(pd);
        }

        private object ReadValue(PropDef pd)
        {
            switch (pd.Type)
            {
                case "bool": return d[p++] != 0;
                case "integer": case "unsigned": return Packed();
                case "object": return ReadCompound(pd);
                case "custom": Packed(); return new Dictionary<string, object>();     // a figure: never written
            }
            return Resolve(pd.Type, Packed());
        }

        private Dictionary<string, object> ReadCompound(PropDef pd)
        {
            string cname;
            if (!CompoundFor.TryGetValue(pd.Name, out cname)) throw new InvalidDataException("compound property " + pd.Name + " has no known class");
            long idx = Packed();
            if (idx < compounds.Count)
            {
                if (compounds[(int)idx].Key != cname) throw new InvalidDataException("compound " + idx + " is " + compounds[(int)idx].Key + ", wanted " + cname);
                return compounds[(int)idx].Value;
            }
            long count = Packed(), mask = Packed(), got = 0;
            PropDef[] defs = Classes[cname].Props;
            Dictionary<string, object> props = new Dictionary<string, object>();
            for (int bit = 0; bit < 64 && (mask >> bit) != 0; bit++)
            {
                if (((mask >> bit) & 1) == 0) continue;
                if (bit >= defs.Length) throw new InvalidDataException(cname + " mask bit " + bit + " past schema");
                int n;
                props[defs[bit].Name] = ReadProperty(defs[bit], out n);
                got += n;
            }
            if (got != count) throw new InvalidDataException(cname + ": " + got + " properties read, header said " + count);
            compounds.Add(new KeyValuePair<string, Dictionary<string, object>>(cname, props));
            return props;
        }

        private static XurObject FindById(XurObject o, string id)
        {
            object v;
            if (o.Props.TryGetValue("Id", out v) && (v as string) == id) return o;
            foreach (XurObject c in o.Children) { XurObject g = FindById(c, id); if (g != null) return g; }
            return null;
        }

        private XurTimeline ReadTimeline(XurObject parent)
        {
            XurTimeline t = new XurTimeline { Target = Str(Packed()) };
            XurObject target = FindById(parent, t.Target);
            List<string> leafFirst = null;
            if (target != null) { leafFirst = Hierarchy(target.Class); leafFirst.Reverse(); }
            long np = Packed();
            for (long i = 0; i < np; i++)
            {
                int packedByte = d[p++];
                int depth = packedByte & 0x7F;
                bool indexed = (packedByte & 0x80) != 0;
                int clsIndex = d[p++];
                List<string> path = new List<string>();
                PropDef pd = null;
                PropDef[] cur = leafFirst != null && clsIndex < leafFirst.Count ? Classes[leafFirst[clsIndex]].Props : null;
                for (int step = 0; step < depth; step++)
                {
                    int pi = d[p++];
                    if (cur != null && pi < cur.Length)
                    {
                        pd = cur[pi];
                        path.Add(pd.Name);
                        string next;
                        cur = CompoundFor.TryGetValue(pd.Name, out next) ? Classes[next].Props : null;
                    }
                    else { path.Add("prop#" + pi); pd = null; }
                }
                t.Paths.Add(string.Join(".", path.ToArray()));
                t.Types.Add(pd != null ? pd.Type : null);
                if (indexed) Packed();
            }
            long nk = Packed(), b = Packed();
            for (long k = 0; k < nk; k++)
            {
                if (b + k >= keydKeys.Count) { t.Keys.Add(new XurKey()); continue; }       // MISSING_KEYD: not written
                XurKey src = keydKeys[(int)(b + k)];
                XurKey key = new XurKey { Frame = src.Frame, Curve = src.Curve, Mode = src.Mode, Ease = src.Ease, Values = new object[t.Paths.Count] };
                long kb = keydBase[(int)(b + k)];
                for (int i = 0; i < t.Paths.Count; i++)
                {
                    long slot = kb + i;
                    if (slot >= keyp.Count) { key.Values[i] = null; continue; }
                    long raw = keyp[(int)slot];
                    string type = t.Types[i] ?? "unsigned";
                    key.Values[i] = type == "bool" ? (object)(raw != 0)
                                  : type == "integer" || type == "unsigned" || type == "object" || type == "custom" ? (object)raw
                                  : Resolve(type, raw);
                }
                t.Keys.Add(key);
            }
            return t;
        }

        private object Resolve(string type, long raw)
        {
            switch (type)
            {
                case "string": return raw < strings.Count ? strings[(int)raw] : "<str#" + raw + "?>";
                case "float": return raw < flot.Count ? (object)flot[(int)raw] : "<flot#" + raw + "?>";
                case "vector": return raw < vect.Count ? (object)vect[(int)raw] : "<vect#" + raw + "?>";
                case "quaternion": return raw < quat.Count ? (object)quat[(int)raw] : "<quat#" + raw + "?>";
                case "colour": return raw < colr.Count ? (object)colr[(int)raw] : "<colr#" + raw + "?>";
            }
            return raw;
        }

        private string Str(long i) { return i < strings.Count ? strings[(int)i] : "<str#" + i + "?>"; }

        private long Packed()
        {
            int b = d[p++];
            if (b < 0xF0) return b;
            if (b == 0xFF) { long v = U32(p); p += 4; return v; }
            return ((b << 8) & 0xF00) | d[p++];
        }

        private uint U32(int o) { return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]); }

        private double F32(int o) { return BitConverter.ToSingle(BitConverter.GetBytes(U32(o)), 0); }

        // ---- export -------------------------------------------------------------------------

        /// <summary>Every `rootClass` subtree as visuals.txt / legend.txt lines.</summary>
        public static int Export(byte[] xur, string fileName, string outPath, string rootClass)
        {
            XurReader r = new XurReader(xur);
            List<string> lines = new List<string>
            {
                string.Format("# the editor's XUI {0} trees, read from {1} by Avatar Studio's setup.", rootClass, fileName),
                "# visual|element <depth> <class> <Id> then name=value; frame <name> <frame> <command> [<target>];",
                "# track <target Id> <prop,...> <type,...>; key <frame> <curve> <mode> <in,out,scale|-> <value>...;",
                "# vectors and quaternions comma-separated, bools 1 / 0. Owned by the last visual or element line.",
            };
            int count = 0;
            Walk(r.Root, rootClass, lines, ref count);
            StringBuilder sb = new StringBuilder();
            foreach (string l in lines) sb.Append(l).Append('\n');
            byte[] bytes = new byte[sb.Length];
            for (int i = 0; i < sb.Length; i++) bytes[i] = sb[i] < 128 ? (byte)sb[i] : (byte)'?';   // ascii, errors="replace"
            SetupInstall.WriteFile(outPath, bytes);
            return count;
        }

        private static void Walk(XurObject o, string rootClass, List<string> lines, ref int count)
        {
            if (o.Class == rootClass) { Emit(o, 0, lines); lines.Add("end"); count++; return; }
            foreach (XurObject c in o.Children) Walk(c, rootClass, lines, ref count);
        }

        private static void Emit(XurObject o, int depth, List<string> lines)
        {
            object id;
            List<string> cells = new List<string>
            {
                depth == 0 ? "visual" : "element\t" + depth, o.Class,
                o.Props.TryGetValue("Id", out id) && !string.IsNullOrEmpty(id as string) ? (string)id : "-",
            };
            List<string> keys = new List<string>(o.Props.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                if (k == "Id" || k == "_class") continue;
                string s = Fmt(o.Props[k]);
                if (s != null) cells.Add(k + "=" + s);
            }
            lines.Add(string.Join("\t", cells.ToArray()));
            if (o.NamedFrames != null)
                foreach (string[] nf in o.NamedFrames)
                    lines.Add("frame\t" + nf[0] + "\t" + nf[1] + "\t" + nf[2] + (nf[3] != null ? "\t" + nf[3] : ""));
            if (o.Timelines != null)
                foreach (XurTimeline t in o.Timelines)
                {
                    List<string> types = new List<string>();
                    foreach (string ty in t.Types) types.Add(ty ?? "-");
                    lines.Add("track\t" + t.Target + "\t" + string.Join(",", t.Paths.ToArray()) + "\t" + string.Join(",", types.ToArray()));
                    foreach (XurKey k in t.Keys)
                    {
                        if (k.Values == null) continue;
                        List<string> c = new List<string>
                        {
                            "key", k.Frame.ToString(CultureInfo.InvariantCulture), k.Curve.ToString(CultureInfo.InvariantCulture),
                            k.Mode.ToString(CultureInfo.InvariantCulture),
                            k.Ease != null ? k.Ease[0] + "," + k.Ease[1] + "," + k.Ease[2] : "-",
                        };
                        foreach (object v in k.Values) { string s = Fmt(v); c.Add(string.IsNullOrEmpty(s) ? "-" : s); }
                        lines.Add(string.Join("\t", c.ToArray()));
                    }
                }
            foreach (XurObject ch in o.Children) Emit(ch, depth + 1, lines);
        }

        /// <summary>A value as text: none '-', bools 1 / 0, floats %.9g, lists comma-joined.</summary>
        private static string Fmt(object v)
        {
            if (v == null) return "-";
            if (v is bool) return (bool)v ? "1" : "0";
            if (v is double) return G9((double)v);
            if (v is double[])
            {
                List<string> l = new List<string>();
                foreach (double x in (double[])v) l.Add(G9(x));
                return string.Join(",", l.ToArray());
            }
            if (v is List<object>)
            {
                List<string> l = new List<string>();
                foreach (object x in (List<object>)v) l.Add(Fmt(x));
                return string.Join(",", l.ToArray());
            }
            if (v is Dictionary<string, object>) return null;
            if (v is long) return ((long)v).ToString(CultureInfo.InvariantCulture);
            return v.ToString().Replace("\t", " ").Replace("\n", " ");
        }

        /// <summary>C's %.9g: nine significant digits, rounded half-even, trailing zeros dropped.</summary>
        public static string G9(double v)
        {
            if (double.IsNaN(v)) return "nan";
            if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
            long bits = BitConverter.DoubleToInt64Bits(v);
            bool neg = bits < 0;
            if (v == 0) return neg ? "-0" : "0";
            int e2 = (int)((bits >> 52) & 0x7FF);
            long man = bits & 0xFFFFFFFFFFFFFL;
            if (e2 == 0) e2 = 1; else man |= 1L << 52;
            e2 -= 1075;
            BigInteger num = e2 >= 0 ? new BigInteger(man) << e2 : new BigInteger(man);
            BigInteger den = e2 >= 0 ? BigInteger.One : BigInteger.One << -e2;
            int e10 = (int)Math.Floor(Math.Log10(Math.Abs(v)));
            BigInteger q, rem, lo = BigInteger.Pow(10, 8), hi = BigInteger.Pow(10, 9);
            while (true)
            {
                int s = 8 - e10;
                BigInteger n = s >= 0 ? num * BigInteger.Pow(10, s) : num, dd = s >= 0 ? den : den * BigInteger.Pow(10, -s);
                q = BigInteger.DivRem(n, dd, out rem);
                if (q < lo) { e10--; continue; }
                if (q >= hi) { e10++; continue; }
                BigInteger twice = rem * 2;
                if (twice > dd || (twice == dd && !q.IsEven)) q += 1;
                if (q >= hi) { q /= 10; e10++; }
                break;
            }
            string digits = q.ToString(CultureInfo.InvariantCulture);
            string r;
            if (e10 < -4 || e10 >= 9)
            {
                string rest = digits.Substring(1).TrimEnd('0');
                r = digits[0] + (rest.Length > 0 ? "." + rest : "") + "e" + (e10 < 0 ? "-" : "+") + Math.Abs(e10).ToString("00", CultureInfo.InvariantCulture);
            }
            else if (e10 >= 0)
            {
                string frac = digits.Substring(e10 + 1).TrimEnd('0');
                r = digits.Substring(0, e10 + 1) + (frac.Length > 0 ? "." + frac : "");
            }
            else r = "0." + new string('0', -e10 - 1) + digits.TrimEnd('0');
            return neg ? "-" + r : r;
        }

        /// <summary>Every .png ImagePath in the tree.</summary>
        public static SortedSet<string> PngImages(byte[] xur)
        {
            SortedSet<string> s = new SortedSet<string>(StringComparer.Ordinal);
            Images(new XurReader(xur).Root, s);
            return s;
        }

        private static void Images(XurObject o, SortedSet<string> into)
        {
            object v;
            if (o.Props.TryGetValue("ImagePath", out v) && v is string && ((string)v).ToLowerInvariant().EndsWith(".png")) into.Add((string)v);
            foreach (XurObject c in o.Children) Images(c, into);
        }
    }
}
