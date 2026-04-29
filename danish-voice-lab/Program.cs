using System.Threading.Channels;
using Azure.AI.VoiceLive;
using Azure.Identity;
using ContactCenterAgent.Shared.Personas;
using ContactCenterAgent.Shared.VoiceLive;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;

namespace DanishVoiceLab;

// ─────────────────────────────────────────────────────────────────────────────
//  Danish Voice Lab — speak Danish to the AI, hear it speak Danish back.
//
//  Mirrors the official Microsoft Voice Live C# quickstart pattern
//  (BasicVoiceAssistant) closely so behaviour matches the documented happy path.
//  Key fixes vs. earlier iterations of this file (which made the AI cut itself
//  off mid-sentence on a laptop):
//
//    1. On speech_started we STOP + DISPOSE the WaveOutEvent (not just
//       ClearBuffer). NAudio's WaveOutEvent has ~100ms of audio already queued
//       in the OS device buffer; ClearBuffer alone leaves that 100ms playing,
//       the mic picks it up → false barge-in → infinite cut-off loop.
//    2. CancelResponse / ClearStreamingAudio are only called when a response
//       is actually active. Calling them at any other time produces server
//       errors that surface as "flicker".
//    3. server_echo_cancellation + azure_deep_noise_suppression are enabled
//       (same config the production phone agent uses).
//
//  Tweak the LabSettings block below, save, `dotnet run` again. Use HEADPHONES
//  for the cleanest experience.
// ─────────────────────────────────────────────────────────────────────────────

public static class Program
{
    private const int SampleRate = 24000;
    private static readonly WaveFormat AudioFormat = new(SampleRate, 16, 1);

    public static async Task<int> Main()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        // ── LAB SETTINGS — tweak these freely ────────────────────────────────
        // Prompts live in the SHARED `personas.json` at the repo root (also
        // consumed by code/admin-chat). Pick which persona to test by id, or
        // override entirely with AzureVoiceLive:Instructions in appsettings.
        var personaId = config["AzureVoiceLive:PersonaId"] ?? "onboarding";
        var instructionsOverride = config["AzureVoiceLive:Instructions"];
        var sharedPrompt = instructionsOverride ?? PersonaStore.GetPrompt(personaId);

        // Voice settings inherited from ContactCenterAgent.Shared.VoiceLive.VoiceLiveDefaults
        // (single source of truth, also used by caller-agent). Override per-key in
        // appsettings.json only when you want the lab to diverge from prod.
        var voiceType = config["AzureVoiceLive:VoiceType"] ?? VoiceLiveDefaults.DefaultVoiceType;
        var voiceName = config["AzureVoiceLive:Voice"] ?? VoiceLiveDefaults.DefaultVoiceName;

