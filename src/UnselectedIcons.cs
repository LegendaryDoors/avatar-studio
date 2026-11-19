// The editor's lavender 'unselected' look for unfocused tiles: its pixel shader, run on the CPU.

using System;
using Microsoft.Xna.Framework;

namespace AvatarStudio
{
    static class UnselectedIcons
    {
        // Literal bank of the shader container at 0x92002028, bit patterns as stored.
        private const float C254y = 0.75f;   // 0x3F400000
        private const float C254z = 0.05f;   // 0x3D4CCCCD
        private const float C254w = 0.4f;    // 0x3ECCCCCD
        private const float C255x = 0.5f;    // 0x3F000000
        private const float C255y = 1f;      // 0x3F800000
        private const float C255z = 10f;     // 0x41200000
        private const float C255w = 1.3f;    // 0x3FA66666

        /// <summary>shader_unselected_icons_ps over straight-alpha pixels, in place, rounded to 8 bits.</summary>
        public static void Apply(Color[] px)
        {
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                // r1 = tfetch2D(InputTexture, uv).xywz
                float r1x = c.R / 255f, r1y = c.G / 255f, r1z = c.A / 255f, r1w = c.B / 255f;

                float r0x = (float)((float)(r1y + r1x) + r1w);   // dp3 r0.x, r1.yxw, c255.yyy
                r0x = (float)(C254w * r0x);                      // mulsc0  ps = c254.w * r0.x
                r0x = Math.Min(r0x, C255y);                      // min r0.x, r0.x, c255.y
                float r0z = (float)(-r0x + r1w);                 // add r0.z, -r0.x, r1.w
                float r0y = (float)(C255w * r0x);                // mulsc0  ps = c255.w * r0.x
                bool p0 = C254z > Math.Abs(r0z);                 // sgt r2.x, c254.z, |r0.z|; setp_ne
                float r0w = (float)(C254y * r0x);                // mulsc0  ps = c254.y * r0.x
                r0y = Math.Min(r0y, C255y);                      // min r0.y, r0.y, c255.y
                if (p0)
                {
                    r0z = (float)((float)(r0z * C255z) + C255x); // (p) mad r0.z, r0.z, c255.z, c255.x
                    r0z = (float)(C255y - r0z);                  // (p) subsc0 r0.z = c255.y - r0.z
                }
                else
                {
                    r0z = -r0z > 0f ? 1f : 0f;                   // (!p) sgts r0.z, -r0.z
                }
                float r1x2 = (float)(-r0w + r0x);                // add r1.x, -r0.w, r0.x
                float r1y2 = (float)(r0y + -r1w);                // add r1.y, r0.y, -r1.w
                float ax = (float)(r1x2 * r0z);                  // mul r0.xy, r1.xy, r0.zz
                float ay = (float)(r1y2 * r0z);
                float oy = (float)(ay + r1w);                    // add r1.y, r0.y, r1.w
                float ox = (float)(r0w + ax);                    // add r1.x, r0.w, r0.x
                // oC0 = r1.xxyz: red and green share one value, blue its own, alpha untouched.
                px[i] = new Color(Q(ox), Q(ox), Q(oy), Q(r1z));
            }
        }

        private static byte Q(float v)
        {
            if (!(v > 0f)) return 0;
            if (v >= 1f) return 255;
            return (byte)(v * 255f + 0.5f);
        }
    }
}
