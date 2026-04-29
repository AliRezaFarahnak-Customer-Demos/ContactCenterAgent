# ---------------------------------------------------------------------------
# Contact Center Agent — Caller Agent
# Multi-stage Dockerfile: .NET 10 API with WebSocket support
#
# Build context is the repo's `code/` folder (not `code/caller-agent/`) so
# the shared project (`code/shared/ContactCenterAgent.Shared/`) referenced
# by `CallerAgent.csproj` is reachable. See .github/workflows/deploy.yml
# and azure.yaml — both must agree on this context.
# ---------------------------------------------------------------------------

# ---- Stage 1: Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files first for restore layer caching.
COPY caller-agent/agent/CallerAgent.csproj caller-agent/agent/
COPY shared/ContactCenterAgent.Shared/ContactCenterAgent.Shared.csproj shared/ContactCenterAgent.Shared/
RUN dotnet restore caller-agent/agent/CallerAgent.csproj

# Copy sources.
COPY caller-agent/agent/ caller-agent/agent/
COPY shared/ContactCenterAgent.Shared/ shared/ContactCenterAgent.Shared/

RUN dotnet publish caller-agent/agent/CallerAgent.csproj -c Release -o /publish

# ---- Stage 2: Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

COPY --from=build /publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CallerAgent.dll"]
