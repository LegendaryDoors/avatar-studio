// The avatar's voice: the editor's sound scenes playing the behaviour's voice id (+40).

using Microsoft.Xna.Framework.Audio;

namespace AvatarStudio
{
    sealed class AvatarVoice
    {
        // +40 -> scnPlayAvatarSound's burp files (BelchHigher .. BelchLow)
        private static readonly string[] Burps =
        {
            null, "Burp_Highest.xma", "Burp_high2.xma", "Burp_high.xma", "Burp_medium3.xma", "Burp_medium.xma", "Burp_low3.xma", "Burp_low.xma",
        };
        public const int Spin = 8, Bonk = 9, Wobble = 10;

        private int seen;                                       // each scene's +12
        private SoundEffectInstance burp, spin, wobble, thonk, tweet;
        private float spinDue = -1f, wobbleDue = -1f, bonkDue = -1f;

        /// <summary>Sounds started, and the last one's file.</summary>
        public int Started { get; private set; }
        public string Last { get; private set; }

        /// <summary>One frame; `voice` is the behaviour's +40.</summary>
        public void Tick(float now, int voice)
        {
            if (voice != seen)
            {
                seen = voice;
                if (voice == 0) Stop(ref burp);                                     // Idle, frame 0
                else if (voice >= 1 && voice < Burps.Length) { Stop(ref burp); burp = Start(Burps[voice]); }
                else if (voice == Spin) { Stop(ref spin); spinDue = now + (53 - 2) / 60f; }
                else if (voice == Bonk) { Stop(ref thonk); Stop(ref tweet); bonkDue = now + 1f / 60f; }
                else if (voice == Wobble) { Stop(ref wobble); wobbleDue = now + 1f / 60f; }
            }
            if (spinDue >= 0f && now >= spinDue) { spinDue = -1f; spin = Start("character_spinning_on_spot.xma"); }
            if (wobbleDue >= 0f && now >= wobbleDue) { wobbleDue = -1f; wobble = Start("Wobble.xma"); }
            if (bonkDue >= 0f && now >= bonkDue) { bonkDue = -1f; thonk = Start("glassy_thonk.xma"); tweet = Start("bird_tweets.xma"); }
        }

        private SoundEffectInstance Start(string file)
        {
            Started++; Last = file;
            return Sounds.Start(file);
        }

        private static void Stop(ref SoundEffectInstance i)
        {
            if (i == null) return;
            try { i.Stop(); } catch (System.Exception) { }
            i = null;
        }
    }
}
