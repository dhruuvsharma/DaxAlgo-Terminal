using System.Diagnostics;
using FluentAssertions;
using TradingTerminal.Backtest.Protocol;
using TradingTerminal.Infrastructure.Backtest.Worker;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

public sealed class WorkerClientTests
{
    [Fact]
    public async Task Client_accepts_verified_atomic_result_from_one_shot_process()
    {
        using var temp = new WorkerTempDirectory();
        var reportSource = System.IO.Path.Combine(temp.Path, "source-report.json");
        await File.WriteAllTextAsync(
            reportSource,
            BacktestProtocolJson.Serialize(WorkerTestData.ReportArtifact()));
        var request = WorkerTestData.Request("success-job");
        var inputHash = BacktestProtocolHash.ComputeSha256(BacktestProtocolJson.Serialize(request.Input));
        var options = PowerShellOptions(temp, "success-job", SuccessWorkerScript, reportSource, inputHash);
        using var client = new BacktestJobClient(options);

        var outcome = await client.RunAsync(request);

        outcome.Status.Should().Be(
            BacktestTerminalStatus.Succeeded,
            $"{outcome.Error?.Message}\n{outcome.WorkerStandardError}");
        outcome.Manifest.Should().NotBeNull();
        outcome.Report.Should().NotBeNull();
        outcome.Report!.Summary.EndingEquity.Should().Be(100_250);
        File.Exists(System.IO.Path.Combine(outcome.JobDirectory, BacktestJobFiles.ResultManifest)).Should().BeTrue();
        File.Exists(System.IO.Path.Combine(outcome.JobDirectory, BacktestJobFiles.ResultManifestHash)).Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_progress_emitted_during_process_exit_is_not_accepted()
    {
        using var temp = new WorkerTempDirectory();
        var reportSource = System.IO.Path.Combine(temp.Path, "source-report.json");
        await File.WriteAllTextAsync(
            reportSource,
            BacktestProtocolJson.Serialize(WorkerTestData.ReportArtifact()));
        var request = WorkerTestData.Request("invalid-progress-job");
        var inputHash = BacktestProtocolHash.ComputeSha256(BacktestProtocolJson.Serialize(request.Input));
        var command = SuccessWorkerScript + Environment.NewLine + "[Console]::Out.WriteLine('not-json')";
        var options = PowerShellOptions(temp, request.JobId, command, reportSource, inputHash);
        using var client = new BacktestJobClient(options);

        var outcome = await client.RunAsync(request);

        outcome.Status.Should().Be(BacktestTerminalStatus.ProtocolError);
        outcome.Error!.Code.Should().Be("invalid_progress");
    }

    [Fact]
    public async Task Client_rejects_manifest_component_version_mismatch()
    {
        using var temp = new WorkerTempDirectory();
        var reportSource = System.IO.Path.Combine(temp.Path, "source-report.json");
        await File.WriteAllTextAsync(
            reportSource,
            BacktestProtocolJson.Serialize(WorkerTestData.ReportArtifact()));
        var request = WorkerTestData.Request("version-mismatch-job");
        var inputHash = BacktestProtocolHash.ComputeSha256(BacktestProtocolJson.Serialize(request.Input));
        var command = SuccessWorkerScript.Replace(
            "engine_version = $request.engine_version",
            "engine_version = '2.0'",
            StringComparison.Ordinal);
        var options = PowerShellOptions(temp, request.JobId, command, reportSource, inputHash);
        using var client = new BacktestJobClient(options);

        var outcome = await client.RunAsync(request);

        outcome.Status.Should().Be(BacktestTerminalStatus.ProtocolError);
        outcome.Error!.Code.Should().Be("result_engine_version_mismatch");
    }

    [Fact]
    public async Task Cancellation_kills_worker_process_tree()
    {
        using var temp = new WorkerTempDirectory();
        var options = PowerShellOptions(temp, "cancel-job", SleepingWorkerScript);
        using var client = new BacktestJobClient(options);
        using var cts = new CancellationTokenSource();
        var request = WorkerTestData.Request("cancel-job");
        var run = client.RunAsync(request, ct: cts.Token);
        var pidPath = System.IO.Path.Combine(temp.Path, "jobs", request.JobId, "child.pid");
        var childPid = await WaitForChildPidAsync(pidPath);

        cts.Cancel();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Status.Should().Be(BacktestTerminalStatus.Cancelled);
        await WaitForExitAsync(childPid, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Disposing_client_kills_worker_process_tree()
    {
        using var temp = new WorkerTempDirectory();
        var options = PowerShellOptions(temp, "dispose-job", SleepingWorkerScript);
        var client = new BacktestJobClient(options);
        var request = WorkerTestData.Request("dispose-job");
        var run = client.RunAsync(request);
        var pidPath = System.IO.Path.Combine(temp.Path, "jobs", request.JobId, "child.pid");
        var childPid = await WaitForChildPidAsync(pidPath);

        client.Dispose();
        var outcome = await run.WaitAsync(TimeSpan.FromSeconds(10));

        outcome.Status.Should().Be(BacktestTerminalStatus.Cancelled);
        outcome.Error!.Code.Should().Be("client_disposed");
        await WaitForExitAsync(childPid, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Real_worker_executes_synthetic_job_end_to_end()
    {
        using var temp = new WorkerTempDirectory();
        var workerDll = ResolveBuiltWorkerDll();
        File.Exists(workerDll).Should().BeTrue("the focused verification builds the worker before this test");
        var options = new BacktestWorkerOptions
        {
            WorkerExecutablePath = workerDll,
            JobRootDirectory = System.IO.Path.Combine(temp.Path, "jobs"),
            DefaultTimeout = TimeSpan.FromSeconds(30),
        };
        using var client = new BacktestJobClient(options);

        var outcome = await client.RunAsync(WorkerTestData.Request("real-worker-job"));

        outcome.Status.Should().Be(
            BacktestTerminalStatus.Succeeded,
            $"{outcome.Error?.Code}: {outcome.Error?.Message}\n{outcome.WorkerStandardError}");
        outcome.Report.Should().NotBeNull();
        outcome.Report!.Summary.EventsProcessed.Should().Be(500);
    }

    [Fact]
    public async Task Real_worker_preserves_explicit_early_protocol_failure()
    {
        using var temp = new WorkerTempDirectory();
        var request = WorkerTestData.Request("real-worker-hash-mismatch");
        request = request with
        {
            Strategy = request.Strategy with { ExpectedAssemblySha256 = new string('f', 64) },
        };
        var options = new BacktestWorkerOptions
        {
            WorkerExecutablePath = ResolveBuiltWorkerDll(),
            JobRootDirectory = System.IO.Path.Combine(temp.Path, "jobs"),
            DefaultTimeout = TimeSpan.FromSeconds(30),
        };
        using var client = new BacktestJobClient(options);

        var outcome = await client.RunAsync(request);

        outcome.Status.Should().Be(BacktestTerminalStatus.ProtocolError);
        outcome.Manifest.Should().NotBeNull();
        outcome.Error!.Code.Should().Be("strategy_assembly_hash_mismatch");
    }

    private static BacktestWorkerOptions PowerShellOptions(
        WorkerTempDirectory temp,
        string jobId,
        string command,
        string? reportSource = null,
        string? inputHash = null)
    {
        var powerShell = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        File.Exists(powerShell).Should().BeTrue("the Windows headless test target requires Windows PowerShell");

        var options = new BacktestWorkerOptions
        {
            WorkerExecutablePath = powerShell,
            JobRootDirectory = System.IO.Path.Combine(temp.Path, "jobs"),
            DefaultTimeout = TimeSpan.FromSeconds(30),
        };
        options.WorkerArguments.Add("-NoProfile");
        options.WorkerArguments.Add("-NonInteractive");
        var requestPath = System.IO.Path.Combine(temp.Path, "jobs", jobId, BacktestJobFiles.Request);
        var preamble = $"$requestPath = '{EscapePowerShellLiteral(requestPath)}'{Environment.NewLine}";
        if (reportSource is not null)
            preamble += $"$reportSource = '{EscapePowerShellLiteral(reportSource)}'{Environment.NewLine}";
        if (inputHash is not null)
            preamble += $"$inputHash = '{EscapePowerShellLiteral(inputHash)}'{Environment.NewLine}";
        var scriptPath = System.IO.Path.Combine(temp.Path, $"fake-worker-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, preamble + command);
        options.WorkerArguments.Add("-File");
        options.WorkerArguments.Add(scriptPath);
        return options;
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string ResolveBuiltWorkerDll()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        var configuration = output.Parent!.Name;
        var targetFramework = output.Name;
        var outputRoot = output.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        return System.IO.Path.Combine(
            outputRoot,
            "src", "windows", "Backtest", "TradingTerminal.Backtest.Worker",
            "bin", configuration, targetFramework, "TradingTerminal.Backtest.Worker.dll");
    }

    private static async Task<int> WaitForChildPidAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid)) return pid;
            await Task.Delay(50);
        }
        throw new TimeoutException($"The fake worker did not publish its child pid at {path}.");
    }

    private static async Task WaitForExitAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited) return;
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"Child process {pid} survived worker teardown.");
    }

    private const string SleepingWorkerScript = """
        $jobDirectory = [IO.Path]::GetDirectoryName($requestPath)
        $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $child = Start-Process -FilePath $powerShell -ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru
        [IO.File]::WriteAllText((Join-Path $jobDirectory 'child.pid'), $child.Id.ToString())
        Start-Sleep -Seconds 60
        """;

    private const string SuccessWorkerScript = """
        $jobDirectory = [IO.Path]::GetDirectoryName($requestPath)
        $artifactDirectory = Join-Path $jobDirectory 'artifacts'
        [IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
        $reportPath = Join-Path $artifactDirectory 'report.json'
        [IO.File]::Copy($reportSource, $reportPath, $false)
        $request = Get-Content -LiteralPath $requestPath -Raw | ConvertFrom-Json
        $requestHash = (Get-FileHash -LiteralPath $requestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $reportHash = (Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $reportLength = (Get-Item -LiteralPath $reportPath).Length
        $now = [DateTime]::UtcNow.ToString('O')
        $zeros = '0' * 64
        $manifest = [ordered]@{
            protocol_version = 1
            job_id = $request.job_id
            terminal_status = 'succeeded'
            started_utc = $now
            completed_utc = $now
            request_sha256 = $requestHash
            engine_version = $request.engine_version
            sdk_version = $request.sdk_version
            strategy_contract_version = $request.strategy_contract_version
            engine_fingerprint = 'fake-test-engine'
            backend_fingerprint = 'fake-test-backend'
            strategy_id = $request.strategy.id
            strategy_assembly_sha256 = $zeros
            parameters_sha256 = $request.parameters_sha256
            input_sha256 = $inputHash
            artifacts = @([ordered]@{
                kind = 'report'
                schema_version = 1
                relative_path = 'artifacts/report.json'
                length_bytes = $reportLength
                sha256 = $reportHash
            })
            error = $null
        }
        $utf8 = New-Object Text.UTF8Encoding($false)
        $manifestTemp = Join-Path $jobDirectory '.result.manifest.tmp'
        $manifestPath = Join-Path $jobDirectory 'result.manifest.json'
        $hashPath = Join-Path $jobDirectory 'result.manifest.sha256'
        [IO.File]::WriteAllText($manifestTemp, ($manifest | ConvertTo-Json -Depth 8), $utf8)
        $manifestHash = (Get-FileHash -LiteralPath $manifestTemp -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($hashPath, $manifestHash + "`n", [Text.Encoding]::ASCII)
        [IO.File]::Move($manifestTemp, $manifestPath)
        """;
}
