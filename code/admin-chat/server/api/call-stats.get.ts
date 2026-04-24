/**
 * Nuxt server route: returns call statistics by country.
 * GET /api/call-stats → { totalCalls, countries: [...] }
 *
 * Queries the Log Analytics workspace directly using the REST API.
 * Auth: Azure Managed Identity (in Container Apps) or DefaultAzureCredential (locally via Azure CLI).
 * Falls back to demo data if credentials are unavailable (local dev without Azure login).
 */

// Complete E.164 phone prefix → country mapping (every sovereign state + major territories)
const PHONE_PREFIX_MAP: Record<
  string,
  { code: string; name: string; flag: string }
> = {
  // ── Zone 1: North America (NANP) ────────────────────────────────────
  "1": { code: "US", name: "United States", flag: "🇺🇸" },
  // Caribbean & territories (4-digit NANP overrides)
  "1242": { code: "BS", name: "Bahamas", flag: "🇧🇸" },
  "1246": { code: "BB", name: "Barbados", flag: "🇧🇧" },
  "1264": { code: "AI", name: "Anguilla", flag: "🇦🇮" },
  "1268": { code: "AG", name: "Antigua and Barbuda", flag: "🇦🇬" },
  "1284": { code: "VG", name: "British Virgin Islands", flag: "🇻🇬" },
  "1340": { code: "VI", name: "US Virgin Islands", flag: "🇻🇮" },
  "1345": { code: "KY", name: "Cayman Islands", flag: "🇰🇾" },
  "1441": { code: "BM", name: "Bermuda", flag: "🇧🇲" },
  "1473": { code: "GD", name: "Grenada", flag: "🇬🇩" },
  "1649": { code: "TC", name: "Turks and Caicos", flag: "🇹🇨" },
  "1664": { code: "MS", name: "Montserrat", flag: "🇲🇸" },
  "1670": { code: "MP", name: "Northern Mariana Islands", flag: "🇲🇵" },
  "1671": { code: "GU", name: "Guam", flag: "🇬🇺" },
  "1684": { code: "AS", name: "American Samoa", flag: "🇦🇸" },
  "1721": { code: "SX", name: "Sint Maarten", flag: "🇸🇽" },
  "1758": { code: "LC", name: "Saint Lucia", flag: "🇱🇨" },
  "1767": { code: "DM", name: "Dominica", flag: "🇩🇲" },
  "1784": { code: "VC", name: "Saint Vincent", flag: "🇻🇨" },
  "1787": { code: "PR", name: "Puerto Rico", flag: "🇵🇷" },
  "1809": { code: "DO", name: "Dominican Republic", flag: "🇩🇴" },
  "1829": { code: "DO", name: "Dominican Republic", flag: "🇩🇴" },
  "1849": { code: "DO", name: "Dominican Republic", flag: "🇩🇴" },
  "1868": { code: "TT", name: "Trinidad and Tobago", flag: "🇹🇹" },
  "1869": { code: "KN", name: "Saint Kitts and Nevis", flag: "🇰🇳" },
  "1876": { code: "JM", name: "Jamaica", flag: "🇯🇲" },
  "1939": { code: "PR", name: "Puerto Rico", flag: "🇵🇷" },

  // ── Zone 7: Russia & Kazakhstan ─────────────────────────────────────
  "7": { code: "RU", name: "Russia", flag: "🇷🇺" },
  "76": { code: "KZ", name: "Kazakhstan", flag: "🇰🇿" },
  "77": { code: "KZ", name: "Kazakhstan", flag: "🇰🇿" },

  // ── Zone 2: Africa ──────────────────────────────────────────────────
  "20": { code: "EG", name: "Egypt", flag: "🇪🇬" },
  "211": { code: "SS", name: "South Sudan", flag: "🇸🇸" },
  "212": { code: "MA", name: "Morocco", flag: "🇲🇦" },
  "213": { code: "DZ", name: "Algeria", flag: "🇩🇿" },
  "216": { code: "TN", name: "Tunisia", flag: "🇹🇳" },
  "218": { code: "LY", name: "Libya", flag: "🇱🇾" },
  "220": { code: "GM", name: "Gambia", flag: "🇬🇲" },
  "221": { code: "SN", name: "Senegal", flag: "🇸🇳" },
  "222": { code: "MR", name: "Mauritania", flag: "🇲🇷" },
  "223": { code: "ML", name: "Mali", flag: "🇲🇱" },
  "224": { code: "GN", name: "Guinea", flag: "🇬🇳" },
  "225": { code: "CI", name: "Côte d'Ivoire", flag: "🇨🇮" },
  "226": { code: "BF", name: "Burkina Faso", flag: "🇧🇫" },
  "227": { code: "NE", name: "Niger", flag: "🇳🇪" },
  "228": { code: "TG", name: "Togo", flag: "🇹🇬" },
  "229": { code: "BJ", name: "Benin", flag: "🇧🇯" },
  "230": { code: "MU", name: "Mauritius", flag: "🇲🇺" },
  "231": { code: "LR", name: "Liberia", flag: "🇱🇷" },
  "232": { code: "SL", name: "Sierra Leone", flag: "🇸🇱" },
  "233": { code: "GH", name: "Ghana", flag: "🇬🇭" },
  "234": { code: "NG", name: "Nigeria", flag: "🇳🇬" },
  "235": { code: "TD", name: "Chad", flag: "🇹🇩" },
  "236": { code: "CF", name: "Central African Republic", flag: "🇨🇫" },
  "237": { code: "CM", name: "Cameroon", flag: "🇨🇲" },
  "238": { code: "CV", name: "Cape Verde", flag: "🇨🇻" },
  "239": { code: "ST", name: "São Tomé and Príncipe", flag: "🇸🇹" },
  "240": { code: "GQ", name: "Equatorial Guinea", flag: "🇬🇶" },
  "241": { code: "GA", name: "Gabon", flag: "🇬🇦" },
  "242": { code: "CG", name: "Republic of the Congo", flag: "🇨🇬" },
  "243": { code: "CD", name: "DR Congo", flag: "🇨🇩" },
  "244": { code: "AO", name: "Angola", flag: "🇦🇴" },
  "245": { code: "GW", name: "Guinea-Bissau", flag: "🇬🇼" },
  "246": { code: "IO", name: "British Indian Ocean Territory", flag: "🇮🇴" },
  "247": { code: "AC", name: "Ascension Island", flag: "🇦🇨" },
  "248": { code: "SC", name: "Seychelles", flag: "🇸🇨" },
  "249": { code: "SD", name: "Sudan", flag: "🇸🇩" },
  "250": { code: "RW", name: "Rwanda", flag: "🇷🇼" },
  "251": { code: "ET", name: "Ethiopia", flag: "🇪🇹" },
  "252": { code: "SO", name: "Somalia", flag: "🇸🇴" },
  "253": { code: "DJ", name: "Djibouti", flag: "🇩🇯" },
  "254": { code: "KE", name: "Kenya", flag: "🇰🇪" },
  "255": { code: "TZ", name: "Tanzania", flag: "🇹🇿" },
  "256": { code: "UG", name: "Uganda", flag: "🇺🇬" },
  "257": { code: "BI", name: "Burundi", flag: "🇧🇮" },
  "258": { code: "MZ", name: "Mozambique", flag: "🇲🇿" },
  "260": { code: "ZM", name: "Zambia", flag: "🇿🇲" },
  "261": { code: "MG", name: "Madagascar", flag: "🇲🇬" },
  "262": { code: "RE", name: "Réunion", flag: "🇷🇪" },
  "263": { code: "ZW", name: "Zimbabwe", flag: "🇿🇼" },
  "264": { code: "NA", name: "Namibia", flag: "🇳🇦" },
  "265": { code: "MW", name: "Malawi", flag: "🇲🇼" },
  "266": { code: "LS", name: "Lesotho", flag: "🇱🇸" },
  "267": { code: "BW", name: "Botswana", flag: "🇧🇼" },
  "268": { code: "SZ", name: "Eswatini", flag: "🇸🇿" },
  "269": { code: "KM", name: "Comoros", flag: "🇰🇲" },
  "27": { code: "ZA", name: "South Africa", flag: "🇿🇦" },
  "290": { code: "SH", name: "Saint Helena", flag: "🇸🇭" },
  "291": { code: "ER", name: "Eritrea", flag: "🇪🇷" },
  "297": { code: "AW", name: "Aruba", flag: "🇦🇼" },
  "298": { code: "FO", name: "Faroe Islands", flag: "🇫🇴" },
  "299": { code: "GL", name: "Greenland", flag: "🇬🇱" },

  // ── Zone 3–4: Europe ────────────────────────────────────────────────
  "30": { code: "GR", name: "Greece", flag: "🇬🇷" },
  "31": { code: "NL", name: "Netherlands", flag: "🇳🇱" },
  "32": { code: "BE", name: "Belgium", flag: "🇧🇪" },
  "33": { code: "FR", name: "France", flag: "🇫🇷" },
  "34": { code: "ES", name: "Spain", flag: "🇪🇸" },
  "350": { code: "GI", name: "Gibraltar", flag: "🇬🇮" },
  "351": { code: "PT", name: "Portugal", flag: "🇵🇹" },
  "352": { code: "LU", name: "Luxembourg", flag: "🇱🇺" },
  "353": { code: "IE", name: "Ireland", flag: "🇮🇪" },
  "354": { code: "IS", name: "Iceland", flag: "🇮🇸" },
  "355": { code: "AL", name: "Albania", flag: "🇦🇱" },
  "356": { code: "MT", name: "Malta", flag: "🇲🇹" },
  "357": { code: "CY", name: "Cyprus", flag: "🇨🇾" },
  "358": { code: "FI", name: "Finland", flag: "🇫🇮" },
  "359": { code: "BG", name: "Bulgaria", flag: "🇧🇬" },
  "36": { code: "HU", name: "Hungary", flag: "🇭🇺" },
  "370": { code: "LT", name: "Lithuania", flag: "🇱🇹" },
  "371": { code: "LV", name: "Latvia", flag: "🇱🇻" },
  "372": { code: "EE", name: "Estonia", flag: "🇪🇪" },
  "373": { code: "MD", name: "Moldova", flag: "🇲🇩" },
  "374": { code: "AM", name: "Armenia", flag: "🇦🇲" },
  "375": { code: "BY", name: "Belarus", flag: "🇧🇾" },
  "376": { code: "AD", name: "Andorra", flag: "🇦🇩" },
  "377": { code: "MC", name: "Monaco", flag: "🇲🇨" },
  "378": { code: "SM", name: "San Marino", flag: "🇸🇲" },
  "379": { code: "VA", name: "Vatican City", flag: "🇻🇦" },
  "380": { code: "UA", name: "Ukraine", flag: "🇺🇦" },
  "381": { code: "RS", name: "Serbia", flag: "🇷🇸" },
  "382": { code: "ME", name: "Montenegro", flag: "🇲🇪" },
  "383": { code: "XK", name: "Kosovo", flag: "🇽🇰" },
  "385": { code: "HR", name: "Croatia", flag: "🇭🇷" },
  "386": { code: "SI", name: "Slovenia", flag: "🇸🇮" },
  "387": { code: "BA", name: "Bosnia and Herzegovina", flag: "🇧🇦" },
  "389": { code: "MK", name: "North Macedonia", flag: "🇲🇰" },
  "39": { code: "IT", name: "Italy", flag: "🇮🇹" },
  "40": { code: "RO", name: "Romania", flag: "🇷🇴" },
  "41": { code: "CH", name: "Switzerland", flag: "🇨🇭" },
  "420": { code: "CZ", name: "Czech Republic", flag: "🇨🇿" },
  "421": { code: "SK", name: "Slovakia", flag: "🇸🇰" },
  "423": { code: "LI", name: "Liechtenstein", flag: "🇱🇮" },
  "43": { code: "AT", name: "Austria", flag: "🇦🇹" },
  "44": { code: "GB", name: "United Kingdom", flag: "🇬🇧" },
  "45": { code: "DK", name: "Denmark", flag: "🇩🇰" },
  "46": { code: "SE", name: "Sweden", flag: "🇸🇪" },
  "47": { code: "NO", name: "Norway", flag: "🇳🇴" },
  "48": { code: "PL", name: "Poland", flag: "🇵🇱" },
  "49": { code: "DE", name: "Germany", flag: "🇩🇪" },

  // ── Zone 5: Latin America ───────────────────────────────────────────
  "500": { code: "FK", name: "Falkland Islands", flag: "🇫🇰" },
  "501": { code: "BZ", name: "Belize", flag: "🇧🇿" },
  "502": { code: "GT", name: "Guatemala", flag: "🇬🇹" },
  "503": { code: "SV", name: "El Salvador", flag: "🇸🇻" },
  "504": { code: "HN", name: "Honduras", flag: "🇭🇳" },
  "505": { code: "NI", name: "Nicaragua", flag: "🇳🇮" },
  "506": { code: "CR", name: "Costa Rica", flag: "🇨🇷" },
  "507": { code: "PA", name: "Panama", flag: "🇵🇦" },
  "508": { code: "PM", name: "Saint Pierre and Miquelon", flag: "🇵🇲" },
  "509": { code: "HT", name: "Haiti", flag: "🇭🇹" },
  "51": { code: "PE", name: "Peru", flag: "🇵🇪" },
  "52": { code: "MX", name: "Mexico", flag: "🇲🇽" },
  "53": { code: "CU", name: "Cuba", flag: "🇨🇺" },
  "54": { code: "AR", name: "Argentina", flag: "🇦🇷" },
  "55": { code: "BR", name: "Brazil", flag: "🇧🇷" },
  "56": { code: "CL", name: "Chile", flag: "🇨🇱" },
  "57": { code: "CO", name: "Colombia", flag: "🇨🇴" },
  "58": { code: "VE", name: "Venezuela", flag: "🇻🇪" },
  "590": { code: "GP", name: "Guadeloupe", flag: "🇬🇵" },
  "591": { code: "BO", name: "Bolivia", flag: "🇧🇴" },
  "592": { code: "GY", name: "Guyana", flag: "🇬🇾" },
  "593": { code: "EC", name: "Ecuador", flag: "🇪🇨" },
  "594": { code: "GF", name: "French Guiana", flag: "🇬🇫" },
  "595": { code: "PY", name: "Paraguay", flag: "🇵🇾" },
  "596": { code: "MQ", name: "Martinique", flag: "🇲🇶" },
  "597": { code: "SR", name: "Suriname", flag: "🇸🇷" },
  "598": { code: "UY", name: "Uruguay", flag: "🇺🇾" },
  "599": { code: "CW", name: "Curaçao", flag: "🇨🇼" },

  // ── Zone 6: Southeast Asia & Oceania ────────────────────────────────
  "60": { code: "MY", name: "Malaysia", flag: "🇲🇾" },
  "61": { code: "AU", name: "Australia", flag: "🇦🇺" },
  "62": { code: "ID", name: "Indonesia", flag: "🇮🇩" },
  "63": { code: "PH", name: "Philippines", flag: "🇵🇭" },
  "64": { code: "NZ", name: "New Zealand", flag: "🇳🇿" },
  "65": { code: "SG", name: "Singapore", flag: "🇸🇬" },
  "66": { code: "TH", name: "Thailand", flag: "🇹🇭" },
  "670": { code: "TL", name: "Timor-Leste", flag: "🇹🇱" },
  "672": { code: "NF", name: "Norfolk Island", flag: "🇳🇫" },
  "673": { code: "BN", name: "Brunei", flag: "🇧🇳" },
  "674": { code: "NR", name: "Nauru", flag: "🇳🇷" },
  "675": { code: "PG", name: "Papua New Guinea", flag: "🇵🇬" },
  "676": { code: "TO", name: "Tonga", flag: "🇹🇴" },
  "677": { code: "SB", name: "Solomon Islands", flag: "🇸🇧" },
  "678": { code: "VU", name: "Vanuatu", flag: "🇻🇺" },
  "679": { code: "FJ", name: "Fiji", flag: "🇫🇯" },
  "680": { code: "PW", name: "Palau", flag: "🇵🇼" },
  "681": { code: "WF", name: "Wallis and Futuna", flag: "🇼🇫" },
  "682": { code: "CK", name: "Cook Islands", flag: "🇨🇰" },
  "683": { code: "NU", name: "Niue", flag: "🇳🇺" },
  "685": { code: "WS", name: "Samoa", flag: "🇼🇸" },
  "686": { code: "KI", name: "Kiribati", flag: "🇰🇮" },
  "687": { code: "NC", name: "New Caledonia", flag: "🇳🇨" },
  "688": { code: "TV", name: "Tuvalu", flag: "🇹🇻" },
  "689": { code: "PF", name: "French Polynesia", flag: "🇵🇫" },
  "690": { code: "TK", name: "Tokelau", flag: "🇹🇰" },
  "691": { code: "FM", name: "Micronesia", flag: "🇫🇲" },
  "692": { code: "MH", name: "Marshall Islands", flag: "🇲🇭" },

  // ── Zone 8: East Asia ───────────────────────────────────────────────
  "81": { code: "JP", name: "Japan", flag: "🇯🇵" },
  "82": { code: "KR", name: "South Korea", flag: "🇰🇷" },
  "84": { code: "VN", name: "Vietnam", flag: "🇻🇳" },
  "850": { code: "KP", name: "North Korea", flag: "🇰🇵" },
  "852": { code: "HK", name: "Hong Kong", flag: "🇭🇰" },
  "853": { code: "MO", name: "Macau", flag: "🇲🇴" },
  "855": { code: "KH", name: "Cambodia", flag: "🇰🇭" },
  "856": { code: "LA", name: "Laos", flag: "🇱🇦" },
  "86": { code: "CN", name: "China", flag: "🇨🇳" },
  "880": { code: "BD", name: "Bangladesh", flag: "🇧🇩" },
  "886": { code: "TW", name: "Taiwan", flag: "🇹🇼" },

  // ── Zone 9: Central/South/West Asia ─────────────────────────────────
  "90": { code: "TR", name: "Turkey", flag: "🇹🇷" },
  "91": { code: "IN", name: "India", flag: "🇮🇳" },
  "92": { code: "PK", name: "Pakistan", flag: "🇵🇰" },
  "93": { code: "AF", name: "Afghanistan", flag: "🇦🇫" },
  "94": { code: "LK", name: "Sri Lanka", flag: "🇱🇰" },
  "95": { code: "MM", name: "Myanmar", flag: "🇲🇲" },
  "960": { code: "MV", name: "Maldives", flag: "🇲🇻" },
  "961": { code: "LB", name: "Lebanon", flag: "🇱🇧" },
  "962": { code: "JO", name: "Jordan", flag: "🇯🇴" },
  "963": { code: "SY", name: "Syria", flag: "🇸🇾" },
  "964": { code: "IQ", name: "Iraq", flag: "🇮🇶" },
  "965": { code: "KW", name: "Kuwait", flag: "🇰🇼" },
  "966": { code: "SA", name: "Saudi Arabia", flag: "🇸🇦" },
  "967": { code: "YE", name: "Yemen", flag: "🇾🇪" },
  "968": { code: "OM", name: "Oman", flag: "🇴🇲" },
  "970": { code: "PS", name: "Palestine", flag: "🇵🇸" },
  "971": { code: "AE", name: "UAE", flag: "🇦🇪" },
  "972": { code: "IL", name: "Israel", flag: "🇮🇱" },
  "973": { code: "BH", name: "Bahrain", flag: "🇧🇭" },
  "974": { code: "QA", name: "Qatar", flag: "🇶🇦" },
  "975": { code: "BT", name: "Bhutan", flag: "🇧🇹" },
  "976": { code: "MN", name: "Mongolia", flag: "🇲🇳" },
  "977": { code: "NP", name: "Nepal", flag: "🇳🇵" },
  "98": { code: "IR", name: "Iran", flag: "🇮🇷" },
  "992": { code: "TJ", name: "Tajikistan", flag: "🇹🇯" },
  "993": { code: "TM", name: "Turkmenistan", flag: "🇹🇲" },
  "994": { code: "AZ", name: "Azerbaijan", flag: "🇦🇿" },
  "995": { code: "GE", name: "Georgia", flag: "🇬🇪" },
  "996": { code: "KG", name: "Kyrgyzstan", flag: "🇰🇬" },
  "998": { code: "UZ", name: "Uzbekistan", flag: "🇺🇿" },
};

