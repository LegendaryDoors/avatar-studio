// An avatar's saved outfits (My Outfits): the worn manifest, as the console's store keeps it.
// On disk (ours): <name>.outfits\<seq>.outfit, the 1000-byte manifest, then the name in UTF-8.
// Wearing one swaps the clothes; the body, head, hair and face stay (sub_920DA1E8).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class Outfit
    {
        public int Seq;                                         // [ours] the file's place in the list
        public string Name;                                     // the console's display name
        public byte[] Manifest;                                 // 1000 bytes, the worn manifest as saved
        public string File;                                     // [ours] its file, null until written
        /// <summary>Strings.xus 100 "Wear %s": the tile's label and tooltip.</summary>
        public string WearLabel { get { return "Wear " + Name; } }
    }

    sealed class OutfitStore
    {
        public const int Max = 99;                              // sub_920BD800: count < 0x63
        public const int NameBuffer = 21;                       // XamShowKeyboardUI's cchResultText (sub_920D4FC0)
        /// <summary>[inferred] the buffer's 21 less its terminator</summary>
        public const int MaxName = NameBuffer - 1;
        private const string Prefix = "Outfit";                  // Strings.xus 174
        public readonly List<Outfit> Items = new List<Outfit>();
        private string dir;                                     // null: not on disk yet

        /// <summary>The store's counter (+180656).</summary>
        public int Counter { get; private set; }

        /// <summary>AE_OutfitStore_Add's name for a new outfit: "Outfit" and the counter + 1.</summary>
        public string NextName { get { return Prefix + " " + (Counter + 1).ToString(CultureInfo.InvariantCulture); } }

        /// <summary>"Hero.avatar" -> "Hero.outfits", beside it.</summary>
        public static string DirFor(string avatarPath)
        {
            if (avatarPath == null) return null;
            return Path.Combine(Path.GetDirectoryName(avatarPath), Path.GetFileNameWithoutExtension(avatarPath) + ".outfits");
        }

        /// <summary>The outfits of the avatar at `avatarPath` (null: a new avatar, none yet).</summary>
        public void Open(string avatarPath)
        {
            Items.Clear();
            Counter = 0;
            dir = DirFor(avatarPath);
            if (dir == null || !Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.outfit"))
            {
                Outfit o = Read(f);
                if (o == null) { Console.WriteLine("[studio] outfits: skipped " + f); continue; }
                Items.Add(o);
            }
            Items.Sort(delegate(Outfit a, Outfit b) { return a.Seq.CompareTo(b.Seq); });
            UpdateCounter();
        }

        /// <summary>"<seq>.outfit" (manifest, then UTF-8 name), or an older "Outfit N.outfit" (bare manifest).</summary>
        private static Outfit Read(string f)
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            byte[] raw = System.IO.File.ReadAllBytes(f);
            if (raw.Length < AvatarLibrary.ManifestLength) return null;
            byte[] m = new byte[AvatarLibrary.ManifestLength];
            Buffer.BlockCopy(raw, 0, m, 0, m.Length);
            if (!AvatarLibrary.IsValid(m)) return null;
            int seq;
            if (int.TryParse(stem, NumberStyles.None, CultureInfo.InvariantCulture, out seq))
            {
                string name = System.Text.Encoding.UTF8.GetString(raw, m.Length, raw.Length - m.Length);
                return new Outfit { Seq = seq, Name = name, Manifest = m, File = f };
            }
            if (stem.StartsWith(Prefix + " ", StringComparison.Ordinal) &&
                int.TryParse(stem.Substring(Prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out seq) &&
                raw.Length == m.Length)
                return new Outfit { Seq = seq, Name = stem, Manifest = m, File = f };
            return null;
        }

        /// <summary>sub_920BDDA0: the counter up to the largest number sub_920BDA68 finds in a name.</summary>
        private void UpdateCounter()
        {
            foreach (Outfit o in Items)
            {
                int n = NameNumber(o.Name);
                if (n > Counter) Counter = n;
            }
        }

        /// <summary>sub_920BDA68: the name's trailing number when it begins with "Outfit", else -1.</summary>
        public static int NameNumber(string name)
        {
            if (name == null || name.Length <= Prefix.Length || string.CompareOrdinal(name, 0, Prefix, 0, Prefix.Length) != 0) return -1;
            int i = name.Length;
            while (i > 0 && name[i - 1] >= '0' && name[i - 1] <= '9') i--;
            int v;
            return int.TryParse(name.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        /// <summary>The avatar now has a file: outfits kept in memory are written beside it.</summary>
        public void Attach(string avatarPath)
        {
            string want = DirFor(avatarPath);
            if (want == null || string.Equals(want, dir, StringComparison.OrdinalIgnoreCase)) { dir = want ?? dir; return; }
            dir = want;
            foreach (Outfit o in Items)
            {
                string moved = o.File == null ? null : Path.Combine(dir, Path.GetFileName(o.File));
                if (moved != null && System.IO.File.Exists(moved)) o.File = moved;    // moved with a rename
                else { o.File = null; Write(o); }
            }
        }

        public bool HasRoom { get { return Items.Count < Max; } }

        /// <summary>AE_OutfitStore_Add: the worn manifest, last in the list, written.</summary>
        public Outfit Add(byte[] manifest, string name)
        {
            int seq = 0;
            foreach (Outfit o in Items) seq = Math.Max(seq, o.Seq);
            Outfit added = new Outfit { Seq = seq + 1, Name = name ?? "", Manifest = (byte[])manifest.Clone() };
            Items.Add(added);
            Write(added);
            UpdateCounter();
            return added;
        }

        /// <summary>The delete (sub_920BDB10); the counter is left alone.</summary>
        public void Delete(Outfit o)
        {
            Items.Remove(o);
            if (o.File != null && System.IO.File.Exists(o.File)) System.IO.File.Delete(o.File);
        }

        /// <summary>sub_920BD820: the saved outfit the worn manifest already is, or -1.</summary>
        public int Find(byte[] worn)
        {
            for (int i = 0; i < Items.Count; i++) if (OutfitOps.Same(Items[i].Manifest, worn)) return i;
            return -1;
        }

        private void Write(Outfit o)
        {
            if (dir == null) return;
            Directory.CreateDirectory(dir);
            if (o.File == null) o.File = Path.Combine(dir, o.Seq.ToString("D3", CultureInfo.InvariantCulture) + ".outfit");
            byte[] name = System.Text.Encoding.UTF8.GetBytes(o.Name ?? "");
            byte[] all = new byte[o.Manifest.Length + name.Length];
            Buffer.BlockCopy(o.Manifest, 0, all, 0, o.Manifest.Length);
            Buffer.BlockCopy(name, 0, all, o.Manifest.Length, name.Length);
            System.IO.File.WriteAllBytes(o.File, all);
        }
    }

    static class OutfitOps
    {
        private const int ColoursAt = 252, BodyAt = 288, ComponentsAt = 352, ComponentSize = 32, Slots = 13;

        /// <summary>sub_920DA1E8: the outfit's clothes put on; the body, head, hair and face stay.</summary>
        public static void Wear(AvatarManifest avatar, AvatarManifest outfit)
        {
            avatar.RemoveComponents(AvatarComponentMasks.All & ~AvatarComponentMasks.Hair);   // 8187
            foreach (ComponentDescription c in new List<ComponentDescription>(outfit.Components))
                if (c.Info.ComponentMask != AvatarComponentMasks.Hair)
                    avatar.SetComponentInfo(c.Info);
            avatar.DynamicColors[(int)DynamicColorType.EyeShadow] = outfit.DynamicColors[(int)DynamicColorType.EyeShadow];
            avatar.DynamicColors[(int)DynamicColorType.Mouth] = outfit.DynamicColors[(int)DynamicColorType.Mouth];
            ReplacementTexture shadow = outfit.GetReplacementTexture(DynamicTextureType.EyeShadow);
            if (shadow.TextureAssetId == Guid.Empty)
            {
                avatar.SetReplacementTexture(DynamicTextureType.EyeShadow, new ReplacementTexture());
                return;
            }
            Guid eye = avatar.GetReplacementTexture(DynamicTextureType.Eye).TextureAssetId;
            AvatarAssetDependency d = Dependencies.Get(eye);
            if (d == null || d.DependentAssetId == Guid.Empty) return;
            shadow.TextureAssetId = d.DependentAssetId;
            avatar.SetReplacementTexture(DynamicTextureType.EyeShadow, shadow);
        }

        /// <summary>sub_920BD820's test on two manifests: the same body, colours and clothes but hair.</summary>
        public static bool Same(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length < 1000 || b.Length < 1000) return false;
            if (IsMale(a) != IsMale(b)) return false;
            foreach (int c in new[] { (int)DynamicColorType.Mouth, (int)DynamicColorType.EyeShadow })
                for (int k = 0; k < 4; k++) if (a[ColoursAt + 4 * c + k] != b[ColoursAt + 4 * c + k]) return false;
            List<byte[]> ca = Worn(a), cb = Worn(b);
            if (ca.Count != cb.Count) return false;
            foreach (byte[] e in cb)
            {
                bool found = false;
                foreach (byte[] f in ca) if (AvatarLibrary.SameBytes(e, f)) { found = true; break; }
                if (!found) return false;
            }
            return true;
        }

        /// <summary>sub_920BE090: the GUIDs of the manifest's worn components; empty when unreadable.</summary>
        public static List<Guid> ComponentIds(byte[] manifest)
        {
            List<Guid> ids = new List<Guid>();
            AvatarManifest m;
            try { m = (AvatarManifest)AvatarManifest.Create(manifest); }
            catch (Exception) { return ids; }
            foreach (ComponentDescription c in m.Components) ids.Add(c.Info.AssetId);
            return ids;
        }

        /// <summary>sub_920DA168: the body is MaleBody (0x92000BA0, 00000002-0000-0001-c1c8-f109a19cb2e0).</summary>
        private static bool IsMale(byte[] m)
        {
            byte[] male = { 0, 0, 0, 2, 0, 0, 0, 1, 0xC1, 0xC8, 0xF1, 0x09, 0xA1, 0x9C, 0xB2, 0xE0 };
            for (int i = 0; i < 16; i++) if (m[BodyAt + i] != male[i]) return false;
            return true;
        }

        /// <summary>The worn components but a Hair one, as 32-byte entries (up to the first empty GUID).</summary>
        private static List<byte[]> Worn(byte[] m)
        {
            List<byte[]> list = new List<byte[]>();
            for (int i = 0; i < Slots; i++)
            {
                int at = ComponentsAt + ComponentSize * i;
                bool empty = true;
                for (int k = 0; k < 16; k++) if (m[at + k] != 0) { empty = false; break; }
                if (empty) break;
                int mask = (m[at + 16] << 8) | m[at + 17];       // i16, big-endian
                if (mask == (int)AvatarComponentMasks.Hair) continue;
                byte[] e = new byte[ComponentSize];
                Buffer.BlockCopy(m, at, e, 0, ComponentSize);
                list.Add(e);
            }
            return list;
        }
    }
}
