using System.Globalization;
using System.Text;

namespace DaxAlgo.Strategy.Bundle;

/// <summary>
/// Minimal JSON string/number encoding with a frozen escape algorithm. It deliberately avoids the
/// runtime's evolving JavaScript encoder block lists so signed bytes remain stable across runtimes.
/// </summary>
internal static class CanonicalJson
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static byte[] ToUtf8(StringBuilder json) => StrictUtf8.GetBytes(json.ToString());

    public static void AppendString(StringBuilder json, string value)
    {
        json.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                case '\b': json.Append("\\b"); break;
                case '\t': json.Append("\\t"); break;
                case '\n': json.Append("\\n"); break;
                case '\f': json.Append("\\f"); break;
                case '\r': json.Append("\\r"); break;
                default:
                    if (character < 0x20)
                    {
                        json.Append("\\u");
                        json.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(character))
                    {
                        if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                            throw new ArgumentException("JSON strings must contain valid Unicode scalar values.", nameof(value));
                        json.Append(character);
                        json.Append(value[++index]);
                    }
                    else if (char.IsLowSurrogate(character))
                    {
                        throw new ArgumentException("JSON strings must contain valid Unicode scalar values.", nameof(value));
                    }
                    else
                    {
                        json.Append(character);
                    }
                    break;
            }
        }
        json.Append('"');
    }
}
