// ──────────────────────────────────────────────────────────────────────────────
// Speech Tool — Quick CLI for testing Azure Speech APIs (standard voices only)
//
// Commands:
//   dotnet run -- tts "Hello world"                          # Standard voice TTS
//   dotnet run -- tts "Hello world" --voice en-US-GuyNeural  # Specific voice
//   dotnet run -- voices                                     # List available voices
//   dotnet run -- voices --locale en-US                      # Filter by locale
//
// Auth: DefaultAzureCredential (az login)
// ──────────────────────────────────────────────────────────────────────────────

using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

const string DefaultEndpoint = "https://cog-contactcenteragent.cognitiveservices.azure.com";
const string DefaultRegion = "swedencentral";
const string DefaultVoice = "en-US-Ava:DragonHDLatestNeural";

// ── Shared options ───────────────────────────────────────────────────────────
var endpointOption = new Option<string>("--endpoint", () => DefaultEndpoint, "Azure AI Services endpoint");
var regionOption = new Option<string>("--region", () => DefaultRegion, "Azure region");

// ══════════════════════════════════════════════════════════════════════════════
// tts command
// ══════════════════════════════════════════════════════════════════════════════
var ttsCommand = new Command("tts", "Synthesize speech from text using a standard Azure voice");
var textArg = new Argument<string>("text", "Text to speak");
var voiceOption = new Option<string>("--voice", () => DefaultVoice, "Voice name (e.g., en-US-GuyNeural)");
var outputOption = new Option<string>("-o", () => "output.wav", "Output audio file path");
var playOption = new Option<bool>("--play", () => true, "Auto-play the output audio");

ttsCommand.AddArgument(textArg);
ttsCommand.AddOption(voiceOption);
ttsCommand.AddOption(outputOption);
ttsCommand.AddOption(playOption);
ttsCommand.AddOption(endpointOption);
ttsCommand.AddOption(regionOption);

ttsCommand.SetHandler(async (InvocationContext context) =>
{
    var text = context.ParseResult.GetValueForArgument(textArg);
    var voice = context.ParseResult.GetValueForOption(voiceOption)!;
    var outputFile = context.ParseResult.GetValueForOption(outputOption)!;
    var autoPlay = context.ParseResult.GetValueForOption(playOption);
    var endpoint = context.ParseResult.GetValueForOption(endpointOption)!;
    var region = context.ParseResult.GetValueForOption(regionOption)!;

    Console.WriteLine($"🔊 Synthesizing speech...");
    Console.WriteLine($"   Text: \"{text}\"");
    Console.WriteLine($"   Voice: {voice}");

    var speechToken = await GetSpeechTokenAsync(endpoint);
    var speechConfig = SpeechConfig.FromAuthorizationToken(speechToken, region);

    var ssml = $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
  <voice name='{voice}'>
    {EscapeXml(text)}
  </voice>
</speak>";

    using var audioConfig = AudioConfig.FromWavFileOutput(outputFile);
    using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);

    var sw = Stopwatch.StartNew();
    var result = await synthesizer.SpeakSsmlAsync(ssml);
    sw.Stop();

    if (result.Reason == ResultReason.SynthesizingAudioCompleted)
    {
        var fileInfo = new FileInfo(outputFile);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"   ✓ Audio saved: {outputFile} ({fileInfo.Length / 1024} KB, {sw.ElapsedMilliseconds}ms)");
        Console.ResetColor();

        if (autoPlay)
        {
            Console.WriteLine($"   ▶ Playing...");
            try
            {
                var psi = new ProcessStartInfo { FileName = outputFile, UseShellExecute = true };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ⚠ Could not auto-play: {ex.Message}");
            }
        }
    }
    else if (result.Reason == ResultReason.Canceled)
    {
        var cancellation = SpeechSynthesisCancellationDetails.FromResult(result);
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"   ✗ Synthesis canceled: {cancellation.Reason}");
        Console.WriteLine($"     Error: {cancellation.ErrorCode} — {cancellation.ErrorDetails}");
        Console.ResetColor();
    }
});

