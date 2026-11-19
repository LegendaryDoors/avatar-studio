// The Xbox 360's display gamma ramp, applied to the editor's 1280x720 frame.

using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class DisplayGamma
    {
        private readonly GraphicsDevice device;
        private readonly Effect effect;
        private readonly Texture2D lut;
        private readonly RenderTarget2D graded;
        private readonly VertexPositionTexture[] quad;
        public readonly byte[] Map;
        public readonly string Status;

        public bool On { get { return effect != null; } }

        public DisplayGamma(GraphicsDevice device, int w, int h)
        {
            this.device = device;
            int type; float power;
            if (!XenosGamma.FromEnvironment(out type, out power)) { Status = "off (AVATAR_STUDIO_DISPLAY_GAMMA=off)"; return; }
            string fxb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ToneLUT.fxb");
            if (!File.Exists(fxb)) { Status = "off (no ToneLUT.fxb beside the exe)"; return; }
            try
            {
                effect = new Effect(device, File.ReadAllBytes(fxb));
                Map = XenosGamma.Ramp8(type, power);
                lut = new Texture2D(device, 256, 1, false, SurfaceFormat.Color);
                Color[] px = new Color[256];
                for (int i = 0; i < 256; i++) px[i] = new Color(Map[i], Map[i], Map[i], (byte)255);
                lut.SetData(px);
                graded = new RenderTarget2D(device, w, h, false, SurfaceFormat.Color, DepthFormat.None);
                quad = new[]
                {
                    new VertexPositionTexture(new Vector3(-1, 1, 0), new Vector2(0, 0)),
                    new VertexPositionTexture(new Vector3(1, 1, 0), new Vector2(1, 0)),
                    new VertexPositionTexture(new Vector3(-1, -1, 0), new Vector2(0, 1)),
                    new VertexPositionTexture(new Vector3(1, -1, 0), new Vector2(1, 1)),
                };
                Status = "on (display type " + type + (type == 3 ? ", power " + power : "") + ")";
            }
            catch (Exception e) { effect = null; Status = "off (" + e.Message + ")"; }
        }

        /// <summary>The frame as the console's display controller sent it to the TV.</summary>
        public RenderTarget2D Apply(RenderTarget2D frame)
        {
            if (effect == null) return frame;
            device.SetRenderTarget(graded);
            device.BlendState = BlendState.Opaque;
            device.DepthStencilState = DepthStencilState.None;
            device.RasterizerState = RasterizerState.CullNone;
            effect.Parameters["SceneTexture"].SetValue(frame);
            effect.Parameters["LutTexture"].SetValue(lut);
            foreach (EffectPass p in effect.CurrentTechnique.Passes)
            {
                p.Apply();
                device.DrawUserPrimitives(PrimitiveType.TriangleStrip, quad, 0, 2);
            }
            return graded;
        }
    }
}
