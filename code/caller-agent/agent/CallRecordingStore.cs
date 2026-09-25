using System.Collections.Concurrent;
using System.Threading.Channels;
using CallAutomation.AzureAI.VoiceLive;

/// <summary>
/// In-memory, pollable copy of each outbound call's transcript and case summary.
/// The live SSE channel is consumed once and disappears when the call ends; this store
/// lets automation (e.g. Cowork browser use on /cowork) re-read a call at any time.
/// Recordings are kept for 24 hours and lost on restart.
/// </summary>
public sealed class CallRecordingStore
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    // The case summary is produced at teardown; after this long without one, treat the call as done anyway.
    private static readonly TimeSpan SummaryGrace = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, CallRecording> _recordings = new();

    public void Start(string contextId, string phoneNumber, string? name, string? purpose)
    {
        Prune();
        _recordings[contextId] = new CallRecording(contextId, phoneNumber, name, purpose, DateTimeOffset.UtcNow);
    }

    public ChannelWriter<TranscriptionEvent> Tee(string contextId, ChannelWriter<TranscriptionEvent> inner) =>
        _recordings.TryGetValue(contextId, out var rec) ? new RecordingWriter(inner, rec) : inner;

    public void MarkEnded(string contextId)
    {
        if (_recordings.TryGetValue(contextId, out var rec)) rec.EndedUtc ??= DateTimeOffset.UtcNow;
    }

    public void SetSummary(string contextId, CaseSummary summary)
    {
        if (_recordings.TryGetValue(contextId, out var rec)) rec.Summary = summary;
    }

    public CallRecordingSnapshot? Get(string contextId) =>
        _recordings.TryGetValue(contextId, out var rec) ? rec.Snapshot(SummaryGrace) : null;

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        foreach (var (id, rec) in _recordings)
            if (rec.StartedUtc < cutoff) _recordings.TryRemove(id, out _);
    }

    private sealed class RecordingWriter(ChannelWriter<TranscriptionEvent> inner, CallRecording rec)
        : ChannelWriter<TranscriptionEvent>
    {
        public override bool TryWrite(TranscriptionEvent item)
        {
            rec.Add(item);
            return inner.TryWrite(item);
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            inner.WaitToWriteAsync(cancellationToken);

        public override bool TryComplete(Exception? error = null) => inner.TryComplete(error);
    }
}

public sealed class CallRecording(string contextId, string phoneNumber, string? name, string? purpose, DateTimeOffset startedUtc)
{
    private readonly List<TranscriptionEvent> _entries = new();

    public DateTimeOffset StartedUtc { get; } = startedUtc;
    public DateTimeOffset? EndedUtc { get; set; }
    public CaseSummary? Summary { get; set; }

    public void Add(TranscriptionEvent evt)
    {
        lock (_entries) _entries.Add(evt);
    }

    public CallRecordingSnapshot Snapshot(TimeSpan summaryGrace)
    {
        TranscriptionEvent[] entries;
        lock (_entries) entries = _entries.ToArray();

        var summary = Summary;
        var ended = EndedUtc;
        var status = summary is not null ? "completed"
            : ended is not null ? "ended"
            : entries.Length > 0 ? "in_progress"
            : "ringing";
        var done = summary is not null || (ended is not null && DateTimeOffset.UtcNow - ended > summaryGrace);

        return new CallRecordingSnapshot(
            contextId, phoneNumber, name, purpose, status, done, StartedUtc, ended,
            entries,
            string.Join("\n", entries.Select(e => $"{e.Speaker}: {e.Text}")),
            summary?.Summary, summary?.Outcome, summary?.Topics,             summary?.FollowUpNeeded, summary?.FollowUpDraft, summary?.Actions);
    }
}

public record CallRecordingSnapshot(
    string ContextId,
    string PhoneNumber,
    string? Name,
    string? Purpose,
    string Status,                  // ringing | in_progress | ended | completed
    bool Done,                      // true once the summary is in (or the grace period after hang-up has passed)
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    IReadOnlyList<TranscriptionEvent> Entries,
    string TranscriptText,
    string? Summary,
    string? Outcome,
    string[]? Topics,
    bool? FollowUpNeeded,
    string? FollowUpDraft,
    CaseAction[]? Actions);
