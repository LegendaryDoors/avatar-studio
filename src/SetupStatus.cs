// What the player has installed, part by part, for the setup screen.

using System;
using System.Collections.Generic;
using System.IO;

namespace AvatarStudio
{
    sealed class SetupPart
    {
        public string Name;          // what it is, for the player
        public string Source;        // where it comes from
        public bool Installed;
        public string Detail;        // what was found, or what is missing
    }

    static class SetupStatus
    {
        /// <summary>The parts, in the order the studio's look depends on them.</summary>
        public static List<SetupPart> Read(string assetDir)
        {
            List<SetupPart> parts = new List<SetupPart>();
            bool items = assetDir != null && File.Exists(Path.Combine(assetDir, "tocassetdependencies.strb"));
            parts.Add(new SetupPart { Name = "Avatar items", Source = "Microsoft's server", Installed = items,
                                      Detail = items ? Count(assetDir, "*") + " files" : "not downloaded yet" });

            bool toc = File.Exists(AvatarToc.InstalledPath);
            parts.Add(new SetupPart { Name = "Avatar catalogue", Source = "your system update", Installed = toc,
                                      Detail = toc ? "AvatarAssetPack.toc" : "not installed" });

            string tiles = TileArt.Find();
            bool art = tiles != null && File.Exists(Path.Combine(tiles, "redirects.txt")) && File.Exists(Path.Combine(tiles, "legend.txt"));
            bool bg = tiles != null && File.Exists(Path.Combine(tiles, "background.jpg"));
            parts.Add(new SetupPart { Name = "Editor art, legend and backdrop", Source = "your system update", Installed = art && bg,
                                      Detail = tiles == null ? "not installed" : Count(tiles, "*.png") + " pictures" + (bg ? "" : ", no backdrop") });

            int anims = Count(EditorAnimations.Folder, "*");
            parts.Add(new SetupPart { Name = "Editor animations", Source = "your system update", Installed = anims > 0,
                                      Detail = anims > 0 ? anims + " animations" : "not installed" });

            int sounds = Count(Sounds.Folder, "*.wav");
            parts.Add(new SetupPart { Name = "Editor sounds and music", Source = "your system update", Installed = sounds > 0,
                                      Detail = sounds > 0 ? sounds + " sounds" : "not installed" });

            bool font = File.Exists(Path.Combine(ConsoleFace.Folder, "xenonjklatin.xtt")) && File.Exists(Path.Combine(ConsoleFace.Folder, "xenonjklatin.xttp"));
            parts.Add(new SetupPart { Name = "Console font", Source = "your font file", Installed = font,
                                      Detail = font ? "xenonjklatin.xtt, patched" : "not installed" });

            bool light = File.Exists(Path.Combine(LibraryShaders.Folder, "AvatarLibrary.fxb"));
            parts.Add(new SetupPart { Name = "Console lighting", Source = "your system update", Installed = light,
                                      Detail = light ? "AvatarLibrary.fxb" : "not installed" });

            // appended, so earlier parts keep their places; tables from an older build count as missing
            bool tables = File.Exists(AvatarStudio.Avatars.ConsoleTables.InstalledPath) && AvatarStudio.Avatars.ConsoleTables.Complete;
            parts.Add(new SetupPart { Name = "Avatar tables", Source = "your system update", Installed = tables,
                                      Detail = tables ? "console-tables.bin" : File.Exists(AvatarStudio.Avatars.ConsoleTables.InstalledPath) ? "an older set" : "not installed" });
            return parts;
        }

        /// <summary>True when every part the setup installs is in (the console font is optional).</summary>
        public static bool Complete(List<SetupPart> parts)
        {
            for (int i = 0; i < parts.Count; i++) if (i != 5 && !parts[i].Installed) return false;
            return true;
        }

        private static int Count(string dir, string pattern)
        {
            try { return dir != null && Directory.Exists(dir) ? Directory.GetFiles(dir, pattern).Length : 0; }
            catch (Exception) { return 0; }
        }
    }
}
