using System.Buffers.Binary;
using Vortice.DXGI;

namespace ItemNet.Render.D3D;

/// <summary>A parsed DDS file: format, dimensions and per-subresource byte ranges (face-major, then mip).</summary>
public sealed class DdsTexture
{
    public required Format Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int MipLevels { get; init; }
    public required int ArraySize { get; init; }
    public required bool IsCube { get; init; }
    public required byte[] Data { get; init; }
    /// <summary>Per subresource: offset into Data, row pitch, slice pitch.</summary>
    public required (int offset, int rowPitch, int slicePitch)[] Subresources { get; init; }

    public static bool IsBlockCompressed(Format f) => f is Format.BC1_UNorm or Format.BC2_UNorm or Format.BC3_UNorm
        or Format.BC4_UNorm or Format.BC4_SNorm or Format.BC5_UNorm or Format.BC5_SNorm or Format.BC7_UNorm
        or Format.BC6H_Uf16 or Format.BC6H_Sf16 or Format.BC1_UNorm_SRgb or Format.BC2_UNorm_SRgb or Format.BC3_UNorm_SRgb
        or Format.BC7_UNorm_SRgb;

    private static int BlockBytes(Format f) => f is Format.BC1_UNorm or Format.BC1_UNorm_SRgb or Format.BC4_UNorm or Format.BC4_SNorm ? 8 : 16;

    public static DdsTexture Parse(byte[] file)
    {
        var s = file.AsSpan();
        if (s.Length < 128 || BinaryPrimitives.ReadUInt32LittleEndian(s) != 0x20534444)
            throw new InvalidDataException("Not a DDS file");
        uint U(int off) => BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(off));

        var height = (int)U(12);
        var width = (int)U(16);
        var mips = Math.Max(1, (int)U(28));
        var pfFlags = U(80);
        var fourCC = U(84);
        var bitCount = U(88);
        uint rMask = U(92), gMask = U(96), bMask = U(100), aMask = U(104);
        var caps2 = U(112);
        var isCube = (caps2 & 0x200) != 0;
        var arraySize = isCube ? 6 : 1;
        var offset = 128;

        Format format;
        byte[]? converted = null;
        int bpp = 0; // bytes per pixel for uncompressed formats after conversion

        if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
        {
            switch (fourCC)
            {
                case 0x31545844: format = Format.BC1_UNorm; break;                         // DXT1
                case 0x32545844: case 0x33545844: format = Format.BC2_UNorm; break;        // DXT2/3
                case 0x34545844: case 0x35545844: format = Format.BC3_UNorm; break;        // DXT4/5
                case 0x31495441: case 0x55344342: format = Format.BC4_UNorm; break;        // ATI1 / BC4U
                case 0x53344342: format = Format.BC4_SNorm; break;                         // BC4S
                case 0x32495441: case 0x55354342: format = Format.BC5_UNorm; break;        // ATI2 / BC5U
                case 0x53354342: format = Format.BC5_SNorm; break;                         // BC5S
                case 0x30315844:                                                           // DX10
                    format = (Format)U(128);
                    var miscFlag = U(136);
                    arraySize = Math.Max(1, (int)U(140));
                    if ((miscFlag & 0x4) != 0) { isCube = true; arraySize *= 6; }
                    offset = 148;
                    break;
                default: throw new NotSupportedException($"Unsupported DDS FourCC 0x{fourCC:X8}");
            }
            // Game textures are authored in gamma space but sampled as linear; keep that look.
            format = format switch
            {
                Format.BC1_UNorm_SRgb => Format.BC1_UNorm, Format.BC2_UNorm_SRgb => Format.BC2_UNorm,
                Format.BC3_UNorm_SRgb => Format.BC3_UNorm, Format.BC7_UNorm_SRgb => Format.BC7_UNorm,
                Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm, Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
                _ => format,
            };
            if (!IsBlockCompressed(format))
            {
                bpp = format switch
                {
                    Format.R8G8B8A8_UNorm or Format.B8G8R8A8_UNorm or Format.B8G8R8X8_UNorm => 4,
                    Format.R8_UNorm or Format.A8_UNorm => 1,
                    Format.R8G8_UNorm => 2,
                    _ => throw new NotSupportedException($"Unsupported DXGI format {format}"),
                };
            }
        }
        else if (bitCount == 32)
        {
            format = rMask == 0x00FF0000 ? (aMask != 0 ? Format.B8G8R8A8_UNorm : Format.B8G8R8X8_UNorm) : Format.R8G8B8A8_UNorm;
            bpp = 4;
        }
        else if (bitCount == 24)
        {
            // No 24-bit DXGI format: expand BGR to BGRX.
            format = Format.B8G8R8X8_UNorm;
            bpp = 4;
            converted = Expand(s[offset..], width, height, mips, arraySize, 3, (src, dst) =>
            {
                for (int i = 0, o = 0; i + 2 < src.Length; i += 3, o += 4) { dst[o] = src[i]; dst[o + 1] = src[i + 1]; dst[o + 2] = src[i + 2]; dst[o + 3] = 255; }
            });
        }
        else if (bitCount == 8)
        {
            // L8 / A8: expand to greyscale RGBA so every shader path can treat it as colour.
            var isAlpha = (pfFlags & 0x2) != 0 && rMask == 0;
            format = Format.R8G8B8A8_UNorm;
            bpp = 4;
            converted = Expand(s[offset..], width, height, mips, arraySize, 1, (src, dst) =>
            {
                for (var i = 0; i < src.Length; i++)
                {
                    var l = src[i];
                    dst[i * 4] = dst[i * 4 + 1] = dst[i * 4 + 2] = isAlpha ? (byte)255 : l;
                    dst[i * 4 + 3] = isAlpha ? l : (byte)255;
                }
            });
        }
        else throw new NotSupportedException($"Unsupported uncompressed DDS ({bitCount} bpp)");

