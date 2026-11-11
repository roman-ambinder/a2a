using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace A2A.Analyzers;

/// <summary>
///     Finds A2A hosting mistakes at build time that the app would otherwise report only when it starts: endpoints mapped
///     without <c>UseA2A()</c> (A2A0001), endpoints without authorization (A2A0002), and <c>UseA2A()</c> in the wrong place
///     of the middleware pipeline (A2A0003).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class A2ASetupAnalyzer : DiagnosticAnalyzer
{
    /// <summary>A2A0001: endpoints are mapped, but <c>UseA2A()</c> is never called.</summary>
    public static readonly DiagnosticDescriptor MissingUseA2A = new(
        "A2A0001", "A2A endpoints need UseA2A()",
        "'{0}' maps A2A endpoints, but app.UseA2A() is never called; the app refuses to start without it. Call app.UseA2A() after UseAuthentication() and before UseAuthorization().",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        "UseA2A() adds the per-caller rate limiter that the A2A endpoints rely on.",
        "https://roman-ambinder.github.io/a2a/guides/getting-started.html#what-can-go-wrong",
        WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>A2A0002: the mapped endpoints have no authorization.</summary>
    public static readonly DiagnosticDescriptor NoAuthorization = new(
        "A2A0002", "A2A endpoints need authorization",
        "'{0}' maps A2A endpoints without authorization; the app refuses to start. Add .RequireAuthorization(), set o.AuthorizationPolicy, or allow anonymous callers on purpose (A2AServerOptions.AllowAnonymous or UseDevelopmentDefaults()).",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        "A2A endpoints refuse anonymous access unless the host opts in.",
        "https://roman-ambinder.github.io/a2a/guides/auth-and-ownership.html",
        WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>A2A0003: <c>UseA2A()</c> runs before authentication or after authorization.</summary>
    public static readonly DiagnosticDescriptor UseA2AOrder = new(
        "A2A0003", "UseA2A() is in the wrong place",
        "Call UseA2A() {0}: the rate limiter partitions by caller, so it must run after authentication and before authorization",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        null, "https://roman-ambinder.github.io/a2a/guides/limits-and-rate-limiting.html");

    private static readonly ImmutableHashSet<string> MapMethods =
        ImmutableHashSet.Create("MapA2A", "MapA2AJsonRpc", "MapA2AHttpJson", "MapA2AGrpc");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(MissingUseA2A, NoAuthorization, UseA2AOrder);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var state = new State();
            start.RegisterOperationBlockAction(block => AnalyzeBlock(block, state));
            start.RegisterOperationAction(op => AnalyzeAssignment((ISimpleAssignmentOperation)op.Operation, state),
                OperationKind.SimpleAssignment);
            start.RegisterCompilationEndAction(end => Report(end, state));
        });
    }

    private static void AnalyzeAssignment(ISimpleAssignmentOperation assignment, State state)
    {
        if (assignment.Target is not IPropertyReferenceOperation { Property: var property }) return;
        var type = property.ContainingType?.ToDisplayString();
        switch (property.Name)
        {
            case "AllowAnonymous" when type == "A2A.Server.A2AServerOptions":
            case "FallbackPolicy" when type == "Microsoft.AspNetCore.Authorization.AuthorizationOptions":
                state.AnonymousOptIn = true;
                break;
            case "Enabled" when type == "A2A.Server.AspNetCore.A2ARateLimitOptions":
                state.RateLimitOptOut = true;
                break;
        }
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, State state)
    {
        foreach (var root in context.OperationBlocks)
        {
            IInvocationOperation? useA2A = null, useAuthentication = null, useAuthorization = null;
            var maps = new List<IInvocationOperation>();
            var requireAuthorizationReceivers = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            foreach (var op in root.DescendantsAndSelf())
            {
                if (op is not IInvocationOperation { TargetMethod: var method } invocation) continue;
                var ns = method.ContainingNamespace?.ToDisplayString();
                switch (method.Name)
                {
                    case "UseA2A" when ns == "A2A.Server.AspNetCore":
                        useA2A ??= invocation;
                        state.UsesA2A = true;
                        break;
                    case "UseDevelopmentDefaults" when ns == "A2A.Server.AspNetCore":
                        state.AnonymousOptIn = true;
                        break;
                    case "UseAuthentication" when ns == "Microsoft.AspNetCore.Builder":
                        useAuthentication ??= invocation;
                        break;
                    case "UseAuthorization" when ns == "Microsoft.AspNetCore.Builder":
                        useAuthorization ??= invocation;
                        break;
                    case "RequireAuthorization":
                        if (Receiver(invocation) is ILocalReferenceOperation { Local: var local })
                            requireAuthorizationReceivers.Add(local);
                        break;
                    case var name when MapMethods.Contains(name) && ns is "A2A.Server.AspNetCore" or "A2A.Server.Grpc":
                        maps.Add(invocation);
                        break;
                }
            }

            foreach (var map in maps)
            {
                state.Maps.Add(map.Syntax.GetLocation());
                if (!IsAuthorized(map, requireAuthorizationReceivers))
                    state.Unauthorized.Add((map.Syntax.GetLocation(), map.TargetMethod.Name));
            }

            if (useA2A is null) continue;
            if (useAuthorization is not null && useA2A.Syntax.SpanStart > useAuthorization.Syntax.SpanStart)
                context.ReportDiagnostic(Diagnostic.Create(UseA2AOrder, useA2A.Syntax.GetLocation(),
                    "before UseAuthorization()"));
            else if (useAuthentication is not null && useA2A.Syntax.SpanStart < useAuthentication.Syntax.SpanStart)
                context.ReportDiagnostic(Diagnostic.Create(UseA2AOrder, useA2A.Syntax.GetLocation(),
                    "after UseAuthentication()"));
        }
    }

    /// <summary>
    ///     True when the mapped endpoints get authorization that can be seen here: a chained <c>RequireAuthorization()</c>,
    ///     a call on the local that holds the result, an options lambda that sets <c>AuthorizationPolicy</c>, or a receiver
    ///     that is itself a route group (which may carry the policy).
    /// </summary>
    private static bool IsAuthorized(IInvocationOperation map, HashSet<ILocalSymbol> requireAuthorizationReceivers)
    {
        if (Receiver(map)?.Type?.Name == "RouteGroupBuilder") return true;
        foreach (var argument in map.Arguments)
            if (argument.Value.DescendantsAndSelf().OfType<ISimpleAssignmentOperation>().Any(a =>
                    a.Target is IPropertyReferenceOperation { Property.Name: "AuthorizationPolicy" }))
                return true;

        IOperation current = map;
        while (current.Parent is { } parent)
        {
            switch (parent)
            {
                case IConversionOperation or IArgumentOperation or IParenthesizedOperation:
                    current = parent;
                    continue;
                case IInvocationOperation { TargetMethod.Name: "RequireAuthorization" }:
                    return true;
                case IInvocationOperation chained when ReferenceEquals(Receiver(chained), Unwrap(current)):
                    current = chained; // another convention (WithName, ...) first: keep looking up the chain
                    continue;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } }:
                    return requireAuthorizationReceivers.Contains(local);
                case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var assigned } }:
                    return requireAuthorizationReceivers.Contains(assigned);
            }

            return false;
        }

        return false;
    }

    private static IOperation Unwrap(IOperation op)
    {
        return op is IArgumentOperation { Value: var v } ? v : op;
    }

    /// <summary>The receiver of a call, also for an extension method called in reduced form.</summary>
    private static IOperation? Receiver(IInvocationOperation invocation)
    {
        var receiver = invocation.Instance ??
                       (invocation.TargetMethod.IsExtensionMethod && invocation.Arguments.Length > 0
                           ? invocation.Arguments[0].Value
                           : null);
        while (receiver is IConversionOperation { Operand: var operand }) receiver = operand;
        return receiver;
    }

    private static void Report(CompilationAnalysisContext context, State state)
    {
        if (!state.UsesA2A && !state.RateLimitOptOut)
            foreach (var location in state.Maps)
                context.ReportDiagnostic(Diagnostic.Create(MissingUseA2A, location, Name(location)));
        if (state.AnonymousOptIn) return;
        foreach (var (location, method) in state.Unauthorized)
            context.ReportDiagnostic(Diagnostic.Create(NoAuthorization, location, method));
    }

    private static string Name(Location location)
    {
        var text = location.SourceTree?.GetText().ToString(location.SourceSpan) ?? "MapA2A";
        var paren = text.IndexOf('(');
        var dot = text.LastIndexOf('.', paren < 0 ? text.Length - 1 : paren);
        return paren > dot ? text.Substring(dot + 1, paren - dot - 1) : "MapA2A";
    }

    private sealed class State
    {
        public readonly ConcurrentBag<Location> Maps = new();
        public readonly ConcurrentBag<(Location Location, string Method)> Unauthorized = new();
        public volatile bool AnonymousOptIn;
        public volatile bool RateLimitOptOut;
        public volatile bool UsesA2A;
    }
}
