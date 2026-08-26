using Condux.Core.Plans;
using Condux.Core.Stats;
using Condux.Storage.ClickHouse;
using Condux.Storage.Postgres;

// Request/response DTOs for the control-plane API. Deliberately in the GLOBAL namespace so the .NET
// OpenAPI schema names (and therefore the generated TS client) match exactly — moving them under a
// namespace would be a needless churn risk to the wire contract.

// Requests (bound from JSON bodies).
// No Tier: the caller does not get to choose its own plan. Every org is created Free and only the
// signature-verified Stripe webhook moves it (ADR-0026). Accepting a client tier let any signed-up
// user POST tier=3 and take Enterprise: unlimited ingest, uncapped Conductor runs, and — with no BYO
// key configured — those runs billed to the platform's own model key.
internal sealed record CreateOrgRequest(string Slug, string Name);
// The org's AI-fix settings, submitted together from Settings → General. AiFixCostCapUsd is the optional
// monthly Conductor spend ceiling (null clears it — no cap; #120 budgets).
// FixExecution is nullable and means "leave it as it is" when omitted (ADR-0033 slice 4c). It has to be:
// a client that does not know the field yet — an older dashboard, a script, anything written before
// runners existed — would otherwise send no value, bind 0, and silently move a self-hosting org's work
// back onto our compute as a side effect of changing something else.
internal sealed record UpdateOrgRequest(int AiFixMode, decimal? AiFixCostCapUsd, int? FixExecution);
internal sealed record CreateProjectRequest(string Name, string? Platform);
internal sealed record UpdateProjectRequest(string Name, string? Platform);
internal sealed record CreateKeyRequest(string? Label);
internal sealed record UpdateKeyRequest(string Label);
internal sealed record Credentials(string Email, string Password);
internal sealed record UpdateMemberRoleRequest(string Role);
internal sealed record CreateInviteRequest(string Email, string Role);
internal sealed record AcceptInviteRequest(string Token);
internal sealed record LinkRepoRequest(string RepoFullName, string? DefaultBranch);
internal sealed record CodeMappingRequest(string StackRoot, string? SourceRoot);
// Which linked repo + base branch a fix should target. Both optional: omit to default to the project's
// sole/first linked repo and that repo's default branch (a project may link several repos, and a fix can
// target any branch of a repo — the branch is chosen per run, not stored).
internal sealed record RequestFixRequest(Guid? RepoId, string? BaseBranch);
internal sealed record RecordReleaseRequest(Guid RepoLinkId, string Version, string CommitSha);
// Scoped release tokens: mint (returns the raw token once), list (never the raw token), and the
// token-authed release record used by CI (repo optional: defaults to the project's sole linked repo).
internal sealed record CreateReleaseTokenRequest(string Name);
internal sealed record MintedReleaseTokenResponse(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);
internal sealed record ReleaseTokenResponse(
    Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);
internal sealed record RecordReleaseViaTokenRequest(string Version, string CommitSha, string? Repo);

// Runner tokens (ADR-0033 slice 4). Org-scoped rather than project-scoped: a runner serves whatever work
// its org produces, across every project in it.
internal sealed record CreateRunnerTokenRequest(string Label);
internal sealed record MintedRunnerTokenResponse(Guid Id, string Label, string Token, DateTimeOffset CreatedAt);
internal sealed record RunnerTokenResponse(
    Guid Id, string Label, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);

// The lease protocol. Deliberately small and additive-only: a customer's runner lags our deploys, so a
// field added here must never be required by an older runner, and one removed breaks every runner at once.
// LeaseId is what proves the caller holds this job. Runners in one org share a token, so a name they
// choose would let any of them act on another's work.
// Ref is the work's display identifier (the issue id, or the GHSA id of a CVE bump) for branch names
// and pull-request copy. IssueId stays for runners that predate it; a CVE bump carries IssueId 0.
internal sealed record LeasedJobResponse(
    Guid FixId, long IssueId, string RepoFullName, string BaseBranch, string Prompt,
    IReadOnlyList<string> ScopedPaths, DateTimeOffset LeaseExpiresAt, string LeaseId, string Ref);
