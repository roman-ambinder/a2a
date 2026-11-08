using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using A2A.Client.Cards;
using A2A.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Client.Peers;

/// <summary>Limits of an <see cref="AgentRegistry"/>.</summary>
public sealed class AgentRegistryOptions
{
    /// <summary>Most agents the registry holds. Default 10,000. Must be at least 1.</summary>
    public int MaxAgents { get; set; } = 10_000;

    /// <summary>Longest agent name that is compared, in characters. Default 256. Must be at least 1.</summary>
    public int MaxNameLength { get; set; } = 256;
}

/// <summary>One registered agent.</summary>
/// <param name="Identity">Who the peer is.</param>
/// <param name="Name">The card name, for display only.</param>
/// <param name="Card">The resolved card.</param>
public sealed record AgentRegistration(PeerIdentity Identity, UntrustedText Name, ResolvedAgentCard Card)
{
    /// <summary>Other identities whose names look like this one (at most 16). Set when the entry was registered.</summary>
    public IReadOnlyList<PeerIdentity> LookalikeOf { get; init; } = [];

    /// <summary>The name mixes Latin, Greek or Cyrillic letters in one word.</summary>
    public bool MixedScriptName { get; init; }

    /// <summary>The identity this entry replaced at the same card URL (key rotation or upgrade), or <see langword="null"/>.</summary>
    public PeerIdentity? PreviousIdentity { get; init; }
}

/// <summary>
/// In-memory, thread-safe registry of peers keyed by <see cref="PeerIdentity"/>. It rejects a second agent with the same name
/// (first registered wins) and warns about lookalike names; see <see cref="AgentRegistration.LookalikeOf"/>. A new key at the
/// same card URL replaces the old entry, so key rotation does not break hosts. A signed agent served unsigned is refused.
/// </summary>
/// <remarks>
/// <para>
/// Two agents on one origin that share a key (or have none) have the same identity. The registry cannot tell them apart and
/// refuses the second one with <see cref="AgentRegistrationRejection.IdentityInUse"/>. Give each tenant its own signing key.
/// </para>
/// <para>
/// Name comparison uses <c>string.Normalize</c>. With <c>InvariantGlobalization</c> it does nothing, so compatibility forms
/// other than fullwidth ASCII are not folded.
/// </para>
/// <para>
/// <see cref="TryGetByName"/> is safe only because names are unique here. Check <see cref="AgentRegistration.Identity"/> before
/// sending credentials.
/// </para>
/// </remarks>
public sealed class AgentRegistry
{
    private const int MaxLookalikes = 16;

    private static readonly Meter Meter = new("A2A");
    private static readonly Counter<long> LookalikeCounter =
        Meter.CreateCounter<long>("a2a.registry.name_lookalike", description: "Agent names that look like the name of another registered agent.");

    private sealed class Entry(AgentRegistration registration, string nameKey, string skeleton)
    {
        public AgentRegistration Registration { get; } = registration;
        public string NameKey { get; } = nameKey;
        public string Skeleton { get; } = skeleton;
        public PeerIdentity Identity => Registration.Identity;
        public Uri Source => Registration.Card.Source;
    }

    private readonly object _gate = new();
    private readonly Dictionary<PeerIdentity, Entry> _byIdentity = [];
    private readonly Dictionary<Uri, Entry> _bySource = [];
    private readonly Dictionary<string, Entry> _byName = [];
    private readonly Dictionary<string, List<Entry>> _bySkeleton = [];
    private readonly int _maxAgents;
    private readonly int _maxNameLength;
    private readonly ILogger _logger;

