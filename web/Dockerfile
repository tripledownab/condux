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
# The link: target for @condux/nextjs (which itself links @condux/core + @condux/browser) must exist
# before install; the whole chain is built right after (the context excludes **/dist, so the host build
# never leaks in).
COPY sdks/core ./sdks/core
COPY sdks/browser ./sdks/browser
COPY sdks/nextjs ./sdks/nextjs
RUN --mount=type=cache,id=pnpm-store,target=/root/.local/share/pnpm/store \
    pnpm install --frozen-lockfile
RUN for pkg in core browser nextjs; do ( cd "sdks/$pkg" && pnpm install --ignore-workspace && pnpm run build ); done

# Source for the dashboard and the workspace deps it consumes.
COPY packages/brand ./packages/brand
COPY web ./web

# NEXT_PUBLIC_* is baked into the client bundle at build time, so the API base URL must be a build arg
# (a compose runtime `environment` would be too late). Empty default = same-origin (production).
ARG NEXT_PUBLIC_CONDUX_API_URL=""
ENV NEXT_PUBLIC_CONDUX_API_URL=$NEXT_PUBLIC_CONDUX_API_URL
# The DSN shown in the dashboard is reconstructed client-side from the ingest base URL, also baked here.
# Empty default falls back to the dev relay (localhost:9010); prod passes the public relay host.
ARG NEXT_PUBLIC_CONDUX_INGEST_URL=""
ENV NEXT_PUBLIC_CONDUX_INGEST_URL=$NEXT_PUBLIC_CONDUX_INGEST_URL
RUN pnpm --filter @condux/web build

FROM node:22-alpine AS run
WORKDIR /app
RUN corepack enable
ENV NODE_ENV=production
# The built workspace (web/.next, the linked brand package, node_modules) as produced above.
COPY --from=build /app ./
EXPOSE 3000
CMD ["pnpm", "--filter", "@condux/web", "start"]
