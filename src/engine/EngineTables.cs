// The console's own tables the avatar engine runs on, copied by the setup from the player's
// AvatarEditor.xex and xam.xex into console-tables.bin.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvatarStudio.Avatars
{
    public static class ConsoleTables
    {
        public static readonly Guid MaleBody = new Guid("00000002-0000-0001-c1c8-f109a19cb2e0");
        public static readonly Guid FemaleBody = new Guid("00000002-0001-0002-c1c8-f109a19cb2e0");

        /// <summary>AVATAR_STUDIO_TABLES, else %LOCALAPPDATA%\Avatar Studio\console-tables.bin.</summary>
        public static string InstalledPath
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_TABLES");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "console-tables.bin");
            }
        }

        private static Dictionary<string, byte[]> tables;
        private static readonly object gate = new object();

        /// <summary>A table by name, or null when the tables are not installed or lack it.</summary>
        public static byte[] Get(string name)
        {
            lock (gate)
            {
                if (tables == null)
                {
                    tables = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                    try { if (File.Exists(InstalledPath)) Read(File.ReadAllBytes(InstalledPath), tables); }
                    catch (Exception e) { Console.WriteLine("[engine] console tables: " + e.Message); }
                }
                byte[] b;
                return tables.TryGetValue(name, out b) ? b : null;
            }
        }

        public static bool Installed { get { return Get("default-required") != null; } }

        /// <summary>Forgets the loaded tables (after the setup writes new ones).</summary>
        public static void Reload() { lock (gate) tables = null; AvatarBuilder.ResetTables(); RandomAvatars.ResetTables(); }

        // ---- what the setup copies out of the editor's image --------------------------------------------

        private struct Region { public string Name, Source; public uint Address; public int Size; public uint Crc; }

        // each table's address in the 17559 image, size and CRC32: another build's file is refused
        private static readonly Region[] Regions =
        {
            new Region { Name = "skeleton-nxe", Source = "editor", Address = 0x9203FAC0, Size = 538, Crc = 0xBFC5A961 },
            new Region { Name = "skeleton-natal", Source = "editor", Address = 0x9203FCE0, Size = 538, Crc = 0xC33927BC },
            new Region { Name = "default-required", Source = "editor", Address = 0x92319880, Size = 256, Crc = 0x8C789EF1 },
            // the float4 constants the body-size code scales joints by (EngineAvatar.cs, SkeletonScaling)
            new Region { Name = "scaling-pool", Source = "editor", Address = 0x9201E0F0, Size = 512, Crc = 0x41986D36 },
            // the heights and widths a random avatar takes (5 floats each)
            new Region { Name = "body-sizes", Source = "editor", Address = 0x92051A48, Size = 40, Crc = 0x9C0FB384 },
            // the colour palettes, the random ones, and the maps from a colour kind to its palette
            new Region { Name = "palettes", Source = "editor", Address = 0x92051A88, Size = 1336, Crc = 0x7FE5DE33 },
            // xam.xex 17559: the random generator's categories, and the rim-light colours
            new Region { Name = "random-categories", Source = "xam", Address = 0x816343C0, Size = 200, Crc = 0x82D16A14 },
            new Region { Name = "rim-light", Source = "xam", Address = 0x81632FA0, Size = 304, Crc = 0xEC3356DF },
            // XAM's rim lookup, sub_81967E08 (its code): RimLookup reads the 18 records out of it
            new Region { Name = "rim-lookup", Source = "xam", Address = 0x81967E08, Size = 728, Crc = 0x887BE76F },
            // XAM's random hair choice, sub_81981448 (its code), and the palette block it reads
            new Region { Name = "random-hair", Source = "xam", Address = 0x81981448, Size = 280, Crc = 0xBCA1305C },
            new Region { Name = "xam-palettes", Source = "xam", Address = 0x81634610, Size = 1354, Crc = 0x998969A8 },
        };

        /// <summary>XAM's random hair choice, read out of its instructions: the skin palette and each position's hair table.</summary>
        public sealed class Hair
        {
            public uint SkinPalette;          // in "xam-palettes"
            public int SkinCount;
            public uint[] Table;               // per skin position: the hair table's address, and its count
            public int[] Count;
            public uint DefaultTable;
            public int DefaultCount;
        }

        public static Hair HairLookup()
        {
            byte[] code = Get("random-hair"), pal = Get("xam-palettes");
            if (code == null || pal == null) return null;
            const uint At = 0x81981448, PalAt = 0x81634610;
            uint[] gpr = new uint[32];
            uint palette = 0, jump = 0, caseBase = 0, defaultAt = 0, lastBranch = 0;
            int bound = -1, lastCmp = -1, pendingCount = -1;
            uint pendingAt = 0;
            bool afterBctr = false;
            Dictionary<uint, KeyValuePair<uint, int>> cases = new Dictionary<uint, KeyValuePair<uint, int>>();
            for (int o = 0; o + 4 <= code.Length; o += 4)
            {
                uint w = (uint)(code[o] << 24 | code[o + 1] << 16 | code[o + 2] << 8 | code[o + 3]);
                uint a = At + (uint)o, op = w >> 26;
                int d = (int)(w >> 21) & 31, ra = (int)(w >> 16) & 31, rb = (int)(w >> 11) & 31;
                uint imm = w & 0xFFFF;
                int simm = (short)imm;
                if (op == 15) gpr[d] = ra == 0 ? imm << 16 : gpr[ra] + (imm << 16);                         // lis
                else if (op == 14)                                                                             // li / addi
                {
                    uint v = (uint)((ra == 0 ? 0 : (int)gpr[ra]) + simm);
                    if (afterBctr && d == 4 && ra == 0) { pendingCount = simm; pendingAt = a; }                // a case's count
                    else if (afterBctr && d == 11 && ra == 31 && pendingCount >= 0)                            // its table
                    { cases[pendingAt] = new KeyValuePair<uint, int>(palette + (uint)simm, pendingCount); pendingCount = -1; }
                    else if (d == 31 && palette == 0) palette = v;
                    if (!(afterBctr && d == 11)) gpr[d] = v;
                }
                else if (op == 10 && (w >> 23 & 7) == 6) { lastCmp = (int)imm; lastBranch = 0; }              // cmplwi cr6
                else if (op == 16) lastBranch = (uint)(a + (short)(w & 0xFFFC));                             // a conditional branch
                else if (op == 31 && ((w >> 1) & 0x3FF) == 87 && ra == 12 && jump == 0)                      // lbzx r0, r12, r11: the
                { jump = gpr[12]; bound = lastCmp; defaultAt = lastBranch; }                                 // jump table; the compare and
                                                                                                              // bgt just before it bound it
                else if (op == 31 && ((w >> 1) & 0x3FF) == 266 && d == 12) caseBase = gpr[12];               // add r12, r12, r0
                else if (op == 19 && ((w >> 1) & 0x3FF) == 528) afterBctr = true;                            // bctr
            }
            if (palette != PalAt || jump == 0 || caseBase == 0 || bound < 0 || cases.Count == 0 || !cases.ContainsKey(defaultAt)) return null;
            int n = bound + 1;
            Hair h = new Hair { SkinPalette = palette, SkinCount = n, Table = new uint[n], Count = new int[n],
                                DefaultTable = cases[defaultAt].Key, DefaultCount = cases[defaultAt].Value };
            for (int i = 0; i < n; i++)
            {
                int j = (int)(jump - PalAt) + i;
                if (j < 0 || j >= pal.Length) return null;
                KeyValuePair<uint, int> c;
                if (!cases.TryGetValue(caseBase + pal[j], out c)) return null;
                h.Table[i] = c.Key; h.Count[i] = c.Value;
            }
            return h;
        }

        /// <summary>A word of "xam-palettes" by its 17559 address (0 outside it).</summary>
        public static uint XamWord(uint address)
        {
            byte[] p = Get("xam-palettes");
            int o = (int)(address - 0x81634610);
            return p == null || o < 0 || o + 4 > p.Length ? 0 : (uint)(p[o] << 24 | p[o + 1] << 16 | p[o + 2] << 8 | p[o + 3]);
        }

        /// <summary>Every table this build reads is installed (an older file makes the setup reinstall).</summary>
        public static bool Complete
        {
            get
            {
                foreach (Region r in Regions) if (Get(r.Name) == null) return false;
                return true;
            }
        }

        /// <summary>XAM's rim lookup's 18 records {key colour, rim address}, read by running its instructions once.</summary>
        public static KeyValuePair<uint, uint>[] RimLookup()
        {
            byte[] code = Get("rim-lookup");
            if (code == null) return null;
            uint[] gpr = new uint[32];
            bool[] known = new bool[32];
            Dictionary<int, uint> vr = new Dictionary<int, uint>();
            Dictionary<uint, uint> keys = new Dictionary<uint, uint>(), rims = new Dictionary<uint, uint>();
            Func<int, uint> reg = r => r == 0 ? 0u : gpr[r];
            for (int o = 0; o + 4 <= code.Length; o += 4)
            {
                uint w = (uint)(code[o] << 24 | code[o + 1] << 16 | code[o + 2] << 8 | code[o + 3]);
                uint op = w >> 26;
                int d = (int)(w >> 21) & 31, a = (int)(w >> 16) & 31, b = (int)(w >> 11) & 31;
                uint imm = w & 0xFFFF;
                int simm = (short)imm;
                if (op == 31 && ((w >> 1) & 0x3FF) == 467) break;                     // mtctr: the walk begins
                if (op == 15) { gpr[d] = a == 0 ? imm << 16 : gpr[a] + (imm << 16); known[d] = a == 0 || known[a]; }   // lis / addis
                else if (op == 14) { gpr[d] = (uint)((a == 0 ? 0 : (int)gpr[a]) + simm); known[d] = a == 0 || known[a]; }  // li / addi
                else if (op == 24) { gpr[a] = gpr[d] | imm; known[a] = known[d]; }    // ori rA, rS, imm
                else if (op == 36 && known[a] && known[d]) keys[(uint)(gpr[a] + simm)] = gpr[d];   // stw rS, d(rA)
                else if (op == 4 && ((w & 0x7F3) == 0xC3 || (w & 0x7F3) == 0x1C3))
                {
                    int v = d | (int)((w >> 2) & 3) << 5;
                    uint at = reg(a) + gpr[b];
                    if ((w & 0x7F3) == 0xC3) vr[v] = at;                                 // lvx128: a rim from the pool
                    else if (vr.ContainsKey(v)) rims[at] = vr[v];                        // stvx128: into a record
                }
            }
            if (rims.Count != 18) return null;
            List<uint> starts = new List<uint>(rims.Keys);
            starts.Sort();
            KeyValuePair<uint, uint>[] recs = new KeyValuePair<uint, uint>[18];
            for (int i = 0; i < 18; i++)
            {
                uint key;
                if (!keys.TryGetValue(starts[i] + 0x10, out key) || (i > 0 && starts[i] - starts[i - 1] != 0x20)) return null;
                recs[i] = new KeyValuePair<uint, uint>(key, rims[starts[i]]);
            }
            return recs;
        }

        /// <summary>A float4 at an address of the 17559 image inside a copied table, or null.</summary>
        public static Microsoft.Xna.Framework.Vector4? Vector4(string table, uint tableAddress, uint address)
        {
            byte[] t = Get(table);
            int o = (int)(address - tableAddress);
            if (t == null || o < 0 || o + 16 > t.Length) return null;
            Func<int, float> f = k => BitConverter.ToSingle(new byte[] { t[k + 3], t[k + 2], t[k + 1], t[k] }, 0);
            return new Microsoft.Xna.Framework.Vector4(f(o), f(o + 4), f(o + 8), f(o + 12));
        }

        /// <summary>A float4 (x, y, z) at an address of the 17559 image inside a copied table, or null.</summary>
        public static Microsoft.Xna.Framework.Vector3? Vector(string table, uint tableAddress, uint address)
        {
            byte[] t = Get(table);
            int o = (int)(address - tableAddress);
            if (t == null || o < 0 || o + 12 > t.Length) return null;
            Func<int, float> f = k => BitConverter.ToSingle(new byte[] { t[k + 3], t[k + 2], t[k + 1], t[k] }, 0);
            return new Microsoft.Xna.Framework.Vector3(f(o), f(o + 4), f(o + 8));
        }

        /// <summary>The tables file, from the editor's and XAM's images; throws on a table not the 17559 bytes.</summary>
        public static byte[] Extract(byte[] editor, uint editorBase, byte[] xam, uint xamBase, Func<byte[], uint> crc32)
        {
            List<KeyValuePair<string, byte[]>> l = new List<KeyValuePair<string, byte[]>>();
            foreach (Region r in Regions)
            {
                byte[] image = r.Source == "xam" ? xam : editor;
                uint imageBase = r.Source == "xam" ? xamBase : editorBase;
                long at = (long)r.Address - imageBase;
                if (image == null || at < 0 || at + r.Size > image.Length) throw new InvalidDataException("the " + r.Source + " image has no " + r.Name + " at 0x" + r.Address.ToString("X8"));
                byte[] b = new byte[r.Size];
                Buffer.BlockCopy(image, (int)at, b, 0, r.Size);
                uint c = crc32(b);
                if (c != r.Crc) throw new InvalidDataException(r.Name + ": CRC32 " + c.ToString("X8") + ", not the 17559 " + r.Source + "'s " + r.Crc.ToString("X8"));
                l.Add(new KeyValuePair<string, byte[]>(r.Name, b));
            }
            return Write(l);
        }

        public static int RegionCount { get { return Regions.Length; } }

        public static byte[] Write(IList<KeyValuePair<string, byte[]>> list)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                byte[] magic = Encoding.ASCII.GetBytes("AVSTAB01");
                ms.Write(magic, 0, 8);
                ms.Write(BitConverter.GetBytes(list.Count), 0, 4);
                foreach (KeyValuePair<string, byte[]> kv in list)
                {
                    byte[] n = Encoding.ASCII.GetBytes(kv.Key);
                    ms.WriteByte((byte)n.Length);
                    ms.Write(n, 0, n.Length);
                    ms.Write(BitConverter.GetBytes(kv.Value.Length), 0, 4);
                    ms.Write(kv.Value, 0, kv.Value.Length);
                }
                return ms.ToArray();
            }
        }

        private static void Read(byte[] b, Dictionary<string, byte[]> into)
        {
            if (b.Length < 12 || Encoding.ASCII.GetString(b, 0, 8) != "AVSTAB01") throw new InvalidDataException("not a console tables file");
            int n = BitConverter.ToInt32(b, 8), at = 12;
            for (int i = 0; i < n; i++)
            {
                int nl = b[at++];
                string name = Encoding.ASCII.GetString(b, at, nl); at += nl;
                int len = BitConverter.ToInt32(b, at); at += 4;
                byte[] v = new byte[len];
                Buffer.BlockCopy(b, at, v, 0, len); at += len;
                into[name] = v;
            }
        }

        // ---- the colour palettes ----------------------------------------------------------------------

        private const uint PalettesAt = 0x92051A88, PickerMap = 0x92051F30, RandomMap = 0x92051F78;

        private static Colorb[] Palette(uint map, DynamicColorType t)
        {
            byte[] p = Get("palettes");
            if (p == null || t < 0 || t >= DynamicColorType.Count) return new Colorb[0];
            int e = (int)(map - PalettesAt) + 8 * (int)t;
            uint at = (uint)(p[e] << 24 | p[e + 1] << 16 | p[e + 2] << 8 | p[e + 3]);
            int n = p[e + 4] << 24 | p[e + 5] << 16 | p[e + 6] << 8 | p[e + 7];
            if (at == 0 || n <= 0) return new Colorb[0];
            int o = (int)(at - PalettesAt);
            if (o < 0 || o + 4 * n > p.Length) return new Colorb[0];
            Colorb[] c = new Colorb[n];
            for (int i = 0; i < n; i++) c[i] = new Colorb(p[o + 4 * i + 1], p[o + 4 * i + 2], p[o + 4 * i + 3], p[o + 4 * i]);
            return c;
        }

        /// <summary>A colour kind's picker palette, in the editor's order (map at 0x92051F30); empty without tables.</summary>
        public static Colorb[] PickerPalette(DynamicColorType t) { return Palette(PickerMap, t); }

        /// <summary>A colour kind's random palette (map at 0x92051F78).</summary>
        public static Colorb[] RandomPalette(DynamicColorType t) { return Palette(RandomMap, t); }

        /// <summary>Where a colour sits in a kind's picker palette (by R, G, B), or -1.</summary>
        public static int PalettePosition(DynamicColorType t, Colorb c)
        {
            Colorb[] p = PickerPalette(t);
            for (int i = 0; i < p.Length; i++) if (p[i].red == c.red && p[i].green == c.green && p[i].blue == c.blue) return i;
            return -1;
        }

        /// <summary>A body colour index's hair table for random avatars: one of six runs in the palettes.</summary>
        public static Colorb[] Run(uint address, int count)
        {
            byte[] p = Get("palettes");
            int o = (int)(address - PalettesAt);
            if (p == null || o < 0 || o + 4 * count > p.Length) return new Colorb[0];
            Colorb[] c = new Colorb[count];
            for (int i = 0; i < count; i++) c[i] = new Colorb(p[o + 4 * i + 1], p[o + 4 * i + 2], p[o + 4 * i + 3], p[o + 4 * i]);
            return c;
        }

        // ---- the default required components ---------------------------------------------------------

        /// <summary>Test hook: other defaults to check the rules against. Null in the studio.</summary>
        public static ComponentInfo[,] DefaultsOverride;

        /// <summary>The body's default for required slot `i` (shoes, trousers, shirt, hair), from the tables.</summary>
        public static ComponentInfo DefaultRequired(AvatarGender body, int i)
        {
            int row = body == AvatarGender.Male ? 0 : 1;
            if (DefaultsOverride != null) return DefaultsOverride[row, i];
            byte[] t = Get("default-required");
            if (t == null || t.Length < 256) return ComponentInfo.Zero;
            int o = (row * 4 + i) * 32;
            ComponentInfo c = new ComponentInfo();
            c.AssetId = AssetIds.FromBytes(t, o);
            c.ComponentMask = (AvatarComponentMasks)(short)(t[o + 16] << 8 | t[o + 17]);
            c.CustomColor0 = new Colorb(t[o + 21], t[o + 22], t[o + 23], t[o + 20]);
            c.CustomColor1 = new Colorb(t[o + 25], t[o + 26], t[o + 27], t[o + 24]);
            c.CustomColor2 = new Colorb(t[o + 29], t[o + 30], t[o + 31], t[o + 28]);
            return c;
        }
    }
}
