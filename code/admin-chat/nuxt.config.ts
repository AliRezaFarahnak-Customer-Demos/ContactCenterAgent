// Build-time site URL for absolute OG image paths (required by LinkedIn/Twitter)
// Set via NUXT_PUBLIC_SITE_URL env var in CI, empty for local dev
const siteUrl = (process.env.NUXT_PUBLIC_SITE_URL || "").replace(/\/$/, "");

export default defineNuxtConfig({
  compatibilityDate: "2025-01-01",
  modules: ["@nuxtjs/tailwindcss"],
  ssr: false,

  css: ["~/assets/css/main.css"],

  runtimeConfig: {
    agentUrl: process.env.AGENT_URL || "http://localhost:8000/",
    callerAgentUrl:
      process.env.NUXT_CALLER_AGENT_URL ||
      process.env.CALLER_AGENT_URL ||
      "http://localhost:5000",
    public: {
      // Set by CI via NUXT_PUBLIC_APP_VERSION env var (e.g. 1.0.0.42)
      // Falls back to "0.0.0-local" for local dev
      appVersion: "0.0.0-local",
      // App Insights connection string for browser-side telemetry
      // Set via NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING env var (injected by Bicep)
      appInsightsConnectionString: "",
    },
  },

  app: {
    head: {
      title: "CallCenter — AI-drevet kundeservice",
      meta: [
        {
          name: "description",
          content:
            "AI-drevet kundeservice — onboarding, regningsforklaring og MFA-verifikation.",
        },
        // Open Graph — social sharing (LinkedIn, Twitter, etc.)
        {
          property: "og:title",
          content: "CallCenter — AI-drevet kundeservice",
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
          content: "CallCenter \u2014 AI-drevet kundeservice",
        },
        // Twitter / X card
        { name: "twitter:card", content: "summary_large_image" },
        {
          name: "twitter:title",
          content: "CallCenter — AI-drevet kundeservice",
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
      ],
    },
  },

  nitro: {
    preset: "node-server",
  },
});
