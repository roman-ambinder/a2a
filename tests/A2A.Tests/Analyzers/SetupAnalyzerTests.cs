using System.Collections.Immutable;
using A2A.Analyzers;
using A2A.Analyzers.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace A2A.Tests.Analyzers;

/// <summary>A2A0001-A2A0003 on small host programs, compiled in memory against the real ASP.NET Core and A2A assemblies.</summary>
public class SetupAnalyzerTests
{
    private const string Usings = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using A2A.Server;
        using A2A.Server.AspNetCore;

        """;

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray());

    private static Compilation Compile(string program) =>
        CSharpCompilation.Create("Host", [CSharpSyntaxTree.ParseText(Usings + program, path: "Program.cs")], References.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string program)
    {
        var compilation = Compile(program);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors)); // the test programs themselves must compile
        return await compilation.WithAnalyzers([new A2ASetupAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    private static async Task<string[]> Ids(string program) =>
        (await Analyze(program)).Select(d => d.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    private const string Good = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => a.AddAgentExecutor((ctx, u, ct) => u.CompleteAsync(cancellationToken: ct).AsTask()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        app.Run();
        """;

    [Fact]
    public async Task Correct_host_has_no_diagnostics() => Assert.Empty(await Ids(Good));

    [Fact]
    public async Task Missing_UseA2A_is_A2A0001() =>
        Assert.Equal(["A2A0001"], await Ids(Good.Replace("app.UseA2A();\n", "", StringComparison.Ordinal)));

    [Fact]
    public async Task Turning_rate_limiting_off_lifts_A2A0001() =>
        Assert.Empty(await Ids(Good.Replace("app.UseA2A();\n", "builder.Services.AddA2ARateLimiting(o => o.Enabled = false);\n",
            StringComparison.Ordinal)));

    [Fact]
    public async Task Mapping_without_authorization_is_A2A0002()
    {
        var diagnostics = await Analyze(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal));
        var d = Assert.Single(diagnostics);
        Assert.Equal("A2A0002", d.Id);
        Assert.Contains("'MapA2A'", d.GetMessage(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("var a2a = app.MapA2A(\"/a2a\");\nif (!app.Environment.IsDevelopment()) a2a.RequireAuthorization();")]
    [InlineData("app.MapA2A(\"/a2a\", o => o.AuthorizationPolicy = \"agents\");")]
    [InlineData("app.MapA2A(\"/a2a\").WithName(\"a2a\").RequireAuthorization();")]
    [InlineData("app.MapGroup(\"/api\").RequireAuthorization().MapA2A(\"/a2a\");")]
    public async Task Visible_authorization_is_accepted(string mapping) =>
        Assert.Empty(await Ids(Good.Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", mapping, StringComparison.Ordinal)));

    [Theory]
    [InlineData("builder.Services.Configure<A2AServerOptions>(o => o.AllowAnonymous = true);")]
    [InlineData("builder.Services.AddA2A(a => a.UseDevelopmentDefaults());")]
    [InlineData("builder.Services.AddAuthorization(o => o.FallbackPolicy = null);")]
    public async Task Anonymous_opt_in_lifts_A2A0002(string optIn) =>
        Assert.Empty(await Ids(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("var app = builder.Build();", optIn + "\nvar app = builder.Build();", StringComparison.Ordinal)));

    [Fact]
    public async Task UseA2A_after_UseAuthorization_is_A2A0003()
    {
        var program = Good.Replace("app.UseA2A();\napp.UseAuthorization();", "app.UseAuthorization();\napp.UseA2A();",
            StringComparison.Ordinal);
        var d = Assert.Single(await Analyze(program));
        Assert.Equal("A2A0003", d.Id);
        Assert.Contains("before UseAuthorization()", d.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UseA2A_before_UseAuthentication_is_A2A0003()
    {
        var program = Good.Replace("app.UseAuthentication();\napp.UseA2A();", "app.UseA2A();\napp.UseAuthentication();",
            StringComparison.Ordinal);
        var d = Assert.Single(await Analyze(program));
        Assert.Equal("A2A0003", d.Id);
        Assert.Contains("after UseAuthentication()", d.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Code_with_no_A2A_call_is_ignored() =>
        Assert.Empty(await Ids("var app = WebApplication.CreateBuilder(args).Build();\napp.Run();"));

    // ---- Code fixes ----

    private static async Task<string> Fix(string program)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Host", "Host",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication),
            metadataReferences: References.Value));
        var document = workspace.AddDocument(project.Id, "Program.cs", SourceText.From(Usings + program));
        var provider = new A2ASetupCodeFixProvider();
        for (var round = 0; round < 3; round++)
        {
            var compilation = (await document.Project.GetCompilationAsync())!;
            var diagnostic = (await compilation.WithAnalyzers([new A2ASetupAnalyzer()]).GetAnalyzerDiagnosticsAsync())
                .FirstOrDefault(d => provider.FixableDiagnosticIds.Contains(d.Id));
            if (diagnostic is null) break;
            var actions = new List<CodeAction>();
            await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a),
                CancellationToken.None));
            var operation = Assert.Single(await Assert.Single(actions).GetOperationsAsync(CancellationToken.None));
            var applied = ((ApplyChangesOperation)operation).ChangedSolution;
            document = applied.GetDocument(document.Id)!;
        }

        return (await document.GetTextAsync()).ToString()[Usings.Length..];
    }

    [Fact]
    public async Task Fix_adds_RequireAuthorization()
    {
        var fixedProgram = await Fix(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal));
        Assert.Contains("app.MapA2A(\"/a2a\").RequireAuthorization();", fixedProgram, StringComparison.Ordinal);
        Assert.Empty(await Ids(fixedProgram));
    }

    [Fact]
    public async Task Fix_adds_UseA2A_before_UseAuthorization()
    {
        var fixedProgram = await Fix(Good.Replace("app.UseA2A();\n", "", StringComparison.Ordinal));
        Assert.Contains("app.UseAuthentication();\napp.UseA2A();\napp.UseAuthorization();", fixedProgram.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Empty(await Ids(fixedProgram));
    }

    [Fact]
    public async Task Fix_inside_a_method_body()
    {
        const string program = """
            internal static class Host
            {
                public static void Configure(WebApplication app)
                {
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.MapA2A("/a2a").RequireAuthorization();
                }

                public static void Main() { }
            }
            """;
        var fixedProgram = (await Fix(program)).ReplaceLineEndings("\n");
        Assert.Contains("        app.UseA2A();\n        app.UseAuthorization();", fixedProgram, StringComparison.Ordinal);
    }
}
