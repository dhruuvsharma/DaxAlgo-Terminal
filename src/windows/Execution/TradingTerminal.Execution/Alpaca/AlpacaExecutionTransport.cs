using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Alpaca;

public sealed record AlpacaAccountSnapshot(
    string AccountId,
    string Status,
    string Currency,
    ScaledMoney Cash,
    ScaledMoney BuyingPower,
    bool TradingBlocked,
    bool AccountBlocked,
    bool TradeSuspendedByUser = false)
{
    public bool IsExecutionAuthorized =>
        !string.IsNullOrWhiteSpace(AccountId) &&
        string.Equals(Status, "ACTIVE", StringComparison.OrdinalIgnoreCase) &&
        !TradingBlocked &&
        !AccountBlocked &&
        !TradeSuspendedByUser;
}

public sealed record AlpacaAssetSnapshot(
    string Symbol,
    string AssetClass,
    bool Tradable,
    bool Fractionable,
    ScaledQuantity? MinimumOrderSize,
    ScaledQuantity? MinimumTradeIncrement,
    ScaledPrice? PriceIncrement);

public sealed record AlpacaLatestTrade(ScaledPrice Price, DateTime TimestampUtc);

public sealed record AlpacaOrderSnapshot(
    string OrderId,
    string ClientOrderId,
    string Symbol,
    string AssetClass,
    string Side,
    string OrderType,
    string TimeInForce,
    string Status,
    ScaledQuantity Quantity,
    ScaledQuantity FilledQuantity,
    ScaledPrice? FilledAveragePrice,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice,
    DateTime UpdatedAtUtc,
    string? FailureReason = null);

public sealed record AlpacaPositionSnapshot(
    string Symbol,
    string AssetClass,
    ScaledQuantity Quantity,
    DateTime ObservedAtUtc);

public sealed record AlpacaSubmitRequest(
    string Symbol,
    string ClientOrderId,
    string Side,
    string OrderType,
    string TimeInForce,
    ScaledQuantity Quantity,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice);

public sealed record AlpacaReplaceRequest(
    ScaledQuantity Quantity,
    string TimeInForce,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice);

public enum AlpacaOrderStatusFilter : byte
{
    Open = 0,
    Closed = 1,
    All = 2,
}

public sealed class AlpacaApiException : Exception
{
    public AlpacaApiException(HttpStatusCode statusCode, string? code, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Code { get; }
}

/// <summary>Injectable Trading API transport; tests implement this entirely in-process.</summary>
public interface IAlpacaExecutionTransport : IAsyncDisposable
{
    AlpacaExecutionEndpoint Endpoint { get; }

    bool IsConnected { get; }

    Task ConnectAsync(string keyId, string secretKey, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default);

    Task<AlpacaAssetSnapshot> GetAssetAsync(string symbol, CancellationToken cancellationToken = default);

    Task<AlpacaLatestTrade?> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken = default);

