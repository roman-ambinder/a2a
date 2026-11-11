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
///     (A2A0002), and adds <c>app.UseA2A();</c> before <c>UseAuthorization()</c> (A2A0001).
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(A2ASetupCodeFixProvider))]
[Shared]
internal sealed class A2ASetupCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds { get; } =
        ImmutableArray.Create(MissingUseA2AId, NoAuthorizationId);

    // The ids of A2ASetupAnalyzer's rules (A2A.Analyzers). Strings, not its descriptors: the two assemblies share no
    // internals.
    private const string MissingUseA2AId = "A2A0001";
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
            if (diagnostic.Id == NoAuthorizationId)
                context.RegisterCodeFix(CodeAction.Create("Add .RequireAuthorization()",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(RequireAuthorization(root, map))),
                    "A2A0002.RequireAuthorization"), diagnostic);
            else if (AddUseA2A(root, map) is { } fixedRoot)
                context.RegisterCodeFix(CodeAction.Create("Add app.UseA2A()",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(fixedRoot)), "A2A0001.UseA2A"), diagnostic);
        }
    }

    private static SyntaxNode RequireAuthorization(SyntaxNode root, InvocationExpressionSyntax map)
    {
        var call = InvocationExpression(MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression,
                map.WithoutTrailingTrivia(), IdentifierName("RequireAuthorization")))
            .WithTrailingTrivia(map.GetTrailingTrivia());
        return root.ReplaceNode(map, call);
    }

    /// <summary>
    ///     Inserts <c>receiver.UseA2A();</c> before the statement that calls <c>UseAuthorization()</c> in the same block or
    ///     top-level program, else before the statement that maps the endpoints.
    /// </summary>
    private static SyntaxNode? AddUseA2A(SyntaxNode root, InvocationExpressionSyntax map)
    {
        if (map.Expression is not MemberAccessExpressionSyntax { Expression: var receiver }) return null;
        if (map.FirstAncestorOrSelf<StatementSyntax>() is not { } mapStatement) return null;

        IReadOnlyList<SyntaxNode> siblings;
        SyntaxNode anchor;
        if (mapStatement.Parent is GlobalStatementSyntax global && global.Parent is CompilationUnitSyntax unit)
        {
            siblings = unit.Members.OfType<GlobalStatementSyntax>().ToList();
            anchor = global;
        }
        else if (mapStatement.Parent is BlockSyntax block)
        {
            siblings = block.Statements;
            anchor = mapStatement;
        }
        else
        {
            return null;
        }

        var before = siblings.FirstOrDefault(s => s.SpanStart < anchor.SpanStart && s.DescendantNodes()
            .OfType<InvocationExpressionSyntax>().Any(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "UseAuthorization"
            })) ?? anchor;

        StatementSyntax use = ExpressionStatement(InvocationExpression(MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, receiver.WithoutTrivia(), IdentifierName("UseA2A"))))
            .WithLeadingTrivia(before.GetLeadingTrivia())
            .WithTrailingTrivia(EndOfLine(root.ToFullString().Contains("\r\n") ? "\r\n" : "\n"));
        SyntaxNode inserted = before is GlobalStatementSyntax ? GlobalStatement(use) : use;
        return root.InsertNodesBefore(before, [inserted]);
    }
}
