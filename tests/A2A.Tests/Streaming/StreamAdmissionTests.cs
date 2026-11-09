using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Tests.Streaming;

/// <summary>The per-caller stream cap counts across tenants, so inventing tenant names does not raise it.</summary>
public sealed class StreamAdmissionTests
{
    private static StreamAdmission Admission() => new(new A2ALimits { MaxConcurrentStreamsPerPrincipal = 2 });

    private static TaskKey Key(string tenant, string task) => new(tenant, "owner", task);

    [Fact]
    public void Cap_counts_one_caller_across_tenants()
    {
        var admission = Admission();
        using var first = admission.Enter(Key("a", "t1"), "alice");
        using var second = admission.Enter(Key("b", "t2"), "alice");

        var ex = Assert.Throws<A2AException>(() => admission.Enter(Key("c", "t3"), "alice"));
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
    }

    [Fact]
    public void Disposing_a_stream_frees_a_slot()
    {
        var admission = Admission();
        var first = admission.Enter(Key("a", "t1"), "alice");
        using var second = admission.Enter(Key("b", "t2"), "alice");
        Assert.Throws<A2AException>(() => admission.Enter(Key("c", "t3"), "alice"));

        first.Dispose();

        using var third = admission.Enter(Key("c", "t3"), "alice");
        Assert.Equal(2, admission.OpenStreams);
    }

    [Fact]
    public void Another_caller_is_not_affected()
    {
        var admission = Admission();
        using var first = admission.Enter(Key("a", "t1"), "alice");
        using var second = admission.Enter(Key("b", "t2"), "alice");

        using var other = admission.Enter(Key("c", "t3"), "bob");
        Assert.Equal(3, admission.OpenStreams);
    }
}
