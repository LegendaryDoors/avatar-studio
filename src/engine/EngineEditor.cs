// Editing a manifest as the avatar editor does: items, textures and shapes on and off, colours and size,
// and the dependency table that ties an eye texture to its shadow and a hat to the hair it reshapes.

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    public enum BinaryAssetType { Unknown, Component, Texture, ShapeOverride, Animation, ShapeOverridePost }

    /// <summary>A component's three custom colours.</summary>
    public struct ComponentColors
    {
        public Colorb CustomColor0, CustomColor1, CustomColor2;

        /// <summary>0 colours: all zero; 1: the one three times; 3: each.</summary>
        public ComponentColors(Colorb[] c)
        {
            if (c == null) throw new ArgumentNullException("c");
            switch (c.Length)
            {
                case 0: CustomColor0 = CustomColor1 = CustomColor2 = new Colorb(0); break;
                case 1: CustomColor0 = CustomColor1 = CustomColor2 = c[0]; break;
                case 3: CustomColor0 = c[0]; CustomColor1 = c[1]; CustomColor2 = c[2]; break;
                default: throw new ArgumentException("Invalid length of the array. Must be one of 0, 1 or 3.");
            }
        }

        public Colorb[] ToColorArray() { return new[] { CustomColor0, CustomColor1, CustomColor2 }; }
    }

    /// <summary>What to put on (or what was there before).</summary>
    public sealed class EditorAssetInfo
    {
        public Guid AssetId = Guid.Empty;
        public uint Details;
        public BinaryAssetType AssetType = BinaryAssetType.Unknown;
        public List<ComponentInfo> PreviousAssets;
        public Colorb Color;
        public DynamicColorType CustomColorType = DynamicColorType.Count;
        public ComponentColors ComponentColor;
        public bool UseComponentColor, RemoveEyeShadow;
        public List<ComponentInfo> Accessories;

        public EditorAssetInfo() { }
        public EditorAssetInfo(Guid asset) { AssetId = asset; }
        public EditorAssetInfo(Guid asset, ComponentColors colors) { AssetId = asset; UseComponentColor = true; ComponentColor = colors; }
        public EditorAssetInfo(BlendShapeType t, Guid asset) { AssetId = asset; Details = (uint)t; AssetType = BinaryAssetType.ShapeOverride; }
        public EditorAssetInfo(DynamicTextureType t, Guid asset) { AssetId = asset; Details = (uint)t; AssetType = BinaryAssetType.Texture; }
        public EditorAssetInfo(AvatarComponentMasks remove) { Details = (uint)(ushort)remove; AssetType = BinaryAssetType.Component; }

        public void SetAssetCustomColor(Colorb c, DynamicColorType t) { Color = c; CustomColorType = t; }
    }

    public sealed class AvatarManifestEditor
    {
        public enum RemovableComponents { NotRemovable, Earrings, Glasses, Gloves, Hat, Ring, Wristwear, FacialHair, EyeShadow, SkinFeatures, Prop }

        private readonly AvatarManifest m;

        public AvatarManifestEditor(AvatarManifest manifest)
        {
            if (manifest == null) throw new ArgumentNullException("manifest");
            m = manifest;
        }

        public AvatarManifest Manifest { get { return m; } }

        public float AvatarWidthFactor
        {
            get { return m.WidthFactor; }
            set { if (value > 1f || value < -1f) throw new ArgumentOutOfRangeException("value", "The value must be in range [-1,1]"); m.WidthFactor = value; }
        }

        public float AvatarHeightFactor
        {
            get { return m.HeightFactor; }
            set { if (value > 1f || value < -1f) throw new ArgumentOutOfRangeException("value", "The value must be in range [-1,1]"); m.HeightFactor = value; }
        }

        /// <summary>The colour; an eye-shadow colour with no shadow on first puts on the eyes' shadow.</summary>
        public void SetAvatarColor(DynamicColorType t, Colorb c)
        {
            if (t == DynamicColorType.EyeShadow && m.GetReplacementTexture(DynamicTextureType.EyeShadow).TextureAssetId == Guid.Empty) UpdateEyeShadow();
            m.SetDynamicColor(t, c);
        }

        /// <summary>The kind an asset's GUID names.</summary>
        public static EditorAssetInfo KindOf(Guid g)
        {
            EditorAssetInfo i = new EditorAssetInfo();
            ComponentCategories c = AssetIds.Category(g);
            BlendShapeType shape = c == ComponentCategories.Chin ? BlendShapeType.Chin : c == ComponentCategories.Ears ? BlendShapeType.Ear
                                 : c == ComponentCategories.Nose ? BlendShapeType.Nose : BlendShapeType.Count;
            if (shape != BlendShapeType.Count) { i.AssetType = BinaryAssetType.ShapeOverride; i.Details = (uint)shape; return i; }
            DynamicTextureType tex = TextureKind(c);
            if (tex != DynamicTextureType.Count) { i.AssetType = BinaryAssetType.Texture; i.Details = (uint)tex; return i; }
            if (c == ComponentCategories.Animation) { i.AssetType = BinaryAssetType.Animation; return i; }
            i.AssetType = BinaryAssetType.Component;
            i.Details = (uint)(c & ComponentCategories.Models);
            return i;
        }

        public static DynamicTextureType TextureKind(ComponentCategories c)
        {
            switch (c)
            {
                case ComponentCategories.FacialHair: return DynamicTextureType.FacialHair;
                case ComponentCategories.Mouth: return DynamicTextureType.Mouth;
                case ComponentCategories.Eyes: return DynamicTextureType.Eye;
                case ComponentCategories.Eyebrows: return DynamicTextureType.Eyebrow;
                case ComponentCategories.EyeShadow: return DynamicTextureType.EyeShadow;
                case ComponentCategories.FacialOther: return DynamicTextureType.SkinFeatures;
                default: return DynamicTextureType.Count;
            }
        }

        /// <summary>Puts `a` on; returns what it replaced (the undo record).</summary>
        public EditorAssetInfo ReplaceAsset(EditorAssetInfo a)
        {
            EditorAssetInfo was = new EditorAssetInfo();
            if (a.AssetId != Guid.Empty && a.AssetType == BinaryAssetType.Unknown)
            {
                EditorAssetInfo k = KindOf(a.AssetId);
                a.AssetType = k.AssetType;
                a.Details = k.Details;
            }
            if (a.CustomColorType < DynamicColorType.Count)
            {
                if (a.CustomColorType == DynamicColorType.EyeShadow && m.GetReplacementTexture(DynamicTextureType.EyeShadow).TextureAssetId == Guid.Empty)
                    was.RemoveEyeShadow = true;
                was.SetAssetCustomColor(m.GetDynamicColor(a.CustomColorType), a.CustomColorType);
                SetAvatarColor(a.CustomColorType, a.Color);
            }
            if (a.RemoveEyeShadow) m.SetReplacementTexture(DynamicTextureType.EyeShadow, ReplacementTexture.None);
            was.AssetType = a.AssetType;
            was.Details = a.Details;
            switch (a.AssetType)
            {
                case BinaryAssetType.Texture:
                {
                    DynamicTextureType t = (DynamicTextureType)a.Details;
                    ReplacementTexture r = ReplacementTexture.None;
                    r.TextureAssetId = a.AssetId;
                    was.AssetId = m.GetReplacementTexture(t).TextureAssetId;
                    m.SetReplacementTexture(t, r);
                    if (t == DynamicTextureType.Eye && m.GetReplacementTexture(DynamicTextureType.EyeShadow).TextureAssetId != Guid.Empty) UpdateEyeShadow();
                    break;
                }
                case BinaryAssetType.Unknown:
                case BinaryAssetType.Animation:
                    throw new InvalidOperationException("Invalid asset type");
                case BinaryAssetType.Component:
                {
                    ComponentInfo c = new ComponentInfo();
                    c.AssetId = a.AssetId;
                    c.ComponentMask = (AvatarComponentMasks)(short)a.Details;
                    if (a.UseComponentColor) { c.CustomColor0 = a.ComponentColor.CustomColor0; c.CustomColor1 = a.ComponentColor.CustomColor1; c.CustomColor2 = a.ComponentColor.CustomColor2; }
                    was.PreviousAssets = m.GetComponents(c.ComponentMask);
                    if (was.PreviousAssets.Count > 0)
                    {
                        ComponentInfo p = was.PreviousAssets[0];
                        was.AssetId = p.AssetId;
                        was.UseComponentColor = true;
                        was.ComponentColor = new ComponentColors(new[] { p.CustomColor0, p.CustomColor1, p.CustomColor2 });
                    }
                    if (a.AssetId == Guid.Empty) m.RemoveComponents(c.ComponentMask);
                    else if (a.PreviousAssets != null && a.PreviousAssets.Count > 0)
                    {
                        // an undo: the components the record holds go back on
                        AvatarComponentMasks mask = AvatarComponentMasks.None;
                        foreach (ComponentInfo p in a.PreviousAssets) mask |= p.ComponentMask;
                        m.UpdatePreviousComponents(mask);
                        m.ClearComponents(mask);
                        foreach (ComponentInfo p in a.PreviousAssets) m.Components.Add(new ComponentDescription(p, Guid.Empty));
                        m.EquipRequiredComponents(true);
                    }
                    else
                    {
                        m.UpdatePreviousComponents(c.ComponentMask);
                        m.ReplaceComponent(c);
                        m.EquipRequiredComponents(true);
                    }
                    break;
                }
                case BinaryAssetType.ShapeOverride:
                case BinaryAssetType.ShapeOverridePost:
                {
                    BlendShapeType t = (BlendShapeType)a.Details;
                    was.AssetId = m.GetBlendShape(t);
                    m.SetBlendShape(t, a.AssetId);
                    break;
                }
            }
            if (a.Accessories != null)
                foreach (ComponentInfo acc in a.Accessories)
                    if (!m.Components.Exists(d => (d.Info.ComponentMask & acc.ComponentMask) != 0))   // nothing on any of its bits
                        m.SetComponentInfo(acc);
            return was;
        }

        /// <summary>Takes a removable kind off: a component by the manifest's rules, a facial texture zeroed.</summary>
        public void RemoveComponents(RemovableComponents k)
        {
            switch (k)
            {
                case RemovableComponents.Earrings: m.RemoveComponents(AvatarComponentMasks.Earrings); break;
                case RemovableComponents.Glasses: m.RemoveComponents(AvatarComponentMasks.Glasses); break;
                case RemovableComponents.Gloves: m.RemoveComponents(AvatarComponentMasks.Gloves); break;
                case RemovableComponents.Hat: m.RemoveComponents(AvatarComponentMasks.Hat); break;
                case RemovableComponents.Ring: m.RemoveComponents(AvatarComponentMasks.Ring); break;
                case RemovableComponents.Wristwear: m.RemoveComponents(AvatarComponentMasks.Wristwear); break;
                case RemovableComponents.FacialHair: m.SetReplacementTexture(DynamicTextureType.FacialHair, new ReplacementTexture()); break;
                case RemovableComponents.EyeShadow: m.SetReplacementTexture(DynamicTextureType.EyeShadow, new ReplacementTexture()); break;
                case RemovableComponents.SkinFeatures: m.SetReplacementTexture(DynamicTextureType.SkinFeatures, new ReplacementTexture()); break;
                case RemovableComponents.Prop: m.RemoveComponents(AvatarComponentMasks.Carryable); break;
                default: throw new InvalidOperationException("Shouldn't get here");
            }
        }

        /// <summary>The eye shadow paired with the eyes on (the dependency table), placed as new.</summary>
        private void UpdateEyeShadow()
        {
            AvatarAssetDependency d = Dependencies.Get(m.GetReplacementTexture(DynamicTextureType.Eye).TextureAssetId);
            if (d == null) throw new NullReferenceException("no dependency record for the eye texture");
            if (d.DependentAssetId == Guid.Empty) throw new InvalidOperationException("Failed to resolve eye shadow texture.");
            ReplacementTexture r = ReplacementTexture.None;
            r.TextureAssetId = d.DependentAssetId;
            m.SetReplacementTexture(DynamicTextureType.EyeShadow, r);
        }
    }

    /// <summary>One dependency record: the asset that goes with another, and the assets it needs.</summary>
    public sealed class AvatarAssetDependency
    {
        public Guid DependentAssetId;
        public Guid[] ModifiedAssetList;
    }

    /// <summary>tocassetdependencies: {GUID, dependent, u32 count (<= 4), count GUIDs}, .NET byte order; first wins.</summary>
    public static class Dependencies
    {
        /// <summary>Where the table's bytes come from; null leaves it empty.</summary>
        public static Func<byte[]> Source;
        private static Dictionary<Guid, AvatarAssetDependency> table;
        private static readonly object gate = new object();

        public static void Reset() { lock (gate) table = null; }

        /// <summary>Every record (empty without a table).</summary>
        public static IEnumerable<AvatarAssetDependency> Records()
        {
            Dictionary<Guid, AvatarAssetDependency> t;
            lock (gate)
            {
                if (table == null) table = Parse(Source == null ? null : Source());
                t = table;
            }
            return t.Values;
        }

        public static AvatarAssetDependency Get(Guid id)
        {
            Dictionary<Guid, AvatarAssetDependency> t;
            lock (gate)
            {
                if (table == null) table = Parse(Source == null ? null : Source());
                t = table;
            }
            AvatarAssetDependency d;
            return t.TryGetValue(id, out d) ? d : null;
        }

        public static Dictionary<Guid, AvatarAssetDependency> Parse(byte[] b)
        {
            Dictionary<Guid, AvatarAssetDependency> t = new Dictionary<Guid, AvatarAssetDependency>();
            if (b == null) return t;
            int at = 0;
            byte[] g = new byte[16];
            while (at + 32 <= b.Length)
            {
                Buffer.BlockCopy(b, at, g, 0, 16);
                Guid key = new Guid(g);
                Buffer.BlockCopy(b, at + 16, g, 0, 16);
                AvatarAssetDependency d = new AvatarAssetDependency { DependentAssetId = new Guid(g) };
                at += 32;
                if (at + 4 > b.Length) break;
                int n = BitConverter.ToInt32(b, at);
                at += 4;
                if (n > 0)
                {
                    if ((uint)n > 4u) break;
                    d.ModifiedAssetList = new Guid[n];
                    for (int j = 0; j < n; j++)
                    {
                        if (at + 16 > b.Length) { d.ModifiedAssetList[j] = Guid.Empty; continue; }
                        Buffer.BlockCopy(b, at, g, 0, 16);
                        d.ModifiedAssetList[j] = new Guid(g);
                        at += 16;
                    }
                }
                if (!t.ContainsKey(key)) t.Add(key, d);
            }
            return t;
        }
    }
}
