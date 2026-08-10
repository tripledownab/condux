import type { CodeMapping, RepoLink } from "@/src/api/generated/model";
import type { ParsedFrame } from "./event-payload";

// Maps a stack frame to its GitHub blob URL through the project's code mappings (#89): the first
// mapping whose stackRoot prefixes the frame's path wins, the mapped path lands on the repo link's
// default branch and the line number anchors the view. Null when nothing maps (no repo connected,
// or a path outside every mapping).
export function frameGithubUrl(
  frame: ParsedFrame,
  repos: RepoLink[],
  mappings: CodeMapping[],
): string | null {
  if (!frame.filename) {
    return null;
  }
  const path = frame.filename.replace(/^\.\//, "");
  for (const mapping of mappings) {
    const stackRoot = mapping.stackRoot.replace(/^\.\//, "");
    if (!path.startsWith(stackRoot)) {
      continue;
    }
    const repo = repos.find((candidate) => candidate.id === mapping.repoLinkId);
    if (repo === undefined) {
      continue;
    }
    const mapped = `${mapping.sourceRoot}${path.slice(stackRoot.length)}`.replace(/^\/+/, "");
    const line = frame.lineno > 0 ? `#L${frame.lineno}` : "";
    return `https://github.com/${repo.repoFullName}/blob/${repo.defaultBranch}/${mapped}${line}`;
  }
  return null;
}
