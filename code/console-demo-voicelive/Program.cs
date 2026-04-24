using System.CommandLine;
using System.Threading.Channels;
using Azure.AI.VoiceLive;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using NAudio.Wave;

namespace ConsoleVoiceLiveDemo;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await CreateRootCommand().InvokeAsync(args).ConfigureAwait(false);
    }

    private static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Voice Live Demo");
        var endpointOpt = new Option<string?>("--endpoint");
        var modelOpt = new Option<string>("--model", () => "gpt-5");
        var voiceOpt = new Option<string>("--voice", () => "da-DK-ChristelNeural");
        var instructionsOpt = new Option<string>("--instructions",
            () => "Du er en venlig dansk assistent. Du taler kun dansk. Svar kort og naturligt som i en telefonsamtale.");
        root.AddOption(endpointOpt);
        root.AddOption(modelOpt);
        root.AddOption(voiceOpt);
        root.AddOption(instructionsOpt);
        root.SetHandler(async (string? ep, string m, string v, string ins) =>
        {
            await RunAsync(ep, m, v, ins).ConfigureAwait(false);
        }, endpointOpt, modelOpt, voiceOpt, instructionsOpt);
        return root;
    }

    static async Task RunAsync(string? cliEndpoint, string model, string voice, string instructions)
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddUserSecrets(System.Reflection.Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

        var endpoint = cliEndpoint
            ?? config["AzureOpenAI:Endpoint"]
            ?? Environment.GetEnvironmentVariable("AZURE_VOICELIVE_ENDPOINT")
            ?? "https://cog-contactcenteragent.cognitiveservices.azure.com/";

        model = config["AzureOpenAI:Model"] ?? model;
        voice = config["AzureOpenAI:Voice"] ?? voice;

        if (!CheckAudio()) return;

        try
        {
            Console.WriteLine($"[*] Authenticating...");
            var cred = new DefaultAzureCredential();
            var client = new VoiceLiveClient(new Uri(endpoint), cred, new VoiceLiveClientOptions());
            Console.WriteLine($"[*] Endpoint: {endpoint}");
            Console.WriteLine($"[*] Connecting — model={model}, voice={voice}");

            using var session = await client.StartSessionAsync(model).ConfigureAwait(false);
            Console.WriteLine("[*] WebSocket connected");

            // Configure session
            var opts = new VoiceLiveSessionOptions
            {
                InputAudioTranscription = new AudioInputTranscriptionOptions(AudioInputTranscriptionOptionsModel.Whisper1)
                {
                    Language = "da"
                },
                Model = model,
                Instructions = instructions,
                Voice = new AzureStandardVoice(voice),
                InputAudioFormat = InputAudioFormat.Pcm16,
                OutputAudioFormat = OutputAudioFormat.Pcm16,
                TurnDetection = new ServerVadTurnDetection
                {
                    Threshold = 0.3f,
                    PrefixPadding = TimeSpan.FromMilliseconds(300),
                    SilenceDuration = TimeSpan.FromMilliseconds(500)
                }
            };
            opts.Modalities.Clear();
            opts.Modalities.Add(InteractionModality.Text);
            opts.Modalities.Add(InteractionModality.Audio);

            await session.ConfigureSessionAsync(opts).ConfigureAwait(false);
            Console.WriteLine("[*] Session configured");

            // Audio setup
            var sendCh = Channel.CreateUnbounded<byte[]>();
            var playCh = Channel.CreateUnbounded<byte[]>();
            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var ct = cts.Token;

            // Mic capture
            var waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(24000, 16, 1),
                BufferMilliseconds = 50,
                DeviceNumber = 0
            };
            waveIn.DataAvailable += (_, e) =>
            {
                if (e.BytesRecorded > 0)
                {
                    var buf = new byte[e.BytesRecorded];
                    Array.Copy(e.Buffer, 0, buf, 0, e.BytesRecorded);
                    sendCh.Writer.TryWrite(buf);
                }
            };
            waveIn.StartRecording();
            Console.WriteLine("[*] Mic started");

            // Playback
            var waveOut = new WaveOutEvent { DesiredLatency = 100 };
            var playBuf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(10),
                DiscardOnBufferOverflow = true
            };
            waveOut.Init(playBuf);
            waveOut.Play();
            Console.WriteLine("[*] Playback started");

            // Send task
            var sendTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var audio in sendCh.Reader.ReadAllAsync(ct))
                    {
                        await session.SendInputAudioAsync(audio, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"[!] Send error: {ex.Message}"); }
            }, ct);

            // Playback task
            var playTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var audio in playCh.Reader.ReadAllAsync(ct))
                    {
                        playBuf?.AddSamples(audio, 0, audio.Length);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.WriteLine($"[!] Play error: {ex.Message}"); }
            }, ct);

            Console.WriteLine();
            Console.WriteLine("============================================================");
            Console.WriteLine("  VOICE ASSISTANT READY");
            Console.WriteLine($"  Model: {model}  Voice: {voice}");
            Console.WriteLine("  Speak Danish — Press Ctrl+C to exit");
            Console.WriteLine("============================================================");
            Console.WriteLine();

            // Event loop — print EVERY event
            bool responseActive = false;
            try
            {
                await foreach (var evt in session.GetUpdatesAsync(ct))
                {
                    var evtName = evt.GetType().Name;
                    Console.WriteLine($"[EVT] {evtName}");

                    switch (evt)
                    {
                        case SessionUpdateSessionCreated c:
                            Console.WriteLine($"  Session: {c.Session?.Id}");
                            break;

                        case SessionUpdateSessionUpdated:
                            Console.WriteLine("  Session ready — talk!");
                            break;

                        case SessionUpdateInputAudioBufferSpeechStarted:
                            Console.WriteLine("  🗣️ Speech started");
                            // Stop playback for barge-in
                            playBuf?.ClearBuffer();
                            if (responseActive)
                            {
                                try { await session.CancelResponseAsync(ct); Console.WriteLine("  Cancelled response"); }
                                catch (Exception ex) { Console.WriteLine($"  Cancel: {ex.Message}"); }
                                try { await session.ClearStreamingAudioAsync(ct); }
                                catch { }
                            }
                            break;

                        case SessionUpdateInputAudioBufferSpeechStopped:
                            Console.WriteLine("  🤔 Speech stopped — processing...");
                            break;

                        case SessionUpdateInputAudioBufferCommitted:
                            Console.WriteLine("  Audio committed");
                            break;

                        case SessionUpdateConversationItemInputAudioTranscriptionCompleted t:
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine($"  👤 Du: {t.Transcript}");
                            Console.ResetColor();
                            break;

                        case SessionUpdateConversationItemCreated:
                            Console.WriteLine("  ConversationItem created");
                            break;

                        case SessionUpdateResponseCreated:
                            Console.WriteLine("  🤖 Response created");
                            responseActive = true;
                            break;

                        case SessionUpdateResponseOutputItemAdded:
                            Console.WriteLine("  OutputItem added");
                            break;

                        case SessionUpdateResponseContentPartAdded:
                            Console.WriteLine("  ContentPart added");
                            break;

                        case SessionUpdateResponseAudioTranscriptDelta td:
                            Console.Write(td.Delta);
                            break;

                        case SessionUpdateResponseAudioDelta ad:
                            if (ad.Delta != null)
                            {
                                var bytes = ad.Delta.ToArray();
                                Console.WriteLine($"  Audio: {bytes.Length}B");
                                await playCh.Writer.WriteAsync(bytes, ct);
                            }
                            break;

                        case SessionUpdateResponseAudioTranscriptDone td:
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.WriteLine($"\n  🤖 AI: {td.Transcript}");
                            Console.ResetColor();
                            break;

                        case SessionUpdateResponseAudioDone:
                            Console.WriteLine("  Audio done — ready for input");
                            break;

                        case SessionUpdateResponseDone:
                            Console.WriteLine("  Response complete");
                            responseActive = false;
                            break;

                        case SessionUpdateError err:
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"  ❌ Error: {err.Error?.Message}");
                            Console.ResetColor();
                            responseActive = false;
                            break;
                    }
                }
            }
            catch (OperationCanceledException) { }

            // Cleanup
            Console.WriteLine("\n👋 Farvel!");
            waveIn.StopRecording();
            waveIn.Dispose();
            waveOut.Stop();
            waveOut.Dispose();
            sendCh.Writer.TryComplete();
            playCh.Writer.TryComplete();
            cts.Cancel();
            try { await Task.WhenAll(sendTask, playTask); } catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Fatal: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    static bool CheckAudio()
    {
        try
        {
            using var wi = new WaveInEvent { WaveFormat = new WaveFormat(24000, 16, 1), BufferMilliseconds = 50 };
            wi.DataAvailable += (_, _) => { };
            wi.StartRecording();
            wi.StopRecording();

            var buf = new BufferedWaveProvider(new WaveFormat(24000, 16, 1)) { BufferDuration = TimeSpan.FromMilliseconds(200) };
            using var wo = new WaveOutEvent { DesiredLatency = 100 };
            wo.Init(buf);
            wo.Play();
            wo.Stop();

            Console.WriteLine("[*] Audio OK");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Audio check failed: {ex.Message}");
            return false;
        }
    }
}
