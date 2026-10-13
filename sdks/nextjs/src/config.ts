/**
 * withConduxConfig (ADR-0028): wrap a Next.js config so production builds come out symbolicatable, client
 * and server. It turns on source maps for both and, from Next's post-compile hook, stamps every emitted
 * chunk with a stable debugId, uploads the maps to Condux (when CONDUX_RELEASE_TOKEN + a URL + a release are
 * configured, e.g. in CI), and keeps the client maps out of the deployed output. Server maps stay, since
 * nothing serves them. Built on
 * `compiler.runAfterProductionCompile`, which Next fires for webpack and Turbopack builds alike; an
 * older Next that lacks the hook simply leaves the maps on disk for the condux-sourcemaps CLI.
 *
 * Know what a build WITHOUT upload credentials does: the maps stay in the output (a map is never
 * destroyed before it is stored somewhere), and everything under the build's static/ directory is
 * served publicly — so the app's original source ships with it until a later step uploads and removes
 * the maps, or you deploy with the credentials set. The build log says so when it happens.
 *
 * ```js
 * // next.config.mjs — server-only import, keep it out of client code
 * import { withConduxConfig } from "@condux/nextjs/config";
 * export default withConduxConfig({ ...yourConfig });
 * ```
 */

import { isAbsolute, join } from "node:path";
import { type UploadTarget, processClientBuild } from "./build.ts";
import { processServerBuild } from "./server-build.ts";

export interface ConduxConfigOptions {
  /** The release the maps belong to (must match what the app reports). Default CONDUX_RELEASE, then
   * NEXT_PUBLIC_CONDUX_RELEASE. Without one, maps are stamped but not uploaded. */
  release?: string;
  /** Optional distribution, when one release ships more than one build. Default CONDUX_DIST. */
  dist?: string;
  /** The Condux app/API base URL uploads go to, e.g. https://app.condux.ai. Default CONDUX_URL. */
  url?: string;
  /** Skip everything (no source maps forced, no stamping): an escape hatch for a build that must come
   * out byte for byte as an unwrapped one does. */
  disable?: boolean;
  /** Set false to leave the server output alone: no server maps forced, stamped or uploaded. Server
   * maps carry the server code's source, like client maps carry the client's. Default true. */
  serverMaps?: boolean;
}

// The structural slice of Next's config this wrapper touches — deliberately not a `next` dependency, so
// the SDK never pins the consumer's Next version.
interface CompileMetadata {
  projectDir: string;
  distDir: string;
}

export interface NextConfigLike {
  productionBrowserSourceMaps?: boolean;
  experimental?: {
    serverSourceMaps?: boolean;
    [key: string]: unknown;
  };
  compiler?: {
    runAfterProductionCompile?: (metadata: CompileMetadata) => Promise<void>;
    [key: string]: unknown;
  };
  [key: string]: unknown;
}

export function withConduxConfig(
  nextConfig: NextConfigLike = {},
  options: ConduxConfigOptions = {},
): NextConfigLike {
  if (options.disable) {
    return nextConfig;
  }

  const userHook = nextConfig.compiler?.runAfterProductionCompile;
  return {
    ...nextConfig,
    // The maps are the input to symbolication; the post-compile hook keeps the client ones out of the
    // deployed output again.
    productionBrowserSourceMaps: true,
    // webpack builds need this for server maps, at a build-memory cost Next's memory guide notes. A
    // Turbopack build wrote them without it. An explicit choice stands.
    ...(options.serverMaps === false
      ? {}
      : {
          experimental: {
            ...nextConfig.experimental,
            serverSourceMaps: nextConfig.experimental?.serverSourceMaps ?? true,
          },
        }),
    compiler: {
      ...nextConfig.compiler,
      async runAfterProductionCompile(metadata: CompileMetadata): Promise<void> {
        await userHook?.(metadata);
        const distDir = isAbsolute(metadata.distDir)
          ? metadata.distDir
          : join(metadata.projectDir, metadata.distDir);
        const upload = uploadTarget(options);
        await processClientBuild({ distDir, upload });
        if (options.serverMaps !== false) {
          await processServerBuild({ distDir, upload });
        }
      },
    },
  };
}

/** The upload destination, or undefined when any credential is missing (stamp-only build). */
function uploadTarget(options: ConduxConfigOptions): UploadTarget | undefined {
  const url = options.url ?? process.env.CONDUX_URL;
  const token = process.env.CONDUX_RELEASE_TOKEN;
  const release =
    options.release ?? process.env.CONDUX_RELEASE ?? process.env.NEXT_PUBLIC_CONDUX_RELEASE;
  if (!url || !token || !release) {
    return undefined;
  }
  return { url, token, release, dist: options.dist ?? process.env.CONDUX_DIST };
}
