# syntax=docker/dockerfile:1
# ONE build for every service (compose targets: relay / controlplane / consumer / conductor / symbolizer,
# plus the customer-hosted runner, which is built from here but deployed by the customer rather than by us).
# Built from the REPO ROOT context (not backend/): Condux.Telemetry references the standalone .NET SDK at
# sdks/dotnet/Condux.Sdk (the services dogfood it via ConduxSelfReporter, #75), which sits outside
# backend/. The repo layout is mirrored under /src so that project's relative reference resolves. The
# web image already builds from the repo root for the same reason (a sibling workspace dependency).
# Layer discipline keeps the build cache small and fast:
#   1. manifests only -> restore, with the NuGet store in a shared cache mount (never in layers);
#   2. source -> publish every app in one stage, so shared projects compile once, not once per app;
#   3. thin runtime stages that only copy their own publish output.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Manifests first: restore re-runs only when a csproj/sln/props changes, not on every source edit.
COPY backend/Condux.sln backend/Directory.Build.props backend/
COPY backend/src/Condux.Agent/Condux.Agent.csproj backend/src/Condux.Agent/
COPY backend/src/Condux.Conductor/Condux.Conductor.csproj backend/src/Condux.Conductor/
COPY backend/src/Condux.Consumer/Condux.Consumer.csproj backend/src/Condux.Consumer/
COPY backend/src/Condux.ControlPlane/Condux.ControlPlane.csproj backend/src/Condux.ControlPlane/
COPY backend/src/Condux.Core/Condux.Core.csproj backend/src/Condux.Core/
COPY backend/src/Condux.GitHub/Condux.GitHub.csproj backend/src/Condux.GitHub/
COPY backend/src/Condux.Linear/Condux.Linear.csproj backend/src/Condux.Linear/
COPY backend/src/Condux.Messaging/Condux.Messaging.csproj backend/src/Condux.Messaging/
COPY backend/src/Condux.Notifications/Condux.Notifications.csproj backend/src/Condux.Notifications/
COPY backend/src/Condux.Relay/Condux.Relay.csproj backend/src/Condux.Relay/
COPY backend/src/Condux.Runner/Condux.Runner.csproj backend/src/Condux.Runner/
COPY backend/src/Condux.Storage/Condux.Storage.csproj backend/src/Condux.Storage/
COPY backend/src/Condux.Symbolizer/Condux.Symbolizer.csproj backend/src/Condux.Symbolizer/
COPY backend/src/Condux.Telemetry/Condux.Telemetry.csproj backend/src/Condux.Telemetry/
# The Telemetry project references the standalone .NET SDK (dogfooding), a sibling of backend/, and the
# relay and control-plane its ASP.NET Core adapter, for the caller-caused rule their self-reporting reads.
COPY sdks/dotnet/Condux.Sdk/Condux.Sdk.csproj sdks/dotnet/Condux.Sdk/
COPY sdks/dotnet/Condux.Sdk.AspNetCore/Condux.Sdk.AspNetCore.csproj sdks/dotnet/Condux.Sdk.AspNetCore/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore backend/src/Condux.Relay/Condux.Relay.csproj && \
    dotnet restore backend/src/Condux.ControlPlane/Condux.ControlPlane.csproj && \
    dotnet restore backend/src/Condux.Consumer/Condux.Consumer.csproj && \
    dotnet restore backend/src/Condux.Conductor/Condux.Conductor.csproj && \
    dotnet restore backend/src/Condux.Runner/Condux.Runner.csproj && \
    dotnet restore backend/src/Condux.Symbolizer/Condux.Symbolizer.csproj

COPY backend/src/ backend/src/
COPY sdks/dotnet/Condux.Sdk/ sdks/dotnet/Condux.Sdk/
COPY sdks/dotnet/Condux.Sdk.AspNetCore/ sdks/dotnet/Condux.Sdk.AspNetCore/
# No --no-restore: the NuGet packages live in the cache mount, which BuildKit may prune while the
# restore LAYER stays cached (NETSDK1064). Restoring inside publish is a no-op when the mount is
# warm and self-heals when it was emptied.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish backend/src/Condux.Relay/Condux.Relay.csproj -c Release -o /out/relay && \
    dotnet publish backend/src/Condux.ControlPlane/Condux.ControlPlane.csproj -c Release -o /out/controlplane && \
    dotnet publish backend/src/Condux.Consumer/Condux.Consumer.csproj -c Release -o /out/consumer && \
    dotnet publish backend/src/Condux.Conductor/Condux.Conductor.csproj -c Release -o /out/conductor && \
    dotnet publish backend/src/Condux.Runner/Condux.Runner.csproj -c Release -o /out/runner && \
    dotnet publish backend/src/Condux.Symbolizer/Condux.Symbolizer.csproj -c Release -o /out/symbolizer

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS relay
WORKDIR /app
COPY --from=build /out/relay .
ENV ASPNETCORE_URLS=http://0.0.0.0:9000
EXPOSE 9000
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Relay.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS controlplane
WORKDIR /app
COPY --from=build /out/controlplane .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.ControlPlane.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0.14 AS consumer
WORKDIR /app
COPY --from=build /out/consumer .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Consumer.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0.14 AS conductor
WORKDIR /app
COPY --from=build /out/conductor .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Conductor.dll"]

# The customer-hosted runner (ADR-0033 slice 4, #68). Not in our compose stack: it runs on the customer's
# machines, needs only outbound HTTPS to the control plane, and holds their model and source-host
# credentials. Build it with `docker build --target runner -f backend/Dockerfile .` from the repo root.
FROM mcr.microsoft.com/dotnet/runtime:10.0.14 AS runner
WORKDIR /app
COPY --from=build /out/runner .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Runner.dll"]

# The native symbolizer (ADR-0049): llvm-symbolizer behind a small HTTP API, in its own container because
# it parses debug files a stranger uploaded. It is given no credential and no store, so a parser defect
# reaches nothing worth having. LLVM is pinned to the Ubuntu release's own version, with every package of
# it pinned alike: the release pocket never changes, where an update pocket keeps only its newest version,
# so a pin there stops resolving the day an update lands and fails every deploy that builds this image.
# The base image names its Ubuntu release for the same reason. The CI integration job reads this ARG and
# installs the same packages, so the tests and the image run the same LLVM version.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble AS symbolizer
ARG LLVM_VERSION=1:18.1.3-1
RUN apt-get update && \
    apt-get install -y --no-install-recommends llvm-18=$LLVM_VERSION llvm-18-runtime=$LLVM_VERSION \
        llvm-18-linker-tools=$LLVM_VERSION libllvm18=$LLVM_VERSION && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out/symbolizer .
ENV ASPNETCORE_URLS=http://0.0.0.0:9100 \
    CONDUX_LLVM_SYMBOLIZER=/usr/lib/llvm-18/bin/llvm-symbolizer \
    CONDUX_SYMBOLIZER_CACHE_DIR=/cache
RUN mkdir /cache && chown $APP_UID /cache
EXPOSE 9100
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Symbolizer.dll"]
