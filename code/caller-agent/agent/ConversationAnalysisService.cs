using System.Text.Json;
using System.Threading.Channels;
using Azure.AI.OpenAI;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace CallAutomation.AzureAI.VoiceLive;

/// <summary>
/// Real-time conversation analysis using GPT-5.6 structured outputs.
/// Listens to transcript events and produces 15-category sentiment scores (0-6, where 3 = neutral)
/// after every new speaker turn.
/// </summary>
public class ConversationAnalysisService : IDisposable
{
    private readonly ILogger<ConversationAnalysisService> _logger;
    private readonly TelemetryClient? _telemetryClient;
    private readonly ChatClient _chatClient;
    private readonly ChannelWriter<AnalysisResult> _analysisWriter;
    private readonly List<TranscriptLine> _conversationHistory = new();
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private bool _disposed;

    // JSON schema for structured output — 15 categories, each 0-6 (3 = neutral / not enough signal)
    private static readonly BinaryData s_analysisSchema = BinaryData.FromBytes("""
        {
            "type": "object",
            "properties": {
                "purchase_intent": { "type": "integer", "description": "Likelihood the caller will buy a product (0=none, 3=neutral/unknown, 6=very likely)" },
                "customer_mood": { "type": "integer", "description": "Overall emotional state (0=very angry, 3=neutral, 6=delighted)" },
                "cooperativeness": { "type": "integer", "description": "Willingness to engage and work with the agent (0=hostile, 3=neutral, 6=very cooperative)" },
                "brand_perception": { "type": "integer", "description": "How the caller feels about the brand or product (0=very negative, 3=neutral, 6=very positive)" },
                "company_satisfaction": { "type": "integer", "description": "Satisfaction with the company overall (0=very unhappy, 3=neutral, 6=very happy)" },
                "urgency": { "type": "integer", "description": "How urgent the caller's need is (0=no urgency, 3=normal, 6=extremely urgent)" },
                "engagement": { "type": "integer", "description": "How actively the caller is participating (0=disengaged, 3=neutral, 6=highly engaged)" },
                "frustration": { "type": "integer", "description": "Signs of frustration or irritation (0=none, 3=neutral, 6=extremely frustrated)" },
                "trust_in_agent": { "type": "integer", "description": "Confidence the caller has in the agent (0=no trust, 3=neutral, 6=full trust)" },
                "churn_risk": { "type": "integer", "description": "Likelihood the caller will leave or cancel (0=no risk, 3=unclear, 6=very high risk)" },
                "upsell_opportunity": { "type": "integer", "description": "Potential for additional sales or upgrades (0=none, 3=unclear, 6=strong opportunity)" },
                "resolution_progress": { "type": "integer", "description": "How close the conversation is to resolving the issue (0=not started, 3=in progress, 6=fully resolved)" },
                "politeness": { "type": "integer", "description": "Caller's tone and manners (0=very rude, 3=neutral, 6=very polite)" },
                "call_effectiveness": { "type": "integer", "description": "How productive the conversation is (0=unproductive, 3=neutral, 6=very productive)" },
                "overall_sentiment": { "type": "integer", "description": "Net positive or negative feeling (0=very negative, 3=neutral, 6=very positive)" }
            },
            "required": [
                "purchase_intent", "customer_mood", "cooperativeness", "brand_perception",
                "company_satisfaction", "urgency", "engagement", "frustration", "trust_in_agent",
                "churn_risk", "upsell_opportunity", "resolution_progress", "politeness",
                "call_effectiveness", "overall_sentiment"
            ],
            "additionalProperties": false
        }
        """u8.ToArray());