/** Resolve a phone number (e.g. "<your-alert-phone>") to a country entry */
function phoneToCountry(
  phone: string,
): { code: string; name: string; flag: string } | null {
  const digits = phone.replace(/^\+/, "");
  // Try 4-digit (NANP Caribbean), 3-digit, 2-digit, then 1-digit prefix
  for (const len of [4, 3, 2, 1]) {
    const prefix = digits.substring(0, len);
    if (PHONE_PREFIX_MAP[prefix]) return PHONE_PREFIX_MAP[prefix];
  }
  return null;
}

interface CountryStat {
  code: string;
  name: string;
  flag: string;
  calls: number;
  messages: number;
}

interface CallStatsResponse {
  totalCalls: number;
  totalMessages: number;
  totalCountries: number;
  countries: CountryStat[];
  lastUpdated: string;
}

// In-memory cache (refresh every 5 minutes)
let cachedStats: CallStatsResponse | null = null;
let cacheTime = 0;
const CACHE_TTL_MS = 5 * 60 * 1000;

/** Get an Azure access token using Managed Identity (IMDS) or Azure CLI */
async function getAzureToken(): Promise<string | null> {
  // 1. Try Managed Identity (works in Azure Container Apps)
  try {
    const identityEndpoint = globalThis.process?.env?.IDENTITY_ENDPOINT;
    const identityHeader = globalThis.process?.env?.IDENTITY_HEADER;

    if (identityEndpoint && identityHeader) {
      const url = `${identityEndpoint}?resource=https://api.loganalytics.io&api-version=2019-08-01`;
      const res = await fetch(url, {
        headers: { "X-IDENTITY-HEADER": identityHeader },
      });
      if (res.ok) {
        const data = (await res.json()) as { access_token: string };
        return data.access_token;
      }
    }
  } catch {
    // Fall through to Azure CLI
  }

  // 2. Try Azure CLI (works locally)
  try {
    const { execSync } = await import("node:child_process");
    const result = execSync(
      "az account get-access-token --resource https://api.loganalytics.io --query accessToken -o tsv",
      { timeout: 10000, encoding: "utf-8" },
    ).trim();
    if (result) return result;
  } catch {
    // No Azure CLI or not logged in
  }

  return null;
}

