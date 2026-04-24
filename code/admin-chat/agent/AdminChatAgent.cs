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
                  someone, use the MakePhoneCall tool with country code, phone number,
                  and topic. The AI caller agent will have a live voice conversation.

                Be concise, professional, and helpful. Use markdown formatting where
                appropriate. If you don't know something, say so clearly.",
            tools:
            [
                AIFunctionFactory.Create(ListAgents),
                AIFunctionFactory.Create(GetAgentStatus),
                AIFunctionFactory.Create(GetPlatformInfo),
                AIFunctionFactory.Create(QueryAppInsights),
                AIFunctionFactory.Create(MakePhoneCall),
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
        [Description("A comma-separated list of keywords and short phrases in the target language that guide the whisper-1 speech-to-text model. whisper-1 uses this as a vocabulary hint — keywords work better than full sentences. Include: common greetings, yes/no words, topic-specific terms matching the call purpose, and goodbye words. Examples for a Danish invoice call: 'Hej, ja, nej, faktura, betaling, konto, i morgen, godt nok, tak, hej hej'. Examples for an English subscription renewal: 'Hello, yes, no, subscription, renewal, cancel, email, okay, thanks, bye'. Always generate fresh keywords matching the call topic and language — NEVER hardcode or reuse examples.")] string? transcriptionHint = null)
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

