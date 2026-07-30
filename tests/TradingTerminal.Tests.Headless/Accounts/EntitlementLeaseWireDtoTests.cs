using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class EntitlementLeaseWireDtoTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Version_one_contract_round_trips_the_platform_wire_shape()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var lease = Create(accountId, deviceId, Now.AddDays(7));

        var json = JsonSerializer.Serialize(lease, SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<EntitlementLeaseWireDto>(json, SerializerOptions);

        json.Should().Contain("\"schemaVersion\":1");
        json.Should().Contain("\"state\":\"Active\"");
        json.Should().Contain("\"edition\":\"Professional\"");
        json.Should().Contain("\"productAccountId\"");
        json.Should().Contain("\"deviceId\"");
        roundTrip.Should().BeEquivalentTo(lease);
        roundTrip!.ToSignedEnvelope().EncodedPayload.Should().Be("payload");
    }

    [Fact]
    public void Public_core_deserializes_the_shared_platform_v1_golden_payload()
    {
        var json = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "entitlement-lease-v1.json"));

        var lease = JsonSerializer.Deserialize<EntitlementLeaseWireDto>(json, SerializerOptions);

        lease.Should().NotBeNull();
        lease!.SchemaVersion.Should().Be(EntitlementLeaseWireDto.CurrentSchemaVersion);
        lease.LeaseId.Should().Be("lease-golden-v1");
        lease.State.Should().Be(SubscriptionEntitlementState.Active);
        lease.Edition.Should().Be(AppEdition.Professional);
        lease.ProductAccountId.Should().Be("11111111-1111-1111-1111-111111111111");
        lease.DeviceId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
        (lease.ExpiresAtUtc - lease.IssuedAtUtc)
            .Should().Be(EntitlementLeaseWireDto.MaximumLeaseDuration);
    }

    [Fact]
    public void Contract_rejects_a_lease_longer_than_seven_days()
    {
        var act = () => Create(Guid.NewGuid(), Guid.NewGuid(), Now.AddDays(7).AddTicks(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Device_binding_requires_both_product_account_and_device()
    {
        var accountId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var lease = Create(accountId, deviceId, Now.AddHours(1));

        lease.IsBoundTo(accountId.ToString("D"), deviceId).Should().BeTrue();
        lease.IsBoundTo(Guid.NewGuid().ToString("D"), deviceId).Should().BeFalse();
        lease.IsBoundTo(accountId.ToString("D"), Guid.NewGuid()).Should().BeFalse();
    }

    private static EntitlementLeaseWireDto Create(
        Guid accountId,
        Guid deviceId,
        DateTimeOffset expiresAtUtc) =>
        new(
            EntitlementLeaseWireDto.CurrentSchemaVersion,
            "lease-1",
            SubscriptionEntitlementState.Active,
            AppEdition.Professional,
            accountId.ToString("D"),
            deviceId,
            "issuer",
            "audience",
            Now,
            Now,
            expiresAtUtc,
            "key-1",
            "algorithm-1",
            "payload",
            "signature");
}
