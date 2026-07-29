/**
 * GET /api/phone-number
 *
 * The ACS number customers can call in on. Served from runtime config so the
 * number is never hardcoded in the UI — Bicep injects NUXT_ACS_PHONE_NUMBER
 * from the same `acsPhoneNumber` param that configures caller-agent.
 */

/** E.164 -> readable grouping for the locales we provision (US toll-free, DK). */
function formatE164(raw: string): string {
  const digits = raw.replace(/[^\d+]/g, "");

  // +1 NANP: +1 833 256 2495
  const nanp = /^\+1(\d{3})(\d{3})(\d{4})$/.exec(digits);
  if (nanp) {
    return `+1 ${nanp[1]} ${nanp[2]} ${nanp[3]}`;
  }

  // +45 Danish: +45 12 34 56 78
  const dk = /^\+45(\d{2})(\d{2})(\d{2})(\d{2})$/.exec(digits);
  if (dk) {
    return `+45 ${dk[1]} ${dk[2]} ${dk[3]} ${dk[4]}`;
  }

  return digits;
}

export default defineEventHandler(() => {
  const raw = (useRuntimeConfig().acsPhoneNumber || "").trim();

  if (!raw) {
    return { configured: false, phoneNumber: "", display: "", telHref: "" };
  }

  return {
    configured: true,
    phoneNumber: raw,
    display: formatE164(raw),
    telHref: `tel:${raw.replace(/[^\d+]/g, "")}`,
  };
});
