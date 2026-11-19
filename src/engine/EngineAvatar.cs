// Builds an avatar from its manifest: the models the renderer draws, the scaled skeleton, the carryable.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;

namespace AvatarStudio.Avatars
{
    public sealed class AvatarCarryable
    {
        public Skeleton Skeleton;
        public AvatarComponent Model = new AvatarComponent();
        public KeyframeAnimation Animation;
    }

    public sealed class Avatar
    {
        public Skeleton Skeleton;
        public Skeleton.SkeletonVersion SkeletonVersion;
        public readonly List<AvatarComponent> Models = new List<AvatarComponent>();
        public AvatarCarryable Carryable;
        public AvatarManifest Manifest;
    }

    /// <summary>Where assets come from: the catalogue's copies first, then the folders.</summary>
    public static class AssetStore
    {
        public static Func<Guid, byte[]> Source;
        public static byte[] Get(Guid g) { return Source == null || g == Guid.Empty ? null : Source(g); }
    }

    public static class AvatarBuilder
    {
        private enum Kind { Model, Carryable, Texture, BlendShape, Override }

        private struct Constant { public ShaderParameterUsage Usage; public Vector4 Value; }

        private sealed class Request
        {
            public Kind Kind;
            public Guid AssetId;
            public ComponentInfo Info;
            public AvatarComponentMasks Mask;
            public Constant[] Constants = new Constant[0];
            public int Detail;                          // the texture or shape type
            public byte[] Data;
            public StrbFile File;
            public AssetMetadata Meta;
            public bool HasOverrides;
        }

        // the head's colour constants: the manifest colour each takes (DynamicColorType order)
        private static readonly ShaderParameterUsage[] ColourUses =
        {
            ShaderParameterUsage.PixelConstantColorSkin, ShaderParameterUsage.PixelConstantColorHair, ShaderParameterUsage.PixelConstantColorMouth,
            ShaderParameterUsage.PixelConstantColorIris, ShaderParameterUsage.PixelConstantColorEyebrow, ShaderParameterUsage.PixelConstantColorEyeShadow,
            ShaderParameterUsage.PixelConstantColorFacialHair, ShaderParameterUsage.PixelConstantColorSkinFeature1, ShaderParameterUsage.PixelConstantColorSkinFeature2,
        };

        // a texture kind's slots (DynamicTextureType order, three each; None matches parameters of no use)
        private static readonly ShaderParameterUsage[] TextureUses =
        {
            ShaderParameterUsage.TextureMouth, ShaderParameterUsage.None, ShaderParameterUsage.None,
            ShaderParameterUsage.TextureEyeLeft, ShaderParameterUsage.TextureEyeRight, ShaderParameterUsage.None,
            ShaderParameterUsage.TextureEyebrowLeft, ShaderParameterUsage.TextureEyebrowRight, ShaderParameterUsage.None,
            ShaderParameterUsage.TextureFacialHair, ShaderParameterUsage.None, ShaderParameterUsage.None,
            ShaderParameterUsage.TextureEyeShadow, ShaderParameterUsage.None, ShaderParameterUsage.None,
            ShaderParameterUsage.TextureSkinFeatures, ShaderParameterUsage.None, ShaderParameterUsage.None,
        };

        /// <summary>A colour as a shader constant: each channel / 255.</summary>
        public static Vector4 ToVector(Colorb c) { return new Vector4(c.red * 0.003921569f, c.green * 0.003921569f, c.blue * 0.003921569f, c.alpha * 0.003921569f); }

