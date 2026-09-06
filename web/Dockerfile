# syntax=docker/dockerfile:1
# Built from the repo root (see deploy/docker-compose.yml `web` context) because the dashboard depends
# on the @condux/brand workspace package, so the build needs the pnpm workspace, not just web/.
FROM node:22-alpine AS build
WORKDIR /app
RUN corepack enable

# @playwright/test is a dev-only e2e dependency; its postinstall downloads browser binaries, which the
# image build neither needs nor can do (no network for the download / no alpine browser). Skip it so
# install succeeds; browsers are installed on the host for `pnpm e2e`.
ENV PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1

# Workspace manifests first, for layer caching: install only re-runs when a manifest or the lockfile
# changes. Copy the root workspace files plus every member's package.json.
COPY pnpm-workspace.yaml package.json pnpm-lock.yaml ./
COPY web/package.json ./web/package.json
COPY packages/brand/package.json ./packages/brand/package.json
COPY packages/plans/package.json ./packages/plans/package.json
# The link: targets for @condux/nextjs (which itself links @condux/core, @condux/node, @condux/browser
# and @condux/react) must exist before install; the whole chain is built right after (the context
# excludes **/dist, so the host build never leaks in).
COPY sdks/core ./sdks/core
# @condux/node: the nextjs adapter links it for the Node-runtime dependency inventory (ADR-0041).
COPY sdks/js ./sdks/js
COPY sdks/browser ./sdks/browser
COPY sdks/react ./sdks/react
COPY sdks/nextjs ./sdks/nextjs
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile
RUN for pkg in core js browser react nextjs; do ( cd "sdks/$pkg" && pnpm install --ignore-workspace && pnpm run build ); done

# Source for the dashboard and the workspace deps it consumes.
COPY packages/brand ./packages/brand
COPY packages/plans ./packages/plans
COPY web ./web

# NEXT_PUBLIC_* is baked into the client bundle at build time, so the API base URL must be a build arg
# (a compose runtime `environment` would be too late). Empty default = same-origin (production).
ARG NEXT_PUBLIC_CONDUX_API_URL=""
ENV NEXT_PUBLIC_CONDUX_API_URL=$NEXT_PUBLIC_CONDUX_API_URL
# The DSN shown in the dashboard is reconstructed client-side from the ingest base URL, also baked here.
# Empty default falls back to the dev relay (localhost:9010); prod passes the public relay host.
ARG NEXT_PUBLIC_CONDUX_INGEST_URL=""
ENV NEXT_PUBLIC_CONDUX_INGEST_URL=$NEXT_PUBLIC_CONDUX_INGEST_URL
# Client-side Condux on Condux (#75): the DSN the dashboard's browser errors report under. Public-host
# form (the DSN's key is public by design); events actually travel through the same-origin /monitoring
# tunnel, so the host in it is never contacted by the browser. Empty = client reporting off.
ARG NEXT_PUBLIC_CONDUX_DSN=""
ENV NEXT_PUBLIC_CONDUX_DSN=$NEXT_PUBLIC_CONDUX_DSN
# Where this deployment's terms of service and privacy policy are published. Signup shows an acceptance
# notice linking them, which is what makes "creating an account accepts these terms" actually true.
# Empty default = a self-hosted install, which has no terms of OURS to accept (the operator runs the
# software under the FSL), so the notice is hidden rather than pointing customers at condux.ai. A
# self-hoster with their own terms sets this to their own site.
ARG NEXT_PUBLIC_CONDUX_LEGAL_URL=""
ENV NEXT_PUBLIC_CONDUX_LEGAL_URL=$NEXT_PUBLIC_CONDUX_LEGAL_URL
# The release the dashboard reports its own errors under (Condux on Condux, #75) — the deploy's commit
# sha, baked so client events and the uploaded source maps carry the same release string (ADR-0028).
ARG NEXT_PUBLIC_CONDUX_RELEASE=""
ENV NEXT_PUBLIC_CONDUX_RELEASE=$NEXT_PUBLIC_CONDUX_RELEASE
# Where withConduxConfig uploads the client source maps (the Condux app/API base URL). With the URL, the
# release and the token all present the maps upload and leave the image; any missing and the build is
# stamp-only, with the maps deploying publicly (fine for this FSL-public dashboard, loudly logged).
ARG CONDUX_URL=""
ENV CONDUX_URL=$CONDUX_URL
# The scoped release token rides a BuildKit secret, never an ARG: an ARG lands in `docker history` for
# anyone who can pull the image. The mount is optional — no secret provided means the file is absent and
# the build stays stamp-only.
RUN --mount=type=secret,id=condux_release_token \
    CONDUX_RELEASE_TOKEN="$(cat /run/secrets/condux_release_token 2>/dev/null || true)" \
    pnpm --filter @condux/web build

FROM node:22-alpine AS run
WORKDIR /app
RUN corepack enable
ENV NODE_ENV=production
# The built workspace (web/.next, the linked brand package, node_modules) as produced above.
COPY --from=build /app ./
EXPOSE 3000
CMD ["pnpm", "--filter", "@condux/web", "start"]
