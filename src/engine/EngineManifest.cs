// The 1000-byte avatar manifest and its component rules (required slots, defaults, dependencies).

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    /// <summary>One component record: the asset, the slot mask and three custom colours.</summary>
    public struct ComponentInfo
    {
        public Guid AssetId;
        public AvatarComponentMasks ComponentMask;
        public Colorb CustomColor0, CustomColor1, CustomColor2;

        public ComponentInfo(Guid asset, AvatarComponentMasks mask, Colorb c0, Colorb c1, Colorb c2)
        { AssetId = asset; ComponentMask = mask; CustomColor0 = c0; CustomColor1 = c1; CustomColor2 = c2; }

        public static readonly ComponentInfo Zero = new ComponentInfo();
        public bool IsEmpty { get { return AssetId == Guid.Empty; } }

        public static bool operator ==(ComponentInfo a, ComponentInfo b)
        {
            return a.AssetId == b.AssetId && a.ComponentMask == b.ComponentMask && a.CustomColor0 == b.CustomColor0
                && a.CustomColor1 == b.CustomColor1 && a.CustomColor2 == b.CustomColor2;
        }
        public static bool operator !=(ComponentInfo a, ComponentInfo b) { return !(a == b); }
        public override bool Equals(object o) { return o is ComponentInfo && (ComponentInfo)o == this; }
        public override int GetHashCode() { return AssetId.GetHashCode() ^ (int)ComponentMask; }
    }

    /// <summary>A worn component, and the asset its dependency rule puts in its place (run time only).</summary>
    public sealed class ComponentDescription
    {
        public ComponentInfo Info;
        public Guid OverrideAsset;
        public ComponentDescription(ComponentInfo info, Guid overrideAsset) { Info = info; OverrideAsset = overrideAsset; }
    }

    public struct TexturePlacement { public float Scale, Rotation, TranslationU, TranslationV; }

    /// <summary>A replacement texture: the asset and its placement; LinkedAssetId is run-time only.</summary>
    public struct ReplacementTexture
    {
        public Guid TextureAssetId, LinkedAssetId;
        public TexturePlacement Placement;

        /// <summary>No texture, placed as an editor clears one (scale 1).</summary>
        public static ReplacementTexture None { get { ReplacementTexture t = new ReplacementTexture(); t.Placement.Scale = 1f; return t; } }
    }

    public sealed class AvatarManifest
    {
        public const int Size = 1000;
        public const int WornSlots = 13;

        /// <summary>The required components, in the previous-required records' order.</summary>
        public static readonly AvatarComponentMasks[] RequiredComponents =
            { AvatarComponentMasks.Shoes, AvatarComponentMasks.Trousers, AvatarComponentMasks.Shirt, AvatarComponentMasks.Hair };

        public float WidthFactor, HeightFactor;
        public readonly Guid[] BlendShapes = new Guid[(int)BlendShapeType.Count];
        public readonly ReplacementTexture[] Textures = new ReplacementTexture[(int)DynamicTextureType.Count];
        public readonly Colorb[] DynamicColors = new Colorb[(int)DynamicColorType.Count];
        public ComponentDescription BodyComponent, HeadComponent;
        public readonly List<ComponentDescription> Components = new List<ComponentDescription>();
        public readonly ComponentInfo[] PreviousRequired = new ComponentInfo[4];
        public byte[] ConsoleId = new byte[5], OwnerXuid = new byte[8];
        /// <summary>Dress the defaults where a required component is missing (always, from bytes).</summary>
        public bool DressDefaultClothes = true;

        // ---- reading and writing ------------------------------------------------------------------

        /// <summary>The manifest in `b`, or null when it is not a valid one.</summary>
        public static AvatarManifest Create(byte[] b)
        {
            if (b == null || b.Length < Size) return null;
            Reader r = new Reader(b);
            if (r.U32() != 0) return null;
            AvatarManifest m = new AvatarManifest();
            m.WidthFactor = r.F32();
            m.HeightFactor = r.F32();
            for (int i = 0; i < m.BlendShapes.Length; i++) { Guid g = r.Guid(); if (!ValidId(g)) return null; m.BlendShapes[i] = g; }
            for (int i = 0; i < m.Textures.Length; i++)
            {
                ReplacementTexture t = new ReplacementTexture();
                t.TextureAssetId = r.Guid();
                if (!ValidId(t.TextureAssetId)) return null;
                t.Placement.Scale = r.F32(); t.Placement.Rotation = r.F32(); t.Placement.TranslationU = r.F32(); t.Placement.TranslationV = r.F32();
                m.Textures[i] = t;
            }
            for (int i = 0; i < m.DynamicColors.Length; i++) m.DynamicColors[i] = r.Color();
            ComponentInfo c;
            if (!r.Component(out c) || c.ComponentMask != AvatarComponentMasks.Body || c.IsEmpty) return null;
            m.BodyComponent = new ComponentDescription(c, Guid.Empty);
            if (!r.Component(out c) || c.ComponentMask != AvatarComponentMasks.Head || c.IsEmpty) return null;
            m.HeadComponent = new ComponentDescription(c, Guid.Empty);
            for (int i = 0; i < WornSlots; i++)
            {
                if (!r.Component(out c)) return null;
                if (!c.IsEmpty) m.Components.Add(new ComponentDescription(c, Guid.Empty));
            }
            for (int i = 0; i < m.PreviousRequired.Length; i++)
            {
                if (!r.Component(out c)) return null;
                m.PreviousRequired[i] = c;
            }
            Array.Copy(b, r.At, m.ConsoleId, 0, 5);
            Array.Copy(b, r.At + 5, m.OwnerXuid, 0, 8);
            return m.Validate() ? m : null;
        }

        /// <summary>The 1000 bytes; worn components fill the 13 slots in order.</summary>
        public byte[] SaveToBinary()
        {
            byte[] b = new byte[Size];
            Writer w = new Writer(b);
            w.U32(0);
            w.F32(WidthFactor);
            w.F32(HeightFactor);
            foreach (Guid g in BlendShapes) w.Guid(g);
            foreach (ReplacementTexture t in Textures)
            {
                w.Guid(t.TextureAssetId);
                w.F32(t.Placement.Scale); w.F32(t.Placement.Rotation); w.F32(t.Placement.TranslationU); w.F32(t.Placement.TranslationV);
            }
            foreach (Colorb c in DynamicColors) w.Color(c);
            w.Component(BodyComponent.Info);
            w.Component(HeadComponent.Info);
            for (int i = 0; i < WornSlots; i++) w.Component(i < Components.Count ? Components[i].Info : ComponentInfo.Zero);
            foreach (ComponentInfo c in PreviousRequired) w.Component(c);
            Array.Copy(ConsoleId, 0, b, w.At, 5);
            Array.Copy(OwnerXuid, 0, b, w.At + 5, 8);
            return b;
        }

        public AvatarManifest Clone()
        {
            AvatarManifest m = new AvatarManifest();
            m.WidthFactor = WidthFactor; m.HeightFactor = HeightFactor; m.DressDefaultClothes = DressDefaultClothes;
            Array.Copy(BlendShapes, m.BlendShapes, BlendShapes.Length);
            Array.Copy(Textures, m.Textures, Textures.Length);
            Array.Copy(DynamicColors, m.DynamicColors, DynamicColors.Length);
            Array.Copy(PreviousRequired, m.PreviousRequired, PreviousRequired.Length);
            m.BodyComponent = new ComponentDescription(BodyComponent.Info, BodyComponent.OverrideAsset);
            m.HeadComponent = new ComponentDescription(HeadComponent.Info, HeadComponent.OverrideAsset);
            foreach (ComponentDescription d in Components) m.Components.Add(new ComponentDescription(d.Info, d.OverrideAsset));
            m.ConsoleId = ConsoleId; m.OwnerXuid = OwnerXuid;
            return m;
        }

        /// <summary>A stock (TOC-kind) GUID must carry the avatar namespace; any other passes.</summary>
        public static bool ValidId(Guid g) { return AssetIds.Kind(g) != AssetGuidType.TOC || AssetIds.InStockNamespace(g); }

        // ---- the body -------------------------------------------------------------------------------

        public AvatarGender BodyType
        {
            get
            {
                Guid g = BodyComponent == null ? Guid.Empty : BodyComponent.Info.AssetId;
                if (g == ConsoleTables.MaleBody) return AvatarGender.Male;
                if (g == ConsoleTables.FemaleBody) return AvatarGender.Female;
                return AvatarGender.Unknown;
            }
        }

        public bool Validate() { AvatarGender g = BodyType; return g == AvatarGender.Male || g == AvatarGender.Female; }

        // ---- colours, textures, shapes ---------------------------------------------------------------

        public Colorb GetDynamicColor(DynamicColorType t) { return DynamicColors[(int)t]; }
        public void SetDynamicColor(DynamicColorType t, Colorb c) { DynamicColors[(int)t] = c; }
        public ReplacementTexture GetReplacementTexture(DynamicTextureType t) { return Textures[(int)t]; }
        public void SetReplacementTexture(DynamicTextureType t, ReplacementTexture r) { Textures[(int)t] = r; }
        public Guid GetBlendShape(BlendShapeType t) { return BlendShapes[(int)t]; }
        public void SetBlendShape(BlendShapeType t, Guid g) { BlendShapes[(int)t] = g; }

        // ---- components -----------------------------------------------------------------------------

        private static bool Matches(AvatarComponentMasks have, AvatarComponentMasks mask, AssetMatchingMode mode)
        {
            switch (mode)
            {
                case AssetMatchingMode.Exact: return have == mask;
                case AssetMatchingMode.Any: return (have & mask) != 0;
                case AssetMatchingMode.All: return (have & mask) == mask;
                default: return false;
            }
        }

        /// <summary>The first worn component whose mask matches, or Zero.</summary>
        public ComponentInfo GetComponentInfo(AvatarComponentMasks mask, AssetMatchingMode mode)
        {
            foreach (ComponentDescription d in Components) if (Matches(d.Info.ComponentMask, mask, mode)) return d.Info;
            return ComponentInfo.Zero;
        }

        /// <summary>Every worn component sharing a bit with `mask`, in the list's order.</summary>
        public List<ComponentInfo> GetComponents(AvatarComponentMasks mask)
        {
            List<ComponentInfo> l = new List<ComponentInfo>();
            foreach (ComponentDescription d in Components) if ((d.Info.ComponentMask & mask) != 0) l.Add(d.Info);
            return l;
        }

        public AvatarComponentMasks CombinedMask
        {
            get { AvatarComponentMasks m = AvatarComponentMasks.None; foreach (ComponentDescription d in Components) m |= d.Info.ComponentMask; return m; }
        }

        /// <summary>Takes off every worn component sharing a bit with `mask`.</summary>
        public void ClearComponents(AvatarComponentMasks mask)
        {
            Components.RemoveAll(d => (d.Info.ComponentMask & mask) != 0);
        }

        /// <summary>Remembers the required components `mask` is about to take off, slot by slot.</summary>
        public void UpdatePreviousComponents(AvatarComponentMasks mask)
        {
            foreach (ComponentDescription d in Components)
            {
                if ((d.Info.ComponentMask & mask) == 0) continue;
                int k = Array.IndexOf(RequiredComponents, d.Info.ComponentMask);
                if (k >= 0) PreviousRequired[k] = d.Info;
            }
        }

        /// <summary>Wears `c` in place of whatever shares a bit with its mask.</summary>
        public void ReplaceComponent(ComponentInfo c)
        {
            ClearComponents(c.ComponentMask);
            Components.Add(new ComponentDescription(c, Guid.Empty));
        }

        /// <summary>Wears `c`, then refills what the required slots lack; false when the body is unknown.</summary>
        public bool SetComponentInfo(ComponentInfo c)
        {
            int k = Array.IndexOf(RequiredComponents, c.ComponentMask);
            if (k >= 0) PreviousRequired[k] = c;
            ReplaceComponent(c);
            return EquipRequiredComponents(false);
        }

        /// <summary>Fills each empty required slot from its previous record, or the body's default.</summary>
        public bool EquipRequiredComponents(bool useDefaults)
        {
            AvatarGender body = BodyType;
            if (body == AvatarGender.Unknown) return false;
            AvatarComponentMasks have = CombinedMask;
            for (int i = 0; i < RequiredComponents.Length; i++)
            {
                if ((RequiredComponents[i] & have) != 0) continue;
                ComponentInfo c = PreviousRequired[i];
                if (useDefaults && c.IsEmpty) c = ConsoleTables.DefaultRequired(body, i);
                if (!c.IsEmpty) Components.Add(new ComponentDescription(c, Guid.Empty));
            }
            return true;
        }

        /// <summary>Takes off `mask`, then refills the required slots from their previous records.</summary>
        public bool RemoveComponents(AvatarComponentMasks mask)
        {
            AvatarGender body = BodyType;
            if (body == AvatarGender.Unknown) return false;
            bool ok = true;
            for (int i = 0; i < RequiredComponents.Length; i++)
            {
                if ((mask & RequiredComponents[i]) == 0) continue;
                ComponentInfo c = GetComponentInfo(RequiredComponents[i], AssetMatchingMode.Exact);
                if (c.IsEmpty) continue;
                ComponentInfo def = ConsoleTables.DefaultRequired(body, i);
                bool done = c.AssetId != def.AssetId && SetComponentInfo(def);
                if (!done) ok = false;
            }
            ClearComponents(mask);
            if (!EquipRequiredComponents(false)) ok = false;
            return ok;
        }

        public bool IsAssetPresent(Guid g)
        {
            if (BodyComponent.Info.AssetId == g || HeadComponent.Info.AssetId == g) return true;
            foreach (ComponentDescription d in Components) if (d.Info.AssetId == g) return true;
            return false;
        }

        // ---- what loading an avatar needs (EngineAvatar.cs) ---------------------------------------

        /// <summary>As EquipRequiredComponents, but each previous record used is cleared.</summary>
        public bool ReplaceMissingComponents(bool useDefaults)
        {
            AvatarGender body = BodyType;
            if (body == AvatarGender.Unknown) return false;
            AvatarComponentMasks have = CombinedMask;
            for (int i = 0; i < RequiredComponents.Length; i++)
            {
                if ((RequiredComponents[i] & have) != 0) continue;
                ComponentInfo c = PreviousRequired[i];
                PreviousRequired[i] = ComponentInfo.Zero;
                if (useDefaults && c.IsEmpty) c = ConsoleTables.DefaultRequired(body, i);
                if (!c.IsEmpty) Components.Add(new ComponentDescription(c, Guid.Empty));
            }
            return true;
        }

        public bool RequiredComponentsPresent
        {
            get { AvatarComponentMasks have = CombinedMask; foreach (AvatarComponentMasks r in RequiredComponents) if ((have & r) == 0) return false; return true; }
        }

        public bool RequiredBlendShapesPresent { get { foreach (Guid g in BlendShapes) if (g == Guid.Empty) return false; return true; } }

        /// <summary>Mouth, eyes and eyebrows.</summary>
        public bool RequiredTexturesPresent
        {
            get
            {
                return Textures[(int)DynamicTextureType.Mouth].TextureAssetId != Guid.Empty && Textures[(int)DynamicTextureType.Eye].TextureAssetId != Guid.Empty
                    && Textures[(int)DynamicTextureType.Eyebrow].TextureAssetId != Guid.Empty;
            }
        }

        /// <summary>The bits of the worn components that share a bit with `mask`.</summary>
        public AvatarComponentMasks CombinedMaskWithin(AvatarComponentMasks mask)
        {
            AvatarComponentMasks m = AvatarComponentMasks.None;
            foreach (ComponentDescription d in Components) if ((d.Info.ComponentMask & mask) != 0) m |= d.Info.ComponentMask;
            return m;
        }

        /// <summary>The dependent the dependency table puts with `asset`, when its condition holds.</summary>
        public Guid ResolveDependency(Guid asset)
        {
            AvatarAssetDependency d = Dependencies.Get(asset);
            if (d == null) return Guid.Empty;
            if (d.ModifiedAssetList == null) return d.DependentAssetId;
            foreach (Guid g in d.ModifiedAssetList) if (IsAssetPresent(g)) return d.DependentAssetId;
            return Guid.Empty;
        }

        /// <summary>Each component's override and each texture's linked asset, from the dependency table.</summary>
        public void UpdateDependencies()
        {
            BodyComponent.OverrideAsset = ResolveDependency(BodyComponent.Info.AssetId);
            HeadComponent.OverrideAsset = ResolveDependency(HeadComponent.Info.AssetId);
            foreach (ComponentDescription d in Components) d.OverrideAsset = ResolveDependency(d.Info.AssetId);
            for (int i = 0; i < Textures.Length; i++) Textures[i].LinkedAssetId = ResolveDependency(Textures[i].TextureAssetId);
        }

        /// <summary>The bodies and the console's default clothes: an avatar cannot be drawn without them.</summary>
        public bool IsCoreAsset(Guid g)
        {
            if (g == ConsoleTables.MaleBody || g == ConsoleTables.FemaleBody) return true;
            for (int i = 0; i < 4; i++)
                if (ConsoleTables.DefaultRequired(AvatarGender.Male, i).AssetId == g || ConsoleTables.DefaultRequired(AvatarGender.Female, i).AssetId == g) return true;
            return false;
        }

        /// <summary>Takes an asset off wherever it is.</summary>
        public void RemoveAsset(Guid g)
        {
            if (BodyComponent.Info.AssetId == g) BodyComponent.Info = ComponentInfo.Zero;
            if (HeadComponent.Info.AssetId == g) HeadComponent.Info = ComponentInfo.Zero;
            int k = Components.FindIndex(d => d.Info.AssetId == g);
            if (k >= 0) Components.RemoveAt(k);
            for (int i = 0; i < PreviousRequired.Length; i++) if (PreviousRequired[i].AssetId == g) { PreviousRequired[i] = ComponentInfo.Zero; break; }
            for (int i = 0; i < BlendShapes.Length; i++) if (BlendShapes[i] == g) { BlendShapes[i] = Guid.Empty; break; }
            for (int i = 0; i < Textures.Length; i++) if (Textures[i].TextureAssetId == g) { Textures[i] = new ReplacementTexture(); break; }
        }

        // ---- comparing ------------------------------------------------------------------------------

        /// <summary>Equal in all the manifest holds; components in any order; placements within 1e-6.</summary>
        public static bool operator ==(AvatarManifest a, AvatarManifest b)
        {
            if (ReferenceEquals(a, b)) return true;
            if ((object)a == null || (object)b == null) return false;
            if (a.WidthFactor != b.WidthFactor || a.HeightFactor != b.HeightFactor) return false;
            for (int i = 0; i < a.DynamicColors.Length; i++) if (a.DynamicColors[i] != b.DynamicColors[i]) return false;
            for (int i = 0; i < a.Textures.Length; i++)
            {
                ReplacementTexture x = a.Textures[i], y = b.Textures[i];
                if (x.TextureAssetId != y.TextureAssetId || x.LinkedAssetId != y.LinkedAssetId) return false;
                if (Math.Abs(x.Placement.Rotation - y.Placement.Rotation) > 1e-6f || Math.Abs(x.Placement.Scale - y.Placement.Scale) > 1e-6f
                    || Math.Abs(x.Placement.TranslationU - y.Placement.TranslationU) > 1e-6f || Math.Abs(x.Placement.TranslationV - y.Placement.TranslationV) > 1e-6f)
                    return false;
            }
            for (int i = 0; i < a.PreviousRequired.Length; i++) if (a.PreviousRequired[i] != b.PreviousRequired[i]) return false;
            if (!Same(a.HeadComponent, b.HeadComponent) || !Same(a.BodyComponent, b.BodyComponent)) return false;
            if (a.Components.Count != b.Components.Count) return false;
            foreach (ComponentDescription d in b.Components)
                if (!a.Components.Exists(e => Same(e, d))) return false;
            for (int i = 0; i < a.BlendShapes.Length; i++) if (a.BlendShapes[i] != b.BlendShapes[i]) return false;
            return true;
        }

        public static bool operator !=(AvatarManifest a, AvatarManifest b) { return !(a == b); }
        public override bool Equals(object o) { return o is AvatarManifest && (AvatarManifest)o == this; }
        public override int GetHashCode() { return BodyComponent == null ? 0 : BodyComponent.Info.AssetId.GetHashCode(); }

        private static bool Same(ComponentDescription x, ComponentDescription y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x == null || y == null) return false;
            return x.Info == y.Info && x.OverrideAsset == y.OverrideAsset;
        }

        // ---- the byte readers -------------------------------------------------------------------------

        private sealed class Reader
        {
            private readonly byte[] b; public int At;
            public Reader(byte[] b) { this.b = b; }
            public uint U32() { uint v = (uint)(b[At] << 24 | b[At + 1] << 16 | b[At + 2] << 8 | b[At + 3]); At += 4; return v; }
            public float F32() { byte[] t = { b[At + 3], b[At + 2], b[At + 1], b[At] }; At += 4; return BitConverter.ToSingle(t, 0); }
            public Guid Guid() { Guid g = AssetIds.FromBytes(b, At); At += 16; return g; }
            public Colorb Color() { Colorb c = new Colorb(b[At + 1], b[At + 2], b[At + 3], b[At]); At += 4; return c; }
            public bool Component(out ComponentInfo c)
            {
                c = new ComponentInfo();
                c.AssetId = Guid();
                if (!ValidId(c.AssetId)) return false;
                c.ComponentMask = (AvatarComponentMasks)(short)(b[At] << 8 | b[At + 1]);
                At += 4;                                                  // the mask and the i16 after it
                c.CustomColor0 = Color(); c.CustomColor1 = Color(); c.CustomColor2 = Color();
                return true;
            }
        }

        private sealed class Writer
        {
            private readonly byte[] b; public int At;
            public Writer(byte[] b) { this.b = b; }
            public void U32(uint v) { b[At] = (byte)(v >> 24); b[At + 1] = (byte)(v >> 16); b[At + 2] = (byte)(v >> 8); b[At + 3] = (byte)v; At += 4; }
            public void F32(float f) { byte[] t = BitConverter.GetBytes(f); b[At] = t[3]; b[At + 1] = t[2]; b[At + 2] = t[1]; b[At + 3] = t[0]; At += 4; }
            public void Guid(Guid g) { Array.Copy(AssetIds.Bytes(g), 0, b, At, 16); At += 16; }
            public void Color(Colorb c) { b[At] = c.alpha; b[At + 1] = c.red; b[At + 2] = c.green; b[At + 3] = c.blue; At += 4; }
            public void Component(ComponentInfo c)
            {
                Guid(c.AssetId);
                short m = (short)c.ComponentMask;
                b[At] = (byte)(m >> 8); b[At + 1] = (byte)m; b[At + 2] = 0; b[At + 3] = 0; At += 4;
                Color(c.CustomColor0); Color(c.CustomColor1); Color(c.CustomColor2);
            }
        }
    }
}
