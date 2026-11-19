// Fetches the system update the setup reads: Microsoft's 2.0.17559 USB update, from archive.org,
// checked by SHA-1, resumed when cut off, then unpacked.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace AvatarStudio
{
    static class SetupDownload
    {
        public const string ZipName = "SystemUpdate_17559_USB.zip";
        public const long ZipSize = 115992188;
        public const string ZipSha1 = "3e3cf2908656adf666ddc771aceefc3496e39195";

        // archive.org items holding the zip, in the order tried
        private static readonly string[] Items =
        {
            "xbox-360-system-update-17559-usb", "xbox-360-system-update-17559", "xbox-360-system-update-17559-cd-usb",
            "system-update-17559-usb", "system-update-17559-usb_202404",
        };

        /// <summary>The four files of $SystemUpdate the setup reads.</summary>
        public static readonly string[] Needed = { "dash.xex", "AvatarEditor.xex", "FFFE07DF00000002", "su20076000_00000000" };

        /// <summary>AVATAR_STUDIO_DOWNLOAD, else %LOCALAPPDATA%\Avatar Studio\update.</summary>
        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_DOWNLOAD");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "update");
            }
        }

        /// <summary>The update an earlier download unpacked, else null.</summary>
        public static string Downloaded() { return SetupArt.Resolve(Folder); }

        /// <summary>The $SystemUpdate folder: unpacked before, or fetched, checked and unpacked.
        /// `cancelled` is polled between reads; a cancel throws.</summary>
        public static string Fetch(Action<string> say, Func<bool> cancelled)
        {
            string have = Downloaded();
            if (have != null) { say("the update is already here -> " + have); return have; }
            string dir = Folder;
            Directory.CreateDirectory(dir);
            string zip = Path.Combine(dir, ZipName), part = zip + ".part";
            if (!(File.Exists(zip) && new FileInfo(zip).Length == ZipSize && Sha1(zip, cancelled) == ZipSha1))
            {
                if (File.Exists(zip)) File.Delete(zip);
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;          // archive.org refuses older TLS
                Exception last = null;
                foreach (string item in Items)
                {
                    string url = "https://archive.org/download/" + item + "/" + ZipName;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            Get(url, part, say, cancelled);
                            last = null;
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (WebException e)
                        {
                            last = e;
                            HttpWebResponse r = e.Response as HttpWebResponse;
                            if (r != null && (int)r.StatusCode >= 400 && (int)r.StatusCode < 500 && r.StatusCode != (HttpStatusCode)416) break;   // not this item
                            if (r != null && r.StatusCode == (HttpStatusCode)416) TryDeleteFile(part);       // the part is past the end: start over
                            Sleep(2000 + 2000 * attempt, cancelled);
                        }
                        catch (IOException e) { last = e; Sleep(2000 + 2000 * attempt, cancelled); }
                    }
                    if (last != null) { say("archive.org " + item + ": " + last.Message); continue; }
                    // the whole file: its size and SHA-1, or it is not used
                    long len = new FileInfo(part).Length;
                    string sha = len == ZipSize ? Sha1(part, cancelled) : null;
                    if (sha == ZipSha1) { File.Move(part, zip); break; }
                    say("archive.org " + item + ": the file is " + len + " bytes" + (sha != null ? ", SHA-1 " + sha : "") + ", not the update; trying the next copy");
                    TryDeleteFile(part);
                    last = new InvalidDataException(ZipName + " from " + item + " is not the expected file");
                }
                if (!File.Exists(zip)) throw last ?? new IOException("no copy of " + ZipName + " could be downloaded");
            }
            string found = Unpack(zip, dir, say, cancelled);
            TryDeleteFile(zip);
            return found;
        }

        /// <summary>The player's own zip or folder; its $SystemUpdate, or null when it holds none.</summary>
        public static string UseOwn(string path, Action<string> say, Func<bool> cancelled)
        {
            if (Directory.Exists(path)) return SetupArt.Resolve(path);
            if (File.Exists(path) && string.Equals(Path.GetFileName(path), "dash.xex", StringComparison.OrdinalIgnoreCase))
                return SetupArt.Resolve(Path.GetDirectoryName(path));
            if (!File.Exists(path)) return null;
            try { return Unpack(path, Folder, say, cancelled); }
            catch (InvalidDataException) { return null; }
        }

        /// <summary>The four files into `dir`\$SystemUpdate, each written whole before it is named.</summary>
        private static string Unpack(string zip, string dir, Action<string> say, Func<bool> cancelled)
        {
            string dst = Path.Combine(dir, "$SystemUpdate");
            using (ZipArchive z = new ZipArchive(File.OpenRead(zip), ZipArchiveMode.Read))
            {
                Dictionary<string, ZipArchiveEntry> want = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipArchiveEntry e in z.Entries)
                {
                    string[] p = e.FullName.Replace('\\', '/').Split('/');
                    // $SystemUpdate/<file>, at the top or one folder down
                    int k = p.Length - 2;
                    if (k < 0 || !string.Equals(p[k], "$SystemUpdate", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string n in Needed)
                        if (string.Equals(p[k + 1], n, StringComparison.OrdinalIgnoreCase) && !want.ContainsKey(n)) want[n] = e;
                }
                foreach (string n in Needed)
                    if (!want.ContainsKey(n)) throw new InvalidDataException(Path.GetFileName(zip) + " holds no $SystemUpdate/" + n);
                Directory.CreateDirectory(dst);
                int done = 0;
                foreach (string n in Needed)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    // plain words on screen; the file's name in the log only
                    Console.WriteLine("[studio] setup: unpacking " + n);
                    say("unpacking the Xbox 360 files (" + (++done) + " of " + Needed.Length + ")");
                    string to = Path.Combine(dst, n), tmp = to + ".tmp";
                    using (Stream s = want[n].Open())
                    using (FileStream f = File.Create(tmp))
                        s.CopyTo(f);
                    if (new FileInfo(tmp).Length != want[n].Length) throw new InvalidDataException(n + " unpacked short");
                    if (File.Exists(to)) File.Delete(to);
                    File.Move(tmp, to);
                }
            }
            string found = SetupArt.Resolve(dst);
            if (found == null) throw new InvalidDataException("the unpacked files do not make an update");
            say("the update -> " + found);
            return found;
        }

        /// <summary>`url` into `part`, resuming with a range request; a line every 4 MB.</summary>
        private static void Get(string url, string part, Action<string> say, Func<bool> cancelled)
        {
            long from = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (from >= ZipSize) return;
            HttpWebRequest rq = (HttpWebRequest)WebRequest.Create(url);
            rq.UserAgent = "AvatarStudio";
            rq.Timeout = 30000;
            rq.ReadWriteTimeout = 60000;
            if (from > 0) rq.AddRange(from);
            using (HttpWebResponse rs = (HttpWebResponse)rq.GetResponse())
            {
                bool resumed = rs.StatusCode == HttpStatusCode.PartialContent;
                if (!resumed) from = 0;
                else say("carrying on from " + (from >> 20) + " MB");
                using (Stream s = rs.GetResponseStream())
                using (FileStream f = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write))
                {
                    long got = from, next = 0;
                    byte[] buf = new byte[1 << 16];
                    int r;
                    while ((r = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        f.Write(buf, 0, r);
                        got += r;
                        if (got >= next)
                        {
                            say("downloading Xbox 360 files from the Internet Archive (" + (got >> 20) + " of " + (ZipSize >> 20) + " MB)");
                            next = got + (4 << 20);
                        }
                    }
                    if (got != ZipSize) throw new IOException("the download stopped at " + got + " of " + ZipSize + " bytes");
                }
            }
        }

        private static string Sha1(string path, Func<bool> cancelled)
        {
            using (FileStream f = File.OpenRead(path))
            using (SHA1 sha = SHA1.Create())
            {
                byte[] buf = new byte[1 << 20];
                int r;
                while ((r = f.Read(buf, 0, buf.Length)) > 0)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    sha.TransformBlock(buf, 0, r, null, 0);
                }
                sha.TransformFinalBlock(buf, 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void Sleep(int ms, Func<bool> cancelled)
        {
            for (int t = 0; t < ms; t += 100)
            {
                if (cancelled()) throw new OperationCanceledException();
                System.Threading.Thread.Sleep(100);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }
    }
}
