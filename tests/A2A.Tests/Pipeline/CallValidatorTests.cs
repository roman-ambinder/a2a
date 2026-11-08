using A2A.Server;
using A2A.Server.Pipeline;

namespace A2A.Tests.Pipeline;

public sealed class CallValidatorTests
{
    private static A2AServerOptions Options(Action<A2AServerOptions>? configure = null)
    {
        var options = new A2AServerOptions { AllowAnonymous = true };
        options.AgentCard.Name = "Validator Test Agent";
        configure?.Invoke(options);
        return options;
    }

    private static CallValidator Validator(A2AServerOptions options) => new(options, new AgentCardProvider(options, []));

    [Fact]
    public void Missing_version_is_rejected_unless_TreatMissingVersionAsCurrent()
    {
        var strict = Validator(Options());
        var ex = Assert.Throws<A2AException>(() => strict.Validate(ServerCallContext.Anonymous(protocolVersion: null)));
        Assert.Equal(A2AErrorCode.VersionNotSupported, ex.Code);

        var lenient = Validator(Options(o => o.TreatMissingVersionAsCurrent = true));
        lenient.Validate(ServerCallContext.Anonymous(protocolVersion: null));
    }

    [Fact]
    public void Patch_version_is_reduced_to_major_minor()
    {
        Validator(Options()).Validate(ServerCallContext.Anonymous(A2AProtocol.Version + ".7"));
    }

    [Fact]
    public void Required_unknown_extension_is_rejected()
    {
        var validator = Validator(Options(o =>
            o.AgentCard.Capabilities.Extensions = [new AgentExtension { Uri = "urn:must", Required = true }]));
        var ex = Assert.Throws<A2AException>(() => validator.Validate(ServerCallContext.Anonymous()));
        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, ex.Code);
    }

    [Fact]
    public void Declared_extension_is_activated()
    {
        var validator = Validator(Options(o =>
            o.AgentCard.Capabilities.Extensions = [new AgentExtension { Uri = "urn:opt" }]));
        var call = new ServerCallContext { ProtocolVersion = A2AProtocol.Version, RequestedExtensions = ["urn:opt", "urn:unknown"] };
        validator.Validate(call);
        Assert.Equal(["urn:opt"], call.ActivatedExtensions);
    }

    [Fact]
    public void Anonymous_caller_is_rejected_unless_allowed()
    {
        var validator = Validator(Options(o => o.AllowAnonymous = false));
        var ex = Assert.Throws<A2AException>(() => validator.Validate(ServerCallContext.Anonymous()));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
    }
}