// ══════════════════════════════════════════════════════════════════════════════
// voices command — list available standard voices
// ══════════════════════════════════════════════════════════════════════════════
var voicesCommand = new Command("voices", "List available Azure TTS voices");
var localeFilterOption = new Option<string?>("--locale", "Filter by locale (e.g., en-US)");
var searchOption = new Option<string?>("--search", "Search voice names");

voicesCommand.AddOption(localeFilterOption);
voicesCommand.AddOption(searchOption);
voicesCommand.AddOption(endpointOption);
voicesCommand.AddOption(regionOption);

voicesCommand.SetHandler(async (InvocationContext context) =>
{
    var localeFilter = context.ParseResult.GetValueForOption(localeFilterOption);
    var search = context.ParseResult.GetValueForOption(searchOption);
    var endpoint = context.ParseResult.GetValueForOption(endpointOption)!;
    var region = context.ParseResult.GetValueForOption(regionOption)!;

    var speechToken = await GetSpeechTokenAsync(endpoint);
    var speechConfig = SpeechConfig.FromAuthorizationToken(speechToken, region);

    using var synthesizer = new SpeechSynthesizer(speechConfig);
    var result = await synthesizer.GetVoicesAsync();

    if (result.Reason == ResultReason.VoicesListRetrieved)
    {
        var voices = result.Voices.AsEnumerable();

        if (!string.IsNullOrEmpty(localeFilter))
            voices = voices.Where(v => v.Locale.StartsWith(localeFilter, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(search))
            voices = voices.Where(v => v.ShortName.Contains(search, StringComparison.OrdinalIgnoreCase)
                                    || v.LocalName.Contains(search, StringComparison.OrdinalIgnoreCase));

        var voiceList = voices.OrderBy(v => v.Locale).ThenBy(v => v.ShortName).ToList();
        Console.WriteLine($"Found {voiceList.Count} voices:");
        Console.WriteLine();

        var currentLocale = "";
        foreach (var v in voiceList)
        {
            if (v.Locale != currentLocale)
            {
                currentLocale = v.Locale;
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  [{currentLocale}]");
                Console.ResetColor();
            }
            var styles = v.StyleList.Length > 0 ? $" (styles: {string.Join(", ", v.StyleList)})" : "";
            Console.WriteLine($"    {v.ShortName,-45} {v.LocalName}{styles}");
        }
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ Failed to list voices: {result.Reason}");
        Console.ResetColor();
    }
});

// ══════════════════════════════════════════════════════════════════════════════
// danish-lab command — A/B test Danish HD Omni: styles (expected ignored),
// paralinguistics, temperature, English voice speaking Danish, etc.
// ══════════════════════════════════════════════════════════════════════════════
var danishLabCommand = new Command("danish-lab", "Play a battery of Danish TTS variants through the speaker so you can hear what HD Omni actually does on da-DK.");
var onlyOption = new Option<string?>("--only", "Run only variants whose id contains this substring (e.g. 'paralinguistic')");
var listOption = new Option<bool>("--list", () => false, "List all variants and exit (no synthesis)");
var pauseOption = new Option<int>("--pause-ms", () => 1200, "Pause between clips, in milliseconds");
var keepOption = new Option<bool>("--keep", () => false, "Keep generated WAV files after playback (default: delete)");

danishLabCommand.AddOption(onlyOption);
danishLabCommand.AddOption(listOption);
danishLabCommand.AddOption(pauseOption);
danishLabCommand.AddOption(keepOption);
danishLabCommand.AddOption(endpointOption);
danishLabCommand.AddOption(regionOption);

danishLabCommand.SetHandler(async (InvocationContext context) =>
{
    var only = context.ParseResult.GetValueForOption(onlyOption);
    var listOnly = context.ParseResult.GetValueForOption(listOption);
    var pauseMs = context.ParseResult.GetValueForOption(pauseOption);
    var keep = context.ParseResult.GetValueForOption(keepOption);
    var endpoint = context.ParseResult.GetValueForOption(endpointOption)!;
    var region = context.ParseResult.GetValueForOption(regionOption)!;

    // Reusable Danish lines
    const string DaNeutral = "Hej Mette, jeg ringer fra Norlys, har du tid et øjeblik?";
    const string DaEmpathetic = "Det er jeg virkelig ked af at høre. Lad os se hvad vi kan gøre sammen.";
    const string DaCheerful = "Super, det lyder rigtig dejligt at høre. Tak skal du have.";
    const string DaCalm = "Det er helt fint, vi tager det stille og roligt sammen.";
    const string DaApology = "Undskyld, det skulle ikke være sket. Det beklager jeg meget.";

    // Voices
    const string ChristelHD = "da-DK-Christel:DragonHDOmniLatestNeural";
    const string JeppeHD = "da-DK-Jeppe:DragonHDOmniLatestNeural";
    const string ChristelStandard = "da-DK-ChristelNeural";
    const string AvaHD = "en-US-Ava:DragonHDOmniLatestNeural";
    const string AndrewHD = "en-US-Andrew:DragonHDOmniLatestNeural";

    // Each variant: id, description, full SSML.
    var variants = new[]
    {
        // ── Baselines ────────────────────────────────────────────────────────
        new Variant("01-christel-hd-baseline",
            "Christel HD Omni, plain text, default temperature",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml(DaNeutral))),

        new Variant("02-christel-standard-baseline",
            "Christel STANDARD neural (current prod), plain — for A/B vs HD",
            DanishLabHelpers.BuildSsml("da-DK", ChristelStandard, EscapeXml(DaNeutral))),

        new Variant("03-jeppe-hd-baseline",
            "Jeppe HD Omni (male), plain — quick voice swap test",
            DanishLabHelpers.BuildSsml("da-DK", JeppeHD, EscapeXml(DaNeutral))),

        // ── Temperature sweep on Christel HD ─────────────────────────────────
        new Variant("10-christel-hd-temp-0.3",
            "Christel HD, temperature=0.3 (most stable)",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml(DaCheerful), parameters: "temperature=0.3")),

        new Variant("11-christel-hd-temp-1.0",
            "Christel HD, temperature=1.0 (most expressive)",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml(DaCheerful), parameters: "temperature=1.0")),

        // ── Style attempts — DOCS SAY ENGLISH ONLY, expected to be ignored ──
        new Variant("20-christel-hd-style-empathetic",
            "Christel HD + mstts:express-as style='empathetic' (EXPECTED: ignored — English only)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD, EscapeXml(DaEmpathetic), style: "empathetic")),

        new Variant("21-christel-hd-style-cheerful",
            "Christel HD + style='cheerful' (EXPECTED: ignored)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD, EscapeXml(DaCheerful), style: "cheerful")),

        new Variant("22-christel-hd-style-sad",
            "Christel HD + style='sad' (EXPECTED: ignored)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD, EscapeXml(DaApology), style: "sad")),

        new Variant("23-christel-hd-style-calm",
            "Christel HD + style='calm' (EXPECTED: ignored)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD, EscapeXml(DaCalm), style: "calm")),

        // ── Paralinguistic tags — DOCS SAY ALL LANGUAGES ────────────────────
        new Variant("30-christel-hd-paralinguistic-sigh",
            "Christel HD + inline [sighing] (EXPECTED: actually sighs)",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml("Det er jeg ked af at høre [sighing]. Lad os se hvad vi kan gøre."))),

        new Variant("31-christel-hd-paralinguistic-laugh",
            "Christel HD + inline [laughter] (EXPECTED: actually laughs)",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml("Hahaha, det var godt nok sjovt [laughter]. Tak for det."))),

        new Variant("32-christel-hd-paralinguistic-breathing",
            "Christel HD + inline [breathing]",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml("Et øjeblik [breathing]. Lad mig lige tjekke det for dig."))),

        // ── English HD Omni voice trying to speak Danish ────────────────────
        new Variant("40-ava-hd-speaks-danish",
            "Ava (en-US HD Omni) speaking Danish text directly (EXPECTED: thick American accent)",
            DanishLabHelpers.BuildSsml("en-US", AvaHD, EscapeXml(DaNeutral))),

        new Variant("41-andrew-hd-speaks-danish-via-lang",
            "Andrew (en-US HD Omni) with <lang xml:lang='da-DK'> wrapper",
            DanishLabHelpers.BuildSsmlWithLang("en-US", AndrewHD, "da-DK", EscapeXml(DaNeutral))),

        new Variant("42-ava-hd-style-empathetic-english-then-danish",
            "Ava HD + style='empathetic' on English text, then code-switches to Danish",
            DanishLabHelpers.BuildSsmlMixed(AvaHD)),

        // ── Combined: paralinguistic + temperature ──────────────────────────
        new Variant("50-christel-hd-paralinguistic-plus-temp",
            "Christel HD, [sighing] + temperature=1.0 (most expressive setup we can do for da-DK)",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD, EscapeXml("Puh [sighing], det har været en lang dag, men nu skal vi nok få det løst."), parameters: "temperature=1.0")),

        // ── Customer-service A/B set ─────────────────────────────────────────
        // Same call-center sentence repeated across multiple styles so you can
        // pick the one that actually sounds like a Norlys agent, not a robot or
        // a soap-opera actor. Listen for: warmth, professionalism, pacing.
        // Sentence chosen to cover greeting + acknowledgement + next step.
        new Variant("60-cs-baseline-no-style",
            "BASELINE (no style) — Christel HD on a real call-center line",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."))),

        new Variant("61-cs-style-customer-service",
            "Style='customer-service' — the obvious one (only documented for zh-CN HD Flash, but worth trying on da-DK HD Omni)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "customer-service")),

        new Variant("62-cs-style-friendly",
            "Style='friendly' — approachable agent",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "friendly")),

        new Variant("63-cs-style-empathetic",
            "Style='empathetic' — for tough conversations (high bills, complaints)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Det er jeg virkelig ked af at høre. Det forstår jeg godt er frustrerende. Lad mig se, hvad vi kan gøre for dig."),
                style: "empathetic")),

        new Variant("64-cs-style-calm",
            "Style='calm' — de-escalating an upset caller",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Det er helt i orden, vi tager det stille og roligt. Jeg er her for at hjælpe dig igennem det."),
                style: "calm")),

        new Variant("65-cs-style-cheerful",
            "Style='cheerful' — warm welcome",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, dejligt at få fat på dig. Jeg ringer fra Norlys for at høre, hvordan det går med din nye internetforbindelse."),
                style: "cheerful")),

        new Variant("66-cs-style-chat",
            "Style='chat' — casual, conversational (zh-CN HD Flash style — testing on da-DK)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Ja, det giver god mening. Lad os bare lige tjekke det sammen, så er vi sikre på det."),
                style: "chat")),

        new Variant("67-cs-style-professional",
            "Style='professional' — formal but not cold",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "professional")),

        new Variant("68-cs-style-newscast",
            "Style='newscast' — formal news-anchor delivery (sanity check: very different ≠ better)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "newscast")),

        new Variant("69-cs-style-customer-service-temp-0.4",
            "Style='customer-service' + temperature=0.4 — stable customer-service voice for prod use",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "customer-service", parameters: "temperature=0.4")),

        new Variant("70-cs-jeppe-customer-service",
            "Same 'customer-service' style on Jeppe (male HD Omni) — voice persona A/B",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", JeppeHD,
                EscapeXml("Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "customer-service")),

        // ── Narrated finalist comparison ─────────────────────────────────────
        // Same Norlys greeting, but each clip starts with a Danish self-intro
        // ("Nu lyder jeg sådan her med stilen 'X' …") so you can hear the label
        // and the voice back-to-back. Use this to make the final pick.
        new Variant("80-narrated-friendly",
            "NARRATED: 'friendly' on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg venlig. Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "friendly")),

        new Variant("81-narrated-professional",
            "NARRATED: 'professional' on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg professionel. Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "professional")),

        new Variant("82-narrated-customer-service",
            "NARRATED: 'customer-service' on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg som en kundeservicemedarbejder. Hej Mette, jeg ringer fra Norlys. Tak fordi du tog dig tid. Lad mig lige bekræfte din adresse, før vi går videre."),
                style: "customer-service")),

        // ── Judge set: same Danish Norlys greeting across 5 named moods ─────
        // Each clip prefixed with "Nu lyder jeg <mood>" so you can identify
        // the style by ear without checking labels. Order: normal → friendly
        // → professional → customer-service → shouting (loudest last).
        new Variant("90-judge-normal",
            "JUDGE: NORMAL (no style) on Christel HD Omni",
            DanishLabHelpers.BuildSsml("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg helt normalt. Jeg kan se på din konto, at din seneste regning er på to tusind og firehundrede kroner, og den forfalder den femtende maj."))),

        new Variant("91-judge-friendly",
            "JUDGE: FRIENDLY on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg venlig. Jeg kan se på din konto, at din seneste regning er på to tusind og firehundrede kroner, og den forfalder den femtende maj."),
                style: "friendly")),

        new Variant("92-judge-professional",
            "JUDGE: PROFESSIONAL on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg professionel. Jeg kan se på din konto, at din seneste regning er på to tusind og firehundrede kroner, og den forfalder den femtende maj."),
                style: "professional")),

        new Variant("93-judge-customer-service",
            "JUDGE: CUSTOMER-SERVICE on Christel HD Omni",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu lyder jeg som en kundeservicemedarbejder. Jeg kan se på din konto, at din seneste regning er på to tusind og firehundrede kroner, og den forfalder den femtende maj."),
                style: "customer-service")),

        new Variant("94-judge-shouting",
            "JUDGE: SHOUTING on Christel HD Omni (loudest — turn down volume)",
            DanishLabHelpers.BuildSsmlExpressAs("da-DK", ChristelHD,
                EscapeXml("Nu råber jeg! Din regning er på to tusind og firehundrede kroner, og den forfalder den femtende maj!"),
                style: "shouting")),
    };

    var selected = variants
        .Where(v => only is null || v.Id.Contains(only, StringComparison.OrdinalIgnoreCase))
        .ToList();

    Console.WriteLine($"Danish Voice Lab — {selected.Count} variant(s){(only is null ? "" : $" matching '{only}'")}");
    Console.WriteLine("Listen carefully and note: do styles change anything on Danish? Do paralinguistics actually fire?");
    Console.WriteLine();

    if (listOnly)
    {
        foreach (var v in selected)
        {
            Console.WriteLine($"  {v.Id}");
            Console.WriteLine($"    {v.Description}");
        }
        return;
    }

    var speechToken = await GetSpeechTokenAsync(endpoint);
    var speechConfig = SpeechConfig.FromAuthorizationToken(speechToken, region);

    var tempDir = Path.Combine(Path.GetTempPath(), "danish-voice-lab");
    Directory.CreateDirectory(tempDir);

    int idx = 0;
    foreach (var v in selected)
    {
        idx++;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"[{idx}/{selected.Count}] {v.Id}");
        Console.ResetColor();
        Console.WriteLine($"   {v.Description}");

        var outFile = Path.Combine(tempDir, $"{v.Id}.wav");
        try
        {
            using var audioConfig = AudioConfig.FromWavFileOutput(outFile);
            using var synthesizer = new SpeechSynthesizer(speechConfig, audioConfig);

            var sw = Stopwatch.StartNew();
            var result = await synthesizer.SpeakSsmlAsync(v.Ssml);
            sw.Stop();

            if (result.Reason == ResultReason.SynthesizingAudioCompleted)
            {
                var size = new FileInfo(outFile).Length / 1024;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"   ✓ {size} KB in {sw.ElapsedMilliseconds}ms");
                Console.ResetColor();
            }
            else if (result.Reason == ResultReason.Canceled)
            {
                var c = SpeechSynthesisCancellationDetails.FromResult(result);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"   ✗ Canceled: {c.Reason} — {c.ErrorCode} — {c.ErrorDetails}");
                Console.ResetColor();
                continue;
            }

            // Play synchronously through the default speaker so we can hear them in order.
            await PlayWavBlockingAsync(outFile);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"   ✗ Exception: {ex.Message}");
            Console.ResetColor();
        }
        finally
        {
            if (!keep)
            {
                try { File.Delete(outFile); } catch { /* best effort */ }
            }
        }

        if (idx < selected.Count)
        {
            await Task.Delay(pauseMs);
        }
    }

    Console.WriteLine();
    Console.WriteLine("Done. Tell me which clips actually sounded different from the Christel HD baseline (01).");
});

