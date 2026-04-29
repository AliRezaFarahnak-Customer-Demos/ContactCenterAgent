# ---------------------------------------------------------------------------
# Contact Center Agent — Admin Chat (Nuxt 3 SPA)
# Single-process Dockerfile: Nuxt 3 only.
#
# History: this used to also bundle a .NET sidecar (port 8000) for the
# AG-UI chat panel. The chat UI was removed; see .github/copilot-instructions
# "Removed: AdminChat .NET sidecar" for restore notes.
# ---------------------------------------------------------------------------
# Note: app version + build number come from .version.json in the build
# context (written by the azd prepackage hook). nuxt.config.ts reads it.

# ---- Stage 1: Build the Nuxt 3 frontend ----
FROM node:22-alpine AS frontend-build
ARG NUXT_PUBLIC_SITE_URL
ARG NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING
ENV NUXT_PUBLIC_SITE_URL=$NUXT_PUBLIC_SITE_URL
ENV NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING=$NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --ignore-scripts

COPY nuxt.config.ts tsconfig.json tailwind.config.ts .version.json ./
COPY app.vue ./
COPY pages/ pages/
COPY components/ components/
COPY composables/ composables/
COPY server/ server/
COPY assets/ assets/
COPY public/ public/

RUN npx nuxt prepare && npm run build

# ---- Stage 2: Runtime — Node only ----
FROM node:22-slim AS runtime
WORKDIR /app

COPY --from=frontend-build /app/.output ./.output

ENV PORT=3000
ENV HOST=0.0.0.0
ENV NITRO_PORT=3000
ENV NITRO_HOST=0.0.0.0

EXPOSE 3000

CMD ["node", ".output/server/index.mjs"]
