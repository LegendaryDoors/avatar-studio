// Random avatars as XAM makes them for the editor's lineup (XamAvatarGetMetadataRandom, xam 0x81675258).

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio.Avatars
{
    public static class RandomAvatars
    {
        private static readonly Random rng = new Random();
        private static readonly object gate = new object();

        private sealed class Asset { public int Category; public Guid Id; public int RandomBody; public Colorb[][] Sets; }

        private static List<Asset> Assets()
        {
            List<Asset> l = new List<Asset>();
            foreach (TocRecord e in TocAssets.Entries)
            {
                uint c = e.Category;
                if ((e.Flags & 1) == 0 || e.RandomBody == 0 || c == 0 || (c & (c - 1)) != 0 || c == (uint)ComponentCategories.Animation) continue;
                int bit = 0;
                while ((1u << bit) != c) bit++;
                l.Add(new Asset { Category = bit, Id = e.Id, RandomBody = e.RandomBody, Sets = e.ColourSets.Length > 0 ? e.ColourSets : null });
            }
            return l;
        }

        private static float[] Floats(string table, int at, int n)
        {
            byte[] t = ConsoleTables.Get(table);
            if (t == null || at + 4 * n > t.Length) throw new InvalidDataException("the avatar tables are not installed (" + table + ")");
            float[] f = new float[n];
            for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(new byte[] { t[at + 4 * i + 3], t[at + 4 * i + 2], t[at + 4 * i + 1], t[at + 4 * i] }, 0);
            return f;
        }

        /// <summary>`count` random avatars; throws without the tables or the catalogue.</summary>
        public static AvatarManifest[] Create(AvatarGender bodyMask, int count)
        {
            lock (gate) return CreateLocked(bodyMask, count);
        }

        private static AvatarManifest[] CreateLocked(AvatarGender bodyMask, int count)
        {
            byte[] cats = ConsoleTables.Get("random-categories");
            if (cats == null) throw new InvalidDataException("the avatar tables are not installed (random-categories)");
            int nCat = cats.Length / 8;
            uint[] catMask = new uint[nCat];
            byte[,] chance = new byte[nCat, 2];
            for (int i = 0; i < nCat; i++)
            {
                catMask[i] = (uint)(cats[8 * i] << 24 | cats[8 * i + 1] << 16 | cats[8 * i + 2] << 8 | cats[8 * i + 3]);
                chance[i, 0] = cats[8 * i + 4]; chance[i, 1] = cats[8 * i + 5];
            }
            float[] heights = Floats("body-sizes", 0, 5), widths = Floats("body-sizes", 20, 5);
            List<Asset> assets = Assets();
            if (assets.Count == 0) throw new InvalidDataException("the catalogue (TOC) is not installed");

            // the bodies
            int[] perBody = new int[2];
            int left = count, bodies = bodyMask <= AvatarGender.Female ? 1 : 2;
            if (count / 2 / bodies > 0)
                for (int b = 0; b < 2; b++) if (((int)bodyMask & (1 << b)) != 0) { perBody[b] = count / 2 / bodies; left -= perBody[b]; }
            for (int b = 0; b < 2; b++)
            {
                if (((int)bodyMask & (1 << b)) == 0) continue;
                if (--bodies <= 0) { perBody[b] += left; break; }
                int k = rng.Next(left + 1);
                perBody[b] += k; left -= k;
            }
            byte[] body = new byte[count];
            int at = 0;
            for (byte b = 0; b < 2; b++) for (int k = 0; k < perBody[b]; k++) body[at++] = b;

            // the categories
            uint[] required = new uint[2];
            for (int b = 0; b < 2; b++) for (int i = 0; i < nCat; i++) if (chance[i, b] == 100) required[b] |= catMask[i];
            uint[] mask = new uint[count];
            for (int a = 0; a < count; a++)
                for (int i = 0; i < nCat; i++)
                    if (rng.NextDouble() < chance[i, body[a]] / 100.0) mask[a] |= catMask[i];

            // the assets
            int[,] pick = new int[nCat, count];
            BitArray used = new BitArray(assets.Count);
            for (int i = 0; i < nCat; i++)
                for (int a = 0; a < count; a++)
                {
                    pick[i, a] = -1;
                    int bodyBit = 1 << body[a], free = 0;
                    for (int k = 0; k < assets.Count; k++) if (assets[k].Category == i && (assets[k].RandomBody & bodyBit) != 0 && !used[k]) free++;
                    if (free == 0)
                        for (int k = 0; k < assets.Count; k++) if (assets[k].Category == i && (assets[k].RandomBody & bodyBit) != 0) { used[k] = false; free++; }
                    if (free == 0) continue;
                    int r = rng.Next(free);
                    for (int k = 0; k < assets.Count; k++)
                        if (assets[k].Category == i && (assets[k].RandomBody & bodyBit) != 0 && !used[k])
                        {
                            if (r == 0) { used[k] = true; pick[i, a] = k; break; }
                            r--;
                        }
                }

            // sizes and colours: values without repeats until the domain runs out
            byte[] h = Values(heights.Length, count), w = Values(widths.Length, count);
            Colorb[][] palettes = new Colorb[9][];
            byte[][] colour = new byte[9][];
            for (int t = 0; t < 9; t++)
            {
                Colorb[] rp = ConsoleTables.RandomPalette((DynamicColorType)t);
                palettes[t] = rp.Length > 0 ? rp : ConsoleTables.PickerPalette((DynamicColorType)t);
                if (palettes[t].Length == 0) throw new InvalidDataException("the avatar tables are not installed (palettes)");
                colour[t] = Values(palettes[t].Length, count);
            }
            // the order they come out in: XAM's sub_8197FBE0, every order equally likely
            int[] order = new int[count];
            {
                BitArray taken = new BitArray(count);
                for (int a = 0, free = count; a < count; a++)
                {
                    int r = rng.Next(free--), k = 0;
                    for (; k < count; k++) { if (taken[k]) continue; if (r == 0) break; r--; }
                    order[a] = k;
                    taken[k] = true;
                }
            }

            AvatarManifest[] result = new AvatarManifest[count];
            for (int a = 0; a < count; a++)
            {
                AvatarManifest m = new AvatarManifest();
                uint found = 0;
                m.HeightFactor = heights[h[a]];
                m.WidthFactor = widths[w[a]];
                Func<ComponentCategories, Guid> asset = c =>
                {
                    if ((mask[a] & (uint)c) == 0) return Guid.Empty;
                    int bit = 0;
                    while ((1u << bit) != (uint)c) bit++;
                    int k = bit < nCat ? pick[bit, a] : -1;
                    if (k < 0) return Guid.Empty;
                    found |= (uint)c;
                    return assets[k].Id;
                };
                m.SetBlendShape(BlendShapeType.Chin, asset(ComponentCategories.Chin));
                m.SetBlendShape(BlendShapeType.Nose, asset(ComponentCategories.Nose));
                m.SetBlendShape(BlendShapeType.Ear, asset(ComponentCategories.Ears));
                Action<DynamicTextureType, ComponentCategories> tex = (t, c) =>
                {
                    Guid g = asset(c);
                    if (g == Guid.Empty) return;
                    ReplacementTexture r = ReplacementTexture.None;
                    r.TextureAssetId = g;
                    m.SetReplacementTexture(t, r);
                };
                tex(DynamicTextureType.Eye, ComponentCategories.Eyes);
                tex(DynamicTextureType.Eyebrow, ComponentCategories.Eyebrows);
                tex(DynamicTextureType.Mouth, ComponentCategories.Mouth);
                tex(DynamicTextureType.FacialHair, ComponentCategories.FacialHair);
                tex(DynamicTextureType.SkinFeatures, ComponentCategories.FacialOther);
                tex(DynamicTextureType.EyeShadow, ComponentCategories.EyeShadow);

                Colorb skin = palettes[0][colour[0][a]];
                m.SetDynamicColor(DynamicColorType.Skin, skin);
                m.SetDynamicColor(DynamicColorType.Hair, Hair(skin));
                foreach (DynamicColorType t in new[] { DynamicColorType.Iris, DynamicColorType.EyeShadow, DynamicColorType.Mouth, DynamicColorType.SkinFeatures1 })
                    m.SetDynamicColor(t, palettes[(int)t][colour[(int)t][a]]);
                m.SetDynamicColor(DynamicColorType.SkinFeatures2, m.GetDynamicColor(DynamicColorType.SkinFeatures1));
                m.SetDynamicColor(DynamicColorType.Eyebrow, m.GetDynamicColor(DynamicColorType.Hair));
                m.SetDynamicColor(DynamicColorType.FacialHair, m.GetDynamicColor(DynamicColorType.Hair));

                // the models, lowest bit first: the head and body records, then each worn component
                for (int bit = 0; bit < 13; bit++)
                {
                    ComponentCategories c = (ComponentCategories)(1 << bit);
                    if ((mask[a] & (uint)c) == 0) continue;
                    int k = pick[bit, a];
                    if (k < 0) continue;
                    ComponentInfo info = new ComponentInfo { AssetId = assets[k].Id, ComponentMask = (AvatarComponentMasks)(short)c };
                    if (assets[k].Sets != null)
                    {
                        Colorb[] set = assets[k].Sets[rng.Next(assets[k].Sets.Length)];
                        info.CustomColor0 = set[0]; info.CustomColor1 = set[1]; info.CustomColor2 = set[2];
                    }
                    if (c == ComponentCategories.Body) m.BodyComponent = new ComponentDescription(info, Guid.Empty);
                    else if (c == ComponentCategories.Head) m.HeadComponent = new ComponentDescription(info, Guid.Empty);
                    else m.SetComponentInfo(info);
                    found |= (uint)c;
                }
                if ((found & required[body[a]]) != required[body[a]]) throw new InvalidDataException("a random avatar found no asset for a required category");
                result[order[a]] = m;
            }
            return result;
        }

        /// <summary>A value per avatar from 0..n-1, none repeated until all are used (then all again).</summary>
        private static byte[] Values(int n, int count)
        {
            BitArray taken = new BitArray(n);
            int free = n;
            byte[] v = new byte[count];
            for (int a = 0; a < count; a++)
            {
                if (free == 0) { taken.SetAll(false); free = n; }
                int r = rng.Next(free--), k = 0;
                for (; k < n; k++) { if (taken[k]) continue; if (r == 0) break; r--; }
                v[a] = (byte)k;
                taken[k] = true;
            }
            return v;
        }

        private static ConsoleTables.Hair hair;

        /// <summary>Forgets the hair lookup after the setup installs new tables.</summary>
        public static void ResetTables() { hair = null; }

        /// <summary>XAM's sub_81981448: the skin picks one of six hair tables, then any of its colours.</summary>
        private static Colorb Hair(Colorb skin)
        {
            ConsoleTables.Hair h = hair ?? (hair = ConsoleTables.HairLookup());
            if (h == null) throw new InvalidDataException("the avatar tables are not installed (random-hair)");
            uint word = (uint)skin.CompositeArgb;
            uint table = h.DefaultTable;
            int n = h.DefaultCount;
            for (int i = 0; i < h.SkinCount; i++)
                if (ConsoleTables.XamWord(h.SkinPalette + 4u * (uint)i) == word) { table = h.Table[i]; n = h.Count[i]; break; }
            return new Colorb(unchecked((int)ConsoleTables.XamWord(table + 4u * (uint)rng.Next(n))));
        }
    }
}
