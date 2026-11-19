// The editor's music: its modes, fades and the gaps between tracks, as the editor plays them.

using System;
using System.Collections.Generic;

namespace AvatarStudio
{
    static class Music
    {
        private const string IntroSelection = "Avatar_Steve_Burke_Ver3_Intro_Selection_Screen.xma";
        private const string IntroMain = "Avatar_Steve_Burke_Ver3_Intro_Main_Screen.xma";
        private const string Title = "Avatars2_Title.xma", Outro = "Avatar_Steve_Burke_Ver3_Outro_Music.xma", Silence = "silence.xma";
        private static readonly string[] Editing =
        {
            "Avatars2_Background_01.xma", "Avatars2_Background_02.xma", "Avatars2_Background_03.xma", "Avatars2_Background_04.xma",
            "Avatars2_Background_05.xma", "Avatars2_Background_06.xma", "Avatars2_Background_07.xma", "Avatars2_Background_08.xma",
        };

        public const int ModeLineup = 0, ModeEditorStart = 1, ModeEditing = 2, ModeExit = 3, ModeQuiet = 4;

        private sealed class Mode { public string First; public string[] Rest; public bool Gapped; }
        private static readonly Mode[] Modes =
        {
            new Mode { First = IntroSelection, Rest = new[] { Title, Silence }, Gapped = false },
            new Mode { First = IntroMain, Rest = Editing, Gapped = true },
            new Mode { First = null, Rest = Editing, Gapped = true },
            new Mode { First = Outro, Rest = new string[0], Gapped = false },
            new Mode { First = null, Rest = new string[0], Gapped = true },
            new Mode { First = Title, Rest = new[] { Silence }, Gapped = false },
        };

        private static readonly Sounds.Voice voice = new Sounds.Voice(4, null);
        private static readonly Random rng = new Random();
        private static int requested = ModeQuiet, current = -1;
        private static float requestedFade = -1f, rate, clock, tick200, tick100;
        private static float lastPlaying, lastActivity, gap;
        private static bool firstPending, activity;
        private static readonly List<string> list = new List<string>();

        public static int Current { get { return current; } }
        public static bool Playing { get { return voice.Playing; } }

        /// <summary>The mode, and the fade of the music playing now.</summary>
        public static void SetMode(int mode, float fade) { requested = mode; requestedFade = fade; }

        /// <summary>The behaviour's activity flag (+44).</summary>
        public static void Activity() { activity = true; }

        public static void Update(float dt)
        {
            clock += dt;
            tick100 += dt;
            while (tick100 >= 0.1f) { tick100 -= 0.1f; Timer1201(); }
            tick200 += dt;
            while (tick200 >= 0.2f) { tick200 -= 0.2f; Timer1200(); }
        }

        private static void Timer1201()
        {
            if (requested != current)
            {
                Enter(Modes[requested]);
                if (requestedFade != -1f) rate = (voice.Db - -30f) / Math.Max(requestedFade, 0.0001f);
                current = requested;
            }
            if (rate != 0f)
            {
                float v = voice.Db - 0.1f * rate;
                voice.Db = v;
                if (v <= -30f) { voice.Stop(); rate = 0f; }
            }
        }

        private static void Timer1200()
        {
            if (voice.Playing) lastPlaying = clock;
            if (activity) { activity = false; lastActivity = clock; }
            if (voice.Playing || current < 0) return;
            string next = Next(Modes[current]);
            if (next == null) return;
            voice.Stop();
            voice.Db = -12f;
            voice.Apply(2, next);
        }

        /// <summary>The strategies' enter (sub_920AD910 / sub_920AD968 / 0x920ADA20).</summary>
        private static void Enter(Mode m)
        {
            firstPending = m.First != null;
            list.Clear();
            Shuffle(m);
            gap = 45f + rng.Next(30);
            lastActivity = clock;
        }

        private static void Shuffle(Mode m)
        {
            list.AddRange(m.Rest);
            int n = list.Count;
            for (int k = 0; k < 2 * n; k++)
            {
                int a = rng.Next(n), b = rng.Next(n);
                if (a == b) b = (b + 1) % n;
                string t = list[a]; list[a] = list[b]; list[b] = t;
            }
        }

        private static string Pop(Mode m)
        {
            if (list.Count == 0) Shuffle(m);
            if (list.Count == 0) return null;
            string s = list[0];
            list.RemoveAt(0);
            return s;
        }

        /// <summary>The strategies' next (sub_920ADBD0 gapped, sub_920ADCD0 back to back).</summary>
        private static string Next(Mode m)
        {
            if (firstPending) { firstPending = false; return m.First; }
            if (!m.Gapped) return Pop(m);
            float silent = clock - lastPlaying, idle = clock - lastActivity;
            if (silent > 3f && idle < 10f && idle > 1f) return Pop(m);
            if (silent <= gap) return null;
            string s = Pop(m);
            gap = 45f + rng.Next(30);
            return s;
        }

        /// <summary>The editor left: the music stops at once.</summary>
        public static void Stop()
        {
            voice.Stop();
            requested = ModeQuiet; requestedFade = -1f; current = ModeQuiet; rate = 0f;
            Enter(Modes[ModeQuiet]);
        }
    }
}
