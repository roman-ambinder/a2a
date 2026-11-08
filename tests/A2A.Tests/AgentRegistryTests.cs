using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Signing;
using A2A.Json;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>The peer-identity registry: duplicate names, lookalikes, rotation and downgrade (spec §3.3-3.5).</summary>
public class AgentRegistryTests
{
    private static string SourceOf(string host, string path = "") => $"https://{host}{path}/.well-known/agent-card.json";

    private static ResolvedAgentCard Card(string? name, string source, string? kid = null)
    {
        var card = SigningTests.SampleCard();
        card.Name = name!;
        var origin = new PeerIdentity(source).Origin;
        var verification = new AgentCardVerificationResult(
            kid is null ? AgentCardVerificationStatus.Unsigned : AgentCardVerificationStatus.Verified, kid, origin, null);
        return new ResolvedAgentCard(card, "{}", new Uri(source), verification);
    }

    private static AgentRegistrationException Rejected(AgentRegistry registry, ResolvedAgentCard card) =>
        Assert.Throws<AgentRegistrationException>(() => registry.Register(card));

    [Fact]
    public void Registers_and_finds_by_identity_and_name()
    {
        var registry = new AgentRegistry();
        var card = Card("Billing", SourceOf("a.example"), "k1");

        var registration = registry.Register(card);

        Assert.Equal(new PeerIdentity("https://a.example", "k1"), registration.Identity);
        Assert.Equal("Billing", registration.Name.ToString());
        Assert.Same(card, registration.Card);
        Assert.Empty(registration.LookalikeOf);
        Assert.False(registration.MixedScriptName);
        Assert.Null(registration.PreviousIdentity);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(registration.Identity, out var byIdentity));
        Assert.Same(card, byIdentity!.Card);
        Assert.True(registry.TryGetByName("billing", out var byName));
        Assert.Equal(registration.Identity, byName!.Identity);
        Assert.False(registry.TryGetByName("other", out _));
        Assert.False(registry.TryGet(new PeerIdentity("https://a.example", "k2"), out _));
        Assert.Single(registry.Agents);
    }

    [Fact]
    public void Agents_returns_a_new_snapshot_each_call()
    {
        var registry = new AgentRegistry();
        registry.Register(Card("One", SourceOf("a.example")));
        var snapshot = registry.Agents;

        registry.Register(Card("Two", SourceOf("b.example")));

        Assert.Single(snapshot);
        Assert.Equal(2, registry.Agents.Count);
    }

    [Fact]
    public void Name_is_not_part_of_identity()
    {
        var registry = new AgentRegistry();
        registry.Register(Card("Whatever", SourceOf("a.example"), "k1"));

        Assert.True(registry.TryGet(new PeerIdentity("https://a.example", "k1"), out var found));
        Assert.Equal("Whatever", found!.Name.ToString());
    }

    [Fact]
    public void Duplicate_name_from_another_identity_is_rejected()
    {
        var registry = new AgentRegistry();
        var first = registry.Register(Card("Billing", SourceOf("a.example"), "k1"));
        var second = Card("Billing", SourceOf("b.example"), "k2");

        var ex = Rejected(registry, second);

        Assert.Equal(AgentRegistrationRejection.DuplicateName, ex.Reason);
        Assert.Equal(second.Identity, ex.Identity);
        Assert.Equal(first.Identity, ex.ConflictingIdentity);
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByName("Billing", out var found));
        Assert.Equal(first.Identity, found!.Identity);
        Assert.False(registry.TryGet(second.Identity, out _));
    }

    [Fact]
    public void Duplicate_detection_uses_the_name_key()
    {
        var registry = new AgentRegistry();
        registry.Register(Card("Agent", SourceOf("a.example")));

        // "AGENT" followed by a zero-width space (U+200B).
        var ex = Rejected(registry, Card("AGENT\u200B", SourceOf("b.example")));

        Assert.Equal(AgentRegistrationRejection.DuplicateName, ex.Reason);
    }

    [Theory]
    [InlineData("\u200B")] // zero-width space only
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_name_is_rejected(string? name)
    {
        var registry = new AgentRegistry();

        var ex = Rejected(registry, Card(name, SourceOf("a.example")));

        Assert.Equal(AgentRegistrationRejection.InvalidName, ex.Reason);
        Assert.Null(ex.ConflictingIdentity);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Lookalike_name_is_registered_with_a_warning()
    {
        const string origin = "https://lookalike.example";
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        var first = registry.Register(Card("Paypal", SourceOf("honest.example")));
        var measured = new List<long>();
        using var listener = CounterListener("a2a.registry.name_lookalike", origin, measured);

        // "Paypal" with a Cyrillic a (U+0430) as the second letter.
        var second = registry.Register(Card("P\u0430ypal", SourceOf("lookalike.example")));

        Assert.Equal([first.Identity], second.LookalikeOf);
        Assert.True(second.MixedScriptName);
        Assert.Equal(2, registry.Count);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal([1L], measured);
    }

    [Fact]
    public void Numbered_names_do_not_warn()
    {
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        registry.Register(Card("Agent 1", SourceOf("a.example")));

        var second = registry.Register(Card("Agent 2", SourceOf("b.example")));

        Assert.Empty(second.LookalikeOf);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
    }

    [Fact]
    public void Mixed_script_warns_without_a_collision()
    {
        const string origin = "https://mixed.example";
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        var measured = new List<long>();
        using var listener = CounterListener("a2a.registry.name_lookalike", origin, measured);

        var registration = registry.Register(Card("P\u0430ypal", SourceOf("mixed.example"))); // Cyrillic a

        Assert.Empty(registration.LookalikeOf);
        Assert.True(registration.MixedScriptName);
        Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Empty(measured);
    }

    [Fact]
    public void Reregistering_does_not_warn_again()
    {
        const string origin = "https://again.example";
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        registry.Register(Card("Paypal", SourceOf("honest.example")));
        var card = Card("P\u0430ypal", SourceOf("again.example")); // Cyrillic a
        var measured = new List<long>();
        using var listener = CounterListener("a2a.registry.name_lookalike", origin, measured);

        var first = registry.Register(card);
        var second = registry.Register(card);

        // One lookalike warning and one mixed-script warning, both from the first call only.
        Assert.Equal(2, logger.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.Equal([1L], measured);
        Assert.Single(first.LookalikeOf);
        Assert.Single(second.LookalikeOf);
        Assert.True(second.MixedScriptName);
    }

    [Fact]
    public void Reregistering_refreshes_the_card()
    {
        var registry = new AgentRegistry();
        var source = SourceOf("a.example");
        var identity = registry.Register(Card("Agent", source, "k1")).Identity;
        var newer = Card("Agent", source, "k1");

        var registration = registry.Register(newer);

        Assert.Equal(1, registry.Count);
        Assert.Null(registration.PreviousIdentity);
        Assert.True(registry.TryGet(identity, out var found));
        Assert.Same(newer, found!.Card);
    }

    [Fact]
    public void Rename_releases_the_old_name()
    {
        var registry = new AgentRegistry();
        var source = SourceOf("a.example");
        registry.Register(Card("Old", source));

        registry.Register(Card("New", source));
        var other = registry.Register(Card("Old", SourceOf("b.example")));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGetByName("New", out var renamed));
        Assert.Equal(new PeerIdentity("https://a.example"), renamed!.Identity);
        Assert.True(registry.TryGetByName("Old", out var old));
        Assert.Equal(other.Identity, old!.Identity);
    }

    [Fact]
    public void Rename_to_a_taken_name_is_rejected_and_keeps_the_entry()
    {
        var registry = new AgentRegistry();
        var holder = registry.Register(Card("Taken", SourceOf("a.example")));
        var source = SourceOf("b.example");
        var original = Card("Mine", source);
        registry.Register(original);

        var ex = Rejected(registry, Card("Taken", source));

        Assert.Equal(AgentRegistrationRejection.DuplicateName, ex.Reason);
        Assert.Equal(holder.Identity, ex.ConflictingIdentity);
        Assert.True(registry.TryGetByName("Mine", out var mine));
        Assert.Same(original, mine!.Card);
        Assert.True(registry.TryGetByName("Taken", out var taken));
        Assert.Equal(holder.Identity, taken!.Identity);
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void Key_rotation_replaces_the_entry()
    {
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        var source = SourceOf("a.example");
        var k1 = registry.Register(Card("Agent", source, "k1")).Identity;

        var rotated = registry.Register(Card("Agent", source, "k2"));

        Assert.Equal(k1, rotated.PreviousIdentity);
        Assert.Equal(new PeerIdentity("https://a.example", "k2"), rotated.Identity);
        Assert.Empty(rotated.LookalikeOf);
        Assert.False(registry.TryGet(k1, out _));
        Assert.True(registry.TryGetByName("Agent", out var byName));
        Assert.Equal(rotated.Identity, byName!.Identity);
        Assert.Equal(1, registry.Count);
        Assert.DoesNotContain(logger.Entries, e => e.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("k2"));
    }

    [Fact]
    public void Unsigned_to_signed_upgrade_replaces_the_entry()
    {
        var registry = new AgentRegistry();
        var source = SourceOf("a.example");
        var unsigned = registry.Register(Card("Agent", source)).Identity;

        var upgraded = registry.Register(Card("Agent", source, "k1"));

        Assert.Equal(unsigned, upgraded.PreviousIdentity);
        Assert.False(registry.TryGet(unsigned, out _));
        Assert.True(registry.TryGet(upgraded.Identity, out _));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Signed_to_unsigned_downgrade_is_rejected()
    {
        var registry = new AgentRegistry();
        var source = SourceOf("a.example");
        var signed = registry.Register(Card("Agent", source, "k1"));

        var ex = Rejected(registry, Card("Agent", source));

        Assert.Equal(AgentRegistrationRejection.IdentityDowngrade, ex.Reason);
        Assert.Equal(signed.Identity, ex.ConflictingIdentity);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(signed.Identity, out var found));
        Assert.Same(signed.Card, found!.Card);
    }

    [Theory]
    [InlineData("k1")]
    [InlineData(null)]
    public void Shared_identity_at_another_source_is_rejected(string? kid)
    {
        var registry = new AgentRegistry();
        var first = registry.Register(Card("Alpha", SourceOf("h", "/a"), kid));

        var ex = Rejected(registry, Card("Beta", SourceOf("h", "/b"), kid));

        Assert.Equal(AgentRegistrationRejection.IdentityInUse, ex.Reason);
        Assert.Equal(first.Identity, ex.ConflictingIdentity);
        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByName("Alpha", out _));
        Assert.False(registry.TryGetByName("Beta", out _));
    }

    [Fact]
    public void Per_tenant_keys_are_separate_agents()
    {
        var registry = new AgentRegistry();

        registry.Register(Card("Alpha", SourceOf("h", "/a"), "k1"));
        registry.Register(Card("Beta", SourceOf("h", "/b"), "k2"));

        Assert.Equal(2, registry.Count);
        Assert.True(registry.TryGet(new PeerIdentity("https://h", "k1"), out _));
        Assert.True(registry.TryGet(new PeerIdentity("https://h", "k2"), out _));
    }

    [Fact]
    public void Registry_full_is_rejected_for_new_entries_only()
    {
        var registry = new AgentRegistry(new AgentRegistryOptions { MaxAgents = 1 });
        var first = Card("One", SourceOf("a.example"));
        registry.Register(first);
        var second = Card("Two", SourceOf("b.example"));

        var ex = Rejected(registry, second);

        Assert.Equal(AgentRegistrationRejection.RegistryFull, ex.Reason);
        Assert.Equal(1, registry.Count);
        registry.Register(first);
        Assert.True(registry.Remove(first.Identity));
        registry.Register(second);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Lookalike_list_is_capped()
    {
        var registry = new AgentRegistry();
        AgentRegistration? last = null;
        var host = 0;
        foreach (var inserted in new[] { "-", "_", ".", ":", " " })
        {
            for (var position = 1; position <= 4; position++)
            {
                var name = "agent".Insert(position, inserted);
                last = registry.Register(Card(name, SourceOf($"h{host++}.example")));
            }
        }

        Assert.Equal(20, registry.Count);
        Assert.Equal(16, last!.LookalikeOf.Count);
    }

    [Fact]
    public void Remove_frees_identity_name_and_source()
    {
        var registry = new AgentRegistry();
        var source = SourceOf("a.example");
        var first = registry.Register(Card("Agent", source, "k1"));

        Assert.True(registry.Remove(first.Identity));
        Assert.False(registry.Remove(first.Identity));

        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGet(first.Identity, out _));
        Assert.False(registry.TryGetByName("Agent", out _));
        // Name, source and identity are all free again, even as an unsigned downgrade at the same source.
        var again = registry.Register(Card("Agent", source));
        Assert.Null(again.PreviousIdentity);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Lone_surrogate_name_registers()
    {
        var registry = new AgentRegistry();

        var registration = registry.Register(Card("Agent\uD800", SourceOf("a.example")));

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGetByName("Agent\uD800", out var found));
        Assert.Equal(registration.Identity, found!.Identity);
    }

    [Fact]
    public async Task Concurrent_same_name_registrations_have_one_winner()
    {
        var registry = new AgentRegistry();
        var cards = Enumerable.Range(0, 64).Select(i => Card("Shared", SourceOf($"h{i}.example"))).ToList();
        using var gate = new ManualResetEventSlim();

        var tasks = cards.Select(card => Task.Run(() =>
        {
            gate.Wait();
            try
            {
                registry.Register(card);
                return (AgentRegistrationRejection?)null;
            }
            catch (AgentRegistrationException ex)
            {
                return ex.Reason;
            }
        })).ToList();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r is null));
        Assert.Equal(63, results.Count(r => r == AgentRegistrationRejection.DuplicateName));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Log_output_sanitizes_the_name()
    {
        var logger = new CardTrustTests.ListLogger();
        var registry = new AgentRegistry(logger: logger);
        registry.Register(Card("Paypal", SourceOf("a.example")));

        // A newline, a right-to-left override (U+202E) and a Cyrillic a (U+0430), so the name is a lookalike.
        registry.Register(Card("P\u0430ypal\n\u202Eevil", SourceOf("b.example")));
        registry.Register(Card("Paypa1\n\u202Eevil", SourceOf("c.example")));
        var ex = Rejected(registry, Card("Paypal\n\u202E", SourceOf("d.example")));

        Assert.NotEmpty(logger.Entries);
        foreach (var (_, message) in logger.Entries)
        {
            Assert.DoesNotContain('\n', message);
            Assert.DoesNotContain('\u202E', message);
        }

        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\u202E', ex.Message);
    }

    [Fact]
    public async Task End_to_end_rotation_through_the_resolver()
    {
        const string origin = "https://agent.example";
        var k1 = NewKey("k1");
        var k2 = NewKey("k2");
        var server = new CardTrustTests.CardServer(Signed(k1));
        var resolver = new A2ACardResolver(new HttpClient(server), TrustedFor(origin, k1, k2));
        var registry = new AgentRegistry();
        var url = new Uri(origin + "/");

        var first = registry.Register(await resolver.ResolveAsync(url));
        server.Json = Signed(k2);
        var rotated = registry.Register(await resolver.ResolveAsync(url));

        Assert.Equal(new PeerIdentity(origin, "k1"), first.Identity);
        Assert.Equal(new PeerIdentity(origin, "k2"), rotated.Identity);
        Assert.Equal(first.Identity, rotated.PreviousIdentity);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public async Task End_to_end_forged_name_at_another_origin_is_rejected()
    {
        var real = NewKey("real");
        var forger = NewKey("forger");
        var registry = new AgentRegistry();
        var honest = new A2ACardResolver(new HttpClient(new CardTrustTests.CardServer(Signed(real))), TrustedFor("https://honest.example", real));
        var forged = new A2ACardResolver(new HttpClient(new CardTrustTests.CardServer(Signed(forger))), TrustedFor("https://forger.example", forger));
        var registered = registry.Register(await honest.ResolveAsync(new Uri("https://honest.example/")));
        var forgedCard = await forged.ResolveAsync(new Uri("https://forger.example/"));

        var ex = Rejected(registry, forgedCard);

        Assert.Equal(AgentRegistrationRejection.DuplicateName, ex.Reason);
        Assert.Equal(registered.Identity, ex.ConflictingIdentity);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Options_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRegistry(new AgentRegistryOptions { MaxAgents = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentRegistry(new AgentRegistryOptions { MaxNameLength = 0 }));
    }

    private static AgentCardSigningKey NewKey(string kid) => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), kid, "ES256", null);

    private static string Signed(AgentCardSigningKey key) => A2AJson.Serialize(AgentCardSigner.Sign(SigningTests.SampleCard(), key));

    private static A2ACardResolverOptions TrustedFor(string origin, params AgentCardSigningKey[] keys)
    {
        var options = new A2ACardResolverOptions { DefaultCacheDuration = TimeSpan.Zero };
        options.Trust.TrustedKeys[origin] = new JwkSet { Keys = keys.Select(k => k.ToPublicJwk()).ToList() };
        return options;
    }

    private static MeterListener CounterListener(string name, string origin, List<long> measured)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == "A2A" && instrument.Name == name) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "origin" && Equals(tag.Value, origin))
                {
                    lock (measured) measured.Add(value);
                }
            }
        });
        listener.Start();
        return listener;
    }
}
