// The console's lighting: the XDK avatar library's shaders, found in the player's AvatarEditor.xex,
// recompiled to HLSL by XenosRecomp (XenosRecompiler.cs), rewritten for FNA and compiled to AvatarLibrary.fxb.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AvatarStudio
{
    static class SetupShaders
    {
        // (technique, vertex shader, pixel shader, alpha test)
        private sealed class Tq
        {
            public string Name, Vs, Ps;
            public bool Alpha;
            public Tq(string name, string vs, string ps, bool alpha) { Name = name; Vs = vs; Ps = ps; Alpha = alpha; }
        }

        private static readonly Tq[] Techniques =
        {
            new Tq("Kind0", "body_opaque_vs", "body_opaque_ps", true),
            new Tq("Kind1", "body_transparent_vs", "body_transparent_ps", false),
            new Tq("Kind2", "body_shiny_vs", "body_shiny_opaque_ps", true),
            new Tq("Kind3", "body_shiny_vs", "body_shiny_transparent_ps", false),
            new Tq("Kind4", "head_opaque_vs", "head_opaque_ps", true),
            new Tq("Shadow0", "body_shadow_vs", null, false),
            new Tq("Shadow4", "head_shadow_vs", null, false),
        };
        private const int PaletteReg = 12;   // [ours] c12..c227
        private const int PaletteSize = 72 * 3;
        private const string AlphaRef = "0.501960814";   // 128 / 255 as a float32

        /// <summary>XenosRecomp's shader_common.h, embedded in the exe: every converted shader carries it.</summary>
        private static string Include()
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("shader_common.h"))
            {
                if (s == null) throw new InvalidDataException("shader_common.h is not in the exe");
                using (MemoryStream m = new MemoryStream())
                {
                    s.CopyTo(m);
                    return Encoding.UTF8.GetString(m.ToArray());
                }
            }
        }

        /// <summary>AvatarLibrary.fx and .fxb from the update's AvatarEditor.xex.</summary>
        public static void Install(string update, Action<string> say)
        {
            string xexPath = Path.Combine(update, "AvatarEditor.xex");
            uint imgBase;
            byte[] img = SetupInstall.XexImage(File.ReadAllBytes(xexPath), out imgBase);
            List<string> notes = new List<string>();
            string fx = BuildFx(img, Path.GetFileName(xexPath), Include(), notes);
            byte[] fxb = Compile(fx);
            string outDir = LibraryShaders.Folder;
            SetupInstall.WriteFile(Path.Combine(outDir, "AvatarLibrary.fx"), Encoding.UTF8.GetBytes(fx));
            SetupInstall.WriteFile(Path.Combine(outDir, "AvatarLibrary.fxb"), fxb);
            foreach (string nt in notes) say("NOTE " + nt);
            say("installed the console lighting (AvatarLibrary.fx, " + fxb.Length + " bytes of effect) -> " + outDir);
        }

        // ---- containers ------------------------------------------------------------------------------

        private sealed class Const
        {
            public string Name, Set;
            public int Index, Count;
        }

        private sealed class Container
        {
            public byte[] Blob;
            public string Kind, Target;
            public List<Const> Constants;
            public int UcodeOffset, UcodeSize;
        }

        private static readonly string[] RegSet = { "b", "i", "c", "s" };

        private static List<int> FindContainers(byte[] d)
        {
            List<int> o = new List<int>();
            for (int i = 0; i < d.Length - 4; i += 4)
                if ((U32(d, i) & 0xFFFFFF00u) == 0x102A1100u) o.Add(i);
            if (o.Count == 0)
                for (int i = 0; i < d.Length - 4; i++)
                    if ((U32(d, i) & 0xFFFFFF00u) == 0x102A1100u) o.Add(i);
            return o;
        }

        /// <summary>The D3DX constant table at `b`, or 4 bytes on (the Xbox's extra dword); null if neither.</summary>
        private static List<Const> ParseConstantTable(byte[] d, int b, out string target)
        {
            target = "";
            if (U32(d, b) == 28) { }
            else if (U32(d, b + 4) == 28) b += 4;
            else return null;
            uint count = U32(d, b + 12), infoOff = U32(d, b + 16), tgt = U32(d, b + 24);
            if (count > 512) return null;
            List<Const> list = new List<Const>();
            for (int k = 0; k < count; k++)
            {
                int o = b + (int)infoOff + k * 20;
                int set = U16(d, o + 4);
                list.Add(new Const
                {
                    Name = CStr(d, b + (int)U32(d, o)),
                    Set = set < RegSet.Length ? RegSet[set] : "?" + set,
                    Index = U16(d, o + 6),
                    Count = U16(d, o + 8),
                });
            }
            if (tgt != 0) target = CStr(d, b + (int)tgt);
            return list;
        }

        /// <summary>name -> container for every container naming a shader_*.updb.</summary>
        private static Dictionary<string, Container> FindLibraryContainers(byte[] img)
        {
            Dictionary<string, Container> found = new Dictionary<string, Container>(StringComparer.Ordinal);
            foreach (int b in FindContainers(img))
            {
                uint flags = U32(img, b), vsz = U32(img, b + 4), psz = U32(img, b + 8);
                uint ctOff = U32(img, b + 16), shOff = U32(img, b + 24);
                long end = Math.Min((long)b + vsz + psz, img.Length);
                byte[] blob = new byte[end - b];
                Buffer.BlockCopy(img, b, blob, 0, blob.Length);
                string name = UpdbName(blob);
                if (name == null) continue;
                string kind = (flags & 0xFF) == 0 ? "ps" : "vs";
                if (!name.EndsWith("_" + kind, StringComparison.Ordinal))
                    throw new InvalidDataException(string.Format("container at 0x{0:X} names {1} but is a {2}", b, name, kind));
                uint physOff = U32(img, b + (int)shOff), size = U32(img, b + (int)shOff + 4);
                string target = "";
                List<Const> ct = ctOff != 0 ? ParseConstantTable(img, b + (int)ctOff, out target) : null;
                Container prev;
                if (found.TryGetValue(name, out prev))
                {
                    if (!SameBytes(prev.Blob, blob)) throw new InvalidDataException("two different containers name shader_" + name);
                    continue;
                }
                found[name] = new Container
                {
                    Blob = blob, Kind = kind, Target = ct != null ? target : "",
                    Constants = ct ?? new List<Const>(), UcodeOffset = (int)(vsz + physOff), UcodeSize = (int)size,
                };
            }
            return found;
        }

        /// <summary>The shader's name, from the container's \shader_<name>.updb debug path.</summary>
        private static string UpdbName(byte[] blob)
        {
            byte[] pre = Encoding.ASCII.GetBytes("\\shader_");
            for (int i = 0; i + pre.Length < blob.Length; i++)
            {
                int k = 0;
                while (k < pre.Length && blob[i + k] == pre[k]) k++;
                if (k < pre.Length) continue;
                int s = i + pre.Length, e = s;
                while (e < blob.Length && IsWordByte(blob[e])) e++;
                if (e > s && e + 5 <= blob.Length && blob[e] == '.' && blob[e + 1] == 'u' && blob[e + 2] == 'p' && blob[e + 3] == 'd' && blob[e + 4] == 'b')
                    return Encoding.ASCII.GetString(blob, s, e - s);
            }
            return null;
        }

        private static bool IsWordByte(byte c)
        {
            return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '_';
        }

        // ---- XenosRecomp and the microcode ----------------------------------------------------------

        private static readonly Regex SigRe = new Regex(@"void main\((.*?)\)\s*\{", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly HashSet<int> ExecOps = new HashSet<int> { 1, 2, 3, 4, 5, 6, 13, 14 };

        /// <summary>A container's HLSL, newlines made \n.</summary>
        private static string Recomp(byte[] container, string include)
        {
            return XenosRecompiler.Recompile(container, include).Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private static List<string> BodyLines(string text)
        {
            Match m = SigRe.Match(text);
            return new List<string>(text.Substring(m.Index + m.Length).Split('\n'));
        }

        private static ulong Bits(ulong v, int lo, int n) { return (v >> lo) & ((1UL << n) - 1); }

        private static uint[] Dwords(byte[] b, int off, int len)
        {
            uint[] d = new uint[len / 4];
            for (int i = 0; i < d.Length; i++) d[i] = U32(b, off + 4 * i);
            return d;
        }

        /// <summary>48-bit control-flow instructions until the instruction stream begins (two per three dwords).</summary>
        private static List<ulong> CfPairs(uint[] dw)
        {
            List<ulong> o = new List<ulong>();
            int instrStart = -1, g = 0;
            while ((g + 1) * 3 <= dw.Length)
            {
                int b = g * 3;
                if (instrStart >= 0 && b >= instrStart) break;
                ulong d0 = dw[b], d1 = dw[b + 1], d2 = dw[b + 2];
                ulong[] cfs = { d0 | ((d1 & 0xFFFF) << 32), ((d1 >> 16) | ((d2 & 0xFFFF) << 16)) | ((d2 >> 16) << 32) };
                foreach (ulong cf in cfs)
                {
                    o.Add(cf);
                    if (ExecOps.Contains((int)Bits(cf, 44, 4)))
                    {
                        int addr = (int)Bits(cf, 0, 12) * 3;
                        instrStart = instrStart < 0 ? addr : Math.Min(instrStart, addr);
                    }
                }
                g++;
            }
            return o;
        }

        /// <summary>The container with cf instruction `cf`'s count set to 0.</summary>
        private static byte[] WithCountZero(byte[] container, int ucodeOffset, int cf)
        {
            byte[] b = (byte[])container.Clone();
            int g = cf / 2;
            bool odd = cf % 2 == 1;
            int o = ucodeOffset + 12 * g + (odd ? 4 : 0);
            uint w = U32(b, o) & ~(odd ? (7u << 28) : (7u << 12));
            b[o] = (byte)(w >> 24); b[o + 1] = (byte)(w >> 16); b[o + 2] = (byte)(w >> 8); b[o + 3] = (byte)w;
            return b;
        }

        /// <summary>Vertex fetches in program order: (destination register, index register, index component).</summary>
        private static List<object[]> VFetches(uint[] dw)
        {
            List<object[]> o = new List<object[]>();
            foreach (ulong cf in CfPairs(dw))
            {
                if (!ExecOps.Contains((int)Bits(cf, 44, 4))) continue;
                int addr = (int)Bits(cf, 0, 12), count = (int)Bits(cf, 12, 3), seq = (int)Bits(cf, 16, 12);
                for (int k = 0; k < count; k++)
                {
                    if (((seq >> (2 * k)) & 1) == 0) continue;
                    ulong d0 = dw[(addr + k) * 3];
                    if (Bits(d0, 0, 5) != 0) continue;                       // a texture fetch
                    o.Add(new object[] { (int)Bits(d0, 12, 6), (int)Bits(d0, 5, 6), "xyzw"[(int)Bits(d0, 30, 2)] });
                }
            }
            return o;
        }

        // ---- the parsed XenosRecomp output -----------------------------------------------------------

        private sealed class Hlsl
        {
            public string Kind, PathName;
            public List<string[]> Inputs = new List<string[]>();     // (type, name, semantic)
            public string RawBody;
            public HashSet<string> CbufArrays = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, int> Samplers = new Dictionary<string, int>(StringComparer.Ordinal);
        }

        private static readonly Regex VkLocation = new Regex(@"\[\[vk::location\(\d+\)\]\]\s*", RegexOptions.CultureInvariant);
        private static readonly Regex ParamRe = new Regex(@"^(in|out)\s+(\w+)\s+(\w+)\s*:\s*(\w+)", RegexOptions.CultureInvariant);
        private static readonly Regex CbufRe = new Regex(@"^\s*float4 (\w+)\[\d+\] : packoffset", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static Hlsl ParseHlsl(string text, string kind, string pathName, List<Const> constants)
        {
            Hlsl h = new Hlsl { Kind = kind, PathName = pathName };
            Match m = SigRe.Match(text);
            if (!m.Success) throw new InvalidDataException("no main() in " + pathName);
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in m.Groups[1].Value.Split('\n'))
            {
                string line = VkLocation.Replace(raw.Trim().TrimEnd(','), "");
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                Match mm = ParamRe.Match(line);
                if (!mm.Success) continue;
                string name = mm.Groups[3].Value;
                if (!seen.Add(name)) continue;
                if (mm.Groups[1].Value == "in") h.Inputs.Add(new[] { mm.Groups[2].Value, name, mm.Groups[4].Value });
            }
            string body = text.Substring(m.Index + m.Length);
            h.RawBody = body.Substring(0, body.TrimEnd().LastIndexOf('}'));
            foreach (Match c in CbufRe.Matches(text)) h.CbufArrays.Add(c.Groups[1].Value);
            foreach (Const c in constants) if (c.Set == "s") h.Samplers[c.Name] = c.Index;
            return h;
        }

        // ---- the library's rewrites ------------------------------------------------------------------

        private static readonly Regex FetchLineRe = new Regex(
            @"^(\s*)r(\d+)\.(\w+) = (.*\bi(?:Position|Normal|BlendWeight|BlendIndices|Color|TexCoord)\d+\b.*);\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex PaletteFetchRe = new Regex(@"tfetchTexcoord\(g_SwappedTexcoords, iTexCoord([678]), \1\)", RegexOptions.CultureInvariant);
        private static readonly Regex Float4Decl = new Regex(@"^\s*float4 ", RegexOptions.CultureInvariant);

        private static void RewritePalette(List<string> lines, List<object[]> fetches, string name, List<string> notes)
        {
            List<int> idx = new List<int>();
            List<Match> ms = new List<Match>();
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = FetchLineRe.Match(lines[i]);
                if (m.Success && !Float4Decl.IsMatch(lines[i])) { idx.Add(i); ms.Add(m); }
            }
            if (idx.Count != fetches.Count)
                throw new InvalidDataException(string.Format("{0}: {1} input reads in XenosRecomp's output, {2} vfetches in the microcode", name, idx.Count, fetches.Count));
            int nPal = 0;
            for (int q = 0; q < idx.Count; q++)
            {
                int i = idx[q];
                Match m = ms[q];
                int dst = (int)fetches[q][0], idxReg = (int)fetches[q][1];
                char idxComp = (char)fetches[q][2];
                if (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) != dst)
                    throw new InvalidDataException(string.Format("{0}: vfetch order mismatch at '{1}' (microcode writes r{2})", name, lines[i], dst));
                string g4 = m.Groups[4].Value;
                Match pm = PaletteFetchRe.Match(g4);
                if (pm.Success)
                {
                    if (idxReg == 0 && idxComp == 'x') throw new InvalidDataException(name + ": palette element read at the vertex index: " + lines[i]);
                    int row = int.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) - 6;
                    string rep = "Palette[r" + idxReg + "." + idxComp + " * 3 + " + row + "]";
                    lines[i] = lines[i].Substring(0, m.Groups[4].Index) + PaletteFetchRe.Replace(g4, delegate (Match x) { return rep; }) + ";";
                    nPal++;
                }
                else if (!(idxReg == 0 && idxComp == 'x'))
                    throw new InvalidDataException(name + ": indexed fetch of a per-vertex element: " + lines[i]);
            }
            notes.Add(name + ": " + nPal + " palette fetches indexed from the microcode");
        }

        private const string Paramgen = "float4((iPos.xy - 0.5) * float2(iFace ? 1.0 : -1.0, 1.0), 0.0, 0.0)";
        private static readonly Regex PosFace = new Regex(@"\biPos\b|\biFace\b", RegexOptions.CultureInvariant);

        private static bool RewriteParamgen(List<string> lines, string name, List<string> notes)
        {
            bool hit = false;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].Contains(Paramgen))
                {
                    lines[i] = lines[i].Replace(Paramgen, "float4(vPos.xy * float2(vFace >= 0.0 ? 1.0 : -1.0, 1.0), 0.0, 0.0)");
                    hit = true;
                }
            foreach (string l in lines)
                if (PosFace.IsMatch(l)) throw new InvalidDataException(name + ": iPos / iFace used outside the parameter-generation register");
            if (hit) notes.Add(name + ": the position register is VPOS / VFACE");
            return hit;
        }

        /// <summary>Each bool-conditioned exec block's statements, found by ablation, wrapped in its condition.</summary>
        private static string ConditionalBlocks(string stem, Container c, string fullText, Dictionary<int, string> boolNames,
                                                List<string> notes, string name, string include)
        {
            List<ulong> cfs = CfPairs(Dwords(c.Blob, c.UcodeOffset, c.UcodeSize));
            List<string> full = BodyLines(fullText);
            List<object[]> spans = new List<object[]>();             // (i1, i2, cond, bool name, cf index)
            Dictionary<int, int> bnum = new Dictionary<int, int>();
            for (int i = 0; i < cfs.Count; i++)
            {
                ulong cf = cfs[i];
                int op = (int)Bits(cf, 44, 4), count = (int)Bits(cf, 12, 3), b = (int)Bits(cf, 34, 8), cond = (int)Bits(cf, 42, 1);
                bnum[i] = b;
                if (!(op == 3 || op == 4 || op == 13 || op == 14) || count == 0) continue;
                if (op == 4 || op == 14) throw new InvalidDataException(stem + ": conditional exec_end block " + i + " -- not handled");
                List<string> abl = BodyLines(Recomp(WithCountZero(c.Blob, c.UcodeOffset, i), include));
                List<int[]> ops = new List<int[]>();
                foreach (int[] o in Opcodes(full, abl)) if (o[0] != OpEqual) ops.Add(o);
                if (ops.Count != 1 || ops[0][0] != OpDelete)
                    throw new InvalidDataException(stem + ": ablating cf " + i + " did not remove one contiguous run");
                int i1 = ops[0][1], i2 = ops[0][2];
                for (int k = i1; k < i2; k++)
                    if (full[k].Contains("return")) throw new InvalidDataException(stem + ": cf " + i + "'s statements contain a return");
                string bname;
                if (!boolNames.TryGetValue(b, out bname))
                    throw new InvalidDataException(stem + ": cf " + i + " tests b" + b + ", which the constant table does not name");
                spans.Add(new object[] { i1, i2, cond, bname, i });
            }
            spans.Sort(CompareSpans);
            for (int k = 0; k + 1 < spans.Count; k++)
                if ((int)spans[k][1] > (int)spans[k + 1][0]) throw new InvalidDataException(stem + ": overlapping conditional blocks");
            List<string> o2 = new List<string>();
            int pos = 0;
            foreach (object[] s in spans)
            {
                int i1 = (int)s[0], i2 = (int)s[1], cond = (int)s[2], cfi = (int)s[4];
                string bname = (string)s[3];
                for (int k = pos; k < i1; k++) o2.Add(full[k]);
                o2.Add("\tif (" + (cond != 0 ? "" : "!") + bname + ")   // cf " + cfi + ": runs while " + bname + " (b" + bnum[cfi] + ") == " + cond);
                o2.Add("\t{");
                for (int k = i1; k < i2; k++) o2.Add("\t" + full[k]);
                o2.Add("\t}");
                pos = i2;
            }
            for (int k = pos; k < full.Count; k++) o2.Add(full[k]);
            if (spans.Count > 0) notes.Add(name + ": " + spans.Count + " bool-conditioned blocks restored");
            Match m = SigRe.Match(fullText);
            return fullText.Substring(0, m.Index + m.Length) + string.Join("\n", o2.ToArray());
        }

        /// <summary>Tuple order on (i1, i2, cond, bool name, cf index).</summary>
        private static int CompareSpans(object[] a, object[] b)
        {
            for (int k = 0; k < 5; k++)
            {
                int c = k == 3 ? string.CompareOrdinal((string)a[k], (string)b[k]) : ((int)a[k]).CompareTo((int)b[k]);
                if (c != 0) return c;
            }
            return 0;
        }

        // ---- difflib.SequenceMatcher(None, a, b, autojunk=False).get_opcodes() ------------------------

        private const int OpEqual = 0, OpDelete = 1, OpInsert = 2, OpReplace = 3;

        private static List<int[]> Opcodes(List<string> a, List<string> b)
        {
            Dictionary<string, List<int>> b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < b.Count; i++)
            {
                List<int> l;
                if (!b2j.TryGetValue(b[i], out l)) b2j[b[i]] = l = new List<int>();
                l.Add(i);
            }
            List<int[]> blocks = new List<int[]>();
            List<int[]> queue = new List<int[]> { new[] { 0, a.Count, 0, b.Count } };
            while (queue.Count > 0)
            {
                int[] q = queue[queue.Count - 1];
                queue.RemoveAt(queue.Count - 1);
                int alo = q[0], ahi = q[1], blo = q[2], bhi = q[3];
                int[] x = LongestMatch(a, b, b2j, alo, ahi, blo, bhi);
                int mi = x[0], mj = x[1], mk = x[2];
                if (mk > 0)
                {
                    blocks.Add(x);
                    if (alo < mi && blo < mj) queue.Add(new[] { alo, mi, blo, mj });
                    if (mi + mk < ahi && mj + mk < bhi) queue.Add(new[] { mi + mk, ahi, mj + mk, bhi });
                }
            }
            blocks.Sort(delegate (int[] p, int[] r)
            {
                for (int k = 0; k < 3; k++) { int c = p[k].CompareTo(r[k]); if (c != 0) return c; }
                return 0;
            });
            List<int[]> merged = new List<int[]>();
            int i1 = 0, j1 = 0, k1 = 0;
            foreach (int[] bl in blocks)
            {
                if (i1 + k1 == bl[0] && j1 + k1 == bl[1]) k1 += bl[2];
                else
                {
                    if (k1 > 0) merged.Add(new[] { i1, j1, k1 });
                    i1 = bl[0]; j1 = bl[1]; k1 = bl[2];
                }
            }
            if (k1 > 0) merged.Add(new[] { i1, j1, k1 });
            merged.Add(new[] { a.Count, b.Count, 0 });

            List<int[]> ops = new List<int[]>();
            int ia = 0, jb = 0;
            foreach (int[] bl in merged)
            {
                int ai = bl[0], bj = bl[1], size = bl[2];
                int tag = -1;
                if (ia < ai && jb < bj) tag = OpReplace;
                else if (ia < ai) tag = OpDelete;
                else if (jb < bj) tag = OpInsert;
                if (tag >= 0) ops.Add(new[] { tag, ia, ai, jb, bj });
                ia = ai + size; jb = bj + size;
                if (size > 0) ops.Add(new[] { OpEqual, ai, ia, bj, jb });
            }
            return ops;
        }

        private static int[] LongestMatch(List<string> a, List<string> b, Dictionary<string, List<int>> b2j, int alo, int ahi, int blo, int bhi)
        {
            int besti = alo, bestj = blo, bestsize = 0;
            Dictionary<int, int> j2len = new Dictionary<int, int>();
            for (int i = alo; i < ahi; i++)
            {
                Dictionary<int, int> newj2len = new Dictionary<int, int>();
                List<int> js;
                if (b2j.TryGetValue(a[i], out js))
                    foreach (int j in js)
                    {
                        if (j < blo) continue;
                        if (j >= bhi) break;
                        int prev;
                        j2len.TryGetValue(j - 1, out prev);
                        int k = prev + 1;
                        newj2len[j] = k;
                        if (k > bestsize) { besti = i - k + 1; bestj = j - k + 1; bestsize = k; }
                    }
                j2len = newj2len;
            }
            // no junk: only the first pair of difflib's extension loops can run
            while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1]) { besti--; bestj--; bestsize++; }
            while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize]) bestsize++;
            return new[] { besti, bestj, bestsize };
        }

        // ---- the HLSL rewriting for FNA --------------------------------------------------------------

        private static readonly Regex AlphaTestRe = new Regex(
            @"\[branch\]\s*if\s*\(g_SpecConstants\(\)\s*&\s*SPEC_CONSTANT_ALPHA_TEST\)\s*\{\s*clip\(oC0\.w\s*-\s*g_AlphaThreshold\);\s*\}",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        private static readonly Regex HalfPixelRe = new Regex(@"^\s*oPos\.xy \+= g_HalfPixelOffset \* oPos\.w;\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex CubeMapRe = new Regex(@"^\s*CubeMapData cubeMapData = \(CubeMapData\)0;\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex AsfloatRe = new Regex(@"asfloat\(uint4\(([^)]*)\)\)", RegexOptions.CultureInvariant);
        private static readonly Regex SetpInvRe = new Regex(@"^(\s*)p0 = (\S+) == 1\.0;\n\s*ps = \2 == 0\.0 \? 1\.0 : \2;", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex Ws = new Regex(@"\s", RegexOptions.CultureInvariant);
        private static readonly Regex BonesIdx = new Regex(@"^(\d+) \+ a0$", RegexOptions.CultureInvariant);

        private static string Transliterate(string body, Hlsl sh, List<string> notes)
        {
            string t = body.Replace("\t", "    ");
            t = AlphaTestRe.Replace(t, "");
            t = HalfPixelRe.Replace(t, "");
            t = CubeMapRe.Replace(t, "");
            t = AsfloatRe.Replace(t, delegate (Match m)
            {
                List<string> vals = new List<string>();
                foreach (string x in m.Groups[1].Value.Split(','))
                    vals.Add(Flit(BitConverter.ToSingle(BitConverter.GetBytes(Convert.ToUInt32(x.Trim(), 16)), 0)));
                return "float4(" + string.Join(", ", vals.ToArray()) + ")";
            });

            t = RewriteCalls(t, "tfetchTexcoord", delegate (List<string> a) { return a[1]; });
            t = RewriteCalls(t, "tfetchR11G11B10", delegate (List<string> a) { return a[0]; });

            t = RewriteCalls(t, "tfetch2D", delegate (List<string> a)
            {
                if (a.Count != 4) throw new InvalidDataException("unexpected tfetch2D arity in " + sh.PathName);
                string smp = a[0].Replace("_Texture2DDescriptorIndex", "");
                if (a[1] != smp + "_SamplerDescriptorIndex") throw new InvalidDataException("texture/sampler mismatch in " + sh.PathName);
                string off = Ws.Replace(a[3], "");
                if (off != "float2(0,0)" && off != "float2(0.0,0.0)") throw new InvalidDataException("non-zero texel offset in " + sh.PathName + " -- not handled");
                if (!sh.Samplers.ContainsKey(smp)) throw new InvalidDataException("sampler " + smp + " not in constant table of " + sh.PathName);
                return "tex2D(" + smp + ", " + a[2] + ")";
            });
            foreach (string bad in new[] { "tfetch3D", "tfetchCube", "tfetch2DBicubic", "getWeights2D", "computeMipLevel" })
                if (t.Contains(bad + "(")) throw new InvalidDataException(sh.PathName + " uses " + bad + " -- not handled");

            t = RewriteCalls(t, "select", delegate (List<string> a)
            {
                if (a.Count != 3) throw new InvalidDataException("select with " + a.Count + " arguments in " + sh.PathName);
                return "((" + a[0] + ") ? (" + a[1] + ") : (" + a[2] + "))";
            });

            int nInv = 0;
            t = SetpInvRe.Replace(t, delegate (Match m)
            {
                nInv++;
                string ind = m.Groups[1].Value, x = m.Groups[2].Value;
                return ind + "p0 = " + x + " == 1.0;\n" + ind + "ps = " + x + " == 1.0 ? 0.0 : (" + x + " == 0.0 ? 1.0 : " + x +
                       ");   /* setp_inv, full definition -- see tool */";
            });
            if (nInv > 0) notes.Add(sh.PathName + ": " + nInv + " setp_inv rewritten to the full hardware definition");

            t = RewriteCalls(t, "clamp", delegate (List<string> a)
            {
                if (a.Count == 3 && a[1] == "FLT_MIN" && a[2] == "FLT_MAX") return a[0];
                return "clamp(" + string.Join(", ", a.ToArray()) + ")";
            });
            if (t.Contains("FLT_MIN") || t.Contains("FLT_MAX"))
                throw new InvalidDataException(sh.PathName + ": FLT_MIN/FLT_MAX used outside the rcp/rsq/log clamp");

            if (t.Contains("Bones("))
                t = RewriteCalls(t, "Bones", delegate (List<string> a)
                {
                    Match m = BonesIdx.Match(a[0]);
                    if (!m.Success) throw new InvalidDataException("unexpected Bones index " + a[0] + " in " + sh.PathName);
                    return "_col(Bones[a0 / 4], " + m.Groups[1].Value + ")";
                });
            return t;
        }

        /// <summary>%.9g, with ".0" when that has no point, exponent, inf or nan.</summary>
        private static string Flit(float f)
        {
            string s = XurReader.G9(f);
            if (s.IndexOf('e') < 0 && s.IndexOf('.') < 0 && s.IndexOf("inf", StringComparison.Ordinal) < 0 && s.IndexOf("nan", StringComparison.Ordinal) < 0)
                s += ".0";
            return s;
        }

        private static List<string> SplitArgs(string s)
        {
            List<string> o = new List<string>();
            int depth = 0;
            StringBuilder cur = new StringBuilder();
            foreach (char ch in s)
            {
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                if (ch == ',' && depth == 0) { o.Add(cur.ToString().Trim()); cur.Length = 0; }
                else cur.Append(ch);
            }
            if (cur.Length > 0) o.Add(cur.ToString().Trim());
            return o;
        }

        /// <summary>`name(` at or after `start`, as a whole word: its start, open paren and matching close.</summary>
        private static int[] FindCall(string text, string name, int start)
        {
            int i = text.IndexOf(name + "(", start, StringComparison.Ordinal);
            while (i >= 0)
            {
                if (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_'))
                {
                    i = text.IndexOf(name + "(", i + 1, StringComparison.Ordinal);
                    continue;
                }
                int j = i + name.Length, depth = 0;
                for (int k = j; k < text.Length; k++)
                {
                    if (text[k] == '(') depth++;
                    else if (text[k] == ')')
                    {
                        depth--;
                        if (depth == 0) return new[] { i, j, k };
                    }
                }
                throw new InvalidDataException("unbalanced parens after " + name);
            }
            return null;
        }

        private static string RewriteCalls(string text, string name, Func<List<string>, string> fn)
        {
            int pos = 0;
            while (true)
            {
                int[] hit = FindCall(text, name, pos);
                if (hit == null) return text;
                int i = hit[0], j = hit[1], k = hit[2];
                string rep = fn(SplitArgs(text.Substring(j + 1, k - j - 1)));
                text = text.Substring(0, i) + rep + text.Substring(k + 1);
                pos = i + rep.Length;
            }
        }

        private static readonly Regex DeclRe = new Regex(
            @"^\s*(float4 (r\d+)|float4 (c25\d)|int (a0)|int (aL)|bool (p0)|float (ps)|uint (pc)) = [^;]*;\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex OutInitRe = new Regex(@"^\s*(o(?:TexCoord|Color)\d+) = 0\.0;\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex UsedRe = new Regex(@"\b(r\d+|c25\d|a0|aL|p0|ps|pc)\b", RegexOptions.CultureInvariant);
        private static readonly Regex ReturnLine = new Regex(@"^\s*return;\s*$", RegexOptions.CultureInvariant);

        /// <summary>Drops the declarations the statements never use.</summary>
        private static string PruneDecls(string t, HashSet<string> keep)
        {
            string[] lines = t.Split('\n');
            List<string> stmts = new List<string>();
            foreach (string l in lines) if (!DeclRe.IsMatch(l)) stmts.Add(l);
            HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in UsedRe.Matches(string.Join("\n", stmts.ToArray()))) used.Add(m.Groups[1].Value);
            List<string> o = new List<string>();
            foreach (string l0 in lines)
            {
                string l = l0;
                Match m = DeclRe.Match(l);
                if (m.Success)
                {
                    string name = null;
                    for (int g = 2; g <= 9 && name == null; g++) if (m.Groups[g].Success) name = m.Groups[g].Value;
                    if (!used.Contains(name) && !l.Contains("= i")) continue;
                }
                Match mo = OutInitRe.Match(l);
                if (mo.Success && keep != null && !keep.Contains(mo.Groups[1].Value)) continue;
                if (ReturnLine.IsMatch(l)) l = "    return;";
                o.Add(l);
            }
            List<string> res = new List<string>();
            int blank = 0;
            foreach (string l in o)
            {
                if (l.Trim().Length == 0)
                {
                    blank++;
                    if (blank > 1) continue;
                }
                else blank = 0;
                res.Add(l);
            }
            return string.Join("\n", res.ToArray()).Trim('\n');
        }

        private static readonly Regex IoWrittenRe = new Regex(@"^\s*(o(?:TexCoord|Color)\d+|oC\d|oPos)(\.[xyzw]+)?\s*=\s*(.*);", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex IoReadRe = new Regex(@"\bi(?:TexCoord|Color)\d+\b", RegexOptions.CultureInvariant);
        private static readonly Regex OutNameRe = new Regex(@"^o(TexCoord|Color)(\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex InNameRe = new Regex(@"^i(TexCoord|Color)(\d+)", RegexOptions.CultureInvariant);
        private static readonly Regex TrailingDigits = new Regex(@"\d+$", RegexOptions.CultureInvariant);
        private static readonly Regex ReturnRe = new Regex(@"^(\s*)return;\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        private static HashSet<string> IoWritten(string body)
        {
            HashSet<string> w = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in IoWrittenRe.Matches(body))
                if (m.Groups[3].Value.Trim() != "0.0") w.Add(m.Groups[1].Value);
            return w;
        }

        private static HashSet<string> IoRead(string body)
        {
            HashSet<string> r = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in IoReadRe.Matches(body)) r.Add(m.Value);
            return r;
        }

        private static string OutDecl(string name)
        {
            if (name == "oPos") return "out float4 oPos : POSITION0";
            if (name.Length == 3 && name.StartsWith("oC", StringComparison.Ordinal) && char.IsDigit(name[2])) return "out float4 " + name + " : COLOR" + name.Substring(2);
            Match m = OutNameRe.Match(name);
            return "out float4 " + name + " : " + m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value;
        }

        private static string InDecl(string name)
        {
            Match m = InNameRe.Match(name);
            return "in float4 " + name + " : " + m.Groups[1].Value.ToUpperInvariant() + m.Groups[2].Value;
        }

        // ---- build and assemble ----------------------------------------------------------------------

        private static string BuildFx(byte[] img, string xexName, string include, List<string> notes)
        {
            Dictionary<string, Container> found = FindLibraryContainers(img);
            SortedSet<string> set = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Tq t in Techniques) { set.Add(t.Vs); if (t.Ps != null) set.Add(t.Ps); }
            List<string> needed = new List<string>(set);
            List<string> missing = new List<string>();
            foreach (string n in needed) if (!found.ContainsKey(n)) missing.Add("shader_" + n);
            if (missing.Count > 0) throw new InvalidDataException("not in " + xexName + ": " + string.Join(", ", missing.ToArray()));

            // the register map, merged: a name must mean the same register in every shader
            List<string> regOrder = new List<string>();
            Dictionary<string, Const> regs = new Dictionary<string, Const>(StringComparer.Ordinal);
            foreach (string n in needed)
                foreach (Const c in found[n].Constants)
                {
                    Const have;
                    if (!regs.TryGetValue(c.Name, out have)) { regs[c.Name] = c; regOrder.Add(c.Name); }
                    else if (have.Set != c.Set || have.Index != c.Index || have.Count != c.Count)
                        throw new InvalidDataException(n + ": " + c.Name + " names two different registers");
                }

            Dictionary<string, Hlsl> shaders = new Dictionary<string, Hlsl>(StringComparer.Ordinal);
            Dictionary<string, string> bodies = new Dictionary<string, string>(StringComparer.Ordinal);
            Dictionary<string, bool> usesVpos = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (int idx = 0; idx < needed.Count; idx++)
            {
                string n = needed[idx];
                Container c = found[n];
                string stem = "AvatarLibrary_" + idx + "_" + c.Kind;
                string text = Recomp(c.Blob, include);

                Dictionary<int, string> boolNames = new Dictionary<int, string>();
                foreach (Const k in c.Constants)
                    if (k.Set == "b") boolNames[k.Index + (c.Kind == "ps" ? 128 : 0)] = k.Name;
                text = ConditionalBlocks(stem, c, text, boolNames, notes, n, include);

                Hlsl sh = ParseHlsl(text, c.Kind, stem + ".restored.hlsl", c.Constants);
                List<string> lines = new List<string>(sh.RawBody.Split('\n'));
                bool vpos = false;
                if (c.Kind == "vs") RewritePalette(lines, VFetches(Dwords(c.Blob, c.UcodeOffset, c.UcodeSize)), n, notes);
                else vpos = RewriteParamgen(lines, n, notes);
                string body = string.Join("\n", lines.ToArray());
                // the VS constants are register arrays: c_modelview(k) -> c_modelview[k]
                List<string> arrays = new List<string>(sh.CbufArrays);
                arrays.Sort(delegate (string p, string q) { return q.Length.CompareTo(p.Length); });
                foreach (string cname in arrays)
                {
                    string cn = cname;
                    body = RewriteCalls(body, cn, delegate (List<string> a) { return cn + "[" + a[0] + "]"; });
                }
                sh.CbufArrays.Clear();
                body = Transliterate(body, sh, notes);
                shaders[n] = sh;
                bodies[n] = body;
                usesVpos[n] = vpos;
            }
            return Assemble(xexName, found, needed, shaders, bodies, usesVpos, regOrder, regs, notes);
        }

        private static string Assemble(string xexName, Dictionary<string, Container> found, List<string> needed, Dictionary<string, Hlsl> shaders,
                                       Dictionary<string, string> bodies, Dictionary<string, bool> usesVpos, List<string> regOrder,
                                       Dictionary<string, Const> regs, List<string> notes)
        {
            List<string> L = new List<string>();
            L.Add("// AvatarLibrary.fx -- GENERATED from " + xexName + " by Avatar Studio's setup. DO NOT EDIT.");
            L.Add("// The XDK avatar library's shaders, transliterated from the player's own console files; never");
            L.Add("// redistributed. Bodies are XenosRecomp's register-level HLSL with the plumbing rewritten");
            L.Add("// (the transliteration's own rules). Every arithmetic statement,");
            L.Add("// swizzle and literal is the compiled shader's own; [ours] marks what FNA forced.");
            L.Add("//");
            foreach (string n in needed)
                L.Add("//   shader_" + n.PadRight(26) + " " + found[n].Target + ", " + found[n].UcodeSize + " bytes of microcode");
            if (notes.Count > 0)
            {
                L.Add("//");
                L.Add("// NOTES FROM THE TRANSLATOR:");
                foreach (string nt in notes) L.Add("//   * " + nt);
            }
            L.Add("");
            L.Add("// ---- constants, from the containers' own constant tables ----");
            List<string> order = new List<string>(regOrder);
            // Python's sorted(key=(set, index)): stable, so equal keys keep the merge's order
            List<KeyValuePair<int, string>> keyed = new List<KeyValuePair<int, string>>();
            for (int i = 0; i < order.Count; i++) keyed.Add(new KeyValuePair<int, string>(i, order[i]));
            keyed.Sort(delegate (KeyValuePair<int, string> p, KeyValuePair<int, string> q)
            {
                Const a = regs[p.Value], b = regs[q.Value];
                int c = string.CompareOrdinal(a.Set, b.Set);
                if (c == 0) c = a.Index.CompareTo(b.Index);
                return c != 0 ? c : p.Key.CompareTo(q.Key);
            });
            foreach (KeyValuePair<int, string> kv in keyed)
            {
                Const c = regs[kv.Value];
                if (c.Set == "c") L.Add("float4       " + c.Name + (c.Count > 1 ? "[" + c.Count + "]" : "") + " : register(c" + c.Index + ");");
                else if (c.Set == "b") L.Add("bool         " + c.Name + " : register(b" + c.Index + ");");
                else if (c.Set == "s") L.Add("sampler      " + c.Name + " : register(s" + c.Index + ");");
                else throw new InvalidDataException("constant " + c.Name + " in register set " + c.Set + " -- not handled");
            }
            L.Add("float4       Palette[" + PaletteSize + "] : register(c" + PaletteReg + ");   // [ours] the bone palette; the console streams it");
            L.Add("");

            HashSet<string> shadowVs = new HashSet<string>(StringComparer.Ordinal), alphaPs = new HashSet<string>(StringComparer.Ordinal);
            foreach (Tq t in Techniques) { if (t.Ps == null) shadowVs.Add(t.Vs); if (t.Alpha) alphaPs.Add(t.Ps); }
            foreach (string n in needed)
            {
                Hlsl sh = shaders[n];
                string body = bodies[n];
                if (sh.Kind == "vs")
                {
                    bool variant = shadowVs.Contains(n);
                    List<string> ins = new List<string>();
                    foreach (string[] inp in sh.Inputs)
                        if (Regex.IsMatch(body, @"\b" + inp[1] + @"\b", RegexOptions.CultureInvariant)) ins.Add("in float4 " + inp[1] + " : " + inp[2]);
                    HashSet<string> w = IoWritten(body);
                    w.Add("oPos");
                    List<string> outs = new List<string>(w);
                    outs.Sort(delegate (string p, string q)
                    {
                        int c = (p != "oPos").CompareTo(q != "oPos");
                        return c != 0 ? c : string.CompareOrdinal(p, q);
                    });
                    string b = PruneDecls(body, new HashSet<string>(outs, StringComparer.Ordinal));
                    foreach (string o in outs)
                        if (o != "oPos" && !Regex.IsMatch(b, @"^\s*" + o + @" = 0\.0;", RegexOptions.Multiline | RegexOptions.CultureInvariant))
                            b = "    " + o + " = 0.0;\n" + b;
                    List<string> decl = new List<string>(ins);
                    foreach (string o in outs) decl.Add(OutDecl(o));
                    string fname = "VS_" + n;
                    if (variant)
                    {
                        // [ours] the depth for the float shadow map: oPos through TEXCOORD0
                        decl.Add("out float4 oDepth : TEXCOORD0");
                        b = ReturnRe.Replace(b, delegate (Match m) { return m.Groups[1].Value + "oDepth = oPos;   // [ours] see the tool\n" + m.Groups[1].Value + "return;"; });
                        if (!b.Contains("oDepth = oPos")) b += "\n    oDepth = oPos;   // [ours] see the tool";
                        fname += "_depth";
                    }
                    L.Add("// ---- shader_" + n + (variant ? "  (+ depth out, [ours])" : ""));
                    L.Add("void " + fname + "(\n    " + string.Join(",\n    ", decl.ToArray()) + ")\n{\n" + b + "\n}\n");
                }
                else
                {
                    string b = PruneDecls(body, null);
                    List<string> reads = new List<string>(IoRead(b));
                    reads.Sort(delegate (string p, string q)
                    {
                        int c = p[1].CompareTo(q[1]);
                        if (c != 0) return c;
                        return int.Parse(TrailingDigits.Match(p).Value, CultureInfo.InvariantCulture)
                                  .CompareTo(int.Parse(TrailingDigits.Match(q).Value, CultureInfo.InvariantCulture));
                    });
                    List<string> ins = new List<string>();
                    foreach (string r in reads) ins.Add(InDecl(r));
                    if (usesVpos[n]) { ins.Add("in float2 vPos : VPOS"); ins.Add("in float vFace : VFACE"); }
                    if (alphaPs.Contains(n))
                    {
                        string test = "    clip(oC0.w >= " + AlphaRef + " ? 1.0 : -1.0);   // [ours] the library's alpha test " +
                                      "(GREATEREQUAL, 128; sub_9214DA80), which XNA 4 cannot set";
                        b = ReturnRe.Replace(b, delegate (Match m) { return test + "\n" + m.Groups[1].Value + "return;"; });
                        if (!b.Contains("[ours] the library's alpha test")) b += "\n" + test;
                    }
                    List<string> decl = new List<string>(ins);
                    decl.Add("out float4 oC0 : COLOR0");
                    L.Add("// ---- shader_" + n);
                    L.Add("void PS_" + n + "(\n    " + string.Join(",\n    ", decl.ToArray()) + ")\n{\n" + b + "\n}\n");
                }
            }

            L.Add("// [ours] the shadow map's pixel shader: z / w into a float target (see the tool)");
            L.Add("float4 PS_depth(in float4 iDepth : TEXCOORD0) : COLOR0");
            L.Add("{");
            L.Add("    return float4(iDepth.z / iDepth.w, 0.0, 0.0, 1.0);");
            L.Add("}");
            L.Add("");
            foreach (Tq t in Techniques)
            {
                L.Add("technique " + t.Name);
                L.Add("{");
                L.Add("    pass P0");
                L.Add("    {");
                if (t.Ps == null)
                {
                    L.Add("        VertexShader = compile vs_3_0 VS_" + t.Vs + "_depth();");
                    L.Add("        PixelShader  = compile ps_3_0 PS_depth();");
                }
                else
                {
                    L.Add("        VertexShader = compile vs_3_0 VS_" + t.Vs + "();");
                    L.Add("        PixelShader  = compile ps_3_0 PS_" + t.Ps + "();");
                }
                L.Add("    }");
                L.Add("}");
            }
            L.Add("");
            return string.Join("\n", L.ToArray());
        }

        // ---- the compile: d3dcompiler_47.dll's D3DCompile, fx_2_0, no preshaders (fxc /Op) ------------

        [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3DBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] UIntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern int D3DCompile(byte[] srcData, UIntPtr srcDataSize, string sourceName, IntPtr defines, IntPtr include,
                                             string entrypoint, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);

        private const uint D3DCOMPILE_NO_PRESHADER = 1u << 8;

        /// <summary>The .fx as fxc /Op /T fx_2_0 compiles it (the same bytes).</summary>
        private static byte[] Compile(string fx)
        {
            byte[] src = Encoding.UTF8.GetBytes(fx);
            ID3DBlob code, errors;
            int hr = D3DCompile(src, (UIntPtr)src.Length, null, IntPtr.Zero, IntPtr.Zero, null, "fx_2_0", D3DCOMPILE_NO_PRESHADER, 0, out code, out errors);
            string messages = BlobText(errors);
            try
            {
                if (hr < 0 || code == null) throw new InvalidDataException(string.Format("d3dcompiler_47 refused the effect (0x{0:X8}): {1}", hr, messages));
                byte[] o = new byte[(long)code.GetBufferSize()];
                Marshal.Copy(code.GetBufferPointer(), o, 0, o.Length);
                return o;
            }
            finally
            {
                if (code != null) Marshal.ReleaseComObject(code);
                if (errors != null) Marshal.ReleaseComObject(errors);
            }
        }

        private static string BlobText(ID3DBlob b)
        {
            if (b == null) return "";
            byte[] o = new byte[(long)b.GetBufferSize()];
            Marshal.Copy(b.GetBufferPointer(), o, 0, o.Length);
            return Encoding.ASCII.GetString(o).TrimEnd('\0').Trim();
        }

        // ---- shared --------------------------------------------------------------------------------------

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string CStr(byte[] d, int o)
        {
            StringBuilder s = new StringBuilder();
            for (int i = o; i < d.Length && d[i] != 0; i++) s.Append(d[i] < 0x80 ? (char)d[i] : '\uFFFD');   // decode("ascii", "replace")
            return s.ToString();
        }

        private static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }
    }
}
