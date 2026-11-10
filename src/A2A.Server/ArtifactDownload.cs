using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>
///     The answer to <see cref="A2ARequestHandler.OpenArtifactAsync" />: either a link to redirect the caller to
///     (<see cref="RedirectTo" />) or the open object (<see cref="Content" />). Exactly one is set. Dispose it when done:
///     that closes the object stream.
/// </summary>
public sealed class ArtifactDownload : IAsyncDisposable
{
    internal ArtifactDownload(Uri? redirectTo, ArtifactObjectContent? content)
    {
        RedirectTo = redirectTo;
        Content = content;
    }

    /// <summary>A presigned URL the caller fetches the bytes from, or null when <see cref="Content" /> is set.</summary>
    public Uri? RedirectTo { get; }

    /// <summary>The open object to stream through this server, or null when <see cref="RedirectTo" /> is set.</summary>
    public ArtifactObjectContent? Content { get; }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return Content?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}