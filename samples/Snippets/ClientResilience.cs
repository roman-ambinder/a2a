using A2A.Client;
using A2A.Client.Resilience;

namespace Snippets.ClientResilience;

internal static class Samples
{
    public static async Task<A2AClient> TurnOn(HttpClient httpClient)
    {
        #region TurnOn
        var options = new A2AClientOptions
        {
            CircuitBreaker = new CircuitBreakerOptions
            {
                FailureThreshold = 5,                      // consecutive failed calls
                OpenDuration = TimeSpan.FromSeconds(30),   // wait before a test call
            },
        };
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient, options);
        #endregion
        return client;
    }

    public static async Task HandleOpen(A2AClient client)
    {
        #region HandleOpen
        try
        {
            var task = await client.GetTaskAsync("task-1");
            Console.WriteLine(task.Status.State);
        }
        catch (CircuitOpenException ex)
        {
            // No request was sent. Try again after RetryAfter, or use another agent.
            Console.WriteLine($"Try again in {ex.RetryAfter}.");
        }
        #endregion
    }
}
