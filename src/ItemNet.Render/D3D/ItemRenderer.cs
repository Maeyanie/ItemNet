using System.Numerics;
using System.Runtime.InteropServices;
using ItemNet.Core.FileSystem;
using ItemNet.Render.Nif;
using ItemNet.Render.Scene;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ItemNet.Render.D3D;

public sealed record ViewSpec(string Label, float YawDegrees, float PitchDegrees = 8);

public sealed class RenderSettings
{
    public int Size { get; init; } = 512;
    /// <summary>Supersampling factor on top of 4x MSAA.</summary>
    public int Supersample { get; init; } = 2;
    public Vector4 Background { get; init; } = new(0.78f, 0.78f, 0.80f, 1);
    public Vector3 ClayColor { get; init; } = new(0.62f, 0.62f, 0.63f);
    public float VerticalFovDegrees { get; init; } = 24;
    /// <summary>Smallest framed height as a fraction of the body's height, so small items keep context.</summary>
    public float MinFrameFraction { get; init; } = 0.4f;
    public float Padding { get; init; } = 0.08f;

    /// <summary>Character faces +Y in Skyrim; yaw is the camera's position around the subject.</summary>
    public static readonly ViewSpec[] ApparelViews =
    [
        new("front", 0), new("right", 90), new("back", 180), new("left", 270),
    ];
}

public sealed class RenderedView
{
    public required string Label { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
    public byte[] ToPng() => Png.Encode(Rgba, Width, Height);
}

[StructLayout(LayoutKind.Sequential)]
internal struct Vertex
{
    public Vector3 Pos, Nrm, Tan, Bit;
    public Vector2 Uv;
    public Vector4 Col;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FrameConstants
{
    public Matrix4x4 ViewProj;
    public Vector4 CameraPos;
    public Vector4 L0, L1, L2;
    public Vector4 C0, C1, C2;
    public Vector4 Ambient;
    public Vector4 GroundAmbient;
    public Vector4 CamRight, CamUp, CamFwd;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MeshConstants
{
    public Vector4 Flags0, Flags1, Spec, Params, Emissive, UvXform, Clay, Pbr, Fuzz;
}

/// <summary>Offscreen D3D11 renderer. One instance can render many scenes; not thread-safe.</summary>
public sealed class ItemRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11Buffer _frameCb, _meshCb;
    private readonly ID3D11SamplerState _sampler;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11BlendState _opaque, _alphaBlend;
    private readonly ID3D11DepthStencilState _depthWrite, _depthRead;
    private readonly TextureCache _textures;

    public string AdapterDescription { get; }
    public IReadOnlyCollection<string> MissingTextures => _textures.Missing;

    public ItemRenderer(IGameFileSystem fs)
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device!, out _ctx!);
        AdapterDescription = "hardware";
        if (hr.Failure)
        {
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _device!, out _ctx!).CheckError();
            AdapterDescription = "WARP (software)";
        }