        // Temperature: optional in JSON. Auto-default from shared ONLY if voice is HD
        // (standard neural silently ignores it but the docs warn against unsupported fields).
        float? temperature = null;
        var tempStr = config["AzureVoiceLive:Temperature"];
        if (!string.IsNullOrWhiteSpace(tempStr) && float.TryParse(tempStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
            temperature = t;
        else if (VoiceLiveDefaults.IsHdVoice(voiceName))
            temperature = (float)VoiceLiveDefaults.DefaultVoiceTemperature;

        var settings = new LabSettings
        {
            Endpoint = config["AzureVoiceLive:Endpoint"] ?? "https://cog-contactcenteragent.cognitiveservices.azure.com/",
            Model = config["AzureVoiceLive:Model"] ?? VoiceLiveDefaults.Model,
            VoiceType = voiceType,
            Voice = voiceName,
            Temperature = temperature,
            Instructions = sharedPrompt,
            MicDevice = 0,
        };
        // ─────────────────────────────────────────────────────────────────────

        PrintBanner(settings);

        ListAudioDevices();

        var micDeviceStr = config["AzureVoiceLive:MicDevice"];
        int micDevice = 0;
        if (!string.IsNullOrWhiteSpace(micDeviceStr) && int.TryParse(micDeviceStr, out var mi)) micDevice = mi;
        settings.MicDevice = micDevice;
        Console.WriteLine($"  Using mic device #{micDevice}: {WaveInEvent.GetCapabilities(micDevice).ProductName}");
        Console.WriteLine();

        if (!AudioSelfTest()) return 1;

        try
        {
            await RunAsync(settings);
            return 0;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n❌ Fatal: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine(ex);
            return 1;
        }
    }

    private static async Task RunAsync(LabSettings s)
    {
        Console.WriteLine("[*] Authenticating with DefaultAzureCredential (uses `az login`)…");
        var cred = new DefaultAzureCredential();
        var client = new VoiceLiveClient(new Uri(s.Endpoint), cred, new VoiceLiveClientOptions());

        Console.WriteLine($"[*] Connecting to model '{s.Model}'…");
        using var session = await client.StartSessionAsync(s.Model);
        Console.WriteLine("[*] WebSocket connected");

        // Per the official quickstart: ServerVadTurnDetection with the documented
        // defaults works well for laptop mic + headphones. azure_semantic_vad_*
        // variants are tuned for telephony / noisy environments.
        var opts = new VoiceLiveSessionOptions
        {
            Model = s.Model,
            Instructions = s.Instructions,
            Voice = s.VoiceType switch
            {
                "azure-standard" => BuildAzureStandardVoice(s.Voice, s.Temperature),
                _ => new OpenAIVoice(new OAIVoice(s.Voice)),
            },
            InputAudioFormat = InputAudioFormat.Pcm16,
            OutputAudioFormat = OutputAudioFormat.Pcm16,
            // Server-side echo cancellation removes the AI's own voice from the
            // input stream when it leaks through the laptop speaker.
            InputAudioEchoCancellation = new AudioEchoCancellation(),
            InputAudioNoiseReduction = new AudioNoiseReduction(new AudioNoiseReductionType("azure_deep_noise_suppression")),
            // Azure Speech (da-DK) — Microsoft's flagship Danish ASR.
            // PhraseList biases recognition toward Norlys-specific vocabulary
            // (billing, onboarding, energy domain). Add real customer/product
            // names here as you discover them in the transcripts.
            InputAudioTranscription = BuildTranscriptionOptions(),
            TurnDetection = new AzureSemanticVadTurnDetection
            {
                Threshold = (float)VoiceLiveDefaults.VadThreshold,
                PrefixPadding = TimeSpan.FromMilliseconds(VoiceLiveDefaults.VadPrefixPaddingMs),
                SilenceDuration = TimeSpan.FromMilliseconds(VoiceLiveDefaults.VadSilenceDurationMs),
                // TEST: prove interrupt_response=false is accepted by the server.
                // If the lab still works with this set, the field is fine and the
                // prod issue is elsewhere (deploy, env, ACS callback wiring).
                InterruptResponse = false
            }
        };
        opts.Modalities.Clear();
        opts.Modalities.Add(InteractionModality.Text);
        opts.Modalities.Add(InteractionModality.Audio);

        await session.ConfigureSessionAsync(opts);
        Console.WriteLine("[*] Session configured");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var ct = cts.Token;

        var audio = new AudioIO(s.MicDevice);
        audio.StartCapture();
        audio.StartPlayback();

        // Background pump: serialize SendInputAudioAsync calls. Fire-and-forget
        // from the NAudio callback (20 calls/sec) races and reorders packets,
        // which the official ModelQuickstart sample explicitly avoids.
        var sendTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var buf in audio.OutgoingAudio.ReadAllAsync(ct))
                {
                    try { await session.SendInputAudioAsync(buf, ct); }
                    catch (OperationCanceledException) { break; }
                    catch { /* keep pumping */ }
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        Console.WriteLine();
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine("  🎙️  Snak dansk — tryk Ctrl+C for at afslutte");
        Console.WriteLine("  💡 Brug høretelefoner for bedst resultat");
        Console.WriteLine("════════════════════════════════════════════════════════════");
        Console.WriteLine();

        // State flags that gate barge-in cancellation (mirrors official sample).
        bool responseActive = false;
        bool canCancelResponse = false;

        try
        {
            await foreach (var evt in session.GetUpdatesAsync(ct))
            {
                switch (evt)
                {
                    case SessionUpdateInputAudioBufferSpeechStarted:
                        // CRITICAL: stop + dispose WaveOut entirely so the OS device
                        // buffer doesn't keep playing the last ~100ms of AI audio.
                        audio.StopPlayback();

                        if (responseActive && canCancelResponse)
                        {
                            try { await session.CancelResponseAsync(ct); } catch { }
                            try { await session.ClearStreamingAudioAsync(ct); } catch { }
                        }
                        break;

                    case SessionUpdateInputAudioBufferSpeechStopped:
                        // Recreate playback for the AI's response.
                        audio.StartPlayback();
                        break;

                    case SessionUpdateConversationItemInputAudioTranscriptionCompleted t:
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"👤 Du:  {t.Transcript}");
                        Console.ResetColor();
                        break;

                    case SessionUpdateResponseCreated:
                        responseActive = true;
                        canCancelResponse = true;
                        break;

                    case SessionUpdateResponseAudioDelta ad when ad.Delta is not null:
                        audio.QueueAudio(ad.Delta.ToArray());
                        break;

                    case SessionUpdateResponseAudioTranscriptDone td:
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"🤖 AI:  {td.Transcript}");
                        Console.ResetColor();
                        break;

                    case SessionUpdateResponseDone:
                        responseActive = false;
                        canCancelResponse = false;
                        break;

                    case SessionUpdateError err:
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"❌ {err.Error?.Message}");
                        Console.ResetColor();
                        responseActive = false;
                        canCancelResponse = false;
                        // If the server is spamming errors (bad config), bail out
                        // instead of flooding the console.
                        cts.Cancel();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }

        Console.WriteLine("\n👋 Farvel!");
        audio.Dispose();
        try { await sendTask; } catch { }
    }

