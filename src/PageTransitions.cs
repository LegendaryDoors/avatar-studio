// The editor's page slides and fades, from the scenes' own timelines.

using System;
using Microsoft.Xna.Framework;

namespace AvatarStudio
{
    enum PageScene { None, GridMain, Grid, Grid2x3, Dialog, Progress, HeightAndWidth, Grid2x2Tall, Tutorial }
    enum PageDir { Left = 0, Right = 1, Forward = 2, Backward = 3 }

    static class PageTransitions
    {
        private struct K
        {
            public int Frame, Curve; public float P1, P2; public Vector3 Pos; public float Opacity;
        }
        private static K L(int f, float x, float y, float z, float o) { return new K { Frame = f, Curve = 0, Pos = new Vector3(x, y, z), Opacity = o }; }
        private static K H(int f, float x, float y, float z, float o) { return new K { Frame = f, Curve = 1, Pos = new Vector3(x, y, z), Opacity = o }; }
        private static K E(int f, int ein, int eout, int escale, float x, float y, float z, float o)
        {
            float s = escale * 0.0099999998f;
            float p1 = s * (float)Math.Sin((1f - ein * 0.0099999998f) * 0.78539819f);
            float p2 = 1f - s * (float)Math.Sin((1f - eout * 0.0099999998f) * 0.78539819f);
            return new K { Frame = f, Curve = 2, P1 = p1, P2 = p2, Pos = new Vector3(x, y, z), Opacity = o };
        }

        // [xur] GridMain.xur scnGrid -> items (main menu, category grids)
        private static readonly K[] GridMainKeys =
        {
            H(0, 0, 0, 0, 1), L(470, 0, 0, 0, 1),
            L(471, -479, 0, 0, 0), H(480, 0, 0, 0, 1), L(481, 0, 0, 0, 1), L(490, -477, 0, 0, 0),
            L(491, 477, 0, 0, 0), H(500, 0, 0, 0, 1), L(501, 0, 0, 0, 1), L(510, 481, 0, 0, 0),
            L(511, 0, 0, 100, 0), H(520, 0, 0, 0, 1), L(521, 0, 0, 0, 1), L(530, 0, 0, 100, 0),
            L(531, 0, 0, -100, 0), H(540, 0, 0, 0, 1), L(541, 0, 0, 0, 1), L(550, 0, 0, -100, 0),
        };
        // [xur] Grid.xur scnGrid -> items, Grid2x3.xur scnGrid -> Items: the same keys
        private static readonly K[] GridKeys =
        {
            H(0, 0, 0, 0, 1), L(470, 0, 0, 0, 1),
            L(471, -100, 0, 0, 0), L(490, 0, 0, 0, 1), L(491, -1.63934f, 0, 0, 0.983607f), L(510, -100, 0, 0, 0),
            L(511, 100, 0, 0, 0), H(530, 0, 0, 0, 1), L(531, 0, 0, 0, 1), L(550, 100, 0, 0, 0),
            E(551, 0, 100, 50, -100, 0, 0, 0), H(560, 0, 0, 0, 1), L(561, 0, 0, 0, 1), L(570, -100, 0, 0, 0),
            E(571, 0, 100, 50, 100, 0, 0, 0), H(580, 0, 0, 0, 1), L(581, 0, 0, 0, 1), L(590, 100, 0, 0, 0),
            L(591, 0, 0, 100, 0), H(600, 0, 0, 0, 1), L(601, 0, 0, 0, 1), L(607, 0, 0, 100, 0),
            E(608, 0, 100, 50, 0, 0, -100, 0), H(620, 0, 0, 0, 1), L(621, 0, 0, 0, 1), L(630, 0, 0, -100, 0),
            L(631, 0, 0, 0, 1), H(640, 0, 0, -100, 0), L(641, 0, 0, -100, 0), E(650, -41, 98, 50, 0, 0, 0, 1),
        };
        // [xur] Dialog.xur scnGrid -> items
        private static readonly K[] DialogKeys =
        {
            H(0, 0, 0, 0, 1), L(300, 0, 0, 0, 1),
            L(301, -479, -2, 0, 0), H(310, 0, 0, 0, 1), L(311, 0, 0, 0, 1), L(320, -477, -3, 0, 0),
            L(321, 477, -2, 0, 0), H(330, 0, 0, 0, 1), L(331, 0, 0, 0, 1), L(340, 481, 0, 0, 0),
            L(341, 0, 0, 100, 0), H(350, 0, 0, 0, 1), L(351, 0, 0, 0, 1), L(364, 0, 0, 100, 0),
            L(365, 0, 0, -100, 0), H(374, 0, 0, 0, 1), L(375, 0, 0, 0, 1), L(389, 0, 0, -100, 0),
        };
        // [xur] Progress.xur scnGrid -> XuiScene1 (the Saving box)
        private static readonly K[] ProgressKeys =
        {
            H(0, 0, 0, 0, 1), L(19, 0, 0, 0, 1),
            L(20, -479, -2, 0, 0), H(29, 0, 0, 0, 1), L(30, 0, 0, 0, 1), L(39, -477, -3, 0, 0),
            L(40, 477, -2, 0, 0), H(49, 0, 0, 0, 1), L(50, 0, 0, 0, 1), L(59, 481, 0, 0, 0),
            L(60, 0, 0, 100, 0), H(69, 0, 0, 0, 1), L(70, 0, 0, 0, 1), L(79, 0, 0, 100, 0),
            L(80, 0, 0, -100, 0), H(89, 0, 0, 0, 1), L(90, 0, 0, 0, 1), L(99, 0, 0, -100, 0),
        };
        // [xur] HeightAndWidth.xur scnGrid -> items (Body Size)
        private static readonly K[] HeightAndWidthKeys =
        {
            H(0, 0, 0, 0, 1), L(470, 0, 0, 0, 1),
            E(471, 0, 100, 50, -100, 0, 0, 0), H(480, 0, 0, 0, 1), L(481, 0, 0, 0, 1), L(490, -100, 0, 0, 0),
            E(491, 0, 100, 50, 100, 0, 0, 0), H(500, 0, 0, 0, 1), L(501, 0, 0, 0, 1), L(510, 100, 0, 0, 0),
            H(511, 0, 0, 100, 0), L(545, 0, 0, 100, 0), H(560, 0, 0, 0, 1), L(561, 0, 0, 0, 1), L(610, 0, 0, 100, 0),
            H(611, 0, 0, -100, 0), L(644, 0, 0, -100, 0), H(660, 0, 0, 0, 1), L(661, 0, 0, 0, 1), L(710, 0, 0, -100, 0),
            H(711, 0, 0, 0, 1), L(740, 0, 0, 0, 1), H(760, 0, 0, -100, 0), L(790, 0, 0, -100, 0), E(810, -41, 98, 50, 0, 0, 0, 1),
        };

