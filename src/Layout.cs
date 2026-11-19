// Every screen coordinate in one place, on the 1280x720 design grid.
// Tagged: [xur] read from the editor's scene files, [ours] chosen, by eye where neither.

using Microsoft.Xna.Framework;

namespace AvatarStudio
{
    static class Layout
    {
        public const int W = 1280, H = 720;

        // ---- scene -> screen -------------------------------------------------------------------
        // Page scenes are 1016 x 720, hosted in EditorMain.xur's scnMain at (192.76, 67), Scale 0.88.
        public const float SceneX = 192.76f, SceneY = 67f, SceneScale = 0.88f;   // [xur] EditorMain scnMain
        public static Vector2 Scene(float x, float y) { return new Vector2(SceneX + x * SceneScale, SceneY + y * SceneScale); }
        public static Rectangle SceneRect(float x, float y, float w, float h)
        {
            Vector2 a = Scene(x, y);
            return new Rectangle((int)System.Math.Round(a.X), (int)System.Math.Round(a.Y),
                                 (int)System.Math.Round(w * SceneScale), (int)System.Math.Round(h * SceneScale));
        }

        // ---- the pages' texts: [xur] the Grid scenes' category name and page number boxes -------
        public const float CategoryW = 695.31f, CategoryH = 61.9334f, PageNumberW = 347.518f, PageNumberH = 47f;
        public static readonly Vector2 CategoryAt = Scene(179.837f - 37f, -26f);
        public static readonly Vector2 PageNumberAt = Scene(179.837f - 38f, -26f + 595f);
        public static readonly Vector2 GridCategoryAt = Scene(284.692f, -26f);

        // the button legend's place and layout are its scene's and class's: LegendBar.cs

        // ---- gallery: the avatars you have ([ours] throughout) -----------------------------------
        public const int CardW = 210, CardH = 236;                               // [ours]
        public const int CardGap = 20;                                           // [ours]
        public const int CardCols = 4, CardRows = 2;
        public const int CardsPerPage = CardCols * CardRows;                     // 8
        public static readonly Point CardsOrigin = new Point(190, 140);          // [ours] 900px block, centred
        public const int CardPlateH = 50;                                        // [ours] name + date strip
        public const int CardInset = 6;                                          // [ours] frame around the picture
        public const int CardRing = 4;                                           // [ours] selection ring
        public static readonly Vector2 GalleryTitlePos = new Vector2(96, 42);    // [ours]
        public static readonly Vector2 GallerySubtitlePos = new Vector2(98, 88); // [ours] count + game status
        public static readonly Vector2 GalleryPageTextPos = new Vector2(1184, 88);// [ours] right-aligned, clear of the legend
        public static readonly Vector2 GalleryEmptyPos = new Vector2(430, 250);  // [ours] beside the New Avatar card
        public static readonly Rectangle CardMenu = new Rectangle(440, 166, 400, 380);  // [ours] the Y menu: its name and seven rows

        // ---- the tile block (main menu and category grids): [xur] GridMain.xur ----------------
        public const int TileW = 150, TileH = 220;                               // [xur] 170 x 249.69 * 0.88
        public const int TileGapX = 4, TileGapY = 6;                             // [xur] pitch 154 / 226.34 minus the art
        public static readonly Point TilesOrigin = new Point(439, 127);          // [xur]
        public static readonly Point MainTilesOrigin = TilesOrigin;
        public static readonly Point GridTilesOrigin = TilesOrigin;

        // the avatar viewport scnAvatar (160, 0, 960 x 720): the avatar stands left of the tiles
        public const float GridAvatarScreenX = -0.5f;                            // [ours] NDC offset that puts it there

