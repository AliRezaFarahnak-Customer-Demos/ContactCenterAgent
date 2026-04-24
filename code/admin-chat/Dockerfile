# ---------------------------------------------------------------------------
# Contact Center Agent — Admin Chat Agent
# Multi-stage Dockerfile: Nuxt 3 frontend + .NET 10 backend in one container
# ---------------------------------------------------------------------------

# Build arg: version string (e.g. 1.0.0.42), set by CI
ARG APP_VERSION=0.0.0-local

# ---- Stage 1: Build the Nuxt 3 frontend ----
FROM node:22-alpine AS frontend-build
ARG APP_VERSION
ARG NUXT_PUBLIC_SITE_URL
ARG NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING
ENV NUXT_PUBLIC_APP_VERSION=$APP_VERSION
ENV NUXT_PUBLIC_SITE_URL=$NUXT_PUBLIC_SITE_URL
ENV NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING=$NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts

COPY nuxt.config.ts tsconfig.json tailwind.config.ts ./
COPY app.vue ./
COPY pages/ pages/
COPY components/ components/
COPY composables/ composables/
COPY server/ server/
COPY assets/ assets/
COPY public/ public/

RUN npx nuxt prepare && npm run build

# ---- Stage 2: Build the .NET Agent ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent-build
WORKDIR /agent

COPY agent/AdminChat.csproj .
RUN dotnet restore

COPY agent/ .
RUN dotnet publish -c Release -o /publish

# ---- Stage 3: Runtime — Node + .NET side by side ----
# Use aspnet:10.0 as base (has all .NET native deps: libssl, libicu, etc.)
# Copy Node.js from the node image instead of apt-get install (faster, cached).
FROM node:22-slim AS node-runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Copy Node.js binary + npm from the node image (no apt-get needed)
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
COPY --from=node-runtime /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm

WORKDIR /app

# Copy .NET agent
COPY --from=agent-build /publish ./agent/

# Copy Nuxt 3 production build
COPY --from=frontend-build /app/.output ./.output

# Set agent URL for the Nuxt server route to connect to the .NET backend
ARG APP_VERSION
ENV NUXT_PUBLIC_APP_VERSION=$APP_VERSION
ENV AGENT_URL=http://localhost:8000/
ENV PORT=3000
ENV HOST=0.0.0.0
ENV NITRO_PORT=3000
ENV NITRO_HOST=0.0.0.0
ENV ASPNETCORE_URLS=http://+:8000

# Expose the frontend port (Container Apps will route to this)
EXPOSE 3000

# Start both processes: .NET agent (port 8000) + Nuxt (port 3000)
CMD dotnet agent/AdminChat.dll & node .output/server/index.mjs