internal sealed record HeartbeatRequest(string LeaseId);
// Model is reported rather than assigned: a runner resolves its own, so until it answers we do not know
// which one ran, and an empty model on the run would price and display as if none had.
internal sealed record ReportRequest(
    string LeaseId, int Status, string Branch, string PrUrl, string Summary, string Model,
    long InputTokens, long OutputTokens);

// Scoped MCP tokens (ADR-0029): a per-project read-only credential an AI agent presents as a bearer to
// the MCP endpoint. Mirrors the release-token DTOs; the raw token is returned once, on mint.
internal sealed record CreateMcpTokenRequest(string Name);
internal sealed record MintedMcpTokenResponse(Guid Id, string Name, string Token, DateTimeOffset CreatedAt);
internal sealed record McpTokenResponse(
    Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool Revoked);

// Source-map artifacts (ADR-0028): the uploaded map's index row, returned to the CI uploader.
internal sealed record SourceMapArtifactResponse(
    Guid Id, string Release, string? Dist, string? DebugId, string Filename, long ByteSize, DateTimeOffset CreatedAt);
internal sealed record ArchiveFixRequest(bool Archived);
internal sealed record UnviewedFixCountResponse(int Count);
internal sealed record SetLlmConfigRequest(string Provider, string Model, string? BaseUrl, string ApiKey);
internal sealed record LlmConfigResponse(string Provider, string Model, string BaseUrl, DateTimeOffset UpdatedAt);
internal sealed record ListLlmModelsRequest(string Provider, string? BaseUrl, string? ApiKey);
// Enterprise SSO (#72): the org's IdP config. Protocol is the SsoProtocol enum (0 OIDC, 1 SAML); each
// protocol fills its own fields and issuer is shared (the OIDC issuer / the SAML IdP entity ID). The OIDC
// client secret is write-only (sealed at rest, never echoed back), so it rides the request but not the
// response — mirrors the BYO-key SetLlmConfig/LlmConfig pair. The SAML certificate is the IdP's public
// signing certificate (not a secret), so it echoes back for the admin to verify.
internal sealed record SetSsoConfigRequest(
    string EmailDomain, string Issuer, int Protocol,
    string? AuthorizationEndpoint, string? TokenEndpoint, string? ClientId, string? ClientSecret,
    string? SamlSsoUrl, string? SamlCertificate);
internal sealed record SsoConfigResponse(
    string EmailDomain, string Issuer, int Protocol,
    string? AuthorizationEndpoint, string? TokenEndpoint, string? ClientId,
    string? SamlSsoUrl, string? SamlCertificate, DateTimeOffset UpdatedAt);
internal sealed record LlmModelsResponse(IReadOnlyList<Condux.ControlPlane.Llm.LlmModel> Models);
internal sealed record CreateAlertRuleRequest(
    string Name, IReadOnlyList<int> Events, IReadOnlyList<int> Levels);
internal sealed record UpdateAlertRuleRequest(
    string Name, IReadOnlyList<int> Events, IReadOnlyList<int> Levels, bool Enabled);
internal sealed record UpdateAlertChannelRequest(int Channel, string Target, string? Template);
internal sealed record AddAlertChannelRequest(int Channel, string Target, string? Template);
// Org-level notification channels (#129): channel is the NotificationChannel transport (1 email, 2 slack,
// 3 webhook), target the address/URL.
internal sealed record AddNotificationChannelRequest(int Channel, string Target);
internal sealed record NotificationChannelResponse(Guid Id, int Channel, string Target);
// The result of a test send to an alert or org-notification channel: Delivered=true when the notifier
// accepted it, else a short reason (channel_not_configured, or the delivery error message).
internal sealed record TestChannelResponse(bool Delivered, string? Error);
// Weekly summary email settings (ADR-0031): the per-org enable flag + schedule (DayOfWeek 0=Sun..6=Sat,
// Hour 0..23 local, Timezone an IANA id). The test response reports whether a live send to the caller was
// delivered (false when SMTP is off).
internal sealed record WeeklySummarySettingsResponse(bool Enabled, int DayOfWeek, int Hour, string Timezone);
internal sealed record UpdateWeeklySummaryRequest(bool Enabled, int DayOfWeek, int Hour, string Timezone);
internal sealed record WeeklySummaryTestResponse(bool Sent, string Recipient);

