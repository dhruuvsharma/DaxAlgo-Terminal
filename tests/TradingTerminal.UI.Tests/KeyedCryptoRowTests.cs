using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The six venues that appear twice in the login list — once keyless, once keyed.
///
/// <para><b>What went wrong, and why it looked like a missing feature.</b> The tile table is keyed by
/// <see cref="BrokerKind"/>, and both rows of a venue share one kind. So the keyed row inherited the
/// keyless row's subtitle: under the heading "Key required · sign up and paste a key", the Binance row
/// read "Public WebSocket · live crypto, L2 depth". The only thing distinguishing it from the row above
/// was a "(API key)" suffix on the name. It was reported as the keyed versions being missing and the
/// keyless ones showing up in the wrong group — a fair reading of what was on screen.</para>
///
/// <para>Nothing threw, nothing logged, and the forms were correctly registered and correctly grouped.
/// The rows just described the wrong product.</para>
/// </summary>
public sealed class KeyedCryptoRowTests
{
    /// <summary>The keyless subtitle every one of these six shares in the tile table. A keyed row
    /// showing this is showing the keyless row's description.</summary>
    private const string KeylessDescription = "Public WebSocket";

    private static CredentialStore Store() => new(NullLogger<CredentialStore>.Instance);

    /// <summary>One instance of every keyed row, in catalogue order.</summary>
    private static KeyedCryptoLoginFormBase[] Rows() =>
    [
        new KeyedBinanceLoginFormViewModel(
            null!, Store(), Options.Create(new BinanceOptions()),
            NullLogger<KeyedBinanceLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
        new KeyedCoinbaseLoginFormViewModel(
            null!, Store(), Options.Create(new CoinbaseOptions()),
            NullLogger<KeyedCoinbaseLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
        new KeyedBybitLoginFormViewModel(
            null!, Store(), Options.Create(new BybitOptions()),
            NullLogger<KeyedBybitLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
        new KeyedKrakenLoginFormViewModel(
            null!, Store(), Options.Create(new KrakenOptions()),
            NullLogger<KeyedKrakenLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
        new KeyedOkxLoginFormViewModel(
            null!, Store(), Options.Create(new OkxOptions()),
            NullLogger<KeyedOkxLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
        new KeyedDeribitLoginFormViewModel(
            null!, Store(), Options.Create(new DeribitOptions()),
            NullLogger<KeyedDeribitLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None),
    ];

    public static TheoryData<KeyedCryptoLoginFormBase> KeyedRows()
    {
        var data = new TheoryData<KeyedCryptoLoginFormBase>();
        foreach (var row in Rows()) data.Add(row);
        return data;
    }

    [Theory]
    [MemberData(nameof(KeyedRows))]
    public void A_keyed_row_never_wears_the_keyless_rows_description(KeyedCryptoLoginFormBase form)
    {
        Assert.False(string.IsNullOrWhiteSpace(form.Subtitle));
        Assert.DoesNotContain(KeylessDescription, form.Subtitle, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(KeyedRows))]
    public void A_keyed_row_says_what_it_wants_before_it_is_opened(KeyedCryptoLoginFormBase form)
    {
        // The point of the subtitle is that a user reads it in the list and knows what to go and fetch.
        // Not all six hand out the same thing, and saying "secret" for all of them would be the same
        // class of error as the bug this file exists for: Coinbase issues an EC private key in PEM for
        // an ES256 JWT, and calling that a secret is how someone ends up pasting a key name into it.
        Assert.True(
            form.Subtitle.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || form.Subtitle.Contains("private key", StringComparison.OrdinalIgnoreCase),
            $"{form.DisplayName} does not name the credential to fetch: \"{form.Subtitle}\"");
    }

    [Theory]
    [MemberData(nameof(KeyedRows))]
    public void A_keyed_row_sits_in_the_key_required_group(KeyedCryptoLoginFormBase form)
    {
        Assert.Equal(BrokerLoginFormBase.KeyedGroupName, form.CategoryName);
        Assert.EndsWith("(API key)", form.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_rows_of_a_venue_share_its_broker_kind()
    {
        // Deliberate: same venue, same client, same market. Splitting the kind would split one
        // exchange's stored history across two provenance partitions.
        var kinds = Rows().Select(form => form.Broker).ToArray();

        Assert.Equal(
            new[]
            {
                BrokerKind.Binance, BrokerKind.Coinbase, BrokerKind.Bybit,
                BrokerKind.Kraken, BrokerKind.Okx, BrokerKind.Deribit,
            },
            kinds);
    }

    [Fact]
    public void Okx_is_the_only_one_asking_for_a_passphrase()
    {
        // Showing the field for the others asks for something that does not exist. OKX's passphrase is
        // chosen at key creation and is not the account password.
        var withPassphrase = Rows()
            .Where(form => form.UsesPassphrase)
            .Select(form => form.Broker)
            .ToArray();

        Assert.Equal(new[] { BrokerKind.Okx }, withPassphrase);
    }

    [Fact]
    public void Coinbase_is_the_only_one_taking_a_private_key()
    {
        // Its secret is an EC private key in PEM for an ES256 JWT, not a shared secret.
        var withPem = Rows()
            .Where(form => form.UsesPrivateKeyPem)
            .Select(form => form.Broker)
            .ToArray();

        Assert.Equal(new[] { BrokerKind.Coinbase }, withPem);
    }
}
