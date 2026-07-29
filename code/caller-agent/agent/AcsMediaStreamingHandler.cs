using System.Net.WebSockets;
using Azure.Communication.CallAutomation;
using System.Text;
using System.Threading.Channels;
using CallAutomation.AzureAI.VoiceLive;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;

public class AcsMediaStreamingHandler
{
    private WebSocket m_webSocket;
    private CancellationTokenSource m_cts;
    private AzureVoiceLiveService? m_aiServiceHandler;
    private IConfiguration m_configuration;
    private readonly ILogger<AcsMediaStreamingHandler> m_logger;
    private readonly ILoggerFactory m_loggerFactory;
    private readonly string? m_callSystemPrompt;
    private readonly string? m_callLanguage;
    private readonly string? m_callLanguageCode;
    private readonly string? m_callTranscriptionHint;
    private readonly string? m_callVoice;
    private readonly string? m_callVoiceStyle;
    private readonly Azure.Core.TokenCredential m_aiCredential;
    private readonly TelemetryClient? m_telemetryClient;
    private readonly string? m_phoneNumber;
    private readonly ChannelWriter<TranscriptionEvent>? m_transcriptionWriter;
    private readonly ChannelWriter<AnalysisResult>? m_analysisWriter;
    private readonly ChannelWriter<CaseSummary>? m_caseSummaryWriter;
    private Func<string, Task>? m_onHangUp;

    /// <summary>
    /// Register a callback invoked when the AI decides to hang up the call.
    /// This is forwarded to AzureVoiceLiveService.
    /// </summary>
    public void OnHangUp(Func<string, Task> callback) => m_onHangUp = callback;

    public AcsMediaStreamingHandler(
        WebSocket webSocket,
        IConfiguration configuration,
        ILogger<AcsMediaStreamingHandler> logger,
        ILoggerFactory loggerFactory,
        Azure.Core.TokenCredential aiCredential,
        string? callSystemPrompt = null,
        string? callLanguage = null,
        string? callLanguageCode = null,
        string? callTranscriptionHint = null,
        TelemetryClient? telemetryClient = null,
        string? phoneNumber = null,
        ChannelWriter<TranscriptionEvent>? transcriptionWriter = null,
        ChannelWriter<AnalysisResult>? analysisWriter = null,
        ChannelWriter<CaseSummary>? caseSummaryWriter = null,
        string? callVoice = null,
        string? callVoiceStyle = null)
    {
        m_webSocket = webSocket;
        m_configuration = configuration;
        m_cts = new CancellationTokenSource();
        m_logger = logger;
        m_loggerFactory = loggerFactory;
        m_aiCredential = aiCredential;
        m_telemetryClient = telemetryClient;
        m_phoneNumber = phoneNumber;
        m_callSystemPrompt = callSystemPrompt;
        m_callLanguage = callLanguage;
        m_callLanguageCode = callLanguageCode;
        m_callTranscriptionHint = callTranscriptionHint;
        m_transcriptionWriter = transcriptionWriter;
        m_analysisWriter = analysisWriter;
        m_caseSummaryWriter = caseSummaryWriter;
        m_callVoice = callVoice;
        m_callVoiceStyle = callVoiceStyle;

        m_logger.LogInformation("AcsMediaStreamingHandler initialized (custom prompt: {HasPrompt}, language: {Language})", callSystemPrompt != null, callLanguage ?? "default");
    }

