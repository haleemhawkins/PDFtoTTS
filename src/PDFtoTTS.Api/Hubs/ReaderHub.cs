using Microsoft.AspNetCore.SignalR;
using PDFtoTTS.Api.Storage;

namespace PDFtoTTS.Api.Hubs;

/// <summary>
/// Reader hub (design §5.2). Clients subscribe to a session and receive
/// <c>ChunkReady</c>, <c>Progress</c>, <c>SessionStatus</c> and <c>Error</c>.
/// On subscribe the hub replays already-completed chunks so late subscribers are
/// backfilled before live events (clients dedupe by <c>chunkIndex</c>).
/// </summary>
public sealed class ReaderHub : Hub
{
    private readonly ISessionStore _sessions;

    public ReaderHub(ISessionStore sessions) => _sessions = sessions;

    public static string Group(Guid sessionId) => $"session:{sessionId}";

    public async Task Subscribe(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var id)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, Group(id));

        // Backfill chunks already completed before this client joined.
        var stored = _sessions.Get(id);
        if (stored is null) return;
        foreach (var chunk in stored.SnapshotChunks())
            await Clients.Caller.SendAsync("ChunkReady", chunk);
    }

    public Task Unsubscribe(string sessionId)
    {
        return Guid.TryParse(sessionId, out var id)
            ? Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(id))
            : Task.CompletedTask;
    }
}
