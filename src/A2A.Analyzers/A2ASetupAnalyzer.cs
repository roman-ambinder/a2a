using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace A2A.Analyzers;

/// <summary>
///     Finds A2A hosting mistakes at build time that the app would otherwise report only when it starts: endpoints mapped
///     without <c>UseA2A()</c> (A2A0001), endpoints without authorization (A2A0002), and <c>UseA2A()</c> in the wrong place
///     of the middleware pipeline (A2A0003).
/// </summary>
/// <remarks>
///     A2A0001 and A2A0002 can depend on code anywhere in the project, so they are normally reported at the end of the
///     build, which IDEs do not run while you type. When one method creates the app, maps the endpoints and calls none of
///     your own methods with the app, its builder or its services, and no other code names anything that could change the
///     answer, they are reported from that method instead, so the IDE shows them as you type.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class A2ASetupAnalyzer : DiagnosticAnalyzer
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
        "'{0}' maps A2A endpoints without authorization; the app refuses to start. Add .RequireAuthorization(), set o.AuthorizationPolicy, call AddApiKeyAuthentication() or AddBearerAuthentication() on the A2A builder, or allow anonymous callers on purpose (A2AServerOptions.AllowAnonymous or UseDevelopmentDefaults()).",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        "A2A endpoints refuse anonymous access unless the host opts in.",
        "https://roman-ambinder.github.io/a2a/guides/auth-and-ownership.html",
        WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>A2A0001 reported while you type, when one method holds the whole setup (see <see cref="IsSelfContained" />).</summary>
    public static readonly DiagnosticDescriptor MissingUseA2ALive = Live(MissingUseA2A);

    /// <summary>A2A0002 reported while you type, when one method holds the whole setup.</summary>
    public static readonly DiagnosticDescriptor NoAuthorizationLive = Live(NoAuthorization);

    /// <summary>A2A0003: <c>UseA2A()</c> runs before authentication or after authorization.</summary>
    public static readonly DiagnosticDescriptor UseA2AOrder = new(
        "A2A0003", "UseA2A() is in the wrong place",
        "Call UseA2A() {0}: the rate limiter partitions by caller, so it must run after authentication and before authorization",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        null, "https://roman-ambinder.github.io/a2a/guides/limits-and-rate-limiting.html");

    /// <summary>
    ///     Names that, outside the method being analyzed, may change its answer: a call or setting that lifts A2A0001 or
    ///     A2A0002. When any other code names one, the method is left to the end-of-build check.
    /// </summary>
    private static readonly ImmutableHashSet<string> LiftingNames = ImmutableHashSet.Create(StringComparer.Ordinal,
        "UseA2A", "UseDevelopmentDefaults", "AddApiKeyAuthentication", "AddCardAuthentication", "AddBearerAuthentication",
        "AllowAnonymous", "FallbackPolicy", "A2AServerOptions", "AddA2ARateLimiting", "A2ARateLimitOptions");

    private static readonly ImmutableHashSet<string> HostingTypes = ImmutableHashSet.Create(StringComparer.Ordinal,
        "Microsoft.AspNetCore.Builder.WebApplication", "Microsoft.AspNetCore.Builder.WebApplicationBuilder",
        "Microsoft.Extensions.DependencyInjection.IServiceCollection", "Microsoft.AspNetCore.Builder.IApplicationBuilder",
        "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder", "Microsoft.Extensions.Hosting.IHostApplicationBuilder",
        "A2A.Server.A2AServerBuilder");

    private static readonly ImmutableHashSet<string> MapMethods =
        ImmutableHashSet.Create("MapA2A", "MapA2AJsonRpc", "MapA2AHttpJson", "MapA2AGrpc");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(MissingUseA2A, NoAuthorization, UseA2AOrder, MissingUseA2ALive, NoAuthorizationLive);

    private static DiagnosticDescriptor Live(DiagnosticDescriptor d)
    {
        return new DiagnosticDescriptor(d.Id, d.Title, d.MessageFormat, d.Category, d.DefaultSeverity,
            d.IsEnabledByDefault, d.Description, d.HelpLinkUri);
    }

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
        switch (Classify(assignment))
        {
            case Setting.AnonymousOptIn:
                state.AnonymousOptIn = true;
                break;
            case Setting.RateLimitOptOut:
                state.RateLimitOptOut = true;
                break;
        }
    }

    /// <summary>
    ///     What an assignment means for A2A0001/A2A0002. A constant value is taken at its word
    ///     (<c>AllowAnonymous = false</c> opts nothing in, <c>Enabled = true</c> turns nothing off); any other value might
    ///     be either, so it counts, to avoid warning about code that may be right.
    /// </summary>
    private static Setting Classify(ISimpleAssignmentOperation assignment)
    {
        if (assignment.Target is not IPropertyReferenceOperation { Property: var property }) return Setting.None;
        var type = property.ContainingType?.ToDisplayString();
        var constant = assignment.Value.ConstantValue;
        var isBool = constant.HasValue && constant.Value is bool;
        return property.Name switch
        {
            "AllowAnonymous" when type == "A2A.Server.A2AServerOptions" =>
                isBool && !(bool)constant.Value! ? Setting.None : Setting.AnonymousOptIn,
            "FallbackPolicy" when type == "Microsoft.AspNetCore.Authorization.AuthorizationOptions" =>
                Setting.AnonymousOptIn,
            "Enabled" when type == "A2A.Server.AspNetCore.A2ARateLimitOptions" =>
                isBool && (bool)constant.Value! ? Setting.None : Setting.RateLimitOptOut,
            _ => Setting.None
        };
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, State state)
    {
        foreach (var root in context.OperationBlocks)
        {
            IInvocationOperation? useA2A = null, useAuthentication = null, useAuthorization = null;
            bool createsApp = false, callsOwnHostingCode = false, anonymousOptIn = false, authorizedByDefault = false,
                rateLimitOptOut = false;
            var maps = new List<IInvocationOperation>();
            var requireAuthorizationReceivers = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            foreach (var op in root.DescendantsAndSelf())
            {
                if (op is ISimpleAssignmentOperation assignment)
                {
                    var setting = Classify(assignment);
                    anonymousOptIn |= setting == Setting.AnonymousOptIn;
                    rateLimitOptOut |= setting == Setting.RateLimitOptOut;
                    continue;
                }

                if (op is not IInvocationOperation { TargetMethod: var method } invocation) continue;
                if (method.Locations.Any(l => l.IsInSource) && PassesHostingObject(invocation))
                    callsOwnHostingCode = true;
                var ns = method.ContainingNamespace?.ToDisplayString();
                switch (method.Name)
                {
                    case "UseA2A" when ns == "A2A.Server.AspNetCore":
                        useA2A ??= invocation;
                        state.UsesA2A = true;
                        break;
                    case "UseDevelopmentDefaults" when ns == "A2A.Server.AspNetCore":
                        state.AnonymousOptIn = anonymousOptIn = true;
                        break;
                    case "AddApiKeyAuthentication" or "AddCardAuthentication" or "AddBearerAuthentication"
                        when ns == "A2A.Server.AspNetCore":
                        // its policy goes on every A2A endpoint without authorization
                        state.AuthorizedByDefault = authorizedByDefault = true;
                        break;
                    case "CreateBuilder" or "CreateSlimBuilder" or "CreateEmptyBuilder"
                        when method.ContainingType?.ToDisplayString() == "Microsoft.AspNetCore.Builder.WebApplication":
                        createsApp = true;
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

            if (maps.Count > 0 && createsApp && !callsOwnHostingCode &&
                IsSelfContained(context.Compilation, root.Syntax, state))
            {
                // The whole setup is here: report now, so the IDE shows it while you type. The end-of-build check
                // never sees these maps, so nothing is reported twice.
                foreach (var map in maps)
                {
                    if (useA2A is null && !rateLimitOptOut)
                        context.ReportDiagnostic(Diagnostic.Create(MissingUseA2ALive, map.Syntax.GetLocation(),
                            Name(map.Syntax.GetLocation())));
                    if (!anonymousOptIn && !authorizedByDefault && !IsAuthorized(map, requireAuthorizationReceivers))
                        context.ReportDiagnostic(Diagnostic.Create(NoAuthorizationLive, map.Syntax.GetLocation(),
                            map.TargetMethod.Name));
                }

                maps.Clear();
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

    /// <summary>
    ///     True when no code outside <paramref name="block" /> names anything that could lift A2A0001 or A2A0002 (see
    ///     <see cref="LiftingNames" />), so the method's own calls decide. A token scan, done once per syntax tree.
    /// </summary>
    private static bool IsSelfContained(Compilation compilation, SyntaxNode block, State state)
    {
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (tree != block.SyntaxTree)
            {
                if (state.TreeNamesLifting.GetOrAdd(tree, t => NamesLifting(t.GetRoot(), default))) return false;
            }
            else if (NamesLifting(tree.GetRoot(), OwnSpan(block)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     The text the method's own operations cover. For top-level statements the block is the whole file, which may also
    ///     declare types: only the statements (they come first, C# requires it) belong to it.
    /// </summary>
    private static Microsoft.CodeAnalysis.Text.TextSpan OwnSpan(SyntaxNode block)
    {
        if (block is not CompilationUnitSyntax unit) return block.FullSpan;
        var statements = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        return statements.Count == 0
            ? default
            : Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(statements[0].FullSpan.Start, statements[statements.Count - 1].FullSpan.End);
    }

    private static bool NamesLifting(SyntaxNode root, Microsoft.CodeAnalysis.Text.TextSpan skip)
    {
        foreach (var token in root.DescendantTokens())
            if (LiftingNames.Contains(token.ValueText) && !skip.Contains(token.Span))
                return true;
        return false;
    }

    /// <summary>True when a call hands the app, its builder, its services or the A2A builder to the method it calls.</summary>
    private static bool PassesHostingObject(IInvocationOperation invocation)
    {
        if (invocation.Instance?.Type is { } instance && IsHosting(instance)) return true;
        foreach (var argument in invocation.Arguments)
            if (argument.Value.Type is { } type && IsHosting(type))
                return true;
        return false;
    }

    private static bool IsHosting(ITypeSymbol type)
    {
        if (HostingTypes.Contains(type.OriginalDefinition.ToDisplayString())) return true;
        foreach (var i in type.AllInterfaces)
            if (HostingTypes.Contains(i.OriginalDefinition.ToDisplayString()))
                return true;
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
        if (state.AnonymousOptIn || state.AuthorizedByDefault) return;
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

    private enum Setting
    {
        None,
        AnonymousOptIn,
        RateLimitOptOut
    }

    private sealed class State
    {
        public readonly ConcurrentDictionary<SyntaxTree, bool> TreeNamesLifting = new();
        public readonly ConcurrentBag<Location> Maps = new();
        public readonly ConcurrentBag<(Location Location, string Method)> Unauthorized = new();
        public volatile bool AnonymousOptIn;
        public volatile bool AuthorizedByDefault;
        public volatile bool RateLimitOptOut;
        public volatile bool UsesA2A;
    }
}
