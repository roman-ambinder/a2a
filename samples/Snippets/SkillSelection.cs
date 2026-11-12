using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using Selection = A2A.Skills.SkillSelection;

namespace Snippets.SkillSelection;

#region Executor
// Branch on the skill the client chose. Null means no usable selection.
internal sealed class TranslatorAgent : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var text = context.RequestedSkillId switch
        {
            "translate" => "Translating...",
            "summarize" => "Summarizing...",
            _ => "Tell me what to do: translate or summarize.",
        };
        return updater.CompleteAsync(text, cancellationToken).AsTask();
    }
}
#endregion

internal static class Snippets
{
    public static void DeclareOnCard(IServiceCollection services)
    {
        #region Card
        services.AddA2A(a => a
            .AddAgentExecutor<TranslatorAgent>()
            .AddAgentCard(new AgentCard
            {
                Name = "Translator",
                Description = "Translates and summarizes text.",
                Version = "1.0.0",
                Skills =
                [
                    new AgentSkill { Id = "translate", Name = "Translate", Description = "Translate text.", Tags = ["text"] },
                    new AgentSkill { Id = "summarize", Name = "Summarize", Description = "Summarize text.", Tags = ["text"] },
                ],
                Capabilities = new AgentCapabilities
                {
                    // true: refuse a skill that is not on the card.
                    Extensions = [Selection.CreateExtension(rejectUnknownSkills: true)],
                },
            }));
        #endregion
    }

    public static async Task SelectAsync(A2AClient a2a)
    {
        #region Client
        var response = await a2a.SendMessageAsync(Message.FromUser("Bonjour tout le monde"),
            options: new A2ARequestOptions { Skill = "translate" });
        #endregion

        _ = response;
    }
}
