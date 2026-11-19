// The console's avatar catalogue, AvatarAssetPack.toc, read as XAM reads it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvatarStudio
{
    sealed class TocEntry
    {
        public Guid Id;
        public uint Category;
        public byte Body;
        public byte Flags;
        public int ColourSets;
        public int ColoursShown;        // m, the low nibble: how many of a set's colours a cell shows
        public uint[][] Sets;           // n sets of three 0xFFRRGGBB, as the editor's loader builds them
        public bool Colourable;
        public uint SortKey;          // 0 in the file is stored as uint.MaxValue: last (sub_922D5900)
        public Guid Linked;
        /// <summary>The second link (+172); XAM merges up to two.</summary>
        public Guid Linked2;
        public string Name;
    }

    static class AvatarToc
    {
        private static Dictionary<Guid, TocEntry> map;
        public static string Status = "not loaded";
        public static bool Loaded { get { return map != null; } }

        /// <summary>%LOCALAPPDATA%\Avatar Studio\AvatarAssetPack.toc, or AVATAR_STUDIO_CATALOGUE.</summary>
        public static string InstalledPath
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_CATALOGUE");
                if (!string.IsNullOrEmpty(env)) return env;
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(local, "Avatar Studio"), "AvatarAssetPack.toc");
            }
        }

        public static TocEntry Find(Guid g)
        {
            TocEntry e;
            return map != null && map.TryGetValue(g, out e) ? e : null;
        }

        private static readonly byte[][] Ids =
        {
            Hex("9ad6ebce62624eeb8a82a3f70b817369"),
            Hex("8a762df4c0674b66b4ee56466b1b8280"),
            Hex("580a07d64bcd40bebf37254be826fb0b"),
        };
        private static readonly int[] HeaderSize = { 32, 40, 48 };
        private static readonly int[] EntrySize = { 248, 248, 268 };

        public static void Load() { Load(InstalledPath); }

        public static void Load(string path)
        {
            map = null;
            if (!File.Exists(path)) { Status = "no " + path + " (the setup installs it)"; return; }
            try
            {
                byte[] d = File.ReadAllBytes(path);
                int fmt = -1;
                for (int k = 0; k < Ids.Length; k++) if (Same(d, 0, Ids[k])) fmt = k;
                if (fmt < 0) { Status = "not an AvatarAssetPack.toc (unknown format id)"; return; }
                ulong suffix = U64(d, 16);
                uint count = U32(d, 28);
                ulong tailv = (suffix & 0x0FFFFFFFFFFFFFFFUL) | (3UL << 62);
                byte[] tail = new byte[8];
                for (int b = 0; b < 8; b++) tail[b] = (byte)(tailv >> (56 - 8 * b));
                Dictionary<Guid, TocEntry> m = new Dictionary<Guid, TocEntry>();
                for (uint i = 0; i < count; i++)
                {
                    int o = HeaderSize[fmt] + EntrySize[fmt] * (int)i;
                    if (o + 196 > d.Length) break;
                    TocEntry e = new TocEntry();
                    e.Category = U32(d, o);
                    e.Body = d[o + 4];
                    e.Flags = d[o + 6];
                    e.ColourSets = d[o + 7] >> 4;
                    e.ColoursShown = d[o + 7] & 0xF;
                    e.Colourable = e.ColourSets > 1 || (e.ColourSets == 0 && (e.Flags & 8) != 0);
                    // set s is three 0xFFRRGGBB colours, filled only when m <= 3 (0x9221760C)
                    e.Sets = new uint[e.ColourSets][];
                    for (int s = 0; s < e.ColourSets; s++)
                    {
                        e.Sets[s] = new uint[3];
                        if (e.ColoursShown > 3) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int j = o + 8 + 3 * (3 * s + k);
                            e.Sets[s][k] = 0xFF000000u | (uint)d[j] << 16 | (uint)d[j + 1] << 8 | d[j + 2];
                        }
                    }
                    uint key = U32(d, o + 152);
                    e.SortKey = key == 0 ? uint.MaxValue : key;
                    byte[] lk = new byte[16];
                    Array.Copy(d, o + 156, lk, 0, 16);
                    e.Linked = FieldGuid(lk);
                    byte[] lk2 = new byte[16];
                    Array.Copy(d, o + 172, lk2, 0, 16);
                    e.Linked2 = FieldGuid(lk2);
                    e.Name = Utf16(d, (int)U32(d, o + 192), 64);
                    e.Id = new Guid((int)e.Category, (short)(ushort)i, (short)e.Body, tail);
                    m[e.Id] = e;
                }
                map = m;
                Status = "loaded " + m.Count + " entries from " + path;
            }
            catch (Exception ex) { Status = "unreadable: " + ex.Message; }
        }

        // ---- big-endian helpers --------------------------------------------------------------------
        private static uint U32(byte[] d, int o) { return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]); }
        private static ulong U64(byte[] d, int o) { return (ulong)U32(d, o) << 32 | U32(d, o + 4); }
        private static bool Same(byte[] d, int o, byte[] s) { if (d.Length < o + s.Length) return false; for (int i = 0; i < s.Length; i++) if (d[o + i] != s[i]) return false; return true; }
        private static byte[] Hex(string s) { byte[] b = new byte[s.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(s.Substring(2 * i, 2), 16); return b; }

        /// <summary>A GUID stored field-wise big-endian, as manifests and the TOC store it.</summary>
        private static Guid FieldGuid(byte[] b)
        {
            byte[] tail = new byte[8];
            Array.Copy(b, 8, tail, 0, 8);
            return new Guid((int)U32(b, 0), (short)(b[4] << 8 | b[5]), (short)(b[6] << 8 | b[7]), tail);
        }

        private static string Utf16(byte[] d, int o, int max)
        {
            if (o <= 0 || o >= d.Length) return null;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < max && o + 2 * i + 1 < d.Length; i++)
            {
                char c = (char)(d[o + 2 * i] << 8 | d[o + 2 * i + 1]);
                if (c == 0) break;
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
