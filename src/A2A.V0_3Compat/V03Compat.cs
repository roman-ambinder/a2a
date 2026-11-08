using System.Reflection;

namespace A2A.V0_3Compat;

/// <summary>Constants of the v0.3 protocol and the startup version check shared by the compat packages.</summary>
internal static class V03Compat
{
    /// <summary>Value of <c>protocolVersion</c> the SDK writes in a v0.3 card.</summary>
    public const string CardProtocolVersion = "0.3.0";

    /// <summary>Value of the <c>A2A-Version</c> header a v0.3 client sends.</summary>
    public const string HeaderVersion = "0.3";

    /// <summary>The <c>protocolVersion</c> the SDK marks converted interfaces with.</summary>
    public const string InterfaceVersion = "0.3";

    /// <summary>The v0.3 header that carries extensions (v1.0 calls it <c>A2A-Extensions</c>).</summary>
    public const string ExtensionsHeader = "X-A2A-Extensions";

    /// <summary>v0.3 JSON-RPC method names.</summary>
    public static class Methods
    {
        public const string SendMessage = "message/send";
        public const string SendStreamingMessage = "message/stream";
        public const string GetTask = "tasks/get";
        public const string CancelTask = "tasks/cancel";
        public const string ResubscribeTask = "tasks/resubscribe";
        public const string SetPushConfig = "tasks/pushNotificationConfig/set";
        public const string GetPushConfig = "tasks/pushNotificationConfig/get";
        public const string ListPushConfig = "tasks/pushNotificationConfig/list";
        public const string DeletePushConfig = "tasks/pushNotificationConfig/delete";
        public const string GetExtendedCard = "agent/getAuthenticatedExtendedCard";
    }

    /// <summary>True when <paramref name="version"/> (an <c>A2A-Version</c> value) names protocol 1.0 or later. Unparseable values are not.</summary>
    public static bool IsV1OrLater(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        var span = version.AsSpan().Trim();
        var dot = span.IndexOf('.');
        var major = dot < 0 ? span : span[..dot];
        return int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var m) && m >= 1;
    }

    /// <summary>True for a card <c>protocolVersion</c> of <c>0.3</c> or <c>0.3.x</c>.</summary>
    public static bool IsV03Version(string? version) =>
        version is not null && (version == "0.3" || version.StartsWith("0.3.", StringComparison.Ordinal));

    /// <summary>
    /// The compat packages use core internals, so they must come from the same build as the core packages. Throws
    /// <see cref="InvalidOperationException"/> when the assembly versions differ.
    /// </summary>
    /// <param name="compat">An assembly of the compat family (the caller's).</param>
    /// <param name="core">Core assemblies it was built against.</param>
    public static void RequireSameVersion(Assembly compat, params Assembly[] core)
    {
        var expected = compat.GetName().Version;
        foreach (var assembly in core)
        {
            var actual = assembly.GetName().Version;
            if (actual != expected)
                throw new InvalidOperationException(
                    $"{compat.GetName().Name} {expected} needs the same version of {assembly.GetName().Name}, but {actual} is loaded. " +
                    "Update all A2A packages (RomanAmbinder.A2A.*) to the same version.");
        }
    }
}