        /// <summary>The avatar the manifest describes, `mask` limiting what loads; throws when it cannot be built.</summary>
        public static Avatar Build(AvatarManifest manifest, AvatarComponentMasks mask = AvatarComponentMasks.All, bool allowAlternatives = true)
        {
            AvatarManifest m = manifest.Clone();
            Avatar target = new Avatar { Manifest = m };
            if (m.DressDefaultClothes)
            {
                m.RemoveComponents((AvatarComponentMasks)(short)~(int)mask);
                Dress(m, mask);
            }
            else m.ClearComponents((AvatarComponentMasks)(short)~(int)mask);
            List<Request> reqs = Requests(m, mask);
            AvatarComponentMasks seen = AvatarComponentMasks.None;
            foreach (Request r in reqs)
            {
                if ((r.Mask & seen) != 0) throw new InvalidDataException("the manifest's components overlap");
                seen |= r.Mask;
            }
            Skeleton.SkeletonVersion version = Skeleton.SkeletonVersion.Invalid;
            AvatarGender body = m.BodyType;
            while (true)
            {
                bool failed = false;
                foreach (Request r in reqs)
                {
                    if (r.Data == null)
                    {
                        r.Data = AssetStore.Get(r.AssetId);
                        r.File = r.Data == null ? null : StrbFile.Read(r.Data);
                        r.Meta = r.File == null || r.File.Namespace != StrbFile.AvatarNamespace ? null : AssetMetadata.Read(r.File);
                    }
                    if (version == Skeleton.SkeletonVersion.Invalid && r.Meta != null) version = r.Meta.SkeletonVersion;
                    if (r.File != null && Valid(r, version, body)) continue;
                    if (m.IsCoreAsset(r.AssetId)) throw new InvalidDataException("a core asset could not be loaded: " + r.AssetId);
                    if (!m.DressDefaultClothes || !allowAlternatives) throw new InvalidDataException("an asset could not be loaded: " + r.AssetId);
                    m.RemoveAsset(r.AssetId);
                    failed = true;
                }
                if (!failed) break;
                Dress(m, mask);
                reqs = Requests(m, mask);
            }
            AvatarComponentMasks worn = m.CombinedMaskWithin(mask);
            foreach (Request r in reqs)
            {
                if (r.Kind == Kind.Model) target.Models.Add(Component(r, worn));
                else if (r.Kind == Kind.Carryable) target.Carryable = Carryable(r);
            }
            foreach (Request r in reqs)
            {
                if (r.Kind == Kind.Texture) ApplyTexture(r, target);
                else if (r.Kind == Kind.BlendShape) ApplyOverrides(r.File, target);
            }
            foreach (Request r in reqs)
                if (r.Kind == Kind.Override || ((r.Kind == Kind.Model || r.Kind == Kind.Carryable) && r.HasOverrides)) ApplyOverrides(r.File, target);
            target.SkeletonVersion = version;
            target.Skeleton = Skeleton.Body(version);
            if (target.Skeleton == null) throw new InvalidDataException("the body skeleton is not installed (the avatar tables)");
            SkeletonScaling.Apply(version, body, m.WidthFactor, m.HeightFactor, target.Skeleton);
            if (target.Carryable != null)
            {
                Vector3 s = target.Skeleton.Joints[0].Local.scale;
                target.Carryable.Skeleton.Joints[0].Local.scale = s;
                target.Carryable.Skeleton.Joints[0].Local.position.X *= s.X;
                target.Carryable.Skeleton.Joints[0].Local.position.Y *= s.Y;
                target.Carryable.Skeleton.Joints[0].Local.position.Z *= s.Z;
            }
            target.Models.RemoveAll(c => { int n = 0; foreach (TriangleBatch b in c.TriangleBatches) n += b.Triangles.Length; return n == 0; });
            return target;
        }

        private static void Dress(AvatarManifest m, AvatarComponentMasks mask)
        {
            if (!m.ReplaceMissingComponents(true)) throw new InvalidDataException("the manifest's body is unknown");
            if (!m.RequiredComponentsPresent) throw new InvalidDataException("the manifest lacks a required component");
            if ((mask & AvatarComponentMasks.Body) != 0 && m.BodyComponent.Info.ComponentMask != AvatarComponentMasks.Body) throw new InvalidDataException("the manifest's body record is wrong");
            if ((mask & AvatarComponentMasks.Head) != 0)
            {
                if (m.HeadComponent.Info.ComponentMask != AvatarComponentMasks.Head) throw new InvalidDataException("the manifest's head record is wrong");
                if (!m.RequiredBlendShapesPresent) throw new InvalidDataException("the manifest lacks a blend shape");
                if (!m.RequiredTexturesPresent) throw new InvalidDataException("the manifest lacks a face texture");
            }
        }

