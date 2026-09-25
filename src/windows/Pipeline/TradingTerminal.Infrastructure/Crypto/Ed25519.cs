using System.Numerics;
using System.Security.Cryptography;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Ed25519 signing (RFC 8032, pure), for the one broker that signs requests with it — Robinhood's crypto
/// trading API.
///
/// <para><b>Why hand-written.</b> .NET has no Ed25519, and a cryptography package for one broker's
/// request header is a dependency every edition would carry. This is the RFC's own reference algorithm
/// over <see cref="BigInteger"/>: slow by cryptographic standards (a few milliseconds a signature) and
/// not constant-time, which is acceptable for signing a handful of polling requests with a key that
/// never leaves this machine, and would not be for a server. The tests hold it to the RFC 8032 test
/// vectors.</para>
/// </summary>
internal static class Ed25519
{
    private static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    private static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    private static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    private static readonly Point G = BasePoint();

    /// <summary>Extended homogeneous coordinates: x = X/Z, y = Y/Z, xy = T/Z.</summary>
    private readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    /// <summary>The 32-byte public key for a 32-byte secret seed.</summary>
    public static byte[] PublicKey(ReadOnlySpan<byte> seed)
    {
        var (a, _) = Expand(seed);
        return Compress(Multiply(a, G));
    }

    /// <summary>The 64-byte signature of <paramref name="message"/> under the 32-byte secret seed.</summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> message)
    {
        var (a, prefix) = Expand(seed);
        var publicKey = Compress(Multiply(a, G));

        var r = HashModL(prefix, message);
        var rEncoded = Compress(Multiply(r, G));
        var h = HashModL(rEncoded, publicKey, message);
        var s = Mod(r + h * a, L);

        var signature = new byte[64];
        rEncoded.CopyTo(signature, 0);
        ToLittleEndian(s).CopyTo(signature, 32);
        return signature;
    }

    /// <summary>The seed from a key as a broker hands it out: 32 bytes, or 64 (seed then public key, the
    /// libsodium layout). Anything else is not an Ed25519 private key.</summary>
    public static byte[] SeedFrom(byte[] key) => key.Length switch
    {
        32 => key,
        64 => key[..32],
        _ => throw new FormatException($"An Ed25519 private key is 32 or 64 bytes; this one is {key.Length}."),
    };

    private static (BigInteger A, byte[] Prefix) Expand(ReadOnlySpan<byte> seed)
    {
        if (seed.Length != 32) throw new ArgumentException("An Ed25519 seed is 32 bytes.", nameof(seed));
        var h = SHA512.HashData(seed);
        var a = FromLittleEndian(h.AsSpan(0, 32));
        a &= (BigInteger.One << 254) - 8;
        a |= BigInteger.One << 254;
        return (a, h[32..]);
    }

    private static BigInteger HashModL(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var buffer = new byte[first.Length + second.Length];
        first.CopyTo(buffer);
        second.CopyTo(buffer.AsSpan(first.Length));
        return HashModL(buffer);
    }

    private static BigInteger HashModL(byte[] data) => Mod(FromLittleEndian(SHA512.HashData(data)), L);

    private static BigInteger HashModL(byte[] first, byte[] second, ReadOnlySpan<byte> third)
    {
        var buffer = new byte[first.Length + second.Length + third.Length];
        first.CopyTo(buffer, 0);
        second.CopyTo(buffer, first.Length);
        third.CopyTo(buffer.AsSpan(first.Length + second.Length));
        return HashModL(buffer);
    }

    private static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a;
        var f = d - c;
        var g = d + c;
        var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Point Multiply(BigInteger scalar, Point point)
    {
        var result = new Point(0, 1, 1, 0);
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, point);
            point = Add(point, point);
            scalar >>= 1;
        }

        return result;
    }

    private static byte[] Compress(Point p)
    {
        var zInverse = Inverse(p.Z);
        var x = Mod(p.X * zInverse);
        var y = Mod(p.Y * zInverse);
        return ToLittleEndian(y | ((x & 1) << 255));
    }

    private static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, sign: 0);
        return new Point(x, y, 1, Mod(x * y));
    }

    private static BigInteger RecoverX(BigInteger y, int sign)
    {
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2.IsZero) return 0;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (!Mod(x * x - x2).IsZero) x = Mod(x * SqrtMinusOne);
        if ((int)(x & 1) != sign) x = P - x;
        return x;
    }

    private static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    private static BigInteger Mod(BigInteger value) => Mod(value, P);

    private static BigInteger Mod(BigInteger value, BigInteger modulus)
    {
        var r = value % modulus;
        return r.Sign < 0 ? r + modulus : r;
    }

    private static BigInteger FromLittleEndian(ReadOnlySpan<byte> bytes) => new(bytes, isUnsigned: true, isBigEndian: false);

    private static byte[] ToLittleEndian(BigInteger value)
    {
        var result = new byte[32];
        value.TryWriteBytes(result, out _, isUnsigned: true, isBigEndian: false);
        return result;
    }
}
