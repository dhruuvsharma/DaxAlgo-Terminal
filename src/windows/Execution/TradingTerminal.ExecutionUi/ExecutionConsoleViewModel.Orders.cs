using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Execution;
using TradingTerminal.UI.Strategies;

namespace TradingTerminal.ExecutionUi;

/// <summary>
/// The parts of the console that send orders by hand: the new-book form's size, the order ticket for the selected
/// book, and changing or cancelling one order. A book has no instrument of its own; it trades what its strategy does.
/// </summary>
public sealed partial class ExecutionConsoleViewModel
{
    private IStrategyFactory? _strategyFactory;
    private IStrategyKernelRegistry? _kernels;
    private TradingTerminal.Blocks.Runtime.IBlocksUnitRegistry? _blocks;

    /// <summary>Book units per unit of the bound strategy's position.</summary>
    [ObservableProperty]
    private string _newBookSize = "1";

    public IReadOnlyList<string> TicketSides { get; } = ["Buy", "Sell"];

    public IReadOnlyList<string> TicketTypes { get; } = ["Market", "Limit", "Stop", "StopLimit"];

    [ObservableProperty]
    private string _ticketSide = "Buy";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TicketNeedsLimit))]
    [NotifyPropertyChangedFor(nameof(TicketNeedsStop))]
    private string _ticketType = "Market";

    [ObservableProperty]
    private string _ticketQuantity = "1";

    [ObservableProperty]
    private string _ticketLimitPrice = string.Empty;

    [ObservableProperty]
    private string _ticketStopPrice = string.Empty;

    partial void OnTicketLimitPriceChanged(string value) => RefreshTicket();

    [ObservableProperty]
    private string _ticketMessage = string.Empty;

    public bool TicketNeedsLimit => TicketType is "Limit" or "StopLimit";

    public bool TicketNeedsStop => TicketType is "Stop" or "StopLimit";

    public bool TicketNeedsPrices => TicketNeedsLimit || TicketNeedsStop;

    public bool TicketIsBuy => TicketSide != "Sell";

    /// <summary>What the ticket for the selected book trades, or an empty string when it trades nothing.</summary>
    public string TicketInstrumentLabel => SelectedBook?.TradableInstruments.FirstOrDefault() is { } instrument
        ? $"{instrument.Symbol} · book units"
        : "This book trades no instrument";

    /// <summary>The quantity in the broker's own terms and what one unit is (<c>= 2,000 EURUSD · 1 unit = 1,000 EURUSD</c>).</summary>
    [ObservableProperty]
    private string _ticketNativeHint = string.Empty;

    [ObservableProperty]
    private string _ticketNotional = "-";

    [ObservableProperty]
    private string _ticketNavShare = "-";

    [ObservableProperty]
    private string _ticketPositionAfter = "-";

    [ObservableProperty]
    private string _ticketPriceLine = "no price";

    [ObservableProperty]
    private string _ticketSubmitLabel = "Send order";

    [ObservableProperty]
    private IReadOnlyList<ExecutionTicketCheck> _ticketChecks = Array.Empty<ExecutionTicketCheck>();

    public bool HasTicketBook => SelectedBook?.TradableInstruments.Count > 0;

    partial void OnTicketSideChanged(string value)
    {
        OnPropertyChanged(nameof(TicketIsBuy));
        RefreshTicket();
    }

    partial void OnTicketTypeChanged(string value)
    {
        OnPropertyChanged(nameof(TicketNeedsPrices));
        RefreshTicket();
    }

    partial void OnTicketQuantityChanged(string value) => RefreshTicket();

    [RelayCommand]
    private void SetTicketSide(string? side) => TicketSide = side == "Sell" ? "Sell" : "Buy";

    [RelayCommand]
    private void SetTicketType(string? type)
    {
        if (type is not null && TicketTypes.Contains(type))
            TicketType = type;
    }

    [RelayCommand]
    private void StepTicketQuantity(string? step)
    {
        var delta = int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var current = long.TryParse(TicketQuantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) ? units : 0;
        TicketQuantity = Math.Max(1, current + delta).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Recomputes what the ticket says about the order before it is sent.</summary>
    private void RefreshTicket()
    {
        OnPropertyChanged(nameof(HasTicketBook));
        OnPropertyChanged(nameof(TicketInstrumentLabel));
        var book = SelectedBook;
        var instrument = book?.TradableInstruments.FirstOrDefault();
        if (book is null || instrument is null)
        {
            TicketNativeHint = string.Empty;
            TicketNotional = "-";
            TicketNavShare = "-";
            TicketPositionAfter = "-";
            TicketPriceLine = "no price";
            TicketSubmitLabel = "Select a book with an instrument";
            TicketChecks = Array.Empty<ExecutionTicketCheck>();
            return;
        }

        var unit = book.Unit;
        var hasUnits = long.TryParse(TicketQuantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) && units > 0;
        var signed = TicketIsBuy ? units : -units;
        TicketNativeHint = hasUnits ? $"= {unit.Native(units)} · {unit.UnitLabel}" : unit.UnitLabel;
        TicketPriceLine = unit.HasPrice
            ? $"{unit.PriceDisplay} · {unit.PriceAge(DateTime.UtcNow)}"
            : "no price for this instrument";
        var price = TicketNeedsLimit && TryPrice(TicketLimitPrice, out var limit) && limit is { } limitPrice
            ? ExecutionBookAccounting.ToDecimal(limitPrice.Coefficient, limitPrice.Scale)
            : unit.ReferencePrice;
        decimal? notional = hasUnits && price is > 0m ? Math.Abs(units) * price.Value * unit.ValuePerPoint : null;
        TicketNotional = notional is { } value
            ? unit.Currency.Length > 0 ? $"{value.ToString("#,##0.00", CultureInfo.InvariantCulture)} {unit.Currency}" : ExecutionFormatting.Money(value)
            : "-";
        TicketNavShare = notional is { } share && book.NetAssetValue > 0m ? $"{share / book.NetAssetValue * 100m:0.0}%" : "-";
        TicketPositionAfter = hasUnits
            ? $"{ExecutionFormatting.SignedUnits(book.PositionUnits)} → {ExecutionFormatting.SignedUnits(book.PositionUnits + signed)} units"
            : "-";
        var typeLabel = TicketType == "StopLimit" ? "Stop limit" : TicketType;
        TicketSubmitLabel = hasUnits
            ? $"{(TicketIsBuy ? "Buy" : "Sell")} {units.ToString("N0", CultureInfo.InvariantCulture)} unit{(units == 1 ? string.Empty : "s")} {instrument.Symbol} · {typeLabel}"
            : "Enter a quantity";

        var checks = new List<ExecutionTicketCheck>
        {
            book.IsIntakePaused
                ? new("Intake is paused on this book: start it to send", ExecutionTone.Negative)
                : new("Intake is on", ExecutionTone.Positive),
            book.AdmissionOpen
                ? new("Reconciled and lease held: the engine will admit it", ExecutionTone.Positive)
                : new("The engine is not admitting orders: reconcile the book first", ExecutionTone.Negative),
            unit.HasPrice
                ? new($"Price {unit.PriceDisplay}, {unit.PriceAge(DateTime.UtcNow)}", ExecutionTone.Positive)
                : TicketType == "Market"
                    ? new("No price: a market order needs one and will be refused", ExecutionTone.Negative)
                    : new("No live price: a resting order still goes", ExecutionTone.Warning),
        };
        if (book.IsLive)
            checks.Add(new("LIVE book: real money, you will be asked to confirm", ExecutionTone.Warning));
        else
            checks.Add(new($"{book.ModeLabel}: no money moves", ExecutionTone.Positive));
        TicketChecks = Array.AsReadOnly(checks.ToArray());
    }

    private void RefreshStrategyChoices()
    {
        // Every kind of strategy the catalog opens: installed plugins, authored kernels, and units written against
        // the Blocks SDK (Hyperion's and hand-written ones). Each name is the one its window attaches to books with.
        var installed = (_strategyFactory?.All ?? []).Select(item => item.DisplayName);
        var authored = (_kernels?.All ?? []).Select(item => item.Descriptor.DisplayName);
        var blocks = (_blocks?.All ?? []).Where(item => item.IsStrategy).Select(item => item.DisplayName);
        AvailableStrategies = Array.AsReadOnly(installed
            .Concat(authored)
            .Concat(blocks)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray());
        OnPropertyChanged(nameof(AvailableStrategies));
    }

    /// <summary>Sends one order by hand to the selected book, through the same guarded engine a strategy uses.</summary>
    [RelayCommand]
    private async Task SubmitTicketAsync()
    {
        TicketMessage = string.Empty;
        if (SelectedBook is not { } book)
        {
            TicketMessage = "Select a book first.";
            return;
        }

        if (book.TradableInstruments.FirstOrDefault() is not { } instrument)
        {
            TicketMessage = "This book trades no instrument. Create a book with an instrument to send orders.";
            return;
        }

        if (!long.TryParse(TicketQuantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) || units <= 0)
        {
            TicketMessage = "Quantity is a whole number of book units, 1 or more.";
            return;
        }

        ScaledPrice? limit = null;
        ScaledPrice? stop = null;
        if (TicketNeedsLimit && !TryPrice(TicketLimitPrice, out limit))
        {
            TicketMessage = "Enter a positive limit price.";
            return;
        }

        if (TicketNeedsStop && !TryPrice(TicketStopPrice, out stop))
        {
            TicketMessage = "Enter a positive stop price.";
            return;
        }

        var side = TicketSide == "Sell" ? ExecutionManualOrderSide.Sell : ExecutionManualOrderSide.Buy;
        var type = TicketType switch
        {
            "Limit" => ExecutionManualOrderType.Limit,
            "Stop" => ExecutionManualOrderType.Stop,
            "StopLimit" => ExecutionManualOrderType.StopLimit,
            _ => ExecutionManualOrderType.Market,
        };

        if (book.IsLive)
        {
            var confirmed = await _confirmation.ConfirmAsync(
                "Send a LIVE order?",
                $"{side} {units} unit(s) of {instrument.Symbol} {type} on '{book.Name}' through {book.AdapterName}. "
                + "This is a real-money order.",
                _lifetimeCancellation.Token);
            if (!confirmed)
                return;
        }

        var request = new ExecutionManualOrderRequest(
            book.Id, instrument.Instrument, instrument.Symbol, side, ScaledQuantity.FromWhole(units), type, limit, stop);
        var result = await RunCommandAsync(token => _client.SubmitManualOrderAsync(request, token));
        TicketMessage = result.Message;
    }

    /// <summary>Cancels one working order, found by the book it belongs to.</summary>
    [RelayCommand]
    private async Task CancelOrderAsync(ExecutionOrderReadModel? order)
    {
        if (order is null || !order.IsOpen)
            return;
        var book = BookEntries.Select(entry => entry.Book)
            .FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.Name, order.BookName, StringComparison.Ordinal));
        if (book is null)
            return;
        var result = await RunCommandAsync(token => _client.CancelOrderAsync(book.Id, order.ClientOrderId, token));
        TicketMessage = result.Message;
    }

    /// <summary>The working order being changed, or null. The Orders view shows its editor while set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModifying))]
    [NotifyPropertyChangedFor(nameof(ModifyHasLimit))]
    [NotifyPropertyChangedFor(nameof(ModifyHasStop))]
    private ExecutionOrderReadModel? _modifyingOrder;

    [ObservableProperty]
    private string _modifyQuantity = string.Empty;

    [ObservableProperty]
    private string _modifyLimitPrice = string.Empty;

    [ObservableProperty]
    private string _modifyStopPrice = string.Empty;

    public bool IsModifying => ModifyingOrder is not null;

    public bool ModifyHasLimit => ModifyingOrder is { LimitPrice: not "-" };

    public bool ModifyHasStop => ModifyingOrder is { StopPrice: not "-" };

    [RelayCommand]
    private void BeginModify(ExecutionOrderReadModel? order)
    {
        if (order is null || !order.IsOpen)
            return;
        ModifyingOrder = order;
        ModifyQuantity = Plain(order.Quantity);
        ModifyLimitPrice = order.LimitPrice == "-" ? string.Empty : Plain(order.LimitPrice);
        ModifyStopPrice = order.StopPrice == "-" ? string.Empty : Plain(order.StopPrice);
    }

    [RelayCommand]
    private void CancelModify() => ModifyingOrder = null;

    /// <summary>Sends the change: new total quantity, and new limit/stop for an order that has them.</summary>
    [RelayCommand]
    private async Task ApplyModifyAsync()
    {
        if (ModifyingOrder is not { } order)
            return;
        var book = BookEntries.Select(entry => entry.Book)
            .FirstOrDefault(candidate => candidate is not null && string.Equals(candidate.Name, order.BookName, StringComparison.Ordinal));
        if (book is null)
        {
            TicketMessage = "The order's book is gone.";
            return;
        }

        if (!long.TryParse(Plain(ModifyQuantity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var units) || units <= 0)
        {
            TicketMessage = "The new quantity is a whole number of book units, 1 or more.";
            return;
        }

        ScaledPrice? limit = null;
        ScaledPrice? stop = null;
        if (ModifyHasLimit && !TryPrice(Plain(ModifyLimitPrice), out limit))
        {
            TicketMessage = "Enter a positive limit price.";
            return;
        }

        if (ModifyHasStop && !TryPrice(Plain(ModifyStopPrice), out stop))
        {
            TicketMessage = "Enter a positive stop price.";
            return;
        }

        if (book.IsLive)
        {
            var confirmed = await _confirmation.ConfirmAsync(
                "Change a LIVE order?",
                $"Change {order.ClientOrderId} on '{book.Name}' to {units} unit(s)" +
                (ModifyHasLimit ? $", limit {ModifyLimitPrice}" : string.Empty) +
                (ModifyHasStop ? $", stop {ModifyStopPrice}" : string.Empty) +
                $" through {book.AdapterName}. This is a real-money order.",
                _lifetimeCancellation.Token);
            if (!confirmed)
                return;
        }

        var result = await RunCommandAsync(token => _client.ReplaceOrderAsync(book.Id, order.ClientOrderId, units, limit, stop, token));
        TicketMessage = result.Message;
        if (result.IsSuccess)
            ModifyingOrder = null;
    }

    /// <summary>A displayed number as typed input: no thousands separators, no surrounding space.</summary>
    private static string Plain(string? text) => (text ?? string.Empty).Replace(",", string.Empty, StringComparison.Ordinal).Trim();

    /// <summary>A positive decimal as the engine's exact price: the digits as written, no rounding.</summary>
    internal static bool TryPrice(string? text, out ScaledPrice? price)
    {
        price = null;
        if (!decimal.TryParse(text?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0)
            return false;
        value /= 1.000000000000000000000000000000000m; // strips trailing zeros
        var scale = (byte)((decimal.GetBits(value)[3] >> 16) & 0xFF);
        var coefficient = value;
        for (var i = 0; i < scale; i++)
            coefficient *= 10m;
        if (coefficient > long.MaxValue)
            return false;
        var candidate = new ScaledPrice((long)coefficient, scale);
        if (!candidate.IsValid)
            return false;
        price = candidate;
        return true;
    }
}
