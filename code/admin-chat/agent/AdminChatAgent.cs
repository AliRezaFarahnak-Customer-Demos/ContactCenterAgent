using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.AI.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.AI;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// -----------------------------------------------------------------------
// Builder & Services
// -----------------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

const string ServiceName = "AdminChatAgent";
const string SourceName = "ContactCenterAgent.AdminChat";

// Credential for Azure OpenAI + App Insights query API
var appCredential = new Azure.Identity.DefaultAzureCredential();

// -----------------------------------------------------------------------
// OpenTelemetry — Tracing, Metrics & Logging
//   → Azure Monitor / Application Insights (UseAzureMonitor distro)
//
// UseAzureMonitor() auto-configures:
//   - ASP.NET Core, HttpClient, SqlClient instrumentation
//   - Azure SDK tracing (Azure.AI.OpenAI calls)
//   - Trace / Metric / Log exporters to Application Insights
//   - Live Metrics stream
//   - Standard App Insights metrics
//
// We layer on top:
//   - Agent Framework sources (Microsoft.Agents.AI)
//   - Custom source (ContactCenterAgent.AdminChat)
//   - M.E.AI (Microsoft.Extensions.AI) source
//   - .NET Runtime metrics (GC, thread pool, etc.)
// -----------------------------------------------------------------------
var otel = builder.Services.AddOpenTelemetry()
    .ConfigureResource(res => res.AddService(ServiceName, serviceVersion: "0.1.0"));

// Azure Monitor distro — auto-detects APPLICATIONINSIGHTS_CONNECTION_STRING env var (set by Bicep)
// Skip when connection string is not set (local dev without App Insights)
var appInsightsCs = Environment.GetEnvironmentVariable("APPLICATIONINSIGHTS_CONNECTION_STRING");
if (!string.IsNullOrEmpty(appInsightsCs))
{
    otel.UseAzureMonitor(o =>
    {
        o.EnableLiveMetrics = true;
    });
}

// Add custom & framework sources on top of what the distro auto-configures
otel
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(SourceName)                      // ContactCenterAgent.AdminChat
            .AddSource("Microsoft.Agents.AI*")           // Agent Framework
            .AddSource("Microsoft.Extensions.AI*")       // M.E.AI chat client pipeline
            .AddSource("Azure.*");                       // Azure SDK (AI, Identity, etc.)
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter(SourceName)                        // ContactCenterAgent.AdminChat
            .AddMeter("Microsoft.Agents.AI*")            // Agent Framework
            .AddMeter("Microsoft.Extensions.AI*")        // M.E.AI
            .AddRuntimeInstrumentation();                // GC, thread pool, etc.
    });

// Logging — OpenTelemetry log export
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(ServiceName));
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
});

builder.Services.AddHttpClient().AddLogging();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Add(AdminChatSerializerContext.Default));
builder.Services.AddAGUI();

// CORS — allow the Nuxt UI to call the agent
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});

var app = builder.Build();
app.UseCors();

// Disable proxy buffering so SSE events stream immediately
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Response.Headers["Cache-Control"] = "no-cache";
    await next();
});

// -----------------------------------------------------------------------
// Create & Map Agent
// -----------------------------------------------------------------------
var agentFactory = new AdminChatAgentFactory(builder.Configuration, appCredential);
app.MapAGUI("/", agentFactory.CreateAgent());

await app.RunAsync();

// -----------------------------------------------------------------------
// Agent Factory — Microsoft Agent Framework + AG-UI
// -----------------------------------------------------------------------
public class AdminChatAgentFactory
{
    private const string SourceName = "ContactCenterAgent.AdminChat";
    private readonly IConfiguration _config;
    private static Azure.Core.TokenCredential _credential = null!;
    private static readonly HttpClient _httpClient = new();
    private static string? _appInsightsAppId;
    private static string? _callerAgentUrl;

    public AdminChatAgentFactory(IConfiguration config, Azure.Core.TokenCredential credential)
    {
        _config = config;
        _credential = credential;
        _appInsightsAppId = config["AppInsights:ApplicationId"];
        _callerAgentUrl = config["CallerAgent:Url"];
    }

