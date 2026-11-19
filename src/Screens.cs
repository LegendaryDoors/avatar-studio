// The editor's category tree and colour palettes, as the 2010 editor arranges them.

using System;
using System.Collections.Generic;
using System.Reflection;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    enum NodeKind { Submenu, Items, Remove, Color, BodySize, Outfits }

    sealed class Node
    {
        public string Name;
        public NodeKind Kind;
        public List<Node> Children = new List<Node>();

        // Items: which category to list and whether it has a colour follow-on
        public ComponentCategories Category;
        public bool MaleOnly;
        public DynamicColorType ColorForItems = DynamicColorType.Count;   // Count = no per-item colour

        // a Color node's colour and header (Strings.xus 124-140; not always the tile's name + Color)
        public DynamicColorType Color = DynamicColorType.Count;
        public string Title;

        // the remove cell's kind, on a colour screen that can take the feature off (Eye Shadow)
        public AvatarManifestEditor.RemovableComponents Remove = AvatarManifestEditor.RemovableComponents.NotRemovable;

        // TileArt key when the name is ambiguous (the Color submenu's Eyes and Eyebrows)
        public string Art;

        // the Color submenu entry's index in AE_ColorMenu_ctor (0x920E9CB8); -1 elsewhere
        public int ColorMenuIndex = -1;
        public bool Enabled = true;

        public Node(string name, NodeKind kind) { Name = name; Kind = kind; }

        public static Node Sub(string name, params Node[] kids)
        {
            Node n = new Node(name, NodeKind.Submenu);
            n.Children.AddRange(kids);
            return n;
        }
        public static Node Items(string name, ComponentCategories cat, DynamicColorType colour = DynamicColorType.Count, bool maleOnly = false)
        {
            Node n = new Node(name, NodeKind.Items) { Category = cat, ColorForItems = colour, MaleOnly = maleOnly };
            return n;
        }
        public static Node Colour(string name, DynamicColorType c, string title, string art = null)
        {
            return new Node(name, NodeKind.Color) { Color = c, Title = title, Art = art };
        }
    }

    static class Tree
    {
        // Change My Features
        public static Node Features()
        {
            return Node.Sub("My Features",
                Node.Items("Hairstyles", ComponentCategories.Hair, DynamicColorType.Hair),
                Node.Sub("Eyes and Eyebrows",
                    Node.Items("Eyes", ComponentCategories.Eyes, DynamicColorType.Iris),
                    Node.Items("Eyebrows", ComponentCategories.Eyebrows, DynamicColorType.Eyebrow)),
                Node.Sub("Face",                                    // "Facial Features" for female bodies
                    Node.Items("Facial Hair", ComponentCategories.FacialHair, DynamicColorType.FacialHair, maleOnly: true),
                    Node.Items("Facial Features", ComponentCategories.FacialOther, DynamicColorType.SkinFeatures1),
                    EyeShadow()),
                new Node("Body Size", NodeKind.BodySize),
                Node.Items("Ears", ComponentCategories.Ears),
                Node.Items("Nose", ComponentCategories.Nose),
                Node.Sub("Mouth and Chin",
                    Node.Items("Mouth", ComponentCategories.Mouth, DynamicColorType.Mouth),
                    Node.Items("Chin", ComponentCategories.Chin)),
                ColorMenu());
        }

        /// <summary>The Color submenu as AE_ColorMenu_ctor builds it: eight tiles, each with its screen.</summary>
        public static Node ColorMenu()
        {
            Node eyeShadow = Node.Colour("Eye Shadow Color", DynamicColorType.EyeShadow, "Eye Shadow Color", "Color/EyeShadow");
            eyeShadow.Remove = AvatarManifestEditor.RemovableComponents.EyeShadow;
            Node[] kids =
            {
                Node.Colour("Hair Color", DynamicColorType.Hair, "Hair Color", "Color/Hair"),                               // 56, label 137
                Node.Colour("Facial Feature Color", DynamicColorType.SkinFeatures1, "Facial Feature Color", "Color/Facial"), // 63, 129
                Node.Colour("Eyebrow Color", DynamicColorType.Eyebrow, "Eyebrow Color", "Color/Eyebrows"),                  // 60, 131
                Node.Colour("Lip Color", DynamicColorType.Mouth, "Lip Color", "Color/Mouth"),                               // 57, 139
                eyeShadow,                                                                                                  // 61, 133
                Node.Colour("Facial Hair Color", DynamicColorType.FacialHair, "Facial Hair Color", "Color/FacialHair"),     // 58, 126
                Node.Colour("Eye Color", DynamicColorType.Iris, "Eye Color", "Color/Eyes"),                                 // 59, 135
                Node.Colour("Skin Color", DynamicColorType.Skin, "Skin Color", "Color/Skin"),                               // 55, 140
            };
            for (int i = 0; i < kids.Length; i++) kids[i].ColorMenuIndex = i;
            return Node.Sub("Color", kids);
        }

        /// <summary>Eye Shadow is a colour, not a texture list: picking a colour switches the shadow on.</summary>
        private static Node EyeShadow()
        {
            Node n = Node.Colour("Eye Shadow", DynamicColorType.EyeShadow, "Eye Shadow Color");
            n.Remove = AvatarManifestEditor.RemovableComponents.EyeShadow;
            return n;
        }

        // Change My Style
        public static Node Style()
        {
            return Node.Sub("My Style",
                Node.Items("Tops", ComponentCategories.Shirt),
                Node.Items("Headwear", ComponentCategories.Hat),
                Node.Sub("Accessories",
                    Node.Items("Glasses", ComponentCategories.Glasses),
                    Node.Items("Wristwear", ComponentCategories.Wristwear),
                    Node.Items("Gloves", ComponentCategories.Gloves),
                    Node.Items("Rings", ComponentCategories.Ring),
                    Node.Items("Earrings", ComponentCategories.Earrings)),
                Node.Items("Props", ComponentCategories.Carryable),
                Node.Items("Bottoms", ComponentCategories.Trousers),
                Node.Items("Shoes", ComponentCategories.Shoes),
                Node.Items("Dress Up", ComponentCategories.Costume),
                new Node("My Outfits", NodeKind.Outfits));
        }
    }

    static class Palettes
    {
        private static Colorb[][] table;

        public static Colorb[] For(DynamicColorType t)
        {
            if (table == null)
            {
                table = new Colorb[(int)DynamicColorType.Count][];
                for (int i = 0; i < table.Length; i++) table[i] = ConsoleTables.PickerPalette((DynamicColorType)i);
            }
            if (table != null && (int)t < table.Length && table[(int)t] != null && table[(int)t].Length > 0)
                return table[(int)t];
            return new Colorb[] { new Colorb(255, 255, 255), new Colorb(0, 0, 0) };   // never empty
        }
    }
}
