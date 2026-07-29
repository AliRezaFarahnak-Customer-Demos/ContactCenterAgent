import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";

// Build-time site URL for absolute OG image paths (required by LinkedIn/Twitter)
// Set via NUXT_PUBLIC_SITE_URL env var in CI, empty for local dev
const siteUrl = (process.env.NUXT_PUBLIC_SITE_URL || "").replace(/\/$/, "");

// Version info written by the azd prepackage hook into .version.json before
// the source is uploaded to ACR remote build. Falls back to local defaults.
const versionPath = resolve(__dirname, ".version.json");
const version = existsSync(versionPath)
  ? JSON.parse(readFileSync(versionPath, "utf-8"))
  : { appVersion: "0.0.0-local", buildNumber: "0" };

export default defineNuxtConfig({
  compatibilityDate: "2025-01-01",
  modules: ["@nuxtjs/tailwindcss"],
  ssr: false,

  css: ["~/assets/css/main.css"],

  runtimeConfig: {
    callerAgentUrl:
      process.env.NUXT_CALLER_AGENT_URL ||
      process.env.CALLER_AGENT_URL ||
      "http://localhost:5000",
    // Inbound ACS number shown in the UI. Injected by Bicep; empty until provisioned.
    acsPhoneNumber:
      process.env.NUXT_ACS_PHONE_NUMBER || process.env.ACS_PHONE_NUMBER || "",
    // Public MCP endpoint URL (for the "connect from VS Code" hint). Injected by Bicep.
    mcpUrl: process.env.NUXT_MCP_URL || "",
    public: {
      appVersion: version.appVersion,
      buildNumber: String(version.buildNumber),
      // App Insights connection string for browser-side telemetry
      // Set via NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING env var (injected by Bicep)
      appInsightsConnectionString: "",
    },
  },

  app: {
    head: {
      title: "Agentic Call Center — AI-drevet kundeservice",
      meta: [
        {
          name: "description",
          content:
            "AI-drevet kundeservice — onboarding, regningsforklaring og MFA-verifikation.",
        },
        // Open Graph — social sharing (LinkedIn, Twitter, etc.)
        {
          property: "og:title",
          content: "Agentic Call Center — AI-drevet kundeservice",
        },
        {
          property: "og:description",
          content:
            "Onboarding, regningsforklaring og MFA — op til 10 AI-opkald i parallel.",
        },
        { property: "og:type", content: "website" },
        ...(siteUrl ? [{ property: "og:url", content: siteUrl }] : []),
        {
          property: "og:image",
          content: `${siteUrl}/og-image.png`,
        },
        { property: "og:image:type", content: "image/png" },
        { property: "og:image:width", content: "1200" },
        { property: "og:image:height", content: "630" },
        {
          property: "og:image:alt",
          content: "Agentic Call Center — AI-drevet kundeservice",
        },
        // Twitter / X card
        { name: "twitter:card", content: "summary_large_image" },
        {
          name: "twitter:title",
          content: "Agentic Call Center — AI-drevet kundeservice",
        },
        {
          name: "twitter:description",
          content:
            "Onboarding, regningsforklaring og MFA — op til 10 AI-opkald i parallel.",
        },
        {
          name: "twitter:image",
          content: `${siteUrl}/og-image.png`,
        },
        // Theme color for mobile browsers
        { name: "theme-color", content: "#ED0812" },
      ],
      viewport: "width=device-width, initial-scale=1, viewport-fit=cover",
      link: [
        { rel: "icon", type: "image/x-icon", href: "/favicon.ico" },
        {
          rel: "icon",
          type: "image/png",
          sizes: "32x32",
          href: "/favicon-32x32.png",
        },
        {
          rel: "icon",
          type: "image/png",
          sizes: "16x16",
          href: "/favicon-16x16.png",
        },
        {
          rel: "apple-touch-icon",
          sizes: "180x180",
          href: "/apple-touch-icon.png",
        },
        // Preload Norlys brand fonts to avoid FOUT on first paint
        {
          rel: "preload",
          as: "font",
          type: "font/otf",
          href: "/fonts/NORLYSText-Regular.otf",
          crossorigin: "anonymous",
        },
        {
          rel: "preload",
          as: "font",
          type: "font/otf",
          href: "/fonts/NORLYSHeadline-Bold.otf",
          crossorigin: "anonymous",
        },
      ],
    },
  },

  nitro: {
    preset: "node-server",
  },
});
