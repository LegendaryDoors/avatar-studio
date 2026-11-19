// The avatar games use: gamertag.txt and avatar.bin in the studio's player folder, which PC ports of
// avatar games read. The studio names no game; a port finds this folder by itself.

using System;
using System.IO;
using System.Text;

namespace AvatarStudio
{
    sealed class PlayerAvatar : IGameTarget
    {
        public string Name { get { return "Your Games"; } }
        public string ShortName { get { return "Games"; } }

        /// <summary>AVATAR_STUDIO_PLAYER, else %LOCALAPPDATA%\Avatar Studio\player.</summary>
        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_PLAYER");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "player");
            }
        }

        public bool IsPresent { get { return true; } }
        public bool CanLaunch { get { return false; } }

        private static byte[] ReadManifest()
        {
            try
            {
                string p = Path.Combine(Folder, "avatar.bin");
                if (!File.Exists(p)) return null;
                byte[] raw = File.ReadAllBytes(p);
                if (raw.Length != Avatars.AvatarManifest.Size && raw.Length != 1021) return null;
                byte[] m = new Avatars.AvatarDescription(raw).Description;
                return Avatars.AvatarManifest.Create(m) != null ? m : null;
            }
            catch (Exception) { return null; }
        }

        private static string ReadName()
        {
            try
            {
                string p = Path.Combine(Folder, "gamertag.txt");
                if (!File.Exists(p)) return null;
                foreach (string line in File.ReadAllLines(p, Encoding.UTF8)) if (line.Trim().Length > 0) return line.Trim();
            }
            catch (Exception) { }
            return null;
        }

        public string CurrentName
        {
            get { return ReadManifest() != null ? (ReadName() ?? "Player") : null; }
        }

        public byte[] CurrentManifest { get { return ReadManifest(); } }

        /// <summary>The name as gamertag.txt (UTF-8, one line) and the 1000-byte manifest as avatar.bin.</summary>
        public void Apply(string name, byte[] manifest)
        {
            string dir = Folder;
            Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(name)) File.WriteAllText(Path.Combine(dir, "gamertag.txt"), name.Trim() + Environment.NewLine, new UTF8Encoding(false));
            if (manifest != null)
            {
                byte[] m = new Avatars.AvatarDescription(manifest).Description;
                if (m.Length != Avatars.AvatarManifest.Size || Avatars.AvatarManifest.Create(m) == null)
                    throw new ArgumentException("avatar bytes do not parse as a manifest", "manifest");
                File.WriteAllBytes(Path.Combine(dir, "avatar.bin"), m);
            }
            Console.WriteLine("[studio] " + Name + ": wrote " + dir);
        }

        public bool Launch() { return false; }

        public string Status
        {
            get
            {
                string who = CurrentName;
                return who == null ? "no avatar set" : "playing as " + who;
            }
        }
    }
}
