// The console's setup wizard look, read in memory from the player's system update.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class SetupArt : IDisposable
    {
        public readonly string Folder;
        public Texture2D Background, Logo, Ring;
        public XuiFace SegoeLight;
        public string Status = "";
        private readonly Dictionary<string, Texture2D> pictures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        // the files each package gives (the names inside the packages)
        private static readonly string[] Dashcomm =
        {
            "oobe_bg.jpg", "XboxLogoHoriz.png", "hubslotshadow.png", "focus_state_white.png", "ico_64x_AllDevices.png", "ico_64x_clock.png",
            "ico_64x_cancel.png", "ico_64x_CustomAvatar.png",
        };
        private static readonly string[] Controlp = { "Legend_A.png", "Legend_B.png", "Legend_X.png", "Legend_Y.png" };
        private const string FlashPackage = "su20076000_00000000", FlashFace = "$flash_SegoeXbox-Light.xtt";

        private SetupArt(string folder) { Folder = folder; }

        /// <summary>A picture by its name without the extension, or null.</summary>
        public Texture2D Named(string name)
        {
            Texture2D t;
            return pictures.TryGetValue(name, out t) ? t : null;
        }

        /// <summary>The folder holding the update's files: `path` or its $SystemUpdate, or null.</summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string c in new[] { path, Path.Combine(path, "$SystemUpdate") })
                if (File.Exists(Path.Combine(c, "dash.xex")) && File.Exists(Path.Combine(c, "AvatarEditor.xex"))
                    && File.Exists(Path.Combine(c, "FFFE07DF00000002")))
                    return c;
            return null;
        }

        // ---- looking before asking --------------------------------------------------------------

        /// <summary>Scripted runs ignore a folder a real run remembered.</summary>
        public static bool IgnoreRemembered;

        private static string Remembered
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "setup.txt"); }
        }

        /// <summary>The update where it would have been put, without searching any drive.</summary>
        public static string LookFirst(out string where)
        {
            where = null;
            string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_UPDATE");
            string found = Resolve(env);
            if (found != null) { where = "AVATAR_STUDIO_UPDATE"; return found; }
            try
            {
                if (!IgnoreRemembered && File.Exists(Remembered))
                    foreach (string line in File.ReadAllLines(Remembered))
                        if (line.StartsWith("update=", StringComparison.Ordinal) && (found = Resolve(line.Substring(7))) != null)
                        { where = "the folder chosen before"; return found; }
            }
            catch (Exception) { }
            found = SetupDownload.Downloaded();
            if (found != null) { where = "downloaded"; return found; }
            found = Resolve(AppDomain.CurrentDomain.BaseDirectory);
            if (found != null) { where = "beside Avatar Studio"; return found; }
            return null;
        }

        /// <summary>Keeps the chosen folder for the next run (never in a scripted run).</summary>
        public static void Remember(string folder)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Remembered));
                File.WriteAllText(Remembered, "# Avatar Studio's setup: the system update folder last chosen\nupdate=" + folder + "\n");
            }
            catch (Exception e) { Console.WriteLine("[studio] setup: could not keep the folder: " + e.Message); }
        }

        // ---- reading -----------------------------------------------------------------------------

        /// <summary>The look from the update in `folder`; a part that fails stays null.</summary>
        public static SetupArt Load(GraphicsDevice device, string folder)
        {
            SetupArt a = new SetupArt(folder);
            List<string> missing = new List<string>();
            try
            {
                byte[] dash = File.ReadAllBytes(Path.Combine(folder, "dash.xex"));
                Dictionary<string, XuizPackage> pk = XuizPackage.All(dash);
                foreach (string n in Dashcomm) a.Take(device, pk, "dashcomm", n, missing);
                foreach (string n in Controlp) a.Take(device, pk, "controlp", n, missing);
            }
            catch (Exception e) { missing.Add("dash.xex (" + e.Message + ")"); }
            try
            {
                byte[] editor = File.ReadAllBytes(Path.Combine(folder, "AvatarEditor.xex"));
                a.Take(device, XuizPackage.All(editor), "MEDIA", "outerRing.png", missing);
            }
            catch (Exception e) { missing.Add("AvatarEditor.xex (" + e.Message + ")"); }
            try
            {
                string flash = Path.Combine(folder, FlashPackage);
                if (File.Exists(flash))
                {
                    StfsPackage p = new StfsPackage(File.ReadAllBytes(flash));
                    StfsEntry e = p.Find(FlashFace);
                    if (e != null) a.SegoeLight = new XuiFace(new XttFace(p.Read(e), FlashFace), null);
                    else missing.Add(FlashFace);
                }
                else missing.Add(FlashPackage);
            }
            catch (Exception e) { missing.Add("the Segoe Light face (" + e.Message + ")"); }
            a.Background = a.Named("oobe_bg");
            a.Logo = a.Named("XboxLogoHoriz");
            a.Ring = a.Named("outerRing");
            a.Status = "setup art <- " + folder + ": " + a.pictures.Count + " pictures" +
                       (a.SegoeLight != null ? ", Segoe Light (" + a.SegoeLight.Base.Glyphs + " glyphs)" : "") +
                       (missing.Count > 0 ? "; missing " + string.Join(", ", missing.ToArray()) : "");
            return a;
        }

        private void Take(GraphicsDevice device, Dictionary<string, XuizPackage> pk, string package, string name, List<string> missing)
        {
            XuizPackage p;
            ArraySegment<byte> bytes;
            if (!pk.TryGetValue(package, out p) || !p.Files.TryGetValue(name, out bytes)) { missing.Add(package + "/" + name); return; }
            try
            {
                Texture2D t;
                using (MemoryStream ms = new MemoryStream(bytes.Array, bytes.Offset, bytes.Count, false))
                    t = Texture2D.FromStream(device, ms);
                TileArt.Premultiply(t);
                pictures[Path.GetFileNameWithoutExtension(name)] = t;
            }
            catch (Exception e) { missing.Add(package + "/" + name + " (" + e.Message + ")"); }
        }

        public void Dispose()
        {
            foreach (Texture2D t in pictures.Values) if (t != null) t.Dispose();
            pictures.Clear();
        }
    }

    /// <summary>One XUIZ package inside an XEX, its files as slices of the XEX.</summary>
    sealed class XuizPackage
    {
        public readonly Dictionary<string, ArraySegment<byte>> Files = new Dictionary<string, ArraySegment<byte>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every package the XEX's resource header names.</summary>
        public static Dictionary<string, XuizPackage> All(byte[] xex)
        {
            Dictionary<string, XuizPackage> named = new Dictionary<string, XuizPackage>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<int, int>> found = new List<KeyValuePair<int, int>>();      // offset, total size
            for (int pos = 0; pos + 0x16 <= xex.Length; pos++)
            {
                if (xex[pos] != 'X' || xex[pos + 1] != 'U' || xex[pos + 2] != 'I' || xex[pos + 3] != 'Z') continue;
                uint ver = U32(xex, pos + 4), total = U32(xex, pos + 8), zero = U32(xex, pos + 12), table = U32(xex, pos + 16);
                int count = U16(xex, pos + 20);
                if (ver == 3 && zero == 0 && count > 0 && count < 100000 && table > 0 && table < total && total <= xex.Length - pos)
                    found.Add(new KeyValuePair<int, int>(pos, (int)total));
            }
            // the resource header (key 0x2FF): name, virtual address, size
            List<KeyValuePair<string, uint>> resources = new List<KeyValuePair<string, uint>>();
            Dictionary<uint, uint> sizeOf = new Dictionary<uint, uint>();
            if (xex.Length > 0x18 && xex[0] == 'X' && xex[1] == 'E' && xex[2] == 'X' && xex[3] == '2')
            {
                int headers = (int)U32(xex, 0x14);
                for (int i = 0; i < headers; i++)
                {
                    uint key = U32(xex, 0x18 + i * 8), val = U32(xex, 0x1C + i * 8);
                    if (key != 0x2FF) continue;
                    int n = ((int)U32(xex, (int)val) - 4) / 16;
                    for (int j = 0; j < n; j++)
                    {
                        int p = (int)val + 4 + j * 16;
                        string name = System.Text.Encoding.ASCII.GetString(xex, p, 8).TrimEnd('\0');
                        uint addr = U32(xex, p + 8);
                        resources.Add(new KeyValuePair<string, uint>(name, addr));
                        sizeOf[addr] = U32(xex, p + 12);
                    }
                }
            }
            // address - offset: one constant, from a resource whose size only one package has
            long delta = long.MinValue;
            foreach (KeyValuePair<string, uint> r in resources)
            {
                int matches = 0, at = 0;
                foreach (KeyValuePair<int, int> f in found) if (f.Value == sizeOf[r.Value]) { matches++; at = f.Key; }
                if (matches == 1) { delta = (long)r.Value - at; break; }
            }
            if (delta == long.MinValue) return named;
            foreach (KeyValuePair<string, uint> r in resources)
            {
                long off = r.Value - delta;
                foreach (KeyValuePair<int, int> f in found)
                    if (f.Key == off) { named[r.Key] = Read(xex, f.Key); break; }
            }
            return named;
        }

        private static XuizPackage Read(byte[] x, int b)
        {
            XuizPackage pk = new XuizPackage();
            int count = U16(x, b + 20), dataStart = b + 0x16 + (int)U32(x, b + 16), p = b + 0x16;
            for (int i = 0; i < count; i++)
            {
                int size = (int)U32(x, p), off = (int)U32(x, p + 4), len = x[p + 8];
                string name = System.Text.Encoding.UTF8.GetString(x, p + 9, len).Replace('\\', '/');
                p += 9 + len;
                if (dataStart + off + size <= x.Length) pk.Files[name] = new ArraySegment<byte>(x, dataStart + off, size);
            }
            return pk;
        }

        private static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }
    }
}
