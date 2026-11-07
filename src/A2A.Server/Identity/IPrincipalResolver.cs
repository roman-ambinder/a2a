using System.Security.Claims;

namespace A2A.Server;

/// <summary>Maps an authenticated user to the stable principal id used for task ownership.</summary>
public interface IPrincipalResolver
{
    /// <summary>
    /// Returns the principal id for an authenticated <paramref name="user"/>, or null when none can be determined
    /// (the caller is then rejected as unauthenticated). Not called for anonymous callers. The id
    /// <see cref="ServerCallContext.AnonymousPrincipal"/> is reserved: the HTTP layer rejects it like null.
    /// </summary>
    string? Resolve(ClaimsPrincipal user);
}

/// <summary>
/// Default: <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then null. An id equal to
/// <see cref="ServerCallContext.AnonymousPrincipal"/> is also null (the caller is rejected as unauthenticated), so an
/// authenticated user can never share the owner of anonymous callers' tasks.
/// </summary>
public sealed class DefaultPrincipalResolver : IPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal user)
    {
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(id)) id = user.FindFirst("sub")?.Value;
        return string.IsNullOrEmpty(id) || string.Equals(id, ServerCallContext.AnonymousPrincipal, StringComparison.Ordinal) ? null : id;
    }
}
