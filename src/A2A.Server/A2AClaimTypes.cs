namespace A2A.Server;

/// <summary>Claim types the A2A server reads from the authenticated caller.</summary>
public static class A2AClaimTypes
{
    /// <summary>
    ///     A tenant the caller may call. A caller with one or more of these claims may call only those tenants; a call to
    ///     any other tenant is refused before the <see cref="Authorization.A2AAuthorizationHandler" /> runs, like any other
    ///     denial (a task-scoped call looks like a missing task, the rest is <see cref="A2AErrorCode.PermissionDenied" />
    ///     with <c>permission</c> <c>tenant</c>), and written to the audit trail with the reason <c>TenantNotAllowed</c>.
    ///     A caller without the claim may call every tenant. API-key authentication sets it for keys registered with
    ///     tenants; for another scheme, add it in a claims transformation.
    /// </summary>
    public const string Tenant = "a2a:tenant";

    /// <summary>
    ///     The caller id of an API-key caller, without the <c>apikey:</c> owner prefix (API-key authentication in
    ///     <c>A2A.Server.AspNetCore</c> sets it). Read it in an authorization handler to give callers their own rules.
    /// </summary>
    public const string ApiKeyCaller = "a2a:apikey:caller";
}