async function fetchCallStats(): Promise<CallStatsResponse> {
  const workspaceId = globalThis.process?.env?.LOG_ANALYTICS_WORKSPACE_ID;
  const token = await getAzureToken();

  if (!token || !workspaceId) {
    console.log(
      "[call-stats] No Azure token or workspace ID, using empty stats",
    );
    return {
      totalCalls: 0,
      totalMessages: 0,
      totalCountries: 0,
      countries: [],
      lastUpdated: new Date().toISOString(),
    };
  }

  // KQL: Get call counts and message counts per phone number prefix
  const kql = `
    let calls = AppEvents
    | where Name == 'OutboundCallInitiated'
    | extend phone = tostring(Properties['PhoneNumber'])
    | where isnotempty(phone)
    | summarize callCount = count() by phone;
    let msgs = AppEvents
    | where Name in ('UserMessage', 'AiMessage')
    | extend phone = tostring(Properties['chat.phone_number'])
    | where isnotempty(phone)
    | summarize msgCount = count() by phone;
    calls
    | join kind=leftouter msgs on phone
    | project phone, callCount, msgCount = coalesce(msgCount, 0)
  `;

  try {
    const url = `https://api.loganalytics.io/v1/workspaces/${workspaceId}/query`;
    const res = await fetch(url, {
      method: "POST",
      headers: {
        Authorization: `Bearer ${token}`,
        "Content-Type": "application/json",
      },
      body: JSON.stringify({ query: kql }),
    });

    if (!res.ok) {
      console.error(
        `[call-stats] Log Analytics query failed: ${res.status} ${res.statusText}`,
      );
      return {
        totalCalls: 0,
        totalMessages: 0,
        totalCountries: 0,
        countries: [],
        lastUpdated: new Date().toISOString(),
      };
    }

    const data = (await res.json()) as {
      tables: Array<{
        columns: Array<{ name: string }>;
        rows: Array<Array<string | number>>;
      }>;
    };

    // Aggregate by country
    const countryMap = new Map<string, CountryStat>();
    let totalCalls = 0;
    let totalMessages = 0;

    for (const row of data.tables[0]?.rows || []) {
      const phone = String(row[0]);
      const calls = Number(row[1]) || 0;
      const msgs = Number(row[2]) || 0;
      const country = phoneToCountry(phone);

      totalCalls += calls;
      totalMessages += msgs;

      if (country) {
        const existing = countryMap.get(country.code);
        if (existing) {
          existing.calls += calls;
          existing.messages += msgs;
        } else {
          countryMap.set(country.code, { ...country, calls, messages: msgs });
        }
      }
    }

    const countries = Array.from(countryMap.values()).sort(
      (a, b) => b.calls - a.calls,
    );

    return {
      totalCalls,
      totalMessages,
      totalCountries: countries.length,
      countries,
      lastUpdated: new Date().toISOString(),
    };
  } catch (err) {
    console.error("[call-stats] Error fetching call stats:", err);
    return {
      totalCalls: 0,
      totalMessages: 0,
      totalCountries: 0,
      countries: [],
      lastUpdated: new Date().toISOString(),
    };
  }
}

export default defineEventHandler(async () => {
  const now = Date.now();
  if (cachedStats && now - cacheTime < CACHE_TTL_MS) {
    return cachedStats;
  }

  cachedStats = await fetchCallStats();
  cacheTime = now;
  return cachedStats;
});
