// The editor's sounds: MEDIA's XMA files, decoded to PCM WAVE by the setup, played as XUI plays them.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Audio;

namespace AvatarStudio
{
    static class Sounds
    {
        private static readonly Dictionary<string, SoundEffect> cache = new Dictionary<string, SoundEffect>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<SoundEffectInstance> live = new List<SoundEffectInstance>();
        private static bool broken, probed;
        private static readonly float Level = Environment.GetEnvironmentVariable("AVATAR_STUDIO_HIDE_WINDOW") == "1" ? 0f : 1f;

        /// <summary>Sounds started so far, and the last one's file.</summary>
        public static int Started { get; private set; }
        public static string Last { get; private set; }

        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_SOUNDS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "sounds");
            }
        }

        /// <summary>Whether there is a sound device. A SoundEffect whose constructor fails still runs its
        /// finalizer, which throws where nothing can catch it, so none is made without one.</summary>
        private static bool DeviceReady()
        {
            if (probed) return !broken;
            probed = true;
            try { float level = SoundEffect.MasterVolume; }
            catch (Exception e)
            {
                Console.WriteLine("[studio] sounds: " + e.GetType().Name + ": " + e.Message);
                broken = true;
            }
            return !broken;
        }

        private static SoundEffect Load(string file)
        {
            if (broken || string.IsNullOrEmpty(file) || !DeviceReady()) return null;
            SoundEffect s;
            if (cache.TryGetValue(file, out s)) return s;
            string path = Path.Combine(Folder, Path.GetFileNameWithoutExtension(file) + ".wav");
            try
            {
                if (File.Exists(path)) using (FileStream fs = File.OpenRead(path)) s = SoundEffect.FromStream(fs);
            }
            catch (Exception e)
            {
                // no audio device, or a file FNA cannot read: silent from here on
                Console.WriteLine("[studio] sounds: " + e.GetType().Name + ": " + e.Message);
                broken = true; s = null;
            }
            cache[file] = s;
            return s;
        }

        /// <summary>The file from its start on a new voice; `db` is the element's Volume.</summary>
        public static SoundEffectInstance Start(string file, float db = 0f)
        {
            SoundEffect s = Load(file);
            if (s == null) return null;
            try
            {
                SoundEffectInstance i = s.CreateInstance();
                i.Volume = Gain(db);
                i.Play();
                live.Add(i);
                Started++; Last = file;
                return i;
            }
            catch (Exception e) { Console.WriteLine("[studio] sounds: " + e.Message); broken = true; return null; }
        }

        /// <summary>A named element played by the editor's own code (AE_XuiSoundPlay).</summary>
        public static void Play(string file) { Start(file); }

        /// <summary>A level in decibels as FNA's linear volume, times the run's level.</summary>
        private static float Gain(float db) { return Level * (float)Math.Min(1.0, Math.Pow(10.0, db / 20.0)); }

        /// <summary>Every frame: voices that have finished are let go.</summary>
        public static void Update()
        {
            for (int i = live.Count - 1; i >= 0; i--)
                if (live[i].IsDisposed || live[i].State == SoundState.Stopped)
                {
                    if (!live[i].IsDisposed) live[i].Dispose();
                    live.RemoveAt(i);
                }
        }

        /// <summary>At the end, before FNA lets its audio device go: every voice disposed (finalizers crashed it).</summary>
        public static void Shutdown()
        {
            broken = true;
            foreach (SoundEffectInstance i in live)
                try { if (!i.IsDisposed) { i.Stop(); i.Dispose(); } } catch (Exception) { }
            live.Clear();
            foreach (SoundEffect s in cache.Values)
                try { if (s != null && !s.IsDisposed) s.Dispose(); } catch (Exception) { }
            cache.Clear();
        }

        /// <summary>One XuiSoundXAudio element: its State and File as its timeline sets them.</summary>
        public sealed class Voice
        {
            private int state;
            private string file;
            private SoundEffectInstance playing;

            /// <summary>The element's own properties (its State is 4 in every scene here).</summary>
            public Voice(int state, string file) { this.state = state; this.file = file; }

            /// <summary>The values at the playhead: a change of State, or of File, is applied.</summary>
            public void Apply(int newState, string newFile)
            {
                bool changed = newState != state || (newFile != null && newFile != file);
                state = newState;
                if (newFile != null) file = newFile;
                if (!changed) return;
                if ((state & 2) != 0) { Stop(); playing = Start(file, db); }
                else if ((state & 4) != 0) Stop();
            }

            /// <summary>Message 2037: the voice stopped. The element's State is left as it was.</summary>
            public void Stop()
            {
                if (playing != null && !playing.IsDisposed) playing.Stop();
                playing = null;
            }

            /// <summary>Message 2038's answer: the voice is sounding.</summary>
            public bool Playing { get { return playing != null && !playing.IsDisposed && playing.State == SoundState.Playing; } }

            /// <summary>The element's Volume in decibels (messages 2041 / 2042, -96..6), live on the voice.</summary>
            public float Db
            {
                get { return db; }
                set
                {
                    db = Math.Max(-96f, Math.Min(6f, value));
                    if (playing != null && !playing.IsDisposed) playing.Volume = Gain(db);
                }
            }
            private float db;
        }
    }
}
