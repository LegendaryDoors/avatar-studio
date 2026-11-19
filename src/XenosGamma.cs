// The Xbox 360's display gamma ramp, computed as the XDK's D3D computes it (in RuntimeHost.xex and
// AvatarEditor.xex): the identity ramp through the display's table, from the sRGB and BT.709 curves.
// The display type is 2 (HDTV) unless AVATAR_STUDIO_DISPLAY_GAMMA says otherwise; float steps as on the PowerPC.

using System;

namespace AvatarStudio
{
    static class XenosGamma
    {
        private static float F(uint bits) { return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0); }
        private static double D(ulong bits) { return BitConverter.Int64BitsToDouble((long)bits); }

        // RuntimeHost .rdata 0x8A045B0C..0x8A045B90 (AvatarEditor 0x920126FC..0x92012780).
        private static readonly float Step = F(0x3A802008);            // 1/1023
        private static readonly double SDecT = D(0x3FA4B5DCC63F1412);  // 0.04045
        private static readonly float SDecLin = F(0x3D9E8391);         // 1/12.92
        private static readonly float SOff = F(0x3D6147AE);            // 0.055
        private static readonly float SScale = F(0x3F72A76F);          // 1/1.055
        private static readonly double SExp = D(0x4003333340000000);   // 2.4
        private static readonly double SEncT = D(0x3F69A5C37387B719);  // 0.0031308
        private static readonly float SEncLin = F(0x414EB852);         // 12.92
        private static readonly double SIExp = D(0x3FDAAAAAA0000000);  // 1/2.4
        private static readonly float SMul = F(0x3F870A3D);            // 1.055
        private static readonly double BDecT = D(0x3FB4C985F06F6944);  // 0.0812
        private static readonly float BDecLin = F(0x3E638E39);         // 1/4.5
        private static readonly float BOff = F(0x3DCAC083);            // 0.099
        private static readonly float BScale = F(0x3F68F065);          // 1/1.099
        private static readonly double BExp = D(0x4001C71C80000000);   // 1/0.45
        private static readonly double BEncT = D(0x3F926E978D4FDF3B);  // 0.018
        private static readonly float BEncLin = F(0x40900000);         // 4.5
        private static readonly double BIExp = D(0x3FDCCCCCC0000000);  // 0.45
        private static readonly float BMul = F(0x3F8CAC08);            // 1.099

        private static float Mul(float a, float b) { return (float)((double)a * b); }   // exact product, one rounding
        private static float Add(float a, float b) { return (float)((double)a + b); }
        private static float Pow(float x, double y) { return (float)Math.Pow(x, y); }

        /// <summary>fmsubs: a * b - c rounded once (TwoSum carries the subtraction's error).</summary>
        private static float Fmsubs(float a, float b, float c)
        {
            double p = (double)a * b, s = p - c;
            double bb = s - p, err = (p - (s - bb)) + (-(double)c - bb);   // s + err == p - c exactly
            float r = (float)s;
            if (err == 0) return r;
            double rd = r;
            float n = s > rd ? NextUp(r) : s < rd ? NextDown(r) : r;
            if (n == r) return r;                       // s is a float; err is far below half its ulp
            if (s != (rd + n) / 2) return r;            // not a tie: the cast already rounded right
            return (err > 0) == (n > r) ? n : r;        // a tie in double, not in the exact value
        }

        private static float NextUp(float f) { int b = BitConverter.ToInt32(BitConverter.GetBytes(f), 0); b += f >= 0 ? 1 : -1; return BitConverter.ToSingle(BitConverter.GetBytes(b), 0); }
        private static float NextDown(float f) { int b = BitConverter.ToInt32(BitConverter.GetBytes(f), 0); b += f > 0 ? -1 : 1; return BitConverter.ToSingle(BitConverter.GetBytes(b), 0); }

        private static float Srgb(float x, bool decode)
        {
            if (decode) return (double)x > SDecT ? Pow(Mul(Add(x, SOff), SScale), SExp) : Mul(x, SDecLin);
            return (double)x > SEncT ? Fmsubs(Pow(x, SIExp), SMul, SOff) : Mul(x, SEncLin);
        }

        private static float Bt709(float x, bool decode)
        {
            if (decode) return (double)x >= BDecT ? Pow(Mul(Add(x, BOff), BScale), BExp) : Mul(x, BDecLin);
            return (double)x >= BEncT ? Fmsubs(Pow(x, BIExp), BMul, BOff) : Mul(x, BEncLin);
        }

        /// <summary>sub_8A2783D0: the 1024-entry 10-bit table for a display (type, power) and mode.</summary>
        public static ushort[] Table(int type, float power, int mode)
        {
            ushort[] t = new ushort[1024];
            for (int i = 0; i < 1024; i++)
            {
                float x = Mul(i, Step);
                if (mode == 0) x = Srgb(x, true);
                if (type == 1) x = Srgb(x, mode != 0);
                else if (type == 3) x = Pow(x, mode != 0 ? power : (float)(1.0 / power));
                else x = Bt709(x, mode != 0);
                if (mode != 0) x = Srgb(x, false);
                double v = (double)x * 1023.0 + 0.5;                    // fmadd: exact here (x has 24 bits)
                int n = (int)v;                                         // fctiwz truncates
                t[i] = (ushort)Math.Max(0, Math.Min(1023, n));
            }
            return t;
        }

        /// <summary>The display controller's 10-bit value for each framebuffer level 0..255.</summary>
        public static ushort[] Ramp10(int type, float power)
        {
            ushort[] t0 = Table(type, power, 0), r = new ushort[256];
            for (int p = 0; p < 256; p++) r[p] = t0[(p * 1023) / 255];
            return r;
        }

        /// <summary>[ours] the nearest 8-bit level of each, for an 8-bit back buffer.</summary>
        public static byte[] Ramp8(int type, float power)
        {
            ushort[] r10 = Ramp10(type, power);
            byte[] r = new byte[256];
            for (int p = 0; p < 256; p++) r[p] = (byte)Math.Floor(r10[p] * 255.0 / 1023.0 + 0.5);
            return r;
        }

        /// <summary>AVATAR_STUDIO_DISPLAY_GAMMA: "1", "2" (default), "3:<power>", or "off" (null).</summary>
        public static bool FromEnvironment(out int type, out float power)
        {
            type = 2; power = 2.2222222f;
            string v = Environment.GetEnvironmentVariable("AVATAR_STUDIO_DISPLAY_GAMMA");
            if (string.IsNullOrEmpty(v)) return true;
            if (v.Equals("off", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            string[] p = v.Split(':');
            int t;
            if (int.TryParse(p[0], out t)) type = t;
            float pw;
            if (p.Length > 1 && float.TryParse(p[1], System.Globalization.NumberStyles.Float,
                                               System.Globalization.CultureInfo.InvariantCulture, out pw)) power = pw;
            return true;
        }
    }
}
