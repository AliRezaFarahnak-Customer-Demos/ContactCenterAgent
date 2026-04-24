using System.Text.Json;
using System.Threading.Channels;
using Azure.AI.OpenAI;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

namespace CallAutomation.AzureAI.VoiceLive;

/// <summary>
/// Real-time conversation analysis using GPT-5.4-nano structured outputs.
/// Listens to transcript events and produces 15-category sentiment scores (0-5)
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

    // JSON schema for structured output — 15 categories, each 0-5
    private static readonly BinaryData s_analysisSchema = BinaryData.FromBytes("""
        {
            "type": "object",
            "properties": {
                "purchase_intent": { "type": "integer", "description": "Likelihood the caller will buy a product (0=none, 5=very likely)" },
                "customer_mood": { "type": "integer", "description": "Overall emotional state (0=very angry, 5=delighted)" },
                "cooperativeness": { "type": "integer", "description": "Willingness to engage and work with the agent (0=hostile, 5=very cooperative)" },
                "brand_perception": { "type": "integer", "description": "How the caller feels about the brand or product (0=very negative, 5=very positive)" },
                "company_satisfaction": { "type": "integer", "description": "Satisfaction with the company overall (0=very unhappy, 5=very happy)" },
                "urgency": { "type": "integer", "description": "How urgent the caller's need is (0=no urgency, 5=extremely urgent)" },
                "engagement": { "type": "integer", "description": "How actively the caller is participating (0=disengaged, 5=highly engaged)" },
                "frustration": { "type": "integer", "description": "Signs of frustration or irritation (0=none, 5=extremely frustrated)" },
                "trust_in_agent": { "type": "integer", "description": "Confidence the caller has in the agent (0=no trust, 5=full trust)" },
                "churn_risk": { "type": "integer", "description": "Likelihood the caller will leave or cancel (0=no risk, 5=very high risk)" },
                "upsell_opportunity": { "type": "integer", "description": "Potential for additional sales or upgrades (0=none, 5=strong opportunity)" },
                "resolution_progress": { "type": "integer", "description": "How close the conversation is to resolving the issue (0=not started, 5=fully resolved)" },
                "politeness": { "type": "integer", "description": "Caller's tone and manners (0=very rude, 5=very polite)" },
                "call_effectiveness": { "type": "integer", "description": "How productive the conversation is (0=unproductive, 5=very productive)" },
                "overall_sentiment": { "type": "integer", "description": "Net positive or negative feeling (0=very negative, 5=very positive)" }
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

    private const string AnalysisSystemPrompt =
        """
        You are a real-time call center analytics engine. You analyze phone call transcripts and score the conversation across 15 categories on a 0-5 scale.

        Rules:
        - Score each category based ONLY on what has been said so far in the conversation.
        - If the conversation has not touched on a topic (e.g., no product mentioned), score that category as 0.
        - Be precise and conservative — don't inflate scores without evidence.
        - Update scores as the conversation evolves; early greetings should show neutral scores.
        - Frustration and churn_risk are INVERSE to positive sentiment — high frustration = low mood.
        - Consider tone, word choice, and context when scoring.
        - All scores must be integers from 0 to 5 inclusive.
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

        var deploymentName = configuration.GetValue<string>("AzureOpenAI:AnalysisDeploymentName") ?? "gpt-5.4-nano";

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

internal record TranscriptLine(string Speaker, string Text);

