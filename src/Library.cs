// The avatars you have made: %LOCALAPPDATA%\Avatar Studio\avatars\<name>.avatar (AVATAR_STUDIO_LIBRARY).
// A .avatar (ours, little-endian): "AVTR", u32 version 1, u32 1000, the console's manifest,
// u32 + UTF-8 name, i64 created and modified (UTC ticks), u32 + thumbnail PNG.
// An avatar's outfits sit beside it in <name>.outfits\.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class AvatarRecord
    {
        public string Name;
        public byte[] Manifest;            // 1000 bytes
        public byte[] ThumbnailPng;        // may be null
        public DateTime Created, Modified;
        public string Path;                // null until first saved

        public AvatarRecord Clone()
        {
            AvatarRecord r = new AvatarRecord();
            r.Name = Name;
            r.Manifest = (byte[])Manifest.Clone();
            r.ThumbnailPng = ThumbnailPng == null ? null : (byte[])ThumbnailPng.Clone();
            r.Created = Created; r.Modified = Modified;
            return r;
        }
    }

    sealed class AvatarLibrary
    {
        public const int ManifestLength = 1000;        // AvatarManifest.MANIFEST_LEN_IN_BYTES
        public const int XboxDescriptorLength = 1021;  // 0x01 + manifest + 20-byte trailer
        private const string Magic = "AVTR";
        private const uint Version = 1;
        public const int MaxNameLength = 15;   // the console's profile-name limit

        public readonly List<AvatarRecord> Records = new List<AvatarRecord>();
        public string Status { get; private set; }

        /// <summary>Our settings beside the avatars (the "Introducing Outfits" flag), one key=value a line.</summary>
        public const string SettingsFile = "studio.settings";

        /// <summary>Created on first save, not on read.</summary>
        public static string Directory
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_LIBRARY");
                if (!string.IsNullOrEmpty(env)) return env;
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    System.IO.Path.Combine("Avatar Studio", "avatars"));
            }
        }

        public void Load()
        {
            Records.Clear();
            string dir = Directory;
            int bad = 0;
            try
            {
                if (!System.IO.Directory.Exists(dir))
                {
                    Status = "no library yet (" + dir + ")";
                    Console.WriteLine("[studio] library: " + Status);
                    return;
                }
                foreach (string f in System.IO.Directory.GetFiles(dir))
                {
                    if (string.Equals(System.IO.Path.GetFileName(f), SettingsFile, StringComparison.OrdinalIgnoreCase)) continue;
                    AvatarRecord r = ReadFile(f);
                    if (r != null) Records.Add(r); else bad++;
                }
                Records.Sort(delegate(AvatarRecord a, AvatarRecord b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
                });
                Status = Records.Count + " avatar" + (Records.Count == 1 ? "" : "s")
                       + (bad > 0 ? ", " + bad + " file(s) skipped" : "");
            }
            catch (Exception e)
            {
                Status = "could not read " + dir + ": " + e.Message;
            }
            Console.WriteLine("[studio] library: " + Status + "  [" + dir + "]");
        }

        /// <summary>A .avatar, a bare manifest or a descriptor as a record, or null.</summary>
        public static AvatarRecord ReadFile(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);

                // A bare manifest or Xbox descriptor dropped in by hand.
                byte[] bare = ToManifest(raw);
                if (bare != null)
                {
                    if (!IsValid(bare)) return null;
                    AvatarRecord b = new AvatarRecord();
                    b.Name = Clip(System.IO.Path.GetFileNameWithoutExtension(path));
                    b.Manifest = bare;
                    b.Created = b.Modified = File.GetLastWriteTimeUtc(path);
                    b.Path = path;
                    return b;
                }

                if (raw.Length < 12 || Encoding.ASCII.GetString(raw, 0, 4) != Magic) return null;
                using (BinaryReader br = new BinaryReader(new MemoryStream(raw), Encoding.UTF8))
                {
                    br.ReadBytes(4);
                    uint version = br.ReadUInt32();
                    if (version != Version) { Console.WriteLine("[studio] " + path + ": version " + version + ", expected " + Version); return null; }
                    int mlen = (int)br.ReadUInt32();
                    if (mlen != ManifestLength) return null;
                    AvatarRecord r = new AvatarRecord();
                    r.Manifest = br.ReadBytes(mlen);
                    if (!IsValid(r.Manifest)) { Console.WriteLine("[studio] " + path + ": manifest does not parse"); return null; }
                    r.Name = Clip(Encoding.UTF8.GetString(br.ReadBytes((int)br.ReadUInt32())));
                    r.Created = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                    r.Modified = new DateTime(br.ReadInt64(), DateTimeKind.Utc);
                    int tlen = (int)br.ReadUInt32();
                    if (tlen > 0) r.ThumbnailPng = br.ReadBytes(tlen);
                    r.Path = path;
                    return r;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[studio] " + path + ": " + e.Message);
                return null;
            }
        }

        /// <summary>Writes the record, moving its file if the name changed; throws on I/O failure.</summary>
        public void Save(AvatarRecord r)
        {
            string dir = Directory;
            System.IO.Directory.CreateDirectory(dir);
            string want = System.IO.Path.Combine(dir, FileNameFor(r.Name));
            r.Modified = DateTime.UtcNow;
            if (r.Created == default(DateTime)) r.Created = r.Modified;
            File.WriteAllBytes(want, Serialize(r));
            if (r.Path != null && !string.Equals(r.Path, want, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(r.Path); } catch (Exception e) { Console.WriteLine("[studio] left behind " + r.Path + ": " + e.Message); }
                // its outfits (Outfits.cs) move with it
                string from = OutfitStore.DirFor(r.Path), to = OutfitStore.DirFor(want);
                try { if (System.IO.Directory.Exists(from) && !System.IO.Directory.Exists(to)) System.IO.Directory.Move(from, to); }
                catch (Exception e) { Console.WriteLine("[studio] outfits left behind in " + from + ": " + e.Message); }
            }
            r.Path = want;
            if (!Records.Contains(r)) Records.Add(r);
            Console.WriteLine("[studio] saved " + want);
        }

        /// <summary>The record as a .avatar file's bytes.</summary>
        public static byte[] Serialize(AvatarRecord r)
        {
            MemoryStream ms = new MemoryStream();
            using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8))
            {
                bw.Write(Encoding.ASCII.GetBytes(Magic));
                bw.Write(Version);
                bw.Write((uint)ManifestLength);
                bw.Write(r.Manifest, 0, ManifestLength);
                byte[] name = Encoding.UTF8.GetBytes(r.Name);
                bw.Write((uint)name.Length);
                bw.Write(name);
                bw.Write(r.Created.Ticks);
                bw.Write(r.Modified.Ticks);
                bw.Write((uint)(r.ThumbnailPng == null ? 0 : r.ThumbnailPng.Length));
                if (r.ThumbnailPng != null) bw.Write(r.ThumbnailPng);
                bw.Flush();
                return ms.ToArray();
            }
        }

        public void Delete(AvatarRecord r)
        {
            if (r.Path != null && File.Exists(r.Path)) File.Delete(r.Path);
            string outfits = OutfitStore.DirFor(r.Path);                   // its outfits go with it
            if (outfits != null && System.IO.Directory.Exists(outfits)) System.IO.Directory.Delete(outfits, true);
            Records.Remove(r);
            Console.WriteLine("[studio] deleted " + (r.Path ?? r.Name));
        }

        public AvatarRecord Create(string name, byte[] manifest)
        {
            AvatarRecord r = new AvatarRecord();
            r.Name = Clip(name);
            r.Manifest = ToManifest(manifest);
            r.Created = r.Modified = DateTime.UtcNow;
            return r;
        }

        /// <summary>"Hero" -> "Hero 2" when "Hero" is taken, within the 15-character limit.</summary>
        public string UniqueName(string wanted)
        {
            string name = Clip(wanted);
            if (!Exists(name)) return name;
            for (int i = 2; i < 1000; i++)
            {
                string suffix = " " + i;
                string trunk = name.Length + suffix.Length > MaxNameLength
                    ? name.Substring(0, MaxNameLength - suffix.Length).TrimEnd()
                    : name;
                string candidate = trunk + suffix;
                if (!Exists(candidate)) return candidate;
            }
            return name;
        }

        public bool Exists(string name)
        {
            foreach (AvatarRecord r in Records)
                if (string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        public AvatarRecord Find(string name)
        {
            foreach (AvatarRecord r in Records)
                if (string.Equals(r.Name, name, StringComparison.CurrentCultureIgnoreCase)) return r;
            return null;
        }

        /// <summary>The record whose manifest is these exact bytes, or null.</summary>
        public AvatarRecord Matching(byte[] manifest)
        {
            if (manifest == null) return null;
            foreach (AvatarRecord r in Records)
                if (SameBytes(r.Manifest, manifest)) return r;
            return null;
        }

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string FileNameFor(string name)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name)
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "avatar";
            return s + ".avatar";
        }

        public static string Clip(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Avatar";
            string s = name.Trim();
            if (s.Length > MaxNameLength) s = s.Substring(0, MaxNameLength).TrimEnd();
            return s.Length == 0 ? "Avatar" : s;
        }

        /// <summary>1000 bytes -> as is; 1021 -> the manifest inside the Xbox descriptor; else null.</summary>
        public static byte[] ToManifest(byte[] raw)
        {
            if (raw == null) return null;
            if (raw.Length == ManifestLength) return (byte[])raw.Clone();
            if (raw.Length == XboxDescriptorLength)
            {
                byte[] m = new byte[ManifestLength];
                Buffer.BlockCopy(raw, 1, m, 0, ManifestLength);
                return m;
            }
            return null;
        }

        public static bool IsValid(byte[] manifest)
        {
            try { return manifest != null && manifest.Length == ManifestLength && AvatarManifest.Create(manifest) != null; }
            catch { return false; }
        }
    }
}