        // [xur] Grid2x2Tall.xur scnGrid -> items (My Outfits): Grid's frames, its own keys
        private static readonly K[] Grid2x2TallKeys =
        {
            H(0, 0, 0, 0, 1), L(470, 0, 0, 0, 1),
            L(471, -100, 0, 0, 0), L(490, 0, 0, 0, 1), L(491, 0, 0, 0, 0.983607f), L(510, -100, 0, 0, 0),
            L(511, 100, 0, 0, 0), H(530, 0, 0, 0, 1), L(531, 0, 0, 0, 1), L(550, 100, 0, 0, 0),
            E(551, 0, 100, 50, -100, 0, 0, 0), H(560, 0, 0, 0, 1), L(561, 0, 0, 0, 1), L(570, -100, 0, 0, 0),
            E(571, 0, 100, 50, 100, 0, 0, 0), H(580, 0, 0, 0, 1), L(581, 0, 0, 0, 1), L(590, 100, 0, 0, 0),
            E(591, 0, 100, 50, 0, 0, 100, 0), H(600, 0, 0, 0, 1), L(601, 0, 0, 0, 1), L(610, 0, 0, 100, 0),
            E(611, 0, 100, 50, 0, 0, -100, 0), H(620, 0, 0, 0, 1), L(621, 0, 0, 0, 1), L(630, 0, 0, -100, 0),
            L(631, 0, 0, 0, 1), H(640, 0, 0, -100, 0), L(641, 0, 0, -100, 0), E(650, -41, 98, 50, 0, 0, 0, 1),
        };

