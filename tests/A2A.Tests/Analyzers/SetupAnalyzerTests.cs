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

/// <summary>A2A0002 on small host programs, compiled in memory against the real ASP.NET Core and A2A assemblies.</summary>
public class SetupAnalyzerTests
{
    // Raw strings take the line endings of this file, which is CRLF in a Windows checkout. The tests edit the programs
    // with "\n" replacements, so both are normalized to "\n".
    private static readonly string Usings = """
        using Microsoft.AspNetCore.Builder;
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Hosting;
        using A2A.Server;
        using A2A.Server.AspNetCore;

        """.ReplaceLineEndings("\n");

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToImmutableArray());

    private static Compilation Compile(params string[] files) =>
        CSharpCompilation.Create("Host",
            files.Select((f, i) => CSharpSyntaxTree.ParseText(i == 0 ? Usings + f : f, path: i == 0 ? "Program.cs" : $"File{i}.cs")),
            References.Value,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

    private static async Task<ImmutableArray<Diagnostic>> Analyze(params string[] files)
    {
        var compilation = Compile(files);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(errors.Count == 0, string.Join("\n", errors)); // the test programs themselves must compile
        return await compilation.WithAnalyzers([new A2ASetupAnalyzer()]).GetAnalyzerDiagnosticsAsync();
    }

    private static async Task<string[]> Ids(params string[] files) =>
        (await Analyze(files)).Select(d => d.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();

    private static readonly string Good = """
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddAuthentication();
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => a.AddAgentExecutor((ctx, u, ct) => u.CompleteAsync(cancellationToken: ct).AsTask()));
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a").RequireAuthorization();
        app.Run();
        """.ReplaceLineEndings("\n");

    [Fact]
    public async Task Correct_host_has_no_diagnostics() => Assert.Empty(await Ids(Good));

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
    [InlineData("app.MapA2A(\"/a2a\").RequireAuthorization(\"agents\");")]
    [InlineData("app.MapA2A(\"/a2a\").WithName(\"a2a\").RequireAuthorization();")]
    [InlineData("app.MapGroup(\"/api\").RequireAuthorization().MapA2A(\"/a2a\");")]
    public async Task Visible_authorization_is_accepted(string mapping) =>
        Assert.Empty(await Ids(Good.Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", mapping, StringComparison.Ordinal)));

    [Theory]
    [InlineData("builder.Services.Configure<A2AServerOptions>(o => o.AllowAnonymous = true);")]
    [InlineData("builder.Services.AddA2A(a => a.ConfigureForDevelopment());")]
    [InlineData("builder.Services.AddA2A(a => a.RequirePolicy(\"agents\"));")]
    [InlineData("builder.Services.AddA2A().Configure(o => o.AllowAnonymous = true);")]
    [InlineData("builder.Services.AddAuthorization(o => o.FallbackPolicy = null);")]
    [InlineData("builder.Services.AddA2A(a => a.AddApiKeyAuthentication());")]
    [InlineData("builder.Services.AddA2A(a => a.AddBearerAuthentication());")]
    [InlineData("builder.Services.AddA2A(a => a.AddCardAuthentication(\"oidc\", A2A.SecurityScheme.Bearer(), \"Bearer\"));")]
    public async Task Anonymous_opt_in_lifts_A2A0002(string optIn) =>
        Assert.Empty(await Ids(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("var app = builder.Build();", optIn + "\nvar app = builder.Build();", StringComparison.Ordinal)));

    [Fact]
    public async Task Code_with_no_A2A_call_is_ignored() =>
        Assert.Empty(await Ids("var app = WebApplication.CreateBuilder(args).Build();\napp.Run();"));

    // ---- Values, and what the IDE shows while you type ----

    [Fact]
    public async Task AllowAnonymous_false_does_not_lift_A2A0002() =>
        Assert.Equal(["A2A0002"], await Ids(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("var app = builder.Build();",
                "builder.Services.Configure<A2AServerOptions>(o => o.AllowAnonymous = false);\nvar app = builder.Build();",
                StringComparison.Ordinal)));

    [Fact]
    public async Task A_value_that_is_not_constant_still_lifts_A2A0002() =>
        Assert.Empty(await Ids(Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("var app = builder.Build();",
                "builder.Services.Configure<A2AServerOptions>(o => o.AllowAnonymous = builder.Environment.IsDevelopment());\nvar app = builder.Build();",
                StringComparison.Ordinal)));

    /// <summary>What the IDE reports for one open document while you type: no compilation-end diagnostics.</summary>
    private static async Task<string[]> LiveIds(params string[] files)
    {
        var compilation = Compile(files);
        var withAnalyzers = compilation.WithAnalyzers([new A2ASetupAnalyzer()]);
        var tree = compilation.SyntaxTrees.First();
        var diagnostics = await withAnalyzers.GetAnalyzerSemanticDiagnosticsAsync(compilation.GetSemanticModel(tree),
            null, CancellationToken.None);
        return diagnostics.Select(d => d.Id).OrderBy(i => i, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task Missing_authorization_shows_while_typing()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal);
        Assert.Equal(["A2A0002"], await LiveIds(program));
        Assert.Equal(["A2A0002"], await Ids(program)); // and once in the build, not twice
    }

    [Fact]
    public async Task UseA2A_rules_no_longer_exist() =>
        Assert.DoesNotContain(new A2ASetupAnalyzer().SupportedDiagnostics, d => d.Id is "A2A0001" or "A2A0003");

    [Fact]
    public async Task Live_diagnostics_are_not_compilation_end()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal);
        var d = Assert.Single(await Analyze(program));
        Assert.DoesNotContain(WellKnownDiagnosticTags.CompilationEnd, d.Descriptor.CustomTags);
        Assert.Contains("'MapA2A'", d.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handing_the_app_to_your_own_method_waits_for_the_build()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("app.Run();", "Configure(app);\napp.Run();\nstatic void Configure(WebApplication app) { }",
                StringComparison.Ordinal);
        Assert.Empty(await LiveIds(program));
        Assert.Equal(["A2A0002"], await Ids(program));
    }

    [Fact]
    public async Task Your_own_methods_that_take_no_hosting_object_keep_it_live()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("app.Run();", "new Helper().Log(\"ready\");\napp.Run();\nsealed class Helper { public void Log(string text) { } }",
                StringComparison.Ordinal);
        Assert.Equal(["A2A0002"], await LiveIds(program));
    }

    [Fact]
    public async Task Handing_a_type_that_implements_a_hosting_interface_waits_for_the_build()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal)
            .Replace("app.Run();", "Register(new ServiceCollection());\napp.Run();\nstatic void Register(ServiceCollection services) { }",
                StringComparison.Ordinal);
        Assert.Empty(await LiveIds(program));
        Assert.Equal(["A2A0002"], await Ids(program));
    }

    private const string AnonymousOptions = """
        sealed class AnonymousA2A : Microsoft.Extensions.Options.IConfigureOptions<A2AServerOptions>
        {
            public void Configure(A2AServerOptions options) => options.AllowAnonymous = true;
        }
        """;

    [Fact]
    public async Task An_opt_in_in_another_file_is_not_reported_as_missing_while_typing()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal);
        Assert.Empty(await LiveIds(program, Usings + AnonymousOptions));
        Assert.Empty(await Ids(program, Usings + AnonymousOptions));
    }

    [Fact]
    public async Task An_opt_in_in_a_type_below_the_statements_is_not_reported_as_missing_while_typing()
    {
        var program = Good.Replace(".RequireAuthorization()", "", StringComparison.Ordinal) + "\n" + AnonymousOptions;
        Assert.Empty(await LiveIds(program));
        Assert.Empty(await Ids(program));
    }

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
    public async Task Fix_inside_a_method_body()
    {
        const string program = """
            internal static class Host
            {
                public static void Configure(WebApplication app)
                {
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.MapA2A("/a2a");
                }

                public static void Main() { }
            }
            """;
        var fixedProgram = (await Fix(program)).ReplaceLineEndings("\n");
        Assert.Contains("        app.MapA2A(\"/a2a\").RequireAuthorization();", fixedProgram, StringComparison.Ordinal);
    }
}
