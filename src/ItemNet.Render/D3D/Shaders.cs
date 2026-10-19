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
            float4 Pbr;       // x TruePBR, y roughness scale, z specular level, w fuzz weight
            float4 FuzzColor; // rgb fuzz tint, w has features texture
        };

        Texture2D Diffuse : register(t0);
        Texture2D NormalMap : register(t1);
        Texture2D EnvMask : register(t2);   // RMAOS for TruePBR
        Texture2D GlowMap : register(t3);
        TextureCube EnvCube : register(t4);
        Texture2D Features : register(t5);  // TruePBR fuzz: rgb tint, a weight
        SamplerState Samp : register(s0);

        static const float PI = 3.14159265;

        // [Karis 2014] analytic fit of the split-sum environment BRDF.
        float3 EnvBRDFApprox(float3 F0, float rough, float NdV)
        {
            const float4 c0 = float4(-1, -0.0275, -0.572, 0.022);
            const float4 c1 = float4(1, 0.0425, 1.04, -0.04);
            float4 r = rough * c0 + c1;
            float a004 = min(r.x * r.x, exp2(-9.28 * NdV)) * r.x + r.y;
            float2 AB = float2(-1.04, 1.04) * a004 + r.zw;
            return F0 * AB.x + AB.y;
        }

        // Community Shaders' TruePBR, without its screen-space and multi-layer extras: GGX + Smith
        // specular, Lambert diffuse, and the fuzz (cloth sheen) lobe. Our light colours already carry
        // the 1/pi of the Lambert term, hence the pi on the specular.
        float3 ShadePbr(float3 albedo, float3 N, float3 V, float2 uv, float hemi)
        {
            float4 rmaos = EnvMask.Sample(Samp, uv);
            float rough = clamp(rmaos.r * Pbr.y, 0.05, 1);
            float metal = saturate(rmaos.g);
            float ao = rmaos.b;
            float3 F0 = lerp((rmaos.a * Pbr.z).xxx, albedo, metal);
            float3 diffuse = albedo * (1 - metal);
            float3 fuzzColor = FuzzColor.rgb;
            float fuzzWeight = Pbr.w;
            if (FuzzColor.w > 0)
            {
                float4 f = Features.Sample(Samp, uv);
                fuzzColor *= f.rgb;
                fuzzWeight *= f.a;
            }

            float NdV = max(dot(N, V), 1e-4);
            float3 color = (diffuse + fuzzColor * fuzzWeight) * lerp(GroundAmbient.rgb, Ambient.rgb, hemi) * ao;
            float a = rough * rough;
            float a2 = a * a;
            [unroll] for (int k = 0; k < 3; k++)
            {
                float3 L = -LightDir[k].xyz;
                float NdL = saturate(dot(N, L));
                float3 H = normalize(L + V);
                float NdH = saturate(dot(N, H));
                float VdH = saturate(dot(V, H));
                float d = NdH * NdH * (a2 - 1) + 1;
                float D = a2 / (PI * d * d);
                float Vis = 0.5 / max(NdL * (NdV * (1 - a) + a) + NdV * (NdL * (1 - a) + a), 1e-4);
                float Fc = pow(1 - VdH, 5);
                float3 F = F0 + (1 - F0) * Fc;
                float3 spec = D * Vis * F;
                if (fuzzWeight > 0)
                {
                    float inv = pow(rough, -4);
                    float Dc = (2 + inv) * pow(saturate(1 - NdH * NdH), inv * 0.5) / (2 * PI);
                    float Vn = 1 / (4 * max(NdL + NdV - NdL * NdV, 1e-4));
                    spec = lerp(spec, Dc * Vn * (fuzzColor + (1 - fuzzColor) * Fc), fuzzWeight);
                }
                color += (diffuse * (1 - F) + PI * spec) * LightColor[k].rgb * NdL;
            }

            // No cube map comes with these materials, so reflect the studio itself: the same sky/ground
            // gradient as the ambient light, a little brighter overhead.
            float3 R = reflect(-V, N);
            float3 env = lerp(GroundAmbient.rgb, Ambient.rgb * 1.5, saturate(R.z * 0.5 + 0.5));
            color += env * EnvBRDFApprox(F0, rough, NdV) * ao;
            return color;
        }

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
            if (Flags1.x >= 1 && alpha < Flags1.y) discard; // test, or blend with a test threshold

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
                // Bethesda's stored "bitangent" runs along +U and its "tangent" along V, so X goes
                // with the bitangent, as in the game's own TBN (BINORMAL0 carries the tangent field).
                float3 t = normalize(i.tan) * facing;
                float3 b = normalize(i.bit) * facing;
                N = normalize(b * n.x + t * n.y + N * n.z);
                specMask = nm.a;
            }

            float hemi = N.z * 0.5 + 0.5;
            if (Pbr.x > 0 && !clay)
            {
                float3 lit = ShadePbr(albedo, N, V, i.uv, hemi);
                if (Flags0.w > 0)
                    lit += GlowMap.Sample(Samp, i.uv).rgb * Emissive.rgb * Params.z;
                return float4(saturate(lit), Flags1.x == 2 ? alpha : 1);
            }
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