    public AIAgent CreateAgent()
    {
        var chatClient = CreateChatClient();

        // Create the agent with OpenTelemetry instrumentation
        return new ChatClientAgent(
            chatClient,
            name: "AdminChatAgent",
            description: @"
                You are the **Contact Center Agent Admin** assistant — an AI-powered admin copilot
                for the Contact Center Agent platform. You help the administrator manage deployed
                agent applications running on Azure Container Apps.

                Your capabilities:
                - Answer questions about the Contact Center Agent platform
                - Report the current status of deployed agents
                - Help configure and troubleshoot agent apps
                - Provide guidance on Azure AI Foundry and Container Apps
                - **Make outbound AI phone calls** — when the admin asks you to call
                  someone, choose the right tool:
                  * `CallCustomerForOnboarding` — guide a new customer through
                    router / fiber installation. Always uses Danish + MFA.
                  * `CallCustomerAboutInvoice` — explain a customer's latest bill.
                    Always uses Danish + MFA.
                  * `MakePhoneCall` — generic call when no persona fits, you control
                    language and purpose.
                  All call tools start with mandatory identity verification (MFA)
                  using the verification facts you pass in.

                Be concise, professional, and helpful. Use markdown formatting where
                appropriate. If you don't know something, say so clearly.",
            tools:
            [
                AIFunctionFactory.Create(ListAgents),
                AIFunctionFactory.Create(GetAgentStatus),
                AIFunctionFactory.Create(GetPlatformInfo),
                AIFunctionFactory.Create(QueryAppInsights),
                AIFunctionFactory.Create(MakePhoneCall),
                AIFunctionFactory.Create(CallCustomerForOnboarding),
                AIFunctionFactory.Create(CallCustomerAboutInvoice),
            ])
            .AsBuilder()
            .UseOpenTelemetry(SourceName, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    private IChatClient CreateChatClient()
    {
        var azureEndpoint = _config["AzureOpenAI:Endpoint"]
            ?? throw new InvalidOperationException(
                "AzureOpenAI:Endpoint is not configured. " +
                "Set it via user-secrets: dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://<your-resource>.cognitiveservices.azure.com/\"");
        var modelName = _config["AzureOpenAI:Model"] ?? "gpt-5.4-nano";

        // Azure OpenAI with shared credential singleton (MI in Azure, AzureCli locally)
        var azureClient = new AzureOpenAIClient(
            new Uri(azureEndpoint),
            _credential);

        return azureClient.GetChatClient(modelName).AsIChatClient()
            .AsBuilder()
            .UseFunctionInvocation()
            .UseOpenTelemetry(sourceName: SourceName, configure: cfg => cfg.EnableSensitiveData = true)
            .Use(inner => new ChatEventTracker(inner))
            .Build();
    }

    // -------------------------------------------------------------------
    // Tools — callable by the agent via Microsoft Agent Framework
    // -------------------------------------------------------------------

    [Description("List all deployed agent apps on the Contact Center Agent platform")]
    private static AgentInfo[] ListAgents()
    {
        // TODO: Connect to Azure Table Storage / Container Apps management API
        return
        [
            new("admin-chat", "Admin Chat Agent", "Running", "Handles admin platform queries"),
        ];
    }

    [Description("Get the current status and health of a specific agent app")]
    private static AgentStatusResult GetAgentStatus(
        [Description("The agent app ID to check")] string agentId)
    {
        // TODO: Connect to Azure Container Apps API
        return new AgentStatusResult(
            agentId,
            "Running",
            DateTime.UtcNow.AddHours(-2),
            "Healthy",
            42);
    }

    [Description("Get information about the Contact Center Agent platform and its infrastructure")]
    private static PlatformInfo GetPlatformInfo()
    {
        return new PlatformInfo(
            Region: "swedencentral",
            Runtime: "Azure Container Apps",
            AIBackend: "Azure AI Foundry (GPT-5.4-nano)",
            Storage: "Azure Table Storage",
            AgentCount: 1);
    }

    [Description("Execute a KQL (Kusto Query Language) query against the platform's Application Insights to investigate telemetry, requests, exceptions, dependencies, traces, and performance metrics. Use this to help the admin monitor, troubleshoot, and analyze the platform.")]
    private static async Task<string> QueryAppInsights(
        [Description("The KQL query to execute against Application Insights (e.g. 'requests | take 10', 'exceptions | summarize count() by type')")] string query)
    {
        if (string.IsNullOrEmpty(_appInsightsAppId))
            return "Error: AppInsights:ApplicationId is not configured. Set it in appsettings.json or user-secrets.";

        try
        {
            var queryUrl = $"https://api.applicationinsights.io/v1/apps/{_appInsightsAppId}/query";

            // Use shared credential singleton (MI in Azure, AzureCli locally)
            var tokenResult = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://api.applicationinsights.io/.default"]),
                CancellationToken.None);

            var payload = JsonSerializer.Serialize(new { query });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, queryUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);
            request.Content = content;

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return $"Error executing App Insights query: {ex.Message}";
        }
    }