// Responses (named so the OpenAPI schema — and the generated TS client — is fully typed).
// The org's Conductor usage: spend this month against its cost cap (#120), and the remaining run
// allowance (#128) — RemainingFixes is the runs it can still start this period (monthly or the Free
// lifetime grant), null when UncappedFixes (Enterprise) so the UI shows no count.
internal sealed record AiFixUsageResponse(
    decimal MonthToDateUsd, decimal? CapUsd, int? RemainingFixes, bool UncappedFixes);
internal sealed record AlertChannelResponse(Guid Id, int Channel, string Target, string? Template);
internal sealed record AlertRuleResponse(
    Guid Id, string Name, IReadOnlyList<int> Events, IReadOnlyList<int> Levels, bool Enabled,
    IReadOnlyList<AlertChannelResponse> Channels);
internal sealed record IssueDetailResponse(IssueSummary Issue, IReadOnlyList<StoredEvent> Events);
// A lazy-loaded page of an issue's sampled events for the detail's events table; HasMore drives "load more".
internal sealed record EventsPageResponse(IReadOnlyList<StoredEvent> Events, bool HasMore);
// The server-side issue subset (Sentry-style): a filtered, sorted, paginated page + whether more remain,
// and the facet counts (status→count, level→count) that drive the views + severity rails.
internal sealed record IssuesPageResponse(IReadOnlyList<IssueSummary> Issues, bool HasMore);
internal sealed record IssueCountsResponse(
    IReadOnlyDictionary<int, long> ByStatus, IReadOnlyDictionary<int, long> ByLevel);
internal sealed record UpdateIssueStatusRequest(int Status);
internal sealed record AssignIssueRequest(long? UserId);
// Per-issue collaborative notes: create takes the body; the response carries the note's UUID id and the
// author's email (null if that account was since deleted).
internal sealed record CreateNoteRequest(string Body);
internal sealed record NoteResponse(
    Guid Id, string Body, long? AuthorUserId, string? AuthorEmail, DateTimeOffset CreatedAt);
internal sealed record SavedViewRequest(string Name, string Query, string Sort);
internal sealed record IssueStatsResponse(IReadOnlyList<HistogramBucket> Buckets, int BucketSeconds);
internal sealed record IssueSparkline(Guid IssueId, IReadOnlyList<HistogramBucket> Buckets);
internal sealed record IssueSparklinesResponse(IReadOnlyList<IssueSparkline> Issues, int BucketSeconds);
internal sealed record ProvisionProjectResponse(ProjectRecord Project, DsnKey Key, string Dsn);
internal sealed record CreateKeyResponse(DsnKey Key, string Dsn);
internal sealed record ErrorResponse(string Error);
// Id/Email/IsPlatformAdmin are always the caller's REAL identity. Impersonation is non-null only while a
// platform admin is in a read-only "view as org" session (ADR-0027); the dashboard shows it as a banner.
internal sealed record AuthUserResponse(
    long Id, string Email, bool IsPlatformAdmin, bool Onboarded, ImpersonationStateResponse? Impersonation = null,
    // True only on the login response, meaning the cookie just issued is half-authenticated and the
    // client must complete the challenge. /me never sets it: by the time /me can be reached the session
    // has already resolved, which it cannot do while pending.
    bool MfaRequired = false);
