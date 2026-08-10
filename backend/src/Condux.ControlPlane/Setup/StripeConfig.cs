using Condux.Core.Plans;

namespace Condux.ControlPlane.Setup;

/// <summary>
/// Stripe billing configuration, from env (<c>CONDUX_STRIPE_*</c>). Opt-in: <see cref="Enabled"/> only
/// when the secret key, webhook signing secret, and at least one self-serve price id are set. A partial
/// config fails fast (a likely typo), matching the GitHub/Google configs. When unset, the
/// <c>/api/orgs/{id}/billing/*</c> + <c>/api/stripe/webhook</c> routes 404. Self-serve checkout covers
/// Team + Business; Free needs no checkout and Enterprise is contact-sales (<see cref="PlanCatalog"/>).
/// </summary>
internal sealed record StripeConfig(
    string? SecretKey, string? WebhookSecret, IReadOnlyDictionary<Tier, string> Prices)
{
    public bool Enabled => !string.IsNullOrEmpty(SecretKey) && !string.IsNullOrEmpty(WebhookSecret)
        && Prices.Count > 0;

    /// <summary>The plan tier a Stripe price id maps to, or null when it isn't one of our configured prices.</summary>
    public Tier? TierForPrice(string? priceId)
    {
        if (priceId is not null)
        {
            foreach (var (tier, price) in Prices)
            {
                if (price == priceId)
                {
                    return tier;
                }
            }
        }

        return null;
    }

    public string? PriceForTier(Tier tier) => Prices.TryGetValue(tier, out var p) ? p : null;

    public static StripeConfig FromEnv(IConfiguration cfg)
    {
        var secretKey = cfg["CONDUX_STRIPE_SECRET_KEY"];
        var webhookSecret = cfg["CONDUX_STRIPE_WEBHOOK_SECRET"];
        var team = cfg["CONDUX_STRIPE_PRICE_TEAM"];
        var business = cfg["CONDUX_STRIPE_PRICE_BUSINESS"];

        var anySet = new[] { secretKey, webhookSecret, team, business }.Any(v => !string.IsNullOrEmpty(v));
        if (!anySet)
        {
            return new StripeConfig(null, null, new Dictionary<Tier, string>()); // feature off
        }

        if (string.IsNullOrEmpty(secretKey) || string.IsNullOrEmpty(webhookSecret)
            || (string.IsNullOrEmpty(team) && string.IsNullOrEmpty(business)))
        {
            throw new InvalidOperationException(
                "Stripe billing is partially configured. Set CONDUX_STRIPE_SECRET_KEY, "
                + "CONDUX_STRIPE_WEBHOOK_SECRET, and at least one price id "
                + "(CONDUX_STRIPE_PRICE_TEAM / CONDUX_STRIPE_PRICE_BUSINESS), or unset them all.");
        }

        var prices = new Dictionary<Tier, string>();
        if (!string.IsNullOrEmpty(team))
        {
            prices[Tier.Team] = team;
        }

        if (!string.IsNullOrEmpty(business))
        {
            prices[Tier.Business] = business;
        }

        return new StripeConfig(secretKey, webhookSecret, prices);
    }
}
