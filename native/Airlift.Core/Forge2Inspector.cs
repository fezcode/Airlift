using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using ZstdSharp;

namespace Airlift.Core;

internal static class Forge2Inspector
{
    public static JsonDocument ReadManifest(Stream payload)
    {
        if (payload.Length < 4) throw new InvalidDataException("Missing Forge 2 index.");
        payload.Position = payload.Length - 4;
        Span<byte> lengthBytes = stackalloc byte[4]; payload.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (length > 16_000_000 || length > payload.Length - 4) throw new InvalidDataException("Invalid Forge 2 index bounds.");
        var dataEnd = payload.Length - 4 - length;
        payload.Position = dataEnd;
        var bytes = new byte[length]; payload.ReadExactly(bytes);
        using var index = JsonDocument.Parse(bytes);
        if (index.RootElement.GetProperty("format_version").GetInt32() != 2) throw new InvalidDataException("Unsupported Forge 2 index.");
        var entries = index.RootElement.GetProperty("entries").EnumerateArray().Where(e => e.GetProperty("path").GetString() == "manifest.json").ToArray();
        if (entries.Length != 1) throw new InvalidDataException("Missing or duplicate Forge 2 manifest.");
        var entry = entries[0];
        var offset = entry.GetProperty("offset").GetInt64();
        var compressed = entry.GetProperty("compressed_length").GetInt64();
        var size = entry.GetProperty("size").GetInt64();
        if (offset < 0 || compressed <= 0 || compressed > 2_000_000 || offset > dataEnd || compressed > dataEnd - offset || size < 0 || size > 2_000_000)
            throw new InvalidDataException("Invalid Forge 2 manifest bounds.");
        payload.Position = offset;
        var frame = new byte[(int)compressed]; payload.ReadExactly(frame);
        var result = new byte[(int)size];
        try
        {
            using var decompressor = new Decompressor();
            var count = decompressor.Unwrap(frame, result);
            if (count != size) throw new InvalidDataException("Incorrect Forge 2 manifest size.");
        }
        catch (ZstdException ex) { throw new InvalidDataException("Damaged Forge 2 manifest.", ex); }
        if (!Convert.ToHexString(SHA256.HashData(result)).Equals(entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Forge 2 manifest checksum mismatch.");
        return JsonDocument.Parse(result);
    }

    public static long PayloadEnd(Stream input, int peOffset)
    {
        var optional = (long)peOffset + 24;
        if (optional > input.Length - 2) return input.Length;
        input.Position = optional;
        Span<byte> bytes = stackalloc byte[8]; input.ReadExactly(bytes[..2]);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        if (magic is not (0x10b or 0x20b)) return input.Length;
        var security = optional + (magic == 0x20b ? 112 : 96) + 32;
        if (security > input.Length - 8) throw new InvalidDataException("Invalid PE optional header.");
        input.Position = security; input.ReadExactly(bytes);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes); var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (offset == 0 && size == 0) return input.Length;
        if (offset < security + 8 || size < 8 || offset > input.Length || size > input.Length - offset) throw new InvalidDataException("Invalid Authenticode table bounds.");
        // Signing may pad the original image to an eight-byte boundary.
        for (var padding = 0; padding < 8 && offset >= 72 + padding; padding++)
        {
            input.Position = offset - padding - 8; input.ReadExactly(bytes);
            if (bytes.SequenceEqual("FRGTRL2B"u8) || bytes.SequenceEqual("\0\0\0FORGE"u8)) return offset - padding;
        }
        return offset;
    }
}
