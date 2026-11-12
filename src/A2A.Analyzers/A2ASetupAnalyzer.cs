using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace A2A.Analyzers;

/// <summary>
///     Finds an A2A hosting mistake at build time that the app would otherwise report only when it starts: endpoints
///     without authorization (A2A0002). A2A0001 and A2A0003 were about <c>UseA2A()</c>, which no longer exists: the rate
///     limit runs inside the A2A endpoints.
/// </summary>
/// <remarks>
///     A2A0002 can depend on code anywhere in the project, so it is normally reported at the end of the
///     build, which IDEs do not run while you type. When one method creates the app, maps the endpoints and calls none of
///     your own methods with the app, its builder or its services, and no other code names anything that could change the
///     answer, they are reported from that method instead, so the IDE shows them as you type.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
internal sealed class A2ASetupAnalyzer : DiagnosticAnalyzer
{
    /// <summary>A2A0002: the mapped endpoints have no authorization.</summary>
    public static readonly DiagnosticDescriptor NoAuthorization = new(
        "A2A0002", "A2A endpoints need authorization",
        "'{0}' maps A2A endpoints without authorization; the app refuses to start. Call AddApiKeyAuthentication(), AddBearerAuthentication() or RequirePolicy(...) on the A2A builder, add .RequireAuthorization(), or allow anonymous callers on purpose (A2AServerOptions.AllowAnonymous or ConfigureForDevelopment()).",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        "A2A endpoints refuse anonymous access unless the host opts in.",
        "https://roman-ambinder.github.io/a2a/guides/auth-and-ownership.html",
        WellKnownDiagnosticTags.CompilationEnd);

    /// <summary>A2A0002 reported while you type, when one method holds the whole setup (see <see cref="IsSelfContained" />).</summary>
    public static readonly DiagnosticDescriptor NoAuthorizationLive = Live(NoAuthorization);

    /// <summary>
    ///     Names that, outside the method being analyzed, may change its answer: a call or setting that lifts A2A0002. When
    ///     any other code names one, the method is left to the end-of-build check.
    /// </summary>
    private static readonly ImmutableHashSet<string> LiftingNames = ImmutableHashSet.Create(StringComparer.Ordinal,
        "ConfigureForDevelopment", "AddApiKeyAuthentication", "AddCardAuthentication", "AddBearerAuthentication", "RequirePolicy",
        "AllowAnonymous", "FallbackPolicy", "A2AServerOptions");

    private static readonly ImmutableHashSet<string> HostingTypes = ImmutableHashSet.Create(StringComparer.Ordinal,
        "Microsoft.AspNetCore.Builder.WebApplication", "Microsoft.AspNetCore.Builder.WebApplicationBuilder",
        "Microsoft.Extensions.DependencyInjection.IServiceCollection", "Microsoft.AspNetCore.Builder.IApplicationBuilder",
        "Microsoft.AspNetCore.Routing.IEndpointRouteBuilder", "Microsoft.Extensions.Hosting.IHostApplicationBuilder",
        "A2A.Server.A2AServerBuilder");

    private static readonly ImmutableHashSet<string> MapMethods =
        ImmutableHashSet.Create("MapA2A", "MapA2AJsonRpc", "MapA2AHttpJson", "MapA2AGrpc");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(NoAuthorization, NoAuthorizationLive);

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
        if (Classify(assignment) == Setting.AnonymousOptIn) state.AnonymousOptIn = true;
    }

    /// <summary>
    ///     What an assignment means for A2A0002. A constant value is taken at its word (<c>AllowAnonymous = false</c> opts
    ///     nothing in); any other value might be either, so it counts, to avoid warning about code that may be right.
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
            _ => Setting.None
        };
    }

    private static void AnalyzeBlock(OperationBlockAnalysisContext context, State state)
    {
        foreach (var root in context.OperationBlocks)
        {
            bool createsApp = false, callsOwnHostingCode = false, anonymousOptIn = false, authorizedByDefault = false;
            var maps = new List<IInvocationOperation>();
            var requireAuthorizationReceivers = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            foreach (var op in root.DescendantsAndSelf())
            {
                if (op is ISimpleAssignmentOperation assignment)
                {
                    anonymousOptIn |= Classify(assignment) == Setting.AnonymousOptIn;
                    continue;
                }

                if (op is not IInvocationOperation { TargetMethod: var method } invocation) continue;
                if (method.Locations.Any(l => l.IsInSource) && PassesHostingObject(invocation))
                    callsOwnHostingCode = true;
                var ns = method.ContainingNamespace?.ToDisplayString();
                switch (method.Name)
                {
                    case "ConfigureForDevelopment" when ns == "A2A.Server.AspNetCore":
                        state.AnonymousOptIn = anonymousOptIn = true;
                        break;
                    case "AddApiKeyAuthentication" or "AddCardAuthentication" or "AddBearerAuthentication"
                        or "RequirePolicy" when ns == "A2A.Server.AspNetCore":
                        // its policy goes on every A2A endpoint without authorization
                        state.AuthorizedByDefault = authorizedByDefault = true;
                        break;
                    case "CreateBuilder" or "CreateSlimBuilder" or "CreateEmptyBuilder"
                        when method.ContainingType?.ToDisplayString() == "Microsoft.AspNetCore.Builder.WebApplication":
                        createsApp = true;
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
                    if (!anonymousOptIn && !authorizedByDefault && !IsAuthorized(map, requireAuthorizationReceivers))
                        context.ReportDiagnostic(Diagnostic.Create(NoAuthorizationLive, map.Syntax.GetLocation(),
                            map.TargetMethod.Name));

                maps.Clear();
            }

            foreach (var map in maps)
                if (!IsAuthorized(map, requireAuthorizationReceivers))
                    state.Unauthorized.Add((map.Syntax.GetLocation(), map.TargetMethod.Name));
        }
    }

    /// <summary>
    ///     True when the mapped endpoints get authorization that can be seen here: a chained <c>RequireAuthorization()</c>,
    ///     a call on the local that holds the result, or a receiver that is itself a route group (which may carry the
    ///     policy).
    /// </summary>
    private static bool IsAuthorized(IInvocationOperation map, HashSet<ILocalSymbol> requireAuthorizationReceivers)
    {
        if (Receiver(map)?.Type?.Name == "RouteGroupBuilder") return true;

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
    ///     True when no code outside <paramref name="block" /> names anything that could lift A2A0002 (see
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
        if (state.AnonymousOptIn || state.AuthorizedByDefault) return;
        foreach (var (location, method) in state.Unauthorized)
            context.ReportDiagnostic(Diagnostic.Create(NoAuthorization, location, method));
    }

    private enum Setting
    {
        None,
        AnonymousOptIn
    }

    private sealed class State
    {
        public readonly ConcurrentDictionary<SyntaxTree, bool> TreeNamesLifting = new();
        public readonly ConcurrentBag<(Location Location, string Method)> Unauthorized = new();
        public volatile bool AnonymousOptIn;
        public volatile bool AuthorizedByDefault;
    }
}
