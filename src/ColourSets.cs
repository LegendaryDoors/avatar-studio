// The colour sets a garment offers, and the colours an item pick applies.

using System;
using System.IO;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class ColourSetList
    {
        public uint[][] Sets;           // three 0xAARRGGBB a set
        public int Shown;               // m
        public string Source;           // "toc" or "file", for the log
    }

    static class ColourSets
    {
        public const int MaxSets = 18;  // sub_922AB0D0 passes 18 to the table reader

        /// <summary>The sets the colour-set screen offers, or null (none, or no catalogue).</summary>
        public static ColourSetList For(CatalogItem it, string assetDir)
        {
            TocEntry t = it == null ? null : it.Toc;
            if (t == null) return null;
            if (t.ColourSets > 0) return new ColourSetList { Sets = t.Sets, Shown = t.ColoursShown, Source = "toc" };
            if (!t.Colourable) return null;
            return FromAsset(it.Id, assetDir);
        }

        /// <summary>The asset's own table: the catalogue's copy (as XAM serves it), else the folder's.</summary>
        public static ColourSetList FromAsset(Guid id, string assetDir)
        {
            byte[] toc = TocAssets.Get(id);
            if (toc != null) return FromBytes(toc);
            return assetDir == null ? null : FromFile(Path.Combine(assetDir, id.ToString("D") + ".strb"));
        }

        /// <summary>The catalogue's sets only: an item cell's colours (record +56).</summary>
        public static uint[][] TocSets(CatalogItem it)
        {
            return it != null && it.Toc != null && it.Toc.ColourSets > 0 ? it.Toc.Sets : null;
        }

        private static readonly byte[] AvatarNamespace =
            { 0x59, 0xBD, 0xE0, 0x8B, 0x49, 0x09, 0x4E, 0xE4, 0xB9, 0x91, 0xAD, 0xA8, 0x7B, 0x7C, 0x0B, 0x6B };

        /// <summary>sub_92218058: the colour table, an avatar STRB's first block 7.</summary>
        public static ColourSetList FromFile(string path)
        {
            if (!File.Exists(path)) return null;
            byte[] d;
            try { d = File.ReadAllBytes(path); } catch { return null; }
            return FromBytes(d);
        }

        public static ColourSetList FromBytes(byte[] d)
        {
            if (d == null || d.Length < 32 || d[0] != 'S' || d[1] != 'T' || d[2] != 'R' || d[3] != 'B' || d[4] >= 2) return null;
            for (int i = 0; i < 16; i++) if (d[6 + i] != AvatarNamespace[i]) return null;
            int ver = d[4], idSize = d[22], spanSize = d[23];
            int align = ver == 1 ? d[26] : 1;
            if (align == 0 || (align & (align - 1)) != 0) return null;
            Func<int, int> round = v => (v + align - 1) & ~(align - 1);
            int off = round(ver != 0 ? 30 : 26), header = round(idSize + 2 * spanSize);
            while (off + header <= d.Length)
            {
                int id = (int)BE(d, off, idSize), length = (int)BE(d, off + idSize, spanSize);
                int data = off + header;
                if (length < 0 || data + length > d.Length) return null;
                if (id == 7) return Table(d, data, length);
                off = data + round(length);
            }
            return null;
        }

        private static ColourSetList Table(byte[] d, int start, int length)
        {
            long bit = (long)start * 8, end = (long)(start + length) * 8;
            Func<int, uint> read = n =>
            {
                uint v = 0;
                for (int k = 0; k < n; k++, bit++)
                {
                    if (bit >= end) throw new EndOfStreamException();
                    v |= (uint)((d[bit >> 3] >> (int)(bit & 7)) & 1) << k;
                }
                return v;
            };
            try
            {
                uint count = read(32);
                uint[] min = new uint[3], bits = new uint[3];
                for (int c = 0; c < 3; c++) { min[c] = read(32); bits[c] = read(32); if (bits[c] > 32) return null; }
                int n = (int)Math.Min(count, (uint)MaxSets);
                if (n == 0) return null;
                uint[][] sets = new uint[n][];
                int shown = 0;
                for (int s = 0; s < n; s++)
                {
                    sets[s] = new uint[3];
                    for (int c = 0; c < 3; c++) sets[s][c] = unchecked(read((int)bits[c]) + min[c]);
                    uint[] v = sets[s];
                    int distinct = 1 + (v[0] != v[1] ? 1 : 0) + (v[2] != v[0] && v[2] != v[1] ? 1 : 0);
                    shown = Math.Max(shown, distinct);
                }
                return new ColourSetList { Sets = sets, Shown = shown, Source = "file" };
            }
            catch (EndOfStreamException) { return null; }
        }

        private static uint BE(byte[] d, int o, int size)
        {
            uint v = 0;
            for (int i = 0; i < size; i++) v = v << 8 | d[o + i];
            return v;
        }

        public static ComponentColors ToColors(uint[] set)
        {
            return new ComponentColors(new[] { new Colorb(unchecked((int)set[0])), new Colorb(unchecked((int)set[1])), new Colorb(unchecked((int)set[2])) });
        }

        /// <summary>The worn colours of an asset, or null when not worn (sub_9213C388).</summary>
        public static uint[] Worn(AvatarManifest m, Guid asset)
        {
            // (object) because AvatarManifest's == dereferences both sides
            if ((object)m == null || asset == Guid.Empty) return null;
            foreach (ComponentInfo ci in m.GetComponents(AvatarComponentMasks.All))
                if (ci.AssetId == asset)
                    return new[] { (uint)ci.CustomColor0.CompositeArgb, (uint)ci.CustomColor1.CompositeArgb, (uint)ci.CustomColor2.CompositeArgb };
            return null;
        }

        public static bool Same(uint[] a, uint[] b)
        {
            return a != null && b != null && a[0] == b[0] && a[1] == b[1] && a[2] == b[2];
        }

        /// <summary>AE_ItemScreen_GetItem: the cell's set; 0 unless worn in another, -1 with no sets.</summary>
        public static int ItemCellSet(uint[][] sets, AvatarManifest snapshot, Guid asset)
        {
            if (sets == null || sets.Length == 0) return -1;
            uint[] worn = Worn(snapshot, asset);
            if (worn != null)
                for (int s = 0; s < sets.Length; s++) if (Same(sets[s], worn)) return s;
            return 0;
        }

        /// <summary>sub_920DBE00: the examine animation for an item's component mask, or 0.</summary>
        public static int ExamineFor(Guid asset)
        {
            int mask = BitConverter.ToInt32(asset.ToByteArray(), 0) & 0x1FFF;
            if ((mask & 0x8) != 0 && (mask & 0x10) != 0) return 15;     // closet_spin
            if ((mask & 0x540) != 0) return 14;                         // closet_examine_headgear
            if ((mask & 0xA88) != 0) return 13;                         // closet_examine_upper_body
            if ((mask & 0x10) != 0) return 12;                          // closet_examine_lower_body
            if ((mask & 0x20) != 0) return 11;                          // closet_examine_shoes
            return 0;
        }
    }
}
