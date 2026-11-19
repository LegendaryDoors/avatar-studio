// Avatars in and out of the library as files: the gallery's Import and Export.
// Reads .avatar files, bare manifests, Xbox descriptors and Xbox 360 profile packages.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AvatarStudio
{
    sealed class ImportedAvatar
    {
        public string Name;
        public byte[] Manifest;
        public byte[] Png;                 // .avatar files carry one
        public bool NameFromProfile;       // a profile: no name in it, ask for one
        public int ItemsWritten;           // non-stock items unpacked to ItemsDir
    }

    static class AvatarFiles
    {
        public enum Format { Manifest, Avatar, Png }

        /// <summary>The dashboard's GPD in a profile and its avatar setting (binary, 1000 bytes).</summary>
        private const string DashboardGpd = "FFFE07D1.gpd";
        private const uint AvatarSetting = 0x63E80044;

        /// <summary>A profile's non-stock items: AVATAR_STUDIO_ITEMS, else %LOCALAPPDATA%\Avatar Studio\items.</summary>
        public static string ItemsDir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_ITEMS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    Path.Combine("Avatar Studio", "items"));
            }
        }

        /// <summary>The avatar in a file, or null with the reason.</summary>
        public static ImportedAvatar Read(string path, out string why)
        {
            why = null;
            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (Exception e) { why = e.Message; return null; }

            if (StfsPackage.LooksLikePackage(raw)) return ReadProfile(raw, out why);

            AvatarRecord r = AvatarLibrary.ReadFile(path);
            if (r == null)
            {
                why = raw.Length == AvatarLibrary.ManifestLength || raw.Length == AvatarLibrary.XboxDescriptorLength
                    ? "the avatar in it does not parse"
                    : "not an avatar file (.avatar, a 1000-byte avatar.bin, a 1021-byte descriptor or an Xbox profile)";
                return null;
            }
            ImportedAvatar a = new ImportedAvatar();
            a.Name = r.Name;
            a.Manifest = r.Manifest;
            a.Png = r.ThumbnailPng;
            return a;
        }

        private static ImportedAvatar ReadProfile(byte[] raw, out string why)
        {
            why = null;
            StfsPackage pkg;
            try { pkg = new StfsPackage(raw); }
            catch (Exception e) { why = e.Message; return null; }
            StfsEntry gpd = pkg.Find(DashboardGpd);
            if (gpd == null) { why = "an Xbox package, but not a profile (no " + DashboardGpd + ")"; return null; }
            byte[] m;
            try { m = Gpd.BinarySetting(pkg.Read(gpd), AvatarSetting); }
            catch (Exception e) { why = "its " + DashboardGpd + " does not read: " + e.Message; return null; }
            if (m == null) { why = "this profile has no avatar"; return null; }
            if (m.Length != AvatarLibrary.ManifestLength || !AvatarLibrary.IsValid(m)) { why = "the profile's avatar does not parse"; return null; }

            ImportedAvatar a = new ImportedAvatar();
            a.Name = "Avatar";
            a.Manifest = m;
            a.NameFromProfile = true;
            a.ItemsWritten = UnpackItems(pkg);
            return a;
        }

        /// <summary>Every AvatarAssets\&lt;GUID&gt; container in the profile, as &lt;guid&gt;.strb, .png and .txt in ItemsDir.</summary>
        private static int UnpackItems(StfsPackage pkg)
        {
            int n = 0;
            foreach (StfsEntry e in pkg.Entries)
            {
                if (e.IsFolder) continue;
                string[] parts = e.Path.Split('\\');
                if (parts.Length != 2 || !string.Equals(parts[0], "AvatarAssets", StringComparison.OrdinalIgnoreCase) || parts[1].Length != 32) continue;
                string g = parts[1].ToLowerInvariant();
                string guid = g.Substring(0, 8) + "-" + g.Substring(8, 4) + "-" + g.Substring(12, 4) + "-" + g.Substring(16, 4) + "-" + g.Substring(20, 12);
                try
                {
                    StfsPackage inner = new StfsPackage(pkg.Read(e));
                    string dir = ItemsDir;
                    Directory.CreateDirectory(dir);
                    foreach (StfsEntry f in inner.Entries)
                    {
                        if (f.IsFolder) continue;
                        string name = f.Path.Substring(f.Path.LastIndexOf('\\') + 1).ToLowerInvariant();
                        if (name.StartsWith("asset") && name.EndsWith(".bin"))
                        {
                            byte[] b = inner.Read(f);
                            if (b.Length < 4 || Encoding.ASCII.GetString(b, 0, 4) != "STRB") { Console.WriteLine("[studio] item " + guid + ": " + name + " is not an STRB; skipped"); continue; }
                            File.WriteAllBytes(Path.Combine(dir, guid + ".strb"), b);
                            if (inner.DisplayName.Length > 0) File.WriteAllText(Path.Combine(dir, guid + ".txt"), inner.DisplayName, new UTF8Encoding(false));
                            Console.WriteLine("[studio] item " + guid + " (" + b.Length + " bytes) \"" + inner.DisplayName + "\"");
                            n++;
                        }
                        else if (name == "icon.png") File.WriteAllBytes(Path.Combine(dir, guid + ".png"), inner.Read(f));
                    }
                }
                catch (Exception ex) { Console.WriteLine("[studio] item " + guid + ": " + ex.Message); }
            }
            return n;
        }

        /// <summary>Writes a manifest or a .avatar; throws on I/O failure.</summary>
        public static void Write(AvatarRecord r, string path, Format format)
        {
            if (format == Format.Manifest) File.WriteAllBytes(path, r.Manifest);
            else if (format == Format.Avatar) File.WriteAllBytes(path, AvatarLibrary.Serialize(r));
            else throw new ArgumentException("a picture is written from its render", "format");
        }

        /// <summary>The file name the export dialog suggests: the avatar's name, made safe.</summary>
        public static string SafeName(string name)
        {
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "avatar" : s;
        }
    }

    /// <summary>The Windows file dialogs; never called in a scripted run.</summary>
    static class FileDialogs
    {
        private sealed class Owner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; set; }
        }

        private static System.Windows.Forms.IWin32Window MainWindow()
        {
            IntPtr h = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            return h == IntPtr.Zero ? null : new Owner { Handle = h };
        }

        /// <summary>One or more files to import, or an empty array when cancelled.</summary>
        public static string[] OpenAvatars()
        {
            using (System.Windows.Forms.OpenFileDialog d = new System.Windows.Forms.OpenFileDialog())
            {
                d.Title = "Import Avatars";
                // a profile package is named for its XUID: E and 15 hex digits
                d.Filter = "Avatars and Xbox profiles|*.avatar;*.bin;E???????????????|All files (*.*)|*.*";
                d.Multiselect = true;
                d.CheckFileExists = true;
                System.Windows.Forms.DialogResult res = MainWindow() != null ? d.ShowDialog(MainWindow()) : d.ShowDialog();
                return res == System.Windows.Forms.DialogResult.OK ? d.FileNames : new string[0];
            }
        }

        /// <summary>Where to export and as what, or null when cancelled.</summary>
        public static string SaveAvatar(string name, out AvatarFiles.Format format)
        {
            format = AvatarFiles.Format.Manifest;
            using (System.Windows.Forms.SaveFileDialog d = new System.Windows.Forms.SaveFileDialog())
            {
                d.Title = "Export \"" + name + "\"";
                d.Filter = "Avatar manifest, avatar.bin (*.bin)|*.bin|Avatar Studio avatar (*.avatar)|*.avatar|Picture (*.png)|*.png";
                d.FilterIndex = 1;
                d.FileName = AvatarFiles.SafeName(name);
                d.AddExtension = true;
                d.OverwritePrompt = true;
                System.Windows.Forms.DialogResult res = MainWindow() != null ? d.ShowDialog(MainWindow()) : d.ShowDialog();
                if (res != System.Windows.Forms.DialogResult.OK) return null;
                format = d.FilterIndex == 2 ? AvatarFiles.Format.Avatar : d.FilterIndex == 3 ? AvatarFiles.Format.Png : AvatarFiles.Format.Manifest;
                return d.FileName;
            }
        }

        /// <summary>The console font for the setup: xenonjklatin.xtt or a zip holding it, or null.</summary>
        public static string PickFontBase()
        {
            using (System.Windows.Forms.OpenFileDialog d = new System.Windows.Forms.OpenFileDialog())
            {
                d.Title = "Choose xenonjklatin.xtt, or a zip holding it";
                d.Filter = "Console font or zip (*.xtt, *.zip)|*.xtt;*.zip|All files (*.*)|*.*";
                d.CheckFileExists = true;
                System.Windows.Forms.DialogResult res = MainWindow() != null ? d.ShowDialog(MainWindow()) : d.ShowDialog();
                return res == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
            }
        }

        /// <summary>The player's system update: its zip, or dash.xex in $SystemUpdate, or null.</summary>
        public static string PickUpdate()
        {
            using (System.Windows.Forms.OpenFileDialog d = new System.Windows.Forms.OpenFileDialog())
            {
                d.Title = "Choose the Xbox 360 system update: SystemUpdate_17559_USB.zip, or dash.xex in its $SystemUpdate folder";
                d.Filter = "Xbox 360 system update (*.zip, dash.xex)|*.zip;dash.xex|All files (*.*)|*.*";
                d.CheckFileExists = true;
                System.Windows.Forms.DialogResult res = MainWindow() != null ? d.ShowDialog(MainWindow()) : d.ShowDialog();
                return res == System.Windows.Forms.DialogResult.OK ? d.FileName : null;
            }
        }

        /// <summary>A folder, through Windows' own picker (IFileOpenDialog, FOS_PICKFOLDERS), or null.</summary>
        public static string PickFolder(string title)
        {
            IFileDialog d = (IFileDialog)new FileOpenDialogCoClass();
            try
            {
                uint fos;
                d.GetOptions(out fos);
                d.SetOptions(fos | 0x20 | 0x40);                 // FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM
                d.SetTitle(title);
                IntPtr owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (d.Show(owner) != 0) return null;              // cancelled (0x800704C7) or failed
                IShellItem item;
                d.GetResult(out item);
                string path;
                item.GetDisplayName(0x80058000, out path);        // SIGDN_FILESYSPATH
                return path;
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(d); }
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
        private class FileOpenDialogCoClass { }

        // IFileDialog's methods in vtable order, through GetResult (shobjidl_core.h)
        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("42F85136-DB7E-439C-85F1-E4075D135FC8"),
         System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [System.Runtime.InteropServices.PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string name);
            void GetFileName([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string name);
            void SetTitle([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string label);
            void SetFileNameLabel([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
        }

        [System.Runtime.InteropServices.ComImport, System.Runtime.InteropServices.Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
         System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint sigdn, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }
    }
}
