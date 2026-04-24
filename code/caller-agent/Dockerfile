# ---------------------------------------------------------------------------
# Contact Center Agent — Caller Agent
# Single-stage Dockerfile: .NET 8 API with WebSocket support
# ---------------------------------------------------------------------------

# ---- Stage 1: Build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY agent/CallerAgent.csproj .
RUN dotnet restore

COPY agent/ .
RUN dotnet publish -c Release -o /publish

# ---- Stage 2: Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "CallerAgent.dll"]
