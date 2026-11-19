// The editor's own tile pictures, from the player's installed art; plain plates without it.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using AvatarStudio.Avatars;

namespace AvatarStudio
{
    sealed class TileArt : IDisposable
    {
        /// <summary>Caption strip height as a fraction of the tile (measured).</summary>
        public const float CaptionFraction = 79f / 235f;

        private readonly GraphicsDevice device;
        private readonly string dir;
        private readonly Dictionary<string, Texture2D> cache = new Dictionary<string, Texture2D>();

        // requested file -> (file to load, draw it through the lavender shader)
        private struct Redirect { public string Source; public bool Shade; }
        private readonly Dictionary<string, Redirect> redirects = new Dictionary<string, Redirect>(StringComparer.OrdinalIgnoreCase);

        public bool Available { get { return dir != null; } }

        /// <summary>True when the editor's redirect table was installed, so the unselected states exist.</summary>
        public bool HasStates { get { return redirects.Count > 0; } }

        /// <summary>The installed folder, or null.</summary>
        public string Dir { get { return dir; } }
        public string Status { get; private set; }

        // node / tile label -> its picture's base name
        private static readonly Dictionary<string, string> Names = Build();

        private static Dictionary<string, string> Build()
        {
            Dictionary<string, string> m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // main menu
            m["Change My Features"] = "goto_creator";
            m["Change My Style"] = "goto_closet";
            m["Choose New Avatar"] = "goto_startover";
            m["Save and Exit"] = "goto_saveandexit";
            m["Marketplace"] = "goto_marketplace";
            m["Awards"] = "awardables";
            m["Gamer Picture"] = "photobooth";
            // My Features
            m["Hairstyles"] = "hair";
            m["Eyes and Eyebrows"] = "eye_and_eyebrow";
            m["Eyes"] = "eyes";
            m["Eyebrows"] = "eyebrows";
            m["Face"] = "facial";
            m["Facial Features"] = "facial_skin_male";
            m["Facial Hair"] = "facial_hair_male";
            m["Eye Shadow"] = "colour_eye_shadow";
            m["Body Size"] = "body";
            m["Ears"] = "ears";
            m["Nose"] = "nose";
            m["Mouth and Chin"] = "mouth_and_chin";
            m["Mouth"] = "mouth";
            m["Chin"] = "faceshape";
            m["Color"] = "colour";
            // the Color submenu's children carry their own keys (AE_ColorMenu_ctor's base names)
            m["Color/Skin"] = "colour_palette_skin";
            m["Color/Hair"] = "colour_hair";
            m["Color/Eyes"] = "colour_eyes";
            m["Color/Eyebrows"] = "colour_eyebrows";
            m["Color/Facial"] = "colour_facial";
            m["Color/Mouth"] = "colour_mouth";
            m["Color/EyeShadow"] = "colour_eye_shadow";
            m["Color/FacialHair"] = "colour_facial_hair_male";
            // My Style
            m["Tops"] = "tops";
            m["Headwear"] = "hat";
            m["Accessories"] = "access";
            m["Glasses"] = "glasses";
            m["Wristwear"] = "watch";
            m["Gloves"] = "gloves";
            m["Rings"] = "ring";
            m["Earrings"] = "earring";
            m["Props"] = "carry";
            m["Bottoms"] = "bottoms";
            m["Shoes"] = "shoes";
            m["Dress Up"] = "malecostume";
            m["My Outfits"] = "loadoutfit";
            return m;
        }

        public TileArt(GraphicsDevice device)
        {
            this.device = device;
            dir = Find();
            if (dir != null) ReadRedirects(Path.Combine(dir, "redirects.txt"));
            Status = dir == null
                ? "no tile art (plain tiles; the setup installs it)"
                : "tile art <- " + dir + (HasStates ? ", " + redirects.Count + " redirects"
                    : " (no redirects.txt: the unselected states need the setup's art)");
            Console.WriteLine("[studio] " + Status);
        }

        private void ReadRedirects(string path)
        {
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                string[] f = line.Split('\t');
                if (f.Length != 3) continue;
                redirects[f[0]] = new Redirect { Source = f[1], Shade = f[2] == "1" };
            }
        }

