// An avatar asset's model and textures, read from an STRB model block for the renderer.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace AvatarStudio.Avatars
{
    public struct IndexedTriangle { public int i1, i2, i3; }
    public struct Vector4b { public byte X, Y, Z, W; public Vector4b(int v) { X = (byte)(v >> 24); Y = (byte)(v >> 16); Z = (byte)(v >> 8); W = (byte)v; } }
    public struct Colorf { public float red, green, blue, alpha; }

    public sealed class VertexDataBuffer
    {
        public Vector3[] RawPositions, RawNormals;
        public Vector2[][] TextureCoordinates;
        public Colorf[] Color0;
        public Vector4b[] SkinBindings, SkinWeights;

        public VertexDataBuffer(int uvSets, int n)
        {
            RawPositions = new Vector3[n]; RawNormals = new Vector3[n]; Color0 = new Colorf[n];
            SkinBindings = new Vector4b[n]; SkinWeights = new Vector4b[n];
            TextureCoordinates = new Vector2[uvSets][];
            for (int i = 0; i < uvSets; i++) TextureCoordinates[i] = new Vector2[n];
        }

        public VertexDataBuffer Clone()
        {
            VertexDataBuffer c = new VertexDataBuffer(TextureCoordinates.Length, 0);
            c.RawPositions = (Vector3[])RawPositions.Clone(); c.RawNormals = (Vector3[])RawNormals.Clone(); c.Color0 = (Colorf[])Color0.Clone();
            c.SkinBindings = (Vector4b[])SkinBindings.Clone(); c.SkinWeights = (Vector4b[])SkinWeights.Clone();
            for (int i = 0; i < TextureCoordinates.Length; i++) c.TextureCoordinates[i] = (Vector2[])TextureCoordinates[i].Clone();
            return c;
        }
    }

    public struct TriangleBatch
    {
        public IndexedTriangle[] Triangles;
        public VertexDataBuffer Vertices;
        public TriangleBatch Clone() { return new TriangleBatch { Triangles = (IndexedTriangle[])Triangles.Clone(), Vertices = Vertices.Clone() }; }
    }

    public enum ShaderId { BodyOpaque, BodyTransparent, BodyShinyOpaque, BodyShinyTransparent, HeadOpaque, Count }
    public enum ShaderParameterType { None, Texture, VertexConstant, PixelConstant }
    [Flags] public enum TextureWrapModes { None = 0, WrapU = 1, WrapV = 2 }

    /// <summary>A shader parameter's use (the model's own numbering).</summary>
    public enum ShaderParameterUsage
    {
        None, TextureColor, TextureIntensity, TextureDecal, TextureReflection, TextureSkinFeatures, TextureFacialHair,
        TextureEyebrowLeft, TextureEyebrowRight, TextureEyeLeft, TextureEyeRight, TextureEyeShadow, TextureMouth,
        PixelConstantColorSkin, PixelConstantColorHair, PixelConstantColorMouth, PixelConstantColorIris, PixelConstantColorEyebrow,
        PixelConstantColorEyeShadow, PixelConstantColorFacialHair, PixelConstantColorSkinFeature1, PixelConstantColorSkinFeature2,
        PixelConstantColorCustom0, PixelConstantColorCustom1, PixelConstantColorCustom2, PixelConstantReflectivity,
        PixelConstantTransparency, PixelConstantColorRimLight, Count,
    }

    public struct TextureInstance { public short UvLayer, TextureIndex; public TextureWrapModes textureWrapMode; }
    public struct ShaderConstantValue { public float f0, f1, f2, f3; public void SetValue(Vector4 v) { f0 = v.X; f1 = v.Y; f2 = v.Z; f3 = v.W; } }
    public struct ShaderConstantInt { public int i0, i1, i2, i3; }

    /// <summary>A texture reference or four words, as the file holds them.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    public struct ShaderParameterData
    {
        [System.Runtime.InteropServices.FieldOffset(0)] public TextureInstance Texture;
        [System.Runtime.InteropServices.FieldOffset(0)] public ShaderConstantValue Constant;
        [System.Runtime.InteropServices.FieldOffset(0)] public ShaderConstantInt ConstantInt;
    }

    public struct ShaderParameter { public ShaderParameterType type; public ShaderParameterUsage usage; public ShaderParameterData data; }

    public struct ShaderInstance
    {
        public ShaderId ShaderId;
        public ShaderParameter[] ShaderParameters;
        public ShaderInstance Clone() { return new ShaderInstance { ShaderId = ShaderId, ShaderParameters = (ShaderParameter[])ShaderParameters.Clone() }; }
    }

    public enum TextureDataFormat { Dxt1 = 0, Dxt2 = 1, Dxt3 = 2, Dxt4 = 3, Dxt5 = 4, RGBA = 5, Empty = 8 }

    /// <summary>A component's texture: DXT layers (a face part's states), or an empty 4 x 4.</summary>
    public sealed class AvatarTexture
    {
        public readonly int Width, Height;
        public readonly TextureDataFormat RawDataFormat;
        private readonly byte[][] layers;

        public AvatarTexture(int width, int height, int layerCount, TextureDataFormat f)
        {
            Width = width; Height = height;
            // DXT2 / DXT4 drawn as DXT3 / DXT5 (premultiplied alpha unused)
            RawDataFormat = f == TextureDataFormat.Dxt2 ? TextureDataFormat.Dxt3 : f == (TextureDataFormat)9 ? (TextureDataFormat)10
                          : f == TextureDataFormat.Dxt4 ? TextureDataFormat.Dxt5 : f == (TextureDataFormat)11 ? (TextureDataFormat)12 : f;
            layers = new byte[layerCount][];
        }

        public int LayersCount { get { return layers.Length; } }
        public bool IsEmpty { get { return (RawDataFormat & TextureDataFormat.Empty) != 0; } }

        public void SetLayer(int i, byte[] dxt) { layers[i] = dxt; }

        /// <summary>A layer's bytes as the GPU takes them.</summary>
        public byte[] GetTextureLayerRawData(int i)
        {
            switch ((int)RawDataFormat)
            {
                case 8: return new byte[8];
                case 9: case 10: case 11: case 12: return new byte[16];
                case 13: return new byte[64];
                default: return layers[i];
            }
        }
    }

    public sealed class ComponentManifest
    {
        public ComponentInfo ComponentInfo;
        public int ModelIndex;
        public ComponentManifest(ComponentInfo c, int modelIndex) { ComponentInfo = c; ModelIndex = modelIndex; }
    }

    public sealed class AvatarComponent
    {
        public AvatarTexture[] Textures = new AvatarTexture[0];
        public TriangleBatch[] TriangleBatches = new TriangleBatch[0];
        public ShaderInstance[] ShaderInstanceParameters = new ShaderInstance[0];
        public ComponentManifest Manifest;

        public Guid AssetId { get { return Manifest == null ? Guid.Empty : Manifest.ComponentInfo.AssetId; } }
        public AvatarTexture[] GetTextures() { return Textures; }
    }

    public static class ModelParser
    {
        public static void Parse(byte[] block, AvatarComponent m)
        {
            ByteCursor c = new ByteCursor(block, true);
            for (int i = 0; i < 5; i++) c.I32();
            uint batches = (uint)c.I32(), textures = (uint)c.I32(), gpuBase = (uint)c.I32();
            for (int i = 0; i < 4; i++) c.I32();
            if (batches > 16) throw new InvalidDataException("a model with more than 16 batches");
            if (textures > 18) throw new InvalidDataException("a model with more than 18 textures");
            m.TriangleBatches = new TriangleBatch[batches];
            m.ShaderInstanceParameters = new ShaderInstance[batches];
            m.Textures = new AvatarTexture[textures];
            uint[] offsets = new uint[batches], sizes = new uint[batches];
            for (int i = 0; i < batches; i++)
            {
                uint gpu, size;
                ParseBatch(c, out m.TriangleBatches[i], out m.ShaderInstanceParameters[i], out gpu, out size);
                offsets[i] = gpu - gpuBase;
                sizes[i] = size;
            }
            for (int j = 0; j < textures; j++)
            {
                c.I32(); c.I32();
                m.Textures[j] = ParseTexture(c);
            }
            VertexPairs(c, m, offsets, sizes);
        }

        private static void ParseBatch(ByteCursor c, out TriangleBatch b, out ShaderInstance s, out uint gpu, out uint size)
        {
            BitReader r = new BitReader(c);
            s = new ShaderInstance();
            s.ShaderId = (ShaderId)r.I(32);
            uint parameters = r.U(5), triangles = r.U(32), vertices = r.U(32), uvSets = r.U(32);
            size = r.U(32);
            r.U(32);
            gpu = r.U(32);
            r.U(32);
            if (uvSets > 6) throw new InvalidDataException("a batch with more than 6 UV sets");
            if (triangles > 8192 || vertices > 8192) throw new InvalidDataException("a batch over 8192 triangles or vertices");
            if (size != 32 + 4 * (uvSets - 1)) throw new InvalidDataException("a batch's vertex size does not match its UV sets");
            s.ShaderParameters = new ShaderParameter[parameters];
            for (int i = 0; i < parameters; i++)
            {
                ShaderParameter p = new ShaderParameter();
                p.type = (ShaderParameterType)c.I32();
                p.usage = (ShaderParameterUsage)c.I32();
                if (p.type == ShaderParameterType.Texture)
                {
                    p.data.Texture.TextureIndex = c.I16();
                    p.data.Texture.UvLayer = c.I16();
                    p.data.Texture.textureWrapMode = (TextureWrapModes)c.I32();
                    c.I32(); c.I32();
                }
                else
                {
                    if (p.type == ShaderParameterType.VertexConstant) throw new InvalidDataException("a vertex constant in a batch");
                    p.data.ConstantInt.i0 = c.I32(); p.data.ConstantInt.i1 = c.I32(); p.data.ConstantInt.i2 = c.I32(); p.data.ConstantInt.i3 = c.I32();
                }
                s.ShaderParameters[i] = p;
            }
            b = new TriangleBatch();
            b.Vertices = Vertices(c, (int)vertices, (int)uvSets);
            b.Triangles = Triangles(c, (int)triangles);
        }

        private static VertexDataBuffer Vertices(ByteCursor c, int count, int uvSets)
        {
            BitReader r = new BitReader(c);
            if (r.U(32) != (uint)count) throw new InvalidDataException("a batch's vertex count does not match its stream");
            Vector3Unpacker pos = new Vector3Unpacker();
            IntUnpacker normal = new IntUnpacker(), weights = new IntUnpacker(), bindings = new IntUnpacker(), colour = new IntUnpacker();
            pos.Header(r); normal.Header(r); weights.Header(r); bindings.Header(r); colour.Header(r);
            // the U and V unpackers have no header: 16 bits each
            VertexDataBuffer v = new VertexDataBuffer(uvSets, count);
            for (int i = 0; i < count; i++)
            {
                float x, y, z;
                pos.Next(r, out x, out y, out z);
                v.RawPositions[i] = new Vector3(x, y, z);
                int n = normal.Next(r), w = weights.Next(r), bi = bindings.Next(r), col = colour.Next(r);
                for (int k = 0; k < uvSets; k++)
                {
                    short u = (short)r.I(16), vv = (short)r.I(16);
                    v.TextureCoordinates[k][i] = new Vector2(Half((ushort)u), Half((ushort)vv));
                }
                v.RawNormals[i] = PackedNormal(n);
                v.SkinBindings[i] = new Vector4b(bi);
                v.SkinWeights[i] = new Vector4b(w);
                v.Color0[i] = new Colorf { blue = (col & 0xFF) / 255f, green = ((col >> 8) & 0xFF) / 255f, red = ((col >> 16) & 0xFF) / 255f, alpha = ((col >> 24) & 0xFF) / 255f };
            }
            return v;
        }

        private static IndexedTriangle[] Triangles(ByteCursor c, int count)
        {
            BitReader r = new BitReader(c);
            if (r.I(32) != 3 * count) throw new InvalidDataException("a batch's index count does not match its triangles");
            IntUnpacker idx = new IntUnpacker(16);
            idx.Header(r);
            IndexedTriangle[] t = new IndexedTriangle[count];
            for (int i = 0; i < count; i++) { t[i].i1 = idx.Next(r); t[i].i2 = idx.Next(r); t[i].i3 = idx.Next(r); }
            return t;
        }

        /// <summary>11 / 11 / 10 bits, signed, over 1023 / 1023 / 511, then normalised.</summary>
        public static Vector3 PackedNormal(int p)
        {
            if (p == 0) return new Vector3(0.01f, 0.01f, 0.01f);
            float x = (float)((p & 0x7FF) << 21 >> 21) / 1023f;
            float y = (float)((p & 0x3FF800) << 10 >> 21) / 1023f;
            float z = (float)(p >> 22) / 511f;
            float k = 1f / (float)Math.Sqrt(x * x + y * y + z * z);
            return new Vector3(x * k, y * k, z * k);
        }

        /// <summary>A half float (denormals widened, the infinity / NaN exponent kept).</summary>
        public static float Half(ushort h)
        {
            uint e = (uint)(h & 0x7C00), m = (uint)(h & 0x3FF);
            if (e == 0)
            {
                if (m != 0)
                {
                    e = 947912704u;                      // 113 << 23: the exponent of 2^-14, the smallest normal half
                    do { e -= 8388608; m <<= 1; } while ((m & 0x400) == 0);
                    m &= 0x3FF;
                }
            }
            else if (e == 0x7C00) e = 0x7F800000u;
            else e = (e << 13) + 939524096u;               // re-biased: (127 - 15) << 23
            uint bits = (uint)((h & 0x8000) << 16) | e | (m << 13);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        private static void VertexPairs(ByteCursor c, AvatarComponent m, uint[] offsets, uint[] sizes)
        {
            while (true)
            {
                BitReader r = new BitReader(c);
                int n = r.I(32);
                if ((long)n > 400) throw new InvalidDataException("more than 400 vertex pairs");
                if (n % 2 != 0) break;
                IntUnpacker u = new IntUnpacker();
                u.Header(r);
                for (int i = 0; i < n; i += 2)
                {
                    uint a = (uint)u.Next(r), b = (uint)u.Next(r);
                    int ba, va, bb, vb;
                    Locate(a, offsets, sizes, out ba, out va);
                    Locate(b, offsets, sizes, out bb, out vb);
                    VertexDataBuffer from = m.TriangleBatches[ba].Vertices, to = m.TriangleBatches[bb].Vertices;
                    to.RawPositions[vb] = from.RawPositions[va];
                    to.SkinWeights[vb] = from.SkinWeights[va];
                    to.SkinBindings[vb] = from.SkinBindings[va];
                }
            }
        }

        private static void Locate(uint gpu, uint[] offsets, uint[] sizes, out int batch, out int vertex)
        {
            int b = 0;
            while (b < offsets.Length && gpu >= offsets[b]) b++;
            b--;
            if (b < 0) throw new IndexOutOfRangeException("a vertex pair before the first batch");
            batch = b;
            vertex = (int)((gpu - offsets[b]) / sizes[b]);
        }

        // ---- textures ------------------------------------------------------------------------------

        // the D3D format words (Xenos texture formats): DXT1 tiled / linear
        private const uint D3DFMT_DXT1 = 438305106, D3DFMT_LIN_DXT1 = 438304850, D3DFMT_DXT2 = 438305107, D3DFMT_LIN_DXT2 = 438304851,
                           D3DFMT_DXT4 = 438305108, D3DFMT_LIN_DXT4 = 438304852;

        public static AvatarTexture ParseTexture(ByteCursor c)
        {
            BitReader r = new BitReader(c);
            uint format = r.U(32);
            int width = r.I(32), height = r.I(32);
            r.I(32); r.I(32);
            int layers = r.I(32);
            bool empty = r.U(1) != 0;
            r.U(1);
            int pitch = r.I(32), rows = r.I(32);
            if ((uint)width > 1024u || (uint)height > 1024u) throw new InvalidDataException("a texture over 1024");
            if ((uint)layers > 14u || layers == 0) throw new InvalidDataException("a texture with no or more than 14 layers");
            TextureDataFormat f = format == D3DFMT_DXT1 || format == D3DFMT_LIN_DXT1 ? TextureDataFormat.Dxt1
                                : format == D3DFMT_DXT2 || format == D3DFMT_LIN_DXT2 ? TextureDataFormat.Dxt2
                                : format == D3DFMT_DXT4 || format == D3DFMT_LIN_DXT4 ? TextureDataFormat.Dxt4 : TextureDataFormat.Dxt5;
            if (empty) return new AvatarTexture(4, 4, layers, f | TextureDataFormat.Empty);
            AvatarTexture t = new AvatarTexture(width, height, layers, f);
            int block = f == TextureDataFormat.Dxt1 ? 8 : 16;
            if (((width + 3) >> 2) * block != pitch) throw new InvalidDataException("a texture's pitch does not match its width");
            if (((height + 3) >> 2) != rows) throw new InvalidDataException("a texture's rows do not match its height");
            for (int i = 0; i < layers; i++)
            {
                byte[] d = c.Bytes(rows * pitch);
                for (int k = 0; k + 1 < d.Length; k += 2) { byte x = d[k]; d[k] = d[k + 1]; d[k + 1] = x; }
                t.SetLayer(i, d);
            }
            return t;
        }
    }
}
