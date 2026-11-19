// The STRB container avatar assets come in, its compressed blocks, and the bit readers the formats use.

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    public enum StrbBlockId { Animation = 1, Texture = 2, Model = 3, ShapeOverrides = 4, Skeleton = 5, AssetMetadata = 6, CustomColorTable = 7, AssetMetadataVersioned = 8 }

    public struct StrbBlock
    {
        public int Id, Offset, Length, FieldSize;
    }

    public sealed class StrbFile
    {
        /// <summary>The avatar asset namespace every avatar STRB carries (bytes 6..21).</summary>
        public static readonly Guid AvatarNamespace = new Guid("59bde08b-4909-4ee4-b991-ada87b7c0b6b");

        public readonly byte[] Data;
        public Guid Namespace;
        public bool LittleEndian;
        public readonly List<StrbBlock> Blocks = new List<StrbBlock>();

        private StrbFile(byte[] data) { Data = data; }

        public static StrbFile Read(byte[] b)
        {
            if (b == null || b.Length < 8) return null;
            int strb = 0;
            if (b[0] == 'Y' && b[1] == 'T' && b[2] == 'G' && b[3] == 'R')
            {
                int size = BitConverter.ToInt32(b, 8);
                strb = -1;
                for (int i = Math.Max(size, 12); i + 4 <= b.Length; i++)
                    if (b[i] == 'S' && b[i + 1] == 'T' && b[i + 2] == 'R' && b[i + 3] == 'B') { strb = i; break; }
                if (strb < 0) return null;
            }
            else if (!(b[0] == 'S' && b[1] == 'T' && b[2] == 'R' && b[3] == 'B')) return null;
            if (strb + 30 > b.Length) return null;
            int version = b[strb + 4];
            if (version >= 2) return null;
            StrbFile f = new StrbFile(b);
            f.LittleEndian = b[strb + 5] > 0;
            int at = strb + 6;
            int d1 = f.LittleEndian ? BitConverter.ToInt32(b, at) : (b[at] << 24 | b[at + 1] << 16 | b[at + 2] << 8 | b[at + 3]);
            short d2 = f.LittleEndian ? BitConverter.ToInt16(b, at + 4) : (short)(b[at + 4] << 8 | b[at + 5]);
            short d3 = f.LittleEndian ? BitConverter.ToInt16(b, at + 6) : (short)(b[at + 6] << 8 | b[at + 7]);
            f.Namespace = new Guid(d1, d2, d3, b[at + 8], b[at + 9], b[at + 10], b[at + 11], b[at + 12], b[at + 13], b[at + 14], b[at + 15]);
            int idSize = b[strb + 22], spanSize = b[strb + 23];
            int align = version == 1 ? b[strb + 26] : 1;
            if (align == 0 || (align & (align - 1)) != 0) return null;
            Func<int, int> up = v => (v + align - 1) & ~(align - 1);
            int header = up(idSize + 2 * spanSize);
            int offset = strb + up(version != 0 ? 30 : 26), length = 0;
            bool first = true;
            while (true)
            {
                int next = first ? offset : offset + up(length);
                first = false;
                if (next + header > b.Length) break;
                StrbBlock k = new StrbBlock();
                k.Id = (int)Multi(b, next, idSize);
                k.Length = (int)Multi(b, next + idSize, spanSize);
                k.FieldSize = (int)Multi(b, next + idSize + spanSize, spanSize);
                if (k.Length < 0 || k.FieldSize == 0 || k.Length % k.FieldSize != 0) throw new InvalidDataException("not a valid STRB block");
                k.Offset = next + header;
                f.Blocks.Add(k);
                offset = k.Offset;
                length = k.Length;
            }
            return f;
        }

        private static long Multi(byte[] b, int at, int n)
        {
            long v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | b[at + i];
            return n == 4 ? (int)v : n == 2 ? (short)v : v;
        }

        public bool Has(StrbBlockId id) { return Blocks.Exists(k => k.Id == (int)id); }

        public List<StrbBlock> All(StrbBlockId id) { return Blocks.FindAll(k => k.Id == (int)id); }

        public byte[] Raw(StrbBlock k) { byte[] r = new byte[k.Length]; Buffer.BlockCopy(Data, k.Offset, r, 0, k.Length); return r; }

        public byte[] Inflate(StrbBlock k)
        {
            List<KeyValuePair<int, byte[]>> parts = new List<KeyValuePair<int, byte[]>>();
            int at = k.Offset, end = k.Offset + k.Length, total = 0;
            while (at + 12 <= end)
            {
                int size = BitConverter.ToInt32(Data, at), start = BitConverter.ToInt32(Data, at + 4), len = BitConverter.ToInt32(Data, at + 8);
                if (start < 0 || (uint)size > 40000u || (uint)len > 32768u) throw new InvalidDataException("not a valid compressed STRB block");
                at += 12;
                if (at + size > end) throw new InvalidDataException("a compressed chunk runs past its block");
                byte[] outb = new byte[len];
                using (MemoryStream src = new MemoryStream(Data, at, size, false))
                using (MemoryStream dst = new MemoryStream(outb, true))
                {
                    int rc = new Lzx.LzxDecoder(15).Decompress(src, size, dst, len);
                    if (rc != 0) throw new InvalidDataException("an LZX chunk did not decode");
                }
                at += size;
                parts.Add(new KeyValuePair<int, byte[]>(start, outb));
                total = Math.Max(total, start + len);
            }
            byte[] all = new byte[total];
            foreach (KeyValuePair<int, byte[]> p in parts) Buffer.BlockCopy(p.Value, 0, all, p.Key, p.Value.Length);
            return all;
        }
    }

    /// <summary>A position in a block, reading words in either byte order.</summary>
    public sealed class ByteCursor
    {
        public readonly byte[] B;
        public int At;
        public bool Little;

        public ByteCursor(byte[] b, bool little) { B = b; Little = little; }

        public int ReadByte() { if (At >= B.Length) throw new EndOfStreamException(); return B[At++]; }
        public int I32()
        {
            if (At + 4 > B.Length) throw new EndOfStreamException();
            int v = Little ? BitConverter.ToInt32(B, At) : (B[At] << 24 | B[At + 1] << 16 | B[At + 2] << 8 | B[At + 3]);
            At += 4;
            return v;
        }
        public short I16()
        {
            if (At + 2 > B.Length) throw new EndOfStreamException();
            short v = Little ? BitConverter.ToInt16(B, At) : (short)(B[At] << 8 | B[At + 1]);
            At += 2;
            return v;
        }
        public float F32() { int i = I32(); return BitConverter.ToSingle(BitConverter.GetBytes(i), 0); }
        public byte[] Bytes(int n) { if (At + n > B.Length) throw new EndOfStreamException(); byte[] r = new byte[n]; Buffer.BlockCopy(B, At, r, 0, n); At += n; return r; }
    }

    /// <summary>Bits least significant first; a new reader starts at the cursor's next byte.</summary>
    public sealed class BitReader
    {
        private readonly ByteCursor c;
        private int current, bitOfs;

        public BitReader(ByteCursor c) { this.c = c; }

        public uint U(int bits)
        {
            if (bits > 32) throw new InvalidDataException("a bit field wider than 32");
            uint mask = bits == 32 ? 0xFFFFFFFFu : (uint)((1L << bits) - 1);
            uint v = (uint)current >> (8 - bitOfs);
            int shift = bitOfs;
            bits -= bitOfs;
            while (bits > 0)
            {
                current = c.ReadByte();
                v += (uint)(current << shift);
                bits -= 8;
                shift += 8;
            }
            bitOfs = -bits;
            return v & mask;
        }

        public int I(int bits) { return (int)U(bits); }
        public float F() { return BitConverter.ToSingle(BitConverter.GetBytes(U(32)), 0); }
    }

    /// <summary>{min, bit count} (each `head` bits), then values of that many bits plus min.</summary>
    public sealed class IntUnpacker
    {
        private readonly int head;
        private int min, bits;
        public IntUnpacker(int head = 32) { this.head = head; }
        public void Header(BitReader r)
        {
            min = r.I(head);
            bits = r.I(head);
            if (bits > head) throw new InvalidDataException("a corrupted integer stream");
        }
        public int Next(BitReader r) { return r.I(bits) + min; }
    }

    /// <summary>A quantised vector on a staggered lattice: {radius, min x / y / z, three 6-bit counts}.</summary>
    public sealed class Vector3Unpacker
    {
        public int BitsX, BitsY, BitsZ;
        public float MinX, MinY, MinZ, Radius, D0, D1, D2;

        public void Header(BitReader r)
        {
            Radius = r.F(); MinX = r.F(); MinY = r.F(); MinZ = r.F();
            BitsX = r.I(6); BitsY = r.I(6); BitsZ = r.I(6);
            D0 = Radius * 2f; D1 = Radius * 1.6329931f; D2 = Radius * 1.7320508f;
        }

        public void Next(BitReader r, out float x, out float y, out float z)
        {
            int qx = r.I(BitsX), qy = r.I(BitsY), qz = r.I(BitsZ);
            y = MinY + D1 * qy;
            z = MinZ + D2 * qz;
            if ((qy & 1) != 0) z += 0.57735026f * D2;
            x = MinX + D0 * qx;
            if ((qy & 1) != (qz & 1)) x += 0.5f * D0;
        }

        public int BitsPerValue { get { return BitsX + BitsY + BitsZ; } }
    }
}
