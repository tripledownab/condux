# syntax=docker/dockerfile:1
# ONE build for every service (compose targets: relay / controlplane / consumer / conductor, plus the
# customer-hosted runner, which is built from here but deployed by the customer rather than by us).
# Built from the REPO ROOT context (not backend/): Condux.Telemetry references the standalone .NET SDK at
# sdks/dotnet/Condux.Sdk (the services dogfood it via ConduxSelfReporter, #75), which sits outside
# backend/. The repo layout is mirrored under /src so that project's relative reference resolves. The
# web image already builds from the repo root for the same reason (a sibling workspace dependency).
# Layer discipline keeps the build cache small and fast:
#   1. manifests only -> restore, with the NuGet store in a shared cache mount (never in layers);
#   2. source -> publish all four apps in one stage, so shared projects compile once, not four times;
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
COPY backend/src/Condux.Messaging/Condux.Messaging.csproj backend/src/Condux.Messaging/
COPY backend/src/Condux.Notifications/Condux.Notifications.csproj backend/src/Condux.Notifications/
COPY backend/src/Condux.Relay/Condux.Relay.csproj backend/src/Condux.Relay/
COPY backend/src/Condux.Runner/Condux.Runner.csproj backend/src/Condux.Runner/
COPY backend/src/Condux.Storage/Condux.Storage.csproj backend/src/Condux.Storage/
COPY backend/src/Condux.Telemetry/Condux.Telemetry.csproj backend/src/Condux.Telemetry/
# The Telemetry project references the standalone .NET SDK (dogfooding), a sibling of backend/.
COPY sdks/dotnet/Condux.Sdk/Condux.Sdk.csproj sdks/dotnet/Condux.Sdk/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore backend/src/Condux.Relay/Condux.Relay.csproj && \
    dotnet restore backend/src/Condux.ControlPlane/Condux.ControlPlane.csproj && \
    dotnet restore backend/src/Condux.Consumer/Condux.Consumer.csproj && \
    dotnet restore backend/src/Condux.Conductor/Condux.Conductor.csproj && \
    dotnet restore backend/src/Condux.Runner/Condux.Runner.csproj

COPY backend/src/ backend/src/
COPY sdks/dotnet/Condux.Sdk/ sdks/dotnet/Condux.Sdk/
# No --no-restore: the NuGet packages live in the cache mount, which BuildKit may prune while the
# restore LAYER stays cached (NETSDK1064). Restoring inside publish is a no-op when the mount is
# warm and self-heals when it was emptied.
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish backend/src/Condux.Relay/Condux.Relay.csproj -c Release -o /out/relay && \
    dotnet publish backend/src/Condux.ControlPlane/Condux.ControlPlane.csproj -c Release -o /out/controlplane && \
    dotnet publish backend/src/Condux.Consumer/Condux.Consumer.csproj -c Release -o /out/consumer && \
    dotnet publish backend/src/Condux.Conductor/Condux.Conductor.csproj -c Release -o /out/conductor && \
    dotnet publish backend/src/Condux.Runner/Condux.Runner.csproj -c Release -o /out/runner

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS relay
WORKDIR /app
COPY --from=build /out/relay .
ENV ASPNETCORE_URLS=http://0.0.0.0:9000
EXPOSE 9000
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Relay.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS controlplane
WORKDIR /app
COPY --from=build /out/controlplane .
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.ControlPlane.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS consumer
WORKDIR /app
COPY --from=build /out/consumer .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Consumer.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS conductor
WORKDIR /app
COPY --from=build /out/conductor .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Conductor.dll"]

# The customer-hosted runner (ADR-0033 slice 4, #68). Not in our compose stack: it runs on the customer's
# machines, needs only outbound HTTPS to the control plane, and holds their model and source-host
# credentials. Build it with `docker build --target runner -f backend/Dockerfile .` from the repo root.
FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runner
WORKDIR /app
COPY --from=build /out/runner .
USER $APP_UID
ENTRYPOINT ["dotnet", "Condux.Runner.dll"]
