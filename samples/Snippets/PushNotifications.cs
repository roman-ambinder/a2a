using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Push;
using A2A.Push;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using Microsoft.AspNetCore.DataProtection;

namespace Snippets.PushNotifications;

internal static class Snippets
{
    public static AgentCard Card()
    {
        #region Capability
        var card = new AgentCard
        {
            Name = "Echo Agent",
            Description = "Echoes your message back.",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities { PushNotifications = true },
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills = [new AgentSkill { Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = ["echo"] }],
        };
        #endregion
        return card;
    }

    public static async Task RegisterAsync(IA2AClient client, string taskId)
    {
        #region Register
        // A signing secret: "whsec_" + base64 of at least 16 random bytes. Keep it; the receiver needs it.
        var secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            TaskId = taskId,
            Url = "https://hooks.example.com/a2a",
            Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = secret },
        });
        #endregion
    }

    public static async Task RegisterInlineAsync(IA2AClient client, string secret)
    {
        #region RegisterInline
        var response = await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("Summarize the report"),
            Configuration = new SendMessageConfiguration
            {
                ReturnImmediately = true,
                TaskPushNotificationConfig = new TaskPushNotificationConfig
                {
                    Url = "https://hooks.example.com/a2a",
                    Token = "my-notification-token",
                    Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = secret },
                },
            },
        });
        #endregion
        _ = response;
    }

    public static void Delivery(IServiceCollection services)
    {
        #region DeliveryOptions
        services.Configure<A2AServerOptions>(o =>
        {
            o.PushDelivery.InitialBackoff = TimeSpan.FromSeconds(2);
            o.PushDelivery.MaxBackoff = TimeSpan.FromMinutes(10);
            o.PushDelivery.MaxAttempts = 8;
            o.PushNotifications.AllowedHosts.Add("hooks.example.com");
            o.PushNotifications.Timeout = TimeSpan.FromSeconds(10);
        });
        #endregion
    }

    public static void PinProxyTarget(IServiceCollection services)
    {
        #region PinProxyTarget
        services.Configure<A2AServerOptions>(o =>
        {
            o.PushNotifications.Proxy = new System.Net.WebProxy("http://egress-proxy.internal:3128");
            // The proxy gets the address the SDK checked (CONNECT 93.184.216.34:443), not the webhook's host name.
            o.PushNotifications.PinProxyTarget = true;
        });
        #endregion
    }

    public static void DeadLetterHook(IServiceCollection services)
    {
        #region DeadLetterHook
        services.Configure<A2AServerOptions>(o =>
        {
            o.PushDelivery.MaxDeliveryAge = TimeSpan.FromHours(6);
            o.PushDelivery.OnDeadLetter = info =>
            {
                Console.Error.WriteLine($"Webhook {info.ConfigId} of task {info.Task.TaskId} gave up at event {info.Sequence}: {info.LastError}");
                return ValueTask.CompletedTask;
            };
        });
        #endregion
    }

    public static void RetryPolicy(IServiceCollection services)
    {
        #region RetryPolicy
        services.Configure<A2AServerOptions>(o =>
        {
            o.PushDelivery.RetryPolicy = ctx => ctx switch
            {
                { StatusCode: 429, RetryAfter: { } wait } => wait, // the receiver said when
                { Attempt: >= 10 } => null, // give up: dead-letter now
                _ => ctx.DefaultDelay
            };
        });
        #endregion
    }

    public static void Secrets(IServiceCollection services, A2AServerBuilder a)
    {
        #region DataProtection
        services.AddDataProtection()
            .SetApplicationName("my-agent")
            .PersistKeysToFileSystem(new DirectoryInfo("/var/keys/my-agent"));
        a.AddDataProtectionPushSecrets();   // a: the builder of AddA2A
        #endregion
    }

    public static void Admin(WebApplication app)
    {
        #region DeadLetters
        app.MapGet("/admin/push/dead-letters", async (A2APushAdmin admin, CancellationToken ct) =>
        {
            var entries = await admin.ListDeadLettersAsync(tenant: "", max: 50, ct);
            return entries.Select(e => new
            {
                e.Delivery.Task.TaskId,
                e.Delivery.ConfigId,
                e.Delivery.Sequence,
                e.Attempt,
                e.LastError,
            });
        }).RequireAuthorization("Admin");

        app.MapPost("/admin/push/redrive/{taskId}/{configId}/{sequence:long}", async (
            string taskId, string configId, long sequence, A2APushAdmin admin, CancellationToken ct) =>
        {
            var entries = await admin.ListDeadLettersAsync("", 1000, ct);
            var entry = entries.FirstOrDefault(e => e.Delivery.Task.TaskId == taskId
                && e.Delivery.ConfigId == configId && e.Delivery.Sequence == sequence);
            if (entry is null) return Results.NotFound();
            return await admin.RedriveAsync(entry.Delivery, ct) ? Results.Accepted() : Results.NotFound();
        }).RequireAuthorization("Admin");
        #endregion
    }

    public static void Receiver(WebApplication app, string whsecSecret)
    {
        #region Receiver
        var receiver = new PushNotificationReceiver(); // one instance: it remembers handled delivery ids
        if (!WebhookSignature.TryParseSecret(whsecSecret, out var secret))
            throw new InvalidOperationException("The signing secret must be whsec_ + base64 of at least 16 bytes.");

        app.MapPost("/a2a-webhook", async (HttpRequest request, CancellationToken ct) =>
        {
            // Verify the exact bytes that were signed, not re-serialized JSON.
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer, ct);
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            var result = await receiver.HandleAsync(headers, buffer.ToArray(), async (delivery, token) =>
            {
                // Save the event first. If this throws, the id is forgotten and the sender's retry comes back here.
                await SaveEventAsync(delivery.Event, delivery.DeliveryId, token);
            }, signingSecret: secret, cancellationToken: ct);

            return Results.StatusCode(result.StatusCode); // 200, 401, 500 or 503: the sender retries 408, 429 and 5xx only
        });
        #endregion
    }

    private static Task SaveEventAsync(StreamResponse evt, string? deliveryId, CancellationToken ct) => Task.CompletedTask;

    public static void ReceiverLowLevel(WebApplication app, string whsecSecret)
    {
        #region ReceiverLowLevel
        var receiver = new PushNotificationReceiver();
        if (!WebhookSignature.TryParseSecret(whsecSecret, out var secret))
            throw new InvalidOperationException("The signing secret must be whsec_ + base64 of at least 16 bytes.");

        app.MapPost("/a2a-webhook-low-level", async (HttpRequest request) =>
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            // VerifySignature marks the id as seen at once. Nothing may fail after a Valid result,
            // or the sender's retry is reported as Duplicate and the event is lost.
            switch (receiver.VerifySignature(headers, body, secret))
            {
                case PushValidationResult.Valid:
                    return Results.Ok();
                case PushValidationResult.Duplicate:
                    return Results.Ok(); // a retry we already handled: answer 2xx so the sender stops
                default:
                    return Results.Unauthorized(); // InvalidSignature or Stale
            }
        });
        #endregion
    }

    public static async Task RegisterSafelyAsync(A2AClient client, string taskId, string secret)
    {
        #region RegisterSafely
        // Registers the webhook first, then reads the task once.
        var registration = await client.RegisterPushAsync(taskId, new TaskPushNotificationConfig
        {
            Url = "https://hooks.example.com/a2a",
            Authentication = new AuthenticationInfo { Scheme = WebhookSignature.Scheme, Credentials = secret },
        });

        if (registration.IsFinal)
        {
            // The task finished before the webhook existed. Servers of other SDKs send no catch-up
            // delivery, so use registration.Task now and do not wait for a callback.
            Console.WriteLine($"Already {registration.Task.Status.State}.");
        }
        #endregion
    }

    public static async Task TrackAsync(IA2AClient client, string taskId)
    {
        #region Tracker
        // One tracker per app. The callback is how you learn what GetTask found.
        using var tracker = new PushTaskTracker(client, (check, ct) =>
        {
            if (check.Task is { } task)
                Console.WriteLine($"{check.TaskId}: {task.Status.State} (checked because of {check.Reason}).");
            else
                Console.WriteLine($"{check.TaskId}: GetTask failed: {check.Error?.Message}");
            return Task.CompletedTask;
        });

        // Watch the task: if no final state arrives within 5 minutes, the tracker calls GetTask.
        await tracker.TrackAsync(taskId, TimeSpan.FromMinutes(5));

        #endregion
    }

    public static Task<PushHandleResult> OnWebhookAsync(PushNotificationReceiver receiver, PushTaskTracker tracker,
        IReadOnlyDictionary<string, string> headers, byte[] body, string token)
    {
        #region TrackerNotify
        // In the webhook handler, report every notification. A skipped sequence number triggers GetTask at once,
        // and a final state removes the task from the tracker.
        return receiver.HandleAsync(headers, body, (delivery, ct) => tracker.NotifyAsync(delivery, ct), expectedToken: token);
        #endregion
    }

    public static void Store()
    {
        #region Stores
        // Keep handled ids and tracked tasks in your own database so they survive a restart.
        // The in-memory defaults lose them; after a restart a delivery can repeat, which is safe for an idempotent handler.
        var receiver = new PushNotificationReceiver(new InMemoryPushDeliveryStore()); // or your IPushDeliveryStore
        var tasks = new InMemoryPushTaskStore();                                      // or your IPushTaskStore
        #endregion
        _ = (receiver, tasks);
    }
}
