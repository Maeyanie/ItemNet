using System.Runtime.InteropServices;
using ItemNet.Core.FileSystem;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ItemNet.Render.D3D;

/// <summary>Loads DDS textures through the game file system, with neutral fallbacks per slot.</summary>
public sealed class TextureCache : IDisposable
{
    public enum Slot { Diffuse, Normal, EnvMask, Glow, Cube, Rmaos, Features }

    private readonly ID3D11Device _device;
    private readonly IGameFileSystem _fs;
    private readonly Dictionary<string, ID3D11ShaderResourceView?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Slot, ID3D11ShaderResourceView> _defaults = new();
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Missing => _missing;

    public TextureCache(ID3D11Device device, IGameFileSystem fs)
    {
        _device = device;
        _fs = fs;
        _defaults[Slot.Diffuse] = Solid(0xFF9A9A9A);
        _defaults[Slot.Normal] = Solid(0xFFFF8080); // (0.5, 0.5, 1) in RGBA little-endian order: R=0x80 G=0x80 B=0xFF A=0xFF
        _defaults[Slot.EnvMask] = Solid(0xFFFFFFFF);
        _defaults[Slot.Glow] = Solid(0xFF000000);
        _defaults[Slot.Cube] = SolidCube(0xFF808080);
        _defaults[Slot.Rmaos] = Solid(0xFFFF00FF);    // rough, not metallic, no occlusion, F0 = specular level
        _defaults[Slot.Features] = Solid(0xFFFFFFFF); // fuzz/coat parameters unscaled
    }

    public ID3D11ShaderResourceView Get(string? path, Slot slot) => Get(path, slot, out _);

    public ID3D11ShaderResourceView Get(string? path, Slot slot, out bool found)
    {
        found = false;
        if (string.IsNullOrWhiteSpace(path)) return _defaults[slot];
        var key = Normalize(path);
        if (!_cache.TryGetValue(key, out var srv))
        {
            srv = Load(key);
            _cache[key] = srv;
        }
        if (srv is null) return _defaults[slot];
        found = true;
        return srv;
    }

    public static string Normalize(string path)
    {
        var p = IGameFileSystem.Normalize(path);
        return p.StartsWith(@"textures\", StringComparison.Ordinal) ? p : @"textures\" + p;
    }

    private ID3D11ShaderResourceView? Load(string path)
    {
        var bytes = _fs.ReadAllBytes(path);
        if (bytes is null) { _missing.Add(path); return null; }
        try
        {
            var dds = DdsTexture.Parse(bytes);
            if (DdsTexture.IsBlockCompressed(dds.Format) && (dds.Width % 4 != 0 || dds.Height % 4 != 0))
            {
                _missing.Add(path + " (block-compressed size not a multiple of 4)");
                return null;
            }
            var handle = GCHandle.Alloc(dds.Data, GCHandleType.Pinned);
            try
            {
                var basePtr = handle.AddrOfPinnedObject();
                var init = dds.Subresources.Select(s => new SubresourceData(basePtr + s.offset, (uint)s.rowPitch, (uint)s.slicePitch)).ToArray();
                var desc = new Texture2DDescription(dds.Format, (uint)dds.Width, (uint)dds.Height, (uint)dds.ArraySize, (uint)dds.MipLevels,
                    BindFlags.ShaderResource, ResourceUsage.Immutable, CpuAccessFlags.None, 1, 0,
                    dds.IsCube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None);
                using var tex = _device.CreateTexture2D(desc, init);
                return dds.IsCube
                    ? _device.CreateShaderResourceView(tex, new ShaderResourceViewDescription(ShaderResourceViewDimension.TextureCube, dds.Format, 0, (uint)dds.MipLevels))
                    : _device.CreateShaderResourceView(tex);
            }
            finally { handle.Free(); }
        }
        catch (Exception e)
        {
            _missing.Add($"{path} ({e.Message})");
            return null;
        }
    }

    private ID3D11ShaderResourceView Solid(uint rgba)
    {
        using var tex = _device.CreateTexture2D([rgba], Format.R8G8B8A8_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource, ResourceOptionFlags.None, ResourceUsage.Immutable, CpuAccessFlags.None);
        return _device.CreateShaderResourceView(tex);
    }

    private ID3D11ShaderResourceView SolidCube(uint rgba)
    {
        var data = Enumerable.Repeat(rgba, 6).ToArray();
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var ptr = handle.AddrOfPinnedObject();
            var init = Enumerable.Range(0, 6).Select(i => new SubresourceData(ptr + i * 4, 4, 4)).ToArray();
            var desc = new Texture2DDescription(Format.R8G8B8A8_UNorm, 1, 1, 6, 1, BindFlags.ShaderResource, ResourceUsage.Immutable,
                CpuAccessFlags.None, 1, 0, ResourceOptionFlags.TextureCube);
            using var tex = _device.CreateTexture2D(desc, init);
            return _device.CreateShaderResourceView(tex, new ShaderResourceViewDescription(ShaderResourceViewDimension.TextureCube, Format.R8G8B8A8_UNorm, 0, 1));
        }
        finally { handle.Free(); }
    }

    public void Dispose()
    {
        foreach (var srv in _cache.Values) srv?.Dispose();
        foreach (var srv in _defaults.Values) srv.Dispose();
    }
}
