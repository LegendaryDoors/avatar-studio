// An animation's table track: up to three position + rotation entries per frame,
// sampled like the joints and read back for an actor's root motion.

using System;
using System.Collections.Generic;
using System.IO;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class AnimTable
    {
        public const int MaxEntries = 3;                 // the renderer's +0x12C holds three (count 3)
        private readonly float fps;
        private readonly Pose[][] frames;                 // [frame][entry]

        public int Entries { get { return frames.Length == 0 ? 0 : frames[0].Length; } }

        private AnimTable(float fps, Pose[][] frames) { this.fps = fps; this.frames = frames; }

        private static readonly Dictionary<byte[], AnimTable> cache = new Dictionary<byte[], AnimTable>();

        /// <summary>The table of an animation asset, or null (none, or a logged decode failure).</summary>
        public static AnimTable Of(byte[] strb)
        {
            if (strb == null) return null;
            AnimTable t;
            if (cache.TryGetValue(strb, out t)) return t;
            t = null;
            try
            {
                StrbFile s = StrbFile.Read(strb);
                StrbBlock? anim = null;
                if (s != null) foreach (StrbBlock k in s.Blocks) if (k.Id == (int)StrbBlockId.Animation) { anim = k; break; }
                if (anim != null)
                {
                    // header dwords: 1 frame rate, 4 entries, 8 offset, 9 data size
                    ByteCursor c = new ByteCursor(s.Data, true) { At = anim.Value.Offset };
                    BitReader h = new BitReader(c);
                    int[] d = new int[10];
                    float rate = 0f;
                    for (int k = 0; k < 10; k++) { if (k == 1) rate = h.F(); else d[k] = h.I(32); }
                    if (d[4] > 0 && d[8] > 0 && d[9] > d[8])
                    {
                        byte[] comp = c.Bytes(d[9]);
                        // frame count, entry count, headers last entry first (three at most), then the frames
                        BitReader r = new BitReader(new ByteCursor(comp, true) { At = d[8] });
                        int frames = r.I(32), n = r.I(32);
                        Vector3Unpacker[] pos = new Vector3Unpacker[n], rot = new Vector3Unpacker[n];
                        for (int j = n - 1; j >= 0; j--) { pos[j] = new Vector3Unpacker(); rot[j] = new Vector3Unpacker(); pos[j].Header(r); rot[j].Header(r); }
                        for (int j = n; j < MaxEntries; j++) { new Vector3Unpacker().Header(r); new Vector3Unpacker().Header(r); }
                        Pose[][] f = new Pose[frames][];
                        for (int i = 0; i < frames; i++)
                        {
                            f[i] = new Pose[n];
                            for (int j = 0; j < n; j++)
                            {
                                float x, y, z;
                                pos[j].Next(r, out x, out y, out z); f[i][j].position = new Microsoft.Xna.Framework.Vector3(x, y, z);
                                rot[j].Next(r, out x, out y, out z); f[i][j].rotation = AvatarMath.Exp(x, y, z);
                            }
                        }
                        if (f.Length > 0 && f[0].Length > 0) t = new AnimTable(rate, f);
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("[studio] an animation's table track did not decode: " + e.Message); }
            cache[strb] = t;
            return t;
        }

        /// <summary>The entries at `seconds`, sampled as the joint track is.</summary>
        public void Sample(float seconds, Pose[] into)
        {
            int n = frames.Length;
            float x = seconds * fps;
            int f = Math.Max(0, Math.Min((int)x, n - 1));
            float frac = Math.Max(0f, x - f);
            int next = (f + 1) % n;
            for (int j = 0; j < into.Length && j < frames[f].Length; j++)
                into[j] = frac > 0f ? RestGaze.Mix(frames[f][j], frames[next][j], frac) : frames[f][j];
        }
    }
}
