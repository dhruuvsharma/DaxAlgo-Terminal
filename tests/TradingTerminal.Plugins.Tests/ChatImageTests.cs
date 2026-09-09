using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A picture attached to a message, and where it goes.
///
/// <para><b>It rides the USER'S TURN into the thread.</b> That is what makes it durable: the planner is
/// the only participant holding the conversation, and a screenshot explaining what to build is worth
/// exactly as much on turn four as on turn one. Builders stay text-only — a picture of the whole window
/// is not what a single-file builder is being asked about.</para>
/// </summary>
public sealed class ChatImageTests
{
    private static CodegenImage Png(string caption = "mock.png") => new(
        "image/png",
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="),
        caption);

    [Fact]
    public void AMessageWithoutPicturesIsUnchanged()
    {
        // The overwhelming majority of turns. The parameter is optional precisely so that every
        // existing construction still means what it did.
        var message = new CodegenMessage(CodegenRole.User, "build me a ladder");

        message.HasImages.Should().BeFalse();
        message.Images.Should().BeNull();
    }

    [Fact]
    public void AMessageCarriesWhatWasAttachedToIt()
    {
        var message = new CodegenMessage(CodegenRole.User, "make it look like this", [Png()]);

        message.HasImages.Should().BeTrue();
        message.Images!.Should().ContainSingle().Which.Caption.Should().Be("mock.png");
    }

    [Fact]
    public void APictureSurvivesTheTripThroughTheThread()
    {
        // The session rewrites messages on the way to the wire — assistant turns get their code
        // stripped, and the last user turn gets the current file set appended. A rewrite that dropped
        // the pictures would lose them on exactly the turn they were sent.
        var original = new CodegenMessage(CodegenRole.User, "here is the window I mean", [Png()]);

        var rewritten = original with { Content = original.Content + "\n\nFILES" };

        rewritten.HasImages.Should().BeTrue("a `with` copy keeps what it did not change");
        rewritten.Images!.Should().ContainSingle();
    }

    [Fact]
    public void OnlyTheThreeTypesEveryProviderAcceptsAreOffered()
    {
        // Offering more would let a user attach a BMP and be told afterwards it could not be used.
        // The picker's filter and the pane's own check have to agree, so both are pinned here.
        var accepted = new[] { "image/png", "image/jpeg", "image/webp" };

        accepted.Should().BeEquivalentTo(
            TradingTerminal.Infrastructure.Strategies.Authoring.Reference.BraveReferenceSearch.AcceptedImageTypes,
            "an attached picture and a searched one go to the same providers");
    }
}
