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
                _ => Task.FromResult(context.Document.WithSyntaxRoot(RequireAuthorization(root, map))),
                "A2A0002.RequireAuthorization"), diagnostic);
        }
    }

    private static SyntaxNode RequireAuthorization(SyntaxNode root, InvocationExpressionSyntax map)
    {
        var call = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                map.WithoutTrailingTrivia(), IdentifierName("RequireAuthorization")))
            .WithTrailingTrivia(map.GetTrailingTrivia());
        return root.ReplaceNode(map, call);
    }
}
