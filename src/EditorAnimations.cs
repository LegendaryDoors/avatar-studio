// The avatar editor's own animation table, by id.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    static class EditorAnimations
    {
        public const int CreatorLookNeutral = 2, Celebration = 8;
        /// <summary>The avatar library's default animation (every renderer's first slot): id 1.</summary>
        public const int LibraryDefault = 1;
        public const int PickMeIntoFloat = 5, PickMeFloat = 6, PickMeOutOfFloat = 7, FloatingCelebrate = 9, PickMe = 10;
        public const int RunOnTurnLeft = 59, RunOnTurnRight = 60, RunOffTurnLeft = 61, RunOffTurnRight = 62;
        public const int CrossToFrontLeft = 63, CrossToFrontRight = 64, CrossToBackLeft = 65, CrossToBackRight = 66;
        public const int BalanceInto = 17, BalanceLoop = 18, BalanceOutOf = 19;
        public const int Spin = 15, SpinMirrored = 16;

        // name; null for a stock GUID or a procedural / empty slot
        private struct Entry { public string Name; public int Guid; public int Body; }
        private static readonly Dictionary<int, Entry> Table = new Dictionary<int, Entry>();

        private static void N(int id, string name) { Table[id] = new Entry { Name = name }; }
        private static void G(int id, int index, int body) { Table[id] = new Entry { Guid = index, Body = body }; }

        static EditorAnimations()
        {
            // sub_920F7500, in its order; GUIDs 00400000-<index>-<body>-c1c8-f109a19cb2e0
            G(1, 0x03, 3); G(2, 0x1E, 3);
            N(5, "creator_pick_me_intofloat"); N(6, "creator_pick_me_float"); N(7, "creator_pick_me_outoffloat");
            G(8, 0x08, 3); N(9, "creator_floating_celebrate"); N(10, "creator_pick_me");
            N(11, "closet_examine_shoes"); N(12, "closet_examine_lower_body"); N(13, "closet_examine_upper_body"); N(14, "closet_examine_headgear");
            N(15, "closet_spin"); N(16, "closet_spin_mirrored");
            N(17, "closet_balance_left_into"); N(18, "closet_balance_left"); N(19, "closet_balance_left_outof");
            G(20, 0x04, 3); G(21, 0x05, 3); G(22, 0x27, 3); G(23, 0x2A, 3); G(24, 0x2B, 3); G(25, 0x06, 3); G(26, 0x07, 3);
            N(27, "idle_n_look_right"); N(28, "idle_n_look_right_02"); N(29, "Idle_n_look_up"); N(30, "idle_n_look_left");
            G(31, 0x09, 2); G(32, 0x0A, 2); G(33, 0x0B, 2); G(34, 0x0C, 2); G(35, 0x0A, 2);
            G(36, 0x0D, 2); G(37, 0x0E, 2); G(38, 0x0F, 2); G(39, 0x10, 2); G(40, 0x11, 2);
            G(41, 0x13, 1); G(42, 0x14, 1); G(43, 0x15, 1); G(44, 0x26, 1); G(45, 0x13, 1);
            G(46, 0x16, 1); G(47, 0x17, 1); G(48, 0x18, 1); G(49, 0x19, 1); G(50, 0x1A, 1);
            N(51, "idle_n_wave"); N(52, "idle_n_look_up_and_behind"); N(53, "idle_n_wave_offscreen");
            N(54, "purchase_n_female_dance"); N(55, "purchase_n_female_hop_clap"); N(56, "purchase_n_male_fist_pump");
            N(57, "purchase_n_male_fist_shake"); N(58, "purchase_n_raise_arms");
            N(59, "Run_On_Turn_Left"); N(60, "Run_On_Turn_Right"); N(61, "Turn_Left_Run_Off"); N(62, "Turn_Right_Run_Off");
            N(63, "Turn_Left_Cross_To_Front"); N(64, "Turn_Right_Cross_To_Front"); N(65, "Turn_Left_Cross_To_Back"); N(66, "Turn_Right_Cross_To_Back");
        }

        /// <summary>The idle pick's lists, per body.</summary>
        public static readonly int[] FemaleIdles = { 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35 };
        public static readonly int[] MaleIdles = { 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 41, 42, 43, 44, 45 };

        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_ANIMATIONS");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Avatar Studio", "animations");
            }
        }

        /// <summary>The stock asset folder, found as the asset provider finds it.</summary>
        private static string AssetDir() { return SetupInstall.ExistingItemsFolder(); }

        private static readonly Dictionary<int, byte[]> cache = new Dictionary<int, byte[]>();

        public static bool Has(int id) { return Bytes(id) != null; }

        /// <summary>The id's asset STRB, or null.</summary>
        public static byte[] Asset(int id) { return Bytes(id); }

        private static byte[] Bytes(int id)
        {
            byte[] b;
            if (cache.TryGetValue(id, out b)) return b;
            Entry e;
            string path = null;
            if (Table.TryGetValue(id, out e))
            {
                if (e.Name != null) path = Path.Combine(Folder, e.Name + ".MsLiveAvatarAsset");
                else
                {
                    // a stock animation: the catalogue's copy (as XAM served it), else the folder's
                    string guid = string.Format("00400000-{0:x4}-{1:x4}-c1c8-f109a19cb2e0", e.Guid, e.Body);
                    b = TocAssets.Get(new Guid(guid));
                    if (b != null) { cache[id] = b; return b; }
                    string dir = AssetDir();
                    if (dir != null) path = Path.Combine(dir, guid + ".strb");
                }
            }
            b = path != null && File.Exists(path) ? File.ReadAllBytes(path) : null;
            cache[id] = b;
            return b;
        }

        /// <summary>A stock animation by its GUID's index and body, or null.</summary>
        public static AvatarAnimation CreateStock(int index, int body)
        {
            int key = -((index << 8) | body);        // apart from the table's ids
            byte[] b;
            if (!cache.TryGetValue(key, out b))
            {
                string guid = string.Format("00400000-{0:x4}-{1:x4}-c1c8-f109a19cb2e0", index, body);
                b = TocAssets.Get(new Guid(guid));
                if (b == null)
                {
                    string dir = AssetDir(), path = dir == null ? null : Path.Combine(dir, guid + ".strb");
                    b = path != null && File.Exists(path) ? File.ReadAllBytes(path) : null;
                }
                cache[key] = b;
            }
            return FromBytes(b, key);
        }

        /// <summary>A fresh animation for the id; null when not installed, procedural or empty.</summary>
        public static AvatarAnimation Create(int id) { return FromBytes(Bytes(id), id); }

        /// <summary>A carryable's linked animation asset, or null.</summary>
        public static AvatarAnimation FromAsset(byte[] strb) { return FromBytes(strb, -3); }

        private static AvatarAnimation FromBytes(byte[] b, int id)
        {
            if (b == null) return null;
            try
            {
                return AvatarAnimation.Load(b);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[studio] editor animation " + id + " failed to load: " + (ex.InnerException ?? ex).Message);
                cache[id] = null;
                return null;
            }
        }
    }
}
