namespace Condux.Core.Plans;

/// <summary>
/// Published per-model token rates for the Conductor's first-party (Anthropic) models — the single
/// source that turns audited token usage into a dollar cost (#120). Rates are USD per million tokens.
/// A model that is not in the table (e.g. a bring-your-own OpenAI-compatible model, billed on the org's
/// own account) has no rate here, so its cost is reported as unknown (null) rather than guessed at the
/// wrong rate; the token counts are still shown. Cost is derived at read time, so correcting a rate here
/// reprices history with no backfill.
/// </summary>
public static class ModelPricing
{
    /// <summary>USD per one million input / output tokens for a model.</summary>
    public readonly record struct Rate(decimal InputPerMillion, decimal OutputPerMillion);

    // The models the Conductor runs on its own key (ADR-0016 default is Opus 4.8), plus mainstream
    // BYO models so a bring-your-own-key org's spend can be priced for its budget cap (ADR-0020/0027).
    // Rates are the providers' published list prices; keep them here only, never scattered across
    // services. A model not in the table (an exotic / self-hosted endpoint) still yields cost null.
    // TODO: verify the OpenAI rates against openai.com/api/pricing when calibrating.
    private static readonly IReadOnlyDictionary<string, Rate> Rates =
        new Dictionary<string, Rate>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-8"] = new(5m, 25m),
            ["claude-opus-4-7"] = new(5m, 25m),
            ["claude-opus-4-6"] = new(5m, 25m),
            ["claude-sonnet-4-6"] = new(3m, 15m),
            ["claude-haiku-4-5"] = new(1m, 5m),
            ["claude-fable-5"] = new(10m, 50m),
            // Mainstream OpenAI models (BYO). Published USD/1M list prices; calibrate when they change.
            ["gpt-4o"] = new(2.5m, 10m),
            ["gpt-4o-mini"] = new(0.15m, 0.6m),
            ["gpt-4.1"] = new(2m, 8m),
            ["gpt-4.1-mini"] = new(0.4m, 1.6m),
            ["o3"] = new(2m, 8m),
            ["o4-mini"] = new(1.1m, 4.4m),
        };

    /// <summary>The published rate for a model, or null when we do not price it (a BYO model).</summary>
    public static Rate? RateFor(string model) => Rates.TryGetValue(model, out var rate) ? rate : null;

    /// <summary>The USD cost of a run's token usage, or null when the model has no known rate.</summary>
    public static decimal? EstimateUsd(string model, long inputTokens, long outputTokens) =>
        RateFor(model) is { } rate
            ? (inputTokens / 1_000_000m * rate.InputPerMillion)
                + (outputTokens / 1_000_000m * rate.OutputPerMillion)
            : null;
}
