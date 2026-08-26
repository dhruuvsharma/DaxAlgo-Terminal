using System.IO;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// What the router remembers between launches (#44 phase 7).
///
/// <para>The estimator was constructed fresh with every view-model, so every launch began at the neutral
/// prior and the routing learned nothing it could keep. An estimator that resets before it warms up is a
/// constant with extra steps, and the whole argument for reward-biased routing is that the weights come
/// from evidence.</para>
/// </summary>
public sealed class AgentMemoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "daxalgo-agent-memory-" + Guid.NewGuid().ToString("N"));

    private string Path_ => System.IO.Path.Combine(_dir, "agent-reliability.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void WhatWasLearnedSurvivesARestart()
    {
        var first = new AgentReliability();
        for (var i = 0; i < 8; i++) first.Record(AgentRole.Coder, 1d);
        var learned = first.Of(AgentRole.Coder);

        AgentMemory.Save(first, Path_);
        var reloaded = AgentMemory.Load(Path_);

        reloaded.Of(AgentRole.Coder).Should().BeApproximately(learned, 1e-6);
        reloaded.ObservationsFor(AgentRole.Coder).Should().Be(8);
    }

    [Fact]
    public void RestoringIsNotItselfAnObservation()
    {
        // Folding a stored score back in as a fresh reward would blend it with the neutral prior and lose
        // a little of what was learned on every single launch — a slow drift back to knowing nothing.
        var first = new AgentReliability();
        for (var i = 0; i < 20; i++) first.Record(AgentRole.Painter, 1d);

        AgentMemory.Save(first, Path_);
        var reloaded = AgentMemory.Load(Path_);

        reloaded.Of(AgentRole.Painter).Should().BeApproximately(first.Of(AgentRole.Painter), 1e-6);
    }

    [Fact]
    public void ManyRoundTripsDoNotDecay()
    {
        // The property the previous test protects, stated over time: ten launches must not walk the
        // estimate back towards 0.5.
        var reliability = new AgentReliability();
        for (var i = 0; i < 20; i++) reliability.Record(AgentRole.Fixer, 1d);
        var original = reliability.Of(AgentRole.Fixer);

        for (var launch = 0; launch < 10; launch++)
        {
            AgentMemory.Save(reliability, Path_);
            reliability = AgentMemory.Load(Path_);
        }

        reliability.Of(AgentRole.Fixer).Should().BeApproximately(original, 1e-6);
    }

    [Fact]
    public void AnUntriedAgentIsNotWrittenAtAll()
    {
        // Writing the neutral prior for every role would make an agent that has never run
        // indistinguishable on reload from one that scored exactly 0.5.
        var reliability = new AgentReliability();
        reliability.Record(AgentRole.Coder, 0.9d);

        AgentMemory.Save(reliability, Path_);

        File.ReadAllText(Path_).Should().NotContain(nameof(AgentRole.Reviewer));
    }

    [Fact]
    public void AnUntriedAgentComesBackOnTheNeutralPrior()
    {
        var reliability = new AgentReliability();
        reliability.Record(AgentRole.Coder, 0.9d);
        AgentMemory.Save(reliability, Path_);

        var reloaded = AgentMemory.Load(Path_);

        reloaded.Of(AgentRole.Reviewer).Should().Be(AgentReliability.NeutralPrior);
        reloaded.ObservationsFor(AgentRole.Reviewer).Should().Be(0);
    }

    [Fact]
    public void AnAbsentFileIsAFreshEstimatorRatherThanAnError()
    {
        var reliability = AgentMemory.Load(System.IO.Path.Combine(_dir, "nothing-here.json"));

        reliability.Of(AgentRole.Coder).Should().Be(AgentReliability.NeutralPrior);
    }

    [Fact]
    public void ACorruptFileIsAFreshEstimatorRatherThanACrash()
    {
        // Losing what was learned is a small harm next to a builder that will not open.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ this is not the file you are looking for");

        AgentMemory.Load(Path_).Of(AgentRole.Coder).Should().Be(AgentReliability.NeutralPrior);
    }

    [Fact]
    public void AnUnknownRoleNameIsSkippedRatherThanLosingTheRest()
    {
        // Roles can be added or renamed. An old file naming one that no longer exists must not cost the
        // estimates for the roles that do.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            [
              { "Role": "Astrologer", "Score": 0.99, "Observations": 5 },
              { "Role": "Coder", "Score": 0.8, "Observations": 4 }
            ]
            """);

        var reloaded = AgentMemory.Load(Path_);

        reloaded.Of(AgentRole.Coder).Should().BeApproximately(0.8d, 1e-6);
    }

    [Fact]
    public void SavingSomewhereUnwritableIsSilentRatherThanFatal()
    {
        // A run that produced a strategy must not be reported as failed because a cache could not be
        // written.
        var save = () => AgentMemory.Save(
            new AgentReliability(), System.IO.Path.Combine(_dir, new string('x', 300), "x.json"));

        save.Should().NotThrow();
    }

    [Fact]
    public void TheStoredFileHoldsNumbersAndRoleNamesOnly()
    {
        // Same rule the trajectory log follows: nothing about the user, their brief, or their code.
        var reliability = new AgentReliability();
        reliability.Record(AgentRole.Coder, 0.75d);
        AgentMemory.Save(reliability, Path_);

        var text = File.ReadAllText(Path_);

        text.Should().Contain("Coder");
        text.Should().NotContain("strategy");
        text.Should().NotContain("prompt");
    }
}
