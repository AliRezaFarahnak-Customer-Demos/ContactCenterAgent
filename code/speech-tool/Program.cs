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
// Root command
// ══════════════════════════════════════════════════════════════════════════════
var rootCommand = new RootCommand("Speech Tool — Quick CLI for testing Azure Speech APIs");
rootCommand.AddCommand(ttsCommand);
rootCommand.AddCommand(voicesCommand);

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