        private static Constant C(ShaderParameterUsage u, Vector4 v) { return new Constant { Usage = u, Value = v }; }

        private static List<Request> Requests(AvatarManifest m, AvatarComponentMasks mask)
        {
            m.UpdateDependencies();
            List<Request> l = new List<Request>();
            Colorb skinColour = m.GetDynamicColor(DynamicColorType.Skin);
            Vector4 skin = ToVector(skinColour);
            if ((mask & m.BodyComponent.Info.ComponentMask) != 0)
                l.Add(new Request
                {
                    Kind = Kind.Model, AssetId = m.BodyComponent.Info.AssetId, Info = m.BodyComponent.Info, Mask = m.BodyComponent.Info.ComponentMask,
                    Constants = new[] { C(ShaderParameterUsage.PixelConstantColorCustom0, skin), Rim(skinColour, true) },
                });
            if ((mask & m.HeadComponent.Info.ComponentMask) != 0)
            {
                Constant[] k = new Constant[10];
                k[0] = Rim(skinColour, true);
                for (int i = 0; i < 9; i++) k[i + 1] = C(ColourUses[i], ToVector(m.GetDynamicColor((DynamicColorType)i)));
                l.Add(new Request { Kind = Kind.Model, AssetId = m.HeadComponent.Info.AssetId, Info = m.HeadComponent.Info, Mask = m.HeadComponent.Info.ComponentMask, Constants = k });
                for (int i = 0; i < 6; i++)
                {
                    Guid t = m.GetReplacementTexture((DynamicTextureType)i).TextureAssetId;
                    if (t != Guid.Empty) l.Add(new Request { Kind = Kind.Texture, AssetId = t, Detail = i });
                }
                for (int i = 0; i < 3; i++)
                {
                    Guid s = m.GetBlendShape((BlendShapeType)i);
                    if (s != Guid.Empty) l.Add(new Request { Kind = Kind.BlendShape, AssetId = s, Detail = i });
                }
                if (m.HeadComponent.OverrideAsset != Guid.Empty) l.Add(new Request { Kind = Kind.Override, AssetId = m.HeadComponent.OverrideAsset });
            }
            Vector4 hair = ToVector(m.GetDynamicColor(DynamicColorType.Hair));
            foreach (ComponentDescription d in m.Components)
            {
                if ((d.Info.ComponentMask & mask) == 0) continue;
                Request r = new Request { Kind = d.Info.ComponentMask == AvatarComponentMasks.Carryable ? Kind.Carryable : Kind.Model, AssetId = d.Info.AssetId, Info = d.Info, Mask = d.Info.ComponentMask };
                Constant c0;
                if (d.Info.ComponentMask == AvatarComponentMasks.Hair)
                {
                    c0 = C(ShaderParameterUsage.PixelConstantColorCustom0, hair);
                    if (d.OverrideAsset != Guid.Empty && (m.CombinedMask & AvatarComponentMasks.Hat) != 0) { r.AssetId = d.OverrideAsset; r.Info.AssetId = d.OverrideAsset; }
                }
                else
                {
                    if (d.OverrideAsset != Guid.Empty) l.Add(new Request { Kind = Kind.Override, AssetId = d.OverrideAsset });
                    c0 = C(ShaderParameterUsage.PixelConstantColorCustom0, ToVector(d.Info.CustomColor0));
                }
                r.Constants = new[]
                {
                    c0, C(ShaderParameterUsage.PixelConstantColorCustom1, ToVector(d.Info.CustomColor1)),
                    C(ShaderParameterUsage.PixelConstantColorCustom2, ToVector(d.Info.CustomColor2)), Rim(skinColour, false),
                };
                l.Add(r);
            }
            return l;
        }

