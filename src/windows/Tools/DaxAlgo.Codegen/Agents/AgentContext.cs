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
    public AgentContext With(AgentRole role, string reply, IReadOnlyList<StrategyFile> files)
    {
        // Code replaces code, WHOEVER wrote it, and this has to happen before the role arms rather than
        // as one of them.
        //
        // It used to be the third arm of a switch whose first two matched on role, so a role that both
        // owns an artifact and may write code lost the code: AgentPrompts.WritesCode(Quant) is true and
        // its prompt says "you may write the computation", but the Quant arm matched first and kept only
        // the prose. The Interviewer hit the same edge from the other side — a fenced sketch inside a
        // specification was compiled as a unit, failed, and then could not be repaired, because the arm
        // that kept the Spec had dropped the files the Fixer would have needed.
        var next = files.Count > 0 ? this with { Files = files } : this;

        return role switch
        {
            AgentRole.Interviewer => next with { Spec = reply },
            AgentRole.Quant => next with { Maths = reply },
            _ => next,
        };
    }

    /// <summary>
    /// Folds the user's own next message into the brief, so a resumed run remembers what it is building.
    ///
    /// <para>The brief is the one artifact the user writes, and an interview is a conversation held in
    /// it. Before this the loop rebuilt its context from the latest message alone, so the second turn of
    /// every interview was handed <i>"approved, now start building"</i> as the entire brief — no
    /// instrument, no rules, nothing to build — and the only sane reply to that is another question.</para>
    ///
    /// <para>Appending rather than replacing keeps the artifacts-not-transcript discipline intact: this
    /// grows with the user's own words, which are short, and never with the model's replies, which are
    /// what made a transcript quadratic.</para>
    /// </summary>
    public AgentContext WithUserReply(string reply) =>
        string.IsNullOrWhiteSpace(reply)
            ? this
            : this with
            {
                Brief = string.Join(
                    Environment.NewLine + Environment.NewLine,
                    Brief.TrimEnd(),
                    "## THE USER THEN SAID",
                    reply.Trim()),
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
