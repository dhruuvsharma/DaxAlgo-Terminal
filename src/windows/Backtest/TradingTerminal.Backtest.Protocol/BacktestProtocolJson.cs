using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Backtesting;

namespace TradingTerminal.Backtest.Protocol;

/// <summary>Canonical JSON settings shared by request files, NDJSON progress, and result artifacts.</summary>
public static class BacktestProtocolJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions(writeIndented: false);
    public static JsonSerializerOptions IndentedOptions { get; } = CreateOptions(writeIndented: true);

    public static string Serialize<T>(T value, bool writeIndented = false) =>
        JsonSerializer.Serialize(value, writeIndented ? IndentedOptions : Options);

    public static byte[] SerializeToUtf8Bytes<T>(T value, bool writeIndented = false) =>
        JsonSerializer.SerializeToUtf8Bytes(value, writeIndented ? IndentedOptions : Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"The JSON payload did not contain a {typeof(T).Name}.");

    public static T Deserialize<T>(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"The JSON payload did not contain a {typeof(T).Name}.");

    private static JsonSerializerOptions CreateOptions(bool writeIndented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            PropertyNameCaseInsensitive = false,
            WriteIndented = writeIndented,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.SnakeCaseLower,
            allowIntegerValues: false));
        return options;
    }
}

public static class BacktestProtocolHash
{
    public const string UnknownSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string ComputeSha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string ComputeSha256(string text) =>
        ComputeSha256(Encoding.UTF8.GetBytes(text));

    public static async Task<string> ComputeFileSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    public static string ComputeParametersSha256(StrategyParameters parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in parameters.Values.OrderBy(x => x.Key, StringComparer.Ordinal))
                writer.WriteNumber(key, value);
            writer.WriteEndObject();
        }

        return ComputeSha256(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    public static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
