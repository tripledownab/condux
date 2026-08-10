using Condux.Core.Plans;
using Xunit;

namespace Condux.Core.Tests;

public class ModelPricingTests
{
    [Fact]
    public void EstimateUsd_PricesInputAndOutputAtTheModelsRate()
    {
        // Opus 4.8 is $5 / 1M input, $25 / 1M output: 2M in + 1M out = $10 + $25 = $35.
        Assert.Equal(35m, ModelPricing.EstimateUsd("claude-opus-4-8", 2_000_000, 1_000_000));

        // Sub-million usage prices proportionally: 2000 in @ $5/1M = $0.01, 400 out @ $25/1M = $0.01.
        Assert.Equal(0.02m, ModelPricing.EstimateUsd("claude-opus-4-8", 2_000, 400));
    }

    [Fact]
    public void EstimateUsd_IsCaseInsensitiveOnTheModelId()
    {
        Assert.Equal(
            ModelPricing.EstimateUsd("claude-opus-4-8", 1_000_000, 0),
            ModelPricing.EstimateUsd("CLAUDE-OPUS-4-8", 1_000_000, 0));
    }

    [Fact]
    public void EstimateUsd_PricesMainstreamByoModels_SoBudgetsCanMeasureThem()
    {
        // BYO on a mainstream OpenAI model is priced (gpt-4o $2.50/$10 per 1M) so a budget cap can measure
        // it: 1M in + 1M out = $2.50 + $10 = $12.50 (ADR-0020/0027).
        Assert.Equal(12.5m, ModelPricing.EstimateUsd("gpt-4o", 1_000_000, 1_000_000));
    }

    [Fact]
    public void EstimateUsd_UnknownModel_IsNull_NotGuessed()
    {
        // A genuinely custom / self-hosted model has no published rate, so we do not price it — the caller
        // shows the token counts and leaves cost blank rather than applying a wrong rate.
        Assert.Null(ModelPricing.EstimateUsd("ollama/llama3", 1_000_000, 1_000_000));
        Assert.Null(ModelPricing.RateFor("some-self-hosted-model"));
    }

    [Fact]
    public void EstimateUsd_ZeroTokens_IsZero_ForAPricedModel()
    {
        // A no-cost run of a priced model (e.g. it failed before any output) is $0, not null.
        Assert.Equal(0m, ModelPricing.EstimateUsd("claude-sonnet-4-6", 0, 0));
    }
}