        var data = converted ?? s[offset..].ToArray();

        // Layout: each face holds the declared mip chain back to back.
        var levels = new (int offset, int row, int slice)[mips];
        var faceStride = 0;
        {
            int w = width, h = height;
            for (var mip = 0; mip < mips; mip++)
            {
                int row, slice;
                if (IsBlockCompressed(format))
                {
                    row = Math.Max(1, (w + 3) / 4) * BlockBytes(format);
                    slice = row * Math.Max(1, (h + 3) / 4);
                }
                else
                {
                    row = w * bpp;
                    slice = row * h;
                }
                levels[mip] = (faceStride, row, slice);
                faceStride += slice;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
        }

        // Truncated files: keep the mip levels every face actually has.
        var usableMips = mips;
        while (usableMips > 0)
        {
            var last = levels[usableMips - 1];
            if ((arraySize - 1) * faceStride + last.offset + last.slice <= data.Length) break;
            usableMips--;
        }
        if (usableMips == 0) throw new InvalidDataException("DDS has no complete mip level");

        var subs = new List<(int, int, int)>();
        for (var face = 0; face < arraySize; face++)
            for (var mip = 0; mip < usableMips; mip++)
                subs.Add((face * faceStride + levels[mip].offset, levels[mip].row, levels[mip].slice));
        mips = usableMips;

        return new DdsTexture
        {
            Format = format, Width = width, Height = height, MipLevels = mips, ArraySize = arraySize, IsCube = isCube,
            Data = data, Subresources = subs.ToArray(),
        };
    }

    private static byte[] Expand(ReadOnlySpan<byte> src, int width, int height, int mips, int faces, int srcBpp, Action<byte[], byte[]> convert)
    {
        var output = new List<byte>();
        var pos = 0;
        for (var f = 0; f < faces; f++)
        {
            int w = width, h = height;
            for (var m = 0; m < mips; m++)
            {
                var n = w * h;
                if (pos + n * srcBpp > src.Length) break;
                var dst = new byte[n * 4];
                convert(src.Slice(pos, n * srcBpp).ToArray(), dst);
                output.AddRange(dst);
                pos += n * srcBpp;
                w = Math.Max(1, w / 2);
                h = Math.Max(1, h / 2);
            }
        }
        return output.ToArray();
    }
}