        var vsCode = Compiler.Compile(Shaders.Source, "VS", "itemnet.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        var psCode = Compiler.Compile(Shaders.Source, "PS", "itemnet.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _vs = _device.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);
        _layout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("NORMAL", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TANGENT", 0, Format.R32G32B32_Float, 24, 0),
            new InputElementDescription("BINORMAL", 0, Format.R32G32B32_Float, 36, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 48, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 56, 0),
        ], vsCode.Span);

        _frameCb = _device.CreateBuffer((uint)Marshal.SizeOf<FrameConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _meshCb = _device.CreateBuffer((uint)Marshal.SizeOf<MeshConstants>(), BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.Anisotropic, TextureAddressMode.Wrap, TextureAddressMode.Wrap, TextureAddressMode.Wrap, 0, 8));
        _raster = _device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid) { MultisampleEnable = true, AntialiasedLineEnable = false, DepthClipEnable = true });
        _opaque = _device.CreateBlendState(BlendDescription.Opaque);
        _alphaBlend = _device.CreateBlendState(BlendDescription.NonPremultiplied);
        _depthWrite = _device.CreateDepthStencilState(DepthStencilDescription.Default);
        _depthRead = _device.CreateDepthStencilState(DepthStencilDescription.DepthRead);
        _textures = new TextureCache(_device, fs);
    }

    public List<RenderedView> Render(Scene.Scene scene, IReadOnlyList<ViewSpec> views, RenderSettings settings, bool clay)
    {
        var size = settings.Size * Math.Max(1, settings.Supersample);
        using var color = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, 1, BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 4, 0));
        using var depth = _device.CreateTexture2D(new Texture2DDescription(Format.D24_UNorm_S8_UInt, (uint)size, (uint)size, 1, 1, BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None, 4, 0));
        using var resolved = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, 1, BindFlags.None, ResourceUsage.Default, CpuAccessFlags.None, 1, 0));
        using var staging = _device.CreateTexture2D(new Texture2DDescription(Format.R8G8B8A8_UNorm, (uint)size, (uint)size, 1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0));
        using var rtv = _device.CreateRenderTargetView(color, new RenderTargetViewDescription(RenderTargetViewDimension.Texture2DMultisampled, Format.R8G8B8A8_UNorm));
        using var dsv = _device.CreateDepthStencilView(depth, new DepthStencilViewDescription(DepthStencilViewDimension.Texture2DMultisampled, Format.D24_UNorm_S8_UInt));

        var gpu = Upload(scene, clay, settings);
        try
        {
            var results = new List<RenderedView>();
            var framing = Framing.Compute(scene, settings);
            foreach (var view in views)
            {
                var (viewProj, camPos, fwd, right, up) = framing.Camera(view, settings);
                DrawView(gpu, rtv, dsv, size, viewProj, camPos, fwd, right, up, settings.Background);
                _ctx.ResolveSubresource(resolved, 0, color, 0, Format.R8G8B8A8_UNorm);
                _ctx.CopyResource(staging, resolved);
                var pixels = ReadBack(staging, size);
                results.Add(new RenderedView
                {
                    Label = view.Label, Width = settings.Size, Height = settings.Size,
                    Rgba = Downsample(pixels, size, settings.Supersample),
                });
            }
            return results;
        }
        finally
        {
            foreach (var g in gpu) { g.Vb.Dispose(); g.Ib.Dispose(); }
        }
    }

    private sealed record GpuMesh(ID3D11Buffer Vb, ID3D11Buffer Ib, int IndexCount, MeshConstants Constants,
        ID3D11ShaderResourceView[] Srvs, bool Blended, bool DepthWrite, Vector3 Centroid);

    private List<GpuMesh> Upload(Scene.Scene scene, bool clay, RenderSettings settings)
    {
        var list = new List<GpuMesh>();
        foreach (var m in scene.Meshes)
        {
            var verts = new Vertex[m.Positions.Length];
            var centroid = Vector3.Zero;
            for (var i = 0; i < verts.Length; i++)
            {
                verts[i] = new Vertex { Pos = m.Positions[i], Nrm = m.Normals[i], Tan = m.Tangents[i], Bit = m.Bitangents[i], Uv = m.Uvs[i], Col = m.Colors[i] };
                centroid += m.Positions[i];
            }
            centroid /= Math.Max(1, verts.Length);
            var vb = _device.CreateBuffer(verts.AsSpan(), new BufferDescription((uint)(verts.Length * Marshal.SizeOf<Vertex>()), BindFlags.VertexBuffer));
            var ib = _device.CreateBuffer(m.Indices.AsSpan(), new BufferDescription((uint)(m.Indices.Length * 4), BindFlags.IndexBuffer));

            var mat = m.Material;
            var isClay = clay && m.IsBody;
            var diffuse = _textures.Get(mat.Textures[0], TextureCache.Slot.Diffuse);
            var normal = _textures.Get(mat.Textures[1], TextureCache.Slot.Normal, out var hasNormal);
            var glow = _textures.Get(mat.Textures[2], TextureCache.Slot.Glow, out var hasGlow);
            var cube = _textures.Get(mat.Textures[4], TextureCache.Slot.Cube, out var hasCube);
            // Slot 5 is the env mask, or RMAOS for TruePBR; slot 6 the PBR fuzz/coat features map.
            var envMask = _textures.Get(mat.Textures[5], mat.Pbr ? TextureCache.Slot.Rmaos : TextureCache.Slot.EnvMask);
            var features = _textures.Get(mat.Pbr ? mat.Textures[6] : null, TextureCache.Slot.Features, out var hasFeatures);

            var alphaMode = mat.AlphaMode switch { Nif.AlphaMode.Test => 1, Nif.AlphaMode.Blend => 2, _ => 0 };
            var constants = new MeshConstants
            {
                Flags0 = new Vector4(hasNormal ? 1 : 0, mat.ModelSpaceNormals ? 1 : 0,
                    mat.EnvMap && hasCube && !mat.Pbr && Environment.GetEnvironmentVariable("ITEMNET_DEBUG") != "noenv" ? 1 : 0,
                    (mat.Glow || mat.Pbr) && hasGlow ? 1 : 0),
                Flags1 = new Vector4(alphaMode, mat.AlphaThreshold, mat.VertexColors ? 1 : 0, isClay ? 1 : 0),
                Spec = new Vector4(mat.SpecularColor, mat.Specular ? mat.SpecularStrength : 0.15f),
                Params = new Vector4(mat.Glossiness, mat.EnvMapScale, mat.EmissiveMultiple, mat.Alpha),
                Emissive = new Vector4(mat.EmissiveColor, 0),
                UvXform = new Vector4(mat.UvOffset, mat.UvScale.X == 0 ? 1 : mat.UvScale.X, mat.UvScale.Y == 0 ? 1 : mat.UvScale.Y),
                Clay = new Vector4(settings.ClayColor, Environment.GetEnvironmentVariable("ITEMNET_DEBUG") == "unlit" ? 2 : 1),
                Pbr = new Vector4(mat.Pbr ? 1 : 0, mat.PbrRoughnessScale, mat.PbrSpecularLevel, mat.FuzzWeight),
                Fuzz = new Vector4(mat.FuzzColor, hasFeatures ? 1 : 0),
            };
            list.Add(new GpuMesh(vb, ib, m.Indices.Length, constants, [diffuse, normal, envMask, glow, cube, features], alphaMode == 2 && !isClay, mat.DepthWrite, centroid));
        }
        return list;
    }

    private void DrawView(List<GpuMesh> meshes, ID3D11RenderTargetView rtv, ID3D11DepthStencilView dsv, int size,
        Matrix4x4 viewProj, Vector3 camPos, Vector3 fwd, Vector3 right, Vector3 up, Vector4 background)
    {
        _ctx.OMSetRenderTargets(rtv, dsv);
        _ctx.ClearRenderTargetView(rtv, new Color4(background.X, background.Y, background.Z, background.W));
        _ctx.ClearDepthStencilView(dsv, DepthStencilClearFlags.Depth, 1, 0);
        _ctx.RSSetViewport(new Viewport(size, size));
        _ctx.RSSetState(_raster);
        _ctx.IASetInputLayout(_layout);
        _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _ctx.VSSetShader(_vs);
        _ctx.PSSetShader(_ps);
        _ctx.VSSetConstantBuffer(0, _frameCb);
        _ctx.PSSetConstantBuffer(0, _frameCb);
        _ctx.VSSetConstantBuffer(1, _meshCb);
        _ctx.PSSetConstantBuffer(1, _meshCb);
        _ctx.PSSetSampler(0, _sampler);

        // Studio lights fixed relative to the camera, so every view is lit the same way.
        var frame = new FrameConstants
        {
            ViewProj = viewProj,
            CameraPos = new Vector4(camPos, 1),
            L0 = new Vector4(Vector3.Normalize(fwd + right * 0.55f - up * 0.65f), 0),   // key: upper left, front
            L1 = new Vector4(Vector3.Normalize(fwd - right * 0.8f - up * 0.15f), 0),    // fill: right
            L2 = new Vector4(Vector3.Normalize(-fwd - up * 0.6f + right * 0.2f), 0),    // rim: behind, above
            C0 = new Vector4(0.80f, 0.79f, 0.76f, 0),
            C1 = new Vector4(0.30f, 0.31f, 0.33f, 0),
            C2 = new Vector4(0.35f, 0.35f, 0.36f, 0),
            Ambient = new Vector4(0.36f, 0.36f, 0.38f, 0),
            GroundAmbient = new Vector4(0.22f, 0.21f, 0.20f, 0),
            CamRight = new Vector4(right, 0),
            CamUp = new Vector4(up, 0),
            CamFwd = new Vector4(fwd, 0),
        };
        _ctx.UpdateSubresource(in frame, _frameCb);

        var opaque = meshes.Where(m => !m.Blended);
        var blended = meshes.Where(m => m.Blended).OrderByDescending(m => Vector3.Dot(m.Centroid - camPos, fwd));
        _ctx.OMSetBlendState(_opaque);
        _ctx.OMSetDepthStencilState(_depthWrite);
        foreach (var m in opaque) DrawMesh(m);
        // Blended shapes go back to front; each writes depth only if its shader says so, as in game.
        _ctx.OMSetBlendState(_alphaBlend);
        foreach (var m in blended)
        {
            _ctx.OMSetDepthStencilState(m.DepthWrite ? _depthWrite : _depthRead);
            DrawMesh(m);
        }
    }

    private void DrawMesh(GpuMesh m)
    {
        var c = m.Constants;
        _ctx.UpdateSubresource(in c, _meshCb);
        _ctx.PSSetShaderResources(0, m.Srvs);
        _ctx.IASetVertexBuffer(0, m.Vb, (uint)Marshal.SizeOf<Vertex>(), 0);
        _ctx.IASetIndexBuffer(m.Ib, Format.R32_UInt, 0);
        _ctx.DrawIndexed((uint)m.IndexCount, 0, 0);
    }

    private byte[] ReadBack(ID3D11Texture2D staging, int size)
    {
        var mapped = _ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var result = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
                Marshal.Copy(mapped.DataPointer + (nint)(y * mapped.RowPitch), result, y * size * 4, size * 4);
            return result;
        }
        finally { _ctx.Unmap(staging, 0); }
    }

    private static byte[] Downsample(byte[] src, int srcSize, int factor)
    {
        if (factor <= 1) return src;
        var dstSize = srcSize / factor;
        var dst = new byte[dstSize * dstSize * 4];
        var n = factor * factor;
        for (var y = 0; y < dstSize; y++)
            for (var x = 0; x < dstSize; x++)
                for (var c = 0; c < 4; c++)
                {
                    var sum = 0;
                    for (var dy = 0; dy < factor; dy++)
                        for (var dx = 0; dx < factor; dx++)
                            sum += src[((y * factor + dy) * srcSize + x * factor + dx) * 4 + c];
                    dst[(y * dstSize + x) * 4 + c] = (byte)(sum / n);
                }
        return dst;
    }

    public void Dispose()
    {
        _textures.Dispose();
        _depthRead.Dispose(); _depthWrite.Dispose(); _alphaBlend.Dispose(); _opaque.Dispose();
        _raster.Dispose(); _sampler.Dispose(); _meshCb.Dispose(); _frameCb.Dispose();
        _layout.Dispose(); _ps.Dispose(); _vs.Dispose(); _ctx.Dispose(); _device.Dispose();
    }
}

