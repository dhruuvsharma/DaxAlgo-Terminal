using TradingTerminal.App.Login;
using TradingTerminal.Core.Brokers;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The two login groups, and the venues that appear in both.
///
/// <para>The login list is now cut by the only question a first-run user is actually asking: can I
/// connect right now, or do I have to go and register somewhere first? That is a different cut from the
/// three-way split it replaced, which separated a local bridge from a keyed broker — a fact about
/// transport rather than about what the user has to go and get, and one that put TWS (needs installing
/// and configuring) in a different group from an API key (needs signing up for).</para>
/// </summary>
public sealed class LoginGroupingTests
{
    [Fact]
    public void ThereAreExactlyTwoGroups()
    {
        // Two expanders, so exactly two names. A third would render an expander nobody asked for.
        var names = Enum.GetValues<LoginCategory>()
            .Select(category => category == LoginCategory.Keyless
                ? BrokerLoginFormBase.KeylessGroupName
                : BrokerLoginFormBase.KeyedGroupName)
            .Distinct()
            .ToArray();

        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void TheKeylessGroupSortsFirst()
    {
        // It is the only group a new user can act on without leaving to register somewhere, so it is
        // the one that should be open and at the top.
        var keyless = new StubForm(LoginCategory.Keyless);
        var keyed = new StubForm(LoginCategory.Credentialed);
        var local = new StubForm(LoginCategory.LocalBridge);

        Assert.True(keyless.CategoryOrder < keyed.CategoryOrder);
        Assert.True(keyless.CategoryOrder < local.CategoryOrder);
    }

    [Fact]
    public void ALocalBridgeCountsAsKeyRequired()
    {
        // TWS needs installing, configuring and signing into. Whatever that is, it is not "connect now",
        // and putting it beside the zero-setup feeds would make the keyless promise false.
        Assert.Equal(BrokerLoginFormBase.KeyedGroupName, new StubForm(LoginCategory.LocalBridge).CategoryName);
    }

    [Fact]
    public void OnlyTheKeylessCategoryIsKeyless()
    {
        Assert.True(new StubForm(LoginCategory.Keyless).IsKeyless);
        Assert.False(new StubForm(LoginCategory.Credentialed).IsKeyless);
        Assert.False(new StubForm(LoginCategory.LocalBridge).IsKeyless);
    }

    [Fact]
    public void TheGroupNamesAreConstantsTheMarkupCanMatch()
    {
        // The XAML opens the keyless expander by matching this exact string through x:Static. A label
        // that drifted from the match would silently collapse the one group that matters most.
        Assert.False(string.IsNullOrWhiteSpace(BrokerLoginFormBase.KeylessGroupName));
        Assert.False(string.IsNullOrWhiteSpace(BrokerLoginFormBase.KeyedGroupName));
        Assert.NotEqual(BrokerLoginFormBase.KeylessGroupName, BrokerLoginFormBase.KeyedGroupName);
    }

    // ── the venues that appear twice ────────────────────────────────────────────────────────────

    [Fact]
    public void TheDualModeVenuesShareOneBrokerKind()
    {
        // Both rows drive the same client because it is the same venue and the same market. Giving the
        // keyed row its own BrokerKind would split one exchange's stored history across two partitions
        // and relabel its provenance based on how we happened to authenticate.
        foreach (var kind in new[]
                 {
                     BrokerKind.Binance, BrokerKind.Coinbase, BrokerKind.Bybit,
                     BrokerKind.Kraken, BrokerKind.Okx,
                 })
        {
            Assert.True(BrokerCatalog.For(kind) is not null, $"{kind} is one venue with one catalogue entry");
        }
    }

    [Fact]
    public void TheCatalogueStillHoldsOneEntryPerVenue()
    {
        // Two login rows, one broker. The catalogue counts brokers.
        Assert.Equal(1, BrokerCatalog.All.Count(b => b.Id == "binance"));
        Assert.Equal(BrokerCatalog.All.Count, BrokerCatalog.All.Select(b => b.Id).Distinct().Count());
    }

    /// <summary>A form that exists only to exercise the category mapping.</summary>
    private sealed class StubForm : BrokerLoginFormBase
    {
        private readonly LoginCategory _category;

        public StubForm(LoginCategory category)
            : base(null!, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) =>
            _category = category;

        public override LoginCategory Category => _category;
        public override BrokerKind Broker => BrokerKind.Simulated;
        public override string DisplayName => "Stub";
        public override bool CanSubmit => true;
        public override void ApplyToOptions() { }
        public override string GetSessionAccountLabel() => "Stub";
        public override string GetTimeoutErrorMessage() => "Stub";
        public override string GetFailureMessage() => "Stub";
        public override void Load() { }
        public override void Save() { }
    }
}
