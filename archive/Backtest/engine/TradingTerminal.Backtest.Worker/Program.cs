using System.Text;
using TradingTerminal.Backtest.Worker;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

return await WorkerApplication.RunAsync(args).ConfigureAwait(false);