/// <summary>Camera placement: frames the item, keeping a minimum amount of body for context.</summary>
internal sealed class Framing
{
    public Vector3 Min { get; private init; }
    public Vector3 Max { get; private init; }
    public Vector3 Center => (Min + Max) / 2;

    public static Framing Compute(Scene.Scene scene, RenderSettings settings)
    {
        var item = scene.Bounds(m => !m.IsBody) ?? scene.Bounds(_ => true) ?? (Vector3.Zero, Vector3.One);
        var body = scene.Bounds(m => m.IsBody);
        var (min, max) = item;
        if (body is { } b)
        {
            // Grow small items (rings, circlets) towards a minimum height, staying inside the body.
            var bodyHeight = b.max.Z - b.min.Z;
            var want = bodyHeight * settings.MinFrameFraction;
            var have = max.Z - min.Z;
            if (have < want)
            {
                var grow = (want - have) / 2;
                var lo = Math.Max(b.min.Z, min.Z - grow);
                var hi = Math.Min(b.max.Z, max.Z + grow);
                min = new Vector3(min.X, min.Y, lo);
                max = new Vector3(max.X, max.Y, hi);
            }
        }
        var pad = (max - min) * settings.Padding + new Vector3(1);
        return new Framing { Min = min - pad, Max = max + pad };
    }

