namespace Condux.ControlPlane.Contracts;

// Request and response shapes for tracker tickets (ADR-0053): connecting Linear, a project's team, and an
// issue's ticket. States and failures travel as the stored integers of Condux.Core.Tickets, as statuses do
// elsewhere in this API.

internal sealed record LinearConnectRequest(string? ReturnPath);

internal sealed record LinearConnectResponse(string AuthorizeUrl);

/// <summary>The org's Linear connection, and the projects a disconnect would leave without a team.</summary>
internal sealed record LinearOrgStatusResponse(
    bool Connected, string? WorkspaceName, bool Broken, IReadOnlyList<string> MappedProjects);

/// <summary>Everything the project's Linear tab draws from: the org's connection, this project's team, and
/// how many tickets it has opened on its own today against its daily limit.</summary>
internal sealed record LinearProjectStatusResponse(
    bool Connected, string? WorkspaceName, bool Broken, LinearMappingResponse? Mapping,
    int AutoCreatedToday, int AutoCreateDailyLimit);

internal sealed record LinearMappingResponse(
    string TeamId, string TeamName, bool NeedsAttention, bool AutoCreate, IReadOnlyList<LinearMappingLabelResponse> Labels);

/// <summary>A label the project puts on its tickets, with the name it had when chosen.</summary>
internal sealed record LinearMappingLabelResponse(string Id, string Name);

/// <summary>A label the mapped team's tickets can carry, live from Linear, with the group it belongs to:
/// an issue carries at most one label of a group.</summary>
internal sealed record LinearLabelResponse(string Id, string Name, string? GroupId, string? GroupName);

internal sealed record LinearTeamResponse(string Id, string Key, string Name);

/// <summary>The project's whole Linear choice, replacing what was there: a label left out is removed, and
/// <paramref name="AutoCreate"/> defaults to off.</summary>
internal sealed record SetLinearMappingRequest(string TeamId, IReadOnlyList<string?>? LabelIds = null, bool AutoCreate = false);

internal sealed record LinearDisconnectResponse(bool Revoked);

/// <summary>An issue's ticket. <paramref name="State"/> is a <c>TicketState</c>; <paramref name="Failure"/>
/// is a <c>TicketFailure</c>, set only when the state is failed; <paramref name="Automatic"/> says the
/// project's auto-create setting opened it rather than a person.</summary>
internal sealed record IssueTicketResponse(
    Guid Id, int Provider, int State, int? Failure, string? Key, string? Url, bool Automatic);