        // XAM's rim colours (18 float4, then the default), chosen as XAM chooses them (sub_81968D88):
        // the key colour nearest the skin colour picks the rim.
        private static KeyValuePair<uint, uint>[] rimRecords;

        /// <summary>The rim-light constant, or the asset's own when the tables are missing.</summary>
        private static Constant Rim(Colorb skin, bool bySkin)
        {
            uint at = 0x816330C0;
            if (bySkin)
            {
                KeyValuePair<uint, uint>[] recs = rimRecords ?? (rimRecords = ConsoleTables.RimLookup());
                if (recs == null) return new Constant { Usage = ShaderParameterUsage.None };
                int best = 0x7FFFFFFF;
                foreach (KeyValuePair<uint, uint> r in recs)
                {
                    int dr = (int)((r.Key >> 16) & 0xFF) - skin.red, dg = (int)((r.Key >> 8) & 0xFF) - skin.green, db = (int)(r.Key & 0xFF) - skin.blue;
                    int d = dr * dr + db * db + dg * dg;                     // sub_81967E08's sum: R, then B, then G
                    if (d < best) { best = d; at = r.Value; }                 // strictly nearer: the first of equals stays
                }
            }
            Vector4? v = ConsoleTables.Vector4("rim-light", 0x81632FA0, at);
            return v == null ? new Constant { Usage = ShaderParameterUsage.None } : C(ShaderParameterUsage.PixelConstantColorRimLight, v.Value);
        }

        /// <summary>Forgets the rim records after the setup installs new tables.</summary>
        public static void ResetTables() { rimRecords = null; }

        private static bool Valid(Request r, Skeleton.SkeletonVersion version, AvatarGender body)
        {
            AssetMetadata a = r.Meta;
            if (a == null) return false;
            switch (r.Kind)
            {
                case Kind.Model:
                case Kind.Carryable:
                    if ((a.BodyTypeMask & body) == 0 || a.AssetType != BinaryAssetType.Component) return false;
                    if (r.Mask != 0 && (AvatarComponentMasks)(short)(a.AssetTypeDetails & ~0x800000u) != r.Mask) return false;
                    return a.SkeletonVersion == version;
                case Kind.Texture:
                    return (a.BodyTypeMask & body) != 0 && a.AssetType == BinaryAssetType.Texture && a.AssetTypeDetails == (uint)r.Detail;
                case Kind.BlendShape:
                    return (a.BodyTypeMask & body) != 0 && a.AssetType == BinaryAssetType.ShapeOverride && a.AssetTypeDetails == (uint)r.Detail && a.SkeletonVersion == version;
                default:
                    return a.SkeletonVersion == version;
            }
        }

        // ---- components ----------------------------------------------------------------------------

        private static AvatarComponent Component(Request r, AvatarComponentMasks worn)
        {
            List<StrbBlock> models = r.File.All(StrbBlockId.Model);
            if (models.Count == 0) throw new InvalidDataException(r.AssetId + " has no model");
            int index = 0;
            if ((worn & AvatarComponentMasks.Hat) != 0 && r.Info.ComponentMask == AvatarComponentMasks.Hair && models.Count > 1) index = 1;
            AvatarComponent c = new AvatarComponent { Manifest = new ComponentManifest(r.Info, index) };
            Model(r, models[index], c);
            r.HasOverrides = r.File.Has(StrbBlockId.ShapeOverrides);
            return c;
        }

