// Scripted runs (AVATAR_STUDIO_HIDE_WINDOW=1) keep the window off-screen and hidden.
// SDL2 or SDL3, whichever FNA loaded; the no-activation hint is named per version.

using System;
using System.Runtime.InteropServices;

namespace AvatarStudio
{
    static class Unattended
    {
        public static readonly bool Hide = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVATAR_STUDIO_HIDE_WINDOW"))
                                           && Environment.GetEnvironmentVariable("AVATAR_STUDIO_HIDE_WINDOW") != "0";
        private static bool hidden;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("SDL2.dll", EntryPoint = "SDL_SetHint", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetHint2([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
        [DllImport("SDL2.dll", EntryPoint = "SDL_SetWindowPosition", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SetWindowPosition2(IntPtr window, int x, int y);
        [DllImport("SDL2.dll", EntryPoint = "SDL_HideWindow", CallingConvention = CallingConvention.Cdecl)]
        private static extern void HideWindow2(IntPtr window);

        [DllImport("SDL3.dll", EntryPoint = "SDL_SetHint", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SetHint3([MarshalAs(UnmanagedType.LPStr)] string name, [MarshalAs(UnmanagedType.LPStr)] string value);
        [DllImport("SDL3.dll", EntryPoint = "SDL_SetWindowPosition", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte SetWindowPosition3(IntPtr window, int x, int y);
        [DllImport("SDL3.dll", EntryPoint = "SDL_HideWindow", CallingConvention = CallingConvention.Cdecl)]
        private static extern byte HideWindow3(IntPtr window);

        private static bool Sdl3 { get { return GetModuleHandle("SDL3.dll") != IntPtr.Zero; } }

        /// <summary>At the end of Initialize, before the window is shown.</summary>
        public static void BeforeShow(IntPtr window)
        {
            if (!Hide) return;
            try
            {
                if (Sdl3)
                {
                    SetHint3("SDL_WINDOW_ACTIVATE_WHEN_SHOWN", "0");
                    SetWindowPosition3(window, -16000, -16000);
                }
                else
                {
                    SetHint2("SDL_WINDOW_NO_ACTIVATION_WHEN_SHOWN", "1");
                    SetWindowPosition2(window, -16000, -16000);
                }
                Console.WriteLine("[studio] unattended run: window off-screen and hidden (SDL" + (Sdl3 ? "3" : "2") + ")");
            }
            catch (Exception e) { Console.WriteLine("[studio] hide window: " + e.Message); }
        }

        /// <summary>After each present: the first one hides the window.</summary>
        public static void AfterPresent(IntPtr window)
        {
            if (!Hide || hidden) return;
            hidden = true;
            try { if (Sdl3) HideWindow3(window); else HideWindow2(window); }
            catch (Exception e) { Console.WriteLine("[studio] hide window: " + e.Message); }
        }
    }
}
