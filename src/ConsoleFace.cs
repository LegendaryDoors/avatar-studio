// The console's UI face, ConvectionUI (XAM's XenonJKLatin.xtt and its patch), read and
// rasterised as the editor's font code does it.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace AvatarStudio
{
    /// <summary>One .xtt / .xttp file, read as sub_921B7BB0 / sub_921C0BA8 / sub_921C0718 read it.</summary>
    sealed class XttFace
    {
        private readonly byte[] file, dir;
        private readonly int glyfOff, xchkOff, locaOff, hmtxOff, cmapSub, blockCap;
        private readonly Dictionary<int, byte[]> blocks = new Dictionary<int, byte[]>();
        private readonly SHA1 sha = SHA1.Create();

        public readonly int Glyphs, UnitsPerEm, Ascender, Descender, LineGap, HMetrics;
        /// <summary>head +16 (sub_921C0BA8 keeps it as the record's word 190): bit 1 set, no lsb shift.</summary>
        public readonly int HeadFlags;
        /// <summary>The record's word 182: lineGap - descender + ascender, as an int16.</summary>
        public readonly int LineUnits;
        public readonly byte[] Signature = new byte[256], Reference = new byte[256];

        public XttFace(string path) : this(File.ReadAllBytes(path), path) { }

        /// <summary>A file already in memory; `path` names it in errors.</summary>
        public XttFace(byte[] bytes, string path)
        {
            file = bytes;
            if (file.Length < 0x118 || file[0] != 'x' || file[1] != 't' || file[2] != 't' || file[3] != 'f')
                throw new InvalidDataException(Path.GetFileName(path) + ": no 'xttf' header");
            Buffer.BlockCopy(file, 4, Signature, 0, 256);
            int signedSize = (int)U32(file, 0x104), fileSize = (int)U32(file, 0x108);
            int comp = (int)U32(file, 0x10C), uncomp = (int)U32(file, 0x110), version = (int)U32(file, 0x114);
            if (version != 0x10000 || fileSize != file.Length || signedSize > fileSize - 256)
                throw new InvalidDataException(Path.GetFileName(path) + ": header refused");
            dir = Inflate(file, 0x118, comp, uncomp);

            int n = U16(dir, 4), cmap = -1, head = -1, hhea = -1, xttf = -1;
            for (int i = 0; i < n; i++)
            {
                int e = 12 + 16 * i;
                string tag = "" + (char)dir[e] + (char)dir[e + 1] + (char)dir[e + 2] + (char)dir[e + 3];
                int off = (int)U32(dir, e + 8), len = (int)U32(dir, e + 12);
                switch (tag)
                {
                    case "cmap": cmap = off; break;
                    case "head": head = off; break;
                    case "hhea": hhea = off; break;
                    case "hmtx": hmtxOff = off; break;
                    case "xloc": locaOff = off; Glyphs = len / 4 - 1; break;
                    case "xglf": glyfOff = off; break;
                    case "xchk": xchkOff = off; break;
                    case "xttf": xttf = off; break;
                }
            }
            if (cmap < 0 || head < 0 || hhea < 0 || xttf < 0 || Glyphs <= 0 || U32(dir, head + 12) != 0x5F0F3CF5)
                throw new InvalidDataException(Path.GetFileName(path) + ": tables missing");
            UnitsPerEm = U16(dir, head + 18);
            HeadFlags = U16(dir, head + 16);
            Ascender = S16(dir, hhea + 4); Descender = S16(dir, hhea + 6); LineGap = S16(dir, hhea + 8);
            LineUnits = (short)(LineGap - Descender + Ascender);
            HMetrics = U16(dir, hhea + 34);
            blockCap = (int)U32(dir, xttf + 4);
            if (blockCap == 0) blockCap = 0x4000;                 // sub_921C0718: 0 means 0x4000
            Buffer.BlockCopy(dir, xttf + 8, Reference, 0, 256);

            // sub_921C0BA8: every encoding record's subtable of format 0 or 4; the last one wins
            cmapSub = -1;
            int records = U16(dir, cmap + 2);
            for (int i = 0; i < records; i++)
            {
                int sub = cmap + (int)U32(dir, cmap + 4 + 8 * i + 4);
                int fmt = U16(dir, sub);
                if (fmt == 0 || fmt == 4) cmapSub = sub;
            }
            if (cmapSub < 0) throw new InvalidDataException(Path.GetFileName(path) + ": no format 0 / 4 cmap");
        }

        /// <summary>The glyph a character maps to, 0 when none.</summary>
        public int GlyphIndex(int ch)
        {
            if (U16(dir, cmapSub) == 0) return ch < 256 ? dir[cmapSub + 6 + ch] : 0;
            int segX2 = U16(dir, cmapSub + 6), seg = segX2 / 2;
            int endsAt = cmapSub + 14, startsAt = endsAt + segX2 + 2, deltasAt = startsAt + segX2, roffAt = deltasAt + segX2;
            for (int i = 0; i < seg; i++)
            {
                if (U16(dir, endsAt + 2 * i) < ch) continue;
                int start = U16(dir, startsAt + 2 * i);
                if (start > ch) return 0;
                int delta = S16(dir, deltasAt + 2 * i), roff = U16(dir, roffAt + 2 * i);
                if (roff == 0) return (ch + delta) & 0xFFFF;
                int g = U16(dir, roffAt + 2 * i + roff + 2 * (ch - start));
                return g == 0 ? 0 : (g + delta) & 0xFFFF;
            }
            return 0;
        }

        /// <summary>hmtx's advance width, in font units.</summary>
        public int Advance(int g)
        {
            return U16(dir, hmtxOff + 4 * Math.Min(g, HMetrics - 1));
        }

        /// <summary>The left side bearing as sub_921BFEF8 reads it: the long metric's.</summary>
        public int LeftSideBearing(int g)
        {
            return S16(dir, hmtxOff + 4 * Math.Min(g, HMetrics - 1) + 2);
        }

        /// <summary>The glyph's glyf record (sub_921C0718), or null for an empty glyph.</summary>
        public byte[] GlyphData(int g, out int at)
        {
            at = 0;
            if (g < 0 || g >= Glyphs) return null;
            int e = locaOff + 4 * g;
            int b0 = U16(dir, e), o0 = U16(dir, e + 2), b1 = U16(dir, e + 4), o1 = U16(dir, e + 6);
            byte[] blk = Block(b0);
            int end = b1 == b0 ? o1 : blk.Length;
            if (end <= o0) return null;
            at = o0;
            return blk;
        }

        private byte[] Block(int b)
        {
            byte[] blk;
            if (blocks.TryGetValue(b, out blk)) return blk;
            int src = glyfOff + (b << 12);
            byte[] hash = sha.ComputeHash(file, src, 4096);
            for (int i = 0; i < 20; i++)
                if (hash[i] != dir[xchkOff + 20 * b + i])
                    throw new InvalidDataException("glyph block " + b + " fails its xchk SHA-1");
            blk = Inflate(file, src, 4096, blockCap);
            blocks[b] = blk;
            return blk;
        }

        /// <summary>A zlib stream (2-byte header, raw deflate) inflated to at most `max` bytes.</summary>
        private static byte[] Inflate(byte[] src, int off, int len, int max)
        {
            using (MemoryStream ms = new MemoryStream(src, off + 2, len - 2))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int r;
                while ((r = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    o.Write(buf, 0, r);
                    if (o.Length > max) throw new InvalidDataException("inflates past 0x" + max.ToString("x"));
                }
                return o.ToArray();
            }
        }

        public static int U16(byte[] b, int o) { return (b[o] << 8) | b[o + 1]; }
        public static int S16(byte[] b, int o) { return (short)((b[o] << 8) | b[o + 1]); }
        public static uint U32(byte[] b, int o) { return (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]); }
    }

    /// <summary>A base face and its patch, paired as XUI's TrueType provider pairs them.</summary>
    sealed class XuiFace
    {
        public readonly XttFace Base, Patch;
        public XuiFace(XttFace b, XttFace patch) { Base = b; Patch = patch; }
    }

    /// <summary>The editor's face, ConvectionUI, and glyphs drawn from any XuiFace.</summary>
    static class ConsoleFace
    {
        /// <summary>ConvectionUI when installed and read (Load), else null.</summary>
        public static XuiFace Face;
        private static XttFace Base { get { return Face == null ? null : Face.Base; } }
        private static XttFace Patch { get { return Face == null ? null : Face.Patch; } }
        public static string Status = "not loaded";
        private static bool tried;

        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_FONTS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "fonts");
            }
        }

        /// <summary>True when the face is installed and reads; Status says what was used and why.</summary>
        public static bool Load()
        {
            if (tried) return Face != null;
            tried = true;
            string dir = Folder;
            if (string.Equals(dir, "off", StringComparison.OrdinalIgnoreCase)) { Status = "Segoe UI (AVATAR_STUDIO_FONTS=off)"; return false; }
            string basePath = Path.Combine(dir, "xenonjklatin.xtt"), patchPath = Path.Combine(dir, "xenonjklatin.xttp");
            if (!File.Exists(basePath)) { Status = "Segoe UI (the console face is not installed)"; return false; }
            try
            {
                XttFace b = new XttFace(basePath), patch = null;
                if (File.Exists(patchPath))
                {
                    XttFace p = new XttFace(patchPath);
                    bool belongs = true;
                    for (int i = 0; i < 256; i++) if (p.Reference[i] != b.Signature[i]) { belongs = false; break; }
                    if (belongs) patch = p;      // sub_921B2EC0 drops a patch made for another base
                }
                Face = new XuiFace(b, patch);
                Status = "ConvectionUI: xenonjklatin.xtt (" + Base.Glyphs + " glyphs)" +
                         (Patch != null ? " + its patch (" + Patch.Glyphs + " glyphs)" : ", no patch") +
                         ", line height " + LineUnits() + "/" + Base.UnitsPerEm + " em, ascender " + AscenderUnits() + "/" + Base.UnitsPerEm;
                return true;
            }
            catch (Exception e)
            {
                Face = null;
                Status = "Segoe UI (the console face did not read: " + e.Message + ")";
                return false;
            }
        }

        private static int LineUnits() { return Patch == null ? Base.LineUnits : Math.Max(Base.LineUnits, Patch.LineUnits); }

        private static int AscenderUnits() { return Patch == null ? Base.Ascender : Math.Max(Base.Ascender, Patch.Ascender); }

        /// <summary>TTProvider_CreateFace's pixel size: 96 x pt / 72, in float steps.</summary>
        public static float PixelSize(float pointSize) { return (float)((float)(96f * pointSize) * 0.013888889f); }

        /// <summary>The line height at `px` (sub_921B7220), the larger face's.</summary>
        public static float LineHeight(float px, int flags) { return FontMetric(Face, px, flags, true); }

        public static float LineHeight(XuiFace face, float px, int flags) { return FontMetric(face, px, flags, true); }

        /// <summary>The ascender, as LineHeight with hhea's ascender (sub_921B7380).</summary>
        public static float Ascender(XuiFace face, float px, int flags) { return FontMetric(face, px, flags, false); }

        private static float FontMetric(XuiFace face, float px, int flags, bool line)
        {
            XttFace b = face.Base, patch = face.Patch;
            float best = FaceMetric(b, px, flags, line ? b.LineUnits : b.Ascender);
            if (patch != null)
            {
                float p = FaceMetric(patch, px, flags, line ? patch.LineUnits : patch.Ascender);
                if (best <= p) best = p;
            }
            return best * 0.125f;
        }

        private static float FaceMetric(XttFace f, float px, int flags, int units)
        {
            Scaler sc = new Scaler(f, px, flags);
            int u = (short)(units + sc.BoldUnits);
            return (float)(sc.Size8 * (float)u) / (float)f.UnitsPerEm;
        }

        /// <summary>The face and glyph a character is drawn with: the patch's when it maps it.</summary>
        public static bool Find(XuiFace pair, int ch, out XttFace face, out int glyph)
        {
            face = null; glyph = 0;
            if (pair.Patch != null && (glyph = pair.Patch.GlyphIndex(ch)) != 0) { face = pair.Patch; return true; }
            if ((glyph = pair.Base.GlyphIndex(ch)) != 0) { face = pair.Base; return true; }
            return false;
        }

        /// <summary>One glyph's cell: alpha, its place from the pen and the line's top, the whole advance.</summary>
        public struct Cell { public int W, H, OffsetX, OffsetY, Advance; public byte[] Alpha; }

        /// <summary>A face's scaler at one size and style (sub_921B6A78, sub_921B6BF0), built at 8 x the size.</summary>
        private sealed class Scaler
        {
            public readonly XttFace Face;
            public readonly int Flags, Scale, BoldUnits, BoldRound;
            public readonly float Size8, Strength;
            public const float Shear = 3f;

            public Scaler(XttFace face, float px, int flags)
            {
                Face = face; Flags = flags;
                Size8 = px * 8f;                                                // sub_921B2EC0
                int fixedSize = (int)Math.Floor(Size8 * 16.0);
                Scale = unchecked(0x10000 * fixedSize) / (16 * face.UnitsPerEm);
                Strength = face.UnitsPerEm * 0.0122f;
                float twice = (float)(Strength * 2f);
                BoldUnits = (flags & 2) != 0 ? (int)Math.Ceiling(twice) : 0;            // the metrics' ceilf(+20 x 2)
                BoldRound = (flags & 2) != 0 ? (int)(float)(twice + 0.5f) : 0;          // sub_921B6BF0's (int)(+24 x 2 + 0.5)
            }
        }

        /// <summary>The character's cell as XUI_Font_GetGlyph makes it; false when neither face maps it.</summary>
        public static bool Render(int ch, float px, int flags, out Cell cell) { return Render(Face, ch, px, flags, out cell); }

        public static bool Render(XuiFace pair, int ch, float px, int flags, out Cell cell)
        {
            cell = new Cell { Alpha = new byte[0] };
            XttFace face; int g;
            if (!Find(pair, ch, out face, out g)) return false;
            Scaler sc = new Scaler(face, px, flags);

            // the advance: hmtx's (+50 emboldened), scaled and rounded as sub_921B8310 and sub_921B2550 do
            int advUnits = face.Advance(g) + sc.BoldUnits;
            int adv16 = (short)((sc.Scale * advUnits + 2048) >> 12);
            float advance = ((adv16 + 15) >> 4) * 0.125f;
            cell.Advance = (int)Math.Ceiling(advance);

            int at; byte[] d = face.GlyphData(g, out at);
            if (d == null || XttFace.S16(d, at) == 0) return true;             // sub_921B77B8's zero box: no ink
            int hx0 = XttFace.S16(d, at + 2), hy0 = XttFace.S16(d, at + 4), hx1 = XttFace.S16(d, at + 6), hy1 = XttFace.S16(d, at + 8);
            int shift = (face.HeadFlags & 2) == 0 ? face.LeftSideBearing(g) - hx0 : 0;
            // the header's box, sheared and emboldened (sub_921B77B8, sub_921B6E28)
            int bx0 = hx0, bx1 = hx1, by1 = hy1;
            if ((flags & 1) != 0)
            {
                float inv = (float)(1f / Scaler.Shear);
                bx0 = (int)(float)((float)(hy0 * inv) + hx0);
                bx1 = (int)(float)((float)(hy1 * inv) + hx1);
            }
            bx1 += sc.BoldUnits; by1 += sc.BoldUnits;

            // the metric box in eighths (sub_921B77B8)
            float upem = face.UnitsPerEm;
            int m0 = (short)(int)Math.Floor((float)((float)((bx0 + shift) * sc.Size8) / upem));
            int m1 = (short)(int)Math.Floor((float)((float)(hy0 * sc.Size8) / upem));
            int m2 = (short)(int)Math.Ceiling((float)((float)((float)((bx1 + shift) * sc.Size8) / upem) + 1f));
            int m3 = (short)(int)Math.Ceiling((float)((float)((float)(by1 * sc.Size8) / upem) + 1f));
            float xMin = m0 * 0.125f, yMin = m1 * 0.125f, xMax = m2 * 0.125f, yMax = m3 * 0.125f;
            float w = xMax - xMin;
            if (w == 0f) return true;                                           // +86: uploaded as nothing

            // the cell (XUI_Font_GetGlyph): from the ascender, its quad at floor(xMin) and top - 1
            float asc = Ascender(pair, px, flags);
            float top = (float)Math.Floor((float)(asc - yMax)), bottom = (float)Math.Ceiling((float)(asc - yMin));
            cell.W = (int)((float)Math.Ceiling(w) + 1f);
            cell.H = (int)((float)Math.Ceiling((float)(bottom - top)) + 2f);
            cell.OffsetX = (int)Math.Floor(xMin);
            cell.OffsetY = (int)top - 1;

            // the render box in whole 8 x pixels (sub_921B6E28)
            int v30 = (short)(bx0 + shift), v29 = (short)(-by1);
            int x0 = (short)((v30 * sc.Scale) >> 16);
            int y0 = (short)((v29 * sc.Scale) >> 16);
            int w8 = (short)(CeilShift(((short)(bx1 - bx0) + v30) * sc.Scale) - x0);
            int h8 = (short)(CeilShift(((short)(by1 - hy0) + v29) * sc.Scale) - y0);
            // the baseline's row in the 8 x bitmap (sub_921B6BF0's +66)
            int asc66 = (short)(((face.Ascender + sc.BoldRound) * sc.Scale + 2048) >> 12);
            int baseRow = (asc66 + 8) >> 4;

            GlyphPath path = new GlyphPath();
            if (!Outline(sc, g, path, 0)) return true;
            List<Seg> segs = Walk(path, sc.Scale);
            cell.Alpha = Rasterise(segs, x0, w8, y0, y0 + h8, baseRow, (int)top, cell.W, cell.H);
            return true;
        }

        /// <summary>((uint)(v + 0xFFFF)) >> 16, as sub_921B6E28 rounds its high edges up.</summary>
        private static int CeilShift(int v) { return (int)((uint)(v + 0xFFFF) >> 16); }

        // ---- the outline (sub_921C1210, sub_921C0158, sub_921C01B8, sub_921BFF30) ---------------

        /// <summary>The scaler's path: points in font units x 16, their types, and open contours.</summary>
        private sealed class GlyphPath
        {
            public readonly List<int> X = new List<int>(), Y = new List<int>();
            public readonly List<byte> T = new List<byte>();
            public bool Open;

            /// <summary>sub_921C19E0: points kept x 16; a repeated first point of an open contour dropped.</summary>
            public void Add(int[] xy, byte type)
            {
                int n = xy.Length / 2, from = 0;
                bool first = true;
                if (Open && X.Count > 0 && xy[0] * 16 == X[X.Count - 1] && xy[1] * 16 == Y[Y.Count - 1]) { from = 1; first = false; }
                byte t0 = Open ? (first ? (byte)1 : type) : (byte)0;
                for (int i = from; i < n; i++)
                {
                    X.Add(16 * xy[2 * i]); Y.Add(16 * xy[2 * i + 1]);
                    T.Add(i == from ? t0 : type);
                }
                Open = true;
            }
        }

        /// <summary>Glyph g's outline (sub_921C1210): simple or composite, sheared, emboldened, made a path.</summary>
        private static bool Outline(Scaler sc, int g, GlyphPath into, int depth)
        {
            XttFace f = sc.Face;
            int at;
            byte[] d = f.GlyphData(g, out at);
            if (d == null || depth > 8) return false;
            int n = XttFace.S16(d, at);
            if (n == -1)
            {
                int q = at + 10;
                while (true)
                {
                    int cf = XttFace.U16(d, q), ci = XttFace.U16(d, q + 2);
                    q += 4;
                    int a1, a2;
                    if ((cf & 1) != 0)
                    {
                        a1 = XttFace.U16(d, q); a2 = XttFace.U16(d, q + 2); q += 4;
                        if ((cf & 2) != 0) { a1 = (short)a1; a2 = (short)a2; }
                    }
                    else
                    {
                        a1 = d[q]; a2 = d[q + 1]; q += 2;
                        if ((cf & 2) != 0) { a1 = (sbyte)a1; a2 = (sbyte)a2; }
                    }
                    bool scaled = false, twoByTwo = false;
                    int xx = 1, yy = 1, yx = 1, xy = 1;
                    if ((cf & 0xC8) != 0) { scaled = true; xx = yy = XttFace.S16(d, q); q += 2; }
                    if ((cf & 0xC0) != 0) { yy = XttFace.S16(d, q); q += 2; }
                    if ((cf & 0x80) != 0) { twoByTwo = true; xy = yy; yx = XttFace.S16(d, q); yy = XttFace.S16(d, q + 2); q += 4; }
                    GlyphPath part = new GlyphPath();
                    if (!Outline(sc, ci, part, depth + 1)) return false;
                    for (int k = 0; k < part.X.Count; k++)
                    {
                        int x = part.X[k], y = part.Y[k];
                        if (twoByTwo) { int nx = (x * xx + y * yx) >> 14; y = (x * xy + y * yy) >> 14; x = nx; }
                        else if (scaled) { x = (x * xx) >> 14; y = (y * yy) >> 14; }
                        part.X[k] = x + 16 * a1;
                        part.Y[k] = y - 16 * a2;
                    }
                    int start = into.X.Count;
                    into.X.AddRange(part.X); into.Y.AddRange(part.Y); into.T.AddRange(part.T);
                    if (part.X.Count > 0) into.T[start] = (byte)(into.T[start] & 0xF8);
                    if ((cf & 0x20) == 0) break;
                }
                return true;
            }
            if (n <= 0) return true;

            int[] ends = new int[n];
            for (int i = 0; i < n; i++) ends[i] = XttFace.U16(d, at + 10 + 2 * i);
            int pts = ends[n - 1] + 1;
            int p = at + 10 + 2 * n;
            p += 2 + XttFace.U16(d, p);                                         // the instructions, skipped
            byte[] fl = new byte[pts];
            for (int i = 0; i < pts; )
            {
                byte b = d[p++];
                fl[i++] = b;
                if ((b & 8) != 0) { int r = d[p++]; while (r-- > 0 && i < pts) fl[i++] = b; }
            }
            int shift = (f.HeadFlags & 2) == 0 ? f.LeftSideBearing(g) - XttFace.S16(d, at + 2) : 0;
            int[] X = new int[pts], Y = new int[pts];
            int v = 0;
            for (int i = 0; i < pts; i++)
            {
                if ((fl[i] & 2) != 0) { int dv = d[p++]; v += (fl[i] & 16) != 0 ? dv : -dv; }
                else if ((fl[i] & 16) == 0) { v += XttFace.S16(d, p); p += 2; }
                X[i] = (short)(v + shift);
            }
            v = 0;
            for (int i = 0; i < pts; i++)
            {
                if ((fl[i] & 4) != 0) { int dv = d[p++]; v += (fl[i] & 32) != 0 ? dv : -dv; }
                else if ((fl[i] & 32) == 0) { v += XttFace.S16(d, p); p += 2; }
                Y[i] = (short)(-(short)v);
            }
            if ((sc.Flags & 1) != 0)                                            // sub_921C0158
                for (int i = 0; i < pts; i++)
                    X[i] = (short)(X[i] - (int)(float)((float)(Y[i] / Scaler.Shear) + 0.5f));
            if ((sc.Flags & 2) != 0) Embolden(X, Y, ends, sc.Strength, sc.Strength);
            BuildPath(X, Y, fl, ends, into);
            return true;
        }

        /// <summary>sub_921C01B8: the emboldening, each point pushed out along its edges' normals.</summary>
        private static void Embolden(int[] X, int[] Y, int[] ends, float sx, float sy)
        {
            const float Eps = 1.1920929E-07f;                                   // 13 << 26, vcmpbfp's bound
            int start = 0;
            foreach (int e in ends)
            {
                int end = e + 1;
                if (end <= start) { start = end; continue; }
                int fx = X[start], fy = Y[start], px = X[end - 1], py = Y[end - 1];
                for (int i = start; i < end; i++)
                {
                    int cx = X[i], cy = Y[i];
                    int nx = i + 1 == end ? fx : X[i + 1], ny = i + 1 == end ? fy : Y[i + 1];
                    float ax, ay, bx, by;
                    Unit(cx - nx, cy - ny, out ax, out ay);
                    Unit(px - cx, py - cy, out bx, out by);
                    float oBx = sx * -by, oAx = sx * -ay, oBy = sy * bx, oAy = sy * ax;
                    bool xSame = (int)(float)(oBx * 100f) == (int)(float)(oAx * 100f);
                    bool ySame = (int)(float)(oBy * 100f) == (int)(float)(oAy * 100f);
                    float dx, dy;
                    if (xSame && !ySame) { dx = oBx; dy = oBy; }
                    else
                    {
                        float p1x = cx + oAx, p1y = cy + oAy, p3x = cx + oBx, p3y = cy + oBy;
                        float p2x = px + oBx, p2y = py + oBy, p0x = nx + oAx, p0y = ny + oAy;
                        float dBx = p3x - p2x, dBy = p3y - p2y, dAx = p0x - p1x, dAy = p0y - p1y;
                        float wx = p2x - p1x, wy = p2y - p1y;
                        float den = (float)(dBx * dAy) - (float)(dBy * dAx);
                        float num = (float)(dAx * wy) - (float)(dAy * wx);
                        float ix, iy;
                        if (Math.Abs(den) <= Eps) { ix = Math.Abs(num) > Eps ? float.PositiveInfinity : float.NaN; iy = ix; }
                        else
                        {
                            float r = 1f / den;
                            r = Fma(r, Fnms(den, r, 1f), r);
                            r = Fma(r, Fnms(den, r, 1f), r);
                            float t = num * r;
                            ix = Fma(dBx, t, p2x); iy = Fma(dBy, t, p2y);
                        }
                        if (float.IsNaN(ix) || float.IsInfinity(ix))
                        {
                            ix = (float)(p1x + p3x) * 0.5f; iy = (float)(p1y + p3y) * 0.5f;
                        }
                        dx = ix - cx; dy = iy - cy;
                    }
                    px = cx; py = cy;                                           // the next point's previous, as it was
                    X[i] = (short)(cx + (int)(float)((float)(dx + sx) + 0.5f));
                    Y[i] = (short)(cy + (int)(float)((float)(dy - sy) + 0.5f));
                }
                start = end;
            }
        }

        /// <summary>A unit vector, its reciprocal square root refined once (vmsum4fp128, vnmsubfp); zero stays zero.</summary>
        private static void Unit(int vx, int vy, out float ux, out float uy)
        {
            float x = vx, y = vy;
            float s = (float)(x * x) + (float)(y * y);
            if (s == 0f) { ux = 0f; uy = 0f; return; }
            float r = (float)(1.0 / Math.Sqrt(s));
            r = Fma(r, Fnms((float)(s * 0.5f), (float)(r * r), 0.5f), r);
            ux = x * r; uy = y * r;
        }

        /// <summary>vmaddfp: a x c + b, one rounding.</summary>
        private static float Fma(float a, float c, float b) { return (float)((double)a * c + b); }

        /// <summary>vnmsubfp: -(a x c - b), one rounding.</summary>
        private static float Fnms(float a, float c, float b) { return (float)(-((double)a * c - b)); }

        /// <summary>sub_921BFF30: contours to lines and quadratics, with implied midpoints between controls.</summary>
        private static void BuildPath(int[] X, int[] Y, byte[] fl, int[] ends, GlyphPath p)
        {
            int start = 0;
            foreach (int end in ends)
            {
                if (end < start) continue;
                p.Open = false;                                                 // sub_921C17C8
                int idx = start + 1;
                int cx = X[start], cy = Y[start];
                bool con = (fl[start] & 1) != 0;
                while (idx <= end + 1)
                {
                    int k = idx++;
                    if (k > end) k = start;
                    int nx = X[k], ny = Y[k];
                    if (con && (fl[k] & 1) != 0)
                    {
                        p.Add(new[] { cx, cy, nx, ny }, 1);
                        cx = nx; cy = ny; con = true;
                        continue;
                    }
                    int k2 = idx > end ? start : idx;
                    int ex = X[k2], ey = Y[k2];
                    bool eon = (fl[k2] & 1) != 0;
                    if (!eon) { ex = (nx + ex + 1) >> 1; ey = (ny + ey + 1) >> 1; }
                    p.Add(new[] { cx, cy, 2 * (nx - cx) / 3 + cx, 2 * (ny - cy) / 3 + cy,
                                  (ex - nx) / 3 + nx, (ey - ny) / 3 + ny, ex, ey }, 3);
                    cx = ex; cy = ey; con = eon;
                }
                start = end + 1;
            }
        }

        // ---- the scan converter (sub_921C2F10 and the edge, span and bitmap code it calls) -------

        /// <summary>One edge between two whole 8 x pixel vertices, top to bottom; +1 when the path went down.</summary>
        private struct Seg { public int X0, Y0, X1, Y1, Dir, Slope; }

        /// <summary>The path walked as sub_921C1E48 walks it: scaled, cubics flattened, contours closed.</summary>
        private static List<Seg> Walk(GlyphPath p, int scale)
        {
            List<Seg> segs = new List<Seg>();
            int n = p.X.Count, i = 0;
            while (i < n)
            {
                int sx = Tx(p.X[i], scale), sy = Tx(p.Y[i], scale), cx = sx, cy = sy;
                i++;
                while (i < n && (p.T[i] & 7) != 0)
                {
                    if ((p.T[i] & 7) == 1)
                    {
                        int x = Tx(p.X[i], scale), y = Tx(p.Y[i], scale);
                        Edge(segs, cx, cy, x, y);
                        cx = x; cy = y; i++;
                    }
                    else
                    {
                        if (i + 2 >= n) { i = n; break; }
                        int[] c = { cx, cy, Tx(p.X[i], scale), Tx(p.Y[i], scale), Tx(p.X[i + 1], scale), Tx(p.Y[i + 1], scale),
                                    Tx(p.X[i + 2], scale), Tx(p.Y[i + 2], scale) };
                        foreach (int[] pt in Flatten(c)) { Edge(segs, cx, cy, pt[0], pt[1]); cx = pt[0]; cy = pt[1]; }
                        i += 3;
                    }
                }
                Edge(segs, cx, cy, sx, sy);
            }
            return segs;
        }

        private static int Tx(int v, int scale) { return unchecked(v * scale) >> 16; }

        private static void Edge(List<Seg> segs, int ax, int ay, int bx, int by)
        {
            int x0 = Snap(ax), y0 = Snap(ay), x1 = Snap(bx), y1 = Snap(by);
            if (y0 == y1) return;
            Seg s;
            if (y0 < y1) { s.X0 = x0; s.Y0 = y0; s.X1 = x1; s.Y1 = y1; s.Dir = 1; }
            else { s.X0 = x1; s.Y0 = y1; s.X1 = x0; s.Y1 = y0; s.Dir = -1; }
            // sub_921C2830: x from (x0 << 16) + 0x8000, stepping by the slope each row
            s.Slope = s.X1 == s.X0 ? 0 : unchecked((s.X1 - s.X0) << 16) / (s.Y1 - s.Y0);
            segs.Add(s);
        }

        private static int Snap(int v)
        {
            v = (v + 8) >> 4;
            return v < -49152 ? -49152 : v > 0x4000 ? 0x4000 : v;
        }

        /// <summary>sub_921C1CB0: halve until the curve is within 8 of its chord and spans under 64.</summary>
        private static List<int[]> Flatten(int[] c)
        {
            List<int[]> o = new List<int[]>();
            Stack<int[]> stack = new Stack<int[]>();
            int[] cur = c;
            while (true)
            {
                int[] a, b;
                while (stack.Count < 4096 && Split(cur, out a, out b)) { stack.Push(b); cur = a; }
                o.Add(new[] { cur[6], cur[7] });
                if (stack.Count == 0) break;
                cur = stack.Pop();
            }
            return o;
        }

        private static bool Split(int[] p, out int[] a, out int[] b)
        {
            a = new int[8]; b = new int[8];
            for (int k = 0; k < 2; k++)
            {
                int p0 = p[k], p1 = p[2 + k], p2 = p[4 + k], p3 = p[6 + k];
                int q0 = (p0 + p1) >> 1, q1 = (p1 + p2) >> 1, q2 = (p2 + p3) >> 1;
                int r0 = (q0 + q1) >> 1, r1 = (q2 + q1) >> 1, m = (r0 + r1) >> 1;
                a[k] = p0; a[2 + k] = q0; a[4 + k] = r0; a[6 + k] = m;
                b[k] = m; b[2 + k] = r1; b[4 + k] = q2; b[6 + k] = p3;
            }
            return Math.Abs(a[0] - a[6]) >= 64 || Math.Abs(a[1] - a[7]) >= 64
                || Math.Abs(((p[6] + p[0]) >> 1) - a[6]) >= 8 || Math.Abs(((p[7] + p[1]) >> 1) - a[7]) >= 8;
        }

        /// <summary>The spans (the nonzero rule, sub_921C2C30) and the cell they make (sub_921B2338).</summary>
        private static byte[] Rasterise(List<Seg> segs, int left, int width, int rowTop, int rowEnd, int baseRow, int top, int w, int h)
        {
            int phase = Math.Abs((short)left) % 8;
            int bytes = (24 + phase + Math.Max(0, width) + 7) / 8 + 1;
            int rows = Math.Max(0, rowEnd - rowTop);
            int[] count = new int[Math.Max(1, rows * bytes)];
            List<long> xs = new List<long>();
            for (int y = rowTop; y < rowEnd; y++)
            {
                xs.Clear();
                foreach (Seg s in segs)
                {
                    if (y < s.Y0 || y >= s.Y1) continue;
                    int fx = unchecked((s.X0 << 16) + 0x8000 + (y - s.Y0) * s.Slope);
                    xs.Add(((long)fx << 2) | (uint)(s.Dir + 1));             // sort by x, keep the direction
                }
                xs.Sort();
                int pos = 0, wind = 0, row = (y - rowTop) * bytes;
                foreach (long e in xs)
                {
                    int fx = (int)(e >> 2), dir = (int)(e & 3) - 1;
                    int at = (short)(fx >> 16) - left;
                    if (at > pos)
                    {
                        int to = Math.Min(at, width);
                        if ((wind & 0x7FFF) != 0)
                            for (int c = pos; c < to; c++) count[row + ((24 + phase + c) >> 3)]++;
                        pos = to;
                        if (pos >= width) break;
                    }
                    wind = (wind + dir) & 0xFFFF;
                }
            }
            byte[] alpha = new byte[w * h];
            for (int k = 0; k < h; k++)
            {
                int slotRow = 1 + top + k;
                if (slotRow < 2) continue;
                int firstBitmapRow = 8 * (slotRow - 2);
                for (int j = 0; j < w; j++)
                {
                    int sum = 0, col = 3 + j;
                    if (col >= bytes) continue;
                    for (int r = 0; r < 8; r++)
                    {
                        int y = firstBitmapRow + r - baseRow;
                        if (y < rowTop || y >= rowEnd) continue;
                        sum += count[(y - rowTop) * bytes + col];
                    }
                    alpha[k * w + j] = (byte)Math.Min(255, 4 * sum);
                }
            }
            return alpha;
        }
    }
}