// ══════════════════════════════════════════════════════════════════════════════
// Root command
// ══════════════════════════════════════════════════════════════════════════════
var rootCommand = new RootCommand("Speech Tool — Quick CLI for testing Azure Speech APIs");
rootCommand.AddCommand(ttsCommand);
rootCommand.AddCommand(voicesCommand);
rootCommand.AddCommand(danishLabCommand);

return await rootCommand.InvokeAsync(args);

// ── Helpers ──────────────────────────────────────────────────────────────────
static string EscapeXml(string text) =>
    text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");

static async Task<string> GetSpeechTokenAsync(string endpoint)
{
    var credential = new DefaultAzureCredential();
    var token = await credential.GetTokenAsync(
        new TokenRequestContext(new[] { "https://cognitiveservices.azure.com/.default" }));
    using var stsClient = new HttpClient();
    stsClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    var stsResponse = await stsClient.PostAsync($"{endpoint}/sts/v1.0/issueToken", new StringContent(""));
    return await stsResponse.Content.ReadAsStringAsync();
}

static async Task PlayWavBlockingAsync(string wavFile)
{
    // Block until the WAV finishes so the next clip in the lab plays cleanly afterwards.
    if (OperatingSystem.IsWindows())
    {
        var ps = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -Command \"(New-Object System.Media.SoundPlayer '{wavFile}').PlaySync()\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(ps)!;
        await proc.WaitForExitAsync();
    }
    else
    {
        var ps = new ProcessStartInfo { FileName = wavFile, UseShellExecute = true };
        using var proc = Process.Start(ps);
        if (proc != null) await proc.WaitForExitAsync();
    }
}

