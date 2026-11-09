namespace A2A.Server.Authorization;

/// <summary>When the server checks the skill a message names (<see cref="SkillAuthorizationOptions.Mode"/>).</summary>
public enum SkillAuthorizationMode
{
    /// <summary>No skill check. A message runs whatever skill it names. The default.</summary>
    Off,

    /// <summary>
    /// A message that names a skill is checked. A message that names none runs: the agent then picks the skill itself, and must call
    /// <see cref="AgentRequestContext.IsSkillAllowedAsync"/> before it uses one.
    /// </summary>
    WhenNamed,

    /// <summary>Every <c>SendMessage</c> and <c>SendStreamingMessage</c> must name a skill, and that skill is checked.</summary>
    Required,
}

/// <summary>
/// Settings of per-skill authorization (<see cref="A2AAuthorizationHandler.AuthorizeSkillAsync"/>). Host configuration: a
/// security rule must not depend on what the agent card says.
/// </summary>
public sealed class SkillAuthorizationOptions
{
    /// <summary>When the server checks the skill a message names. Default <see cref="SkillAuthorizationMode.Off"/>.</summary>
    public SkillAuthorizationMode Mode { get; set; }

    /// <summary>
    /// True to remove from the extended agent card every skill the handler denies. Works in any <see cref="Mode"/>. The public
    /// card is never filtered. Default false.
    /// </summary>
    public bool FilterExtendedCard { get; set; }
}
