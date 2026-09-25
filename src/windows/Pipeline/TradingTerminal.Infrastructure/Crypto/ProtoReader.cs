using System.Text;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Just enough of the protocol-buffers wire format to read MEXC's public socket.
///
/// <para>MEXC refuses JSON subscriptions on its public socket ("Blocked!") and its current endpoint
/// sends protocol buffers only. The messages involved are five small, flat ones whose fields are all
/// strings, integers and nested messages, so a forty-line reader beats a code-generated dependency on
/// <c>Google.Protobuf</c> plus a vendored <c>.proto</c> that would need tracking.</para>
///
/// <para>Unknown fields are skipped by wire type, which is what makes the reader tolerant of MEXC
/// adding fields — the format is designed for exactly that.</para>
/// </summary>
internal ref struct ProtoReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    /// <summary>Wire type 0 — varint.</summary>
    public const int Varint = 0;

    /// <summary>Wire type 2 — length-delimited (strings, bytes, nested messages).</summary>
    public const int Len = 2;

    /// <summary>Advances to the next field. False at the end of the message.</summary>
    public bool Next(out int field, out int wireType)
    {
        if (_pos >= _data.Length)
        {
            field = 0;
            wireType = 0;
            return false;
        }

        var key = ReadVarint();
        field = (int)(key >> 3);
        wireType = (int)(key & 7);
        return true;
    }

    public ulong ReadVarint()
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (_pos >= _data.Length) throw new FormatException("Truncated varint.");
            var b = _data[_pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
        }

        throw new FormatException("Varint longer than ten bytes.");
    }

    /// <summary>A length-delimited field's payload.</summary>
    public ReadOnlySpan<byte> ReadBytes()
    {
        var length = (int)ReadVarint();
        if (length < 0 || _pos + length > _data.Length) throw new FormatException("Truncated field.");
        var slice = _data.Slice(_pos, length);
        _pos += length;
        return slice;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadBytes());

    /// <summary>Skips the current field's value.</summary>
    public void Skip(int wireType)
    {
        switch (wireType)
        {
            case Varint: ReadVarint(); break;
            case 1: _pos += 8; break;
            case Len: ReadBytes(); break;
            case 5: _pos += 4; break;
            default: throw new FormatException($"Unsupported wire type {wireType}.");
        }

        if (_pos > _data.Length) throw new FormatException("Truncated field.");
    }
}
