// The tabs on a project's page. Its own file because a tab's value is also part of a URL: a connect flow
// that leaves the app returns to ?tab=<value>, so the panel that starts the flow names the same value the
// page reads when it opens.
export enum ProjectTab {
  GitHub = "github",
  Releases = "releases",
  Dsn = "dsn",
  Mcp = "mcp",
  Linear = "linear",
}
