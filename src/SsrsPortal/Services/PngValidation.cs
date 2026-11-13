using System.Buffers.Binary;
using System.IO.Compression;

namespace SsrsPortal.Services;

// Validate the PNG container and bounded decoded scanlines, rather than trusting its MIME type
// or a signature alone. No platform image codecs or filesystem operations are involved.
internal static class PngValidation
{
    internal static bool Valid(byte[] png)
    {
        if (png.Length is < 57 or > SsrsBrandingService.MaxLogoBytes ||
            !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) { return false; }
        var offset = 8;
        int width = 0, height = 0, depth = 0, channels = 0, interlace = 0, type = 0;
        var header = false; var palette = false; var data = false; var dataEnded = false; var ended = false;
        using var compressed = new MemoryStream();
        while (offset <= png.Length - 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > png.Length - offset - 12) { return false; }
            var size = (int)length;
            var name = png.AsSpan(offset + 4, 4);
            if (name.IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8) >= 0 || (name[2] & 32) != 0) { return false; }
            var content = png.AsSpan(offset + 8, size);
            if (Crc(png.AsSpan(offset + 4, size + 4)) != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + size, 4))) { return false; }
            if (!header && !name.SequenceEqual("IHDR"u8)) { return false; }
            if (data && !name.SequenceEqual("IDAT"u8)) { dataEnded = true; }
            if (name.SequenceEqual("IHDR"u8))
            {
                if (header || size != 13) { return false; }
                header = true;
                var w = BinaryPrimitives.ReadUInt32BigEndian(content);
                var h = BinaryPrimitives.ReadUInt32BigEndian(content[4..]);
                if (w is 0 or > 4096 || h is 0 or > 4096 || (long)w * h > 4 * 1024 * 1024) { return false; }
                width = (int)w; height = (int)h; depth = content[8]; type = content[9]; interlace = content[12];
                channels = type switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
                if (channels == 0 || content[10] != 0 || content[11] != 0 || interlace > 1 ||
                    !(type switch { 0 => depth is 1 or 2 or 4 or 8 or 16, 3 => depth is 1 or 2 or 4 or 8, _ => depth is 8 or 16 })) { return false; }
            }
            else if (name.SequenceEqual("PLTE"u8))
            {
                if (palette || data || type is 0 or 4 || size is 0 or > 768 || size % 3 != 0 ||
                    (type == 3 && size / 3 > (1 << depth))) { return false; }
                palette = true;
            }
            else if (name.SequenceEqual("IDAT"u8))
            {
                if (dataEnded || (type == 3 && !palette)) { return false; }
                data = true;
                compressed.Write(content);
            }
            else if (name.SequenceEqual("IEND"u8))
            {
                if (size != 0 || !data || offset + 12 != png.Length) { return false; }
                ended = true;
                break;
            }
            else if ((name[0] & 32) == 0 || name.SequenceEqual("acTL"u8) || name.SequenceEqual("fcTL"u8) || name.SequenceEqual("fdAT"u8)) { return false; }
            offset += size + 12;
        }
        if (!ended) { return false; }
        compressed.Position = 0;
        try
        {
            using var pixels = new ZLibStream(compressed, CompressionMode.Decompress);
            var row = new byte[(width * channels * depth + 7) / 8];
            bool Pass(int x, int y, int dx, int dy)
            {
                var columns = width <= x ? 0 : (width - x + dx - 1) / dx;
                var rows = height <= y ? 0 : (height - y + dy - 1) / dy;
                if (columns == 0) { return true; }
                var bytes = (columns * channels * depth + 7) / 8;
                for (var index = 0; index < rows; index++)
                {
                    var filter = pixels.ReadByte();
                    if (filter is < 0 or > 4) { return false; }
                    pixels.ReadExactly(row.AsSpan(0, bytes));
                }
                return true;
            }
            var valid = interlace == 0 ? Pass(0, 0, 1, 1) :
                Pass(0, 0, 8, 8) && Pass(4, 0, 8, 8) && Pass(0, 4, 4, 8) && Pass(2, 0, 4, 4) &&
                Pass(0, 2, 2, 4) && Pass(1, 0, 2, 2) && Pass(0, 1, 1, 2);
            return valid && pixels.ReadByte() == -1;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return false; }
    }

    internal static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        }
        return ~crc;
    }
}