        private static AvatarCarryable Carryable(Request r)
        {
            List<StrbBlock> models = r.File.All(StrbBlockId.Model);
            if (models.Count == 0) throw new InvalidDataException(r.AssetId + " has no model");
            AvatarCarryable k = new AvatarCarryable();
            k.Model.Manifest = new ComponentManifest(r.Info, 0);
            Model(r, models[0], k.Model);
            List<StrbBlock> skel = r.File.All(StrbBlockId.Skeleton);
            if (skel.Count == 0 || skel[0].Length <= 0) throw new InvalidDataException(r.AssetId + " has no skeleton");
            k.Skeleton = Skeleton.Parse(new ByteCursor(r.File.Inflate(skel[0]), true));
            List<StrbBlock> anim = r.File.All(StrbBlockId.Animation);
            if (anim.Count > 0) k.Animation = KeyframeAnimation.Parse(new ByteCursor(r.File.Data, true) { At = anim[0].Offset }, AvatarGender.Both);
            r.HasOverrides = r.File.Has(StrbBlockId.ShapeOverrides);
            return k;
        }

        private static void Model(Request r, StrbBlock block, AvatarComponent c)
        {
            if (block.Length <= 0) throw new InvalidDataException("an empty model block");
            ModelParser.Parse(r.File.Inflate(block), c);
            Vector4[] custom = new Vector4[3];
            for (int i = r.Constants.Length - 1; i >= 0; i--)
            {
                Constant k = r.Constants[i];
                ShaderParameterData d = new ShaderParameterData();
                d.Constant.SetValue(k.Value);
                Override(c, k.Usage, d);
                if (k.Usage >= ShaderParameterUsage.PixelConstantColorCustom0 && k.Usage <= ShaderParameterUsage.PixelConstantColorCustom2)
                    custom[(int)k.Usage - (int)ShaderParameterUsage.PixelConstantColorCustom0] = k.Value;
            }
            List<StrbBlock> table = r.File.All(StrbBlockId.CustomColorTable);
            if (table.Count > 0) ColourTable(c, custom, ReadColourTable(r.File, table[0]));
        }

        private static void Override(AvatarComponent c, ShaderParameterUsage u, ShaderParameterData d)
        {
            if (u == ShaderParameterUsage.None) return;
            for (int b = c.TriangleBatches.Length - 1; b >= 0; b--)
            {
                ShaderParameter[] p = c.ShaderInstanceParameters[b].ShaderParameters;
                for (int i = p.Length - 1; i >= 0; i--) if (p[i].usage == u) p[i].data = d;
            }
        }

        /// <summary>A colour table (block 7): a count, three integer unpackers, then ARGB sets.</summary>
        private static Vector4[][] ReadColourTable(StrbFile f, StrbBlock k)
        {
            BitReader r = new BitReader(new ByteCursor(f.Data, true) { At = k.Offset });
            int n = r.I(32);
            IntUnpacker a = new IntUnpacker(), b = new IntUnpacker(), c = new IntUnpacker();
            a.Header(r); b.Header(r); c.Header(r);
            Vector4[][] t = new Vector4[n][];
            for (int i = 0; i < n; i++) t[i] = new[] { FromArgb(a.Next(r)), FromArgb(b.Next(r)), FromArgb(c.Next(r)) };
            return t;
        }

        private static Vector4 FromArgb(int v)
        {
            return new Vector4((float)((v >> 16) & 0xFF) / 255f, (float)((v >> 8) & 0xFF) / 255f, (float)(v & 0xFF) / 255f, (float)((v >> 24) & 0xFF) / 255f);
        }

        /// <summary>When the colours match none of the table's sets, set 0's go on.</summary>
        private static void ColourTable(AvatarComponent c, Vector4[] custom, Vector4[][] table)
        {
            for (int i = table.Length - 1; i >= 0; i--)
                if (Distance(custom[0], table[i][0]) + Distance(custom[1], table[i][1]) + Distance(custom[2], table[i][2]) < 1E-05f) return;
            for (int k = 0; k < 3; k++)
            {
                ShaderParameterData d = new ShaderParameterData();
                d.Constant.SetValue(table[0][k]);
                Override(c, ShaderParameterUsage.PixelConstantColorCustom0 + k, d);
            }
        }

