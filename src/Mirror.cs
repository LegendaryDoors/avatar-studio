// The standing mirror on My Style (EditorBG.xur's scnMirror), with the avatar reflected in it.

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace AvatarStudio
{
    sealed class Mirror
    {
        // AvatarMirrorParentSceneOverride (sub_920EF788 / sub_920EF800)
        private const float ShownX = 80f;                   // scnMirror's own x
        private const float HiddenX = ShownX - 640f;        // x - 640 (flt_920073FC)
        private const float Ease = 0.08f, Keep = 0.92f;      // 0x9200C200 / 0x9200C204
        private float current = HiddenX;                     // +0xC, starts hidden (sub_920EF788)
        private float posX = ShownX;                         // the element's x until the first tick

        private readonly GraphicsDevice device;
        private BasicEffect fx;

        public Mirror(GraphicsDevice device) { this.device = device; }

        /// <summary>One tick of timer 0x452 (sub_920EF800): x eases toward shown on My Style, else hidden.</summary>
        public void Tick(bool myStyle)
        {
            float target = myStyle ? ShownX : HiddenX;
            current = target * Ease + current * Keep;
            if (Math.Abs(posX - current) > 1f) posX = current;
        }

        /// <summary>The stencil scene is shown while the mirror is not parked (sub_920F1DF0).</summary>
        private bool StencilShown { get { return posX != HiddenX; } }

        // ---- XUI geometry ------------------------------------------------------------------------

        private static Matrix Local(Vector3 position, Vector2 scale, Quaternion rotation, Vector3 pivot)
        {
            return Matrix.CreateTranslation(-pivot) * Matrix.CreateScale(scale.X, scale.Y, 1f)
                 * Matrix.CreateFromQuaternion(rotation) * Matrix.CreateTranslation(pivot)
                 * Matrix.CreateTranslation(position);
        }

        private static Matrix At(float x, float y) { return Matrix.CreateTranslation(x, y, 0f); }

        private Matrix MirrorFull()
        {
            return Local(new Vector3(posX, 38.6501f, 3000f), new Vector2(3f, 5.18182f),
                         new Quaternion(-0.018149f, -0.086984f, -0.01026f, 0.995987f), new Vector3(253.333f, 317f, 0f));
        }

        /// <summary>T(-C) x P x T(C), P's M34 = ProjectionScale 1.5 x 0.001.</summary>
        private static Matrix Perspective(Matrix mirrorFull)
        {
            Vector3 c = Vector3.Transform(new Vector3(0.5f * 506.667f, 0.5f * 634f, 0f), mirrorFull);
            Matrix p = Matrix.Identity;
            p.M34 = 1.5f * 0.001f;
            return Matrix.CreateTranslation(-c.X, -c.Y, 0f) * p * Matrix.CreateTranslation(c.X, c.Y, 0f);
        }

        /// <summary>The 1280 x 720 canvas to clip space; z to 0 so nothing is depth-clipped.</summary>
        private static readonly Matrix Canvas = new Matrix(2f / Layout.W, 0, 0, 0, 0, -2f / Layout.H, 0, 0, 0, 0, 0, 0, -1f, 1f, 0, 1f);

        // ---- drawing ------------------------------------------------------------------------------

        private static readonly BlendState Additive = new BlendState     // mode 5 on premultiplied art
        {
            ColorSourceBlend = Blend.One, ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One, AlphaDestinationBlend = Blend.InverseSourceAlpha,
        };
        private static readonly BlendState NoColour = new BlendState { ColorWriteChannels = ColorWriteChannels.None };
        private static readonly DepthStencilState WriteStencil = new DepthStencilState
        {
            DepthBufferEnable = false, StencilEnable = true, StencilFunction = CompareFunction.Always,
            ReferenceStencil = 255, StencilPass = StencilOperation.Replace,
        };

        /// <summary>The mirror and the avatar in it, between the backdrop and the avatar.</summary>
        public void Draw(TileArt art, AvatarView stage)
        {
            if (fx == null) fx = new BasicEffect(device) { VertexColorEnabled = true };
            BlendState blend = device.BlendState;
            DepthStencilState depth = device.DepthStencilState;
            RasterizerState raster = device.RasterizerState;
            device.RasterizerState = RasterizerState.CullNone;
            device.DepthStencilState = DepthStencilState.None;

            Matrix mirror = MirrorFull();
            Matrix post = Perspective(mirror) * Canvas;

            // scnMirrorModelBG: a #FF0F0F0F figure at opacity 0.9, then a #FF9F98B0 one
            Matrix bg = At(-12f, 77f) * mirror;
            device.BlendState = BlendState.NonPremultiplied;
            Figure(At(-10f, -10f) * bg, 352f, 444f, new Color(0x0F, 0x0F, 0x0F) * 0.9f, post);
            Figure(bg, 332f, 424f, new Color(0x9F, 0x98, 0xB0), post);

            // scnMirrorStencil: its figure into the stencil, the reflected avatar, the stencil cleared
            if (StencilShown)
            {
                device.BlendState = NoColour;
                device.DepthStencilState = WriteStencil;
                Figure(At(5f, 0f) * At(-10f, 78f) * mirror, 332f, 424f, new Color(0x7B, 0xA0, 0x82) * 0.9f, post);
                device.BlendState = BlendState.Opaque;
                stage.DrawReflection(Reflection(-10f));
                device.Clear(ClearOptions.Stencil, Color.Black, 1f, 0);
                device.RasterizerState = RasterizerState.CullNone;
                device.DepthStencilState = DepthStencilState.None;
            }

            // mirror sheen (-118, 27), 512 x 512, opacity 0.5, BlendMode 5
            Texture2D sheen = art.Named("closet_mirror_sheen");
            if (sheen != null)
            {
                device.BlendState = Additive;
                Image(At(-118f, 27f) * mirror, 512f, 512f, sheen, 0.5f, post);
            }
            // scnMirrorModelFG: the mirror's border
            Texture2D border = art.Named("closet_mirror_border");
            if (border != null)
            {
                device.BlendState = BlendState.AlphaBlend;
                Matrix frame = Local(new Vector3(-93.9468f, -48.8253f, 0f), Vector2.One,
                                     new Quaternion(-0.001745f, 0.002618f, 0f, -1f), Vector3.Zero) * At(-10f, 79f) * mirror;
                Image(frame, 512f, 512f, border, 1f, post);
            }
            device.BlendState = blend; device.DepthStencilState = depth; device.RasterizerState = raster;
        }

        /// <summary>AE_DrawMirror's reflection (0x920EFA74): a plane through P with normal n, from the stencil's x.</summary>
        private static Matrix Reflection(float x)
        {
            Vector3 n = Vector3.Normalize(new Vector3(-1.15f, -0.15f, 1.8f));
            float px = 0.5f - (-8f + x) * 0.02f;
            float d = px * n.X + 1f * n.Y + -1f * n.Z;
            return new Matrix(
                1f - 2f * n.X * n.X, -2f * n.X * n.Y, -2f * n.X * n.Z, 0f,
                -2f * n.X * n.Y, 1f - 2f * n.Y * n.Y, -2f * n.Y * n.Z, 0f,
                -2f * n.X * n.Z, -2f * n.Y * n.Z, 1f - 2f * n.Z * n.Z, 0f,
                -2f * d * n.X, -2f * d * n.Y, -2f * d * n.Z, 1f);
        }

        private readonly VertexPositionColor[] quadC = new VertexPositionColor[4];
        private readonly VertexPositionColorTexture[] quadT = new VertexPositionColorTexture[4];

        private void Figure(Matrix full, float w, float h, Color c, Matrix post)
        {
            quadC[0] = new VertexPositionColor(new Vector3(0, 0, 0), c);
            quadC[1] = new VertexPositionColor(new Vector3(w, 0, 0), c);
            quadC[2] = new VertexPositionColor(new Vector3(0, h, 0), c);
            quadC[3] = new VertexPositionColor(new Vector3(w, h, 0), c);
            fx.TextureEnabled = false;
            fx.World = full * post; fx.View = Matrix.Identity; fx.Projection = Matrix.Identity;
            fx.CurrentTechnique.Passes[0].Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleStrip, quadC, 0, 2);
        }

        private void Image(Matrix full, float w, float h, Texture2D t, float opacity, Matrix post)
        {
            Color c = Color.White * opacity;                 // premultiplied art: opacity scales it all
            quadT[0] = new VertexPositionColorTexture(new Vector3(0, 0, 0), c, new Vector2(0, 0));
            quadT[1] = new VertexPositionColorTexture(new Vector3(w, 0, 0), c, new Vector2(1, 0));
            quadT[2] = new VertexPositionColorTexture(new Vector3(0, h, 0), c, new Vector2(0, 1));
            quadT[3] = new VertexPositionColorTexture(new Vector3(w, h, 0), c, new Vector2(1, 1));
            fx.TextureEnabled = true; fx.Texture = t;
            fx.World = full * post; fx.View = Matrix.Identity; fx.Projection = Matrix.Identity;
            device.SamplerStates[0] = SamplerState.LinearClamp;
            fx.CurrentTechnique.Passes[0].Apply();
            device.DrawUserPrimitives(PrimitiveType.TriangleStrip, quadT, 0, 2);
        }
    }
}