    Task<AlpacaOrderSnapshot> SubmitOrderAsync(AlpacaSubmitRequest request, CancellationToken cancellationToken = default);

    Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default);

    Task<AlpacaOrderSnapshot> ReplaceOrderAsync(
        string orderId,
        AlpacaReplaceRequest request,
        CancellationToken cancellationToken = default);

    Task<AlpacaOrderSnapshot?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default);

    Task<AlpacaOrderSnapshot?> GetOrderByClientIdAsync(string clientOrderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOrdersAsync(
        AlpacaOrderStatusFilter status,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reusable raw HttpClient transport constrained to gated Alpaca paper/live and data endpoints.</summary>
public sealed class AlpacaHttpExecutionTransport : IAlpacaExecutionTransport, IDisposable
{
    private const int MaximumJsonResponseBytes = 8 * 1024 * 1024;
    private const int MaximumErrorResponseBytes = 64 * 1024;
    private readonly object _gate = new();
    private readonly HttpClient _client;
    private string? _keyId;
    private string? _secretKey;
    private bool _disposed;

    public AlpacaHttpExecutionTransport(
        AlpacaExecutionEndpoint endpoint,
        TimeSpan timeout,
        HttpMessageHandler? handler = null)
    {
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (!Endpoint.IsAuthorized)
            throw new InvalidOperationException("The HTTP transport requires an exact endpoint produced by the Alpaca authorization gate.");
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        handler ??= new HttpClientHandler { AllowAutoRedirect = false };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }

    public AlpacaExecutionEndpoint Endpoint { get; }

    public bool IsConnected
    {
        get
        {
            lock (_gate)
                return !_disposed && _keyId is not null && _secretKey is not null;
        }
    }

    public Task ConnectAsync(string keyId, string secretKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Both Alpaca execution credentials are required.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _keyId = keyId;
            _secretKey = secretKey;
        }
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _keyId = null;
            _secretKey = null;
        }
        return Task.CompletedTask;
    }

    public async Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(TradingUri("v2/account"), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new AlpacaAccountSnapshot(
            RequiredString(root, "id"),
            RequiredString(root, "status"),
            RequiredString(root, "currency"),
            RequiredMoney(root, "cash"),
            RequiredMoney(root, "buying_power"),
            RequiredBoolean(root, "trading_blocked"),
            RequiredBoolean(root, "account_blocked"),
            RequiredBoolean(root, "trade_suspended_by_user"));
    }

    public async Task<AlpacaAssetSnapshot> GetAssetAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(symbol, nameof(symbol));
        using var document = await GetJsonAsync(TradingUri($"v2/assets/{Uri.EscapeDataString(symbol)}"), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        return new AlpacaAssetSnapshot(
            RequiredString(root, "symbol"),
            RequiredString(root, "class"),
            RequiredBoolean(root, "tradable"),
            RequiredBoolean(root, "fractionable"),
            OptionalQuantity(root, "min_order_size"),
            OptionalQuantity(root, "min_trade_increment"),
            OptionalPrice(root, "price_increment"));
    }

    public async Task<AlpacaLatestTrade?> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(symbol, nameof(symbol));
        try
        {
            using var document = await GetJsonAsync(
                DataUri($"v2/stocks/{Uri.EscapeDataString(symbol)}/trades/latest"),
                cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("trade", out var trade) || trade.ValueKind != JsonValueKind.Object)
                return null;
            return new AlpacaLatestTrade(RequiredPrice(trade, "p"), RequiredUtc(trade, "t"));
        }
        catch (AlpacaApiException)
        {
            return null;
        }
    }

    public async Task<AlpacaOrderSnapshot> SubmitOrderAsync(
        AlpacaSubmitRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = SerializeOrderRequest(request);
        using var document = await SendJsonAsync(HttpMethod.Post, TradingUri("v2/orders"), payload, cancellationToken).ConfigureAwait(false);
        return ParseOrder(document.RootElement);
    }

    public async Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(orderId, nameof(orderId));
        using var response = await SendAsync(
            HttpMethod.Delete,
            TradingUri($"v2/orders/{Uri.EscapeDataString(orderId)}"),
            content: null,
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlpacaOrderSnapshot> ReplaceOrderAsync(
        string orderId,
        AlpacaReplaceRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(orderId, nameof(orderId));
        ArgumentNullException.ThrowIfNull(request);
        var payload = SerializeReplaceRequest(request);
        using var document = await SendJsonAsync(
            HttpMethod.Patch,
            TradingUri($"v2/orders/{Uri.EscapeDataString(orderId)}"),
            payload,
            cancellationToken).ConfigureAwait(false);
        return ParseOrder(document.RootElement);
    }

    public async Task<AlpacaOrderSnapshot?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(orderId, nameof(orderId));
        return await GetOptionalOrderAsync(
            TradingUri($"v2/orders/{Uri.EscapeDataString(orderId)}"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AlpacaOrderSnapshot?> GetOrderByClientIdAsync(
        string clientOrderId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(clientOrderId, nameof(clientOrderId));
        return await GetOptionalOrderAsync(
            TradingUri($"v2/orders:by_client_order_id?client_order_id={Uri.EscapeDataString(clientOrderId)}"),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOrdersAsync(
        AlpacaOrderStatusFilter status,
        CancellationToken cancellationToken = default)
    {
        var value = status switch
        {
            AlpacaOrderStatusFilter.Open => "open",
            AlpacaOrderStatusFilter.Closed => "closed",
            AlpacaOrderStatusFilter.All => "all",
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        using var document = await GetJsonAsync(
            TradingUri($"v2/orders?status={value}&limit=500&direction=desc&nested=false"),
            cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Alpaca orders response was not an array.");
        var result = new List<AlpacaOrderSnapshot>();
        foreach (var element in document.RootElement.EnumerateArray())
            result.Add(ParseOrder(element));
        return result.AsReadOnly();
    }

    public async Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetJsonAsync(TradingUri("v2/positions"), cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("The Alpaca positions response was not an array.");
        var now = DateTime.UtcNow;
        var result = new List<AlpacaPositionSnapshot>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            result.Add(new AlpacaPositionSnapshot(
                RequiredString(element, "symbol"),
                RequiredString(element, "asset_class"),
                RequiredQuantity(element, "qty"),
                now));
        }
        return result.AsReadOnly();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _keyId = null;
            _secretKey = null;
        }
        _client.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private Uri TradingUri(string relative) => new(Endpoint.TradingBaseUri, relative);

    private Uri DataUri(string relative) => new(Endpoint.MarketDataBaseUri, relative);

    private async Task<AlpacaOrderSnapshot?> GetOptionalOrderAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
        return ParseOrder(document.RootElement);
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, uri, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        Uri uri,
        string payload,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await SendAsync(method, uri, content, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return await ReadDocumentAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri uri,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        string keyId;
        string secretKey;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            keyId = _keyId ?? throw new InvalidOperationException("The Alpaca transport is disconnected.");
            secretKey = _secretKey ?? throw new InvalidOperationException("The Alpaca transport is disconnected.");
        }

        if (!IsApprovedUri(uri))
            throw new InvalidOperationException("An Alpaca request attempted to leave the centrally gated trading/data hosts.");
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Add("APCA-API-KEY-ID", keyId);
        request.Headers.Add("APCA-API-SECRET-KEY", secretKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is not { } finalUri || !IsApprovedUri(finalUri))
        {
            response.Dispose();
            throw new InvalidOperationException("The Alpaca HTTP response escaped the centrally gated trading/data hosts.");
        }
        return response;
    }

    private bool IsApprovedUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) &&
        (string.Equals(uri.Host, Endpoint.TradingBaseUri.Host, StringComparison.Ordinal) ||
         string.Equals(uri.Host, Endpoint.MarketDataBaseUri.Host, StringComparison.Ordinal)) &&
        uri.IsDefaultPort &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = response.Content is null
            ? string.Empty
            : await ReadBoundedTextAsync(response.Content, MaximumErrorResponseBytes, cancellationToken).ConfigureAwait(false);
        string? code = null;
        var message = $"Alpaca returned HTTP {(int)response.StatusCode}.";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            code = OptionalText(root, "code");
            message = OptionalText(root, "message") ?? message;
        }
        catch (JsonException)
        {
            // Keep the bounded status-only diagnostic; response bodies can contain credentials-adjacent detail.
        }
        throw new AlpacaApiException(response.StatusCode, code, message);
    }

    private static async Task<JsonDocument> ReadDocumentAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[81_920];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (buffer.Length + read > MaximumJsonResponseBytes)
                throw new InvalidDataException("The Alpaca JSON response exceeded the bounded response limit.");
            buffer.Write(block, 0, read);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[8_192];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            var remaining = maximumBytes - (int)buffer.Length;
            if (remaining <= 0)
                break;
            buffer.Write(block, 0, Math.Min(read, remaining));
            if (read > remaining)
                break;
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static AlpacaOrderSnapshot ParseOrder(JsonElement root) => new(
        RequiredString(root, "id"),
        RequiredString(root, "client_order_id"),
        RequiredString(root, "symbol"),
        RequiredString(root, "asset_class"),
        RequiredString(root, "side"),
        RequiredString(root, "type"),
        RequiredString(root, "time_in_force"),
        RequiredString(root, "status"),
        RequiredQuantity(root, "qty"),
        RequiredQuantity(root, "filled_qty"),
        OptionalPrice(root, "filled_avg_price"),
        OptionalPrice(root, "limit_price"),
        OptionalPrice(root, "stop_price"),
        RequiredUtc(root, "updated_at"),
        OptionalText(root, "reject_reason"));

    private static string SerializeOrderRequest(AlpacaSubmitRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("symbol", request.Symbol);
            writer.WriteString("qty", AlpacaDecimal.Format(request.Quantity.Coefficient, request.Quantity.Scale));
            writer.WriteString("side", request.Side);
            writer.WriteString("type", request.OrderType);
            writer.WriteString("time_in_force", request.TimeInForce);
            writer.WriteString("client_order_id", request.ClientOrderId);
            if (request.LimitPrice is { } limit)
                writer.WriteString("limit_price", AlpacaDecimal.Format(limit.Coefficient, limit.Scale));
            if (request.StopPrice is { } stop)
                writer.WriteString("stop_price", AlpacaDecimal.Format(stop.Coefficient, stop.Scale));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string SerializeReplaceRequest(AlpacaReplaceRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("qty", AlpacaDecimal.Format(request.Quantity.Coefficient, request.Quantity.Scale));
            writer.WriteString("time_in_force", request.TimeInForce);
            if (request.LimitPrice is { } limit)
                writer.WriteString("limit_price", AlpacaDecimal.Format(limit.Coefficient, limit.Scale));
            if (request.StopPrice is { } stop)
                writer.WriteString("stop_price", AlpacaDecimal.Format(stop.Coefficient, stop.Scale));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"The Alpaca response omitted '{name}'.");

    private static string? OptionalText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static bool RequiredBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : throw new InvalidDataException($"The Alpaca response omitted boolean '{name}'.");

    private static DateTime RequiredUtc(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            throw new InvalidDataException($"The Alpaca response contained invalid UTC '{name}'.");
        return parsed.UtcDateTime;
    }

    private static ScaledQuantity RequiredQuantity(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (!AlpacaDecimal.TryParse(value, out var coefficient, out var scale))
            throw new InvalidDataException($"The Alpaca response contained unrepresentable quantity '{name}'.");
        return new ScaledQuantity(coefficient, scale);
    }

    private static ScaledQuantity? OptionalQuantity(JsonElement element, string name)
    {
        var value = OptionalText(element, name);
        if (value is null)
            return null;
        if (!AlpacaDecimal.TryParse(value, out var coefficient, out var scale))
            throw new InvalidDataException($"The Alpaca response contained unrepresentable quantity '{name}'.");
        return new ScaledQuantity(coefficient, scale);
    }

    private static ScaledPrice RequiredPrice(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (!AlpacaDecimal.TryParse(value, out var coefficient, out var scale))
            throw new InvalidDataException($"The Alpaca response contained unrepresentable price '{name}'.");
        return new ScaledPrice(coefficient, scale);
    }

    private static ScaledPrice? OptionalPrice(JsonElement element, string name)
    {
        var value = OptionalText(element, name);
        if (value is null)
            return null;
        if (!AlpacaDecimal.TryParse(value, out var coefficient, out var scale))
            throw new InvalidDataException($"The Alpaca response contained unrepresentable price '{name}'.");
        return new ScaledPrice(coefficient, scale);
    }

    private static ScaledMoney RequiredMoney(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        if (!AlpacaDecimal.TryParse(value, out var coefficient, out var scale))
            throw new InvalidDataException($"The Alpaca response contained unrepresentable money '{name}'.");
        return new ScaledMoney(coefficient, scale);
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A bounded non-empty Alpaca identifier is required.", parameterName);
    }
}

internal static class AlpacaDecimal
{
    internal static bool TryParse(string value, out long coefficient, out byte scale)
    {
        coefficient = 0;
        scale = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var span = value.AsSpan().Trim();
        var negative = false;
        var index = 0;
        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            index++;
        }
        if (index == span.Length)
            return false;

        Int128 magnitude = 0;
        var seenDigit = false;
        var seenDecimal = false;
        var decimals = 0;
        for (; index < span.Length; index++)
        {
            var character = span[index];
            if (character == '.' && !seenDecimal)
            {
                seenDecimal = true;
                continue;
            }
            if (character is < '0' or > '9')
                return false;
            seenDigit = true;
            if (seenDecimal)
                decimals++;
            if (decimals > ScaledValueMath.MaximumScale || magnitude > (Int128.MaxValue - 9) / 10)
                return false;
            magnitude = magnitude * 10 + (character - '0');
        }
        if (!seenDigit)
            return false;
        var signed = negative ? -magnitude : magnitude;
        return ScaledValueMath.TryNarrow(signed, decimals, out coefficient, out scale);
    }

    internal static string Format(long coefficient, byte scale)
    {
        if (scale > ScaledValueMath.MaximumScale)
            throw new ArgumentOutOfRangeException(nameof(scale));
        var negative = coefficient < 0;
        var magnitude = negative ? -(Int128)coefficient : coefficient;
        var digits = magnitude.ToString(CultureInfo.InvariantCulture);
        if (scale == 0)
            return negative ? "-" + digits : digits;
        if (digits.Length <= scale)
            digits = new string('0', scale - digits.Length + 1) + digits;
        var split = digits.Length - scale;
        var result = string.Concat(digits.AsSpan(0, split), ".", digits.AsSpan(split));
        return negative ? "-" + result : result;
    }
}
