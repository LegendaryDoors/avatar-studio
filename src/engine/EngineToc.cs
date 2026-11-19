// The console's catalogue (AvatarAssetPack.toc): the stock assets, and each entry's fields for the random generator.

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    public sealed class TocRecord
    {
        public int Index;
        public Guid Id;
        public uint Category;
        public byte Body, RandomBody, Flags;
        public Colorb[][] ColourSets;      // each three colours, alpha 255
    }

    public static class TocAssets
    {
        /// <summary>The installed catalogue's path; null or a missing file leaves the TOC out.</summary>
        public static Func<string> PathSource;

        private struct Span { public long Offset; public int Size; }

        private static readonly object gate = new object();
        private static bool loaded;
        private static string path;
        private static Dictionary<Guid, Span> spans;
        private static List<TocRecord> entries;

        public static void Reset() { lock (gate) { loaded = false; spans = null; entries = null; path = null; } }

        public static string TocPath { get { Load(); return path; } }
        public static int Count { get { Load(); return spans == null ? 0 : spans.Count; } }
        public static bool Has(Guid id) { Load(); return spans != null && spans.ContainsKey(id); }

        public static List<TocRecord> Entries { get { Load(); return entries ?? new List<TocRecord>(); } }

        /// <summary>The asset's STRB as the TOC holds it, or null.</summary>
        public static byte[] Get(Guid id)
        {
            Load();
            Span s;
            if (spans == null || !spans.TryGetValue(id, out s)) return null;
            try
            {
                using (FileStream f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] b = new byte[s.Size];
                    f.Seek(s.Offset, SeekOrigin.Begin);
                    int got = 0;
                    while (got < b.Length)
                    {
                        int n = f.Read(b, got, b.Length - got);
                        if (n <= 0) return null;
                        got += n;
                    }
                    return b[0] == 'S' && b[1] == 'T' && b[2] == 'R' && b[3] == 'B' ? b : null;
                }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static void Load()
        {
            if (loaded) return;
            lock (gate)
            {
                if (loaded) return;
                try
                {
                    string p = PathSource == null ? null : PathSource();
                    if (p != null && File.Exists(p) && Read(p)) path = p;
                }
                catch (Exception) { spans = null; entries = null; path = null; }
                loaded = true;
            }
        }

        private static uint U32(byte[] d, int o) { return (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]); }

        private static bool Read(string p)
        {
            byte[] head = new byte[48];
            using (FileStream f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (f.Read(head, 0, 48) != 48) return false;
                int hsize, esize, dataDword;
                if (Id(head, V1)) { hsize = 32; esize = 248; dataDword = 60; }
                else if (Id(head, V2)) { hsize = 40; esize = 248; dataDword = 60; }
                else if (Id(head, V3)) { hsize = 48; esize = 268; dataDword = 65; }
                else return false;
                uint count = U32(head, 28);
                long fileLen = f.Length;
                if (count == 0 || hsize + (long)esize * count > fileLen) return false;
                byte[] tail = new byte[8];
                Array.Copy(head, 16, tail, 0, 8);
                tail[0] = (byte)(tail[0] & 0x0F | 0xC0);
                byte[] all = new byte[esize * count];
                f.Seek(hsize, SeekOrigin.Begin);
                int got = 0;
                while (got < all.Length) { int n = f.Read(all, got, all.Length - got); if (n <= 0) return false; got += n; }
                Dictionary<Guid, Span> map = new Dictionary<Guid, Span>();
                List<TocRecord> list = new List<TocRecord>();
                for (int i = 0; i < count; i++)
                {
                    int e = esize * i;
                    Guid g = new Guid((int)U32(all, e), (short)i, (short)all[e + 4], tail);
                    TocRecord t = new TocRecord { Index = i, Id = g, Category = U32(all, e), Body = all[e + 4], RandomBody = all[e + 5], Flags = all[e + 6] };
                    int sets = all[e + 7] >> 4;
                    t.ColourSets = new Colorb[sets][];
                    for (int s = 0; s < sets; s++)
                    {
                        t.ColourSets[s] = new Colorb[3];
                        for (int k = 0; k < 3; k++)
                        {
                            int j = e + 8 + 3 * (3 * s + k);
                            t.ColourSets[s][k] = new Colorb(all[j], all[j + 1], all[j + 2]);
                        }
                    }
                    list.Add(t);
                    uint off = U32(all, e + 4 * dataDword), size = U32(all, e + 4 * dataDword + 4);
                    if (size == 0 || off + (long)size > fileLen) continue;
                    map[g] = new Span { Offset = off, Size = (int)size };
                }
                spans = map;
                entries = list;
                return true;
            }
        }

        // the three format ids, as stored
        private static readonly byte[] V1 = Hex("9ad6ebce62624eeb8a82a3f70b817369");
        private static readonly byte[] V2 = Hex("8a762df4c0674b66b4ee56466b1b8280");
        private static readonly byte[] V3 = Hex("580a07d64bcd40bebf37254be826fb0b");

        private static byte[] Hex(string h)
        {
            byte[] b = new byte[h.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return b;
        }

        private static bool Id(byte[] h, byte[] id)
        {
            for (int i = 0; i < 16; i++) if (h[i] != id[i]) return false;
            return true;
        }
    }
}
