// The exe's icon on the window and the taskbar: SDL loads it by the hint
// SDL_WINDOWS_INTRESOURCE_ICON (32512, where csc puts the -win32icon icon).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace AvatarStudio
{
    static class WindowIcon
    {
        public const int Resource = 32512;

        /// <summary>Before the game is made, so SDL registers its window class with it.</summary>
        public static void Use()
        {
            string id = Resource.ToString(CultureInfo.InvariantCulture);
            Environment.SetEnvironmentVariable("SDL_WINDOWS_INTRESOURCE_ICON", id);
            Environment.SetEnvironmentVariable("SDL_WINDOWS_INTRESOURCE_ICON_SMALL", id);
        }

        private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumProc proc, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] private static extern IntPtr GetClassLongPtr(IntPtr hwnd, int index);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll", EntryPoint = "GetIconInfoExW", CharSet = CharSet.Unicode)] private static extern bool GetIconInfoEx(IntPtr icon, ref IconInfoEx info);
        private const int GCLP_HICON = -14, GCLP_HICONSM = -34;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct IconInfoEx                               // ICONINFOEXW
        {
            public int Size;
            public bool IsIcon;
            public int HotspotX, HotspotY;
            public IntPtr Mask, Colour;
            public ushort ResourceId;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Module;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ResourceName;
        }

        /// <summary>Where an icon was loaded from, and whether that is this exe's 32512.</summary>
        private static string Origin(IntPtr icon, out bool ours)
        {
            ours = false;
            if (icon == IntPtr.Zero) return "none";
            IconInfoEx info = new IconInfoEx();
            info.Size = Marshal.SizeOf(typeof(IconInfoEx));
            if (!GetIconInfoEx(icon, ref info)) return "unreadable";
            if (info.Mask != IntPtr.Zero) DeleteObject(info.Mask);
            if (info.Colour != IntPtr.Zero) DeleteObject(info.Colour);
            if (string.IsNullOrEmpty(info.Module)) return "not from a resource";
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
            ours = info.ResourceId == Resource && string.Equals(info.Module, exe, StringComparison.OrdinalIgnoreCase);
            return ours ? "the exe's " + Resource : System.IO.Path.GetFileName(info.Module) + " " + info.ResourceId;
        }

        /// <summary>Whether this thread's SDL window class shows the exe's icon, big and small.
        /// By origin (GetIconInfoEx): under SDL3 the handles differ from our own LoadIcon's.</summary>
        public static bool Check(out string report)
        {
            var found = new List<string>();
            bool ok = false;
            EnumThreadWindows(GetCurrentThreadId(), delegate(IntPtr hwnd, IntPtr l)
            {
                StringBuilder name = new StringBuilder(256);
                GetClassName(hwnd, name, name.Capacity);
                if (!name.ToString().StartsWith("SDL", StringComparison.Ordinal)) return true;
                bool bigOurs, smallOurs;
                string big = Origin(GetClassLongPtr(hwnd, GCLP_HICON), out bigOurs);
                string small = Origin(GetClassLongPtr(hwnd, GCLP_HICONSM), out smallOurs);
                ok |= bigOurs && smallOurs;
                found.Add(name + " (big " + big + ", small " + small + ")");
                return true;
            }, IntPtr.Zero);
            report = found.Count == 0 ? "no SDL window on this thread" : "window class " + string.Join(", ", found.ToArray());
            return ok;
        }
    }
}
