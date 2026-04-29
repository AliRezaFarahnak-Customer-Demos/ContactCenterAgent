# ---------------------------------------------------------------------------
# Contact Center Agent — Caller Agent
# Multi-stage Dockerfile: .NET 10 API with WebSocket support
#
# Build context is the REPO ROOT (not code/ or code/caller-agent/) so the
# shared project (`code/shared/ContactCenterAgent.Shared/`) and its content
# include `..\..\..\personas.json` are both reachable. See
# .github/workflows/deploy.yml and azure.yaml — both must agree on this.
# ---------------------------------------------------------------------------

# ---- Stage 1: Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first for restore layer caching.
COPY code/caller-agent/agent/CallerAgent.csproj code/caller-agent/agent/
COPY code/shared/ContactCenterAgent.Shared/ContactCenterAgent.Shared.csproj code/shared/ContactCenterAgent.Shared/
RUN dotnet restore code/caller-agent/agent/CallerAgent.csproj

# Copy sources + the repo-root personas.json that the shared project links in.
COPY personas.json ./
COPY code/caller-agent/agent/ code/caller-agent/agent/
COPY code/shared/ContactCenterAgent.Shared/ code/shared/ContactCenterAgent.Shared/

RUN dotnet publish code/caller-agent/agent/CallerAgent.csproj -c Release -o /publish

# ---- Stage 2: Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CallerAgent.dll"]
