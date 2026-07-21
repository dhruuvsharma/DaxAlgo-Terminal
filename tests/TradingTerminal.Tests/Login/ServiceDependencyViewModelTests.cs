using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TradingTerminal.App.Login;
using Xunit;

namespace TradingTerminal.Tests.Login;

public sealed class ServiceDependencyViewModelTests
{
    [Fact]
    public async Task Tcp_probe_returns_true_when_one_candidate_port_is_listening()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var openPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await ServiceDependencyViewModel.TcpOpenAsync(
            "127.0.0.1",
            new[] { openPort },
            CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Tcp_probe_observes_connection_failure_and_returns_false()
    {
        int closedPort;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0))
        {
            reservation.Start();
            closedPort = ((IPEndPoint)reservation.LocalEndpoint).Port;
        }

        var result = await ServiceDependencyViewModel.TcpOpenAsync(
            "127.0.0.1",
            new[] { closedPort },
            CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task Tcp_probe_honors_an_already_cancelled_sweep_without_starting_more_work()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var elapsed = Stopwatch.StartNew();

        var result = await ServiceDependencyViewModel.TcpOpenAsync(
            "192.0.2.1",
            new[] { 7497, 7496 },
            cancellation.Token);

        elapsed.Stop();
        result.Should().BeFalse();
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }
}
