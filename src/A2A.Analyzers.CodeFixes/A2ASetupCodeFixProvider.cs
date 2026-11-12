using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace A2A.Analyzers.CodeFixes;

/// <summary>
///     Fixes for the A2A setup analyzer (A2A.Analyzers): adds <c>.RequireAuthorization()</c> to a mapping without authorization
///     (A2A0002).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(A2ASetupCodeFixProvider))]
[Shared]
internal sealed class A2ASetupCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(NoAuthorizationId);

    // The id of A2ASetupAnalyzer's rule (A2A.Analyzers). A string, not its descriptor: the two assemblies share no
    // internals.
    private const string NoAuthorizationId = "A2A0002";

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider()
    {
        return WellKnownFixAllProviders.BatchFixer;
    }

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null) return;
        foreach (var diagnostic in context.Diagnostics)
        {
            if (root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true)
                    .FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } map) continue;
            context.RegisterCodeFix(CodeAction.Create("Add .RequireAuthorization()",
                ct => RequireAuthorization(context.Document, root, map, ct),
                "A2A0002.RequireAuthorization"), diagnostic);
        }
    }

    // RequireAuthorization() is an extension method in this namespace, which a file may not import (a class library,
    // ImplicitUsings off, only Microsoft.AspNetCore.Routing for IEndpointRouteBuilder).
    private const string BuilderNamespace = "Microsoft.AspNetCore.Builder";

    private static async Task<Document> RequireAuthorization(Document document, SyntaxNode root,
        InvocationExpressionSyntax map, CancellationToken cancellationToken)
    {
        var call = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                map.WithoutTrailingTrivia(), IdentifierName("RequireAuthorization")))
            .WithTrailingTrivia(map.GetTrailingTrivia());
        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var imported = model is null || IsImported(model, map);
        var newRoot = root.ReplaceNode(map, call);
        if (!imported && newRoot is CompilationUnitSyntax unit) newRoot = AddUsing(unit);
        return document.WithSyntaxRoot(newRoot);
    }

    /// <summary>
    ///     True when <see cref="BuilderNamespace" /> is in scope at <paramref name="node" />: a using in the file or an
    ///     enclosing namespace, a global using in any file (ImplicitUsings writes them), or an enclosing namespace itself.
    /// </summary>
    private static bool IsImported(SemanticModel model, SyntaxNode node)
    {
        foreach (var scope in model.GetImportScopes(node.SpanStart))
            if (scope.Imports.Any(i => i.NamespaceOrType is INamespaceSymbol ns && ns.ToDisplayString() == BuilderNamespace))
                return true;
        for (var ns = model.GetEnclosingSymbol(node.SpanStart)?.ContainingNamespace; ns is { IsGlobalNamespace: false };
             ns = ns.ContainingNamespace)
            if (ns.ToDisplayString() == BuilderNamespace)
                return true;
        return false;
    }

    /// <summary>
    ///     Adds <c>using Microsoft.AspNetCore.Builder;</c> to the file's usings, in order (System first, then by name),
    ///     with the file's line ending.
    /// </summary>
    private static CompilationUnitSyntax AddUsing(CompilationUnitSyntax unit)
    {
        var usings = unit.Usings;
        var newline = usings.Select(u => u.GetTrailingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia)))
            .FirstOrDefault(t => t.IsKind(SyntaxKind.EndOfLineTrivia));
        if (!newline.IsKind(SyntaxKind.EndOfLineTrivia)) newline = EndOfLine("\n");
        var directive = UsingDirective(ParseName(BuilderNamespace).WithLeadingTrivia(Space)).WithTrailingTrivia(newline);
        var index = 0;
        while (index < usings.Count && Precedes(usings[index])) index++;
        if (index == 0 && usings.Count > 0)
        {
            // the first using carries the file header: keep it first
            directive = directive.WithLeadingTrivia(usings[0].GetLeadingTrivia());
            usings = usings.Replace(usings[0], usings[0].WithLeadingTrivia());
        }
        else if (usings.Count == 0)
        {
            directive = directive.WithTrailingTrivia(newline, newline);
            if (unit.Externs.Count == 0 && unit.GetLeadingTrivia() is { Count: > 0 } header)
            {
                // a header comment stays above the using
                directive = directive.WithLeadingTrivia(header);
                unit = unit.WithoutLeadingTrivia();
            }
        }

        return unit.WithUsings(usings.Insert(index, directive));
    }

    /// <summary>True when <paramref name="existing" /> sorts before the new using: global, alias and static ones stay first.</summary>
    private static bool Precedes(UsingDirectiveSyntax existing)
    {
        if (existing.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword)) return true;
        if (existing.Alias is not null || existing.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) return false;
        var name = existing.Name?.ToString() ?? "";
        if (name.StartsWith("global::", StringComparison.Ordinal)) name = name.Substring("global::".Length);
        var system = name == "System" || name.StartsWith("System.", StringComparison.Ordinal);
        return system || string.CompareOrdinal(name, BuilderNamespace) < 0;
    }
}