        // ---- item picker: avatar centred, one column of four each side: [xur] Grid.xur ---------
        public const int CellSize = 106;                                         // [xur] 120 * 0.88
        public const int CellInset = 4;                                          // [ours] the art sits inside its button
        public const int CellRows = 4;
        public const int CellsPerPage = 8;                                       // [xur] ItemButton1..8
        public static readonly int[] CellColumnX = { 306, 862 };                 // [xur] 129 / 760 in the 1016 canvas
        public static readonly int[] CellGhostX = { 80, 1083 };                  // [xur] Left/RightOffscreenItems
        public const int CellsTop = 137;                                         // [xur] 80
        public const int CellRowPitch = 106;                                     // [xur] 120 * 0.88
        // [xur] Grid.xur's left / right handles, 57 x 54, as top-left corners
        public static readonly Rectangle PagePrevButton = SceneRect(-42.7354f, 298.191f, 57f, 54f);
        public static readonly Rectangle PageNextButton = SceneRect(996.636f, 296.434f, 57f, 54f);
        // [xur] shadow_item under each picker column (Grid / Grid2x3 shadowLeft / shadowRight)
        public static readonly float[] CellShadowSceneX = { 130f, 759f };
        public const float CellShadowSceneW = 128f;
        // [xur] shadow_EditorBoxart under each tile column (GridMain shadow1..4)
        public static readonly float[] TileShadowSceneX = { 276f, 451f, 626f, 801f };
        public const float TileShadowSceneW = 180f;

        // ---- colour picker: three rows, one column each side: [xur] Grid2x3.xur ---------------
        public const int ChipSize = 106;                                         // [xur] 120 * 0.88
        public const int ChipRows = 3;
        public const int ChipsPerPage = 6;                                       // [xur] ItemButton1..6
        public const int ChipsTop = 189;                                         // [xur] 139
        public const int ChipRowPitch = 106;                                     // [xur] 120 * 0.88

        // ---- lineup: "Choose an avatar as a starting point": [xur] EditorStart.xur -----------
        // Five avatars on screen at once, on an arc.
        public static readonly int[] LineupSlotX = { 203, 370, 536, 703, 869 };  // [xur] AvatarButton1..5
        public static readonly int[] LineupSlotY = { 220, 230, 238, 230, 220 };  // [xur] the arc
        public const int LineupSlotW = 207, LineupSlotH = 319;                   // [xur] 207 x 318.76
        public const int LineupVisible = 5;                                      // [xur] AvatarButton1..5
        public const int LineupPages = 20;                                       // AE_Lineup_MakeManifests: 20 pages of 5 (and "Page 1 of 20", Strings.xus 201)
        public static readonly Vector2 LineupTitlePos = new Vector2(640, 108);   // [xur] XuiText1 (231.976, 55.73) 816x104, centred
        public static readonly Vector2 LineupPageTextPos = new Vector2(640, 652);// [xur] groupIndex (320, 625) 640x54, centred
        // [xur] the scroll handles: 60 x 60 at Scale 0.8 about (30, 30), centred at (126.05, 380) / (1154.25, 380)
        public static readonly Vector2 LineupPrevHandle = new Vector2(96.0479f + 30f, 350f + 30f);
        public static readonly Vector2 LineupNextHandle = new Vector2(1124.25f + 30f, 350f + 30f);
        public const float LineupHandleScale = 0.8f;
        public const int LineupHandleSize = 48;                                  // [xur] h 60 at Scale 0.8

        // ---- body size: height rail (vertical) and width rail (horizontal) --------------------
        public static readonly Rectangle HeightRail = new Rectangle(940, 150, 40, 380);   // [ours] the stand-in without the art (StudioGame.DrawRail draws the scene's)
        public static readonly Rectangle WidthRail = new Rectangle(380, 600, 520, 40);    // [ours]

        // ---- name entry ------------------------------------------------------------------------
        public static readonly Rectangle NamePanel = new Rectangle(200, 90, 880, 520);   // by eye: grey panel
        public static readonly Rectangle NameField = new Rectangle(240, 262, 700, 40);   // by eye
    }
}
