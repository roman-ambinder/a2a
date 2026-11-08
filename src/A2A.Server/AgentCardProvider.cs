using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Server.Pipeline;
using A2A.Signing;

namespace A2A.Server;

/// <summary>
/// The default <see cref="IAgentCardProvider"/>: a copy of <see cref="A2AServerOptions.AgentCard"/> (through the wire format, so the
/// host's instance is never changed) that lists the URI of every registered <see cref="A2AExtensionOperation"/> in
/// <see cref="AgentCapabilities.Extensions"/> (not required), unless the card already lists it. It adds no interfaces: the core has
/// no addresses. The card is built on first use and cached. Derive to complete the copy further (see <see cref="Complete"/>).
/// With a <see cref="TenantCardProvider"/> (one agent per tenant), <see cref="GetAsync"/> completes each tenant's card the same
/// way (kept per card instance), and extended cards and signing keys come from that provider.
/// </summary>
public class AgentCardProvider : IAgentCardProvider
{
    private readonly A2AServerOptions _options;
    private readonly IEnumerable<A2AExtensionOperation> _extensions;
    private readonly TenantCardProvider? _tenants;
    private readonly object _gate = new();
    private readonly ConditionalWeakTable<AgentCard, AgentCard> _completedTenantCards = new();
    private AgentCard? _card;

    /// <summary>The message of the exception thrown when the card has no name.</summary>
    internal const string NoNameMessage =
        "The Agent Card has no name. Call AddAgentCard(...) on the A2A builder or set A2AServerOptions.AgentCard.";

    /// <summary>Creates the provider. In an app the DI container does this.</summary>
    /// <param name="options">The server options; <see cref="A2AServerOptions.AgentCard"/> is read when the card is first built.</param>
    /// <param name="extensions">The registered extension operations.</param>
    /// <param name="tenants">The cards of each tenant's agent, or null when one agent (the host's card) serves every tenant.</param>
    public AgentCardProvider(A2AServerOptions options, IEnumerable<A2AExtensionOperation> extensions, TenantCardProvider? tenants = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(extensions);
        _options = options;
        _extensions = extensions;
        _tenants = tenants;
    }

    /// <summary>True when a <see cref="TenantCardProvider"/> supplies each tenant's card.</summary>
    public bool HasTenantCards => _tenants is not null;

    /// <summary>
    /// Whether the card about to be built may be kept. Checked under a lock before the card is built (so a value that turns true
    /// while <see cref="Complete"/> runs does not keep a card completed with older data). When false, that call returns the card but
    /// the next call builds again. Default true.
    /// </summary>
    protected virtual bool CanCache => true;

    /// <summary>Completes the copy (never the host's card) before it is returned. Default: nothing.</summary>
    /// <param name="copy">The copy, with the extension entries already added.</param>
    protected virtual void Complete(AgentCard copy) { }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The card has no name (no <c>AddAgentCard(...)</c> and no <see cref="A2AServerOptions.AgentCard"/>).</exception>
    public AgentCard Get()
    {
        if (Volatile.Read(ref _card) is { } cached) return cached;
        lock (_gate)
        {
            if (_card is { } again) return again;
            var canCache = CanCache;
            var card = Build(_options.AgentCard);
            if (string.IsNullOrWhiteSpace(card.Name)) throw new InvalidOperationException(NoNameMessage);
            if (canCache) Volatile.Write(ref _card, card);
            return card;
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentCard?> GetAsync(AgentRequestScope scope, CancellationToken cancellationToken)
    {
        if (_tenants is null) return new(Get());
        return CompleteTenantCardAsync(_tenants.GetCardAsync(scope, cancellationToken));
    }

    private async ValueTask<AgentCard?> CompleteTenantCardAsync(ValueTask<AgentCard?> pending)
    {
        var raw = await pending.ConfigureAwait(false);
        if (raw is null) return null;
        if (_completedTenantCards.TryGetValue(raw, out var completed)) return completed;
        lock (_gate)
        {
            if (_completedTenantCards.TryGetValue(raw, out completed)) return completed;
            var canCache = CanCache;
            completed = Build(raw);
            if (canCache) _completedTenantCards.AddOrUpdate(raw, completed);
            return completed;
        }
    }

    /// <inheritdoc />
    public ValueTask<AgentCard?> GetExtendedAsync(ServerCallContext call, AgentCard publicCard, CancellationToken cancellationToken)
    {
        if (_tenants is not null) return _tenants.GetExtendedCardAsync(call, cancellationToken);
        return _options.ExtendedAgentCardProvider is { } provider ? provider(call, publicCard, cancellationToken) : default;
    }

    /// <inheritdoc />
    public ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken) =>
        _tenants?.GetSigningKeyAsync(tenant, cancellationToken) ?? default;

    private AgentCard Build(AgentCard source)
    {
        var card = JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(source), A2AJson.GetTypeInfo<AgentCard>())
            ?? throw new InvalidOperationException("The Agent Card could not be copied.");
        foreach (var extension in _extensions)
        {
            var list = card.Capabilities.Extensions ??= [];
            if (list.Exists(e => string.Equals(e.Uri, extension.ExtensionUri, StringComparison.Ordinal))) continue;
            list.Add(new AgentExtension { Uri = extension.ExtensionUri });
        }
        Complete(card);
        return card;
    }
}
