using System.Diagnostics;
using A2A.Client;

namespace A2A.TestKit;

/// <summary>
///     Assertions on A2A responses, task states and event order. A failure throws
///     <see cref="A2AAssertionException" />.
/// </summary>
public static class A2AAssert
{
    /// <summary>Checks that the response is a task, and returns it.</summary>
    public static AgentTask IsTask(SendMessageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response is TaskResponse t
            ? t.Task
            : throw new A2AAssertionException($"Expected a task response but got {TestEvents.Describe(response)}.");
    }

    /// <summary>Checks that the response is a message, and returns it.</summary>
    public static Message IsMessage(SendMessageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response is MessageResponse m
            ? m.Message
            : throw new A2AAssertionException($"Expected a message response but got {TestEvents.Describe(response)}.");
    }

    /// <summary>Checks the state of the task, and returns the task.</summary>
    public static AgentTask HasState(AgentTask task, TaskState expected)
    {
        ArgumentNullException.ThrowIfNull(task);
        var actual = task.Status.State;
        if (actual != expected)
            throw new A2AAssertionException(
                $"Expected task {task.Id} to be {TestEvents.StateName(expected)} but it is {TestEvents.StateName(actual)}.");
        return task;
    }

    /// <summary>Checks that the response is a task in the expected state, and returns the task.</summary>
    public static AgentTask HasState(SendMessageResponse response, TaskState expected)
    {
        return HasState(IsTask(response), expected);
    }

    /// <summary>Checks that the task has an artifact with this name or id, and returns it.</summary>
    public static Artifact HasArtifact(AgentTask task, string nameOrId)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(nameOrId);
        var artifacts = task.Artifacts ?? [];
        return artifacts.FirstOrDefault(a => a.Name == nameOrId || a.ArtifactId == nameOrId)
               ?? throw new A2AAssertionException(
                   $"Expected task {task.Id} to have an artifact '{nameOrId}'. Artifacts: " +
                   (artifacts.Count == 0
                       ? "(none)"
                       : string.Join(", ", artifacts.Select(a => a.Name ?? a.ArtifactId))) + ".");
    }

    /// <summary>
    ///     Checks the exact order of states in the events: the state of every task event and status update event, with no
    ///     de-duplication. Message and artifact events are skipped.
    /// </summary>
    public static void StateOrder(IEnumerable<StreamResponse> events, params TaskState[] expected)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(expected);
        var list = events.ToList();
        var actual = list.Select(StateOf).Where(s => s is not null).Select(s => s!.Value).ToList();
        if (!actual.SequenceEqual(expected))
            throw new A2AAssertionException(
                $"Expected state order: {TestEvents.StateList(expected)}\nActual state order:   {TestEvents.StateList(actual)}\nActual events: {TestEvents.Describe(list)}");
    }

    /// <summary>Checks that the events are exactly <paramref name="expected" />, in order.</summary>
    public static void EventOrder(IEnumerable<StreamResponse> events, params ExpectedEvent[] expected)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(expected);
        var list = events.ToList();
        var mismatch = -1;
        for (var i = 0; i < Math.Max(list.Count, expected.Length); i++)
            if (i >= list.Count || i >= expected.Length || !expected[i].Matches(list[i]))
            {
                mismatch = i;
                break;
            }

        if (mismatch < 0) return;
        var where = mismatch >= list.Count
            ? $"event {mismatch + 1} is missing"
            : mismatch >= expected.Length
                ? $"event {mismatch + 1} ({TestEvents.Describe(list[mismatch])}) is not expected"
                : $"event {mismatch + 1} is {TestEvents.Describe(list[mismatch])}, expected {expected[mismatch]}";
        throw new A2AAssertionException(
            $"Event order does not match: {where}.\nExpected events: {Join(expected)}\nActual events:   {TestEvents.Describe(list)}");
    }

    /// <summary>Checks that the events contain <paramref name="expected" /> in this order. Other events may sit in between.</summary>
    public static void ContainsEventOrder(IEnumerable<StreamResponse> events, params ExpectedEvent[] expected)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(expected);
        var list = events.ToList();
        var position = 0;
        for (var i = 0; i < expected.Length; i++)
        {
            while (position < list.Count && !expected[i].Matches(list[position])) position++;
            if (position >= list.Count)
                throw new A2AAssertionException(
                    $"Event {expected[i]} (expected event {i + 1} of {expected.Length}) was not found after the earlier ones.\nExpected events in order: {Join(expected)}\nActual events: {TestEvents.Describe(list)}");
            position++;
        }
    }

    /// <summary>
    ///     Runs <paramref name="action" /> and checks that it throws an <see cref="A2AException" />, with the code
    ///     <paramref name="expected" /> when given. Returns the exception.
    /// </summary>
    public static async Task<A2AException> ThrowsA2AAsync(Func<Task> action, A2AErrorCode? expected = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (A2AException e)
        {
            if (expected is { } code && e.Code != code)
                throw new A2AAssertionException($"Expected A2A error {code} but got {e.Code}: {e.Message}", e);
            return e;
        }
        catch (Exception e)
        {
            throw new A2AAssertionException(
                $"Expected an A2AException{(expected is null ? "" : $" ({expected})")} but got {e.GetType().Name}: {e.Message}",
                e);
        }

        throw new A2AAssertionException(
            $"Expected an A2AException{(expected is null ? "" : $" ({expected})")} but nothing was thrown.");
    }

    /// <summary>
    ///     Polls GetTask every 10 ms (real time) until the task has the expected state, and returns it. A task that does not
    ///     exist
    ///     yet counts as not there yet.
    /// </summary>
    /// <param name="client">The client to ask.</param>
    /// <param name="taskId">The task id.</param>
    /// <param name="expected">The state to wait for.</param>
    /// <param name="timeout">How long to wait, in real time. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="A2AAssertionException">The task did not reach the state in time.</exception>
    public static async Task<AgentTask> EventuallyHasStateAsync(IA2AClient client, string taskId, TaskState expected,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(taskId);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = Stopwatch.StartNew();
        var last = "the task was not found";
        while (true)
        {
            try
            {
                var task = await client
                    .GetTaskAsync(new GetTaskRequest { Id = taskId }, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                if (task.Status.State == expected) return task;
                last = $"its state is {TestEvents.StateName(task.Status.State)}";
            }
            catch (A2AException e) when (e.Code == A2AErrorCode.TaskNotFound)
            {
                last = "the task was not found";
            }

            if (watch.Elapsed >= limit)
                throw new A2AAssertionException(
                    $"Task {taskId} did not reach {TestEvents.StateName(expected)} within {limit}: {last}.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TaskState? StateOf(StreamResponse e)
    {
        return e switch
        {
            TaskEvent t => t.Task.Status.State,
            StatusUpdateEvent s => s.StatusUpdate.Status.State,
            _ => null
        };
    }

    private static string Join(IEnumerable<ExpectedEvent> events)
    {
        var text = string.Join(", ", events);
        return text.Length == 0 ? "(no events)" : text;
    }
}