    private static void PrintBanner(LabSettings s)
    {
        Console.WriteLine("┌────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│  Danish Voice Lab — local speech-to-speech sandbox         │");
        Console.WriteLine("└────────────────────────────────────────────────────────────┘");
        Console.WriteLine($"  Endpoint    : {s.Endpoint}");
        Console.WriteLine($"  Model       : {s.Model}");
        var voiceFlavor = s.Voice.Contains(":DragonHDOmni", StringComparison.OrdinalIgnoreCase) ? "HD Omni"
                         : s.Voice.Contains(":DragonHD", StringComparison.OrdinalIgnoreCase) ? "HD"
                         : s.VoiceType == "openai" ? "OpenAI"
                         : "Azure standard neural";
        Console.WriteLine($"  Voice       : {s.Voice}  ({voiceFlavor})");
        if (s.Temperature.HasValue)
            Console.WriteLine($"  Temperature : {s.Temperature.Value} (HD/HD Omni only — ignored on standard neural)");
        Console.WriteLine();
    }

    private static void ListAudioDevices()
    {
        Console.WriteLine("  Available input devices:");
        for (int i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            var caps = WaveInEvent.GetCapabilities(i);
            Console.WriteLine($"    [{i}] {caps.ProductName}");
        }
        Console.WriteLine("  (set AzureVoiceLive:MicDevice in appsettings.json to choose)");
    }

    /// <summary>
    /// Build the AzureStandardVoice payload. The same SDK type carries both standard
    /// neural voices (e.g. da-DK-ChristelNeural) and HD Omni voices
    /// (e.g. da-DK-Christel:DragonHDOmniLatestNeural) — Voice Live keys off the
    /// ":DragonHDOmniLatestNeural" suffix in the name. Temperature only has effect
    /// on HD voices; on standard neural the server silently ignores it.
    ///
    /// Locale is ENFORCED via VoiceLiveDefaults.DefaultVoiceLocale (da-DK) so the
    /// voice can't drift toward a generic Scandinavian / Swedish accent on English
    /// loanwords. Without it, da-DK voices fall back to default-accent behaviour
    /// based on text content.
    /// </summary>
    private static AzureStandardVoice BuildAzureStandardVoice(string name, float? temperature)
    {
        var voice = new AzureStandardVoice(name)
        {
            Locale = VoiceLiveDefaults.DefaultVoiceLocale,
        };
        if (temperature.HasValue) voice.Temperature = temperature.Value;
        return voice;
    }