/// <summary>Which external sign-in providers are configured, so the login page shows only enabled ones.</summary>
internal sealed record AuthProvidersResponse(bool Google, bool Sso);

/// <summary>Whether the caller has a second factor, and whether the server can offer one at all.</summary>
internal sealed record MfaStatusResponse(bool Enabled, bool Available, int RemainingRecoveryCodes);

/// <summary>The secret and the otpauth URI, returned once at enrolment and never recoverable after.</summary>
internal sealed record MfaEnrolmentResponse(string Secret, string Uri);

/// <summary>Recovery codes, shown exactly once. Never returned again by any route.</summary>
internal sealed record RecoveryCodesResponse(string[] Codes);

/// <summary>Re-authentication for a route that changes the second factor.</summary>
internal sealed record PasswordConfirmation(string? Password);

internal sealed record MfaConfirmation(string? Password, string? Code);

/// <summary>A TOTP code or a recovery code. One field, because the caller must not distinguish them.</summary>
internal sealed record MfaChallenge(string? Code);
/// <summary>Start a Stripe subscription checkout for a plan tier (e.g. "Team", "Business").</summary>
internal sealed record CheckoutRequest(string Tier);
/// <summary>The hosted Stripe Checkout URL to redirect the browser to.</summary>
internal sealed record CheckoutResponse(string Url);
/// <summary>
/// An org's billing state for the settings page: current tier, whether Stripe is configured at all, the
/// tiers that can be self-served, and whether the org has an active managed subscription.
/// </summary>
internal sealed record BillingStatusResponse(
    string Tier, bool BillingEnabled, IReadOnlyList<string> PurchasableTiers, bool HasSubscription);