    public async Task ProcessWebSocketAsync()
    {
        if (m_webSocket == null)
        {
            m_logger.LogError("WebSocket is null in ProcessWebSocketAsync");
            return;
        }

        m_telemetryClient?.TrackEvent("AcsWebSocketConnected", new Dictionary<string, string>
        {
            { "acs.ws_state", m_webSocket.State.ToString() },
            { "chat.phone_number", m_phoneNumber ?? "" }
        });

        m_logger.LogInformation("Initializing Azure Voice Live Service");
        var voiceLiveLogger = m_loggerFactory.CreateLogger<AzureVoiceLiveService>();
        m_aiServiceHandler = new AzureVoiceLiveService(this, m_configuration, voiceLiveLogger, m_aiCredential, m_callSystemPrompt, m_callLanguage, m_callLanguageCode, m_callTranscriptionHint, m_telemetryClient, m_phoneNumber, m_transcriptionWriter, m_analysisWriter, m_caseSummaryWriter, m_callVoice, m_callVoiceStyle);

        // Initialize AI session asynchronously (avoids sync-over-async blocking in constructor)
        await m_aiServiceHandler.InitializeAsync(m_configuration);

        // Forward hang-up callback to the AI service
        if (m_onHangUp != null)
        {
            m_aiServiceHandler.OnHangUp(m_onHangUp);
        }

        try
        {
            await StartReceivingFromAcsMediaWebSocket();
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "Exception in ProcessWebSocketAsync");
            m_telemetryClient?.TrackException(ex, new Dictionary<string, string>
            {
                { "Component", "AcsMediaStreamingHandler.ProcessWebSocketAsync" }
            });
        }
        finally
        {
            m_logger.LogInformation("ACS WebSocket processing ended. WS state: {State}", m_webSocket?.State.ToString() ?? "null");
            m_telemetryClient?.TrackEvent("AcsWebSocketDisconnected", new Dictionary<string, string>
            {
                { "acs.ws_state", m_webSocket?.State.ToString() ?? "null" },
                { "chat.phone_number", m_phoneNumber ?? "" }
            });
            await m_aiServiceHandler.Close();
            this.Close();
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (m_webSocket?.State == WebSocketState.Open)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(message);
            await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
    }

    public void Close()
    {
        m_cts.Cancel();
        m_cts.Dispose();
    }

    private async Task WriteToAzureOpenAIRealtimeStream(string data)
    {
        if (m_aiServiceHandler == null)
        {
            m_logger.LogWarning("AI service handler is null");
            return;
        }

        var input = StreamingData.Parse(data);
        if (input is AudioData audioData)
        {
            if (!audioData.IsSilent)
            {
                await m_aiServiceHandler.SendAudioToExternalAI(audioData.Data.ToArray());
            }
        }
    }

    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (m_webSocket == null)
        {
            return;
        }
        try
        {
            while (m_webSocket.State == WebSocketState.Open)
            {
                byte[] receiveBuffer = new byte[2048];
                WebSocketReceiveResult receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), m_cts.Token);

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    m_logger.LogWarning("ACS WebSocket received Close frame. CloseStatus: {Status}, Description: {Desc}",
                        receiveResult.CloseStatus, receiveResult.CloseStatusDescription);
                    m_telemetryClient?.TrackEvent("AcsWebSocketCloseReceived", new Dictionary<string, string>
                    {
                        { "acs.close_status", receiveResult.CloseStatus?.ToString() ?? "unknown" },
                        { "acs.close_description", receiveResult.CloseStatusDescription ?? "" },
                        { "chat.phone_number", m_phoneNumber ?? "" }
                    });
                    break;
                }

                string data = Encoding.UTF8.GetString(receiveBuffer, 0, receiveResult.Count);
                await WriteToAzureOpenAIRealtimeStream(data);
            }

            // Log why the loop exited
            if (m_webSocket.State != WebSocketState.Open)
            {
                m_logger.LogWarning("ACS WebSocket loop exited. State: {State}, CloseStatus: {CloseStatus}",
                    m_webSocket.State, m_webSocket.CloseStatus);
                m_telemetryClient?.TrackEvent("AcsWebSocketLoopExited", new Dictionary<string, string>
                {
                    { "acs.ws_state", m_webSocket.State.ToString() },
                    { "acs.close_status", m_webSocket.CloseStatus?.ToString() ?? "" },
                    { "acs.close_description", m_webSocket.CloseStatusDescription ?? "" },
                    { "chat.phone_number", m_phoneNumber ?? "" }
                });
            }
        }
        catch (OperationCanceledException)
        {
            m_logger.LogInformation("ACS WebSocket receive cancelled");
            m_telemetryClient?.TrackEvent("AcsWebSocketCancelled", new Dictionary<string, string>
            {
                { "acs.ws_state", m_webSocket?.State.ToString() ?? "null" },
                { "chat.phone_number", m_phoneNumber ?? "" }
            });
        }
        catch (Exception ex)
        {
            m_logger.LogError(ex, "Error receiving from ACS media WebSocket");
            m_telemetryClient?.TrackException(ex, new Dictionary<string, string>
            {
                { "Component", "AcsMediaStreamingHandler.StartReceivingFromAcsMediaWebSocket" },
                { "acs.ws_state", m_webSocket?.State.ToString() ?? "null" },
                { "chat.phone_number", m_phoneNumber ?? "" }
            });
        }
    }
}