    [Description("Initiate an outbound AI phone call. The AI caller agent will phone the given number and have a voice conversation about the specified topic. Use this when the admin asks to call someone.")]
    private static async Task<PhoneCallResult> MakePhoneCall(
        [Description(@$"
        Country calling code without '+' (e.g. '1' for US, '45' for Denmark, '46' for Sweden). 
        You never ask user for a country code number or digits for this but just what country are you calling.
        Whitespace is stripped automatically, if user just says country you know the country code.")] string countryCode,
        [Description("Phone number without country code (e.g. '80719050', '5551234567'). Whitespace is stripped automatically — '80 71 90 50' becomes '80719050'.")] string phoneNumber,
        [Description("The topic or purpose of the call - this becomes the AI caller's conversation goal")] string talkAboutThis,
        [Description("Name of the person being called (e.g. 'Ali', 'John'). The AI will greet them by name.")] string name,
        [Description("The EXACT language the AI caller must speak during the call. Be very specific — never leave ambiguous. Examples: 'Danish' (NOT 'Scandinavian'), 'English' (NOT 'British'), 'Swedish', 'Turkish', 'Arabic', 'French', 'German', 'Spanish'. If the user says the person speaks Danish or is in Denmark, set this to 'Danish'. If the user speaks English or is in the US/UK, set this to 'English'. Defaults to English.")] string language = "English",
        [Description("The ISO 639-1 two-letter language code matching the language parameter. You MUST classify this yourself — examples: 'da' for Danish, 'en' for English, 'sv' for Swedish, 'de' for German, 'fr' for French, 'es' for Spanish, 'tr' for Turkish, 'ar' for Arabic, 'nl' for Dutch, 'no' for Norwegian, 'fi' for Finnish, 'pl' for Polish, 'it' for Italian, 'pt' for Portuguese, 'ru' for Russian, 'ja' for Japanese, 'ko' for Korean, 'zh' for Chinese, 'hi' for Hindi, 'el' for Greek, 'cs' for Czech, 'hu' for Hungarian, 'ro' for Romanian, 'uk' for Ukrainian. Defaults to 'en'.")] string languageCode = "en",
        [Description("Optional comma-separated keyword list in the target language. UNUSED in production (STT is azure-speech, which uses the static phrase_list in caller-agent appsettings instead). Kept for one-off A/B tests with whisper-1 / gpt-4o-transcribe — those models would use this as a free-text 'prompt' vocabulary hint. Safe to leave null.")] string? transcriptionHint = null)
    {
        if (string.IsNullOrEmpty(_callerAgentUrl))
            return new PhoneCallResult(false, null, "Caller agent URL is not configured (CallerAgent:Url).");

        // Strip all whitespace and build E.164 phone number: +<countryCode><phoneNumber>
        countryCode = countryCode.Replace(" ", "").Replace("\t", "");
        phoneNumber = phoneNumber.Replace(" ", "").Replace("\t", "").Replace("-", "");
        var fullNumber = $"+{countryCode.TrimStart('+')}{phoneNumber.TrimStart('0')}";

        try
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                phoneNumber = fullNumber,
                purpose = talkAboutThis,
                name = name,
                language = language,
                languageCode = languageCode,
                transcriptionHint = transcriptionHint
            });

            var url = $"{_callerAgentUrl.TrimEnd('/')}/api/outboundCall";
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                // Parse contextId from caller agent response for live transcription
                string? callId = null;
                try
                {
                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    if (jsonDoc.RootElement.TryGetProperty("contextId", out var ctxProp))
                    {
                        callId = ctxProp.GetString();
                    }
                }
                catch { /* Non-critical — transcription just won't work */ }

                return new PhoneCallResult(true, fullNumber, $"Call initiated successfully to {fullNumber}. The AI agent is now calling and will have a live voice conversation.", callId);
            }
            else
            {
                return new PhoneCallResult(false, fullNumber, $"Failed to initiate call (HTTP {(int)response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            return new PhoneCallResult(false, fullNumber, $"Error initiating call: {ex.Message}");
        }
    }

