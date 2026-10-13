import type { CodeMapping, RepoLink } from "@/src/api/generated/model";
import type { ParsedFrame } from "./event-payload";

// Maps a stack frame to its GitHub blob URL through the project's code mappings (#89): the longest
// stackRoot prefixing the frame's path wins, so an empty root for a whole source tree and a narrower
// rule for one module can coexist whatever order they arrive in. The mapped path lands on the repo
// link's default branch and the line number anchors the view. Null when nothing maps (no repo
// connected, or a path outside every mapping).
export function frameGithubUrl(
  frame: ParsedFrame,
  repos: RepoLink[],
  mappings: CodeMapping[],
): string | null {
  if (!frame.filename) {
    return null;
  }
  const path = frame.filename.replace(/^\.\//, "");
  let best: { stackRoot: string; sourceRoot: string; repo: RepoLink } | null = null;
  for (const mapping of mappings) {
    const stackRoot = mapping.stackRoot.replace(/^\.\//, "");
    const repo = repos.find((candidate) => candidate.id === mapping.repoLinkId);
    if (
      path.startsWith(stackRoot) &&
      repo !== undefined &&
      (best === null || stackRoot.length > best.stackRoot.length)
    ) {
      best = { stackRoot, sourceRoot: mapping.sourceRoot, repo };
    }
  }
  if (best === null) {
    return null;
  }
  const mapped = `${best.sourceRoot}${path.slice(best.stackRoot.length)}`.replace(/^\/+/, "");
  const line = frame.lineno > 0 ? `#L${frame.lineno}` : "";
  return `https://github.com/${best.repo.repoFullName}/blob/${best.repo.defaultBranch}/${mapped}${line}`;
}
