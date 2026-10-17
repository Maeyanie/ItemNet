namespace ItemNet.Render.D3D;

internal static class Shaders
{
    public const string Source = """
        cbuffer Frame : register(b0)
        {
            row_major float4x4 ViewProj;
            float4 CameraPos;
            float4 LightDir[3];   // direction the light travels (towards the subject)
            float4 LightColor[3];
            float4 Ambient;       // rgb: sky colour; a: unused
            float4 GroundAmbient;
            float4 CamRight;      // camera basis, so reflections turn with the camera like the lights
            float4 CamUp;
            float4 CamFwd;
        };

        cbuffer Mesh : register(b1)
        {
            float4 Flags0;    // x normal map, y model-space normals, z env map, w glow
            float4 Flags1;    // x alpha mode (0 opaque, 1 test, 2 blend), y threshold, z vertex colours, w clay
            float4 Spec;      // rgb specular colour, a specular strength
            float4 Params;    // x glossiness, y env scale, z emissive multiple, w alpha
            float4 Emissive;  // rgb emissive colour
            float4 UvXform;   // xy offset, zw scale
            float4 ClayColor;
        };

        Texture2D Diffuse : register(t0);
        Texture2D NormalMap : register(t1);
        Texture2D EnvMask : register(t2);
        Texture2D GlowMap : register(t3);
        TextureCube EnvCube : register(t4);
        SamplerState Samp : register(s0);

        struct VSIn
        {
            float3 pos : POSITION;
            float3 nrm : NORMAL;
            float3 tan : TANGENT;
            float3 bit : BINORMAL;
            float2 uv  : TEXCOORD0;
            float4 col : COLOR0;
        };

        struct PSIn
        {
            float4 pos  : SV_Position;
            float3 wpos : TEXCOORD1;
            float3 nrm  : NORMAL;
            float3 tan  : TANGENT;
            float3 bit  : BINORMAL;
            float2 uv   : TEXCOORD0;
            float4 col  : COLOR0;
        };

        PSIn VS(VSIn i)
        {
            PSIn o;
            o.pos = mul(float4(i.pos, 1), ViewProj);
            o.wpos = i.pos;
            o.nrm = i.nrm;
            o.tan = i.tan;
            o.bit = i.bit;
            o.uv = i.uv * UvXform.zw + UvXform.xy;
            o.col = i.col;
            return o;
        }

        float4 PS(PSIn i, bool front : SV_IsFrontFace) : SV_Target
        {
            bool clay = Flags1.w > 0;
            float4 base = Diffuse.Sample(Samp, i.uv);
            if (Flags1.z > 0) base *= i.col;
            float alpha = base.a * Params.w;
            if (Flags1.x == 1 && alpha < Flags1.y) discard;

            if (ClayColor.w > 1.5) return float4(base.rgb, 1); // debug: unlit diffuse
            float3 albedo = clay ? ClayColor.rgb : base.rgb;
            float3 V = normalize(CameraPos.xyz - i.wpos);
            // Winding is unreliable across meshes, so face the interpolated normal towards the camera
            // instead of trusting SV_IsFrontFace; the normal map is then applied in that frame.
            float3 geo = normalize(i.nrm);
            float facing = dot(geo, V) < 0 ? -1 : 1;
            float3 N = geo * facing;
            // Model-space-normal meshes (skin) keep specular in a separate _s map; approximate it as low.
            float specMask = clay ? 0.25 : (Flags0.y > 0 ? 0.08 : 1);
            if (Flags0.x > 0 && !clay && Flags0.y == 0)
            {
                float4 nm = NormalMap.Sample(Samp, i.uv);
                float3 n = nm.xyz * 2 - 1;
                float3 t = normalize(i.tan) * facing;
                float3 b = normalize(i.bit) * facing;
                N = normalize(t * n.x + b * n.y + N * n.z);
                specMask = nm.a;
            }

            float hemi = N.z * 0.5 + 0.5;
            float3 color = albedo * lerp(GroundAmbient.rgb, Ambient.rgb, hemi);
            float gloss = max(Params.x, 2);
            [unroll] for (int k = 0; k < 3; k++)
            {
                float3 L = -LightDir[k].xyz;
                float ndl = saturate(dot(N, L));
                color += albedo * LightColor[k].rgb * ndl;
                float3 H = normalize(L + V);
                float spec = pow(saturate(dot(N, H)), gloss) * ndl;
                color += spec * Spec.rgb * Spec.a * specMask * LightColor[k].rgb * 0.6;
            }

            if (Flags0.z > 0 && !clay)
            {
                float3 R = reflect(-V, N);
                float mask = EnvMask.Sample(Samp, i.uv).r;
                // Sample in camera space: a world-fixed cube made one view much brighter than the rest.
                float3 Rv = float3(dot(R, CamRight.xyz), dot(R, CamUp.xyz), dot(R, -CamFwd.xyz));
                color += EnvCube.Sample(Samp, Rv).rgb * mask * Params.y * 0.6;
            }
            if (Flags0.w > 0 && !clay)
                color += GlowMap.Sample(Samp, i.uv).rgb * Emissive.rgb * Params.z;

            return float4(saturate(color), Flags1.x == 2 ? alpha : 1);
        }
        """;
}
