namespace Condux.ControlPlane.Setup;

/// <summary>Whether the secret store (BYO-key registry, #65) is configured. Opt-in: on only when
/// <c>CONDUX_SECRET_KEY</c> (the base64 AES-256 master key) is set, so the
/// <c>/api/orgs/{id}/llm-config</c> routes 404 otherwise — the same opt-in shape as the GitHub App.</summary>
public sealed record SecretsConfig(bool Enabled);
