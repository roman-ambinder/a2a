using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Push;
using A2A.Push;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.PushNotifications;

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

    public static void Secrets(IServiceCollection services)
    {
        #region DataProtection
        services.AddDataProtection()
            .SetApplicationName("my-agent")
            .PersistKeysToFileSystem(new DirectoryInfo("/var/keys/my-agent"));
        services.AddA2ADataProtectionPushSecrets();
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
        var receiver = new PushNotificationReceiver(); // one instance: it remembers webhook ids
        if (!WebhookSignature.TryParseSecret(whsecSecret, out var secret))
            throw new InvalidOperationException("The signing secret must be whsec_ + base64 of at least 16 bytes.");

        app.MapPost("/a2a-webhook", async (HttpRequest request) =>
        {
            // Verify the exact bytes that were signed, not re-serialized JSON.
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            var body = buffer.ToArray();
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            switch (receiver.VerifySignature(headers, body, secret))
            {
                case PushValidationResult.Valid:
                    var evt = A2A.Json.A2AJson.Deserialize<StreamResponse>(System.Text.Encoding.UTF8.GetString(body));
                    // Hand evt to your own code here.
                    _ = evt;
                    return Results.Ok();
                case PushValidationResult.Duplicate:
                    return Results.Ok(); // a retry we already handled: answer 2xx so the sender stops
                default:
                    return Results.Unauthorized(); // InvalidSignature or Stale
            }
        });
        #endregion
    }
}