    // Structured end-of-interaction summary — the cross-channel case backbone.
    // Runs ONCE when an interaction ends (not per-turn like the sentiment scores),
    // producing the outcome, a Danish summary, topics, and a ready-to-send follow-up
    // draft that the SMS/email composer injects as prior context.
    private static readonly BinaryData s_caseSummarySchema = BinaryData.FromBytes("""
        {
            "type": "object",
            "properties": {
                "summary": { "type": "string", "description": "2-3 sentence factual summary of the interaction, in Danish. No emojis, no markdown." },
                "outcome": { "type": "string", "enum": ["resolved", "callback_needed", "verification_failed", "escalated", "no_answer", "other"], "description": "Single best-fit outcome of the interaction." },
                "topics": { "type": "array", "items": { "type": "string" }, "description": "1-4 short Danish topic tags, e.g. regning, fiber, flytning." },
                "verified": { "type": "boolean", "description": "True only if the caller passed MFA (address + one more security answer) in this interaction." },
                "followUpNeeded": { "type": "boolean", "description": "True if the case is not fully resolved and a follow-up on another channel is warranted." },
                "followUpDraft": { "type": "string", "description": "If followUpNeeded, a short Danish follow-up message body suitable for SMS or email. Empty string otherwise. No emojis, no markdown." },
                "actions": {
                    "type": "array",
                    "description": "Concrete tasks the person on the call asked to have carried out (close a report, reply to an email or Teams message, etc). Empty array if none.",
                    "items": {
                        "type": "object",
                        "properties": {
                            "type": { "type": "string", "enum": ["close_report", "update_report", "reply_email", "reply_teams", "send_email", "send_teams", "create_task", "create_request", "book_resource", "escalate_ticket", "status_update", "other"] },
                            "target": { "type": "string", "description": "Who or what the action applies to: customer, report, person, or thread, e.g. Fjordvik Logistics." },
                            "content": { "type": "string", "description": "The text to submit or send, written ready to paste, in the language spoken on the call." },
                            "status": { "type": "string", "enum": ["ready", "postponed", "open"], "description": "ready = solved, the person gave everything needed; postponed = they agreed to do it another day; open = not solved, it still needs an answer." },
                            "priority": { "type": "string", "enum": ["today", "later"], "description": "today = must be answered or closed today; later = can wait." }
                        },
                        "required": ["type", "target", "content", "status", "priority"],
                        "additionalProperties": false
                    }
                }
            },
            "required": ["summary", "outcome", "topics", "verified", "followUpNeeded", "followUpDraft", "actions"],
            "additionalProperties": false
        }
        """u8.ToArray());

    private const string CaseSummarySystemPrompt =
        """
        You summarize a completed Norlys customer-service interaction for a cross-channel case file.
        Output STRICTLY per the schema. Write summary, topics, and followUpDraft in DANISH.
        Rules:
        - summary: 2-3 factual sentences. What the customer wanted and what happened. No opinions, no emojis, no markdown, no asterisks.
        - outcome: pick the single best fit. 'resolved' only if the customer's need was fully met. 'callback_needed' if something was promised or left open. 'verification_failed' if MFA did not pass. 'escalated' if handed to a human. 'no_answer' if no real conversation happened.
        - verified: true ONLY if the customer confirmed their address AND one more security answer this interaction. When the call instructions define another security check (for example an alias plus upcoming engagements), true only if that check passed.
        - followUpNeeded: true when the case is open or something was promised.
        - followUpDraft: if followUpNeeded, write a warm, concrete Danish message (under 320 chars, SMS-safe) the customer can receive on another channel. Reference the concrete open point. No emojis, no markdown. Empty string if no follow-up needed.
        - actions: one entry per task listed in the call instructions (only when they contain an explicit task list) plus every extra task the person asked for on the call, e.g. "close the report for Fjordvik Logistics, we made the POC production ready today" gives {type: close_report, target: "Fjordvik Logistics", content: "The POC was made production ready today.", status: ready, priority: today}. Write content ready to paste, in the language spoken on the call, and include every detail they gave (dates, names, numbers). status: ready when solved, postponed when they chose another day, open when it was not answered. priority: take it from the instructions (today for must-answer items, later for items that can wait); extra tasks from the call are today unless they said otherwise. For open items, content says what is still missing. If the security check failed, return an empty actions array. Empty array when no such tasks exist, which is normal for customer-service calls. Type guide: create_request for a new request or engagement for a customer, book_resource for booking or confirming a person such as a CSA, escalate_ticket for raising the priority of a support case, status_update for a status the person gave that must be written down or passed on.
        """;

