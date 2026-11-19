// Avatar Studio's entry point: an Xbox 360 avatar editor and library.

using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace AvatarStudio
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // the setup installers' self-check: no window
            if (Array.IndexOf(args, "--install-check") >= 0) return SetupInstall.Check(args);
            // Run from inside the zip, FNA is missing: say how to unzip.
            if (!File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FNA.dll")))
            {
                Tell("Avatar Studio can't find the files that came with it.\n\nUnzip the whole download first "
                     + "(right-click it, then Extract All), then start AvatarStudio.exe from the folder that makes.", args);
                return 1;
            }
            return Run(args);
        }

        // apart from Main, so Main can still report a missing FNA
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Run(string[] args)
        {
            WindowIcon.Use();                       // before FNA starts SDL (WindowIcon.cs)
            StudioGame g = null;
            try
            {
                g = new StudioGame(args);
                g.Run();
                StudioGame done = g;
                g = null;
                done.Dispose();
                return StudioGame.ExitCode;
            }
            catch (Exception e)
            {
                // closing may throw too; keep the first exception
                if (g != null)
                {
                    try { g.Dispose(); } catch (Exception closing) { Console.Error.WriteLine("[studio] while closing: " + closing.Message); }
                    g = null;
                }
                Console.Error.WriteLine("[studio] fatal: " + e);
                // details to crash.txt, one plain sentence on screen; a scripted run writes no real file
                string log = null;
                if (!Scripted(args))
                    try
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio");
                        Directory.CreateDirectory(dir);
                        log = Path.Combine(dir, "crash.txt");
                        File.WriteAllText(log, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + e + Environment.NewLine);
                    }
                    catch { log = null; }
                Tell("Avatar Studio stopped because of a problem it didn't expect:\n\n" + e.Message
                     + (log != null ? "\n\nThe details are in " + log + "." : ""), args);
                return 1;
            }
        }

        /// <summary>A message box, except in scripted runs (nobody to click OK).</summary>
        private static void Tell(string text, string[] args)
        {
            Console.Error.WriteLine("[studio] " + text.Replace("\n\n", " "));
            if (!Scripted(args))
                try { System.Windows.Forms.MessageBox.Show(text, "Avatar Studio"); } catch { }
        }

        private static bool Scripted(string[] args)
        {
            return Unattended.Hide || Array.IndexOf(args, "--selftest") >= 0 || Array.IndexOf(args, "--handoff-test") >= 0
                   || Array.IndexOf(args, "--fuzz") >= 0 || Array.IndexOf(args, "--setup-shots") >= 0
                   || Array.IndexOf(args, "--setup-install-test") >= 0 || Array.IndexOf(args, "--import-test") >= 0;
        }
    }
}
