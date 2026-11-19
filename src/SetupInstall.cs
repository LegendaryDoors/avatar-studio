// The setup's installers: each takes the player's own files, or Microsoft's server, and writes
// what the studio reads (catalogue, font, tables, items, art, sounds).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    static class SetupInstall
    {
        // ---- the catalogue -----------------------------------------------------------------------------

        private static readonly string[] TocIds =
        {
            "9ad6ebce62624eeb8a82a3f70b817369", "8a762df4c0674b66b4ee56466b1b8280", "580a07d64bcd40bebf37254be826fb0b",
        };

        /// <summary>AvatarAssetPack.toc out of the update's FFFE07DF00000002 (a PIRS package), to AvatarToc.InstalledPath.</summary>
        public static void Catalogue(string update, Action<string> say)
        {
            string pkgPath = Path.Combine(update, "FFFE07DF00000002");
            StfsPackage pkg = new StfsPackage(File.ReadAllBytes(pkgPath));
            StfsEntry e = pkg.Find("AvatarAssetPack.toc");
            if (e == null) throw new InvalidDataException(pkgPath + " holds no AvatarAssetPack.toc");
            byte[] toc = pkg.Read(e);
            string id = toc.Length >= 16 ? BitConverter.ToString(toc, 0, 16).Replace("-", "").ToLowerInvariant() : "";
            if (Array.IndexOf(TocIds, id) < 0) throw new InvalidDataException("not an AvatarAssetPack.toc: unknown format id " + id);
            WriteFile(AvatarToc.InstalledPath, toc);
            say("installed the avatar catalogue (" + toc.Length + " bytes)");
        }

        // ---- the font ----------------------------------------------------------------------------------

        public const string BaseName = "xenonjklatin.xtt", PatchName = "$flash_xenonjklatin.xttp";
        public const int BaseSize = 0x1A8000, PatchSize = 0x7000;
        public const uint BaseCrc = 0xDDE4A14C, PatchCrc = 0xE2ADDDFB;
        private static readonly string[] XttTables = { "cmap", "head", "hhea", "hmtx", "name", "xchk", "xttf", "xloc", "xglf" };

        /// <summary>The console face: the base from the player's file, folder or zip, the patch from the update,
        /// both checked as the 17559 console's (size and CRC32) and as the loader checks them.</summary>
        public static void Font(string fontBase, string update, Action<string> say)
        {
            string from;
            byte[] b = FindBase(fontBase, out from);
            say("font base: " + from);
            byte[] p = FindPatch(update, out from);
            say("font patch: " + from);
            CheckFile(b, BaseSize, BaseCrc, "base");
            CheckFile(p, PatchSize, PatchCrc, "patch");
            byte[] bsig, bref, psig, pref;
            int bblocks, bglyphs, pblocks, pglyphs;
            CheckXtt(b, "base", out bsig, out bref, out bblocks, out bglyphs);
            CheckXtt(p, "patch", out psig, out pref, out pblocks, out pglyphs);
            foreach (byte x in bref) if (x != 0) throw new InvalidDataException("base: its xttf reference is not zero, so it is itself a patch");
            for (int i = 0; i < 256; i++)
                if (pref[i] != bsig[i]) throw new InvalidDataException("patch: its xttf reference is not the base's signature; XUI would drop it");
            say("checked: base " + bglyphs + " glyphs in " + bblocks + " blocks, patch " + pglyphs + " glyphs in " + pblocks +
                " blocks; every block's SHA-1 matches xchk; the patch belongs to the base");
            string dst = ConsoleFace.Folder;
            WriteFile(Path.Combine(dst, "xenonjklatin.xtt"), b);
            WriteFile(Path.Combine(dst, "xenonjklatin.xttp"), p);
            say("installed the console font -> " + dst);
        }

        private static byte[] FindBase(string path, out string from)
        {
            if (Directory.Exists(path))
            {
                foreach (string f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileName(f), BaseName, StringComparison.OrdinalIgnoreCase) && new FileInfo(f).Length == BaseSize)
                    {
                        byte[] d = File.ReadAllBytes(f);
                        if (Crc32(d) == BaseCrc) { from = f; return d; }
                    }
                throw new InvalidDataException(string.Format("no {0} of 0x{1:x} bytes and CRC32 0x{2:x8} under {3}", BaseName, BaseSize, BaseCrc, path));
            }
            if (IsZip(path))
            {
                using (ZipArchive z = new ZipArchive(File.OpenRead(path), ZipArchiveMode.Read))
                    foreach (ZipArchiveEntry en in z.Entries)
                    {
                        string leaf = en.FullName.Replace('\\', '/');
                        leaf = leaf.Substring(leaf.LastIndexOf('/') + 1);
                        if (!string.Equals(leaf, BaseName, StringComparison.OrdinalIgnoreCase) || en.Length != BaseSize) continue;
                        byte[] d = new byte[BaseSize];
                        using (Stream s = en.Open()) ReadFully(s, d);
                        if (Crc32(d) == BaseCrc) { from = path + "!" + en.FullName; return d; }
                    }
                throw new InvalidDataException(string.Format("no {0} of 0x{1:x} bytes and CRC32 0x{2:x8} in {3}", BaseName, BaseSize, BaseCrc, path));
            }
            from = path;
            return File.ReadAllBytes(path);
        }

        private static byte[] FindPatch(string path, out string from)
        {
            if (Directory.Exists(path))
            {
                string loose = Path.Combine(path, PatchName);
                if (File.Exists(loose)) { from = loose; return File.ReadAllBytes(loose); }
                string pkg = Path.Combine(path, "su20076000_00000000");
                if (!File.Exists(pkg)) throw new InvalidDataException(path + " has neither " + PatchName + " nor su20076000_00000000");
                path = pkg;
            }
            byte[] data = File.ReadAllBytes(path);
            if (StfsPackage.LooksLikePackage(data))
            {
                StfsPackage p = new StfsPackage(data);
                StfsEntry e = p.Find(PatchName);
                if (e == null) throw new InvalidDataException(path + " holds no " + PatchName);
                from = path + "!" + PatchName;
                return p.Read(e);
            }
            from = path;
            return data;
        }

        private static void CheckFile(byte[] d, int size, uint crc, string what)
        {
            if (d.Length != size || Crc32(d) != crc)
                throw new InvalidDataException(string.Format("{0}: 0x{1:x} bytes, CRC32 0x{2:x8}; the 17559 console's is 0x{3:x} / 0x{4:x8}",
                                                             what, d.Length, Crc32(d), size, crc));
        }

        /// <summary>The loader's checks (sub_921B7BB0, sub_921C0BA8, sub_921C0718).</summary>
        private static void CheckXtt(byte[] d, string what, out byte[] sig, out byte[] reference, out int blocks, out int glyphs)
        {
            if (d.Length < 0x118 || d[0] != 'x' || d[1] != 't' || d[2] != 't' || d[3] != 'f')
                throw new InvalidDataException(what + ": not an XTT (no 'xttf' header)");
            sig = new byte[256];
            Buffer.BlockCopy(d, 4, sig, 0, 256);
            uint signed = U32(d, 0x104), size = U32(d, 0x108), comp = U32(d, 0x10C), uncomp = U32(d, 0x110), version = U32(d, 0x114);
            if (version != 0x10000 || size != d.Length || signed > size - 256)
                throw new InvalidDataException(string.Format("{0}: header refused (version 0x{1:x}, size 0x{2:x} of 0x{3:x}, signed 0x{4:x})", what, version, size, d.Length, signed));
            byte[] sfnt = Inflate(d, 0x118, (int)comp, int.MaxValue);
            if (sfnt.Length != uncomp) throw new InvalidDataException(string.Format("{0}: directory inflates to 0x{1:x}, header says 0x{2:x}", what, sfnt.Length, uncomp));
            int n = U16(sfnt, 4);
            Dictionary<string, KeyValuePair<int, int>> tables = new Dictionary<string, KeyValuePair<int, int>>();
            for (int i = 0; i < n; i++)
            {
                int e = 12 + 16 * i;
                string tag = "" + (char)sfnt[e] + (char)sfnt[e + 1] + (char)sfnt[e + 2] + (char)sfnt[e + 3];
                tables[tag] = new KeyValuePair<int, int>((int)U32(sfnt, e + 8), (int)U32(sfnt, e + 12));
            }
            List<string> missing = new List<string>();
            foreach (string t in XttTables) if (!tables.ContainsKey(t)) missing.Add(t);
            if (missing.Count > 0) throw new InvalidDataException(what + ": tables missing: " + string.Join(" ", missing.ToArray()));
            if (U32(sfnt, tables["head"].Key + 12) != 0x5F0F3CF5) throw new InvalidDataException(what + ": head magic wrong");
            int xt = tables["xttf"].Key;
            int cap = (int)U32(sfnt, xt + 4);
            if (cap == 0) cap = 0x4000;
            reference = new byte[256];
            Buffer.BlockCopy(sfnt, xt + 8, reference, 0, 256);
            int glyf = tables["xglf"].Key, xchk = tables["xchk"].Key;
            blocks = tables["xchk"].Value / 20;
            if (glyf + ((long)blocks << 12) > d.Length) throw new InvalidDataException(what + ": xglf runs past the file");
            using (SHA1 sha = SHA1.Create())
                for (int bk = 0; bk < blocks; bk++)
                {
                    byte[] h = sha.ComputeHash(d, glyf + (bk << 12), 4096);
                    for (int i = 0; i < 20; i++)
                        if (h[i] != sfnt[xchk + 20 * bk + i]) throw new InvalidDataException(what + ": glyph block " + bk + " fails its xchk SHA-1");
                    if (Inflate(d, glyf + (bk << 12), 4096, cap + 1).Length > cap)
                        throw new InvalidDataException(string.Format("{0}: glyph block {1} inflates past 0x{2:x}", what, bk, cap));
                }
            glyphs = tables["xloc"].Value / 4 - 1;
        }

        // ---- the console's avatar tables ---------------------------------------------------------------

        /// <summary>The tables the avatar engine runs on, copied out of the update's AvatarEditor.xex and xam.xex.</summary>
        public static void ConsoleTablesInstall(string update, Action<string> say)
        {
            string xexPath = Path.Combine(update, "AvatarEditor.xex");
            uint imgBase, xamBase;
            byte[] image = XexImage(File.ReadAllBytes(xexPath), out imgBase);
            StfsPackage flash = new StfsPackage(File.ReadAllBytes(Path.Combine(update, "su20076000_00000000")));
            StfsEntry xe = flash.Find("$flash_xam.xex");
            if (xe == null) throw new InvalidDataException("the update's flash package has no $flash_xam.xex");
            byte[] xam = XexImage(flash.Read(xe), out xamBase);
            byte[] file = AvatarStudio.Avatars.ConsoleTables.Extract(image, imgBase, xam, xamBase, Crc32);
            WriteFile(AvatarStudio.Avatars.ConsoleTables.InstalledPath, file);
            AvatarStudio.Avatars.ConsoleTables.Reload();
            say("installed the avatar tables (" + AvatarStudio.Avatars.ConsoleTables.RegionCount + " tables, " + file.Length + " bytes) -> "
                + AvatarStudio.Avatars.ConsoleTables.InstalledPath);
        }

        // ---- the stock items ---------------------------------------------------------------------------

        private static readonly string[] Cdn = { "http://download.xboxlive.com/content/584d07d1/v2", "http://download.xboxlive.com/content/584d07d1" };

        /// <summary>Every stock asset's GUID: the catalogue's entries that carry their asset, distinct, sorted.</summary>
        public static List<string> StockGuids()
        {
            if (TocAssets.PathSource == null) TocAssets.PathSource = () => AvatarToc.InstalledPath;
            TocAssets.Reset();
            SortedDictionary<string, bool> set = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (TocRecord r in TocAssets.Entries) if (TocAssets.Has(r.Id)) set[r.Id.ToString("D")] = true;
            return new List<string>(set.Keys);
        }

        /// <summary>A player's stock items folder: %LOCALAPPDATA%\Avatar Studio\stock.</summary>
        public static string StockFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "stock"); }
        }

        /// <summary>The override assets tocassetdependencies names (.NET byte order).</summary>
        private static List<string> OverrideGuids(byte[] raw)
        {
            SortedDictionary<string, bool> set = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            int i = 0;
            while (i + 36 <= raw.Length)
            {
                byte[] g = new byte[16];
                Buffer.BlockCopy(raw, i + 16, g, 0, 16);
                int n = BitConverter.ToInt32(raw, i + 32);
                if (n < 0 || n > 4) break;
                i += 36 + n * 16;
                set[new Guid(g).ToString("D")] = true;
            }
            return new List<string>(set.Keys);
        }

        /// <summary>AVATAR_STUDIO_STOCK, else StockFolder.</summary>
        public static string ItemsFolder()
        {
            string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_STOCK");
            return !string.IsNullOrEmpty(env) ? env : StockFolder;
        }

        /// <summary>ItemsFolder() when it exists, else null.</summary>
        public static string ExistingItemsFolder()
        {
            string d = ItemsFolder();
            return Directory.Exists(d) ? d : null;
        }

        /// <summary>The dependency table, and when `only` names GUIDs, those from the CDN, 8 at a time.</summary>
        public static List<string> Items(Action<string> say, List<string> only, Func<bool> cancelled)
        {
            string dir = ItemsFolder();
            Directory.CreateDirectory(dir);
            List<string> names = only != null ? new List<string>(only) : new List<string>();
            string st;
            Fetch("tocassetdependencies", dir, out st);
            if (st != "ok" && st != "skip") throw new IOException("tocassetdependencies: " + st);
            say("fetching " + names.Count + " items -> " + dir);
            Queue<string> q = new Queue<string>(names);
            List<string> failed = new List<string>();
            int ok = 0, skip = 0;
            object gate = new object();
            ThreadStart work = delegate
            {
                while (true)
                {
                    string n;
                    lock (gate) { if (q.Count == 0 || cancelled()) return; n = q.Dequeue(); }
                    string s;
                    Fetch(n, dir, out s);
                    lock (gate)
                    {
                        if (s == "ok") ok++; else if (s == "skip") skip++; else failed.Add(n + " " + s);
                        int done = ok + skip + failed.Count;
                        if (done % 50 == 0 || done == names.Count) say("downloading the avatar items (" + done + " of " + names.Count + ")");
                    }
                }
            };
            List<Thread> threads = new List<Thread>();
            for (int k = 0; k < 8; k++) { Thread t = new Thread(work) { IsBackground = true }; t.Start(); threads.Add(t); }
            foreach (Thread t in threads) t.Join();
            say("downloaded " + ok + ", already had " + skip + ", failed " + failed.Count);
            return failed;
        }

        private static void Fetch(string name, string dir, out string status)
        {
            string dest = Path.Combine(dir, name + ".strb");
            if (File.Exists(dest) && new FileInfo(dest).Length > 0) { status = "skip"; return; }
            status = "fail";
            foreach (string b in Cdn)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    byte[] data;
                    try
                    {
                        HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(b + "/" + name + ".bin");
                        rq.UserAgent = "Xbox360/2.0";
                        rq.Timeout = 30000;
                        using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
                        using (Stream s = rs.GetResponseStream())
                        using (MemoryStream ms = new MemoryStream())
                        {
                            s.CopyTo(ms);
                            data = ms.ToArray();
                        }
                    }
                    catch (WebException e)
                    {
                        HttpWebResponse r = e.Response as HttpWebResponse;
                        if (r != null) { status = r.StatusCode == HttpStatusCode.NotFound ? "404" : "http" + (int)r.StatusCode; break; }
                        status = "neterr";
                        Thread.Sleep(1000 + 1000 * attempt);       // transient: the CDN drops connections under load
                        continue;
                    }
                    // tocassetdependencies is a raw table, not an STRB container
                    if (name != "tocassetdependencies" && (data.Length < 4 || data[0] != 'S' || data[1] != 'T' || data[2] != 'R' || data[3] != 'B'))
                    { status = "not-strb"; break; }
                    WriteFile(dest, data, ".part");
                    status = "ok";
                    return;
                }
            }
        }

        // ---- the editor's art and data ------------------------------------------------------------------

        // the sizes the studio uses, and the chrome the scenes name whatever its size
        private static readonly int[][] WantedSizes = { new[] { 160, 235 }, new[] { 128, 128 }, new[] { 180, 235 } };
        private static readonly HashSet<string> WantedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "rotate_bar.png", "legend_LT.png", "legend_RT.png", "trigger_glow.png",
            "page_turn_handle_radon_right.png", "editor_mainUiShadow.png", "AvatarShadow.png",
            "closet_mirror_border.png", "closet_mirror_sheen.png", "rainbow_blur.png", "outerRing.png",
            "avatar_selection_beam.png", "bg_star.png", "width_rail_bg.png", "clasp_width.png", "outfits_tutorial.png",
        };

        /// <summary>AVATAR_STUDIO_TILES, else %LOCALAPPDATA%\Avatar Studio\tiles.</summary>
        public static string TilesFolder()
        {
            string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_TILES");
            if (!string.IsNullOrEmpty(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "tiles");
        }

        /// <summary>From the update's AvatarEditor.xex: tiles, chrome, backdrop, animations, redirects, visuals, legend.</summary>
        public static void EditorArt(string update, Action<string> say)
        {
            string xexPath = Path.Combine(update, "AvatarEditor.xex");
            byte[] x = File.ReadAllBytes(xexPath);
            Dictionary<string, XuizPackage> pk = XuizPackage.All(x);
            XuizPackage media, controlp;
            if (!pk.TryGetValue("MEDIA", out media)) throw new InvalidDataException("no MEDIA package in " + xexPath);
            string tiles = TilesFolder(), anims = EditorAnimations.Folder;
            List<string> names = new List<string>();
            foreach (string n in media.Files.Keys) if (n.IndexOf('/') < 0) names.Add(n);      // MEDIA's own files, not its sub-folders
            names.Sort(StringComparer.Ordinal);
            int picked = 0, animations = 0;
            foreach (string n in names)
            {
                ArraySegment<byte> f = media.Files[n];
                if (n.ToLowerInvariant().EndsWith(".png"))
                {
                    int[] size = PngSize(f);
                    bool wanted = WantedNames.Contains(n);
                    foreach (int[] w in WantedSizes) if (size != null && size[0] == w[0] && size[1] == w[1]) wanted = true;
                    if (wanted) { WriteFile(Path.Combine(tiles, n), Bytes(f)); picked++; }
                }
                else if (n.EndsWith(".MsLiveAvatarAsset", StringComparison.Ordinal))
                { WriteFile(Path.Combine(anims, n), Bytes(f)); animations++; }
            }
            say("installed " + picked + " tiles and " + animations + " animations");
            ArraySegment<byte> bg;
            if (media.Files.TryGetValue("background.jpg", out bg)) { WriteFile(Path.Combine(tiles, "background.jpg"), Bytes(bg)); say("installed background.jpg (the backdrop)"); }

            List<string[]> table = Redirects(x);
            StringBuilder sb = new StringBuilder();
            sb.Append("# requested<TAB>source<TAB>flag -- the editor's image redirect table, read from\n");
            sb.Append("# " + Path.GetFileName(xexPath) + " by Avatar Studio's setup. flag 1: the source is drawn through\n");
            sb.Append("# shader_unselected_icons_ps to make the requested image (TileArt.cs).\n");
            foreach (string[] r in table) sb.Append(r[0]).Append('\t').Append(r[1]).Append('\t').Append(r[2]).Append('\n');
            WriteFile(Path.Combine(tiles, "redirects.txt"), Encoding.ASCII.GetBytes(sb.ToString()));
            say("wrote " + table.Count + " redirects");

            ArraySegment<byte> skin;
            if (media.Files.TryGetValue("EditorSkin.xur", out skin))
                say("wrote " + XurReader.Export(Bytes(skin), "EditorSkin.xur", Path.Combine(tiles, "visuals.txt"), "XuiVisual") + " visuals");
            ArraySegment<byte> legend;
            if (pk.TryGetValue("controlp", out controlp) && controlp.Files.TryGetValue("LegendScene.xur", out legend))
            {
                XurReader.Export(Bytes(legend), "LegendScene.xur", Path.Combine(tiles, "legend.txt"), "ControlPackLegendScene");
                int copied = 0;
                SortedSet<string> pics = XurReader.PngImages(Bytes(legend));
                foreach (string n in pics)
                {
                    ArraySegment<byte> f;
                    if (controlp.Files.TryGetValue(n, out f)) { WriteFile(Path.Combine(tiles, n), Bytes(f)); copied++; }
                }
                say("wrote the legend scene and " + copied + " of its " + pics.Count + " pictures");
            }
        }

        // ---- the editor's sounds -----------------------------------------------------------------------

        // ffmpeg decodes them: BtbN's LGPL Windows build, the stable `latest` release, checked by SHA-256.
        private const string FfmpegRelease = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";
        private const string FfmpegZip = "ffmpeg-master-latest-win64-lgpl-shared.zip";

        /// <summary>AVATAR_STUDIO_FFMPEG's folder, else a download into %LOCALAPPDATA%\Avatar Studio\ffmpeg, removed after.</summary>
        private static string FfmpegFolder(out bool keep)
        {
            string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_FFMPEG");
            keep = !string.IsNullOrEmpty(env);
            return keep ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "ffmpeg");
        }

        /// <summary>MEDIA's *.xma (XMA, which FNA cannot play), each decoded by ffmpeg to 16-bit PCM WAVE.</summary>
        public static void EditorSounds(string update, Action<string> say)
        {
            string xexPath = Path.Combine(update, "AvatarEditor.xex");
            XuizPackage media;
            if (!XuizPackage.All(File.ReadAllBytes(xexPath)).TryGetValue("MEDIA", out media)) throw new InvalidDataException("no MEDIA package in " + xexPath);
            List<string> xmas = new List<string>();
            foreach (string n in media.Files.Keys)
                if (n.IndexOf('/') < 0 && n.ToLowerInvariant().EndsWith(".xma")) xmas.Add(n);
            xmas.Sort(StringComparer.Ordinal);
            if (xmas.Count == 0) { say("MEDIA holds no sounds"); return; }
            string dst = Sounds.Folder;
            int have = 0;
            foreach (string n in xmas)
            {
                string w = Path.Combine(dst, Path.GetFileNameWithoutExtension(n) + ".wav");
                if (File.Exists(w) && new FileInfo(w).Length > 0) have++;
            }
            if (have == xmas.Count) { say("the " + have + " sounds are already installed -> " + dst); return; }

            bool keep;
            string ffDir = FfmpegFolder(out keep);
            string work = Path.Combine(Path.GetTempPath(), "AvatarStudio-sounds-" + Guid.NewGuid().ToString("N"));
            try
            {
                string ff = Ffmpeg(ffDir, keep, say);
                Directory.CreateDirectory(work);
                Directory.CreateDirectory(dst);
                List<string> failed = new List<string>();
                int done = 0;
                for (int i = 0; i < xmas.Count; i++)
                {
                    if (Cancelled()) throw new OperationCanceledException();
                    string n = xmas[i], stem = Path.GetFileNameWithoutExtension(n);
                    string src = Path.Combine(work, n), wav = Path.Combine(work, stem + ".wav");
                    File.WriteAllBytes(src, Bytes(media.Files[n]));
                    // -nostdin only stops ffmpeg reading keys
                    string err;
                    int rc = RunHidden(ff, "-nostdin -hide_banner -loglevel error -y -i \"" + src + "\" -c:a pcm_s16le \"" + wav + "\"", work, out err);
                    if (rc == 0 && File.Exists(wav))
                    {
                        string to = Path.Combine(dst, stem + ".wav");
                        if (File.Exists(to)) File.Delete(to);
                        File.Move(wav, to);
                        done++;
                    }
                    else failed.Add(n + " (" + (err.Length > 200 ? err.Substring(0, 200) : err) + ")");
                    if ((i + 1) % 10 == 0 || i + 1 == xmas.Count) say("preparing the editor's sounds (" + (i + 1) + " of " + xmas.Count + ")");
                }
                say("decoded " + done + " of " + xmas.Count + " sounds -> " + dst);
                if (failed.Count > 0) throw new IOException(failed.Count + " could not be decoded, e.g. " + failed[0]);
            }
            finally
            {
                TryDelete(work);
                if (!keep) TryDelete(ffDir);
            }
        }

        /// <summary>ffmpeg.exe in `dir`: reused, else BtbN's zip downloaded, checked by SHA-256 and unpacked.</summary>
        private static string Ffmpeg(string dir, bool reuse, Action<string> say)
        {
            string exe = Path.Combine(dir, "ffmpeg.exe");
            if (reuse && File.Exists(exe)) { say("ffmpeg: " + exe); return exe; }
            if (!reuse) TryDelete(dir);
            Directory.CreateDirectory(dir);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;        // GitHub refuses older TLS
            string zip = Path.Combine(dir, FfmpegZip + ".part");
            for (int attempt = 0; ; attempt++)
            {
                string want = null, got = null;
                try
                {
                    want = FfmpegChecksum();
                    got = Download(FfmpegRelease + FfmpegZip, zip, say);
                }
                catch (WebException e)
                {
                    if (e.Response != null || attempt == 2) throw;
                    Thread.Sleep(2000 + 2000 * attempt);       // a dropped connection: try again
                    continue;
                }
                if (got == want) break;
                File.Delete(zip);
                // the release is rebuilt daily: a new build may land between the two reads
                if (attempt >= 1) throw new InvalidDataException(FfmpegZip + ": SHA-256 " + got + ", but the release lists " + want);
            }
            bool wrote = false;
            using (ZipArchive z = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read))
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string[] p = e.FullName.Replace('\\', '/').Split('/');
                    bool take = (p.Length == 3 && p[1] == "bin" && (p[2] == "ffmpeg.exe" || p[2].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                             || (p.Length == 2 && p[1] == "LICENSE.txt");
                    if (!take) continue;
                    using (Stream s = e.Open())
                    using (FileStream f = File.Create(Path.Combine(dir, p[p.Length - 1])))
                        s.CopyTo(f);
                    wrote |= p[p.Length - 1] == "ffmpeg.exe";
                }
            File.Delete(zip);
            if (wrote && !File.Exists(exe)) throw new ToolBlockedException(exe, false);   // removed once written: antivirus
            if (!File.Exists(exe)) throw new InvalidDataException(FfmpegZip + " holds no bin/ffmpeg.exe");
            say("ffmpeg: " + exe);
            return exe;
        }

        /// <summary>The zip's SHA-256 as the release's checksums.sha256 lists it ("hash  name" lines).</summary>
        private static string FfmpegChecksum()
        {
            string text;
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(FfmpegRelease + "checksums.sha256");
            rq.UserAgent = "AvatarStudio";
            rq.Timeout = 30000;
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            using (StreamReader r = new StreamReader(rs.GetResponseStream(), Encoding.ASCII))
                text = r.ReadToEnd();
            foreach (string line in text.Split('\n'))
            {
                string[] f = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 2 && f[1].TrimStart('*') == FfmpegZip && f[0].Length == 64) return f[0].ToLowerInvariant();
            }
            throw new InvalidDataException("the release's checksums.sha256 lists no " + FfmpegZip);
        }

        /// <summary>The file at `url` into `path`, the line every 4 MB; returns its SHA-256.</summary>
        private static string Download(string url, string path, Action<string> say)
        {
            Console.WriteLine("[studio] setup: downloading " + url);
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "AvatarStudio";
            rq.Timeout = 30000;
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            using (Stream s = rs.GetResponseStream())
            using (FileStream f = File.Create(path))
            using (SHA256 sha = SHA256.Create())
            {
                long total = rs.ContentLength, got = 0, next = 0;
                string of = total > 0 ? " of " + (total >> 20) : "";
                byte[] buf = new byte[1 << 16];
                int r;
                while ((r = s.Read(buf, 0, buf.Length)) > 0)
                {
                    if (Cancelled()) throw new OperationCanceledException();
                    f.Write(buf, 0, r);
                    sha.TransformBlock(buf, 0, r, null, 0);
                    got += r;
                    if (got >= next)
                    {
                        // the busy page shows this line: plain words (the URL is in the log)
                        say("downloading a helper for the editor's sounds (" + (got >> 20) + of + " MB)");
                        next += 4 << 20;
                    }
                }
                sha.TransformFinalBlock(buf, 0, 0);
                if (total >= 0 && got != total) throw new IOException("the download stopped at " + got + " of " + total + " bytes");
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>A program with no window; its exit code, stderr in `err`; killed after two minutes.</summary>
        internal static int RunHidden(string exe, string args, string cwd, out string err)
        {
            if (!File.Exists(exe)) throw new ToolBlockedException(exe, true);
            // the install test's stand-in for antivirus refusing the program
            if (string.Equals(Environment.GetEnvironmentVariable("AVATAR_STUDIO_TEST_BLOCK"), Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase))
                throw new ToolBlockedException(exe, false);
            System.Diagnostics.ProcessStartInfo si = new System.Diagnostics.ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = cwd,
            };
            StringBuilder e = new StringBuilder();
            System.Diagnostics.Process started;
            try { started = System.Diagnostics.Process.Start(si); }
            catch (System.ComponentModel.Win32Exception x)
            {
                if (Blocked(x.NativeErrorCode)) throw new ToolBlockedException(exe, false, x);
                if (x.NativeErrorCode == 2 || x.NativeErrorCode == 3) throw new ToolBlockedException(exe, true, x);
                throw;
            }
            using (System.Diagnostics.Process p = started)
            {
                p.ErrorDataReceived += (o, a) => { if (a.Data != null) lock (e) e.AppendLine(a.Data); };
                p.BeginErrorReadLine();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch (Exception) { } }
                p.WaitForExit();
                lock (e) err = e.ToString().Trim();
                // removed while it ran: antivirus stopped it
                if (p.ExitCode != 0 && !File.Exists(exe)) throw new ToolBlockedException(exe, false);
                return p.ExitCode;
            }
        }

        // winerror.h: ERROR_VIRUS_INFECTED, ERROR_VIRUS_DELETED, then Application Control's policy and reputation blocks
        private static readonly int[] BlockedCodes = { 225, 226, 4551, 4556, 4557, 4558, 4559, 4580, 4581, 4582 };

        /// <summary>Whether a program failed to start because antivirus or Windows' app control refused it.</summary>
        internal static bool Blocked(int win32Error) { return Array.IndexOf(BlockedCodes, win32Error) >= 0; }

        private static void TryDelete(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception) { }
        }

        private static byte[] Bytes(ArraySegment<byte> s)
        {
            byte[] b = new byte[s.Count];
            Buffer.BlockCopy(s.Array, s.Offset, b, 0, s.Count);
            return b;
        }

        private static int[] PngSize(ArraySegment<byte> f)
        {
            byte[] a = f.Array;
            int o = f.Offset;
            if (f.Count < 24 || a[o] != 0x89 || a[o + 1] != 'P' || a[o + 2] != 'N' || a[o + 3] != 'G') return null;
            return new[] { (int)U32(a, o + 16), (int)U32(a, o + 20) };
        }

        /// <summary>The XEX's PE image and its load address.</summary>
        internal static byte[] XexImage(byte[] x, out uint imgBase)
        {
            return AvatarStudio.Avatars.XexFile.Image(x, out imgBase);
        }

        /// <summary>The editor's image redirect table: the longest run of 12-byte records in .rdata.</summary>
        private static List<string[]> Redirects(byte[] x)
        {
            uint imgBase;
            byte[] img = XexImage(x, out imgBase);
            int size = img.Length;

            Dictionary<uint, string> nameCache = new Dictionary<uint, string>();
            Func<uint, string> nameAt = ptr =>
            {
                string s;
                if (nameCache.TryGetValue(ptr, out s)) return s;
                s = null;
                if (ptr >= imgBase && ptr < imgBase + (uint)size)
                {
                    int off = (int)(ptr - imgBase), e = off;
                    bool good = true;
                    while (e + 1 < img.Length && (img[e] != 0 || img[e + 1] != 0))
                    {
                        if (img[e] != 0 || img[e + 1] < 0x20 || img[e + 1] >= 0x7F || e - off > 256) { good = false; break; }
                        e += 2;
                    }
                    if (good && e > off)
                    {
                        StringBuilder t = new StringBuilder();
                        for (int k = off; k < e; k += 2) t.Append((char)img[k + 1]);
                        string l = t.ToString().ToLowerInvariant();
                        if (l.EndsWith(".png") || l.EndsWith(".jpg")) s = t.ToString();
                    }
                }
                nameCache[ptr] = s;
                return s;
            };
            Func<int, string[]> record = o =>
            {
                uint a = U32(img, o), b = U32(img, o + 4), f = U32(img, o + 8);
                if (f > 1) return null;
                string ra = nameAt(a), rb = nameAt(b);
                return ra != null && rb != null ? new[] { ra, rb, f.ToString(CultureInfo.InvariantCulture) } : null;
            };

            // the PE's section table, little-endian
            int lfanew = BitConverter.ToInt32(img, 0x3C);
            int nsec = BitConverter.ToUInt16(img, lfanew + 6), optSize = BitConverter.ToUInt16(img, lfanew + 20);
            int first = lfanew + 24 + optSize, bestAt = 0, bestN = 0;
            for (int i = 0; i < nsec; i++)
            {
                int so = first + 40 * i;
                string sname = Encoding.ASCII.GetString(img, so, 8).Split('\0')[0];
                if (sname != ".rdata") continue;
                int vsize = BitConverter.ToInt32(img, so + 8), va = BitConverter.ToInt32(img, so + 12);
                int o = va, end = Math.Min(va + vsize, img.Length);
                while (o + 12 <= end)
                {
                    int n = 0;
                    while (o + 12 * (n + 1) <= end && record(o + 12 * n) != null) n++;
                    if (n > bestN) { bestAt = o; bestN = n; }
                    o += n > 0 ? 12 * n : 4;
                }
            }
            List<string[]> table = new List<string[]>();
            if (bestN >= 100) for (int k = 0; k < bestN; k++) table.Add(record(bestAt + 12 * k));
            return table;
        }

        // ---- the check: AvatarStudio.exe --install-check ----------------------------------------------

        private static readonly string[] ScratchVars =
        {
            "AVATAR_STUDIO_CATALOGUE", "AVATAR_STUDIO_FONTS", "AVATAR_STUDIO_TILES", "AVATAR_STUDIO_ANIMATIONS",
            "AVATAR_STUDIO_SOUNDS", "AVATAR_STUDIO_FFMPEG", "AVATAR_STUDIO_SHADERS", "AVATAR_STUDIO_STOCK",
            "AVATAR_STUDIO_DOWNLOAD", "AVATAR_STUDIO_TABLES",
        };

        /// <summary>Polled by the long downloads; a cancel throws OperationCanceledException.</summary>
        internal static Func<bool> Cancelled = () => false;

        /// <summary>True when every folder an installer writes is named by the environment (scratch).</summary>
        public static bool ScratchOnly()
        {
            foreach (string v in ScratchVars) if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) return false;
            return true;
        }

        /// <summary>The installers into the folders the environment names; a check never reaches a real install.</summary>
        public static int Check(string[] args)
        {
            if (!ScratchOnly())
            { Console.WriteLine("[install-check] refused: " + string.Join(", ", ScratchVars) + " must all be set (every folder it writes must be a scratch one)"); return 2; }
            string update = Arg(args, "--update"), fontBase = Arg(args, "--font-base"), sample = Arg(args, "--items-sample"), zip = Arg(args, "--zip");
            Action<string> say = s => Console.WriteLine("[install-check] " + s);
            int rc = 0;
            Action<string, Action> step = (title, a) =>
            {
                try { a(); say(title + ": ok"); }
                catch (Exception e) { say(title + ": FAILED " + e.Message); rc = 1; }
            };
            if (zip != null)
                step("update zip", () =>
                {
                    string got = SetupDownload.UseOwn(zip, say, () => false);
                    if (got == null) throw new InvalidDataException(zip + " gave no update");
                    foreach (string n in SetupDownload.Needed)
                    {
                        byte[] a = File.ReadAllBytes(Path.Combine(got, n));
                        if (update != null)
                        {
                            byte[] b = File.ReadAllBytes(Path.Combine(update, n));
                            if (a.Length != b.Length || Crc32(a) != Crc32(b)) throw new InvalidDataException(n + " differs from " + update + "'s");
                        }
                        say(n + ": " + a.Length + " bytes, CRC32 " + Crc32(a).ToString("x8") + (update != null ? ", the same as the folder's" : ""));
                    }
                });
            if (update != null) step("catalogue", () => Catalogue(update, say));
            if (update != null) step("avatar tables", () => ConsoleTablesInstall(update, say));
            if (update != null && fontBase != null) step("font", () => Font(fontBase, update, say));
            if (update != null) step("editor art", () => EditorArt(update, say));
            if (update != null) step("lighting", () => SetupShaders.Install(update, say));
            if (update != null) step("sounds", () => EditorSounds(update, say));
            step("stock list", () =>
            {
                List<string> all = StockGuids();
                File.WriteAllLines(Path.Combine(ItemsFolder(), "_stock_guids.txt"), all.ToArray());
                say(all.Count + " stock GUIDs -> _stock_guids.txt");
            });
            if (sample != null)
                step("items", () =>
                {
                    // the table first, then N of the assets it names, spread over it
                    List<string> failed = Items(say, new List<string>(), () => false);
                    List<string> all = OverrideGuids(File.ReadAllBytes(Path.Combine(ItemsFolder(), "tocassetdependencies.strb")));
                    int n = int.Parse(sample);
                    List<string> some = new List<string>();
                    for (int i = 0; i < all.Count && some.Count < n; i += Math.Max(1, all.Count / n)) some.Add(all[i]);
                    failed.AddRange(Items(say, some, () => false));
                    if (failed.Count > 0) throw new IOException(string.Join(", ", failed.ToArray()));
                });
            Console.WriteLine("[install-check] done");
            return rc;
        }

        private static string Arg(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        // ---- shared ------------------------------------------------------------------------------------

        /// <summary>Written through `tmp` beside it, then moved over.</summary>
        public static void WriteFile(string path, byte[] data, string tmp = ".tmp")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            string t = path + tmp;
            File.WriteAllBytes(t, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(t, path);
        }

        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        /// <summary>zlib.crc32.</summary>
        public static uint Crc32(byte[] d)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in d) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }

        private static bool IsZip(string path)
        {
            try
            {
                using (FileStream f = File.OpenRead(path))
                {
                    byte[] h = new byte[4];
                    return f.Read(h, 0, 4) == 4 && h[0] == 'P' && h[1] == 'K' && h[2] == 3 && h[3] == 4;
                }
            }
            catch (Exception) { return false; }
        }

        private static void ReadFully(Stream s, byte[] d)
        {
            int got = 0, r;
            while (got < d.Length && (r = s.Read(d, got, d.Length - got)) > 0) got += r;
            if (got != d.Length) throw new EndOfStreamException();
        }

        /// <summary>A zlib stream (2-byte header, raw deflate) inflated, stopping past `max` bytes.</summary>
        private static byte[] Inflate(byte[] src, int off, int len, int max)
        {
            using (MemoryStream ms = new MemoryStream(src, off + 2, len - 2))
            using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (MemoryStream o = new MemoryStream())
            {
                byte[] buf = new byte[4096];
                int r;
                while ((r = ds.Read(buf, 0, buf.Length)) > 0)
                {
                    o.Write(buf, 0, r);
                    if (o.Length > max) break;
                }
                return o.ToArray();
            }
        }

        private static uint U32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }
        private static int U16(byte[] b, int o) { return b[o] << 8 | b[o + 1]; }
    }

    /// <summary>A program the setup runs is missing, or antivirus refused it.</summary>
    sealed class ToolBlockedException : Exception
    {
        public readonly string Tool;         // its file name
        public readonly bool Missing;        // not there, rather than refused

        public ToolBlockedException(string path, bool missing, Exception inner = null)
            : base(Path.GetFileName(path) + (missing ? " is missing (" : " was blocked (") + path + ")", inner)
        {
            Tool = Path.GetFileName(path);
            Missing = missing;
        }
    }
}
