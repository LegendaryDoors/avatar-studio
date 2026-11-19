// An XEX2's PE image (uncompressed or LZX), for the tables in AvatarEditor.xex and xam.xex.

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    public static class XexFile
    {
        private static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }

        public static byte[] Image(byte[] x, out uint imageBase)
        {
            if (x.Length < 0x18 || x[0] != 'X' || x[1] != 'E' || x[2] != 'X' || x[3] != '2') throw new InvalidDataException("not an XEX2");
            int peOff = (int)U32(x, 0x08), secOff = (int)U32(x, 0x10), nOpt = (int)U32(x, 0x14);
            Dictionary<uint, uint> opt = new Dictionary<uint, uint>();
            for (int i = 0; i < nOpt; i++) opt[U32(x, 0x18 + 8 * i)] = U32(x, 0x1C + 8 * i);
            int size = (int)U32(x, secOff + 4);
            uint ffi;
            if (!opt.TryGetValue(0x3FF, out ffi)) throw new InvalidDataException("no file format info header");
            int infoSize = (int)U32(x, (int)ffi), enc = U16(x, (int)ffi + 4), comp = U16(x, (int)ffi + 6);
            if (enc != 0) throw new InvalidDataException("encrypted image (" + enc + "): not handled");
            byte[] img = new byte[size];
            if (comp == 0) Buffer.BlockCopy(x, peOff, img, 0, Math.Min(size, x.Length - peOff));
            else if (comp == 1)
            {
                int src = peOff, dst = 0;
                for (int i = 0; i < (infoSize - 8) / 8; i++)
                {
                    int data = (int)U32(x, (int)ffi + 8 + 8 * i), zeros = (int)U32(x, (int)ffi + 12 + 8 * i);
                    int n = Math.Min(data, size - dst);
                    if (n > 0) Buffer.BlockCopy(x, src, img, dst, n);
                    src += data; dst += data + zeros;
                    if (dst >= size) break;
                }
            }
            else if (comp == 2)
            {
                uint window = U32(x, (int)ffi + 8);
                int block = (int)U32(x, (int)ffi + 12), at = peOff;
                MemoryStream packed = new MemoryStream();
                while (block != 0)
                {
                    if (at + 24 > x.Length) throw new InvalidDataException("an LZX block past the file's end");
                    int next = (int)U32(x, at), q = at + 24;
                    while (true)
                    {
                        int chunk = U16(x, q);
                        q += 2;
                        if (chunk == 0) break;
                        packed.Write(x, q, chunk);
                        q += chunk;
                    }
                    at += block;
                    block = next;
                }
                int bits = 0;
                while ((1u << bits) < window) bits++;
                img = Inflate(packed.ToArray(), bits, size);
            }
            else throw new InvalidDataException("compression " + comp + ": not handled");
            uint b0;
            imageBase = opt.TryGetValue(0x10201, out b0) ? b0 : U32(x, secOff + 0x110);
            return img;
        }

        /// <summary>One LZX stream, 32 KB of output a frame.</summary>
        private static byte[] Inflate(byte[] packed, int windowBits, int size)
        {
            Lzx.LzxDecoder d = new Lzx.LzxDecoder(Math.Max(15, windowBits));
            MemoryStream input = new MemoryStream(packed, false), output = new MemoryStream(size);
            int done = 0;
            while (done < size)
            {
                int n = Math.Min(32768, size - done);
                if (d.Decompress(input, (int)(input.Length - input.Position), output, n) != 0)
                    throw new InvalidDataException("the image's LZX stream did not decode at " + done);
                done += n;
            }
            return output.ToArray();
        }
    }
}