// ── Danish lab helpers ──────────────────────────────────────────────────────
internal sealed record Variant(string Id, string Description, string Ssml);

internal static class DanishLabHelpers
{
    public static string BuildSsml(string xmlLang, string voice, string escapedText, string? parameters = null)
    {
        var paramAttr = parameters is null ? "" : $" parameters='{parameters}'";
        return $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='{xmlLang}'>
  <voice name='{voice}'{paramAttr}>{escapedText}</voice>
</speak>";
    }

    public static string BuildSsmlExpressAs(string xmlLang, string voice, string escapedText, string style, string? parameters = null)
    {
        var paramAttr = parameters is null ? "" : $" parameters='{parameters}'";
        return $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='{xmlLang}'>
  <voice name='{voice}'{paramAttr}>
    <mstts:express-as style='{style}'>{escapedText}</mstts:express-as>
  </voice>
</speak>";
    }

    public static string BuildSsmlWithLang(string outerXmlLang, string voice, string innerXmlLang, string escapedText)
    {
        return $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='{outerXmlLang}'>
  <voice name='{voice}'>
    <lang xml:lang='{innerXmlLang}'>{escapedText}</lang>
  </voice>
</speak>";
    }

    public static string BuildSsmlMixed(string voice)
    {
        // English HD voice with empathetic style on English, then code-switch to Danish via <lang>
        var enText = "I'm really sorry to hear that. Let me say something in Danish:";
        var daText = "Det er jeg virkelig ked af at høre. Vi finder en løsning sammen.";
        return $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='http://www.w3.org/2001/mstts' xml:lang='en-US'>
  <voice name='{voice}'>
    <mstts:express-as style='empathetic'>{enText}</mstts:express-as>
    <lang xml:lang='da-DK'>{daText}</lang>
  </voice>
</speak>";
    }
}

// Re-export the helpers as top-level statics so the danish-lab handler above can call
// them without a class qualifier. (Removed: top-level statements must precede type
// declarations; the handler now calls DanishLabHelpers.X directly.)

