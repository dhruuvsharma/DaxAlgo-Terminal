using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

/// <summary>
/// The header books chip, and the lifetime rule it depends on: the execution engine belongs to the
/// application, not to the console window.
/// </summary>
[Collection("Execution client")]
public sealed class ExecutionBooksChipTests
{
    [Fact]
    public async Task BooksSurviveTheConsoleWindowClosing()
    {
        // The reported bug: books vanished when the Execution Console was closed. The client was
        // registered transient and resolved inside the window's DI scope, and the view-model disposed
        // it on close - so the engine and every book went with the window.
        using var client = new InProcessExecutionClient();
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Overnight", "paper", Array.AsReadOnly(["Test strategy"])))).IsSuccess);

        // Stand a chip up, tear it down again - the header control coming and going must not disturb
        // the engine either.
        var chip = new ExecutionBooksChipViewModel(client);
        Assert.Single(chip.Books);
        chip.Dispose();

        Assert.Single(client.GetSnapshot().Books);
    }

    [Fact]
    public async Task Toggle_StopsAndRestartsOrderIntakeWithoutTouchingPositions()
    {
        using var client = new InProcessExecutionClient();
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Live Book", "paper", Array.AsReadOnly(["Test strategy"])))).IsSuccess);

        using var chip = new ExecutionBooksChipViewModel(client);
        var book = Assert.Single(chip.Books);
        Assert.True(book.IsRunning);
        Assert.Equal("Stop", book.ToggleLabel);
        Assert.Equal(1, chip.RunningCount);
        Assert.False(chip.HasStoppedBook);

        await chip.ToggleCommand.ExecuteAsync(book.Id);

        var stopped = Assert.Single(chip.Books);
        Assert.False(stopped.IsRunning);
        Assert.Equal("Run", stopped.ToggleLabel);
        Assert.Equal(0, chip.RunningCount);
        Assert.True(chip.HasStoppedBook);
        Assert.Equal(string.Empty, chip.LastError);

        // Stop is intake only. The book still exists and still holds whatever it held.
        Assert.True(Assert.Single(client.GetSnapshot().Books).IsIntakePaused);

        await chip.ToggleCommand.ExecuteAsync(stopped.Id);

        Assert.True(Assert.Single(chip.Books).IsRunning);
        Assert.False(Assert.Single(client.GetSnapshot().Books).IsIntakePaused);
    }

    [Fact]
    public void NoBooks_HidesTheChipRatherThanShowingAnEmptyOne()
    {
        using var client = new InProcessExecutionClient();

        using var chip = new ExecutionBooksChipViewModel(client);

        Assert.False(chip.HasBooks);
        Assert.Empty(chip.Books);
        Assert.Equal("No books", chip.Summary);
        Assert.False(chip.HasStoppedBook);
    }

    [Fact]
    public async Task Summary_CountsRunningAgainstTotal()
    {
        using var client = new InProcessExecutionClient();
        foreach (var name in new[] { "One", "Two" })
        {
            Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
                name, "paper", Array.AsReadOnly(["Test strategy"])))).IsSuccess);
        }

        using var chip = new ExecutionBooksChipViewModel(client);
        Assert.Equal("2/2 running", chip.Summary);

        await chip.ToggleCommand.ExecuteAsync(chip.Books[0].Id);

        Assert.Equal("1/2 running", chip.Summary);
        Assert.True(chip.HasStoppedBook);
    }

    [Fact]
    public async Task UnboundBook_ReportsItRatherThanShowingAnEmptyStrategyLine()
    {
        using var client = new InProcessExecutionClient();
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Unbound", "paper", Array.Empty<string>()))).IsSuccess);

        using var chip = new ExecutionBooksChipViewModel(client);

        Assert.Equal("No strategy bound", Assert.Single(chip.Books).StrategySummary);
    }
}