    /// <summary>Creates a registry.</summary>
    /// <param name="options">Limits; defaults are used when <see langword="null"/>.</param>
    /// <param name="logger">Receives warnings about lookalike names and identity changes.</param>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="AgentRegistryOptions.MaxAgents"/> or <see cref="AgentRegistryOptions.MaxNameLength"/> is below 1.</exception>
    public AgentRegistry(AgentRegistryOptions? options = null, ILogger? logger = null)
    {
        options ??= new AgentRegistryOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAgents, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxNameLength, 1);
        _maxAgents = options.MaxAgents;
        _maxNameLength = options.MaxNameLength;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Number of registered agents.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _byIdentity.Count;
        }
    }

    /// <summary>A snapshot of the registered agents.</summary>
    public IReadOnlyList<AgentRegistration> Agents
    {
        get
        {
            lock (_gate) return _byIdentity.Values.Select(e => e.Registration).ToList();
        }
    }

    /// <summary>
    /// Adds or updates the agent behind <paramref name="resolved"/>, keyed by <see cref="ResolvedAgentCard.Identity"/>. A refused
    /// call leaves the registry unchanged.
    /// </summary>
    /// <returns>The stored registration.</returns>
    /// <exception cref="AgentRegistrationException">The card is refused; see <see cref="AgentRegistrationException.Reason"/>.</exception>
    public AgentRegistration Register(ResolvedAgentCard resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var identity = resolved.Identity;
        var source = resolved.Source;
        var rawName = resolved.Card.Name;
        var shownName = TextSanitizer.ForMessage(rawName);
        var nameKey = AgentNameNormalizer.NameKey(rawName, _maxNameLength);
        if (nameKey.Length == 0)
        {
            throw new AgentRegistrationException(AgentRegistrationRejection.InvalidName, identity, null,
                $"Agent name from {identity} is empty.");
        }

        var skeleton = AgentNameNormalizer.Skeleton(rawName, _maxNameLength);
        var mixedScript = AgentNameNormalizer.IsMixedScript(rawName, _maxNameLength);
        var name = new UntrustedText(rawName, _maxNameLength);

        AgentRegistration registration;
        Entry? replaced;
        lock (_gate)
        {
            _byIdentity.TryGetValue(identity, out var sameIdentity);
            _bySource.TryGetValue(source, out replaced);

            if (sameIdentity is not null && sameIdentity.Source != source)
            {
                throw new AgentRegistrationException(AgentRegistrationRejection.IdentityInUse, identity, sameIdentity.Identity,
                    $"Identity {identity} is already registered for another card URL. Use a separate signing key per agent.");
            }

            if (replaced is not null && replaced.Identity != identity && identity.Assurance < replaced.Identity.Assurance)
            {
                throw new AgentRegistrationException(AgentRegistrationRejection.IdentityDowngrade, identity, replaced.Identity,
                    $"Agent at {TextSanitizer.ForMessage(source.AbsoluteUri)} was registered as {replaced.Identity} and is now {identity}.");
            }

            if (_byName.TryGetValue(nameKey, out var holder) && !ReferenceEquals(holder, replaced))
            {
                throw new AgentRegistrationException(AgentRegistrationRejection.DuplicateName, identity, holder.Identity,
                    $"Agent name '{shownName}' from {identity} is already used by {holder.Identity}.");
            }

            if (replaced is null && _byIdentity.Count >= _maxAgents)
            {
                throw new AgentRegistrationException(AgentRegistrationRejection.RegistryFull, identity, null,
                    $"The registry holds {_maxAgents} agents; {identity} was not added.");
            }

            var lookalikes = skeleton.Length == 0 || !_bySkeleton.TryGetValue(skeleton, out var sameSkeleton)
                ? []
                : sameSkeleton.Where(e => !ReferenceEquals(e, replaced)).Take(MaxLookalikes).Select(e => e.Identity).ToList();
            registration = new AgentRegistration(identity, name, resolved)
            {
                LookalikeOf = lookalikes,
                MixedScriptName = mixedScript,
                PreviousIdentity = replaced is not null && replaced.Identity != identity ? replaced.Identity : null,
            };

            if (replaced is not null) Unindex(replaced);
            Index(new Entry(registration, nameKey, skeleton));
        }

        // Logging and counting stay outside the lock. A refresh of an unchanged name stays quiet.
        var identityText = identity.ToString();
        if (replaced is not null && replaced.Identity != identity)
        {
            var sourceText = TextSanitizer.ForMessage(source.AbsoluteUri);
            var oldText = replaced.Identity.ToString();
            ClientLog.AgentIdentityChanged(_logger, sourceText, oldText, identityText);
        }

        if (replaced is null || replaced.Skeleton != skeleton)
        {
            if (registration.LookalikeOf.Count > 0)
            {
                ClientLog.AgentNameLookalike(_logger, shownName, identityText, registration.LookalikeOf.Count);
                LookalikeCounter.Add(1, new KeyValuePair<string, object?>("origin", identity.Origin));
            }

            if (mixedScript)
            {
                ClientLog.AgentMixedScriptName(_logger, shownName, identityText);
            }
        }

        return registration;
    }

    /// <summary>Finds the agent registered under <paramref name="identity"/>.</summary>
    public bool TryGet(PeerIdentity identity, [NotNullWhen(true)] out AgentRegistration? registration)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            var found = _byIdentity.TryGetValue(identity, out var entry);
            registration = entry?.Registration;
            return found;
        }
    }

    /// <summary>Finds the agent whose name matches <paramref name="name"/> by name key (case, width and spacing are ignored).</summary>
    public bool TryGetByName(string name, [NotNullWhen(true)] out AgentRegistration? registration)
    {
        var key = AgentNameNormalizer.NameKey(name, _maxNameLength);
        lock (_gate)
        {
            var found = key.Length > 0 && _byName.TryGetValue(key, out var entry);
            registration = found ? _byName[key].Registration : null;
            return found;
        }
    }

    /// <summary>Removes the agent registered under <paramref name="identity"/>. Returns <see langword="false"/> when there is none.</summary>
    public bool Remove(PeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (!_byIdentity.TryGetValue(identity, out var entry)) return false;
            Unindex(entry);
            return true;
        }
    }

    private void Index(Entry entry)
    {
        _byIdentity[entry.Identity] = entry;
        _bySource[entry.Source] = entry;
        _byName[entry.NameKey] = entry;
        if (entry.Skeleton.Length == 0) return;
        if (!_bySkeleton.TryGetValue(entry.Skeleton, out var list)) _bySkeleton[entry.Skeleton] = list = [];
        list.Add(entry);
    }

    private void Unindex(Entry entry)
    {
        _byIdentity.Remove(entry.Identity);
        _bySource.Remove(entry.Source);
        _byName.Remove(entry.NameKey);
        if (entry.Skeleton.Length == 0 || !_bySkeleton.TryGetValue(entry.Skeleton, out var list)) return;
        list.Remove(entry);
        if (list.Count == 0) _bySkeleton.Remove(entry.Skeleton);
    }
}
