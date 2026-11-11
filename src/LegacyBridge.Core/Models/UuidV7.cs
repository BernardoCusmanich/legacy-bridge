using System.Buffers.Binary;
using System.Security.Cryptography;

namespace LegacyBridge.Core.Models;

/// <summary>
/// Gerador de UUID v7 (RFC 9562): 48 bits de Unix ms + versão + 74 bits aleatórios.
/// O .NET 8 ainda não tem <c>Guid.CreateVersion7</c> (chegou no .NET 9).
/// </summary>
public static class UuidV7
{
    public static string NewId() => NewId(DateTimeOffset.UtcNow);

    public static string NewId(DateTimeOffset timestamp)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        var ms = timestamp.ToUnixTimeMilliseconds();
        Span<byte> msBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msBytes, ms);
        msBytes[2..].CopyTo(bytes); // 6 bytes menos significativos

        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // versão 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variante RFC 4122

        return new Guid(bytes, bigEndian: true).ToString("D");
    }
}