        private static float Distance(Vector4 a, Vector4 b)
        {
            float x = a.X - b.X, y = a.Y - b.Y, z = a.Z - b.Z, w = a.W - b.W;
            return x * x + y * y + z * z + w * w;
        }

        // ---- textures and overrides -------------------------------------------------------------------

        private static void ApplyTexture(Request r, Avatar target)
        {
            List<StrbBlock> blocks = r.File.All(StrbBlockId.Texture);
            if (blocks.Count == 0 || blocks[0].Length <= 0) throw new InvalidDataException(r.AssetId + " has no texture");
            AvatarTexture tex = null;
            for (int m = target.Models.Count - 1; m >= 0; m--)
            {
                AvatarComponent c = target.Models[m];
                bool[] taken = new bool[18];
                for (int i = 0; i < 3; i++)
                {
                    ShaderParameterUsage u = TextureUses[r.Detail * 3 + i];
                    for (int b = c.TriangleBatches.Length - 1; b >= 0; b--)
                    {
                        ShaderParameter[] p = c.ShaderInstanceParameters[b].ShaderParameters;
                        for (int k = p.Length - 1; k >= 0; k--)
                        {
                            if (p[k].usage != u) continue;
                            int slot = p[k].data.Texture.TextureIndex;
                            if (taken[slot]) continue;
                            taken[slot] = true;
                            if (tex == null) tex = ModelParser.ParseTexture(new ByteCursor(r.File.Inflate(blocks[0]), true));
                            c.Textures[slot] = tex;
                        }
                    }
                }
            }
        }

        /// <summary>Every override block of a file, each on the model it names.</summary>
        private static void ApplyOverrides(StrbFile f, Avatar target)
        {
            foreach (StrbBlock k in f.All(StrbBlockId.ShapeOverrides))
            {
                ByteCursor c = new ByteCursor(f.Data, true) { At = k.Offset };
                TriangleOverride t = TriangleOverride.Read(c);
                VertexOverride v = VertexOverride.Read(c);
                for (int m = target.Models.Count - 1; m >= 0; m--)
                {
                    AvatarComponent model = target.Models[m];
                    if (model.AssetId != t.Target) continue;
                    if (model.AssetId != v.Target) throw new InvalidDataException("an override names two models");
                    int tri = 0, vert = 0;
                    foreach (TriangleBatch b in model.TriangleBatches)
                    {
                        tri += b.Triangles.Length * 6;
                        vert += b.Vertices.RawPositions.Length * (28 + b.Vertices.TextureCoordinates.Length * 4);
                    }
                    vert = (vert + 127) & -128;
                    if (tri != t.IndexBufferSize || vert != v.VertexBufferSize) throw new InvalidDataException("an override does not fit its model");
                    t.Apply(model);
                    v.Apply(model);
                }
            }
        }

        /// <summary>{count, index buffer size, the model's GUID, an unpacker, the triangles to flatten}</summary>
        private sealed class TriangleOverride
        {
            public int IndexBufferSize;
            public Guid Target;
            public int[] Triangles;

            public static TriangleOverride Read(ByteCursor c)
            {
                BitReader r = new BitReader(c);
                uint n = r.U(32);
                if (n > 8192) throw new InvalidDataException("a triangle override over 8192");
                TriangleOverride t = new TriangleOverride { IndexBufferSize = r.I(32) };
                byte[] g = new byte[16];
                for (int i = 0; i < 16; i++) g[i] = (byte)r.U(8);
                t.Target = new Guid(g);
                IntUnpacker u = new IntUnpacker();
                u.Header(r);
                t.Triangles = new int[n];
                for (int i = 0; i < n; i++) t.Triangles[i] = u.Next(r);
                return t;
            }

