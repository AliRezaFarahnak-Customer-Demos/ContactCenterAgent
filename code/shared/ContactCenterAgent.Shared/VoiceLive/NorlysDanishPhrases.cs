namespace ContactCenterAgent.Shared.VoiceLive;

/// <summary>
/// Norlys-specific Danish vocabulary boost for Azure Speech (<c>phrase_list</c>).
/// Used by both danish-voice-lab (laptop mic) and caller-agent (PSTN) so both
/// surfaces send the exact same hint set to the STT model.
///
/// Add real customer/product names as you discover them in the transcripts.
/// </summary>
public static class NorlysDanishPhrases
{
    /// <summary>Full phrase list. Safe to pass straight into Voice Live <c>phrase_list</c>.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        // ── Brand & subsidiaries ──────────────────────
        "Norlys", "Norlys Energi", "Norlys Tele", "Stofa",
        "Norlys Erhverv", "Norlys Privat", "Andel", "OK", "Ørsted",
        "EWII", "SEAS-NVE", "NRGi", "Verdo", "Nordlys",

        // ── Billing & payments ────────────────────────
        "regning", "faktura", "fakturanummer", "betaling", "betalingsservice",
        "PBS", "BetalingsService", "rykker", "rykkergebyr", "girokort",
        "MobilePay", "MitID", "NemID", "NemKonto", "abonnement",
        "opkrævning", "afregning", "acontobeløb", "aconto", "årsopgørelse",
        "depositum", "rentenota", "kreditnota", "tilgodehavende",
        "restance", "inkasso", "afdragsordning", "betalingsaftale",
        "moms", "afgift", "elafgift", "PSO-afgift", "transportbetaling",
        "nettarif", "abonnementsgebyr", "oprettelsesgebyr",
        "forbrug", "forbrugsafregning", "merforbrug", "tilbagebetaling",
        "kreditering", "saldo", "overforbrug", "underforbrug",

        // ── Onboarding, contracts & moving ───────────────────
        "onboarding", "tilmelding", "oprettelse", "kontrakt",
        "el-aftale", "elaftale", "gasaftale", "varmeaftale",
        "fjernvarme", "naturgas", "biogas",
        "bindingsperiode", "opsigelse", "opsigelsesvarsel",
        "fortrydelsesret", "leverandørskifte", "skift af elselskab",
        "flytning", "tilflytning", "fraflytning", "flyttemeddelelse",
        "flyttedato", "overtagelsesdato",
        "måleraflæsning", "selvaflæsning", "målernummer", "aftagernummer",
        "målerstand", "fjernaflæst måler", "elmåler", "varmemåler",
        "installationsadresse", "aftagepunkt", "EAN-nummer",

        // ── Products & tariffs ────────────────────────
        "fastpris", "variabel pris", "spotpris", "timepris",
        "grøn strøm", "vindenergi", "solcelle", "klimaaftale",
        "Energi Plus", "Energi Basis", "Trumf",
        "fiber", "fiberbredbånd", "bredbånd", "internet",
        "TV-pakke", "tv-pakke", "streaming", "Stofa WebTV",
        "mobilabonnement", "mobil", "fastnet", "telefoni",
        "router", "modem", "wifi", "hastighed",
        "hovedmåler", "bimåler", "ladestander", "elbil",

        // ── Customer & identity ───────────────────────
        "kundenummer", "CPR-nummer", "CVR-nummer", "kontonummer",
        "adresse", "postnummer", "vejnavn", "husnummer",
        "lejlighed", "etage", "telefonnummer", "mobilnummer",
        "e-mail", "mailadresse",

        // ── Common service phrases ────────────────────
        "kundeservice", "support", "teknisk support", "fejlmelding",
        "afbrydelse", "strømsvigt", "nedbrud", "driftforstyrrelse",
        "reklamation", "klage", "ankenævn", "Energiklagenævnet",
        "selvbetjening", "Mit Norlys", "app", "login",
        "kodeord", "nulstil adgangskode",
        "samtykke", "GDPR", "persondata", "tavshedspligt",

        // ── Greetings, frequent words misheard by ASR ─────────
        "godmorgen", "goddag", "godaften", "farvel", "tak",
        "ja tak", "nej tak", "et øjeblik", "vent venligst",
        "har du tid", "kan du hjælpe", "jeg vil gerne",
        "hvad koster det", "hvornår", "hvor lang tid",

        // ── Numbers spelled out (Danish often misheard) ───────
        "halvtreds", "tres", "halvfjerds", "firs", "halvfems",
        "hundrede", "tusinde",

        // ── Months & weekdays ────────────────────────
        "januar", "februar", "marts", "april", "maj", "juni",
        "juli", "august", "september", "oktober", "november", "december",
        "mandag", "tirsdag", "onsdag", "torsdag", "fredag", "lørdag", "søndag"
    };
}
