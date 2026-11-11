using System.Collections;
using System.Reflection;
using A2A.Client;
using A2A.Client.Resilience;

namespace A2A.Tests;

/// <summary><see cref="A2AClientOptions.Clone"/> is the only copy of the options, so it must carry every property.</summary>
public sealed class ClientOptionsCloneTests
{
    private sealed class Clock : TimeProvider;

    private sealed class Factory : IA2ATransportFactory
    {
        public string ProtocolBinding => "custom";
        public bool CanCreate(AgentInterface agentInterface) => false;
        public IA2AClient Create(AgentInterface agentInterface, HttpClient httpClient, A2AClientOptions options) => throw new NotSupportedException();
    }

    /// <summary>One non-default value per property. A new property without a value here fails the test, so it cannot be forgotten.</summary>
    private static Dictionary<string, object?> Samples() => new()
    {
        [nameof(A2AClientOptions.ProtocolVersion)] = "9.9",
        [nameof(A2AClientOptions.RequireHttpsWebhooks)] = true,
        [nameof(A2AClientOptions.Extensions)] = new List<string> { "urn:ext" },
        [nameof(A2AClientOptions.Tenant)] = "tenant-a",
        [nameof(A2AClientOptions.SupportedBindings)] = new List<string> { "custom" },
        [nameof(A2AClientOptions.TransportFactories)] = new List<IA2ATransportFactory> { new Factory() },
        [nameof(A2AClientOptions.RestSubscribeMethod)] = HttpMethod.Put,
        [nameof(A2AClientOptions.ValidateCapabilities)] = false,
        [nameof(A2AClientOptions.ValidateResponses)] = false,
        [nameof(A2AClientOptions.MaxRetries)] = 7,
        [nameof(A2AClientOptions.TimeProvider)] = new Clock(),
        [nameof(A2AClientOptions.CircuitBreaker)] = new CircuitBreakerOptions(),
        [nameof(A2AClientOptions.OnMessageReceived)] = new Func<ReceivedMessage, CancellationToken, ValueTask>((_, _) => ValueTask.CompletedTask),
        [nameof(A2AClientOptions.EndpointPolicy)] = AgentEndpointPolicies.AllowAll,
    };

    private static PropertyInfo[] Properties => typeof(A2AClientOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance);

    [Fact]
    public void Clone_carries_every_property()
    {
        var samples = Samples();
        var defaults = new A2AClientOptions();
        var source = new A2AClientOptions();
        foreach (var property in Properties)
        {
            Assert.True(samples.ContainsKey(property.Name), $"Add {property.Name} to Samples() and to A2AClientOptions.Clone.");
            Assert.NotEqual(property.GetValue(defaults), samples[property.Name]);
            property.SetValue(source, samples[property.Name]);
        }

        var clone = source.Clone();

        foreach (var property in Properties)
        {
            var expected = property.GetValue(source);
            var actual = property.GetValue(clone);
            if (expected is IEnumerable list and not string) Assert.Equal(list.Cast<object>(), ((IEnumerable)actual!).Cast<object>());
            else Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Clone_has_its_own_lists()
    {
        var source = new A2AClientOptions { Extensions = ["urn:a"] };

        var clone = source.Clone();
        clone.Extensions.Add("urn:b");
        clone.SupportedBindings.Clear();
        clone.TransportFactories.Add(new Factory());

        Assert.Equal(["urn:a"], source.Extensions);
        Assert.NotEmpty(source.SupportedBindings);
        Assert.Empty(source.TransportFactories);
    }
}
