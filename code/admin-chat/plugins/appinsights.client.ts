/**
 * Nuxt plugin — Application Insights (browser-side only).
 * Auto-tracks page views, unhandled exceptions, AJAX dependencies, and performance.
 * Also captures Vue errors via app.config.errorHandler.
 *
 * The connection string is injected via NUXT_PUBLIC_APPINSIGHTS_CONNECTION_STRING
 * env var (set by Bicep). Falls back gracefully if not set (local dev).
 *
 * App Insights automatically resolves geo-location (country, city, region),
 * device type, browser, and OS from the ingestion endpoint — no client-side
 * geo lookups needed.
 */
import { ApplicationInsights } from "@microsoft/applicationinsights-web";

// Augment NuxtApp so $appInsights is properly typed everywhere
declare module "#app" {
  interface NuxtApp {
    $appInsights: ApplicationInsights | null;
  }
}

/**
 * Generate or retrieve a persistent anonymous user ID from localStorage.
 * This ensures each browser gets a unique, stable user ID even when
 * cookies are blocked, partitioned, or cleared by the browser.
 */
function getOrCreateAnonymousUserId(): string {
  const storageKey = "contactcenteragent_anon_uid";
  try {
    const existing = localStorage.getItem(storageKey);
    if (existing) return existing;
    const newId = crypto.randomUUID();
    localStorage.setItem(storageKey, newId);
    return newId;
  } catch {
    // localStorage blocked (e.g. incognito in some browsers) — fall back to
    // a per-session random ID. At least this session will be unique.
    return crypto.randomUUID();
  }
}

export default defineNuxtPlugin((nuxtApp) => {
  const config = useRuntimeConfig();
  const connectionString = config.public.appInsightsConnectionString as string;

  if (!connectionString) {
    console.warn(
      "[AppInsights] No connection string configured — telemetry disabled",
    );
    return {
      provide: {
        appInsights: null as ApplicationInsights | null,
      },
    };
  }

  const anonymousUserId = getOrCreateAnonymousUserId();

  const appInsights = new ApplicationInsights({
    config: {
      connectionString,
      enableAutoRouteTracking: true,
      enableCorsCorrelation: true,
      enableRequestHeaderTracking: true,
      enableResponseHeaderTracking: true,
      enableUnhandledPromiseRejectionTracking: true,
      disableFetchTracking: false,
      enableAjaxPerfTracking: true,
      cookieCfg: {
        enabled: true,
        domain: undefined, // auto-detect from current hostname
      },
    },
  });

  appInsights.loadAppInsights();

  // Override the SDK's cookie-based user ID with our localStorage-backed ID.
  // This guarantees unique user tracking even when browsers block or
  // partition the ai_user cookie, which causes all users to collapse into one.
  appInsights.context.user.id = anonymousUserId;
  appInsights.setAuthenticatedUserContext(anonymousUserId, undefined, false);

  // Telemetry initializer — stamp cloud role and stable user ID on every item.
  // SDK v3.x: envelope.tags is Tags[] (array), not a flat object.
  appInsights.addTelemetryInitializer((envelope) => {
    const tags: Record<string, string> = {
      "ai.cloud.role": "admin-chat-frontend",
      "ai.user.id": anonymousUserId,
      "ai.user.authUserId": anonymousUserId,
    };
    if (Array.isArray(envelope.tags)) {
      envelope.tags.push(tags);
    } else {
      envelope.tags = Object.assign(envelope.tags || {}, tags) as any;
    }
  });

  // Track app version
  const appVersion = config.public.appVersion as string;
  if (appVersion) {
    appInsights.context.application.ver = appVersion;
  }

  // Capture Vue errors as App Insights exceptions.
  nuxtApp.hook("vue:error", (err, instance, info) => {
    appInsights.trackException({
      exception: err instanceof Error ? err : new Error(String(err)),
      properties: {
        vueInfo: info,
        component:
          (instance as any)?.$options?.name ??
          (instance as any)?.type?.__name ??
          "unknown",
      },
    });
  });

  // Track initial page view (enableAutoRouteTracking handles subsequent SPA navigations).
  appInsights.trackPageView();

  console.log("[AppInsights] Frontend telemetry initialized");

  return {
    provide: {
      appInsights,
    },
  };
});
