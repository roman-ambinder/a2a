using System.Net;
using System.Net.Http.Headers;
using A2A.Server.Push;

namespace A2A.Tests;

/// <summary>
/// <see cref="PushDeliveryOptions.RetryPolicy"/> (S11): after a failure worth retrying, the policy picks the wait before the
/// next attempt or dead-letters the delivery.
/// </summary>
public class PushRetryPolicyTests
{
    private static TaskPushNotificationConfig Hook() => new() { Url = "https://hooks.example/cb", Token = "tok" };

    /// <summary>Answers with <paramref name="first"/> (and its Retry-After, if any) once, then 200.</summary>
    private sealed class OnceThenOk(HttpStatusCode first, RetryConditionHeaderValue? retryAfter = null) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = new HttpResponseMessage(Interlocked.Increment(ref Calls) == 1 ? first : HttpStatusCode.OK);
            if (Calls == 1 && retryAfter is not null) response.Headers.RetryAfter = retryAfter;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task The_policy_sees_the_failure_and_its_wait_is_used()
    {
        var hooks = new OnceThenOk(HttpStatusCode.TooManyRequests, new RetryConditionHeaderValue(TimeSpan.FromSeconds(120)));
        var rig = new PushRig(hooks);
        var seen = new List<PushRetryContext>();
        rig.Options.PushDelivery.RetryPolicy = ctx =>
        {
            seen.Add(ctx);
            return ctx.RetryAfter;
        };
        await rig.SaveConfigAsync(Hook());
        var d = (await rig.CommitAsync("c1")).Single();
        using var service = rig.NewService();

        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // 429, Retry-After: 120

        var ctx = Assert.Single(seen);
        Assert.Equal((PushRig.Key, "c1", d.Sequence, 1), (ctx.Task, ctx.ConfigId, ctx.Sequence, ctx.Attempt));
        Assert.Equal(429, ctx.StatusCode);
        Assert.Equal("HTTP 429", ctx.Error);
        Assert.Equal(TimeSpan.FromSeconds(120), ctx.RetryAfter);
        // The default backoff: InitialBackoff (1 s) ± 20 %.
        Assert.InRange(ctx.DefaultDelay, TimeSpan.FromSeconds(0.8), TimeSpan.FromSeconds(1.2));

        rig.Time.Advance(TimeSpan.FromSeconds(119));
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None)); // the default backoff would have sent it by now
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, hooks.Calls);
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task A_policy_that_returns_null_dead_letters_the_delivery_at_once()
    {
        var hooks = new OnceThenOk(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        PushDeadLetterInfo? deadLettered = null;
        rig.Options.PushDelivery.RetryPolicy = _ => null;
        rig.Options.PushDelivery.OnDeadLetter = info =>
        {
            deadLettered = info;
            return ValueTask.CompletedTask;
        };
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None);

        var dead = Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
        Assert.Equal(1, dead.Attempt);
        Assert.Equal("HTTP 503", dead.LastError);
        Assert.Equal("HTTP 503", deadLettered?.LastError);
        Assert.Equal(1, hooks.Calls);
    }

    [Fact]
    public async Task A_policy_that_throws_falls_back_to_the_default_backoff()
    {
        var hooks = new OnceThenOk(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        rig.Options.PushDelivery.RetryPolicy = _ => throw new InvalidOperationException("bug in the policy");
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None);
        Assert.Empty(await rig.Outbox.ListDeadLettersAsync("", 10));
        rig.Time.Advance(TimeSpan.FromSeconds(1.2));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(2, hooks.Calls);
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task A_negative_wait_retries_at_once_and_a_huge_one_is_capped()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var rig = new PushRig(hooks);
        var calls = 0;
        rig.Options.PushDelivery.RetryPolicy = _ => ++calls == 1 ? TimeSpan.FromSeconds(-5) : TimeSpan.MaxValue;
        rig.Options.PushDelivery.MaxDeliveryAge = null;
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None); // 503, wait -5 s: due now
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None)); // 503, wait TimeSpan.MaxValue: 30 days
        rig.Time.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await service.RunOnceAsync(CancellationToken.None));
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await service.RunOnceAsync(CancellationToken.None));
        Assert.Equal(3, hooks.Requests.Count);
        Assert.Equal(0, rig.Outbox.Count);
    }

    [Fact]
    public async Task MaxAttempts_still_applies_and_the_policy_is_not_asked_after_the_last_attempt()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.ServiceUnavailable);
        var rig = new PushRig(hooks);
        var asked = 0;
        rig.Options.PushDelivery.MaxAttempts = 2;
        rig.Options.PushDelivery.RetryPolicy = ctx =>
        {
            asked++;
            return ctx.DefaultDelay;
        };
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await rig.DrainAsync(service);

        Assert.Equal(2, hooks.Requests.Count);
        Assert.Equal(1, asked);
        Assert.Equal(2, Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10)).Attempt);
    }

    [Fact]
    public async Task A_failure_that_will_not_change_is_dead_lettered_without_asking_the_policy()
    {
        var hooks = new ScriptedHooks(HttpStatusCode.NotFound);
        var rig = new PushRig(hooks);
        var asked = 0;
        rig.Options.PushDelivery.RetryPolicy = ctx =>
        {
            asked++;
            return ctx.DefaultDelay;
        };
        await rig.SaveConfigAsync(Hook());
        await rig.CommitAsync("c1");
        using var service = rig.NewService();

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, asked);
        Assert.Single(await rig.Outbox.ListDeadLettersAsync("", 10));
    }

    [Fact]
    public void Retry_after_as_a_date_is_the_time_until_it()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        using var later = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        later.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(3));
        using var past = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddMinutes(-3));
        using var none = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        Assert.Equal(TimeSpan.FromMinutes(3), PushWebhookSender.RetryAfterOf(later, now));
        Assert.Equal(TimeSpan.Zero, PushWebhookSender.RetryAfterOf(past, now));
        Assert.Null(PushWebhookSender.RetryAfterOf(none, now));
    }
}
