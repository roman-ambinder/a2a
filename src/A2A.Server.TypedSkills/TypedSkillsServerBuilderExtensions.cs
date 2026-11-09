using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace A2A.Server.TypedSkills;

/// <summary>Typed skills for the A2A server builder.</summary>
public static class TypedSkillsServerBuilderExtensions
{
    /// <summary>
    /// Adds typed skills: registers a router as the agent executor, a filter that refuses bad input, and a step that writes the
    /// skills and their schemas to the Agent Card (also when <c>AddAgentCard</c> comes later). Use
    /// <see cref="TypedSkillSet.UseFallback{T}"/> for a plain executor, instead of <c>AddAgentExecutor</c>, which would replace the router.
    /// </summary>
    /// <param name="builder">The server builder.</param>
    /// <param name="configure">Adds the skills. It runs now.</param>
    /// <returns>The same builder.</returns>
    /// <exception cref="InvalidOperationException">The method was called before, or <paramref name="configure"/> added no skill.</exception>
    /// <remarks>One set per host. A host with an <c>AgentExecutorResolver</c> (one agent per tenant) is not covered.</remarks>
    [global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
    public static A2AServerBuilder AddTypedSkills(this A2AServerBuilder builder, Action<TypedSkillSet> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        if (builder.Services.Any(d => d.ServiceType == typeof(TypedSkillFilter)))
            throw new InvalidOperationException("AddTypedSkills was already called. One set serves the host: add every skill in one call.");

        var set = new TypedSkillSet();
        configure(set);
        if (set.Count == 0) throw new InvalidOperationException("Map at least one typed skill.");

        builder.Services.AddSingleton(new TypedSkillFilter(set));
        builder.AddOperationFilter<TypedSkillFilter>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(services => new TypedSkillRouter(set, services)));
        // After every Configure, so the card is final whichever came first.
        builder.Services.PostConfigure<A2AServerOptions>(options => set.ApplyTo(options.AgentCard));
        return builder;
    }
}
