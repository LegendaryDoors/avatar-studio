// The avatar editor's own camera: one view and projection; each screen a record the avatar eases to.

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace AvatarStudio
{
    static class EditorCamera
    {
        // Screen ids: PushState (sub_920ED2F0) pushes them; the main menu is 2.
        public const int Exit = 1, Saving = 76;   // the exit's empty page; the Saving box
        public const int ChooseNew = 81;   // the Choose New Avatar dialog
        public const int MainMenu = 2, Features = 4, BodySize = 5, Hair = 6, MouthAndChin = 7, Chin = 8,
            Face = 9, Ears = 10, Nose = 11, Mouth = 12, EyesAndEyebrows = 13, Eyes = 14, Eyebrows = 15,
            FacialHair = 19, FacialFeatures = 20, Color = 21, Style = 22, Tops = 23, Bottoms = 24,
            Shoes = 25, Headwear = 26, Props = 28, Accessories = 29, DressUp = 30, Outfits = 31,
            Glasses = 32, Earrings = 33, Wristwear = 34, Rings = 35, Gloves = 36,
            SkinColor = 55, HairColor = 56, LipColor = 57, FacialHairColor = 58, EyeColor = 59,
            EyebrowColor = 60, EyeShadowColor = 61, FacialFeatureColor = 63;

        /// <summary>The records at 0x92312A28 + 24 id: pitch, yaw, roll (radians), x, y, z.</summary>
        private static readonly Dictionary<int, float[]> Records = new Dictionary<int, float[]>
        {
            {  1, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },   // the bytes of 28's
            {  2, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            {  4, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            {  5, new[] { -0.058202f, -0.185316f, -0.02227f, 0.47413775f, 0.028978f, 0.895782f } },
            {  6, new[] { 0f, -0.559795f, 0f, 0.47413775f, -0.40702f, -1.01913f } },
            {  7, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            {  8, new[] { 0.134677f, -0.35697f, -0.019159f, 0.47413775f, -0.380767f, -1.176682f } },
            {  9, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            { 10, new[] { 0f, -1.046294f, 0f, 0.47413775f, -0.427073f, -1.169648f } },
            { 11, new[] { 0f, -1.024629f, 0f, 0.47413775f, -0.416466f, -1.129985f } },
            { 12, new[] { 0.059434f, -0.166752f, -0.01958f, 0.47413775f, -0.412483f, -1.240277f } },
            { 13, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            { 14, new[] { 0.059434f, -0.166752f, -0.01958f, 0.47413775f, -0.412483f, -1.240277f } },
            { 15, new[] { 0f, -0.191352f, 0f, 0.47413775f, -0.393396f, -0.954294f } },
            { 19, new[] { 0.039005f, -0.028605f, -0.01929f, 0.47413775f, -0.389398f, -1.161231f } },
            { 20, new[] { 0.039005f, -0.028605f, -0.01929f, 0.47413775f, -0.389398f, -1.161231f } },
            { 21, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            { 22, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            { 23, new[] { 0f, -0.321689f, 0f, 0.47413775f, -0.159133f, -0.845112f } },
            { 24, new[] { 0.031832f, -0.177963f, 0f, 0.47413775f, 0.261957f, -0.956476f } },
            { 25, new[] { -0.040137f, -0.33274f, 0.046627f, 0.47413775f, 0.409881f, -0.83432f } },
            { 26, new[] { 0f, -0.831693f, 0f, 0.47413775f, -0.459625f, -1.116506f } },
            { 28, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },
            { 29, new[] { 0f, -0.468548f, 0f, -0.5710583f, -0.021022f, 0.573379f } },
            { 30, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },
            { 31, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },
            { 32, new[] { 0f, -0.831693f, 0f, 0.47413775f, -0.419807f, -1.116506f } },
            { 33, new[] { 0f, -0.15f, 0f, 0.47413775f, -0.418856f, -1.116506f } },
            { 34, new[] { 0.031832f, 0.940619f, 0f, 0.47413775f, 0.161957f, -0.956476f } },
            { 35, new[] { 0.031832f, -1.999999f, 0f, 0.47413775f, 0.261957f, -0.956476f } },
            { 36, new[] { 0.031832f, -0.815319f, 0f, 0.47413775f, -0.166681f, -0.956476f } },
            { 55, new[] { -0.058202f, -0.185316f, -0.02227f, 0.47413775f, -0.081389f, -0.065462f } },
            { 56, new[] { 0f, -0.559795f, 0f, 0.47413775f, -0.40702f, -1.01913f } },
            { 57, new[] { 0.059434f, -0.166752f, -0.01958f, 0.47413775f, -0.412483f, -1.240277f } },
            { 58, new[] { 0.039005f, -0.028605f, -0.01929f, 0.47413775f, -0.389398f, -1.161231f } },
            { 59, new[] { 0.059434f, -0.166752f, -0.01958f, 0.47413775f, -0.412483f, -1.240277f } },
            { 60, new[] { 0f, -0.191352f, 0f, 0.47413775f, -0.393396f, -0.954294f } },
            { 61, new[] { 0.059434f, -0.166752f, -0.01958f, 0.47413775f, -0.412483f, -1.240277f } },
            { 63, new[] { 0.039005f, -0.028605f, -0.01929f, 0.47413775f, -0.389398f, -1.161231f } },
            { 76, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },   // the bytes of 28's
            { 81, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.495782f } },   // the bytes of 28's
            // every override state's record (0x92314730): 28's, but nearer
            { Override, new[] { 0f, -0.468548f, 0f, 0.47413775f, -0.021022f, 0.116544f } },
        };

        /// <summary>The studio's id for the override record (0x92314730), not a console state.</summary>
        public const int Override = 1000;

        /// <summary>Screens whose y is used as stored (sub_920C34F0's switch).</summary>
        private static bool FixedHeight(int id)
        {
            switch (id)
            {
                case 1: case 2: case 4: case 5: case 7: case 9: case 13: case 16: case 17: case 18:
                case 21: case 22: case 25: case 39: case 41: case 42: case 96: case Override:
                    return true;
            }
            return (id >= 28 && id <= 31) || (id >= 51 && id <= 53) || (id >= 67 && id <= 94);
        }

        /// <summary>Whether LT / RT turn the avatar on a screen (sub_920C40D0).</summary>
        public static bool TriggersSpin(int id)
        {
            switch (id)
            {
                case 1: case 2: case 3: case 5: case 54: case 68: case 75: case 76: case 77:
                case 81: case 82: case 87: case 89: case 90: case 91: case Override:
                    return false;
            }
            return id >= 0;
        }

        public static bool Has(int id) { return Records.ContainsKey(id); }

        /// <summary>The target record for a screen, with the height rule applied.</summary>
        public static void Target(int id, float heightFactor, float[] into) { Target(id, heightFactor, into, false); }

        /// <summary>Mirrored when an item asks: yaw' = -yaw - 0.29 (sub_920C34F0).</summary>
        public static void Target(int id, float heightFactor, float[] into, bool mirrored)
        {
            float[] r = Records[id];
            Array.Copy(r, into, 6);
            if (!FixedHeight(id))
            {
                float k = (heightFactor + 1f) * 0.10000002f + 0.9f;   // 0x92006C14, 0x92001CE8
                into[4] = (r[4] - 1f) * k + 1f;
            }
            if (mirrored) into[1] = into[1] * -1f - 0.28999999f;
        }

        /// <summary>The per-frame easing factor, min(6 * dt, 0.1) (sub_920C5B58, 0x920C5B78).</summary>
        public static float Blend(float dt) { return Math.Min(6f * dt, 0.1f); }

        public static void Ease(float[] current, float[] target, float t)
        {
            for (int i = 0; i < 6; i++) current[i] = target[i] * t + current[i] * (1f - t);   // sub_920C2650
        }

        /// <summary>The console's world for a record: the Z flip, the spin, then the record's rotation and position.</summary>
        public static Matrix World(float[] rec, float spin)
        {
            return Matrix.CreateScale(1f, 1f, -1f)
                 * Matrix.CreateRotationY(MathHelper.Pi + spin)
                 * Matrix.CreateFromYawPitchRoll(rec[1], rec[0], rec[2])
                 * Matrix.CreateTranslation(rec[3], rec[4], rec[5]);
        }

        /// <summary>The same world for the library renderer, which has no flip to cancel.</summary>
        public static Matrix LibraryWorld(float[] rec, float spin)
        {
            return Matrix.CreateScale(1f, 1f, -1f)
                 * Matrix.CreateRotationY(spin)
                 * Matrix.CreateFromYawPitchRoll(rec[1], rec[0], rec[2])
                 * Matrix.CreateTranslation(rec[3], rec[4], rec[5]);
        }

        /// <summary>The floor shadow's world (AE_DrawAvatar, sub_920F7D08).</summary>
        public static Matrix ShadowWorld(float[] rec, float spin)
        {
            return Matrix.CreateRotationY(spin)
                 * Matrix.CreateFromYawPitchRoll(rec[1], rec[0], rec[2])
                 * Matrix.CreateTranslation(rec[3], rec[4], rec[5]);
        }

        /// <summary>The view sub_920C5B58 builds for every screen but 90.</summary>
        public static readonly Matrix View = Matrix.CreateTranslation(-0.5f, -0.75f, 2.5f);

        /// <summary>PerspectiveFovLH(0.78539819, 1.7777778, 0.01, 200), as sub_920C5B58 stores it.</summary>
        public static readonly Matrix Projection = PerspectiveFovLH(0.78539819f, 1.7777778f, 0.0099999998f, 200f);

        /// <summary>The lineup's projection (sub_920B10F0): pi / 4, the display's aspect, near 1, far 20.</summary>
        public static readonly Matrix LineupProjection = PerspectiveFovLH(0.78539819f, 1.7777778f, 1f, 20f);

        private static Matrix PerspectiveFovLH(float fov, float aspect, float near, float far)
        {
            float ys = 1f / (float)Math.Tan(fov * 0.5f), xs = ys / aspect, q = far / (far - near);
            return new Matrix(xs, 0, 0, 0,
                              0, ys, 0, 0,
                              0, 0, q, 1,
                              0, 0, -q * near, 0);
        }

        /// <summary>The height factor at offset 8 of the manifest (big-endian float), as is.</summary>
        public static float HeightFactor(byte[] manifest)
        {
            if (manifest == null || manifest.Length < 12) return 0f;
            byte[] b = { manifest[8], manifest[9], manifest[10], manifest[11] };
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            return BitConverter.ToSingle(b, 0);
        }
    }
}