            public void Apply(AvatarComponent m)
            {
                int[] start = new int[m.TriangleBatches.Length + 1];
                for (int i = 0; i < m.TriangleBatches.Length; i++) start[i + 1] = start[i] + m.TriangleBatches[i].Triangles.Length;
                foreach (int g in Triangles)
                    for (int b = 1; b <= m.TriangleBatches.Length; b++)
                    {
                        if (g >= start[b]) continue;
                        IndexedTriangle[] t = m.TriangleBatches[b - 1].Triangles;
                        int i = g - start[b - 1];
                        t[i].i2 = t[i].i1;
                        t[i].i3 = t[i].i1;
                        break;
                    }
            }
        }

        /// <summary>{count, vertex buffer size, the model's GUID, unpackers, then the per-vertex data}</summary>
        private sealed class VertexOverride
        {
            public int VertexBufferSize;
            public Guid Target;
            private int[] at;
            private Vector3[] pos, nrm;
            private Vector4b[] weights, bindings;
            private Colorb[] colour;

            public static VertexOverride Read(ByteCursor c)
            {
                BitReader r = new BitReader(c);
                int n = r.I(32);
                if ((long)n > 8192L) throw new InvalidDataException("a vertex override over 8192");
                VertexOverride v = new VertexOverride { VertexBufferSize = r.I(32) };
                byte[] g = new byte[16];
                for (int i = 0; i < 16; i++) g[i] = (byte)r.U(8);
                v.Target = new Guid(g);
                IntUnpacker index = new IntUnpacker(), normal = new IntUnpacker(), w = new IntUnpacker(), b = new IntUnpacker(), col = new IntUnpacker();
                Vector3Unpacker p = new Vector3Unpacker();
                index.Header(r); p.Header(r); normal.Header(r); w.Header(r); b.Header(r); col.Header(r);
                v.at = new int[n]; v.pos = new Vector3[n]; v.nrm = new Vector3[n]; v.weights = new Vector4b[n]; v.bindings = new Vector4b[n]; v.colour = new Colorb[n];
                for (int i = 0; i < n; i++)
                {
                    v.at[i] = index.Next(r);
                    float x, y, z;
                    p.Next(r, out x, out y, out z);
                    v.pos[i] = new Vector3(x, y, z);
                    v.nrm[i] = ModelParser.PackedNormal(normal.Next(r));
                    v.weights[i] = new Vector4b(w.Next(r));
                    v.bindings[i] = new Vector4b(b.Next(r));
                    v.colour[i] = new Colorb(col.Next(r));
                }
                return v;
            }

            public void Apply(AvatarComponent m)
            {
                int n = m.TriangleBatches.Length;
                int[] start = new int[n + 1], stride = new int[n];
                for (int i = 0; i < n; i++)
                {
                    stride[i] = m.TriangleBatches[i].Vertices.TextureCoordinates.Length * 4 + 28;
                    start[i + 1] = start[i] + m.TriangleBatches[i].Vertices.RawPositions.Length * stride[i];
                }
                for (int k = 0; k < at.Length; k++)
                {
                    int b = 1;
                    while (b <= n && at[k] >= start[b]) b++;
                    if (b > n) throw new InvalidDataException("a vertex override past its model");
                    b--;
                    int i = (at[k] - start[b]) / stride[b];
                    VertexDataBuffer vb = m.TriangleBatches[b].Vertices;
                    vb.RawPositions[i] = pos[k];
                    vb.RawNormals[i] = nrm[k];
                    vb.SkinBindings[i] = bindings[k];
                    vb.SkinWeights[i] = weights[k];
                    vb.Color0[i].red = (float)colour[k].red / 255f;
                    vb.Color0[i].green = (float)colour[k].green / 255f;
                    vb.Color0[i].blue = (float)colour[k].blue / 255f;
                    vb.Color0[i].alpha = (float)colour[k].alpha / 255f;
                }
            }
        }
    }

    /// <summary>The body's proportions: joint scales lerped toward templates by height, then by width.</summary>
    public static class SkeletonScaling
    {
        private const uint Pool = 0x9201E0F0;
        // the joints the width templates set
        private static readonly int[] Limbs = { 32, 35, 26, 29, 27, 30 }, Legs = { 7, 13, 9, 17 };
        private const int Neck = 24, BackB = 18, BackA = 10, Base = 4, Root = 0, Head = 19;