        // [xur] Tutorial.xur scnGrid -> items (Introducing Outfits): scnGrid scaled 1.136 about (508, 360)
        private const float TutorialScale = 1.136f;
        private static readonly K[] TutorialKeys =
        {
            H(0, 0, 0, 0, 1), L(300, 0, 0, 0, 1),
            L(301, -480 * TutorialScale, 0, 0, 0), H(310, 0, 0, 0, 1), L(311, 0, 0, 0, 1), L(320, -480 * TutorialScale, 0, 0, 0),
            L(321, 480 * TutorialScale, 0, 0, 0), H(330, 0, 0, 0, 1), L(331, 0, 0, 0, 1), L(340, 480 * TutorialScale, 0, 0, 0),
            L(341, 0, 0, 100, 0), H(350, 0, 0, 0, 1), L(351, 0, 0, 0, 1), L(360, 0, 0, 100, 0),
            L(361, 0, 0, -100, 0), H(370, 0, 0, 0, 1), L(371, 0, 0, 0, 1), L(380, 0, 0, -100, 0),
        };
        // [xur] Tutorial.xur scnGrid -> background (the full-screen dim beside `items`): Opacity only
        private static readonly K[] TutorialBackgroundKeys =
        {
            H(0, 0, 0, 0, 1), L(300, 0, 0, 0, 1),
            L(301, 0, 0, 0, 0), H(310, 0, 0, 0, 1), L(311, 0, 0, 0, 1), H(320, 0, 0, 0, 0),
            L(321, 0, 0, 0, 0), H(330, 0, 0, 0, 1), L(331, 0, 0, 0, 1), H(340, 0, 0, 0, 0),
            L(341, 0, 0, 0, 0), H(350, 0, 0, 0, 1), L(351, 0, 0, 0, 1), H(360, 0, 0, 0, 0),
            L(361, 0, 0, 0, 0), H(370, 0, 0, 0, 1), L(371, 0, 0, 0, 1), L(380, 0, 0, 0, 0),
        };

        /// <summary>[xur] scnGrid's named frames: (start, end) of Left / Right / Forward / Backward In and Out.</summary>
        private static readonly int[][] GridMainFrames = { R(471, 480), R(481, 490), R(491, 500), R(501, 510), R(511, 520), R(521, 530), R(531, 540), R(541, 550) };
        private static readonly int[][] GridFrames = { R(551, 560), R(561, 570), R(571, 580), R(581, 590), R(591, 600), R(601, 607), R(608, 620), R(621, 630) };
        private static readonly int[][] Grid2x3Frames = { R(551, 560), R(561, 570), R(571, 580), R(581, 590), R(591, 600), R(601, 610), R(611, 620), R(621, 630) };
        private static readonly int[][] DialogFrames = { R(301, 310), R(311, 320), R(321, 330), R(331, 340), R(341, 350), R(351, 364), R(365, 374), R(375, 389) };
        private static readonly int[][] ProgressFrames = { R(20, 29), R(30, 39), R(40, 49), R(50, 59), R(60, 69), R(70, 79), R(80, 89), R(90, 99) };
        private static readonly int[][] HeightAndWidthFrames = { R(471, 480), R(481, 490), R(491, 500), R(501, 510), R(511, 560), R(561, 610), R(611, 660), R(661, 710) };
        private static readonly int[][] Grid2x2TallFrames = { R(551, 560), R(561, 570), R(571, 580), R(581, 590), R(591, 600), R(601, 610), R(611, 620), R(621, 630) };
        private static readonly int[][] TutorialFrames = { R(301, 310), R(311, 320), R(321, 330), R(331, 340), R(341, 350), R(351, 360), R(361, 370), R(371, 380) };
        private static int[] R(int a, int b) { return new[] { a, b }; }