    /// <summary>
    /// Build the Voice Live transcription options using the shared Norlys phrase list.
    /// Production STT is azure-speech (da-DK) — same on PSTN.
    /// </summary>
    private static AudioInputTranscriptionOptions BuildTranscriptionOptions()
    {
        var opts = new AudioInputTranscriptionOptions(AudioInputTranscriptionOptionsModel.AzureSpeech)
        {
            Language = VoiceLiveDefaults.TranscriptionLanguage,
        };
        foreach (var phrase in NorlysDanishPhrases.All)
            opts.PhraseList.Add(phrase);
        return opts;
    }

    private static bool AudioSelfTest()
    {
        try
        {
            using var wi = new WaveInEvent { WaveFormat = AudioFormat, BufferMilliseconds = 50 };
            wi.DataAvailable += (_, _) => { };
            wi.StartRecording();
            wi.StopRecording();

            var buf = new BufferedWaveProvider(AudioFormat) { BufferDuration = TimeSpan.FromMilliseconds(200) };
            using var wo = new WaveOutEvent { DesiredLatency = 100 };
            wo.Init(buf);
            wo.Play();
            wo.Stop();
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"❌ Audio device check failed: {ex.Message}");
            Console.ResetColor();
            return false;
        }
    }

    /// <summary>
    /// Wraps NAudio capture + playback. Playback is intentionally torn down and
    /// recreated on every barge-in so the OS device buffer is fully flushed —
    /// otherwise the AI's residual audio keeps reaching the mic.
    /// </summary>
    private sealed class AudioIO : IDisposable
    {
        private readonly int _micDevice;
        public AudioIO(int micDevice) { _micDevice = micDevice; }
        private WaveInEvent? _waveIn;
        private WaveOutEvent? _waveOut;
        private BufferedWaveProvider? _playBuf;
        private readonly object _playLock = new();
        private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        public ChannelReader<byte[]> OutgoingAudio => _outgoing.Reader;
        public float LastPeakLevel { get; private set; }

        public void StartCapture()
        {
            _waveIn = new WaveInEvent
            {
                WaveFormat = AudioFormat,
                BufferMilliseconds = 50,
                DeviceNumber = _micDevice
            };
            _waveIn.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded <= 0) return;
                // Compute peak amplitude (16-bit PCM little-endian)
                short peak = 0;
                for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                {
                    short sample = (short)(e.Buffer[i] | (e.Buffer[i + 1] << 8));
                    short abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                    if (abs > peak) peak = abs;
                }
                LastPeakLevel = peak / 32768f;
                var buf = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, 0, buf, 0, e.BytesRecorded);
                _outgoing.Writer.TryWrite(buf);
            };
            _waveIn.StartRecording();
        }

        public void StartPlayback()
        {
            lock (_playLock)
            {
                if (_waveOut is not null) return;
                _playBuf = new BufferedWaveProvider(AudioFormat)
                {
                    BufferDuration = TimeSpan.FromSeconds(10),
                    DiscardOnBufferOverflow = true
                };
                _waveOut = new WaveOutEvent { DesiredLatency = 100 };
                _waveOut.Init(_playBuf);
                _waveOut.Play();
            }
        }

        public void StopPlayback()
        {
            lock (_playLock)
            {
                if (_waveOut is null) return;
                try { _waveOut.Stop(); } catch { }
                _waveOut.Dispose();
                _waveOut = null;
                _playBuf = null;
            }
        }

        public void QueueAudio(byte[] data)
        {
            lock (_playLock)
            {
                _playBuf?.AddSamples(data, 0, data.Length);
            }
        }

        public void Dispose()
        {
            try { _waveIn?.StopRecording(); } catch { }
            _waveIn?.Dispose();
            StopPlayback();
            _outgoing.Writer.TryComplete();
        }
    }
}

public sealed class LabSettings
{
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public required string VoiceType { get; init; }
    public required string Voice { get; init; }
    /// <summary>HD/HD Omni temperature (0.0–1.0). Null = omit from payload.</summary>
    public float? Temperature { get; init; }
    public required string Instructions { get; init; }
    public int MicDevice { get; set; }
}