        private static Vector3 P(uint address)
        {
            Vector3? v = ConsoleTables.Vector("scaling-pool", Pool, address);
            if (v == null) throw new InvalidDataException("the body-size constants are not installed (the avatar tables)");
            return v.Value;
        }

        private static Vector3[] Width(uint limbs, uint legs, uint neck, uint backB, uint backA, uint baseJ, bool heavy)
        {
            Vector3[] s = new Vector3[72];
            for (int i = 0; i < 72; i++) s[i] = Vector3.One;
            foreach (int j in Limbs) s[j] = P(limbs);
            foreach (int j in Legs) s[j] = P(legs);
            s[Neck] = P(neck); s[BackB] = P(backB); s[BackA] = P(backA); s[Base] = P(baseJ);
            if (heavy) for (int i = 0; i < 72; i++) s[i] = Lerp(Vector3.One, s[i], 0.6f);
            return s;
        }

        private static Vector3[] Height(uint root, uint head)
        {
            Vector3[] s = new Vector3[72];
            for (int i = 0; i < 72; i++) s[i] = Vector3.One;
            s[Root] = P(root); s[Head] = P(head);
            return s;
        }

        private static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            return new Vector3(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Z + t * (b.Z - a.Z));
        }

        public static void Apply(Skeleton.SkeletonVersion version, AvatarGender body, float width, float height, Skeleton skeleton)
        {
            bool natal = version == Skeleton.SkeletonVersion.Natal;
            if (version != Skeleton.SkeletonVersion.Natal && version != Skeleton.SkeletonVersion.Nxe) throw new InvalidDataException("an invalid skeleton version");
            if (height >= 0f) Apply(skeleton, height, Height(0x9201E2D0, 0x9201E2C0));
            else Apply(skeleton, -height, Height(0x9201E2C0, 0x9201E2E0));
            if (body == AvatarGender.Male)
            {
                if (width >= 0f) Apply(skeleton, width, Width(0x9201E140, 0x9201E130, 0x9201E120, 0x9201E110, 0x9201E0F0, 0x9201E100, true));
                else if (natal) Apply(skeleton, -width, Width(0x9201E230, 0x9201E220, 0x9201E220, 0x9201E210, 0x9201E200, 0x9201E1F0, false));
                else Apply(skeleton, -width, Width(0x9201E1E0, 0x9201E1D0, 0x9201E1D0, 0x9201E1C0, 0x9201E1B0, 0x9201E1A0, false));
            }
            else if (body == AvatarGender.Female)
            {
                if (width > 0f) Apply(skeleton, width, Width(0x9201E190, 0x9201E180, 0x9201E170, 0x9201E180, 0x9201E160, 0x9201E150, true));
                else if (natal) Apply(skeleton, -width, Width(0x9201E2B0, 0x9201E2A0, 0x9201E2A0, 0x9201E290, 0x9201E280, 0x9201E270, false));
                else Apply(skeleton, -width, Width(0x9201E1E0, 0x9201E1D0, 0x9201E1D0, 0x9201E260, 0x9201E250, 0x9201E240, false));
            }
        }

        private static void Apply(Skeleton skeleton, float t, Vector3[] template)
        {
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            for (int i = skeleton.Joints.Length - 1; i >= 0; i--)
            {
                Vector3 v = Lerp(Vector3.One, template[i], t);
                skeleton.Joints[i].Local.scale.X *= v.X;
                skeleton.Joints[i].Local.scale.Y *= v.Y;
                skeleton.Joints[i].Local.scale.Z *= v.Z;
            }
            Vector3 r = Lerp(Vector3.One, template[0], t);
            skeleton.Joints[0].Local.position.X *= r.X;
            skeleton.Joints[0].Local.position.Y *= r.Y;
            skeleton.Joints[0].Local.position.Z *= r.Z;
        }
    }
}
