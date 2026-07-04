using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// Canonical System.Text.Json settings for serializing <see cref="ModelArtifact"/> (and its option
/// blobs). One shared instance so the registry, any export path, and tests never drift.
///
/// <para><see cref="JsonNumberHandling.AllowNamedFloatingPointLiterals"/> is essential: a model's
/// rolling scoreboard (and some running scalars) can legitimately be <c>NaN</c> or ±∞ before enough
/// data accrues — a baseline MAE with nothing scored yet, a variance not yet observed — and the
/// default serializer throws on non-finite doubles. Named literals write them as <c>"NaN"</c> /
/// <c>"Infinity"</c> and read them back.</para>
/// </summary>
public static class ModelArtifactJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };
}
