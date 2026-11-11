using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using A2A.Client;
using A2A.Security.Http;

namespace A2A.Cli;

/// <summary>Runs one command line and turns every failure into an exit code and one error message.</summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args, CliEnvironment env, CancellationToken cancellationToken)
    {
        var output = new Output(env, args.TakeWhile(a => a != "--").Contains("--json"));
        foreach (var variable in new[]
                 {
                     ConnectionSettings.TokenVariable, ConnectionSettings.ApiKeyVariable,
                     ConnectionSettings.ClientSecretVariable
                 })
            output.AddSecret(env.GetEnvironmentVariable(variable));
        try
        {
            return await DispatchAsync(args, env, output, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Every failure becomes an exit code; a stack trace is never printed.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return Fail(ex, output, cancellationToken);
        }
    }

    private static async Task<int> DispatchAsync(string[] args, CliEnvironment env, Output output,
        CancellationToken cancellationToken)
    {
        if (args.Length == 0) throw CliException.Usage("No command given. Run 'a2a --help'.");
        var first = args[0];
        if (first is "--help" or "-h" or "help")
        {
            if (args.Length > 1 && args[1] == "keys")
            {
                output.Line(Commands.KeysCommand.Help);
                return ExitCodes.Success;
            }

            var topic = args.Length > 1 ? CommandTable.Find(args[1]) : null;
            output.Line(topic is null ? CommandTable.GeneralHelp() : CommandTable.CommandHelp(topic));
            return ExitCodes.Success;
        }

        if (first == "--version")
        {
            output.Line(CommandTable.Version);
            return ExitCodes.Success;
        }

        // Offline: no agent, no connection options.
        if (first == "keys") return Commands.KeysCommand.Run(args[1..], env, output);

        var spec = CommandTable.Find(first) ??
                   throw CliException.Usage(
                       $"Unknown command '{(first.StartsWith('-') ? first.Split('=')[0] : first)}'. Run 'a2a --help'.");
        var rest = args[1..];
        if (ArgumentParser.HasHelp(rest))
        {
            output.Line(CommandTable.CommandHelp(spec));
            return ExitCodes.Success;
        }

        var parsed = ArgumentParser.Parse(rest, spec);
        var settings = ConnectionSettings.From(parsed, env.GetEnvironmentVariable);
        output.Json = settings.Json;
        foreach (var secret in settings.Secrets) output.AddSecret(secret);

        using var connection = await AgentConnection
            .OpenAsync(settings, env, output, !spec.NeedsInterface, cancellationToken).ConfigureAwait(false);
        return await spec.Run(new CommandContext(parsed, settings, env, output, connection), cancellationToken)
            .ConfigureAwait(false);
    }

    private static int Fail(Exception ex, Output output, CancellationToken cancellationToken)
    {
        var (exitCode, a2aCode, message) = Classify(ex, cancellationToken);
        var clean = output.One(ErrorText.StripUrlSecrets(message), 500);
        if (output.Json)
            output.ErrorLine(Render.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartObject("error");
                w.WriteNumber("exitCode", exitCode);
                if (a2aCode is null) w.WriteNull("code");
                else w.WriteString("code", a2aCode);
                w.WriteString("message", clean);
                w.WriteEndObject();
                w.WriteEndObject();
            }));
        else
            output.ErrorLine($"Error: {clean}");
        return exitCode;
    }

    internal static (int ExitCode, string? A2ACode, string Message) Classify(Exception ex,
        CancellationToken cancellationToken)
    {
        switch (ex)
        {
            case CliException cli:
                return (cli.ExitCode, null, cli.Message);
            case OperationCanceledException:
                return cancellationToken.IsCancellationRequested
                    ? (ExitCodes.Cancelled, null, "Cancelled.")
                    : (ExitCodes.Network, null, "The operation timed out.");
            case AgentCardVerificationException or AgentCardPinException:
                return (ExitCodes.CardVerification, A2AErrors.Reason(((A2AException)ex).Code), ex.Message);
            case A2AException a2a:
                var status = a2a.HttpStatus;
                var code = A2AErrors.Reason(a2a.Code);
                if (a2a.Code is A2AErrorCode.Unauthenticated or A2AErrorCode.PermissionDenied || status is 401 or 403)
                    return (ExitCodes.Auth, code, a2a.Message);
                if (a2a.Code == A2AErrorCode.TaskNotFound || status == 404)
                    return (ExitCodes.NotFound, code, a2a.Message);
                return (ExitCodes.Protocol, code, a2a.Message);
            case InvalidDataException or BlockedAddressException:
                return (ExitCodes.Integrity, null, ex.Message);
            case HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }:
                return (ExitCodes.Auth, null, ex.Message);
            case HttpRequestException { StatusCode: HttpStatusCode.NotFound }:
                return (ExitCodes.NotFound, null, ex.Message);
            case HttpRequestException or TimeoutException or SocketException:
                return (ExitCodes.Network, null, ex.Message);
            case JsonException:
                return (ExitCodes.Protocol, null, "The agent sent a response that could not be read.");
            default:
                return (ExitCodes.Error, null,
                    ex is IOException or UnauthorizedAccessException ? ex.Message : "An unexpected error occurred.");
        }
    }
}