    // -------------------------------------------------------------------
    // Persona-based call tools — onboarding & invoice
    // -------------------------------------------------------------------

    private const string OnboardingInstructions = @"Du er en venlig tekniksupporter, der ringer til en ny kunde for at hjælpe dem i gang med deres internetforbindelse og router.

Dit mål med samtalen:
1. Bekræft, at routeren og fiberboksen er pakket ud og strømmen er tilsluttet.
2. Guid kunden trin-for-trin gennem opsætningen:
   - Tilslut fiberkablet i WAN-porten på routeren.
   - Tænd routeren og vent ~2 minutter på, at lampen lyser konstant grønt.
   - Forbind telefon eller computer til Wi-Fi'et (navn og kode står på undersiden af routeren).
3. Spørg om alt virker, og lav en hurtig hastighedstest hvis muligt.
4. Tilbyd at booke en tekniker hvis noget ikke virker.

Hvis kunden allerede er online, ros dem og afslut høfligt.";

    private const string InvoiceInstructions = @"Du er en venlig kundeservicemedarbejder, der ringer til en kunde for at gennemgå deres seneste regning, fordi kunden har anmodet om en forklaring.

Dit mål med samtalen:
1. Forklar at du ringer ift. den seneste faktura.
2. Gennemgå hovedposterne på regningen ud fra konteksten:
   - Abonnement / fast pris
   - Forbrug (el / gas / internet alt efter produkt)
   - Eventuelle gebyrer eller engangsbeløb
3. Svar på spørgsmål, og hvis kunden er uenig, tilbyd at oprette en sag til deres regningsteam.
4. Mind kunden om, at de altid kan se detaljer på selvbetjeningen.

Vær empatisk hvis kunden er overrasket over beløbet.";

    [Description("Call a new customer in Danish to walk them through onboarding and modem/router installation. Begins with mandatory identity verification (MFA) using the supplied verification facts before any technical guidance. Use this when the admin asks to onboard a customer, help install a modem/router, or set up a fiber connection.")]
    private static Task<PhoneCallResult> CallCustomerForOnboarding(
        [Description("Customer's first name (used by the AI to greet them)")] string customerName,
        [Description("Country calling code without '+' (e.g. '45' for Denmark). Defaults to '45'.")] string countryCode,
        [Description("Phone number without country code (e.g. '80719050'). Whitespace and dashes are stripped automatically.")] string phoneNumber,
        [Description("Verification facts on file used for MFA. Free-text — typically 1–3 lines, e.g. 'Adresse: Hovedgaden 12, 8000 Aarhus C\\nEmail: kunde@example.dk'. The AI will ask 1–2 light security questions based on these BEFORE proceeding with the call topic.")] string verificationFacts,
        [Description("Optional extra context the AI should know about the customer's order (router model, fiber speed, ship date, etc.). Leave empty if not known.")] string notes = "")
    {
        return PlacePersonaCall(
            personaLabel: "Onboarding & modem-installation",
            personaInstructions: OnboardingInstructions,
            customerName: customerName,
            countryCode: countryCode,
            phoneNumber: phoneNumber,
            verificationFacts: verificationFacts,
            notes: notes);
    }

