// What can be worn: the console's catalogue plus the items in the item folders.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class CatalogItem
    {
        public Guid Id;
        public string Name;
        public ComponentCategories Category;   // the single category bit (or costume combination)
        public AvatarGender Body;
        public bool Stock;
        public string TitleId;                  // awarding title for non-stock
        public string IconPath;                 // optional <guid>.png
        public bool OnDisk;
        public bool Dependent;                  // another asset's TOC dependent: worn only through it
        public TocEntry Toc;   // its catalogue entry, when there is one
    }

    sealed class Catalog
    {
        public readonly List<CatalogItem> Items = new List<CatalogItem>();
        public string AssetDir { get; private set; }
        private readonly Dictionary<Guid, CatalogItem> byId = new Dictionary<Guid, CatalogItem>();

        public Catalog(string assetDir)
        {
            AssetDir = assetDir;
            HashSet<Guid> seen = new HashSet<Guid>();
            foreach (TocRecord r in TocAssets.Entries)
            {
                if (!TocAssets.Has(r.Id) || !seen.Add(r.Id)) continue;
                CatalogItem it = Make(r.Id, true);
                it.Name = "Item";                         // the TOC's display name replaces it below
                Add(it);
            }
            if (assetDir != null && Directory.Exists(assetDir))
            {
                foreach (string path in Directory.GetFiles(assetDir, "*.strb"))
                {
                    string stem = Path.GetFileNameWithoutExtension(path);
                    Guid g;
                    if (!TryParseGuid(stem, out g) || seen.Contains(g)) continue;
                    CatalogItem it = Make(g, false);
                    string txt = Path.Combine(assetDir, stem + ".txt");
                    AssetGuidType kind = AssetIds.Kind(g);
                    it.Name = File.Exists(txt) ? File.ReadAllText(txt).Trim()
                        : kind == AssetGuidType.Awardable ? "Award item"
                        : kind == AssetGuidType.MarketPlace ? "Marketplace item" : "Item";
                    Add(it); seen.Add(g);
                }
            }
            HashSet<Guid> dependents = Dependents();
            foreach (CatalogItem it in Items)
            {
                it.OnDisk = TocAssets.Has(it.Id) ||
                            assetDir != null && File.Exists(Path.Combine(assetDir, it.Id.ToString() + ".strb"));
                string png = assetDir == null ? null : Path.Combine(assetDir, it.Id.ToString() + ".png");
                if (png != null && File.Exists(png)) it.IconPath = png;
                it.Dependent = dependents.Contains(it.Id);
                // the console's own display name, from the catalogue
                it.Toc = AvatarToc.Find(it.Id);
                if (it.Toc != null && !string.IsNullOrEmpty(it.Toc.Name)) it.Name = it.Toc.Name;
            }
            Console.WriteLine("[studio] catalog: " + Items.Count + " assets, " + dependents.Count + " named as dependents in tocassetdependencies (not listed)");
        }

        /// <summary>Assets the dependency table swaps in for others; nothing is filtered without the table.</summary>
        private static HashSet<Guid> Dependents()
        {
            HashSet<Guid> set = new HashSet<Guid>();
            int n = 0;
            foreach (AvatarAssetDependency d in Dependencies.Records())
            {
                n++;
                if (d != null && d.DependentAssetId != Guid.Empty) set.Add(d.DependentAssetId);
            }
            if (n == 0) Console.WriteLine("[studio] no dependency table; hat hairs and eye shadows will be listed");
            return set;
        }

        private void Add(CatalogItem it) { Items.Add(it); byId[it.Id] = it; }

        private static CatalogItem Make(Guid g, bool stock)
        {
            CatalogItem it = new CatalogItem();
            it.Id = g;
            it.Stock = stock;
            it.Category = AssetIds.Category(g);
            it.Body = AssetIds.Body(g);
            if (!AssetIds.IsStock(g)) it.TitleId = AssetIds.TitleId(g);
            return it;
        }

        public CatalogItem Find(Guid g) { CatalogItem it; return byId.TryGetValue(g, out it) ? it : null; }

        /// <summary>Items of exactly `bit`, or for costumes any with the Costume bit; filtered by body.</summary>
        public List<CatalogItem> For(ComponentCategories bit, AvatarGender body, bool onDiskOnly)
        {
            List<CatalogItem> list = new List<CatalogItem>();
            List<CatalogItem> other = new List<CatalogItem>();
            foreach (CatalogItem it in Items)
            {
                bool match = bit == ComponentCategories.Costume
                    ? (it.Category & ComponentCategories.Costume) != 0
                    : it.Category == bit;
                if (!match) continue;
                if (it.Dependent) continue;
                if ((it.Body & body) == 0) continue;
                if (onDiskOnly && !it.OnDisk) continue;
                (it.Stock ? list : other).Add(it);
            }
            // the editor's order: the catalogue's sort key, then the name (0x922184C0; key 0 last)
            if (AvatarToc.Loaded)
            {
                List<CatalogItem> known = new List<CatalogItem>(), unknown = new List<CatalogItem>();
                foreach (CatalogItem it in list) (it.Toc != null ? known : unknown).Add(it);
                Dictionary<CatalogItem, int> at = new Dictionary<CatalogItem, int>();
                for (int i = 0; i < known.Count; i++) at[known[i]] = i;
                known.Sort(delegate(CatalogItem a, CatalogItem b)
                {
                    int c = a.Toc.SortKey.CompareTo(b.Toc.SortKey);
                    if (c == 0) c = string.CompareOrdinal(a.Toc.Name ?? "", b.Toc.Name ?? "");
                    return c != 0 ? c : at[a].CompareTo(at[b]);
                });
                known.AddRange(unknown);
                list = known;
            }
            list.AddRange(other);
            return list;
        }

        public static string SplitWords(string ident)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ident.Length; i++)
            {
                char c = ident[i];
                if (i > 0 && (char.IsUpper(c) || char.IsDigit(c) && !char.IsDigit(ident[i - 1])) && ident[i - 1] != ' ')
                    sb.Append(' ');
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool TryParseGuid(string s, out Guid g)
        {
            g = Guid.Empty;
            if (s.Length != 36) return false;
            try { g = new Guid(s); return true; } catch { return false; }
        }
    }
}
