using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace Airlift.Core;

public sealed record ForgeInfo(string Id, string Version, string Architecture, bool NeedsElevation, string DefaultDirectory);
public static class ForgeInspector
{
    public static ForgeInfo Inspect(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length < 136) throw new InvalidDataException("Not a Forge installer.");
        Span<byte> peHeader = stackalloc byte[64]; input.ReadExactly(peHeader);
        if (peHeader[0] != 'M' || peHeader[1] != 'Z') throw new InvalidDataException("Installer is not a Windows executable.");
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(peHeader[60..]);
        if (peOffset < 64 || peOffset > input.Length - 26) throw new InvalidDataException("Invalid PE header offset.");
        input.Position = peOffset; Span<byte> pe = stackalloc byte[6]; input.ReadExactly(pe);
        if (!pe[..4].SequenceEqual("PE\0\0"u8)) throw new InvalidDataException("Invalid PE signature.");
        var arch = BinaryPrimitives.ReadUInt16LittleEndian(pe[4..]) switch { 0x8664 => "x64", 0xaa64 => "arm64", 0x14c => "x86", _ => "unknown" };
        input.Seek(-72, SeekOrigin.End); Span<byte> trailer = stackalloc byte[72]; input.ReadExactly(trailer);
        if (!trailer[..8].SequenceEqual("FORGE\0\0\0"u8) || !trailer[64..].SequenceEqual("\0\0\0FORGE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]) != 1)
            throw new InvalidDataException("Missing or unsupported Forge trailer.");
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(trailer[16..]); var length = BinaryPrimitives.ReadUInt64LittleEndian(trailer[24..]);
        if (offset > (ulong)(input.Length - 72) || length != (ulong)(input.Length - 72) - offset || length == 0) throw new InvalidDataException("Invalid Forge payload bounds.");
        using var payload = new SliceStream(input, (long)offset, (long)length);
        if (!SHA256.HashData(payload).AsSpan().SequenceEqual(trailer.Slice(32, 32))) throw new InvalidDataException("Forge payload checksum mismatch.");
        payload.Position = 0; using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        var manifest = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Forge manifest missing.");
        if (manifest.Length > 2_000_000) throw new InvalidDataException("Forge manifest is too large.");
        using var stream = manifest.Open(); using var json = JsonDocument.Parse(stream); var root = json.RootElement;
        var app = root.GetProperty("app");
        var directory = root.GetProperty("install").GetProperty("default_dir").GetString() ?? "";
        var elevated = directory.Contains("${PROGRAMFILES}", StringComparison.OrdinalIgnoreCase) ||
            (root.TryGetProperty("registry", out var registry) && registry.ValueKind == JsonValueKind.Array && registry.EnumerateArray().Any(r => r.GetProperty("hive").GetString() == "HKLM")) ||
            (root.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Array && env.EnumerateArray().Any(e => e.GetProperty("scope").GetString() == "machine"));
        return new(app.GetProperty("id").GetString()!, app.GetProperty("version").GetString()!, arch, elevated, directory);
    }
    public static ForgeInfo Verify(string path, PackagePlan plan)
    {
        var info = Inspect(path);
        if (info.Id != plan.App.Id || SemVersion.Compare(info.Version, plan.Release.Version) != 0 || info.Architecture != plan.Arch)
            throw new InvalidDataException("The downloaded installer does not match the expected app, release version, or architecture.");
        return info;
    }
    private sealed class SliceStream(Stream inner, long offset, long length) : Stream
    {
        private long _position;
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int start, int count) => Read(buffer.AsSpan(start, count));
        public override int Read(Span<byte> buffer) { inner.Position = offset + _position; var n = inner.Read(buffer[..(int)Math.Min(buffer.Length, length - _position)]); _position += n; return n; }
        public override long Seek(long target, SeekOrigin origin) { var next = origin switch { SeekOrigin.Begin => target, SeekOrigin.Current => _position + target, SeekOrigin.End => length + target, _ => throw new ArgumentException() }; if (next < 0 || next > length) throw new IOException("Seek outside payload."); return _position = next; }
        public override void Flush() { } public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
