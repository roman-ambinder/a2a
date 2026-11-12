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
        using Microsoft.AspNetCore.Routing;

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

    // The startup check accepts any IAuthorizeData metadata, so WithMetadata of one is authorization.
    [Theory]
    [InlineData("app.MapA2A(\"/a2a\").WithMetadata(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute());")]
    [InlineData("app.MapA2A(\"/a2a\").WithMetadata(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute(\"agents\"));")]
    [InlineData("app.MapGroup(\"/agents\").WithMetadata(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute()).MapA2A(\"/a2a\");")]
    [InlineData("app.MapGroup(\"/a\").RequireAuthorization().MapGroup(\"/b\").MapA2A(\"/a2a\");")]
    [InlineData("var agents = app.MapGroup(\"/agents\");\nagents.RequireAuthorization();\nagents.MapA2A(\"/a2a\");")]
    [InlineData("var agents = app.MapGroup(\"/agents\").RequireAuthorization();\nagents.MapA2A(\"/a2a\");")]
    public async Task Authorization_metadata_is_accepted(string mapping) =>
        Assert.Empty(await Ids(Good.Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", mapping, StringComparison.Ordinal)));

    // A group is authorized only when the group itself is; AllowAnonymous metadata fails the startup check everywhere.
    [Theory]
    [InlineData("app.MapGroup(\"/agents\").MapA2A(\"/a2a\");")]
    [InlineData("app.MapGroup(\"/agents\").WithName(\"agents\").MapA2A(\"/a2a\");")]
    [InlineData("var agents = app.MapGroup(\"/agents\");\nagents.MapA2A(\"/a2a\");")]
    [InlineData("app.MapGroup(\"/agents\").AllowAnonymous().MapA2A(\"/a2a\");")]
    [InlineData("app.MapGroup(\"/agents\").RequireAuthorization().AllowAnonymous().MapA2A(\"/a2a\");")]
    [InlineData("app.MapA2A(\"/a2a\").RequireAuthorization().AllowAnonymous();")]
    [InlineData("app.MapA2A(\"/a2a\").AllowAnonymous();")]
    [InlineData("app.MapA2A(\"/a2a\").WithName(\"a2a\");")]
    [InlineData("app.MapA2A(\"/a2a\").WithMetadata(\"not authorization\");")]
    [InlineData("var a2a = app.MapA2A(\"/a2a\");\na2a.WithName(\"a2a\");")]
    public async Task Missing_authorization_is_A2A0002(string mapping) =>
        Assert.Equal(["A2A0002"], await Ids(Good.Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", mapping, StringComparison.Ordinal)));

    // ---- AllowAnonymous() next to a policy of the A2A builder ----

    /// <summary><see cref="Good" /> with <paramref name="policy" /> on the A2A builder and <paramref name="mapping" /> in place of its mapping.</summary>
    private static string WithBuilderPolicy(string policy, string mapping) =>
        Good.Replace("var app = builder.Build();", policy + "\nvar app = builder.Build();", StringComparison.Ordinal)
            .Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", mapping, StringComparison.Ordinal);

    private const string ApiKeys = "builder.Services.AddA2A(a => a.AddApiKeyAuthentication());";

    public static TheoryData<string, string> AnonymousEndpointsWithBuilderPolicy()
    {
        string[] policies =
        [
            ApiKeys,
            "builder.Services.AddA2A(a => a.AddBearerAuthentication());",
            "builder.Services.AddA2A(a => a.AddCardAuthentication(\"oidc\", A2A.SecurityScheme.Bearer(), \"Bearer\"));",
            "builder.Services.AddA2A(a => a.RequirePolicy(\"agents\"));"
        ];
        string[] mappings =
        [
            "app.MapA2A(\"/a2a\").AllowAnonymous();",
            "app.MapA2A(\"/a2a\").RequireAuthorization().AllowAnonymous();",
            "app.MapA2A(\"/a2a\").WithName(\"a2a\").AllowAnonymous();",
            "app.MapA2A(\"/a2a\").WithMetadata(new Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute());",
            "var a2a = app.MapA2A(\"/a2a\");\na2a.AllowAnonymous();",
            "var a2a = app.MapA2A(\"/a2a\").AllowAnonymous();\na2a.WithName(\"a2a\");",
            "app.MapGroup(\"/agents\").MapA2A(\"/a2a\").AllowAnonymous();"
        ];
        var data = new TheoryData<string, string>();
        foreach (var policy in policies)
        foreach (var mapping in mappings)
            data.Add(policy, mapping);
        return data;
    }

    // The builder's policy goes only on endpoints without authorization metadata: AllowAnonymous() on the endpoints keeps
    // it off, and the startup check refuses the IAllowAnonymous metadata, so the app does not start.
    [Theory]
    [MemberData(nameof(AnonymousEndpointsWithBuilderPolicy))]
    public async Task AllowAnonymous_on_the_endpoints_is_A2A0002_even_with_a_builder_policy(string policy, string mapping)
    {
        var d = Assert.Single(await Analyze(WithBuilderPolicy(policy, mapping)));
        Assert.Equal("A2A0002", d.Id);
        Assert.Contains("'MapA2A'", d.GetMessage(), StringComparison.Ordinal);
        Assert.Contains("AllowAnonymous()", d.GetMessage(), StringComparison.Ordinal);
    }

    // On an outer group, AllowAnonymous() is not on the endpoints the startup check reads (those MapA2A builds inside the
    // group), and the builder's policy goes on them: the app starts, and the A2A handler still refuses anonymous callers.
    [Theory]
    [InlineData("app.MapGroup(\"/agents\").AllowAnonymous().MapA2A(\"/a2a\");")]
    [InlineData("var agents = app.MapGroup(\"/agents\").AllowAnonymous();\nagents.MapA2A(\"/a2a\");")]
    [InlineData("var a2a = app.MapGroup(\"/agents\").AllowAnonymous().MapA2A(\"/a2a\");\na2a.WithName(\"a2a\");")]
    public async Task AllowAnonymous_on_an_outer_group_with_a_builder_policy_is_not_reported(string mapping) =>
        Assert.Empty(await Ids(WithBuilderPolicy(ApiKeys, mapping)));

    [Theory]
    [InlineData("builder.Services.Configure<A2AServerOptions>(o => o.AllowAnonymous = true);")]
    [InlineData("builder.Services.AddA2A(a => a.ConfigureForDevelopment());")]
    public async Task Anonymous_opt_in_lifts_AllowAnonymous_with_a_builder_policy(string optIn) =>
        Assert.Empty(await Ids(WithBuilderPolicy(ApiKeys + "\n" + optIn, "app.MapA2A(\"/a2a\").AllowAnonymous();")));

    [Fact]
    public async Task AllowAnonymous_with_a_builder_policy_shows_while_typing()
    {
        var program = WithBuilderPolicy(ApiKeys, "app.MapA2A(\"/a2a\").AllowAnonymous();");
        Assert.Equal(["A2A0002"], await LiveIds(program));
        Assert.Equal(["A2A0002"], await Ids(program)); // and once in the build, not twice
    }

    [Fact]
    public async Task AllowAnonymous_with_a_builder_policy_in_another_method_is_reported_in_the_build()
    {
        var program = Good.Replace("builder.Services.AddAuthorization();",
                "builder.Services.AddAuthorization();\nSetup.Register(builder.Services);", StringComparison.Ordinal)
            .Replace("app.MapA2A(\"/a2a\").RequireAuthorization();", "app.MapA2A(\"/a2a\").AllowAnonymous();",
                StringComparison.Ordinal);
        const string setup = "static class Setup { public static void Register(IServiceCollection s) => s.AddA2A(a => a.AddApiKeyAuthentication()); }";
        Assert.Empty(await LiveIds(program, Usings + setup));
        var d = Assert.Single(await Analyze(program, Usings + setup));
        Assert.Equal("A2A0002", d.Id);
        Assert.Contains("AllowAnonymous()", d.GetMessage(), StringComparison.Ordinal);
    }

    // RequireAuthorization() would not help: the IAllowAnonymous metadata stays and the startup check still refuses it.
    [Fact]
    public async Task No_RequireAuthorization_fix_for_AllowAnonymous_with_a_builder_policy()
    {
        var program = WithBuilderPolicy(ApiKeys, "app.MapA2A(\"/a2a\").AllowAnonymous();");
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Host", "Host",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.ConsoleApplication),
            metadataReferences: References.Value));
        var document = workspace.AddDocument(project.Id, "Program.cs", SourceText.From(Usings + program));
        var diagnostic = Assert.Single(await (await document.Project.GetCompilationAsync())!
            .WithAnalyzers([new A2ASetupAnalyzer()]).GetAnalyzerDiagnosticsAsync());
        var actions = new List<CodeAction>();
        await new A2ASetupCodeFixProvider().RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic,
            (a, _) => actions.Add(a), CancellationToken.None));
        Assert.Empty(actions);
    }

    // Code the analyzer cannot see into may add authorization (a helper that calls RequireAuthorization()): the startup
    // check decides, and no warning is reported.
    [Theory]
    [InlineData("Secure(app.MapA2A(\"/a2a\"));\napp.Run();\nstatic void Secure(RouteGroupBuilder group) => group.RequireAuthorization();")]
    [InlineData("var a2a = app.MapA2A(\"/a2a\");\nSecure(a2a);\napp.Run();\nstatic void Secure(RouteGroupBuilder group) => group.RequireAuthorization();")]
    [InlineData("app.MapA2A(\"/a2a\").Secure();\napp.Run();\nstatic class Security { public static RouteGroupBuilder Secure(this RouteGroupBuilder group) => group.RequireAuthorization(); }")]
    [InlineData("Group(app).MapA2A(\"/a2a\");\napp.Run();\nstatic RouteGroupBuilder Group(WebApplication app) => app.MapGroup(\"/agents\").RequireAuthorization();")]
    public async Task Authorization_in_code_the_analyzer_cannot_see_is_not_reported(string mapping) =>
        Assert.Empty(await Ids(Good.Replace("app.MapA2A(\"/a2a\").RequireAuthorization();\napp.Run();", mapping, StringComparison.Ordinal)));

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
    public void Fix_all_uses_the_batch_fixer() =>
        Assert.Same(WellKnownFixAllProviders.BatchFixer, new A2ASetupCodeFixProvider().GetFixAllProvider());

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

    // ---- The fix brings its own using ----

    /// <summary>
    ///     Applies the fix to <paramref name="program" />, compiled as a library with the given extra files (global usings,
    ///     as ImplicitUsings writes them), and returns the fixed text and the errors of the fixed project.
    /// </summary>
    private static async Task<(string Text, string[] Errors)> FixFile(string program, params string[] others)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Lib", "Lib",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: References.Value));
        foreach (var (other, i) in others.Select((o, i) => (o, i)))
            project = workspace.AddDocument(project.Id, $"Other{i}.cs", SourceText.From(other.ReplaceLineEndings("\n"))).Project;
        var document = workspace.AddDocument(project.Id, "Endpoints.cs", SourceText.From(program.ReplaceLineEndings("\n")));
        var before = (await document.Project.GetCompilationAsync())!;
        Assert.Empty(before.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var diagnostic = Assert.Single(await before.WithAnalyzers([new A2ASetupAnalyzer()]).GetAnalyzerDiagnosticsAsync());
        var provider = new A2ASetupCodeFixProvider();
        var actions = new List<CodeAction>();
        await provider.RegisterCodeFixesAsync(new CodeFixContext(document, diagnostic, (a, _) => actions.Add(a),
            CancellationToken.None));
        var operation = Assert.Single(await Assert.Single(actions).GetOperationsAsync(CancellationToken.None));
        document = ((ApplyChangesOperation)operation).ChangedSolution.GetDocument(document.Id)!;
        var after = (await document.Project.GetCompilationAsync())!;
        var errors = after.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString())
            .ToArray();
        return ((await document.GetTextAsync()).ToString(), errors);
    }

    private const string RoutingOnly = """
        using A2A.Server.AspNetCore;
        using Microsoft.AspNetCore.Routing;

        namespace Lib;

        public static class Endpoints
        {
            public static void Map(IEndpointRouteBuilder endpoints)
            {
                endpoints.MapA2A("/a2a");
            }
        }
        """;

    [Fact]
    public async Task Fix_adds_the_using_RequireAuthorization_needs()
    {
        var (text, errors) = await FixFile(RoutingOnly);
        Assert.Empty(errors);
        Assert.Contains("endpoints.MapA2A(\"/a2a\").RequireAuthorization();", text, StringComparison.Ordinal);
        Assert.StartsWith("using A2A.Server.AspNetCore;\nusing Microsoft.AspNetCore.Builder;\nusing Microsoft.AspNetCore.Routing;\n",
            text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_adds_no_using_that_a_global_using_already_brings()
    {
        // what ImplicitUsings writes for a web project (GlobalUsings.g.cs), in part
        const string globalUsings = """
            global using global::System;
            global using global::Microsoft.AspNetCore.Builder;
            global using global::Microsoft.AspNetCore.Routing;
            """;
        var program = RoutingOnly.Replace("using Microsoft.AspNetCore.Routing;\n", "", StringComparison.Ordinal);
        var (text, errors) = await FixFile(program, globalUsings);
        Assert.Empty(errors);
        Assert.Contains("endpoints.MapA2A(\"/a2a\").RequireAuthorization();", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.AspNetCore.Builder", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fix_adds_no_using_that_the_namespace_already_has()
    {
        var program = RoutingOnly.Replace("namespace Lib;", "namespace Lib\n{\nusing Microsoft.AspNetCore.Builder;\n", StringComparison.Ordinal) + "\n}";
        var (text, errors) = await FixFile(program);
        Assert.Empty(errors);
        Assert.Equal(1, text.Split("Microsoft.AspNetCore.Builder").Length - 1);
    }

    [Fact]
    public async Task Fix_all_adds_the_using_once()
    {
        var program = RoutingOnly.Replace("endpoints.MapA2A(\"/a2a\");", "endpoints.MapA2A(\"/a2a\");\n        endpoints.MapA2AJsonRpc(\"/rpc\");", StringComparison.Ordinal)
            .ReplaceLineEndings("\n");
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Lib", "Lib",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: References.Value));
        var document = workspace.AddDocument(project.Id, "Endpoints.cs", SourceText.From(program));
        var diagnostics = await (await document.Project.GetCompilationAsync())!.WithAnalyzers([new A2ASetupAnalyzer()])
            .GetAnalyzerDiagnosticsAsync();
        Assert.Equal(2, diagnostics.Length);
        var provider = new A2ASetupCodeFixProvider();
        var context = new FixAllContext(document, provider, FixAllScope.Document, "A2A0002.RequireAuthorization", ["A2A0002"],
            new FixedDiagnostics(diagnostics), CancellationToken.None);
        var action = (await provider.GetFixAllProvider().GetFixAsync(context))!;
        var operation = Assert.Single(await action.GetOperationsAsync(CancellationToken.None));
        document = ((ApplyChangesOperation)operation).ChangedSolution.GetDocument(document.Id)!;
        var text = (await document.GetTextAsync()).ToString();
        Assert.Equal(1, text.Split("using Microsoft.AspNetCore.Builder;").Length - 1);
        Assert.Empty((await document.Project.GetCompilationAsync())!.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    private sealed class FixedDiagnostics(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);

        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>([]);

        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken) =>
            Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