        internal static string Find()
        {
            string env = Environment.GetEnvironmentVariable("AVATAR_STUDIO_TILES");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string p = Path.Combine(local, Path.Combine("Avatar Studio", "tiles"));
            if (Directory.Exists(p)) return p;
            p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "tiles");
            return Directory.Exists(p) ? p : null;
        }

        /// <summary>The picture for a tile, or null when there is none (or no art installed).</summary>
        public Texture2D For(string key, bool female)
        {
            return For(key, female, true);
        }

        /// <summary>A tile's picture, focused (_selected) or not (_unselected); null without the redirect table.</summary>
        public Texture2D For(string key, bool female, bool focused)
        {
            if (dir == null || key == null) return null;
            string bas;
            if (!Names.TryGetValue(key, out bas)) return null;

            // Dress Up and Color change their name with the body, not a _male / _female suffix.
            if (female && bas == "malecostume") bas = "femalecostume";
            if (bas == "colour") bas = female ? "colour_female" : "colour_male";

            string sex = female ? "female" : "male";
            if (!focused)
                return HasStates ? Load(bas + "_" + sex + "_unselected") ?? Load(bas + "_unselected") : null;
            return Load(bas + "_" + sex + "_selected")
                ?? Load(bas + "_selected")
                ?? Load(bas + "_" + sex + "_unselected")
                ?? Load(bas + "_unselected")
                ?? Load(bas);
        }

        /// <summary>A tile's _pressdisable picture, shown while a press on a disabled tile is refused.</summary>
        public Texture2D PressDisable(string key, bool female)
        {
            if (dir == null || key == null) return null;
            string bas;
            if (!Names.TryGetValue(key, out bas)) return null;
            if (female && bas == "malecostume") bas = "femalecostume";
            if (bas == "colour") bas = female ? "colour_female" : "colour_male";
            return Load(bas + "_" + (female ? "female" : "male") + "_pressdisable") ?? Load(bas + "_pressdisable");
        }

        /// <summary>A file from the folder by its own name, or null.</summary>
        public Texture2D Named(string name)
        {
            Texture2D t = dir == null ? null : Load(name);
            if (t == null) lent.TryGetValue(name, out t);
            return t;
        }

        // pictures the setup read before anything is installed, used where the folder has none
        private readonly Dictionary<string, Texture2D> lent = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        public void Lend(string name, Texture2D t) { if (t != null) lent[name] = t; }

        /// <summary>The remove cell's picture for a removable category.</summary>
        public Texture2D Remove(AvatarManifestEditor.RemovableComponents kind, bool female)
        {
            return Remove(kind, female, true);
        }

        public Texture2D Remove(AvatarManifestEditor.RemovableComponents kind, bool female, bool focused)
        {
            if (dir == null) return null;
            string bas;
            switch (kind)
            {
                case AvatarManifestEditor.RemovableComponents.Hat: bas = female ? "remove_hat_female" : "remove_hat"; break;
                case AvatarManifestEditor.RemovableComponents.Glasses: bas = "remove_glasses"; break;
                case AvatarManifestEditor.RemovableComponents.Gloves: bas = "remove_gloves"; break;
                case AvatarManifestEditor.RemovableComponents.Wristwear: bas = "remove_watch"; break;
                case AvatarManifestEditor.RemovableComponents.Ring: bas = "remove_ring"; break;
                case AvatarManifestEditor.RemovableComponents.Earrings: bas = "remove_earring"; break;
                case AvatarManifestEditor.RemovableComponents.FacialHair: bas = "remove_facial_hair"; break;
                case AvatarManifestEditor.RemovableComponents.SkinFeatures: bas = female ? "remove_facial" : "remove_facial_male"; break;
                case AvatarManifestEditor.RemovableComponents.EyeShadow: bas = "remove_eyeshadow"; break;
                case AvatarManifestEditor.RemovableComponents.Prop: bas = "remove_carry"; break;
                default: return null;
            }
            return (!focused && HasStates ? Load(bas + "_unselected") : null) ?? Load(bas + "_selected");
        }

        /// <summary>Loads `name`.png as the editor's loader does: a redirected name loads its source, maybe shaded.</summary>
        private Texture2D Load(string name)
        {
            Texture2D t;
            if (cache.TryGetValue(name, out t)) return t;
            t = null;
            string file = name + ".png";
            Redirect rd;
            bool shade = false;
            if (redirects.TryGetValue(file, out rd))
            {
                file = rd.Source;
                shade = rd.Shade;
            }
            string path = Path.Combine(dir, file);
            if (File.Exists(path))
            {
                try
                {
                    using (FileStream fs = File.OpenRead(path)) t = Texture2D.FromStream(device, fs);
                    if (shade) Shade(t);
                    Premultiply(t);
                }
                catch (Exception e)
                {
                    Console.WriteLine("[studio] " + name + ".png: " + e.Message);
                    t = null;
                }
            }
            cache[name] = t;
            return t;
        }

        /// <summary>The editor's unselected pass, on the straight-alpha pixels FromStream returns.</summary>
        private static void Shade(Texture2D t)
        {
            Color[] px = new Color[t.Width * t.Height];
            t.GetData(px);
            UnselectedIcons.Apply(px);
            t.SetData(px);
        }

        /// <summary>Straight alpha to premultiplied, as our SpriteBatch blends.</summary>
        internal static void Premultiply(Texture2D t)
        {
            if (t == null) return;
            Color[] px = new Color[t.Width * t.Height];
            t.GetData(px);
            for (int i = 0; i < px.Length; i++)
            {
                Color c = px[i];
                px[i] = new Color(c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255, c.A);
            }
            t.SetData(px);
        }

        public void Dispose()
        {
            foreach (Texture2D t in cache.Values) if (t != null) t.Dispose();
            cache.Clear();
        }
    }
}