    private const string AnalysisSystemPrompt =
        """
        You are a real-time call center analytics engine. You analyze phone call transcripts and score the conversation across 15 categories on a 0-6 scale, where 3 means NEUTRAL.

        Rules:
        - Score each category based ONLY on what has been said so far in the conversation.
        - 3 is the neutral midpoint. Use 3 whenever the caller has not yet given a clear positive OR negative signal for that category (e.g. early greetings, identity verification only, no problem stated yet).
        - Use 0–2 only when there is clear NEGATIVE evidence (anger, refusal, dissatisfaction, frustration).
        - Use 4–6 only when there is clear POSITIVE evidence (satisfaction, agreement, gratitude, resolution).
        - Be precise and conservative — don't inflate scores without evidence. When in doubt, return 3 (neutral).
        - Update scores as the conversation evolves; early greetings should mostly show 3s.
        - Frustration and churn_risk are INVERSE to positive sentiment — high frustration (>3) implies low mood (<3).
        - Consider tone, word choice, and context when scoring.
        - All scores must be integers from 0 to 6 inclusive.
        """;

    public ConversationAnalysisService(
        IConfiguration configuration,
        ILogger<ConversationAnalysisService> logger,
        Azure.Core.TokenCredential credential,
        ChannelWriter<AnalysisResult> analysisWriter,
        TelemetryClient? telemetryClient = null)
    {
        _logger = logger;
        _telemetryClient = telemetryClient;
        _analysisWriter = analysisWriter;

        var endpoint = configuration.GetValue<string>("AzureOpenAI:Endpoint");
        ArgumentNullException.ThrowIfNullOrEmpty(endpoint);

        var deploymentName = configuration.GetValue<string>("AzureOpenAI:AnalysisDeploymentName") ?? ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults.AnalysisModel;

        // Create ChatClient with Azure OpenAI using DefaultAzureCredential
        var azureClient = new AzureOpenAIClient(new Uri(endpoint), credential);
        _chatClient = azureClient.GetChatClient(deploymentName);
        _logger.LogInformation("ConversationAnalysisService initialized with model {Model}", deploymentName);
    }

