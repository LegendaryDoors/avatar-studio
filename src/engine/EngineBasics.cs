// The avatar engine's basics: enums, the colour type, and what an asset's GUID says.

using System;

namespace AvatarStudio.Avatars
{
    public enum AvatarGender { Unknown = 0, Male = 1, Female = 2, Both = 3 }

    /// <summary>A manifest component's mask (i16 at +16 of its 32-byte record).</summary>
    [Flags]
    public enum AvatarComponentMasks : short
    {
        None = 0, Head = 1, Body = 2, Hair = 4, Shirt = 8, Trousers = 0x10, Shoes = 0x20, Hat = 0x40, Gloves = 0x80,
        Glasses = 0x100, Wristwear = 0x200, Earrings = 0x400, Ring = 0x800, Carryable = 0x1000, All = 0x1FFF,
    }

    /// <summary>An asset's category: a stock GUID's Data1; the first 13 bits are the component masks'.</summary>
    [Flags]
    public enum ComponentCategories
    {
        None = 0, Head = 1, Body = 2, Hair = 4, Shirt = 8, Trousers = 0x10, Shoes = 0x20, Hat = 0x40, Gloves = 0x80,
        Glasses = 0x100, Wristwear = 0x200, Earrings = 0x400, Ring = 0x800, Carryable = 0x1000,
        Eyes = 0x2000, Eyebrows = 0x4000, Mouth = 0x8000, FacialHair = 0x10000, FacialOther = 0x20000, EyeShadow = 0x40000,
        Nose = 0x80000, Chin = 0x100000, Ears = 0x200000, Animation = 0x400000, Costume = 0x800000, Shape = 0x1000000,
        Models = 0x1FFF, Textures = 0x7E000, BlendShapes = 0x1380000, Clothing = 0x801FF8, Valid = 0x1FFFFFF,
    }

    /// <summary>The manifest's nine dynamic colours, in stored order.</summary>
    public enum DynamicColorType { Skin, Hair, Mouth, Iris, Eyebrow, EyeShadow, FacialHair, SkinFeatures1, SkinFeatures2, Count }

    /// <summary>The manifest's six replacement textures, in stored order.</summary>
    public enum DynamicTextureType { Mouth, Eye, Eyebrow, FacialHair, EyeShadow, SkinFeatures, Count }

    /// <summary>The manifest's three blend shapes, in stored order.</summary>
    public enum BlendShapeType { Chin, Nose, Ear, Count }

    /// <summary>A GUID's kind: (Data3 >> 8) & 0xF when Data4[0]'s top nibble is 0xC, else custom.</summary>
    public enum AssetGuidType { Custom, TOC, Awardable, MarketPlace }

    public enum AssetMatchingMode { Exact, Any, All }

    /// <summary>An 8-bit colour as the manifest stores it (A, R, G, B).</summary>
    public struct Colorb
    {
        public byte blue, green, red, alpha;

        public Colorb(byte red, byte green, byte blue) : this(red, green, blue, 255) { }
        public Colorb(byte red, byte green, byte blue, byte alpha) { this.red = red; this.green = green; this.blue = blue; this.alpha = alpha; }
        public Colorb(int argb) { alpha = (byte)(argb >> 24); red = (byte)(argb >> 16); green = (byte)(argb >> 8); blue = (byte)argb; }

        public int CompositeArgb { get { return (alpha << 24) | (red << 16) | (green << 8) | blue; } }

        public static bool operator ==(Colorb a, Colorb b) { return a.CompositeArgb == b.CompositeArgb; }
        public static bool operator !=(Colorb a, Colorb b) { return a.CompositeArgb != b.CompositeArgb; }
        public override bool Equals(object o) { return o is Colorb && (Colorb)o == this; }
        public override int GetHashCode() { return CompositeArgb; }
        public override string ToString() { return CompositeArgb.ToString("X8"); }
    }

    /// <summary>What a GUID says: stock GUIDs end in c1c8-f109a19cb2e0; Data1 is the category, Data3 body and kind.</summary>
    public static class AssetIds
    {
        /// <summary>The 16 bytes as manifests and file names write them: Data1-3 big-endian, then Data4.</summary>
        public static byte[] Bytes(Guid g)
        {
            byte[] le = g.ToByteArray();
            return new byte[] { le[3], le[2], le[1], le[0], le[5], le[4], le[7], le[6], le[8], le[9], le[10], le[11], le[12], le[13], le[14], le[15] };
        }

        public static Guid FromBytes(byte[] b, int o)
        {
            return new Guid((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3], (short)((b[o + 4] << 8) | b[o + 5]),
                            (short)((b[o + 6] << 8) | b[o + 7]), b[o + 8], b[o + 9], b[o + 10], b[o + 11], b[o + 12], b[o + 13], b[o + 14], b[o + 15]);
        }

        public static uint Data1(Guid g) { byte[] b = Bytes(g); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        public static int Data3(Guid g) { byte[] b = Bytes(g); return b[6] << 8 | b[7]; }

        public static ComponentCategories Category(Guid g) { return (ComponentCategories)Data1(g); }

        public static AvatarGender Body(Guid g) { return (AvatarGender)(Data3(g) & 0xF); }

        public static AssetGuidType Kind(Guid g)
        {
            byte[] b = Bytes(g);
            if ((b[8] >> 4) != 0xC) return AssetGuidType.Custom;
            switch ((Data3(g) >> 8) & 0xF)
            {
                case 0: return AssetGuidType.TOC;
                case 1: return AssetGuidType.Awardable;
                case 2: return AssetGuidType.MarketPlace;
                default: return AssetGuidType.Custom;
            }
        }

        /// <summary>Stock: the TOC's kind, or no kind at all.</summary>
        public static bool IsStock(Guid g) { AssetGuidType k = Kind(g); return k == AssetGuidType.TOC || k == AssetGuidType.Custom; }

        /// <summary>A non-stock asset's title id: its last four bytes, lower-case hex.</summary>
        public static string TitleId(Guid g)
        {
            byte[] b = Bytes(g);
            uint n = (uint)(b[12] << 24 | b[13] << 16 | b[14] << 8 | b[15]);
            return Convert.ToString((int)n, 16);
        }

        /// <summary>Whether the GUID ends in the stock namespace's last six bytes.</summary>
        public static bool InStockNamespace(Guid g)
        {
            byte[] b = Bytes(g);
            return b[10] == 0xF1 && b[11] == 0x09 && b[12] == 0xA1 && b[13] == 0x9C && b[14] == 0xB2 && b[15] == 0xE0;
        }
    }
}
