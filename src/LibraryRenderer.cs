// The avatar drawn as the console's editor draws it: the XDK avatar library's own shaders and
// render sequence (the setup's AvatarLibrary.fxb), the self-shadow map redrawn every frame.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using AvatarStudio.Avatars;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    /// <summary>One vertex of a batch, in the order of the library's declarations (unpacked from its packed formats).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct LibraryVertex : IVertexType
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector4 Weights;
        public Vector4 Indices;
        public Vector4 Color;
        public Vector2 T0, T1, T2, T3, T4, T5;

        public static readonly VertexDeclaration Declaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0),
            new VertexElement(40, VertexElementFormat.Vector4, VertexElementUsage.BlendIndices, 0),
            new VertexElement(56, VertexElementFormat.Vector4, VertexElementUsage.Color, 0),
            new VertexElement(72, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(80, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 1),
            new VertexElement(88, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 2),
            new VertexElement(96, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 3),
            new VertexElement(104, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 4),
            new VertexElement(112, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 5));

        VertexDeclaration IVertexType.VertexDeclaration { get { return Declaration; } }
    }

    /// <summary>The library's shaders, jitter map and shadow map, shared by every avatar.</summary>
    sealed class LibraryShaders : IDisposable
    {
        public readonly Effect Fx;
        public readonly Texture2D Jitter;
        public readonly RenderTarget2D ShadowMap;
        public readonly int ShadowSize;

        /// <summary>Where the setup (SetupShaders.cs) installs the effect.</summary>
        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_SHADERS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "shaders");
            }
        }

        /// <summary>The effect, or null when not installed (no avatar is drawn then).</summary>
        public static LibraryShaders TryLoad(GraphicsDevice device)
        {
            string path = Path.Combine(Folder, "AvatarLibrary.fxb");
            if (!File.Exists(path))
            {
                Console.WriteLine("[studio] no " + path + " (the setup's console lighting): no avatar can be drawn");
                return null;
            }
            try { return new LibraryShaders(device, File.ReadAllBytes(path)); }
            catch (Exception e)
            {
                Console.WriteLine("[studio] AvatarLibrary.fxb failed to load (" + e.Message + "): no avatar can be drawn");
                return null;
            }
        }

        private LibraryShaders(GraphicsDevice device, byte[] fxb)
        {
            Fx = new Effect(device, fxb);
            // a 1024 x 1024 shadow map: the renderer's shadow type 2 (sub_920F7940 -> sub_92141860)
            ShadowSize = 1024;
            // [ours] FNA cannot sample a depth buffer: the shadow pass writes z / w to a float target
            ShadowMap = new RenderTarget2D(device, ShadowSize, ShadowSize, false, SurfaceFormat.Single, DepthFormat.Depth24);
            Jitter = MakeJitter(device);
        }

        /// <summary>The jitter map as XAv_SharedState_Init fills it (sub_9214A6D8): 64 x 64, one random u32 a texel.</summary>
        private static Texture2D MakeJitter(GraphicsDevice device)
        {
            Random rng = new Random(0x4A17);
            Color[] px = new Color[64 * 64];
            byte[] u4 = new byte[4];
            for (int i = 0; i < px.Length; i++)
            {
                rng.NextBytes(u4);
                uint u = BitConverter.ToUInt32(u4, 0);
                float theta = ((float)u * 6.1037019e-05f - 1f) * 3.14159274f;
                float c = (float)Math.Cos(theta), s = (float)Math.Sin(theta);
                px[i] = new Color((byte)(int)((c + 1f) * 0.5f * 255f), (byte)(int)((s + 1f) * 0.5f * 255f), (byte)0, (byte)255);
            }
            Texture2D t = new Texture2D(device, 64, 64, false, SurfaceFormat.Color);
            t.SetData(px);
            return t;
        }

        public void Dispose()
        {
            Fx.Dispose(); Jitter.Dispose(); ShadowMap.Dispose();
        }
    }

    /// <summary>One avatar's batches on the GPU, drawn as the library draws them.</summary>
    sealed class LibraryAvatar : IDisposable
    {
        private sealed class Tex
        {
            public Texture2D[] Layers;          // one per animation frame (AnimatedTexture layers)
        }

        private sealed class Batch
        {
            public ShaderId Kind;
            public ShaderParameter[] Params;
            public Tex[] Textures;               // the component's, indexed by TextureIndex
            public VertexBuffer Vertices;
            public IndexBuffer Indices;
            public int Triangles;
            public float Transparency = float.NaN;   // PixelConstantTransparency.x, if present
        }

        private sealed class Mesh { public readonly List<Batch> Batches = new List<Batch>(); }

        private readonly LibraryShaders sh;
        private readonly GraphicsDevice device;
        private readonly List<Mesh> meshes = new List<Mesh>();
        private readonly List<Tex> ownedTextures = new List<Tex>();
        private readonly Joint[] joints;
        private readonly Matrix[] localSpace, inverseBind, vertexMatrices;
        private readonly Vector4[] palette = new Vector4[72 * 3];

        public static LibraryAvatar TryBuild(LibraryShaders sh, AvatarRenderer r)
        {
            try
            {
                Avatar a = r == null ? null : r.Avatar;
                if (a == null) { Console.WriteLine("[studio] library renderer: the avatar is not built"); return null; }
                return new LibraryAvatar(sh, a);
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] library renderer: could not build the avatar (" + e + ")");
                return null;
            }
        }

        private LibraryAvatar(LibraryShaders sh, Avatar a)
        {
            this.sh = sh;
            device = sh.Fx.GraphicsDevice;
            bool dump = Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBDUMP") == "1";
            foreach (AvatarComponent c in a.Models)
            {
                Mesh m = new Mesh();
                Tex[] textures = BuildTextures(c.GetTextures());
                for (int i = 0; i < c.TriangleBatches.Length; i++)
                    m.Batches.Add(BuildBatch(c.TriangleBatches[i], c.ShaderInstanceParameters[i], textures));
                meshes.Add(m);
                if (dump) Dump(c);
            }
            // the skin matrices, with an identity model matrix: the library's palette is model space
            joints = a.Skeleton.Joints;
            Bind(joints, out localSpace, out inverseBind);
            vertexMatrices = new Matrix[joints.Length];

            // the carryable: its own model on its own skeleton (up to 72 joints), its palette after the body's
            if (a.Carryable != null && a.Carryable.Model != null && a.Carryable.Skeleton != null && a.Carryable.Skeleton.Joints != null)
            {
                AvatarComponent c = a.Carryable.Model;
                carry = new Mesh();
                Tex[] textures = BuildTextures(c.GetTextures());
                for (int i = 0; i < c.TriangleBatches.Length; i++)
                    carry.Batches.Add(BuildBatch(c.TriangleBatches[i], c.ShaderInstanceParameters[i], textures));
                carryJoints = a.Carryable.Skeleton.Joints;
                Bind(carryJoints, out carryLocal, out carryInverseBind);
                carryMatrices = new Matrix[carryJoints.Length];
            }
        }

        /// <summary>A skeleton's local matrices and inverse binds.</summary>
        private static void Bind(Joint[] js, out Matrix[] local, out Matrix[] inverse)
        {
            int n = js.Length;
            local = new Matrix[n]; inverse = new Matrix[n];
            for (int i = 0; i < n; i++)
            {
                Matrix l = Matrix.CreateFromQuaternion(Q(js[i].Local.rotation)) * Matrix.CreateScale(V(js[i].Local.scale));
                l.M41 = js[i].Local.position.X; l.M42 = js[i].Local.position.Y; l.M43 = js[i].Local.position.Z;
                local[i] = l;
                Matrix b = Matrix.CreateFromQuaternion(Q(js[i].BindRotation));
                b.M41 = js[i].BindPosition.X; b.M42 = js[i].BindPosition.Y; b.M43 = js[i].BindPosition.Z;
                inverse[i] = Matrix.Invert(b);
            }
        }

        private readonly Mesh carry;
        private readonly Joint[] carryJoints;
        private readonly Matrix[] carryLocal, carryInverseBind, carryMatrices;
        private readonly Vector4[] carryPalette = new Vector4[72 * 3];
        private bool carryVisible;

        /// <summary>The avatar wears a carryable this renderer can draw.</summary>
        public bool HasCarryable { get { return carry != null; } }

        /// <summary>The carryable skeleton's joints (B's, from its STRB), 0 without one.</summary>
        public int CarryableJointCount { get { return carry == null ? 0 : carryJoints.Length; } }

        /// <summary>The carryable's pose this frame, one matrix per joint, or null.</summary>
        public void SetCarryablePose(IList<Matrix> bones)
        {
            carryVisible = false;
            if (carry == null || bones == null || bones.Count == 0) return;
            Matrix r = bones[0];
            carryVisible = r.M11 * r.M11 + r.M12 * r.M12 + r.M13 * r.M13 + r.M21 * r.M21 + r.M22 * r.M22 + r.M23 * r.M23
                         + r.M31 * r.M31 + r.M32 * r.M32 + r.M33 * r.M33 > 0f;
            int n = carryJoints.Length;
            for (int i = 0; i < n; i++)
            {
                Matrix m = (i < bones.Count ? bones[i] : new Matrix()) * carryLocal[i];
                carryMatrices[i] = i == 0 ? m : m * carryMatrices[carryJoints[i].Parent];
            }
            Array.Clear(carryPalette, 0, carryPalette.Length);
            for (int j = 0; j < n && j < 72; j++)
            {
                Matrix s = carryInverseBind[j] * carryMatrices[j];
                carryPalette[3 * j] = new Vector4(s.M11, s.M21, s.M31, s.M41);
                carryPalette[3 * j + 1] = new Vector4(s.M12, s.M22, s.M32, s.M42);
                carryPalette[3 * j + 2] = new Vector4(s.M13, s.M23, s.M33, s.M43);
            }
        }

        /// <summary>Whether the carryable is drawn this frame.</summary>
        public bool CarryableVisible { get { return carry != null && carryVisible; } }

        /// <summary>AVATAR_STUDIO_LIBDUMP=1: every batch's parameters and the component's textures.</summary>
        private static void Dump(AvatarComponent c)
        {
            AvatarTexture[] tx = c.GetTextures();
            Console.WriteLine("[libdump] component " + c.AssetId + ": " + c.TriangleBatches.Length + " batches, " + tx.Length + " textures");
            for (int i = 0; i < tx.Length; i++)
                Console.WriteLine("[libdump]   tex " + i + ": " + tx[i].Width + "x" + tx[i].Height + " layers " + tx[i].LayersCount
                    + " fmt " + tx[i].RawDataFormat);
            for (int b = 0; b < c.TriangleBatches.Length; b++)
            {
                ShaderInstance si = c.ShaderInstanceParameters[b];
                Console.WriteLine("[libdump]   batch " + b + ": " + si.ShaderId + ", " + c.TriangleBatches[b].Triangles.Length + " tris, "
                    + c.TriangleBatches[b].Vertices.TextureCoordinates.Length + " uv sets");
                foreach (ShaderParameter p in si.ShaderParameters)
                {
                    if (p.type == ShaderParameterType.Texture)
                        Console.WriteLine("[libdump]     " + p.usage + " tex " + p.data.Texture.TextureIndex + " reg/uv " + p.data.Texture.UvLayer + " wrap " + p.data.Texture.textureWrapMode);
                    else
                        Console.WriteLine("[libdump]     " + p.type + " " + p.usage + " (" + p.data.Constant.f0 + ", " + p.data.Constant.f1 + ", " + p.data.Constant.f2 + ", " + p.data.Constant.f3 + ")"
                            + " ints " + p.data.ConstantInt.i0.ToString("X8"));
                }
            }
        }

        private static Quaternion Q(Quaternion q) { return q; }
        private static Vector3 V(Vector3 v) { return v; }

        // ---- data ------------------------------------------------------------------------------

        private Tex[] BuildTextures(AvatarTexture[] src)
        {
            Tex[] t = new Tex[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                t[i] = BuildTexture(src[i]);
                ownedTextures.Add(t[i]);
            }
            return t;
        }

        /// <summary>Every layer of one of the component's textures (DXT1 / 3 / 5 uploaded as is).</summary>
        private Tex BuildTexture(AvatarTexture src)
        {
            int fmt = (int)src.RawDataFormat;
            Tex t = new Tex { Layers = new Texture2D[Math.Max(1, src.LayersCount)] };
            for (int k = 0; k < t.Layers.Length; k++)
            {
                SurfaceFormat sf;
                int w = src.Width, h = src.Height;
                switch (fmt)
                {
                    case 0: sf = SurfaceFormat.Dxt1; break;
                    case 2: sf = SurfaceFormat.Dxt3; break;
                    case 4: sf = SurfaceFormat.Dxt5; break;
                    case 8: sf = SurfaceFormat.Dxt1; w = h = 4; break;
                    case 10: sf = SurfaceFormat.Dxt3; w = h = 4; break;
                    case 12: sf = SurfaceFormat.Dxt5; w = h = 4; break;
                    case 13: sf = SurfaceFormat.Color; w = h = 4; break;
                    default: throw new NotSupportedException("texture data format " + fmt);
                }
                Texture2D tex = new Texture2D(device, w, h, false, sf);
                if (fmt == 13) tex.SetData(new Color[16]);
                else tex.SetData(src.GetTextureLayerRawData(k));
                t.Layers[k] = tex;
            }
            return t;
        }

        private Batch BuildBatch(TriangleBatch tb, ShaderInstance si, Tex[] textures)
        {
            VertexDataBuffer vb = tb.Vertices;
            int n = vb.RawPositions.Length;
            LibraryVertex[] v = new LibraryVertex[n];
            Vector2[][] uv = vb.TextureCoordinates;
            for (int i = 0; i < n; i++)
            {
                Vector3 p = vb.RawPositions[i], nn = vb.RawNormals[i];
                Vector4b bi = vb.SkinBindings[i], bw = vb.SkinWeights[i];
                Colorf c = vb.Color0[i];
                v[i].Position = new Vector3(p.X, p.Y, p.Z);
                v[i].Normal = new Vector3(nn.X, nn.Y, nn.Z);
                // UBYTE4N weights and UBYTE4 indices: byte / 255 and the byte, in the same order
                v[i].Weights = new Vector4(bw.X / 255f, bw.Y / 255f, bw.Z / 255f, bw.W / 255f);
                v[i].Indices = new Vector4(bi.X, bi.Y, bi.Z, bi.W);
                v[i].Color = new Vector4(c.red, c.green, c.blue, c.alpha);
                if (uv.Length > 0) v[i].T0 = new Vector2(uv[0][i].X, uv[0][i].Y);
                if (uv.Length > 1) v[i].T1 = new Vector2(uv[1][i].X, uv[1][i].Y);
                if (uv.Length > 2) v[i].T2 = new Vector2(uv[2][i].X, uv[2][i].Y);
                if (uv.Length > 3) v[i].T3 = new Vector2(uv[3][i].X, uv[3][i].Y);
                if (uv.Length > 4) v[i].T4 = new Vector2(uv[4][i].X, uv[4][i].Y);
                if (uv.Length > 5) v[i].T5 = new Vector2(uv[5][i].X, uv[5][i].Y);
            }
            short[] idx = new short[tb.Triangles.Length * 3];
            for (int i = 0; i < tb.Triangles.Length; i++)
            {
                idx[3 * i] = (short)tb.Triangles[i].i1;
                idx[3 * i + 1] = (short)tb.Triangles[i].i2;
                idx[3 * i + 2] = (short)tb.Triangles[i].i3;
            }
            Batch b = new Batch { Kind = si.ShaderId, Params = si.ShaderParameters, Textures = textures, Triangles = tb.Triangles.Length };
            b.Vertices = new VertexBuffer(device, LibraryVertex.Declaration, n, BufferUsage.WriteOnly);
            b.Vertices.SetData(v);
            b.Indices = new IndexBuffer(device, IndexElementSize.SixteenBits, idx.Length, BufferUsage.WriteOnly);
            b.Indices.SetData(idx);
            foreach (ShaderParameter p in b.Params)
                if (p.type == ShaderParameterType.PixelConstant && p.usage == ShaderParameterUsage.PixelConstantTransparency)
                    b.Transparency = p.data.Constant.f0;
            return b;
        }

        // ---- the palette -----------------------------------------------------------------------

        /// <summary>The bone palette for this frame: each bone's skin matrix, transposed, three rows (sub_9213D6B8).</summary>
        public void UpdatePalette(IList<Matrix> bones)
        {
            int n = joints.Length;
            if (bones == null || bones.Count != n) throw new ArgumentException("bone count " + (bones == null ? 0 : bones.Count) + ", skeleton " + n);
            for (int i = 0; i < n; i++)
            {
                Matrix m = bones[i] * localSpace[i];
                vertexMatrices[i] = i == 0 ? m : m * vertexMatrices[joints[i].Parent];
            }
            Array.Clear(palette, 0, palette.Length);
            for (int j = 0; j < n && j < 72; j++)
            {
                Matrix s = inverseBind[j] * vertexMatrices[j];
                palette[3 * j] = new Vector4(s.M11, s.M21, s.M31, s.M41);
                palette[3 * j + 1] = new Vector4(s.M12, s.M22, s.M32, s.M42);
                palette[3 * j + 2] = new Vector4(s.M13, s.M23, s.M33, s.M43);
            }
        }

        /// <summary>The skin matrix of bone j as last computed.</summary>
        public Matrix Skin(int j) { return inverseBind[j] * vertexMatrices[j]; }

        // ---- lights and the shadow camera ------------------------------------------------------

        // the renderer's lights (XAvRenderer_ctor, sub_92140B60); the editor never changes them
        private static readonly Vector4 LightColour1 = new Vector4(0.4f, 0.4f, 0.4f, 1f);          // 0x92018480
        private static readonly Vector4 Ambient = new Vector4(0.55f, 0.55f, 0.55f, 1f);            // 0x920184A0
        // 0x92018490 x (1, 1, -1, 1): the editor runs the library in coordinate system 0
        private static readonly Vector3 LightDirection1 = new Vector3(-0.5f, -0.6123f, 0.6123f);
        // XAv_SharedState_Init (0x9214BC38): the fill colours and directions (z kept in system 0)
        private static readonly Vector4 LightColour2 = new Vector4(0.2f, 0.1f, 0.05f, 1f);
        private static readonly Vector4 LightColour3 = new Vector4(0.1f, 0.1f, 0.2f, 1f);
        private static readonly Vector4 LightDirection2Raw = new Vector4(0.342f, 0f, 0.94f, 1f);
        private static readonly Vector4 LightDirection3Raw = new Vector4(1f, 0f, 0f, 1f);

        /// <summary>XAvShadow_LightMatrices (sub_9213E638) for coordinate system 0, in closed form.</summary>
        public static void ShadowCamera(Matrix recordWorld, Matrix view, Matrix proj, int size,
                                        out Matrix lightView, out Matrix lightProj, out Matrix cShadow)
        {
            ShadowCamera(recordWorld, view, proj, size, LightDirection1, out lightView, out lightProj, out cShadow);
        }

        /// <summary>The same with the renderer's light direction as set (the lineup turns it per avatar).</summary>
        public static void ShadowCamera(Matrix recordWorld, Matrix view, Matrix proj, int size, Vector3 lightDirection,
                                        out Matrix lightView, out Matrix lightProj, out Matrix cShadow)
        {
            Vector3 t = recordWorld.Translation;
            Vector3 eye = t - 5f * lightDirection;                                  // -5.0 at 0x92018590
            lightView = LookAtLH(eye, t, Vector3.Up);                               // up at 0x92001030
            Matrix wv = recordWorld * view;
            float near = float.MaxValue, far = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                // the avatar's box, (-1, 0, -1) at 0x92018580 to (1, 2, 1) at 0x92018570
                Vector3 c = new Vector3((i & 1) != 0 ? 1f : -1f, (i & 2) != 0 ? 2f : 0f, (i & 4) != 0 ? 1f : -1f);
                float z = Vector3.Transform(c, wv).Z;
                near = Math.Min(near, z); far = Math.Max(far, z);
            }
            // sub_9213D9F0: the view frustum's slice, into light space; fov = 2 atan(1 / proj._22)
            float fov = 2f * (float)Math.Atan(1.0 / proj.M22);
            float aspect = proj.M22 / proj.M11;
            float tn = (float)Math.Tan(0.5 * fov);                                  // 0.5 at 0x920186A4
            Matrix vl = Matrix.Invert(view) * lightView;
            Vector3 mn = new Vector3(float.MaxValue), mx = new Vector3(float.MinValue);
            float[] depths = { near, far };
            foreach (float d in depths)
            {
                for (int k = 0; k < 5; k++)
                {
                    Vector3 p = k == 4 ? new Vector3(0, 0, d)
                              : new Vector3(((k & 1) != 0 ? 1f : -1f) * tn * aspect * d, ((k & 2) != 0 ? 1f : -1f) * tn * d, d);
                    Vector3 q = Vector3.Transform(p, vl);
                    mn = Vector3.Min(mn, q); mx = Vector3.Max(mx, q);
                }
            }
            // snapped outwards to 0.1 (10.0 at 0x92001B28, 0.1 at 0x920010DC; floor / ceil)
            float l = (float)Math.Floor(mn.X * 10f) * 0.1f, b = (float)Math.Floor(mn.Y * 10f) * 0.1f;
            float r = (float)Math.Ceiling(mx.X * 10f) * 0.1f, tp = (float)Math.Ceiling(mx.Y * 10f) * 0.1f;
            lightProj = OrthoOffCenterLH(l, r, b, tp, 3f, mx.Z);                    // 3.0 at 0x92001B2C
            float bias = 0.5f + 0.5f / size;                                        // 0x92018540 / 0x92018550
            Matrix tex = new Matrix(0.5f, 0, 0, 0, 0, -0.5f, 0, 0, 0, 0, 1f, 0, bias, bias, 0, 1f);
            cShadow = vl * lightProj * tex;
        }

        private static Matrix LookAtLH(Vector3 eye, Vector3 at, Vector3 up)
        {
            Vector3 z = Vector3.Normalize(at - eye), x = Vector3.Normalize(Vector3.Cross(up, z)), y = Vector3.Cross(z, x);
            return new Matrix(x.X, y.X, z.X, 0, x.Y, y.Y, z.Y, 0, x.Z, y.Z, z.Z, 0,
                              -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1f);
        }

        private static Matrix OrthoOffCenterLH(float l, float r, float b, float t, float zn, float zf)
        {
            return new Matrix(2f / (r - l), 0, 0, 0, 0, 2f / (t - b), 0, 0, 0, 0, 1f / (zf - zn), 0,
                              (l + r) / (l - r), (t + b) / (b - t), zn / (zn - zf), 1f);
        }

        /// <summary>The console negates the avatar data's z for its left-handed mode; DataFlip does the same.</summary>
        private static readonly Matrix DataFlip = Matrix.CreateScale(1f, 1f, -1f);

        private static Vector4[] Rows(Matrix m)
        {
            return new[] { new Vector4(m.M11, m.M12, m.M13, m.M14), new Vector4(m.M21, m.M22, m.M23, m.M24),
                           new Vector4(m.M31, m.M32, m.M33, m.M34), new Vector4(m.M41, m.M42, m.M43, m.M44) };
        }

        private static Vector4 Normalize3(Vector4 v)
        {
            float len2 = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
            if (len2 == 0f) return v;                                               // vsel, 0x9214C45C
            return v * (1f / (float)Math.Sqrt(len2));
        }

        // ---- states ----------------------------------------------------------------------------

        private static readonly DepthStencilState DepthWrite = new DepthStencilState
        { DepthBufferEnable = true, DepthBufferWriteEnable = true, DepthBufferFunction = CompareFunction.LessEqual };
        private static readonly DepthStencilState DepthRead = new DepthStencilState
        { DepthBufferEnable = true, DepthBufferWriteEnable = false, DepthBufferFunction = CompareFunction.LessEqual };
        // XAv_DrawMeshTransparent (sub_9214DCF8): blend control 0x07010706
        private static readonly BlendState Transparent = new BlendState
        {
            ColorSourceBlend = Blend.SourceAlpha, ColorDestinationBlend = Blend.InverseSourceAlpha, ColorBlendFunction = BlendFunction.Add,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha, AlphaBlendFunction = BlendFunction.Add,
        };
        private static readonly RasterizerState CullCW = new RasterizerState { CullMode = CullMode.CullClockwiseFace };
        private static readonly RasterizerState CullCCW = new RasterizerState { CullMode = CullMode.CullCounterClockwiseFace };
        // s0..s8: linear mag / min / mip (XAv_SetupDraw sub_9214C1D8), addressing per texture
        private static readonly SamplerState[] Linear = new SamplerState[4];
        private static SamplerState LinearFor(int wrap)
        {
            if (Linear[wrap] == null)
                Linear[wrap] = new SamplerState
                {
                    Filter = TextureFilter.Linear,
                    AddressU = (wrap & 1) != 0 ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                    AddressV = (wrap & 2) != 0 ? TextureAddressMode.Wrap : TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Wrap,
                };
            return Linear[wrap];
        }

        // ---- drawing ---------------------------------------------------------------------------

        private Matrix world, view, proj, lightView, lightProj, cShadow;

        /// <summary>The key light and ambient as SetLighting last set them (defaults: XAv_DefaultLighting).</summary>
        public Vector4 KeyColour = LightColour1, AmbientColour = Ambient;
        public Vector3 KeyDirection = LightDirection1;
        public static Vector4 DefaultKeyColour { get { return LightColour1; } }
        public static Vector4 DefaultAmbient { get { return Ambient; } }
        public static Vector3 DefaultKeyDirection { get { return LightDirection1; } }

        /// <summary>A shadow map of this avatar's own, as every console renderer has.</summary>
        public void UseOwnShadowMap()
        {
            if (ownShadow == null)
                ownShadow = new RenderTarget2D(device, sh.ShadowSize, sh.ShadowSize, false, SurfaceFormat.Single, DepthFormat.Depth24);
        }
        private RenderTarget2D ownShadow;
        private RenderTarget2D ShadowTarget { get { return ownShadow ?? sh.ShadowMap; } }

        /// <summary>The shadow pass (XAvRenderer_RenderShadow sub_9213FAF0), before anything else this frame.</summary>
        public void ShadowPass(Matrix recordWorld, Matrix world, Matrix view, Matrix proj, IList<Matrix> bones)
        {
            this.world = world; this.view = view; this.proj = proj;
            UpdatePalette(bones);
            ShadowCamera(recordWorld, view, proj, sh.ShadowSize, KeyDirection, out lightView, out lightProj, out cShadow);

            RenderTargetBinding[] saved = device.GetRenderTargets();
            device.SetRenderTarget(ShadowTarget);
            // cleared to depth 1.0: sub_92122BA0 with f1 = 1.0 (flt_92000F94, 0x9213FC20)
            device.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, new Vector4(1f, 0f, 0f, 1f), 1f, 0);
            device.DepthStencilState = DepthWrite;
            device.BlendState = BlendState.Opaque;
            Matrix wl = world * lightView;
            // XAv_SetupShadowDraw (sub_9214D050): the opposite cull of the main pass (DataFlip)
            device.RasterizerState = (DataFlip * wl).Determinant() >= 0f ? CullCCW : CullCW;
            Effect fx = sh.Fx;
            fx.Parameters["c_modelview"].SetValue(Rows(wl));
            fx.Parameters["c_projection"].SetValue(Rows(lightProj));
            fx.Parameters["Palette"].SetValue(palette);
            foreach (Mesh m in meshes) ShadowMesh(m);
            // the carryable casts its shadow too, after the body
            if (CarryableVisible)
            {
                fx.Parameters["Palette"].SetValue(carryPalette);
                ShadowMesh(carry);
                fx.Parameters["Palette"].SetValue(palette);
            }
            device.SetRenderTargets(saved);
        }

        private void ShadowMesh(Mesh m)
        {
            Effect fx = sh.Fx;
            for (int i = m.Batches.Count - 1; i >= 0; i--)
            {
                Batch b = m.Batches[i];
                // transparent kinds cast a shadow only below 0.5 transparency (sub_9214D260)
                if ((b.Kind == ShaderId.BodyTransparent || b.Kind == ShaderId.BodyShinyTransparent) && !(b.Transparency < 0.5f))
                    continue;
                fx.CurrentTechnique = fx.Techniques[b.Kind == ShaderId.HeadOpaque ? "Shadow4" : "Shadow0"];
                fx.CurrentTechnique.Passes[0].Apply();
                DrawBatch(b);
            }
        }

        /// <summary>The main pass (XAvRenderer_Render sub_9213CAA8): opaque kinds 4, 0, 2, then transparent 1, 3.</summary>
        public void Draw(AvatarExpression expression) { Draw(expression, view, false); }

        /// <summary>The mirror's draw (AE_DrawAvatarReflected sub_920C5580): this frame's state, a reflected view.</summary>
        public void DrawReflected(AvatarExpression expression, Matrix reflectedView) { Draw(expression, reflectedView, true); }

        private static readonly DepthStencilState DepthWriteStencil = new DepthStencilState
        {
            DepthBufferEnable = true, DepthBufferWriteEnable = true, DepthBufferFunction = CompareFunction.LessEqual,
            StencilEnable = true, StencilFunction = CompareFunction.LessEqual, ReferenceStencil = 255,
            StencilPass = StencilOperation.Keep, StencilFail = StencilOperation.Keep, StencilDepthBufferFail = StencilOperation.Keep,
        };
        private static readonly DepthStencilState DepthReadStencil = new DepthStencilState
        {
            DepthBufferEnable = true, DepthBufferWriteEnable = false, DepthBufferFunction = CompareFunction.LessEqual,
            StencilEnable = true, StencilFunction = CompareFunction.LessEqual, ReferenceStencil = 255,
            StencilPass = StencilOperation.Keep, StencilFail = StencilOperation.Keep, StencilDepthBufferFail = StencilOperation.Keep,
        };

        private void Draw(AvatarExpression expression, Matrix view, bool stencil)
        {
            Effect fx = sh.Fx;
            Matrix wv = world * view;
            fx.Parameters["c_modelview"].SetValue(Rows(wv));
            fx.Parameters["c_projection"].SetValue(Rows(proj));
            fx.Parameters["c_shadow"].SetValue(Rows(cShadow));
            fx.Parameters["Palette"].SetValue(palette);
            // light 1 negated, turned by the view and normalised; the fills only normalised (sub_9214C1D8)
            Vector3 d1 = Vector3.TransformNormal(-KeyDirection, view);
            fx.Parameters["lightColour1"].SetValue(KeyColour);
            fx.Parameters["lightDirection1"].SetValue(Normalize3(new Vector4(d1, 0f)));
            fx.Parameters["lightColour2"].SetValue(LightColour2);
            fx.Parameters["lightDirection2"].SetValue(Normalize3(LightDirection2Raw));
            fx.Parameters["lightColour3"].SetValue(LightColour3);
            fx.Parameters["lightDirection3"].SetValue(Normalize3(LightDirection3Raw));
            fx.Parameters["ambientColour"].SetValue(AmbientColour);
            // the shadowed setup (sub_9214C888): 1 / size, 1 / 64, strength 1 so c7.x = 0 ([ours] c7.yzw 0)
            float inv = 1f / sh.ShadowSize;
            fx.Parameters["shadowSizes"].SetValue(new Vector4(inv, inv, 0.015625f, 0.015625f));
            fx.Parameters["oneMinusShadowStrength"].SetValue(new Vector4(1f - Math.Min(1f, 1f), 0f, 0f, 0f));
            fx.Parameters["enableShadows"].SetValue(true);

            RasterizerState cull = (DataFlip * wv).Determinant() >= 0f ? CullCW : CullCCW;   // sub_92113F30, 2 / 6; DataFlip
            int[] layers = FaceLayers(expression);

            device.RasterizerState = cull;
            device.DepthStencilState = stencil ? DepthWriteStencil : DepthWrite;
            device.BlendState = BlendState.Opaque;
            ShaderId[] opaque = { ShaderId.HeadOpaque, ShaderId.BodyOpaque, ShaderId.BodyShinyOpaque };   // XAv_DrawMeshOpaque
            foreach (Mesh m in meshes)
                foreach (ShaderId k in opaque) DrawKind(m, k, layers);
            // the body's components, then the carryable, opaque, then the same order transparent
            if (CarryableVisible) DrawCarryable(opaque, layers);
            device.DepthStencilState = stencil ? DepthReadStencil : DepthRead;
            device.BlendState = Transparent;
            ShaderId[] transparent = { ShaderId.BodyTransparent, ShaderId.BodyShinyTransparent };        // XAv_DrawMeshTransparent
            foreach (Mesh m in meshes)
                foreach (ShaderId k in transparent) DrawKind(m, k, layers);
            if (CarryableVisible) DrawCarryable(transparent, layers);
            for (int s = 0; s < 16; s++) device.Textures[s] = null;
        }

        private void DrawCarryable(ShaderId[] kinds, int[] layers)
        {
            Effect fx = sh.Fx;
            fx.Parameters["Palette"].SetValue(carryPalette);
            foreach (ShaderId k in kinds) DrawKind(carry, k, layers);
            fx.Parameters["Palette"].SetValue(palette);
        }

        private void DrawKind(Mesh m, ShaderId kind, int[] layers)
        {
            Effect fx = sh.Fx;
            for (int i = m.Batches.Count - 1; i >= 0; i--)
            {
                Batch b = m.Batches[i];
                if (b.Kind != kind) continue;
                fx.CurrentTechnique = fx.Techniques["Kind" + (int)kind];
                SetConstants(b);
                fx.CurrentTechnique.Passes[0].Apply();
                BindTextures(b, layers);
                device.Textures[14] = sh.Jitter;
                device.SamplerStates[14] = SamplerState.PointWrap;
                device.Textures[15] = ShadowTarget;
                device.SamplerStates[15] = SamplerState.PointWrap;
                DrawBatch(b);
                for (int s = 0; s < 7; s++) device.Textures[s] = null;             // sub_9214D768
            }
        }

        private void DrawBatch(Batch b)
        {
            device.SetVertexBuffer(b.Vertices);
            device.Indices = b.Indices;
            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, b.Vertices.VertexCount, 0, b.Triangles);
        }

        /// <summary>The pixel constants a batch carries, by usage, into the library's registers (sub_9214A6D8).</summary>
        private static readonly Dictionary<ShaderParameterUsage, string> PixelNames = new Dictionary<ShaderParameterUsage, string>
        {
            { ShaderParameterUsage.PixelConstantColorSkin, "SkinTone" },                // c215
            { ShaderParameterUsage.PixelConstantColorMouth, "MouthTone" },              // c217
            { ShaderParameterUsage.PixelConstantColorIris, "IrisTone" },                // c218
            { ShaderParameterUsage.PixelConstantColorEyebrow, "EyeBrowTone" },          // c219
            { ShaderParameterUsage.PixelConstantColorEyeShadow, "EyeShadowTone" },      // c220
            { ShaderParameterUsage.PixelConstantColorFacialHair, "FacialHair" },        // c221
            { ShaderParameterUsage.PixelConstantColorSkinFeature1, "SkinFeature1Tone" },// c222
            { ShaderParameterUsage.PixelConstantColorSkinFeature2, "SkinFeature2Tone" },// c223
            { ShaderParameterUsage.PixelConstantColorCustom0, "CustomColor1" },         // c210
            { ShaderParameterUsage.PixelConstantColorCustom1, "CustomColor2" },         // c211
            { ShaderParameterUsage.PixelConstantColorCustom2, "CustomColor3" },         // c212
            { ShaderParameterUsage.PixelConstantReflectivity, "Reflectivity" },         // c214
            { ShaderParameterUsage.PixelConstantTransparency, "Transparency" },         // c213
            { ShaderParameterUsage.PixelConstantColorRimLight, "rimLightColour" },      // c5
        };

        private void SetConstants(Batch b)
        {
            foreach (ShaderParameter p in b.Params)
            {
                if (p.type != ShaderParameterType.PixelConstant) continue;
                string name;
                if (!PixelNames.TryGetValue(p.usage, out name)) continue;
                EffectParameter ep = sh.Fx.Parameters[name];
                if (ep != null) ep.SetValue(new Vector4(p.data.Constant.f0, p.data.Constant.f1, p.data.Constant.f2, p.data.Constant.f3));
            }
        }

        /// <summary>A batch's textures (XAv_SetBatchParams sub_9214D470): sampler by register, layer by frame.</summary>
        private void BindTextures(Batch b, int[] layers)
        {
            foreach (ShaderParameter p in b.Params)
            {
                if (p.type != ShaderParameterType.Texture) continue;
                int reg = p.data.Texture.UvLayer;
                if (reg == -1)
                {
                    if (p.usage == ShaderParameterUsage.TextureReflection && (b.Kind == ShaderId.BodyShinyOpaque || b.Kind == ShaderId.BodyShinyTransparent)) reg = 2;
                    else continue;
                }
                int ti = p.data.Texture.TextureIndex;
                if (ti < 0 || ti >= b.Textures.Length || reg < 0 || reg > 8) continue;
                Tex t = b.Textures[ti];
                int frame = 0;
                int li = FaceSlot(p.usage);
                if (li >= 0) { frame = layers[li]; if (frame < 0 || frame >= t.Layers.Length) frame = 0; }
                device.Textures[reg] = t.Layers[frame];
                device.SamplerStates[reg] = LinearFor((int)p.data.Texture.textureWrapMode & 3);
            }
        }

        private static int FaceSlot(ShaderParameterUsage u)
        {
            switch (u)
            {
                case ShaderParameterUsage.TextureMouth: return 0;
                case ShaderParameterUsage.TextureEyebrowLeft: return 1;
                case ShaderParameterUsage.TextureEyebrowRight: return 2;
                case ShaderParameterUsage.TextureEyeLeft: return 3;
                case ShaderParameterUsage.TextureEyeRight: return 4;
                default: return -1;
            }
        }

        /// <summary>Frames for mouth, eyebrows and eyes from the expression; the left eye's look swapped.</summary>
        private static int[] FaceLayers(AvatarExpression e)
        {
            int leftEye = (int)e.LeftEye;
            if (e.LeftEye == AvatarEye.LookLeft) leftEye = (int)AvatarEye.LookRight;
            else if (e.LeftEye == AvatarEye.LookRight) leftEye = (int)AvatarEye.LookLeft;
            return new[] { (int)e.Mouth, (int)e.LeftEyebrow, (int)e.RightEyebrow, leftEye, (int)e.RightEye };
        }

        public void Dispose()
        {
            foreach (Mesh m in meshes)
                foreach (Batch b in m.Batches) { b.Vertices.Dispose(); b.Indices.Dispose(); }
            if (carry != null)
                foreach (Batch b in carry.Batches) { b.Vertices.Dispose(); b.Indices.Dispose(); }
            foreach (Tex t in ownedTextures)
                foreach (Texture2D l in t.Layers) l.Dispose();
            meshes.Clear(); ownedTextures.Clear();
            if (ownShadow != null) { ownShadow.Dispose(); ownShadow = null; }
        }
    }
}
