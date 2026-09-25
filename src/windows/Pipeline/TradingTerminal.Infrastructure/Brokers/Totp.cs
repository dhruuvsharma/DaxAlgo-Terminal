using System.Security.Cryptography;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// Time-based one-time passwords (RFC 6238, HMAC-SHA1, 30-second steps) — the six digits an
/// authenticator app shows.
///
/// <para>Angel One and 5paisa sign in with one. A user can type the current code each morning, or store
/// the authenticator's setup key (the base32 text behind its QR code) and let the terminal compute it;
/// the key is stored DPAPI-encrypted like every other secret. It is exactly as sensitive as the phone
/// app holding it, which is worth saying at the point of pasting it.</para>
/// </summary>
public static class Totp
{
    /// <summary>The <paramref name="digits"/>-digit code for <paramref name="at"/>, from the authenticator setup
    /// key <paramref name="base32Secret"/> (spaces, hyphens and case ignored), in steps of
    /// <paramref name="stepSeconds"/>.</summary>
    public static string Compute(string base32Secret, DateTimeOffset at, int digits = 6, int stepSeconds = 30)
    {
        var key = Base32(base32Secret);
        var counter = at.ToUnixTimeSeconds() / stepSeconds;

        Span<byte> message = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            message[i] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        var hash = HMACSHA1.HashData(key, message);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>True when <paramref name="text"/> is already a code rather than a setup key.</summary>
    public static bool IsCode(string text) =>
        text.Length is 6 or 8 && text.All(char.IsAsciiDigit);

    /// <summary>RFC 4648 base32, forgiving of the spacing and case authenticator apps display.</summary>
    public static byte[] Base32(string text)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = new string(text.Where(c => !char.IsWhiteSpace(c) && c != '-' && c != '=').ToArray()).ToUpperInvariant();

        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0) throw new FormatException($"'{c}' is not a base32 character — is this the authenticator's setup key?");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        return [.. bytes];
    }
}