internal sealed record OrgMembershipResponse(Org Org, string Role);
internal sealed record OrgMemberResponse(long UserId, string Email, string Role, DateTimeOffset CreatedAt);
internal sealed record InviteResponse(long Id, string Email, string Role, DateTimeOffset ExpiresAt, string Token);
internal sealed record InvitePendingResponse(
    long Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

// Platform super-admin console (read-only). Cross-tenant views behind the platform-admin gate.
internal sealed record PlatformStatsResponse(long Orgs, long Users, long Projects, long Issues);
internal sealed record AdminOrgResponse(
    long Id, string Slug, string Name, int Tier, DateTimeOffset CreatedAt,
    string? OwnerEmail, int MemberCount, int ProjectCount);
// OrgId/OrgName are the user's primary org (earliest membership), so the users list can "view as" it;
// null for a user who belongs to no org (ADR-0027).
internal sealed record AdminUserResponse(
    long Id, string Email, DateTimeOffset CreatedAt, int OrgCount, long? OrgId, string? OrgName);

// Admin management console (ADR-0027): view/edit orgs + members, full billing control, cross-org AI
// spend, audit log, and read-only impersonation. All behind the same platform-admin 404 gate.
// One org as the admin detail page sees it: the list-row header plus settings, billing, and members.
internal sealed record AdminOrgDetailResponse(
    long Id, string Slug, string Name, int Tier, DateTimeOffset CreatedAt, string? OwnerEmail,
    int MemberCount, int ProjectCount, int AiFixMode, decimal? AiFixCostCapUsd,
    AdminBillingResponse Billing, IReadOnlyList<OrgMemberResponse> Members);
// Editable org fields (name + AI-fix settings). Deliberately NO tier — tier is Stripe/webhook owned.
internal sealed record AdminUpdateOrgRequest(string Name, int AiFixMode, decimal? AiFixCostCapUsd);
// The org's subscription as the admin sees it: tier + Stripe linkage + which tiers are purchasable.
internal sealed record AdminBillingResponse(
    string Tier, bool BillingEnabled, bool HasSubscription,
    string? StripeCustomerId, string? StripeSubscriptionId, IReadOnlyList<string> PurchasableTiers);
internal sealed record AdminChangePlanRequest(string Tier);
/// <summary>A Stripe billing-portal session URL to redirect the operator to.</summary>
internal sealed record AdminPortalResponse(string Url);
internal sealed record AdminSpendModelResponse(
    string Model, int RunCount, long InputTokens, long OutputTokens, decimal? CostUsd);
internal sealed record AdminSpendOrgResponse(
    long OrgId, string OrgName, int RunCount, long InputTokens, long OutputTokens, decimal? CostUsd);
// A spend rollup over a window: grand total plus per-model and per-org breakdowns (TotalUsd sums only
// priced models; a BYO/unpriced model contributes tokens but null dollars).
internal sealed record AdminSpendRollupResponse(
    decimal TotalUsd, IReadOnlyList<AdminSpendModelResponse> ByModel,
    IReadOnlyList<AdminSpendOrgResponse> ByOrg);
// One Conductor run for the per-run drill-down. Kind is "issue_fix" or "cve_bump"; Status is FixStatus.
internal sealed record AdminSpendRunResponse(
    Guid Id, long OrgId, string OrgName, string Model, string Kind, int Status,
    long InputTokens, long OutputTokens, decimal? CostUsd, DateTimeOffset CreatedAt);
internal sealed record AdminAuditResponse(
    long Id, long ActorId, string ActorEmail, string Action, long? TargetOrgId, long? TargetUserId,
    System.Text.Json.JsonElement Details, DateTimeOffset CreatedAt);
// The target of an active read-only impersonation session (surfaced on /api/auth/me for the banner).
internal sealed record ImpersonationStateResponse(long OrgId, string OrgSlug, string OrgName);

// GitHub App connect (#61): the URL that starts the install flow, carrying our signed state token.
// The connect flow returns to this in-app path after the install round-trip (defaults to home; a
// non-relative path is rejected server-side). Lets "Connect GitHub" live inside a project and land back there.
internal sealed record GithubConnectRequest(string? ReturnPath);
internal sealed record GithubConnectResponse(string InstallUrl);
// ManageUrl points at the installation's own page on GitHub (repository access, and Uninstall at the
// bottom), so managing the connection does not mean hunting through GitHub's settings. Built server-side
// because only the server knows the app slug, which also keeps the shape of the URL in one place.
internal sealed record GithubInstallationResponse(
    long InstallationId, string? AccountLogin, DateTimeOffset CreatedAt, string ManageUrl);
internal sealed record GithubBranchesResponse(IReadOnlyList<string> Branches);

// The owner/name of every repo the org's installation can reach, so linking a repo is a choice rather
// than typing a string that may not exist or may sit outside what the installation can touch.
internal sealed record GithubRepositoriesResponse(IReadOnlyList<string> Repositories);

// A live check of the org's GitHub connection, distinct from the stored row: the listing endpoint only
// reports what we recorded, so a link lost locally reads as never connected and one revoked on GitHub
// still reads as connected. Status is NotConnected, Healthy, Revoked or Unreachable, and the three
// failure states are kept apart so the UI can say what to actually do about each.
internal sealed record GithubHealthResponse(
    string Status, long? InstallationId, string? AccountLogin, string? Detail);

// Connecting when the authorizing user can reach several installations of the app: the callback cannot
// guess which account the org meant, so it hands the choice back through the browser in a signed token
// (the ids came from GitHub and must not be editable) and the user picks one.
internal sealed record GithubSelectionResponse(IReadOnlyList<GithubSelectionOption> Installations);
internal sealed record GithubSelectionOption(long InstallationId, string AccountLogin);
internal sealed record GithubSelectRequest(string Selection, long InstallationId);
internal sealed record UpdateRepoRequest(string DefaultBranch);

// Start a Conductor CVE-bump run (#117 slice 2): the client supplies only the advisory id; the server
// re-fetches the authoritative package + fixed version from GitHub, so a stale/forged body can't drive it.
internal sealed record StartCveFixRequest(string GhsaId);
