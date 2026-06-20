namespace PDFtoTTS.Api.Contracts;

/// <summary>Body for creating a TTS session over a document. <see cref="StartWordIndex"/>
/// begins synthesis at that source-word (default 0 = the start), so the reader can
/// jump to a position and synthesize from there instead of waiting for everything
/// before it.</summary>
public sealed record CreateSessionRequest(
    string Voice, float Speed = 1f, string Language = "en", int StartWordIndex = 0);

/// <summary>Body for renaming a document's display title.</summary>
public sealed record RenameDocumentRequest(string Name);

/// <summary>Body for saving a document's reading position (resume point) so it
/// follows the user across devices. <see cref="UpdatedAtMs"/> is the client's
/// Unix-ms timestamp, used server-side for last-writer-wins.</summary>
public sealed record UpdatePositionRequest(
    int Page = 1, int Word = 0, string? Voice = null, float Speed = 1f, long UpdatedAtMs = 0);

/// <summary>Structured error envelope returned by all endpoints.</summary>
public sealed record ErrorResponse(string Code, string Message, object? Detail = null);

// SignalR server→client payloads.
public sealed record SessionStatusPayload(string SessionId, string Status);
public sealed record ProgressPayload(string SessionId, int CompletedChunks, int TotalChunks, double Progress);
public sealed record ErrorPayload(string SessionId, string Code, string Message);
