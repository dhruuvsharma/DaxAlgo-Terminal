using TradingTerminal.Execution.Ipc;
using TradingTerminal.Execution.Service;

namespace TradingTerminal.Execution.ServiceHost;

internal static class Program
{
    private static async Task<int> Main()
    {
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            using var runtime = ExecutionServiceRuntime.Create();
            using var server = new ExecutionNamedPipeServer(
                runtime.Engine,
                new DpapiExecutionServiceSecretStore(),
                log: message => Console.Error.WriteLine(message));

            Console.WriteLine(
                $"DaxAlgo execution service is listening on local named pipe '{SecureExecutionNamedPipe.DefaultPipeName}' " +
                $"for simulated account {runtime.Engine.Account.AccountId.Value}; fencing token {runtime.Engine.LeaseGrant.FencingToken.Value}.");
            await server.RunAsync(shutdown.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Execution service failed closed: {exception.Message}");
            return 1;
        }
    }
}
