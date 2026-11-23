using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Airlift.Core;
using Xunit;
using ZstdSharp;

namespace Airlift.Tests;

public sealed class Forge2InspectorTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public void Reads_v2_and_signed_v2_with_padding(int padding)
    {
        var path = Path.Combine(Path.GetTempPath(), "airlift-forge2-" + Guid.NewGuid() + ".exe");
        try
        {
            File.WriteAllBytes(path, Fixture(padding));
            var info = ForgeInspector.Inspect(path);
            Assert.Equal("com.forge.test.reader", info.Id);
            Assert.Equal("2.0.0", info.Version);
            Assert.Equal("x64", info.Architecture);
            Assert.True(info.SupportsSilentHandoff);
            Assert.True(info.NeedsElevation);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Rejects_tampered_v2_payload()
    {
        var path = Path.Combine(Path.GetTempPath(), "airlift-forge2-" + Guid.NewGuid() + ".exe");
        try
        {
            var data = Fixture(-1); data[520] ^= 1; File.WriteAllBytes(path, data);
            Assert.Throws<InvalidDataException>(() => ForgeInspector.Inspect(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Rejects_oversized_manifest_before_decompression()
    {
        var path = Path.Combine(Path.GetTempPath(), "airlift-forge2-" + Guid.NewGuid() + ".exe");
        try
        {
            File.WriteAllBytes(path, Fixture(-1, 3_000_000));
            Assert.Throws<InvalidDataException>(() => ForgeInspector.Inspect(path));
        }
        finally { File.Delete(path); }
    }
    private static byte[] Fixture(int padding, int? declaredSize = null)
    {
        var manifest = Encoding.UTF8.GetBytes("""
            {"manifest":{"app":{"id":"com.forge.test.reader","version":"2.0.0"},"install":{"default_dir":"${LOCALAPPDATA}/Test"},"services":[{"name":"test"}],"capabilities":["silent-update-handoff-v1"]},"files":[]}
            """);
        using var compressor = new Compressor(); var frame = compressor.Wrap(manifest).ToArray();
        var index = JsonSerializer.SerializeToUtf8Bytes(new { format_version = 2, entries = new[] { new { path = "manifest.json", size = declaredSize ?? manifest.Length, sha256 = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant(), offset = 0, compressed_length = frame.Length } } });
        using var payload = new MemoryStream(); payload.Write(frame); payload.Write(index);
        Span<byte> count = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(count, index.Length); payload.Write(count);
        var bytes = payload.ToArray(); var stub = new byte[512]; stub[0] = (byte)'M'; stub[1] = (byte)'Z'; BinaryPrimitives.WriteInt32LittleEndian(stub.AsSpan(60), 64);
        "PE\0\0"u8.CopyTo(stub.AsSpan(64)); BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(68), 0x8664); BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(88), 0x20b);
        var trailer = new byte[72]; "FRGTRL2A"u8.CopyTo(trailer); "FRGTRL2B"u8.CopyTo(trailer.AsSpan(64)); BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(8), 2);
        BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(16), stub.Length); BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(24), bytes.Length); SHA256.HashData(bytes).CopyTo(trailer, 32);
        if (padding >= 0) { BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(232), (uint)(stub.Length + bytes.Length + trailer.Length + padding)); BinaryPrimitives.WriteUInt32LittleEndian(stub.AsSpan(236), 16); }
        return [.. stub, .. bytes, .. trailer, .. padding >= 0 ? new byte[padding + 16] : []];
    }
}
