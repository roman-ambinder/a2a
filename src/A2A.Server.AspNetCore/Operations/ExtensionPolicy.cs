using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Server.AspNetCore.Operations;

/// <summary>
///     Evaluates the ASP.NET Core authorization policy of an extension operation (
///     <see cref="A2AExtensionOperation.AuthorizationPolicy" />)
///     before the HTTP bindings and the gRPC extension service call it, and passes the result to the core in
///     <see cref="ServerCallContext.Items" />.
/// </summary>
public static class ExtensionPolicy
{
    /// <summary>
    ///     Does nothing when the operation has no policy. Otherwise evaluates it for <c>HttpContext.User</c>: on success sets
    ///     <see cref="A2AExtensionOperation.PolicySatisfiedItem" />; on failure throws the error an authorization handler
    ///     denial
    ///     gets (Unauthenticated for an anonymous caller the server refuses anyway, otherwise PermissionDenied).
    /// </summary>
    /// <param name="http">The request; its <c>User</c> is checked.</param>
    /// <param name="operation">The extension operation.</param>
    /// <param name="call">The call context that receives <see cref="A2AExtensionOperation.PolicySatisfiedItem" />.</param>
    /// <param name="allowAnonymous">The server's <see cref="A2AServerOptions.AllowAnonymous" />.</param>
    public static async Task RequireAsync(HttpContext http, A2AExtensionOperation operation, ServerCallContext call,
        bool allowAnonymous)
    {
        if (operation.AuthorizationPolicy is not { } policy) return;
        var authorization = http.RequestServices.GetService<IAuthorizationService>() ??
                            throw new InvalidOperationException(
                                $"The extension operation '{operation.Method}' has the authorization policy '{policy}', but no IAuthorizationService is registered. Call services.AddAuthorization().");
        var result = await authorization.AuthorizeAsync(http.User, policy).ConfigureAwait(false);
        if (!result.Succeeded)
            // The same order as the core: the anonymous backstop first, then the denial.
            throw !allowAnonymous && !call.IsAuthenticated
                ? new A2AException(A2AErrorCode.Unauthenticated)
                : new A2AException(A2AErrorCode.PermissionDenied,
                    $"The caller is not allowed to call {operation.Method}.")
                {
                    // Which permission is missing (spec §3.3.2), without the policy name, which is the server's own.
                    Metadata = new Dictionary<string, string>
                        { ["permission"] = "policy", ["operation"] = operation.Method }
                };
        call.Items[A2AExtensionOperation.PolicySatisfiedItem] = true;
    }
}