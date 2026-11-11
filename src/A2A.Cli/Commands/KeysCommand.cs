namespace A2A.Cli.Commands;

/// <summary>
///     <c>a2a keys new</c> makes an API key and its hash; <c>a2a keys hash</c> hashes a key read from standard input. Both
///     work offline: no agent is called.
/// </summary>
internal static class KeysCommand
{
    public const string Summary = "Make an API key and its hash for an agent (offline, no --agent).";

    public const string Help = """
        Usage: a2a keys new [--json]
               a2a keys hash [--json] < key-file

        new   Makes an API key (a2a_ and 43 random characters) and its hash.
              Give the key to the caller. Store only the hash on the agent:
              AddApiKeyAuthentication(o => o.Add("caller", "<hash>")), or the configuration section
              A2A:ApiKeys:Keys (CallerId and Hash).
        hash  Prints the hash of a key read from standard input, never from an option, so it stays out of
              process lists and shell history.
        """;

    public static int Run(string[] args, CliEnvironment env, Output output)
    {
        var json = args.Contains("--json");
        var rest = args.Where(a => a != "--json").ToArray();
        if (rest is ["--help"] or ["-h"] or [])
        {
            output.Line(Help);
            return rest.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        A2AApiKey? created = null;
        string hash;
        switch (rest)
        {
            case ["new"]:
                created = A2AApiKey.Create();
                hash = created.Hash;
                break;
            case ["hash"]:
                var key = env.In.ReadToEnd().Trim();
                if (!A2AApiKey.IsWellFormed(key))
                    throw CliException.Usage("Standard input is not an A2A API key (a2a_ and 43 base64url characters).");
                hash = A2AApiKey.ComputeHash(key);
                break;
            default:
                throw CliException.Usage($"Unknown 'keys' command '{string.Join(' ', rest)}'. Run 'a2a keys --help'.");
        }

        if (json)
        {
            output.Line(Render.Json(w =>
            {
                w.WriteStartObject();
                if (created is not null) w.WriteString("key", created.Key);
                w.WriteString("hash", hash);
                w.WriteEndObject();
            }));
            return ExitCodes.Success;
        }

        if (created is not null)
        {
            output.Line($"key:  {created.Key}");
            output.Line($"hash: {hash}");
            output.Line("");
            output.Line("Give the key to the caller now; it is not shown again. Store only the hash on the agent.");
        }
        else
        {
            output.Line(hash);
        }

        return ExitCodes.Success;
    }
}