    public (Matrix4x4 viewProj, Vector3 camPos, Vector3 fwd, Vector3 right, Vector3 up) Camera(ViewSpec view, RenderSettings settings)
    {
        var yaw = view.YawDegrees * MathF.PI / 180;
        var pitch = view.PitchDegrees * MathF.PI / 180;
        // Yaw 0 puts the camera in front of the character (+Y), looking back at it.
        var toCamera = Vector3.Normalize(new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Cos(yaw) * MathF.Cos(pitch), MathF.Sin(pitch)));
        var fwd = -toCamera;
        var right = Vector3.Normalize(Vector3.Cross(fwd, Vector3.UnitZ));
        var up = Vector3.Cross(right, fwd);

        // Fit the box's projection on the view plane.
        var ext = (Max - Min) / 2;
        float halfW = 0, halfH = 0, halfD = 0;
        foreach (var sx in new[] { -1f, 1f })
            foreach (var sy in new[] { -1f, 1f })
                foreach (var sz in new[] { -1f, 1f })
                {
                    var c = new Vector3(ext.X * sx, ext.Y * sy, ext.Z * sz);
                    halfW = Math.Max(halfW, Math.Abs(Vector3.Dot(c, right)));
                    halfH = Math.Max(halfH, Math.Abs(Vector3.Dot(c, up)));
                    halfD = Math.Max(halfD, Math.Abs(Vector3.Dot(c, fwd)));
                }
        var fov = settings.VerticalFovDegrees * MathF.PI / 180;
        var half = Math.Max(halfW, halfH);
        var dist = half / MathF.Tan(fov / 2) + halfD;
        var camPos = Center + toCamera * dist;

        var viewM = Matrix4x4.CreateLookAt(camPos, Center, Vector3.UnitZ);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fov, 1, Math.Max(1, dist - halfD * 3), dist + halfD * 3 + 100);
        return (viewM * proj, camPos, fwd, right, up);
    }
}
