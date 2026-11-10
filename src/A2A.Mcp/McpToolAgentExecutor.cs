using System.Text.Json;
using System.Threading.Channels;
using A2A.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace A2A.Mcp;

/// <summary>
///     Runs MCP tools as an A2A agent. The caller picks the tool with skill selection (the skill id is the tool name), or
///     the default tool runs.
///     Only tools on the allowlist of <see cref="McpToolAgentOptions" /> run.
/// </summary>
/// <remarks>
///     <para>
///         The host owns the <see cref="McpClient" /> and its transport credentials. Nothing from
///         <see cref="AgentRequestContext.Call" /> (the caller's
///         token or headers) reaches the MCP server. Elicitation and sampling handlers are not set by this class: an MCP
///         server that needs them
///         fails the tool call.
///     </para>
///     <para>
///         The arguments are one JSON data part with an object, or plain text for a tool allowed with a text argument. A
///         tool error fails the task with
///         a fixed text (see <see cref="McpToolAgentOptions.IncludeToolErrorText" />). Any other exception ends the task
///         as failed with the generic server
///         text, and the server logs it. Cancellation ends the task as canceled and cancels the MCP call.
///     </para>
/// </remarks>
public sealed class McpToolAgentExecutor : IAgentExecutor
{
    private const string NotAvailableText = "The requested tool is not available.";

    private const string BadArgumentsText =
        "The message must be one JSON data part with an object, or plain text for a tool that takes text.";

    private const string ArgumentsRejectedText = "The tool rejected the arguments.";
    private const string ToolErrorText = "The tool reported an error.";
    private const string TimedOutText = "The tool did not finish in time.";

    private const string UrlElicitationText =
        "The tool needs an authorization step that this agent cannot do. It happens outside this agent.";

    private const int MaxLoggedCharacters = 500;
    private const int MaxProgressCharacters = 1000;

    private readonly McpClient _client;
    private readonly ILogger _logger;
    private readonly McpToolAgentOptions _options;