    [Description("Call a customer in Danish to explain their latest invoice / bill. Begins with mandatory identity verification (MFA) using the supplied verification facts before any billing details are discussed. Use this when the admin asks to call a customer about an invoice, regning, faktura, or bill.")]
    private static Task<PhoneCallResult> CallCustomerAboutInvoice(
        [Description("Customer's first name (used by the AI to greet them)")] string customerName,
        [Description("Country calling code without '+' (e.g. '45' for Denmark). Defaults to '45'.")] string countryCode,
        [Description("Phone number without country code (e.g. '80719050'). Whitespace and dashes are stripped automatically.")] string phoneNumber,
        [Description("Verification facts on file used for MFA. Free-text — typically 1–3 lines, e.g. 'Adresse: Søndergade 4, 9000 Aalborg\\nEmail: kunde@example.dk'. The AI will ask 1–2 light security questions based on these BEFORE discussing the bill.")] string verificationFacts,
        [Description("Invoice details to explain — amount, period, line items. E.g. 'November 2025: 1842 kr — 1290 kr forbrug, 450 kr abonnement, 102 kr afgifter'. The AI uses this when explaining the bill.")] string invoiceDetails)
    {
        return PlacePersonaCall(
            personaLabel: "Forklaring af regning",
            personaInstructions: InvoiceInstructions,
            customerName: customerName,
            countryCode: countryCode,
            phoneNumber: phoneNumber,
            verificationFacts: verificationFacts,
            notes: invoiceDetails);
    }

    /// <summary>
    /// Shared helper: builds the persona + MFA system prompt and POSTs to the caller agent.
    /// Keeps the same prompt structure as <c>server/api/place-call.post.ts</c>.
    /// </summary>
    private static async Task<PhoneCallResult> PlacePersonaCall(
        string personaLabel,
        string personaInstructions,
        string customerName,
        string countryCode,
        string phoneNumber,
        string verificationFacts,
        string notes)
    {
        if (string.IsNullOrEmpty(_callerAgentUrl))
            return new PhoneCallResult(false, null, "Caller agent URL is not configured (CallerAgent:Url).");

        var cc = (string.IsNullOrWhiteSpace(countryCode) ? "45" : countryCode)
            .Replace(" ", "").Replace("\t", "").TrimStart('+');
        var local = phoneNumber.Replace(" ", "").Replace("\t", "").Replace("-", "").TrimStart('0');
        var fullNumber = $"+{cc}{local}";

        var facts = string.IsNullOrWhiteSpace(verificationFacts) ? "(none provided)" : verificationFacts.Trim();
        var notesBlock = string.IsNullOrWhiteSpace(notes) ? "" : $"## Yderligere kontekst:\n{notes.Trim()}\n\n";

        var systemPrompt =
            $"Du er en AI-kundeservicemedarbejder, der ringer til {customerName} på {fullNumber}.\n\n" +
            $"## Din rolle: {personaLabel}\n{personaInstructions.Trim()}\n\n" +
            "## OBLIGATORISK identitetsverifikation (MFA) — DETTE FØRST\n" +
            "Før du diskuterer NOGEN kontooplysninger, regningsinformation, teknisk opsætning eller andet følsomt, SKAL du verificere kundens identitet.\n" +
            "Åbn samtalen med kort at forklare hvorfor: \"For at beskytte din konto skal jeg lige stille et par hurtige sikkerhedsspørgsmål, før vi går videre.\"\n" +
            "Stil derefter 1–2 lette spørgsmål baseret på fakta nedenfor (fx \"Kan du bekræfte din adresse?\" eller \"Hvilken e-mail har vi registreret på dig?\"). Stil ét ad gangen.\n" +
            "Hvis svaret matcher de registrerede fakta, bekræft og fortsæt. Hvis det IKKE matcher efter to forsøg, forklar høfligt at du ikke kan fortsætte og afslut samtalen.\n" +
            "Læs ALDRIG svarene højt, og fortsæt ALDRIG til hovedemnet før verifikationen er bestået.\n\n" +
            $"### Verifikationsfakta på fil (FORTROLIGE — må aldrig læses op):\n{facts}\n\n" +
            notesBlock +
            "## Stil\n" +
            "- Tal naturligt og varmt på dansk.\n" +
            "- Hold ture korte og samtaleagtige.\n" +
            "- Hvis kunden vil tale med et menneske, tilbyd at oprette en sag eller en tilbagekald.";

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                phoneNumber = fullNumber,
                purpose = personaLabel,
                systemPrompt,
                name = customerName,
                language = "Danish",
                languageCode = "da",
                transcriptionHint = "Hej, ja, nej, adresse, e-mail, faktura, regning, router, internet, fiber, tak, hej hej",
            });

            var url = $"{_callerAgentUrl.TrimEnd('/')}/api/outboundCall";
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(url, content);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return new PhoneCallResult(false, fullNumber, $"Failed to initiate call (HTTP {(int)response.StatusCode}): {body}");

            string? callId = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("contextId", out var ctx))
                    callId = ctx.GetString();
            }
            catch { }

            return new PhoneCallResult(
                true,
                fullNumber,
                $"Call initiated to {fullNumber} ({personaLabel}). The AI will start with MFA verification before proceeding.",
                callId);
        }
        catch (Exception ex)
        {
            return new PhoneCallResult(false, fullNumber, $"Error initiating call: {ex.Message}");
        }
    }
}

