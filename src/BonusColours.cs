// The editor's hidden hair colours: nine more chips after a minute on My Features.

using System;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class BonusColours
    {
        /// <summary>The nine, in the table's order (0x92006088, ARGB).</summary>
        public static readonly uint[] Colours =
        {
            0xFFF1A6A0, 0xFFB33526, 0xFF611F2B, 0xFF5862A0, 0xFF6E6286, 0xFF785073, 0xFF92BCBB, 0xFF7EAA53, 0xFF47706A,
        };

        public static Colorb At(int i) { uint c = Colours[i]; return new Colorb((byte)(c >> 16), (byte)(c >> 8), (byte)c); }

        /// <summary>One of the nine (hair colours have alpha 0xFF, so RGB decides).</summary>
        public static bool Contains(Colorb c)
        {
            foreach (uint k in Colours)
                if ((byte)(k >> 16) == c.red && (byte)(k >> 8) == c.green && (byte)k == c.blue) return true;
            return false;
        }

        /// <summary>The Hair Color list: the nine first while unlocked.</summary>
        public Colorb[] HairList(Colorb[] palette)
        {
            if (!Unlocked) return palette;
            Colorb[] all = new Colorb[Colours.Length + palette.Length];
            for (int i = 0; i < Colours.Length; i++) all[i] = At(i);
            Array.Copy(palette, 0, all, Colours.Length, palette.Length);
            return all;
        }

        private float clock;                    // +12
        private int lastState = -1, lastPage = -1;

        /// <summary>+17: My Features was left (or is) in its window.</summary>
        public bool Window { get; private set; }
        /// <summary>+16: the nine are on the Hair Color screen.</summary>
        public bool Unlocked { get; private set; }

        /// <summary>The unlock clock.</summary>
        public float Clock { get { return clock; } }
        public void TestClock(float t) { clock = t; }

        public int Played { get; private set; }

        /// <summary>One frame; true when sfxBonusUnlocked plays. `state` and `page`: the screen shown.</summary>
        public bool Update(float dt, int state, int page, Colorb hair)
        {
            bool entered = state != lastState;
            if (entered || page != lastPage) clock = 0f;
            lastState = state; lastPage = page;
            clock += dt;
            if (state == EditorCamera.Features)
            {
                Window = Unlocked = clock > 60f && clock < 66f;
                return false;
            }
            if (state == EditorCamera.Hair || state == EditorCamera.Color)
            {
                if (Window) { if (entered) Played++; return entered; }
                if (!Unlocked && Contains(hair)) Unlocked = true;
                return false;
            }
            Window = false;
            if (state != EditorCamera.HairColor) Unlocked = false;
            return false;
        }
    }
}
