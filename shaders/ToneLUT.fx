// ToneLUT.fx: the final pass, the Xbox 360's display gamma ramp (our own shader: on the console the
// display controller applied it). LutTexture is the 256 x 1 ramp XenosGamma.cs computes.

texture SceneTexture;
sampler SceneSampler = sampler_state
{
    Texture   = (SceneTexture);
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU  = Clamp; AddressV  = Clamp;
};

// 256x1, R=G=B=mapped level; point sampled, one texel per input level.
texture LutTexture;
sampler LutSampler = sampler_state
{
    Texture   = (LutTexture);
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU  = Clamp; AddressV  = Clamp;
};

struct VSOut
{
    float4 Position : POSITION0;
    float2 UV       : TEXCOORD0;
};

// Fullscreen triangle-pair drawn by SpriteBatch, so positions arrive already in clip space.
VSOut ToneVS(float4 pos : POSITION0, float2 uv : TEXCOORD0)
{
    VSOut o;
    o.Position = pos;
    o.UV = uv;
    return o;
}

// texel i's centre, (i + 0.5) / 256: a boundary could pick either neighbour
float3 Map(float3 c)
{
    float3 idx = saturate(c) * 255.0;
    return float3(
        tex2D(LutSampler, float2((idx.r + 0.5) / 256.0, 0.5)).r,
        tex2D(LutSampler, float2((idx.g + 0.5) / 256.0, 0.5)).g,
        tex2D(LutSampler, float2((idx.b + 0.5) / 256.0, 0.5)).b);
}

float4 TonePS(VSOut input) : COLOR0
{
    float4 c = tex2D(SceneSampler, input.UV);
    return float4(Map(c.rgb), c.a);
}

technique ToneLUT
{
    pass P0
    {
        VertexShader = compile vs_3_0 ToneVS();
        PixelShader  = compile ps_3_0 TonePS();
    }
}