    /// <summary>
    /// Add a new transcript line and trigger analysis.
    /// Called after each user or AI utterance.
    /// </summary>
    public async Task AddTranscriptAndAnalyzeAsync(string speaker, string text)
    {
        _conversationHistory.Add(new TranscriptLine(speaker, text));
        Console.WriteLine($"[Analysis] Added {speaker} utterance, total {_conversationHistory.Count} lines");
        _logger.LogInformation("Analysis: added {Speaker} utterance, total {Count} lines", speaker, _conversationHistory.Count);

        // Run analysis (non-blocking if another is already running)
        if (_analysisLock.CurrentCount > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunAnalysisAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Analysis] UNHANDLED ERROR in Task.Run: {ex}");
                }
            });
        }
        else
        {
            Console.WriteLine("[Analysis] Skipped — analysis already in flight");
        }
    }

    private async Task RunAnalysisAsync()
    {
        if (!await _analysisLock.WaitAsync(0)) return; // skip if already running

        try
        {
            // Build the conversation transcript
            var transcript = string.Join("\n", _conversationHistory.Select(l => $"{l.Speaker}: {l.Text}"));

            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "conversation_analysis",
                    jsonSchema: s_analysisSchema,
                    jsonSchemaIsStrict: true)
                // Do NOT set MaxOutputTokenCount — Azure.AI.OpenAI v2.1.0 sends 'max_tokens'
                // but GPT-5.4-nano requires 'max_completion_tokens'. Omitting uses the model's default.
            };

            var messages = new ChatMessage[]
            {
                new SystemChatMessage(AnalysisSystemPrompt),
                new UserChatMessage($"Analyze this phone call transcript:\n\n{transcript}")
            };

            _logger.LogInformation("Running structured output analysis on {Lines} lines", _conversationHistory.Count);

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var content = completion.Value.Content[0].Text;

            _logger.LogDebug("Analysis result: {Result}", content);

            var scores = JsonSerializer.Deserialize<Dictionary<string, int>>(content);
            if (scores != null)
            {
                var result = new AnalysisResult(
                    PurchaseIntent: scores.GetValueOrDefault("purchase_intent"),
                    CustomerMood: scores.GetValueOrDefault("customer_mood"),
                    Cooperativeness: scores.GetValueOrDefault("cooperativeness"),
                    BrandPerception: scores.GetValueOrDefault("brand_perception"),
                    CompanySatisfaction: scores.GetValueOrDefault("company_satisfaction"),
                    Urgency: scores.GetValueOrDefault("urgency"),
                    Engagement: scores.GetValueOrDefault("engagement"),
                    Frustration: scores.GetValueOrDefault("frustration"),
                    TrustInAgent: scores.GetValueOrDefault("trust_in_agent"),
                    ChurnRisk: scores.GetValueOrDefault("churn_risk"),
                    UpsellOpportunity: scores.GetValueOrDefault("upsell_opportunity"),
                    ResolutionProgress: scores.GetValueOrDefault("resolution_progress"),
                    Politeness: scores.GetValueOrDefault("politeness"),
                    CallEffectiveness: scores.GetValueOrDefault("call_effectiveness"),
                    OverallSentiment: scores.GetValueOrDefault("overall_sentiment"),
                    Timestamp: DateTime.UtcNow
                );

                LatestAnalysis = result;
                _analysisWriter.TryWrite(result);

                _telemetryClient?.TrackEvent("ConversationAnalysis", new Dictionary<string, string>
                {
                    { "analysis.lines_analyzed", _conversationHistory.Count.ToString() },
                    { "analysis.overall_sentiment", result.OverallSentiment.ToString() },
                    { "analysis.purchase_intent", result.PurchaseIntent.ToString() },
                    { "analysis.frustration", result.Frustration.ToString() }
                });

                _logger.LogInformation("Analysis complete: sentiment={Sentiment}, mood={Mood}, frustration={Frustration}",
                    result.OverallSentiment, result.CustomerMood, result.Frustration);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Analysis] ERROR in RunAnalysisAsync: {ex}");
            _logger.LogError(ex, "Error running conversation analysis");
            _telemetryClient?.TrackException(ex, new Dictionary<string, string>
            {
                { "Component", "ConversationAnalysisService.RunAnalysisAsync" }
            });
        }
        finally
        {
            _analysisLock.Release();
        }
    }

    /// <summary>Most recent per-turn sentiment scores, carried into the case summary at teardown.</summary>
    public AnalysisResult? LatestAnalysis { get; private set; }

    /// <summary>
    /// Produce the one-shot structured case summary for the whole interaction.
    /// Call this once when an interaction ends. Isolated from the sentiment loop and
    /// from the hang-up/farewell timing chain — safe to await off the critical path.
    /// Returns null if there was no meaningful conversation or the model call failed.
    /// </summary>
    public async Task<CaseSummary?> GenerateCaseSummaryAsync(string? callBrief = null)
    {
        if (_conversationHistory.Count == 0) return null;

        try
        {
            var transcript = string.Join("\n", _conversationHistory.Select(l => $"{l.Speaker}: {l.Text}"));

            var options = new ChatCompletionOptions
            {
                ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                    jsonSchemaFormatName: "case_summary",
                    jsonSchema: s_caseSummarySchema,
                    jsonSchemaIsStrict: true)
            };

            var brief = string.IsNullOrWhiteSpace(callBrief)
                ? ""
                : $"Instructions the agent was given for this call (the task list, priorities and security check):\n{callBrief}\n\n";

            var messages = new ChatMessage[]
            {
                new SystemChatMessage(CaseSummarySystemPrompt),
                new UserChatMessage($"{brief}Summarize this completed interaction:\n\n{transcript}")
            };

            var completion = await _chatClient.CompleteChatAsync(messages, options);
            var content = completion.Value.Content[0].Text;

            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var summary = new CaseSummary(
                Summary: root.GetProperty("summary").GetString() ?? "",
                Outcome: root.GetProperty("outcome").GetString() ?? "other",
                Topics: root.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToArray()
                    : Array.Empty<string>(),
                Verified: root.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True,
                FollowUpNeeded: root.TryGetProperty("followUpNeeded", out var f) && f.ValueKind == JsonValueKind.True,
                FollowUpDraft: root.TryGetProperty("followUpDraft", out var d) ? d.GetString() ?? "" : "",
                Timestamp: DateTime.UtcNow,
                Analysis: LatestAnalysis,
                Actions: root.TryGetProperty("actions", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(e => new CaseAction(
                        Type: e.TryGetProperty("type", out var at) ? at.GetString() ?? "other" : "other",
                        Target: e.TryGetProperty("target", out var ag) ? ag.GetString() ?? "" : "",
                        Content: e.TryGetProperty("content", out var ac) ? ac.GetString() ?? "" : "",
                        Status: e.TryGetProperty("status", out var st) ? st.GetString() ?? "ready" : "ready",
                        Priority: e.TryGetProperty("priority", out var pr) ? pr.GetString() ?? "today" : "today")).ToArray()
                    : Array.Empty<CaseAction>()
            );

            _telemetryClient?.TrackEvent("CaseSummary", new Dictionary<string, string>
            {
                { "case.outcome", summary.Outcome },
                { "case.topics", string.Join(",", summary.Topics) },
                { "case.verified", summary.Verified.ToString() },
                { "case.follow_up_needed", summary.FollowUpNeeded.ToString() }
            });
            _logger.LogInformation("Case summary: outcome={Outcome}, verified={Verified}, followUp={FollowUp}",
                summary.Outcome, summary.Verified, summary.FollowUpNeeded);

            return summary;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating case summary");
            _telemetryClient?.TrackException(ex, new Dictionary<string, string>
            {
                { "Component", "ConversationAnalysisService.GenerateCaseSummaryAsync" }
            });
            return null;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _analysisLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>15-category analysis result, scored 0-5 each.</summary>
public record AnalysisResult(
    int PurchaseIntent,
    int CustomerMood,
    int Cooperativeness,
    int BrandPerception,
    int CompanySatisfaction,
    int Urgency,
    int Engagement,
    int Frustration,
    int TrustInAgent,
    int ChurnRisk,
    int UpsellOpportunity,
    int ResolutionProgress,
    int Politeness,
    int CallEffectiveness,
    int OverallSentiment,
    DateTime Timestamp
);

/// <summary>One-shot structured case summary for the cross-channel timeline.</summary>
public record CaseSummary(
    string Summary,
    string Outcome,
    string[] Topics,
    bool Verified,
    bool FollowUpNeeded,
    string FollowUpDraft,
    DateTime Timestamp,
    AnalysisResult? Analysis = null,
    CaseAction[]? Actions = null
);

/// <summary>A task spoken on the call that an automation (e.g. Cowork) should carry out.</summary>
public record CaseAction(string Type, string Target, string Content, string Status, string Priority = "today");

internal record TranscriptLine(string Speaker, string Text);

