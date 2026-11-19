// Where the avatar engine gets its data: the console's catalogue, then the item folders.

using System;
using System.IO;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    static class AvatarStack
    {
        public static string Status { get; private set; }
        private static string assetDir, itemsDir;

        /// <summary>The engine can build an avatar: the tables, and the catalogue or a stock folder.</summary>
        public static bool Ready { get { return ConsoleTables.Installed && (TocAssets.Count > 0 || assetDir != null); } }

        /// <summary>Non-stock items: AVATAR_STUDIO_ITEMS, else %LOCALAPPDATA%\Avatar Studio\items.</summary>
        public static string ItemsDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_ITEMS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "items");
            }
        }

        public static void Bind(string stockDir)
        {
            assetDir = stockDir;
            itemsDir = ItemsDir;
            bool toc = !string.Equals(Environment.GetEnvironmentVariable("AVATAR_STUDIO_TOC"), "off", StringComparison.OrdinalIgnoreCase);
            TocAssets.PathSource = () => toc ? AvatarToc.InstalledPath : null;
            TocAssets.Reset();
            AssetStore.Source = Get;
            Dependencies.Source = () =>
            {
                string p = assetDir == null ? null : Path.Combine(assetDir, "tocassetdependencies.strb");
                return p != null && File.Exists(p) ? File.ReadAllBytes(p) : null;
            };
            Dependencies.Reset();
            ConsoleTables.Reload();
            Status = "tables " + (ConsoleTables.Installed ? "installed" : "MISSING") + ", catalogue " + (TocAssets.Count > 0 ? TocAssets.Count + " assets" : "none")
                   + ", stock folder " + (assetDir ?? "none");
            Console.WriteLine("[studio] avatar engine: " + Status);
        }

        private static byte[] Get(Guid g)
        {
            byte[] b = TocAssets.Get(g);
            if (b != null) return b;
            foreach (string dir in new[] { assetDir, itemsDir })
            {
                if (dir == null) continue;
                string p = Path.Combine(dir, g.ToString("D") + ".strb");
                try { if (File.Exists(p)) return File.ReadAllBytes(p); }
                catch (IOException) { }
            }
            return null;
        }

        public static AvatarDescription Describe(AvatarManifest manifest)
        {
            return new AvatarDescription(manifest.SaveToBinary());
        }

        public static AvatarManifestEditor Editor(AvatarManifest manifest)
        {
            return new AvatarManifestEditor(manifest);
        }

        /// <summary>A body and head only, until the setup has installed the rest.</summary>
        public static AvatarManifest Placeholder()
        {
            AvatarManifest m = new AvatarManifest();
            m.BodyComponent = new ComponentDescription(new ComponentInfo(ConsoleTables.MaleBody, AvatarComponentMasks.Body, new Colorb(0), new Colorb(0), new Colorb(0)), Guid.Empty);
            m.HeadComponent = new ComponentDescription(new ComponentInfo(new Guid("00000001-0002-0003-c1c8-f109a19cb2e0"), AvatarComponentMasks.Head, new Colorb(0), new Colorb(0), new Colorb(0)), Guid.Empty);
            return m;
        }

        /// <summary>`count` random avatars, or placeholders before the setup.</summary>
        public static AvatarManifest[] Random(AvatarGender body, int count)
        {
            try { return RandomAvatars.Create(body, count); }
            catch (Exception e)
            {
                Console.WriteLine("[studio] random avatars unavailable (" + e.Message + "): placeholders");
                AvatarManifest[] r = new AvatarManifest[count];
                for (int i = 0; i < count; i++) r[i] = Placeholder();
                return r;
            }
        }
    }
}
