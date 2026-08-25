using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// What the session has produced so far, as <b>artifacts rather than a transcript</b>.
///
/// <para>The first version of the loop appended every agent's reply to one shared conversation. That is
/// the obvious design and it is wrong on both counts that matter here.</para>
///
/// <para><b>Cost.</b> A transcript grows with every turn and every turn re-sends all of it, so the bill
/// is quadratic in turns. Artifacts are replaced rather than accumulated, so a run's context is bounded
/// by the size of the work, not by how long it took to get there — and under a token budget, how long
/// it took is exactly what you do not want to pay for twice.</para>
///
/// <para><b>Quality.</b> A transcript hands the Painter the Quant's derivation, the Fixer the
/// Interviewer's questions, and everyone three stale versions of the same file. Models repair the wrong
/// copy when an old one is in context; that is not a token problem, it is a correctness one. Each agent
/// gets what its job needs and nothing else.</para>
/// </summary>
/// <param name="Brief">What the user asked for, verbatim.</param>
/// <param name="Spec">The Interviewer's specification, once there is one.</param>
/// <param name="Maths">The Quant's derivation, once there is one.</param>
/// <param name="Files">The <b>current</b> code. Replaced on every write, never appended to.</param>
/// <param name="Findings">Only the most recent verdict's findings. Older ones describe code that no
/// longer exists.</param>
public sealed record AgentContext(
    string Brief,
    string? Spec = null,
    string? Maths = null,
    IReadOnlyList<StrategyFile>? Files = null,
    IReadOnlyList<VerificationFinding>? Findings = null)
{
    /// <summary>
    /// Composes the single user message for <paramref name="role"/> — only the artifacts that role acts
    /// on.
    ///
    /// <para>The omissions are deliberate and each has a reason. The Painter does not get the spec's
    /// prose because it must not re-derive intent, only draw what the code already computes. The Fixer
    /// does not get the spec at all: its remedy says what to change, and a Fixer that can see the
    /// original intent starts redesigning instead of repairing. The Reviewer does get the spec, because
    /// its whole job is comparing intent against implementation.</para>
    /// </summary>
    public string ComposeFor(AgentRole role)
    {
        var text = new StringBuilder();

        switch (role)
        {
            case AgentRole.Interviewer:
                Section(text, "BRIEF", Brief);
                break;

            case AgentRole.Quant:
                Section(text, "SPECIFICATION", Spec ?? Brief);
                break;

            case AgentRole.Coder:
                Section(text, "SPECIFICATION", Spec ?? Brief);
                Section(text, "MATHEMATICS", Maths);
                Code(text);
                break;

            case AgentRole.Painter:
                Code(text);
                break;

            case AgentRole.Fixer:
                Code(text);
                Diagnostics(text);
                break;

            case AgentRole.Reviewer:
                Section(text, "SPECIFICATION", Spec ?? Brief);
                Code(text);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, "No context shape for this role.");
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Folds an agent's reply back in, replacing what it owns.</summary>
    public AgentContext With(AgentRole role, string reply, IReadOnlyList<StrategyFile> files) => role switch
    {
        AgentRole.Interviewer => this with { Spec = reply },
        AgentRole.Quant => this with { Maths = reply },

        // Code replaces code. Keeping the old alongside is how a model ends up repairing a version that
        // no longer exists, and it doubles what every later turn pays to carry.
        _ when files.Count > 0 => this with { Files = files },
        _ => this,
    };

    /// <summary>Attaches the current verdict, dropping the previous one.</summary>
    public AgentContext With(VerificationReport report) =>
        this with { Findings = report.Findings.Count > 0 ? report.Findings : null };

    private static void Section(StringBuilder text, string heading, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        text.Append("## ").AppendLine(heading).AppendLine().AppendLine(body.Trim()).AppendLine();
    }

    private void Code(StringBuilder text)
    {
        if (Files is not { Count: > 0 }) return;

        text.AppendLine("## CURRENT CODE").AppendLine();
        foreach (var file in Files)
        {
            text.AppendLine("```csharp");
            text.Append("// file: ").AppendLine(file.Name);
            text.AppendLine(file.Content.TrimEnd());
            text.AppendLine("```").AppendLine();
        }
    }

    private void Diagnostics(StringBuilder text)
    {
        if (Findings is not { Count: > 0 }) return;

        text.AppendLine("## WHAT FAILED").AppendLine();
        foreach (var finding in Findings)
        {
            text.Append("- **").Append(finding.Code).Append("** — ").Append(finding.Message);
            if (finding.Remedy is { Length: > 0 } remedy) text.Append(' ').Append(remedy);
            text.AppendLine();
        }

        text.AppendLine();
    }
}
