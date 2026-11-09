namespace A2A.Cli.Commands;

/// <summary><c>a2a card</c>: resolve and verify the Agent Card.</summary>
internal static class CardCommand
{
    public static Task<int> RunAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var card = c.Connection.Card;
        if (c.Out.Json) c.Out.Line(Render.CardJson(card));
        else foreach (var line in Render.CardText(card, c.Out)) c.Out.Line(line);
        return Task.FromResult(ExitCodes.Success);
    }
}
