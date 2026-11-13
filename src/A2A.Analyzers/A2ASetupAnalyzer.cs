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
    ///     A2A0002 when the A2A builder has a policy (<c>AddApiKeyAuthentication()</c>, <c>RequirePolicy(...)</c>, ...) but
    ///     the endpoints have <c>AllowAnonymous()</c>, on them or from an outer group: the policy goes only on endpoints
    ///     without authorization metadata, so it is kept off them, and the startup check refuses the <c>IAllowAnonymous</c>
    ///     metadata.
    /// </summary>
    public static readonly DiagnosticDescriptor AnonymousEndpoints = new(
        "A2A0002", "A2A endpoints need authorization",
        "'{0}' maps A2A endpoints with AllowAnonymous(), so the A2A builder's policy is not put on them and the app refuses to start. Remove AllowAnonymous() from the endpoints and from the route groups around them, or allow anonymous callers on purpose (A2AServerOptions.AllowAnonymous or ConfigureForDevelopment()).",
        "A2A.Hosting", DiagnosticSeverity.Warning, true,
        "A2A endpoints refuse anonymous access unless the host opts in.",
        "https://roman-ambinder.github.io/a2a/guides/auth-and-ownership.html",
        WellKnownDiagnosticTags.CompilationEnd);

    /// <summary><see cref="AnonymousEndpoints" /> reported while you type.</summary>
    public static readonly DiagnosticDescriptor AnonymousEndpointsLive = Live(AnonymousEndpoints);

    /// <summary>
    ///     The property, on an <see cref="AnonymousEndpoints" /> diagnostic, that tells the code fix not to offer
    ///     <c>RequireAuthorization()</c>: the <c>IAllowAnonymous</c> metadata would stay, and the startup check refuses it.
    /// </summary>
    public const string CauseProperty = "Cause";

    /// <summary>The value of <see cref="CauseProperty" /> for <see cref="AnonymousEndpoints" />.</summary>
    public const string AllowAnonymousCause = "AllowAnonymous";

    private static readonly ImmutableDictionary<string, string?> AnonymousProperties =
        ImmutableDictionary<string, string?>.Empty.Add(CauseProperty, AllowAnonymousCause);

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
        ImmutableArray.Create(NoAuthorization, NoAuthorizationLive, AnonymousEndpoints, AnonymousEndpointsLive);

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
                    if (anonymousOptIn)
                        continue;
                    else if (authorizedByDefault)
                    {
                        if (AllowsAnonymous(map, root))
                            context.ReportDiagnostic(Diagnostic.Create(AnonymousEndpointsLive, map.Syntax.GetLocation(),
                                AnonymousProperties, map.TargetMethod.Name));
                    }
                    else if (!IsAuthorized(map, root))
                        context.ReportDiagnostic(Diagnostic.Create(NoAuthorizationLive, map.Syntax.GetLocation(),
                            map.TargetMethod.Name));

                maps.Clear();
            }

            foreach (var map in maps)
            {
                if (!IsAuthorized(map, root))
                    state.Unauthorized.Add((map.Syntax.GetLocation(), map.TargetMethod.Name));
                if (AllowsAnonymous(map, root))
                    state.Anonymous.Add((map.Syntax.GetLocation(), map.TargetMethod.Name));
            }
        }
    }

    /// <summary>
    ///     False when the mapped endpoints are provably without authorization, as the startup check sees them: it wants
    ///     <c>IAuthorizeData</c> metadata (<c>RequireAuthorization(...)</c>, <c>WithMetadata(new AuthorizeAttribute())</c>,
    ///     on the endpoints or on an outer group) and no <c>IAllowAnonymous</c> metadata. When the result, or the group it is
    ///     mapped on, goes to code this method does not follow (your own method, a field, a return), that code may add
    ///     authorization, so nothing is reported and the startup check decides.
    /// </summary>
    private static bool IsAuthorized(IInvocationOperation map, IOperation root)
    {
        var walk = new BuilderWalk(root);
        var auth = walk.Uses(map, 0) | walk.Origin(Receiver(map), 0);
        return (auth & Auth.Anonymous) == 0 && (auth & (Auth.Authorized | Auth.Unknown)) != 0;
    }

    /// <summary>
    ///     True when the mapped endpoints provably get <c>IAllowAnonymous</c> metadata: from the builder <c>MapA2A</c>
    ///     returns (<c>MapA2A(...).AllowAnonymous()</c>, through the chain and the locals that hold it) or from an outer
    ///     group (<c>MapGroup(...).AllowAnonymous().MapA2A(...)</c>). With a policy on the A2A builder, that is what fails
    ///     the startup check: the policy goes on every endpoint without <c>IAuthorizeData</c> or <c>IAllowAnonymous</c>
    ///     metadata, so these keep none, and the check, which reads the endpoints as the app serves them, refuses them.
    /// </summary>
    private static bool AllowsAnonymous(IInvocationOperation map, IOperation root)
    {
        var walk = new BuilderWalk(root);
        return ((walk.Uses(map, 0) | walk.Origin(Receiver(map), 0)) & Auth.Anonymous) != 0;
    }

    /// <summary>What code applies to an endpoint builder. Flags, since several paths can apply several things.</summary>
    [Flags]
    private enum Auth
    {
        None = 0,

        /// <summary><c>IAuthorizeData</c> metadata.</summary>
        Authorized = 1,

        /// <summary><c>IAllowAnonymous</c> metadata, which fails the startup check whatever else is there.</summary>
        Anonymous = 2,

        /// <summary>Code that is not followed, which may add either.</summary>
        Unknown = 4
    }

    /// <summary>Follows an endpoint builder through one method body: the calls on it and the locals that hold it.</summary>
    private sealed class BuilderWalk
    {
        private const int MaxDepth = 4;
        private const string AuthorizeData = "Microsoft.AspNetCore.Authorization.IAuthorizeData";
        private const string AllowAnonymous = "Microsoft.AspNetCore.Authorization.IAllowAnonymous";
        private readonly IOperation root;

        public BuilderWalk(IOperation root)
        {
            this.root = root;
        }

        /// <summary>What the uses of the builder that <paramref name="value" /> produces apply to it.</summary>
        public Auth Uses(IOperation value, int depth)
        {
            var auth = Auth.None;
            var current = value;
            while (true)
            {
                var parent = current.Parent;
                while (parent is IConversionOperation or IParenthesizedOperation)
                {
                    current = parent;
                    parent = parent.Parent;
                }

                if (parent is IArgumentOperation { Parent: IInvocationOperation outer } argument &&
                    ReferenceEquals(Receiver(outer), value))
                {
                    current = argument; // an extension method called in reduced form: the builder is its receiver
                    parent = outer;
                }

                switch (parent)
                {
                    case IInvocationOperation call when ReferenceEquals(Receiver(call), value):
                        var (applied, fluent) = Convention(call);
                        auth |= applied;
                        if (!fluent) return auth;
                        current = value = call; // the convention returns the builder: keep looking up the chain
                        continue;
                    case IExpressionStatementOperation:
                        return auth; // the result is dropped
                    case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation { Symbol: var local } }:
                        return auth | Local(local, depth);
                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation { Local: var assigned } } assignment
                        when ReferenceEquals(assignment.Value, current):
                        return auth | Local(assigned, depth);
                    default:
                        return auth | Auth.Unknown; // an argument, a field, a return, ...: code that is not followed
                }
            }
        }

        /// <summary>What is applied to the group <paramref name="receiver" /> evaluates to, through the calls that made it.</summary>
        public Auth Origin(IOperation? receiver, int depth)
        {
            if (receiver?.Type?.Name != "RouteGroupBuilder") return Auth.None; // the app, or a plain route builder
            if (depth > MaxDepth) return Auth.Unknown;
            switch (receiver)
            {
                case IInvocationOperation call when IsFramework(call.TargetMethod) &&
                                                    call.TargetMethod.Name.StartsWith("Map", StringComparison.Ordinal):
                    // a new group (MapGroup, MapA2A): outer groups apply to it, as they do to the endpoints the app serves
                    return Origin(Receiver(call), depth + 1);
                case IInvocationOperation call:
                    var (applied, fluent) = Convention(call);
                    return fluent ? applied | Origin(Receiver(call), depth + 1) : applied | Auth.Unknown;
                case ILocalReferenceOperation { Local: var local }:
                    return Local(local, depth);
                default:
                    return Auth.Unknown; // a parameter, a field, ...: made by code that is not followed
            }
        }

        /// <summary>What is applied to the builder a local holds: by the values assigned to it and by its uses.</summary>
        private Auth Local(ILocalSymbol local, int depth)
        {
            if (depth > MaxDepth) return Auth.Unknown;
            var auth = Auth.None;
            foreach (var op in root.Descendants())
                switch (op)
                {
                    case IVariableDeclaratorOperation { Initializer.Value: var value } declarator
                        when SymbolEqualityComparer.Default.Equals(declarator.Symbol, local):
                        auth |= Origin(Strip(value), depth + 1);
                        break;
                    case ISimpleAssignmentOperation { Target: ILocalReferenceOperation target } assignment
                        when SymbolEqualityComparer.Default.Equals(target.Local, local):
                        auth |= Origin(Strip(assignment.Value), depth + 1);
                        break;
                    case ILocalReferenceOperation reference
                        when SymbolEqualityComparer.Default.Equals(reference.Local, local) &&
                             !(reference.Parent is ISimpleAssignmentOperation a && ReferenceEquals(a.Target, reference)):
                        auth |= Uses(reference, depth + 1);
                        break;
                }

            return auth;
        }

        /// <summary>
        ///     What a call on an endpoint builder applies to it, and whether it returns that builder. Calls that create
        ///     endpoints on it (<c>MapGet</c>, <c>MapGroup</c>, <c>MapA2A</c>, ...) apply nothing to it.
        /// </summary>
        private static (Auth Applied, bool Fluent) Convention(IInvocationOperation call)
        {
            var method = call.TargetMethod;
            if (method.Name == "RequireAuthorization") return (Auth.Authorized, true);
            if (!IsFramework(method)) return (Auth.Unknown, false); // your own method: it may do anything
            switch (method.Name)
            {
                case "AllowAnonymous":
                    return (Auth.Anonymous, true);
                case "WithMetadata":
                    return (Metadata(call), true);
                case "Add" or "Finally":
                    return (Auth.Unknown, false); // a convention delegate, which may add any metadata
                case var name when name.StartsWith("Map", StringComparison.Ordinal):
                    return (Auth.None, false);
            }

            var receiverType = Receiver(call)?.Type;
            return (Auth.None, receiverType is not null && SymbolEqualityComparer.Default.Equals(call.Type, receiverType));
        }

        /// <summary>What the items given to <c>WithMetadata</c> are, as far as their types show.</summary>
        private static Auth Metadata(IInvocationOperation call)
        {
            var auth = Auth.None;
            var receiver = Receiver(call);
            foreach (var argument in call.Arguments)
            {
                if (ReferenceEquals(Strip(argument.Value), receiver)) continue;
                var items = argument is
                    { ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation { Initializer: { } init } }
                    ? init.ElementValues
                    : ImmutableArray.Create(argument.Value);
                foreach (var item in items)
                {
                    var value = Strip(item);
                    if (value.Type is not { } type)
                    {
                        auth |= Auth.Unknown;
                        continue;
                    }

                    if (Implements(type, AuthorizeData)) auth |= Auth.Authorized;
                    if (Implements(type, AllowAnonymous)) auth |= Auth.Anonymous;
                    // a value of an open type (object, an interface, a class that is not sealed) may be either
                    if (value is not IObjectCreationOperation && !type.IsSealed && !type.IsValueType)
                        auth |= Auth.Unknown;
                }
            }

            return auth;
        }

        private static bool Implements(ITypeSymbol type, string name)
        {
            return type.ToDisplayString() == name || type.AllInterfaces.Any(i => i.ToDisplayString() == name);
        }

        /// <summary>ASP.NET Core and A2A methods, whose effect on authorization is known.</summary>
        private static bool IsFramework(IMethodSymbol method)
        {
            if (method.Locations.Any(l => l.IsInSource)) return false;
            var ns = method.ContainingNamespace?.ToDisplayString() ?? "";
            return ns.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
                   ns.StartsWith("A2A.", StringComparison.Ordinal);
        }

        private static IOperation Strip(IOperation op)
        {
            while (op is IConversionOperation { Operand: var operand }) op = operand;
            return op;
        }
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
        if (state.AnonymousOptIn) return;
        if (state.AuthorizedByDefault)
        {
            foreach (var (location, method) in state.Anonymous)
                context.ReportDiagnostic(Diagnostic.Create(AnonymousEndpoints, location, AnonymousProperties, method));
            return;
        }

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

        /// <summary>Mappings whose endpoints get <c>AllowAnonymous()</c> (see <see cref="AllowsAnonymous" />).</summary>
        public readonly ConcurrentBag<(Location Location, string Method)> Anonymous = new();
        public volatile bool AnonymousOptIn;
        public volatile bool AuthorizedByDefault;
    }
}