        // ---- the scenes' own sounds: XuiSoundXAudio1's State / File track (Sounds.cs), keys held -----
        private struct SK { public int Frame, State; public string File; }
        private static SK S(int f, int s, string file) { return new SK { Frame = f, State = s, File = file }; }
        private const string Arrows = "New_left_or_right_arrows_move_between_screens.xma", OldArrows = "left_or_right_arrows_move_between_screens.xma";
        private const string Down = "down_a_menu_in_the_hierarchy.xma", Up = "up_a_menu_in_the_hierarchy.xma";
        // [xur] GridMain.xur and HeightAndWidth.xur (the In / Out ranges 471..510; ForwardIn plays Down)
        private static readonly SK[] GridMainSounds =
        {
            S(0, 4, OldArrows), S(471, 2, Arrows), S(480, 4, Arrows), S(481, 2, Arrows), S(490, 4, Arrows), S(491, 2, Arrows),
            S(500, 4, Arrows), S(501, 2, Arrows), S(510, 4, Arrows), S(511, 2, Down), S(520, 4, Down), S(521, 4, Up),
            S(531, 4, Down), S(541, 4, Up), S(550, 4, Up),
        };
        private static readonly SK[] HeightAndWidthSounds =
        {
            S(0, 4, OldArrows), S(471, 2, Arrows), S(480, 4, Arrows), S(481, 2, Arrows), S(490, 4, Arrows), S(491, 2, Arrows),
            S(500, 4, Arrows), S(501, 2, Arrows), S(510, 4, Arrows), S(511, 2, Down), S(560, 4, Down), S(561, 4, Up),
            S(611, 4, Down), S(661, 4, Up), S(810, 4, Up),
        };
        // [xur] Grid.xur; Grid2x3.xur and Grid2x2Tall.xur the same with 611 for 608
        private static SK[] GridSounds(int backwardIn)
        {
            return new[]
            {
                S(0, 4, Arrows), S(471, 2, Arrows), S(490, 4, Arrows), S(491, 2, Arrows), S(510, 4, Arrows), S(511, 2, Arrows),
                S(530, 4, Arrows), S(531, 2, Arrows), S(550, 4, Arrows), S(551, 2, Arrows), S(560, 4, Arrows), S(561, 2, Arrows),
                S(570, 4, Arrows), S(571, 2, Arrows), S(580, 4, Arrows), S(581, 2, Arrows), S(590, 4, Arrows), S(591, 2, Down),
                S(600, 4, Down), S(601, 4, Up), S(backwardIn, 4, Down), S(621, 4, Up), S(650, 4, Up),
            };
        }
        private static readonly SK[] GridSoundKeys = GridSounds(608), Grid2x3SoundKeys = GridSounds(611);
        // [xur] Dialog.xur: the popup sounds on the Forward / Backward ranges
        private static readonly SK[] DialogSounds =
        {
            S(0, 4, "popup_appearing.xma"), S(341, 2, "popup_appearing.xma"), S(350, 4, "popup_appearing.xma"),
            S(351, 2, "popup_disappearing.xma"), S(364, 4, "popup_disappearing.xma"), S(365, 2, "popup_appearing.xma"),
            S(374, 4, "popup_appearing.xma"), S(375, 2, "popup_disappearing.xma"), S(389, 4, "popup_disappearing.xma"),
        };

        /// <summary>The scene's sound State and File `seconds` into its In or Out; false without one.</summary>
        public static bool SoundAt(PageScene scene, PageDir dir, bool incoming, float seconds, out int state, out string file)
        {
            state = 4; file = null;
            SK[] keys; int[][] frames;
            switch (scene)
            {
                case PageScene.GridMain: keys = GridMainSounds; frames = GridMainFrames; break;
                case PageScene.HeightAndWidth: keys = HeightAndWidthSounds; frames = HeightAndWidthFrames; break;
                case PageScene.Grid: keys = GridSoundKeys; frames = GridFrames; break;
                case PageScene.Grid2x3: keys = Grid2x3SoundKeys; frames = Grid2x3Frames; break;
                case PageScene.Grid2x2Tall: keys = Grid2x3SoundKeys; frames = Grid2x2TallFrames; break;
                case PageScene.Dialog: keys = DialogSounds; frames = DialogFrames; break;
                default: return false;
            }
            int[] range = frames[2 * (int)dir + (incoming ? 0 : 1)];
            float f = Math.Min(range[0] + seconds * 60f, range[1]);
            SK k = keys[0];
            foreach (SK x in keys) { if (x.Frame <= f) k = x; else break; }
            state = k.State; file = k.File;
            return true;
        }

        /// <summary>The scene's sound element's own File: the voice's start.</summary>
        public static string SoundFile(PageScene scene)
        {
            switch (scene)
            {
                case PageScene.GridMain: case PageScene.HeightAndWidth: return OldArrows;
                case PageScene.Grid: case PageScene.Grid2x3: case PageScene.Grid2x2Tall: return Arrows;
                case PageScene.Dialog: return "popup_appearing.xma";
            }
            return null;
        }