// -----------------------------------------------------------------------
// Telemetry — custom event tracker for user & AI messages
// -----------------------------------------------------------------------

/// <summary>
/// Logs user and AI chat messages as custom telemetry spans via OpenTelemetry.
/// Shows up in Application Insights <c>dependencies</c> table with
/// <c>chat.event_type</c> and <c>chat.content</c> custom dimensions.
/// Query: dependencies | where name in ("UserMessage", "AiMessage")
/// </summary>
public sealed class ChatEventTracker(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly ActivitySource Source = new("ContactCenterAgent.AdminChat");

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        TrackUserMessage(chatMessages);
        var response = await base.GetResponseAsync(chatMessages, options, cancellationToken);
        TrackAiMessage(response.Text);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        TrackUserMessage(chatMessages);

        var sb = new StringBuilder();
        await foreach (var update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken))
        {
            if (update.Text is not null) sb.Append(update.Text);
            yield return update;
        }

        TrackAiMessage(sb.ToString());
    }

    private static void TrackUserMessage(IEnumerable<ChatMessage> messages)
    {
        var last = messages.LastOrDefault(m => m.Role == ChatRole.User);
        if (last is null) return;

        using var activity = Source.StartActivity("UserMessage", ActivityKind.Internal);
        activity?.SetTag("chat.event_type", "UserMessage");
        activity?.SetTag("chat.content", last.Text);
    }

    private static void TrackAiMessage(string? content)
    {
        if (string.IsNullOrEmpty(content)) return;

        using var activity = Source.StartActivity("AiMessage", ActivityKind.Internal);
        activity?.SetTag("chat.event_type", "AiMessage");
        activity?.SetTag("chat.content", content);
    }
}

// -----------------------------------------------------------------------
// Data models
// -----------------------------------------------------------------------
public record AgentInfo(string Id, string Name, string Status, string Description);
public record AgentStatusResult(string AgentId, string Status, DateTime LastStarted, string Health, int RequestCount);
public record PlatformInfo(string Region, string Runtime, string AIBackend, string Storage, int AgentCount);
public record PhoneCallResult(bool Success, string? PhoneNumber, string Message, string? CallId = null);

public partial class Program { }

// -----------------------------------------------------------------------
// JSON serialization context (AOT-compatible)
// -----------------------------------------------------------------------
[JsonSerializable(typeof(AgentInfo[]))]
[JsonSerializable(typeof(AgentStatusResult))]
[JsonSerializable(typeof(PlatformInfo))]
[JsonSerializable(typeof(PhoneCallResult))]
internal sealed partial class AdminChatSerializerContext : JsonSerializerContext;

