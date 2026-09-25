using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Login;
using TradingTerminal.UI.Controls;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Every login row that needs something typed into it has somewhere to type it.
///
/// <para><b>What went wrong.</b> The login window builds each row's body through one VM→view switch
/// (<c>LoginWindow.BuildForm</c>). The six keyed crypto rows, Tradier and OANDA were registered, grouped
/// under "Key required" and labelled correctly — and missing from that switch. Their bodies were empty,
/// so no key could be entered, <c>CanSubmit</c> never went true and Connect stayed disabled. It was
/// reported as the keyed rows "still using keyless". Every view-model test passed throughout, because
/// none of them asked whether a view existed.</para>
/// </summary>
public sealed class LoginRowViewTests
{
    /// <summary>Every row <c>AddLogin</c> registers — the keyless public feeds and all eight keyed ones.</summary>
    private static IReadOnlyList<IBrokerLoginForm> RegisteredRows()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddOptions();
        services.AddSingleton<IBrokerSelector>(new LiveStateSelector());
        services.AddSingleton(IBrokerCredentialVerifier.None);
        services.AddLogin();

        return [.. services.BuildServiceProvider().GetServices<IBrokerLoginForm>()];
    }

    /// <summary>The window's VM→view switch, as the window installs it. Each view comes back with its
    /// bindings attached: built outside a window, WPF defers that to the dispatcher, and until it runs
    /// text typed into a box goes nowhere — here, not in the app, where the view is in a live window.</summary>
    private static Func<object, UIElement?> ViewFactory()
    {
        RuntimeHelpers.RunClassConstructor(typeof(LoginWindow).TypeHandle);
        var build = InjectedFormHost.ViewFactory
            ?? throw new InvalidOperationException("LoginWindow installed no view factory.");

        return form =>
        {
            var view = build(form);
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            return view;
        };
    }

    [WpfFact]
    public void Every_row_that_needs_input_has_a_view()
    {
        var build = ViewFactory();

        // A fresh row that cannot submit is waiting for the user to type something. With no view there
        // is nothing to type into, and the row is dead.
        var needingInput = RegisteredRows().Where(form => !form.CanSubmit).ToArray();
        Assert.NotEmpty(needingInput);

        var withoutView = needingInput
            .Where(form => build(form) is null)
            .Select(form => form.DisplayName)
            .ToArray();

        Assert.True(withoutView.Length == 0, "No view for: " + string.Join(", ", withoutView));
    }

    [WpfFact]
    public void Every_keyed_crypto_row_takes_a_key_typed_into_its_view()
    {
        var build = ViewFactory();

        foreach (var form in RegisteredRows().OfType<KeyedCryptoLoginFormBase>())
        {
            var view = Assert.IsType<KeyedCryptoLoginForm>(build(form));

            ((TextBox)view.FindName("KeyBox")).Text = "the-key";
            if (form.UsesPrivateKeyPem)
                ((TextBox)view.FindName("PemBox")).Text = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
            else
                ((PasswordBox)view.FindName("SecretBox")).Password = "the-secret";
            if (form.UsesPassphrase)
                ((PasswordBox)view.FindName("PassphraseBox")).Password = "the-passphrase";

            Assert.Equal("the-key", form.ApiKey);
            Assert.False(string.IsNullOrEmpty(form.ApiSecret));
            Assert.True(form.CanSubmit, $"{form.DisplayName} still cannot submit after its fields were filled.");
        }
    }

    [WpfFact]
    public void A_pem_keeps_its_line_breaks()
    {
        // A single-line input keeps only the first line of a paste. A PEM entered that way is its
        // header line and nothing else, which the venue reports as a rejected key.
        var build = ViewFactory();
        var coinbase = RegisteredRows().OfType<KeyedCoinbaseLoginFormViewModel>().Single();
        var view = (KeyedCryptoLoginForm)build(coinbase)!;

        var pemBox = (TextBox)view.FindName("PemBox");
        Assert.True(pemBox.AcceptsReturn);

        const string Pem = "-----BEGIN EC PRIVATE KEY-----\nAAAA\n-----END EC PRIVATE KEY-----";
        pemBox.Text = Pem;

        Assert.Equal(Pem, coinbase.ApiSecret);
    }

    [WpfFact]
    public void Tradier_and_oanda_take_a_token_typed_into_their_views()
    {
        var build = ViewFactory();
        var rows = RegisteredRows();

        var tradier = rows.OfType<TradierLoginFormViewModel>().Single();
        var tradierView = Assert.IsType<TradierLoginForm>(build(tradier));
        ((PasswordBox)tradierView.FindName("TokenBox")).Password = "tradier-token";
        Assert.True(tradier.CanSubmit);

        var oanda = rows.OfType<OandaLoginFormViewModel>().Single();
        var oandaView = Assert.IsType<OandaLoginForm>(build(oanda));
        ((PasswordBox)oandaView.FindName("TokenBox")).Password = "oanda-token";
        oanda.AccountId = "101-001-1234567-001";
        Assert.True(oanda.CanSubmit);
    }

    [Fact]
    public void Every_described_venue_has_a_keyless_row_and_a_keyed_row()
    {
        var rows = RegisteredRows().OfType<BrokerLoginFormBase>().ToList();

        foreach (var venue in PublicVenueLogins.All)
        {
            var mine = rows.Where(r => r.Broker == venue.Broker).ToList();
            Assert.Equal(2, mine.Count);

            var keyless = Assert.Single(mine, r => r.IsKeyless);
            var keyed = Assert.IsType<KeyedVenueLoginFormViewModel>(Assert.Single(mine, r => !r.IsKeyless));

            Assert.True(keyless.CanSubmit, $"{keyless.DisplayName} should connect with nothing filled in");
            Assert.False(keyed.CanSubmit, $"{keyed.DisplayName} should wait for a key");
            Assert.Equal(BrokerLoginFormBase.KeyedGroupName, keyed.CategoryName);
            Assert.DoesNotContain("Public WebSocket", keyed.Subtitle, StringComparison.Ordinal);
            Assert.EndsWith("(API key)", keyed.DisplayName, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(keyed.WhereToGetAKey));
            Assert.NotEqual("?", keyless.Badge);
        }
    }

    [WpfFact]
    public void Every_catalogued_broker_with_a_mark_on_disk_shows_it()
    {
        // BrokerLogo used to keep its own table of twelve, and marks on disk for Deribit, Hyperliquid,
        // Tradier and OANDA were never shown. It reads the catalogue now; this holds it to that.
        // pack://application URIs resolve only once WPF has registered the scheme and the application
        // package, which Application's static constructor does; touching a static member runs it
        // without creating an Application.
        _ = System.Windows.Application.Current;

        var onDisk = Directory.GetFiles(BrokerMarks(), "*.png")
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        var connectable = BrokerCatalog.All.Where(p => p.Kind is not null && onDisk.Contains(p.Id)).ToList();
        Assert.NotEmpty(connectable);

        foreach (var profile in connectable)
            Assert.True(new BrokerLogo { Broker = profile.Kind!.Value }.Source is not null,
                $"{profile.DisplayName} has a mark on disk and none is shown");
    }

    [WpfFact]
    public void A_sign_in_row_shows_only_the_fields_its_broker_asks_for_and_takes_what_is_typed()
    {
        var build = ViewFactory();
        foreach (var row in RegisteredRows().OfType<SessionBrokerLoginFormViewModel>())
        {
            var view = Assert.IsType<SessionLoginForm>(build(row));

            void Check(string box, bool asked)
            {
                var element = (FrameworkElement)view.FindName(box);
                var shown = ((FrameworkElement)element.Parent).Visibility == Visibility.Visible;
                Assert.True(shown == asked, $"{row.DisplayName}: {box} shown={shown}, asked={asked}");
            }

            Check("KeyBox", row.AsksKey);
            Check("AccountBox", row.AsksAccount);
            Check("ExtraBox", row.AsksExtra);
            Check("SecretBox", row.AsksSecret);
            Check("PassphraseBox", row.AsksPassphrase);
            Check("RedirectBox", row.AsksRedirect);
            Check("ProofBox", row.AsksProof);

            if (row.AsksAccount)
            {
                ((TextBox)view.FindName("AccountBox")).Text = "CLIENT1";
                Assert.Equal("CLIENT1", row.Account);
            }

            if (row.AsksSecret)
            {
                ((PasswordBox)view.FindName("SecretBox")).Password = "typed-secret";
                Assert.Equal("typed-secret", row.ApiSecret);
            }
        }
    }

    private static string BrokerMarks([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "assets", "brokers"));
}
