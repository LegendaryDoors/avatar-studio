// Reads files out of an Xbox 360 STFS package (CON / LIVE / PIRS) and a GPD's settings.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvatarStudio
{
    sealed class StfsEntry
    {
        public int Index;
        public string Name, Path;
        public int Flags, BlocksForFile, StartingBlock, PathIndicator;
        public uint FileSize;
        public bool IsFolder { get { return (Flags & 2) != 0; } }
    }

    sealed class StfsPackage
    {
        public const uint MagicCon = 0x434F4E20, MagicLive = 0x4C495645, MagicPirs = 0x50495253;
        private const int BlockSize = 0x1000;
        private const int Int24Max = 0xFFFFFF;

        private readonly byte[] f;
        public uint Magic { get; private set; }
        public string DisplayName { get; private set; }        // header 0x411, UTF-16BE

        private int headerSize, blockSeparation, fileTableBlockCount, fileTableBlockNum;
        private int allocatedBlocks, packageSex, topLevel, firstHashTable;
        private int stepA, stepB;
        private int[] topStatus;
        public readonly List<StfsEntry> Entries = new List<StfsEntry>();

        /// <summary>The whole package in memory; throws on anything that is not STFS.</summary>
        public StfsPackage(byte[] bytes)
        {
            f = bytes;
            if (f.Length < 0x1000) throw new InvalidDataException("too small for an STFS package");
            Magic = U32(0);
            if (Magic != MagicCon && Magic != MagicLive && Magic != MagicPirs)
                throw new InvalidDataException("not an Xbox 360 package (no CON / LIVE / PIRS magic)");
            headerSize = (int)U32(0x340);                       // after 0x22C + 16 x 16 licences + the 0x14 header hash
            DisplayName = Utf16(0x411, 0x80);
            if (U32(0x3A9) != 0) throw new InvalidDataException("an SVOD package, not STFS");
            ReadVolumeDescriptor(0x379);
            Parse();
        }

        public static bool LooksLikePackage(byte[] b)
        {
            if (b == null || b.Length < 4) return false;
            uint m = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            return m == MagicCon || m == MagicLive || m == MagicPirs;
        }

        // ---- readers (big-endian unless named) --------------------------------------------------

        private uint U32(int at)
        {
            Check(at, 4);
            return (uint)(f[at] << 24 | f[at + 1] << 16 | f[at + 2] << 8 | f[at + 3]);
        }

        private int Int24Le(int at) { Check(at, 3); return f[at] | f[at + 1] << 8 | f[at + 2] << 16; }
        private int Int24Be(int at) { Check(at, 3); return f[at] << 16 | f[at + 1] << 8 | f[at + 2]; }

        private string Utf16(int at, int bytes)
        {
            Check(at, bytes);
            string s = Encoding.BigEndianUnicode.GetString(f, at, bytes);
            int z = s.IndexOf('\0');
            return z >= 0 ? s.Substring(0, z) : s;
        }

        private void Check(int at, int n)
        {
            if (at < 0 || at + n > f.Length) throw new InvalidDataException("package truncated at 0x" + at.ToString("x"));
        }

        // ---- the volume descriptor and the hash tree (ReadStfsVolumeDescriptorEx, Parse) --------

        private void ReadVolumeDescriptor(int at)
        {
            blockSeparation = f[at + 2];
            Check(at + 3, 2);
            fileTableBlockCount = f[at + 3] | f[at + 4] << 8;
            fileTableBlockNum = Int24Le(at + 5);
            allocatedBlocks = (int)U32(at + 8 + 0x14);
        }

        private void Parse()
        {
            packageSex = ~blockSeparation & 1;                     // 0 female, 1 male
            if (packageSex == 0) { stepA = 0xAB; stepB = 0x718F; } else { stepA = 0xAC; stepB = 0x723A; }
            firstHashTable = (headerSize + 0xFFF) & unchecked((int)0xFFFFF000);

            int abc = allocatedBlocks;
            if (abc <= 0xAA) topLevel = 0;
            else if (abc <= 0x70E4) topLevel = 1;
            else if (abc <= 0x4AF768) topLevel = 2;
            else throw new InvalidDataException("invalid allocated block count");

            int trueBlock = LevelNBackingHashBlock(0, topLevel);
            int topTable = (trueBlock << 0xC) + firstHashTable + ((blockSeparation & 2) << 0xB);
            int[] perLevel = { 1, 0xAA, 0x70E4 };
            int count = abc / perLevel[topLevel];
            if (abc > 0x70E4 && abc % 0x70E4 != 0) count++;
            else if (abc > 0xAA && abc % 0xAA != 0) count++;
            topStatus = new int[count];
            for (int i = 0; i < count; i++) { Check(topTable + i * 0x18, 0x18); topStatus[i] = f[topTable + i * 0x18 + 0x14]; }
            ReadFileListing();
        }

        private int Level0BackingHashBlock(int block)
        {
            if (block < 0xAA) return 0;
            int num = (block / 0xAA) * stepA;
            num += ((block / 0x70E4) + 1) << packageSex;
            if (block / 0x70E4 == 0) return num;
            return num + (1 << packageSex);
        }

        private int Level1BackingHashBlock(int block)
        {
            if (block < 0x70E4) return stepA;
            return (1 << packageSex) + (block / 0x70E4) * stepB;
        }

        private int LevelNBackingHashBlock(int block, int level)
        {
            if (level == 0) return Level0BackingHashBlock(block);
            if (level == 1) return Level1BackingHashBlock(block);
            return stepB;
        }

        private int BackingDataBlockNumber(int block)
        {
            int r = (((block + 0xAA) / 0xAA) << packageSex) + block;
            if (block < 0xAA) return r;
            if (block < 0x70E4) return r + (((block + 0x70E4) / 0x70E4) << packageSex);
            return (1 << packageSex) + (r + (((block + 0x70E4) / 0x70E4) << packageSex));
        }

        private int BlockToAddress(int block)
        {
            if (block >= Int24Max) throw new InvalidDataException("block number out of range");
            return (BackingDataBlockNumber(block) << 0xC) + firstHashTable;
        }

        private int HashAddressOfBlock(int block)
        {
            if (block >= allocatedBlocks) throw new InvalidDataException("illegal block number");
            int a = (Level0BackingHashBlock(block) << 0xC) + firstHashTable + (block % 0xAA) * 0x18;
            if (topLevel == 0) a += (blockSeparation & 2) << 0xB;
            else if (topLevel == 1) a += (topStatus[block / 0xAA] & 0x40) << 6;
            else
            {
                int level1Off = (topStatus[block / 0x70E4] & 0x40) << 6;
                // a block's level-1 record is its level-0 table's: (block / 170) % 170
                int pos = (Level1BackingHashBlock(block) << 0xC) + firstHashTable + level1Off + ((block / 0xAA) % 0xAA) * 0x18;
                Check(pos + 0x14, 1);
                a += (f[pos + 0x14] & 0x40) << 6;
            }
            return a;
        }

        private int NextBlock(int block) { return Int24Be(HashAddressOfBlock(block) + 0x15); }

        // ---- the file table (ReadFileListing) -------------------------------------------------

        private void ReadFileListing()
        {
            int block = fileTableBlockNum;
            for (int x = 0; x < fileTableBlockCount; x++)
            {
                int cur = BlockToAddress(block);
                for (int i = 0; i < 0x40; i++)
                {
                    int e = cur + i * 0x40;
                    Check(e, 0x40);
                    int nlen = f[e + 0x28];
                    if ((nlen & 0x3F) == 0) continue;
                    int z = 0;
                    while (z < 0x28 && f[e + z] != 0) z++;
                    if (z == 0) break;
                    StfsEntry en = new StfsEntry();
                    en.Index = x * 0x40 + i;
                    en.Name = Encoding.GetEncoding(28591).GetString(f, e, z);   // latin-1
                    en.BlocksForFile = Int24Le(e + 0x29);
                    en.StartingBlock = Int24Le(e + 0x2F);
                    en.PathIndicator = f[e + 0x32] << 8 | f[e + 0x33];
                    en.FileSize = U32(e + 0x34);
                    en.Flags = nlen >> 6;
                    Entries.Add(en);
                }
                block = NextBlock(block);
            }
            Dictionary<int, StfsEntry> byIndex = new Dictionary<int, StfsEntry>();
            foreach (StfsEntry en in Entries) byIndex[en.Index] = en;
            foreach (StfsEntry en in Entries)
            {
                string p = en.Name;
                StfsEntry cur = en;
                HashSet<int> seen = new HashSet<int>();
                while (cur.PathIndicator != 0xFFFF && byIndex.ContainsKey(cur.PathIndicator) && seen.Add(cur.Index))
                {
                    cur = byIndex[cur.PathIndicator];
                    p = cur.Name + "\\" + p;
                }
                en.Path = p;
            }
        }

        public byte[] Read(StfsEntry e)
        {
            byte[] o = new byte[e.FileSize];
            int done = 0, block = e.StartingBlock;
            while (done < o.Length)
            {
                if (block >= allocatedBlocks) throw new InvalidDataException("illegal block number in " + e.Path);
                int n = Math.Min(BlockSize, o.Length - done);
                int a = BlockToAddress(block);
                Check(a, n);
                Buffer.BlockCopy(f, a, o, done, n);
                done += n;
                if (done < o.Length) block = NextBlock(block);
            }
            return o;
        }

        public StfsEntry Find(string path)
        {
            foreach (StfsEntry e in Entries)
                if (!e.IsFolder && string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }
    }

    static class Gpd
    {
        public const int NamespaceSetting = 3;

        public static byte[] BinarySetting(byte[] gpd, uint id)
        {
            if (gpd.Length < 0x18 || Encoding.ASCII.GetString(gpd, 0, 4) != "XDBF") throw new InvalidDataException("not an XDBF file");
            int etl = (int)Be32(gpd, 8), count = (int)Be32(gpd, 12), fsl = (int)Be32(gpd, 16);
            long baseAt = 0x18L + etl * 0x12L + fsl * 8L;
            for (int i = 0; i < count; i++)
            {
                int e = 0x18 + i * 0x12;
                if (e + 0x12 > gpd.Length) break;
                int ns = gpd[e] << 8 | gpd[e + 1];
                if (ns != NamespaceSetting) continue;
                long at = baseAt + Be32(gpd, e + 10), len = Be32(gpd, e + 14);
                if (len < 24 || at + len > gpd.Length) continue;
                int r = (int)at;
                if (Be32(gpd, r) != id || gpd[r + 8] != 6) continue;        // type 6: binary
                int n = (int)Be32(gpd, r + 16);
                if (n < 0 || 24 + n > len) continue;
                byte[] v = new byte[n];
                Buffer.BlockCopy(gpd, r + 24, v, 0, n);
                return v;
            }
            return null;
        }

        private static uint Be32(byte[] b, int at)
        {
            return (uint)(b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
        }
    }
}
