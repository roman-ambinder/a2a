using System.Security.Claims;

namespace A2A.Server;

/// <summary>Maps an authenticated user to the stable principal id used for task ownership.</summary>
public interface IPrincipalResolver
{
    /// <summary>
    /// Returns the principal id for an authenticated <paramref name="user"/>, or null when none can be determined
    /// (the caller is then rejected as unauthenticated). Not called for anonymous callers.
    /// </summary>
    string? Resolve(ClaimsPrincipal user);
}

/// <summary>Default: <see cref="ClaimTypes.NameIdentifier"/>, then <c>sub</c>, then null.</summary>
public sealed class DefaultPrincipalResolver : IPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal user)
    {
        var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(id)) return id;
        id = user.FindFirst("sub")?.Value;
        return string.IsNullOrEmpty(id) ? null : id;
    }
}
