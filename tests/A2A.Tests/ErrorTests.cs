using A2A.Server;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace A2A.Tests;

/// <summary>Task store whose every method throws, to hit the endpoint's unexpected-exception path.</summary>
public sealed class ThrowingTaskStore : ITaskStore
{
    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("store exploded");

    public Task SaveAsync(TaskRecord record, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("store exploded");

    public Task<TaskQueryResult> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("store exploded");
}

public class ErrorTests
{
    [Fact]
    public void Payload_too_large_maps_to_413_with_reason()
    {
        var ex = A2AException.PayloadTooLarge(10);
        Assert.Equal(413, ex.HttpStatus);
        var info = A2AErrors.BuildDetails(ex)[0];
        Assert.Equal("PAYLOAD_TOO_LARGE", info.GetProperty("reason").GetString());
        Assert.Equal("false", info.GetProperty("metadata").GetProperty("retryable").GetString());
    }

    [Fact]
    public void Rate_limited_is_retryable_with_retry_after()
    {
        var info = A2AErrors.BuildDetails(A2AException.RateLimited(TimeSpan.FromSeconds(7)))[0].GetProperty("metadata");
        Assert.Equal("true", info.GetProperty("retryable").GetString());
        Assert.Equal("7", info.GetProperty("retryAfter").GetString());
    }

    [Fact]
    public void Idempotency_key_mismatch_maps_to_422()
    {
        var ex = A2AException.IdempotencyKeyMismatch();
        Assert.Equal(422, ex.HttpStatus);
        Assert.Equal("IDEMPOTENCY_KEY_MISMATCH", A2AErrors.BuildDetails(ex)[0].GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("PAYLOAD_TOO_LARGE", A2AErrorCode.InvalidRequest)]
    [InlineData("IDEMPOTENCY_KEY_MISMATCH", A2AErrorCode.InvalidRequest)]
    [InlineData("RATE_LIMITED", A2AErrorCode.RateLimited)]
    public void New_reasons_parse_back_to_a_code(string reason, A2AErrorCode expected)
    {
        Assert.True(A2AErrors.TryParseReason(reason, out var code));
        Assert.Equal(expected, code);
        Assert.False(A2AErrors.TryParseReason("SOMETHING_ELSE", out _));
    }

    [Fact]
    public void Retryable_defaults_follow_code()
    {
        Assert.True(new A2AException(A2AErrorCode.Internal).Retryable);
        Assert.False(new A2AException(A2AErrorCode.TaskNotFound).Retryable);
        Assert.True(A2AException.RateLimited(TimeSpan.FromSeconds(1)).Retryable);
    }

    [Theory]
    [MemberData(nameof(BindingTests.Bindings), MemberType = typeof(BindingTests))]
    public async Task Rate_limit_round_trips_to_client(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<ITaskStore, RateLimitedTaskStore>());
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).GetTaskAsync("x"));
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
        Assert.True(ex.IsRetryable);
    }

    [Theory]
    [MemberData(nameof(BindingTests.Bindings), MemberType = typeof(BindingTests))]
    public async Task Unexpected_exception_text_hidden_outside_development(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<ITaskStore, ThrowingTaskStore>());
        Assert.False(host.App.Environment.IsDevelopment());
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).GetTaskAsync("x"));
        Assert.Equal(A2AErrorCode.Internal, ex.Code);
        Assert.DoesNotContain("store exploded", ex.Message);
    }

    [Fact]
    public async Task Unexpected_exception_text_shown_when_option_is_true()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.IncludeExceptionDetails = true,
            services: s => s.AddSingleton<ITaskStore, ThrowingTaskStore>());
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).GetTaskAsync("x"));
        Assert.Contains("InvalidOperationException: store exploded", ex.Message);
    }
}

internal sealed class RateLimitedTaskStore : ITaskStore
{
    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
        throw A2AException.RateLimited(TimeSpan.FromSeconds(3));

    public Task SaveAsync(TaskRecord record, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<TaskQueryResult> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default) =>
        throw A2AException.RateLimited(TimeSpan.FromSeconds(3));
}
