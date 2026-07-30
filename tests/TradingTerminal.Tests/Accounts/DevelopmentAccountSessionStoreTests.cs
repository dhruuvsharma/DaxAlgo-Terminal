using System.IO;
using System.Text;
using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Accounts;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class DevelopmentAccountSessionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "DaxAlgoAccountTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Protected_session_can_be_reused_and_cleared_without_storing_plain_identity()
    {
        var path = Path.Combine(_directory, "account-session.dat");
        var store = new DevelopmentAccountSessionStore(path, new XorProtector());
        var session = new AccountSessionSnapshot(
            "google-development-session",
            new AccountIdentity(
                "google:subject-42",
                "Example Person",
                "person@example.com"),
            Now,
            Now.AddHours(8));

        store.Save(session).Should().BeTrue();

        var bytes = File.ReadAllBytes(path);
        Encoding.UTF8.GetString(bytes).Should().NotContain("person@example.com");
        store.Load().Should().BeEquivalentTo(session);

        store.Clear().Should().BeTrue();
        File.Exists(path).Should().BeFalse();
        store.Load().Should().BeNull();
    }

    [Fact]
    public void Corrupt_session_is_rejected_and_removed()
    {
        var path = Path.Combine(_directory, "account-session.dat");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "not a protected account session");
        var store = new DevelopmentAccountSessionStore(path, new XorProtector());

        store.Load().Should().BeNull();

        File.Exists(path).Should().BeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class XorProtector : IAccountSessionProtector
    {
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);

        public byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext);

        private static byte[] Transform(byte[] value)
        {
            var transformed = new byte[value.Length];
            for (var index = 0; index < value.Length; index++)
                transformed[index] = (byte)(value[index] ^ 0xA5);
            return transformed;
        }
    }
}