        /// <summary>Whether the scene's fixed part draws under `items` (Tutorial.xur's background).</summary>
        public static bool FixedBehind(PageScene scene) { return scene == PageScene.Tutorial; }

        /// <summary>The opacity of what a scene keeps beside `items`: 1, except Tutorial.xur's background.</summary>
        public static float FixedOpacity(PageScene scene, PageDir dir, bool incoming, float seconds)
        {
            if (scene != PageScene.Tutorial) return 1f;
            int[] range = TutorialFrames[2 * (int)dir + (incoming ? 0 : 1)];
            float f = Math.Min(range[0] + seconds * 60f, range[1]);
            Vector3 p; float o;
            Sample(TutorialBackgroundKeys, f, out p, out o);
            return o;
        }

        public static PageDir Opposite(PageDir d)
        {
            switch (d)                                              // 0x9231344C: 1, 0, 3, 2
            {
                case PageDir.Left: return PageDir.Right;
                case PageDir.Right: return PageDir.Left;
                case PageDir.Forward: return PageDir.Backward;
                default: return PageDir.Forward;
            }
        }

        /// <summary>The moving element's offset and opacity `seconds` into the scene's In or Out; true once ended.</summary>
        public static bool Evaluate(PageScene scene, PageDir dir, bool incoming, float seconds, out Vector3 pos, out float opacity, out bool ended)
        {
            pos = Vector3.Zero; opacity = 1f; ended = true;
            K[] keys; int[][] frames;
            switch (scene)
            {
                case PageScene.GridMain: keys = GridMainKeys; frames = GridMainFrames; break;
                case PageScene.Grid: keys = GridKeys; frames = GridFrames; break;
                case PageScene.Grid2x3: keys = GridKeys; frames = Grid2x3Frames; break;
                case PageScene.Dialog: keys = DialogKeys; frames = DialogFrames; break;
                case PageScene.Progress: keys = ProgressKeys; frames = ProgressFrames; break;
                case PageScene.HeightAndWidth: keys = HeightAndWidthKeys; frames = HeightAndWidthFrames; break;
                case PageScene.Grid2x2Tall: keys = Grid2x2TallKeys; frames = Grid2x2TallFrames; break;
                case PageScene.Tutorial: keys = TutorialKeys; frames = TutorialFrames; break;
                default: return false;
            }
            int[] range = frames[2 * (int)dir + (incoming ? 0 : 1)];
            float f = range[0] + seconds * 60f;
            ended = f >= range[1];
            if (ended) f = range[1];
            Sample(keys, f, out pos, out opacity);
            return true;
        }

        private static void Sample(K[] keys, float f, out Vector3 pos, out float opacity)
        {
            int k = 0;
            while (k + 1 < keys.Length && keys[k + 1].Frame <= f) k++;
            if (k + 1 >= keys.Length) { pos = keys[k].Pos; opacity = keys[k].Opacity; return; }
            K a = keys[k], b = keys[k + 1];
            float t = (f - a.Frame) / (b.Frame - a.Frame);
            float e;
            switch (a.Curve)
            {
                case 1: e = t >= 0.99989998f ? 1f : 0f; break;     // XUI_Ease: hold
                case 2:                                             // XUI_EaseCurve case 2
                    float u = 1f - t;
                    e = 3f * ((u * a.P1 + a.P2 * t) * u) * t + t * t * t;
                    break;
                default: e = t; break;
            }
            pos = Vector3.Lerp(a.Pos, b.Pos, e);
            opacity = MathHelper.Lerp(a.Opacity, b.Opacity, e);
        }

        /// <summary>scnMain's perspective centre on screen.</summary>
        public static readonly Vector2 Centre = Layout.Scene(1016.45f * 0.5f, 720f * 0.5f);

        /// <summary>Where a full-screen layer offset by `pos` is drawn: c + (p + d - c) / (1 + 0.001 z).</summary>
        public static void Place(Vector3 pos, out Vector2 topLeft, out float scale)
        {
            float w = 1f + 0.001f * pos.Z;
            Vector2 d = new Vector2(pos.X, pos.Y) * Layout.SceneScale;
            scale = 1f / w;
            topLeft = Centre + (d - Centre) * scale;
        }
    }
}