    /// <summary>
    ///     Creates an executor over <paramref name="client" />. The client belongs to the caller and is not disposed
    ///     here.
    /// </summary>
    /// <exception cref="ArgumentException">The options allow no tool.</exception>
    public McpToolAgentExecutor(McpClient client, McpToolAgentOptions options,
        ILogger<McpToolAgentExecutor>? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.ThrowIfNoTools();
        _logger = logger ?? NullLogger<McpToolAgentExecutor>.Instance;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(updater);

        var toolName = ChooseTool(context);
        if (toolName is null || !_options.AllowedTools.TryGetValue(toolName, out var textArgument))
        {
            // The name the caller sent is not echoed.
            await updater.RejectAsync(NotAvailableText, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!TryBuildArguments(context.Message.Parts, textArgument, out var arguments))
        {
            await updater.RejectAsync(BadArgumentsText, cancellationToken).ConfigureAwait(false);
            return;
        }

        await updater.StartWorkAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var outcome = await RunToolAsync(toolName, arguments, updater, cancellationToken).ConfigureAwait(false);
        switch (outcome.Kind)
        {
            case OutcomeKind.Rejected:
                await updater.RejectAsync(outcome.Text, cancellationToken).ConfigureAwait(false);
                return;
            case OutcomeKind.Failed:
                await updater.FailAsync(outcome.Text, cancellationToken).ConfigureAwait(false);
                return;
        }

        var parts = outcome.Result!.ToParts();
        if (parts.Count > 0)
            await updater.AddArtifactAsync(parts, name: _options.ArtifactName, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        await updater.CompleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private string? ChooseTool(AgentRequestContext context)
    {
        // The skill the caller selected wins. A selected skill that is not allowed is refused, never swapped for the default.
        if (context.RequestedSkillId is { Length: > 0 } requested) return requested;
        if (_options.DefaultTool is { } fallback) return fallback;
        return _options.AllowedTools.Count == 1 ? _options.AllowedTools.Keys.First() : null;
    }

    /// <summary>
    ///     One JSON data part with an object, or text parts for a tool with a text argument. Nothing else: file and URL parts
    ///     are not
    ///     accepted, because a tool would fetch them or take them as paths.
    /// </summary>
    private static bool TryBuildArguments(List<Part> parts, string? textArgument,
        out Dictionary<string, object?> arguments)
    {
        arguments = [];
        if (parts.Count == 1 && parts[0] is DataPart { Data: { ValueKind: JsonValueKind.Object } data })
        {
            foreach (var property in data.EnumerateObject()) arguments[property.Name] = property.Value.Clone();
            return true;
        }

        if (textArgument is not null && parts.Count > 0 && parts.All(p => p is TextPart))
        {
            arguments[textArgument] = string.Join("\n", parts.Cast<TextPart>().Select(p => p.Text));
            return true;
        }

        return false;
    }

    private async Task<Outcome> RunToolAsync(string toolName, Dictionary<string, object?> arguments,
        TaskUpdater updater, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_options.ToolTimeout != Timeout.InfiniteTimeSpan) linked.CancelAfter(_options.ToolTimeout);
        var pump = _options.ProgressInterval == Timeout.InfiniteTimeSpan
            ? null
            : new ProgressPump(updater, _options.ProgressInterval, _logger);
        CallToolResult result;
        try
        {
            result = await _client.CallToolAsync(toolName, arguments, pump, cancellationToken: linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            McpLog.ToolTimedOut(_logger, toolName, _options.ToolTimeout);
            return new Outcome(OutcomeKind.Failed, Text: TimedOutText);
        }
        catch (UrlElicitationRequiredException)
        {
            // The URL belongs to the MCP server. It is neither forwarded nor logged.
            McpLog.ToolNeedsUrlElicitation(_logger, toolName);
            return new Outcome(OutcomeKind.Failed, Text: UrlElicitationText);
        }
        catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.UrlElicitationRequired)
        {
            McpLog.ToolNeedsUrlElicitation(_logger, toolName);
            return new Outcome(OutcomeKind.Failed, Text: UrlElicitationText);
        }
        catch (McpProtocolException ex) when (ex.ErrorCode == McpErrorCode.InvalidParams)
        {
            McpLog.ToolRejectedArguments(_logger, ex, toolName);
            return new Outcome(OutcomeKind.Rejected, Text: ArgumentsRejectedText);
        }
        finally
        {
            // Stopped before any final state, so no progress update can follow it.
            if (pump is not null)
            {
                await pump.StopAsync().ConfigureAwait(false);
                pump.Dispose();
            }
        }

        if (result.IsError == true)
        {
            var detail = ErrorText(result);
            McpLog.ToolReturnedError(_logger, toolName,
                detail.Length > MaxLoggedCharacters ? detail[..MaxLoggedCharacters] : detail);
            return new Outcome(OutcomeKind.Failed,
                Text: _options.IncludeToolErrorText && detail.Length > 0 ? detail : ToolErrorText);
        }

        return new Outcome(OutcomeKind.Succeeded, result);
    }

    private static string ErrorText(CallToolResult result)
    {
        return result.Content is null
            ? ""
            : string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
    }

    private enum OutcomeKind
    {
        Succeeded,
        Rejected,
        Failed
    }

    private readonly record struct Outcome(OutcomeKind Kind, CallToolResult? Result = null, string? Text = null);

    /// <summary>
    ///     Takes MCP progress through a one-slot channel (a newer value replaces an older one) and sends it as Working status
    ///     updates, at most one per interval.
    /// </summary>
    private sealed class ProgressPump : IProgress<ProgressNotificationValue>, IDisposable
    {
        private readonly Channel<ProgressNotificationValue> _channel = Channel.CreateBounded<ProgressNotificationValue>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        private readonly TimeSpan _interval;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;
        private readonly TaskUpdater _updater;

        public ProgressPump(TaskUpdater updater, TimeSpan interval, ILogger logger)
        {
            _updater = updater;
            _interval = interval;
            _logger = logger;
            _task = Task.Run(RunAsync);
        }

        public void Dispose()
        {
            _stop.Dispose();
        }

        public void Report(ProgressNotificationValue value)
        {
            _channel.Writer.TryWrite(value);
        }

        public async Task StopAsync()
        {
            _channel.Writer.TryComplete();
            await _stop.CancelAsync().ConfigureAwait(false);
            await _task.ConfigureAwait(false);
        }

        private async Task RunAsync()
        {
            try
            {
                await foreach (var value in _channel.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        await _updater.UpdateStatusAsync(TaskState.Working, _updater.NewAgentMessage(Describe(value)),
                            cancellationToken: _stop.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        McpLog.ProgressFailed(_logger, ex);
                        return;
                    }

                    await Task.Delay(_interval, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        private static string Describe(ProgressNotificationValue value)
        {
            var text = value.Message;
            if (string.IsNullOrWhiteSpace(text))
                text = value.Total is { } total
                    ? $"Progress: {value.Progress} of {total}"
                    : $"Progress: {value.Progress}";
            return text.Length > MaxProgressCharacters ? text[..MaxProgressCharacters] : text;
        }
    }
}