using A2A.Client;
using A2A.Server;
using A2A.Server.Authorization;

namespace Snippets.SkillAuthorization;

#region Handler
// Callers with the "premium" role may use "translate". Everyone may use "summarize".
internal sealed class SkillHandler : A2AAuthorizationHandler
{
    public override ValueTask<bool> AuthorizeSkillAsync(ServerCallContext call, string skillId, CancellationToken cancellationToken)
    {
        var allowed = skillId switch
        {
            "summarize" => true,
            "translate" => call.User?.IsInRole("premium") == true,
            _ => false, // deny what you do not know
        };
        return ValueTask.FromResult(allowed);
    }
}
#endregion

#region Executor
// The server already checked the skill the message named. A message that named none (WhenNamed) picks its own skill here.
internal sealed class RoutingAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // Branch on AuthorizedSkillId, not RequestedSkillId: it is the result of the check.
        var skill = context.AuthorizedSkillId;
        if (skill is null)
        {
            // No skill was named, so nothing was checked. Pick one, then ask.
            skill = "translate";
            if (!await context.IsSkillAllowedAsync(skill, cancellationToken))
            {
                await updater.FailAsync("You may not use this skill.", cancellationToken);
                return;
            }
        }
        await updater.CompleteAsync($"Running {skill}.", cancellationToken);
    }
}
#endregion

internal static class Snippets
{
    public static void Register(IServiceCollection services)
    {
        #region Register
        services.AddA2AServer(o =>
        {
            // Off (default), WhenNamed, or Required.
            o.SkillAuthorization.Mode = SkillAuthorizationMode.Required;
            // Also list only the allowed skills on the extended card.
            o.SkillAuthorization.FilterExtendedCard = true;
        })
        .AddAgentExecutor<RoutingAgent>()
        .AddAuthorizationHandler<SkillHandler>();
        #endregion
    }

    public static async Task CallAsync(A2AClient a2a)
    {
        #region Client
        // The same call as skill selection. The server reads metadata["skillId"].
        var response = await a2a.SendMessageAsync(Message.FromUser("Bonjour tout le monde"),
            options: new A2ARequestOptions { Skill = "translate" });
        #endregion

        _ = response;
    }
}
