using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Text;

namespace A2A.V0_3Compat;

/// <summary>
///     Agent Card conversion between the v1.0 wire form and the v0.3 wire form, on JSON trees (so it needs no model of
///     v0.3 and
///     never touches the deprecated flow properties). The same code serves the public card and the extended card, in both
///     directions. It must not lose security: every scheme type, flow, scope and requirement (card and skill) crosses
///     over, and a
///     scheme with no flow left stays listed, so a client cannot read it as anonymous access.
/// </summary>
internal static class V03CardCodec
{
    private static readonly string[] V1SchemeMembers =
    [
        "apiKeySecurityScheme", "httpAuthSecurityScheme", "oauth2SecurityScheme", "openIdConnectSecurityScheme",
        "mtlsSecurityScheme"
    ];

    // ================= v1.0 -> v0.3 =================

    /// <summary>
    ///     Builds the pure v0.3 card from a v1.0 card (wire form). Returns false, with a reason, when the card has no JSON-RPC
    ///     or
    ///     HTTP+JSON interface (v0.3 needs a <c>url</c>); gRPC is never listed.
    /// </summary>
    /// <param name="v1">The v1.0 card.</param>
    /// <param name="pushNotifications">
    ///     When set, replaces <c>capabilities.pushNotifications</c> (the v0.3-only extended card
    ///     says false).
    /// </param>
    /// <param name="v03">The v0.3 card.</param>
    /// <param name="problem">Why no card was built.</param>
    public static bool TryToV03(JsonObject v1, bool? pushNotifications, out JsonObject v03, out string? problem)
    {
        v03 = new JsonObject();
        var interfaces = V03Interfaces(v1);
        if (interfaces.Count == 0)
        {
            problem = "the card lists no JSONRPC or HTTP+JSON interface, so no v0.3 fields were added";
            return false;
        }

        problem = null;

        v03["protocolVersion"] = V03Compat.CardProtocolVersion;
        v03["name"] = V03Json.Str(v1, "name") ?? "";
        v03["description"] = V03Json.Str(v1, "description") ?? "";
        v03["url"] = interfaces[0].Url;
        v03["preferredTransport"] = interfaces[0].Transport;
        var additional = new JsonArray();
        foreach (var (url, transport) in interfaces)
            V03Json.Append(additional, new JsonObject { ["url"] = url, ["transport"] = transport });
        v03["additionalInterfaces"] = additional;
        if (v1["provider"] is JsonObject provider) v03["provider"] = provider.DeepClone();
        v03["version"] = V03Json.Str(v1, "version") ?? "";
        CopyString(v1, v03, "documentationUrl");
        CopyString(v1, v03, "iconUrl");

        var capabilities = new JsonObject();
        var v1Capabilities = v1["capabilities"] as JsonObject;
        if (v1Capabilities?["streaming"] is { } streaming) capabilities["streaming"] = streaming.DeepClone();
        if (pushNotifications is { } push) capabilities["pushNotifications"] = push;
        else if (v1Capabilities?["pushNotifications"] is { } shared)
            capabilities["pushNotifications"] = shared.DeepClone();
        if (v1Capabilities?["extensions"] is JsonArray extensions) capabilities["extensions"] = extensions.DeepClone();
        v03["capabilities"] = capabilities;

        if (v1["securitySchemes"] is JsonObject schemes)
        {
            var flat = new JsonObject();
            foreach (var (name, scheme) in schemes)
                if (scheme is JsonObject s && SchemeToV03(name, s) is { } converted)
                    flat[name] = converted;
            v03["securitySchemes"] = flat;
        }

        if (v1["securityRequirements"] is JsonArray requirements) v03["security"] = RequirementsToV03(requirements);

        v03["defaultInputModes"] = (v1["defaultInputModes"] as JsonArray)?.DeepClone() ?? new JsonArray();
        v03["defaultOutputModes"] = (v1["defaultOutputModes"] as JsonArray)?.DeepClone() ?? new JsonArray();

        var skills = new JsonArray();
        if (v1["skills"] is JsonArray v1Skills)
            foreach (var item in v1Skills)
            {
                var skill = item as JsonObject ?? new JsonObject();
                var o = new JsonObject
                {
                    ["id"] = V03Json.Str(skill, "id") ?? "",
                    ["name"] = V03Json.Str(skill, "name") ?? "",
                    ["description"] = V03Json.Str(skill, "description") ?? "",
                    ["tags"] = (skill["tags"] as JsonArray)?.DeepClone() ?? new JsonArray()
                };
                if (skill["examples"] is JsonArray examples) o["examples"] = examples.DeepClone();
                if (skill["inputModes"] is JsonArray inputModes) o["inputModes"] = inputModes.DeepClone();
                if (skill["outputModes"] is JsonArray outputModes) o["outputModes"] = outputModes.DeepClone();
                if (skill["securityRequirements"] is JsonArray skillSecurity)
                    o["security"] = RequirementsToV03(skillSecurity);
                V03Json.Append(skills, o);
            }

        v03["skills"] = skills;

        if (v1Capabilities?["extendedAgentCard"] is { } extended)
            v03["supportsAuthenticatedExtendedCard"] = extended.DeepClone();
        if (v1["signatures"] is JsonArray signatures) v03["signatures"] = signatures.DeepClone();
        return true;
    }

    /// <summary>
    ///     The one blended document a v0.3-compatible server serves at the well-known path: the v1.0 card plus the v0.3
    ///     members.
    ///     The v1.0 members stay as they are, so a v1.0 client (and its signature check) reads the card as before. Each
    ///     <c>securitySchemes</c> entry gains the flat v0.3 members next to the v1.0 wrapper, each skill gains <c>security</c>
    ///     .
    ///     Without a JSON-RPC or HTTP+JSON interface nothing is added and a warning is logged once.
    /// </summary>
    public static JsonObject Blend(JsonObject v1)
    {
        if (!TryToV03(v1, null, out var v03, out var problem))
        {
            V03LossReporter.CardProblem(problem!);
            return v1;
        }

        foreach (var (name, value) in v03)
        {
            if (value is null) continue;
            switch (name)
            {
                case "securitySchemes" when v1["securitySchemes"] is JsonObject target:
                    foreach (var (schemeName, flat) in (JsonObject)value)
                        if (flat is JsonObject flatMembers && target[schemeName] is JsonObject wrapper)
                            foreach (var (member, memberValue) in flatMembers)
                                if (wrapper[member] is null && memberValue is not null)
                                    wrapper[member] = memberValue.DeepClone();

                    break;
                case "skills" when v1["skills"] is JsonArray targetSkills:
                    var converted = (JsonArray)value;
                    for (var i = 0; i < targetSkills.Count && i < converted.Count; i++)
                        if (targetSkills[i] is JsonObject skill && converted[i] is JsonObject c &&
                            c["security"] is { } security && skill["security"] is null)
                            skill["security"] = security.DeepClone();
                    break;
                case "capabilities":
                    break; // shared members only
                default:
                    if (v1[name] is null) v1[name] = value.DeepClone();
                    break;
            }
        }

        return v1;
    }

    private static List<(string Url, string Transport)> V03Interfaces(JsonObject v1)
    {
        var list = new List<(string, string)>();
        if (v1["supportedInterfaces"] is not JsonArray interfaces) return list;
        foreach (var item in interfaces)
        {
            if (item is not JsonObject itf) continue;
            var binding = V03Json.Str(itf, "protocolBinding");
            var url = V03Json.Str(itf, "url");
            if (binding is not (ProtocolBindings.JsonRpc or ProtocolBindings.HttpJson) ||
                string.IsNullOrEmpty(url)) continue;
            var tenant = V03Json.Str(itf, "tenant");
            if (!string.IsNullOrEmpty(tenant)) url = url.TrimEnd('/') + "/" + Uri.EscapeDataString(tenant);
            if (!list.Contains((url, binding))) list.Add((url, binding));
        }

        return list;
    }

    private static JsonObject? SchemeToV03(string name, JsonObject scheme)
    {
        var o = new JsonObject();
        if (scheme["apiKeySecurityScheme"] is JsonObject apiKey)
        {
            o["type"] = "apiKey";
            o["in"] = V03Json.Str(apiKey, "location") ?? "header";
            o["name"] = V03Json.Str(apiKey, "name") ?? "";
            CopyString(apiKey, o, "description");
        }
        else if (scheme["httpAuthSecurityScheme"] is JsonObject http)
        {
            o["type"] = "http";
            o["scheme"] = V03Json.Str(http, "scheme") ?? "Bearer";
            CopyString(http, o, "bearerFormat");
            CopyString(http, o, "description");
        }
        else if (scheme["oauth2SecurityScheme"] is JsonObject oauth)
        {
            o["type"] = "oauth2";
            o["flows"] = FlowsToV03(oauth["flows"] as JsonObject);
            CopyString(oauth, o, "oauth2MetadataUrl");
            CopyString(oauth, o, "description");
        }
        else if (scheme["openIdConnectSecurityScheme"] is JsonObject oidc)
        {
            o["type"] = "openIdConnect";
            o["openIdConnectUrl"] = V03Json.Str(oidc, "openIdConnectUrl") ?? "";
            CopyString(oidc, o, "description");
        }
        else if (scheme["mtlsSecurityScheme"] is JsonObject mtls)
        {
            o["type"] = "mutualTLS";
            CopyString(mtls, o, "description");
        }
        else
        {
            V03LossReporter.CardProblem($"security scheme '{name}' has no known type and was left out");
            return null;
        }

        return o;
    }

    private static JsonObject FlowsToV03(JsonObject? flows)
    {
        // A scheme with no usable flow keeps an empty "flows": it must stay listed (and fail), never read as open access.
        var o = new JsonObject();
        if (flows is null) return o;
        if (flows["authorizationCode"] is JsonObject code)
        {
            if (V03Json.Bool(code, "pkceRequired") == true) V03LossReporter.Drop("oauth.pkceRequired");
            o["authorizationCode"] = Flow(code, "authorizationUrl", "tokenUrl");
        }

        if (flows["clientCredentials"] is JsonObject clientCredentials)
            o["clientCredentials"] = Flow(clientCredentials, "tokenUrl");
        if (flows["implicit"] is JsonObject implicitFlow) o["implicit"] = Flow(implicitFlow, "authorizationUrl");
        if (flows["password"] is JsonObject password) o["password"] = Flow(password, "tokenUrl");
        if (flows["deviceCode"] is not null) V03LossReporter.Drop("oauth.deviceCode");
        return o;
    }

    private static JsonObject Flow(JsonObject source, params string[] urls)
    {
        var o = new JsonObject();
        foreach (var url in urls) o[url] = V03Json.Str(source, url) ?? "";
        CopyString(source, o, "refreshUrl");
        o["scopes"] = (source["scopes"] as JsonObject)?.DeepClone() ?? new JsonObject();
        return o;
    }

    private static JsonArray RequirementsToV03(JsonArray requirements)
    {
        var result = new JsonArray();
        foreach (var item in requirements)
        {
            var entry = new JsonObject();
            if (item is JsonObject { } requirement && requirement["schemes"] is JsonObject schemes)
                foreach (var (name, scopes) in schemes)
                    entry[name] = (scopes is JsonObject s ? s["list"] as JsonArray : null)?.DeepClone() ??
                                  new JsonArray();
            V03Json.Append(result, entry);
        }

        return result;
    }

    // ================= v0.3 -> v1.0 =================

    /// <summary>
    ///     Converts a v0.3 card to the v1.0 wire form. Its interfaces carry <c>protocolVersion</c> <c>0.3</c>. Signatures are
    ///     not
    ///     carried: they cover v0.3 content, so the converted card is unsigned. Security is converted in full.
    /// </summary>
    /// <exception cref="JsonException">The card is malformed or has a security scheme of an unknown type.</exception>
    public static JsonObject ToV1(JsonObject v03)
    {
        var v1 = new JsonObject
        {
            ["name"] = V03Json.Str(v03, "name") ?? "",
            ["description"] = V03Json.Str(v03, "description") ?? ""
        };

        var interfaces = new JsonArray();
        var seen = new HashSet<(string, string)>();

        void Add(string? url, string? transport)
        {
            if (string.IsNullOrEmpty(url)) return;
            var binding = NormalizeBinding(transport ?? ProtocolBindings.JsonRpc);
            if (!seen.Add((url, binding))) return;
            V03Json.Append(interfaces,
                new JsonObject
                    { ["url"] = url, ["protocolBinding"] = binding, ["protocolVersion"] = V03Compat.InterfaceVersion });
        }

        Add(V03Json.Str(v03, "url"), V03Json.Str(v03, "preferredTransport"));
        if (V03Json.Arr(v03, "additionalInterfaces") is { } additional)
            foreach (var item in additional)
            {
                var itf = V03Json.Obj(item, "additionalInterfaces entry");
                Add(V03Json.Str(itf, "url"), V03Json.Str(itf, "transport"));
            }

        v1["supportedInterfaces"] = interfaces;

        if (v03["provider"] is JsonObject provider) v1["provider"] = provider.DeepClone();
        v1["version"] = V03Json.Str(v03, "version") ?? "";
        CopyString(v03, v1, "documentationUrl");
        CopyString(v03, v1, "iconUrl");

        var capabilities = new JsonObject();
        if (V03Json.OptObj(v03, "capabilities") is { } v03Capabilities)
        {
            if (v03Capabilities["streaming"] is { } streaming) capabilities["streaming"] = streaming.DeepClone();
            if (v03Capabilities["pushNotifications"] is { } push) capabilities["pushNotifications"] = push.DeepClone();
            if (v03Capabilities["extensions"] is JsonArray extensions)
                capabilities["extensions"] = extensions.DeepClone();
            if (V03Json.Bool(v03Capabilities, "stateTransitionHistory") is not null)
                V03LossReporter.Drop("capabilities.stateTransitionHistory");
        }

        if (V03Json.Bool(v03, "supportsAuthenticatedExtendedCard") is { } extended)
            capabilities["extendedAgentCard"] = extended;
        v1["capabilities"] = capabilities;

        if (V03Json.OptObj(v03, "securitySchemes") is { } schemes)
        {
            var wrapped = new JsonObject();
            foreach (var (name, scheme) in schemes)
                wrapped[name] = SchemeToV1(name, V03Json.Obj(scheme, $"security scheme '{name}'"));
            v1["securitySchemes"] = wrapped;
        }

        if (V03Json.Arr(v03, "security") is { } security) v1["securityRequirements"] = RequirementsToV1(security);

        v1["defaultInputModes"] = (v03["defaultInputModes"] as JsonArray)?.DeepClone() ?? new JsonArray();
        v1["defaultOutputModes"] = (v03["defaultOutputModes"] as JsonArray)?.DeepClone() ?? new JsonArray();

        var skills = new JsonArray();
        if (V03Json.Arr(v03, "skills") is { } v03Skills)
            foreach (var item in v03Skills)
            {
                var skill = V03Json.Obj(item, "skill");
                var o = new JsonObject
                {
                    ["id"] = V03Json.Str(skill, "id") ?? "",
                    ["name"] = V03Json.Str(skill, "name") ?? "",
                    ["description"] = V03Json.Str(skill, "description") ?? "",
                    ["tags"] = (skill["tags"] as JsonArray)?.DeepClone() ?? new JsonArray()
                };
                if (skill["examples"] is JsonArray examples) o["examples"] = examples.DeepClone();
                if (skill["inputModes"] is JsonArray inputModes) o["inputModes"] = inputModes.DeepClone();
                if (skill["outputModes"] is JsonArray outputModes) o["outputModes"] = outputModes.DeepClone();
                if (V03Json.Arr(skill, "security") is { } skillSecurity)
                    o["securityRequirements"] = RequirementsToV1(skillSecurity);
                V03Json.Append(skills, o);
            }

        v1["skills"] = skills;

        if (v03["signatures"] is not null) V03LossReporter.Drop("card.signatures");
        return v1;
    }

    /// <summary>The v1.0 model of a v0.3 card. See <see cref="ToV1" />.</summary>
    public static AgentCard FromV03(JsonObject v03)
    {
        return A2AJson.Deserialize<AgentCard>(ToV1(v03).ToJsonString());
    }

    /// <summary>
    ///     The card resolver seam of the client: turns a v0.3-only card (a top-level <c>protocolVersion</c> <c>0.3.x</c> and
    ///     no
    ///     <c>supportedInterfaces</c>) into a v1.0 card, before the model is read and before the signature is checked. Without
    ///     <c>signatures</c> the card reads as unsigned, so a client that requires signatures refuses it. A card that has
    ///     <c>supportedInterfaces</c> (v1.0, or the blended card) is returned as it is. Other <c>0.x</c> versions are refused.
    /// </summary>
    /// <exception cref="A2AException">The card is v0.2 or older.</exception>
    /// <exception cref="JsonException">The v0.3 card is malformed.</exception>
    public static JsonObject NormalizeForResolver(JsonObject card)
    {
        if (card["supportedInterfaces"] is JsonArray { Count: > 0 }) return card;
        var version = V03Json.Str(card, "protocolVersion");
        if (version is null) return card;
        if (V03Compat.IsV03Version(version)) return ToV1(card);
        if (version.StartsWith("0.", StringComparison.Ordinal))
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The Agent Card is protocol version '{TextSanitizer.ForMessage(version)}'. Only 0.3.x and 1.x are supported.");
        return card;
    }

    private static string NormalizeBinding(string transport)
    {
        return string.Equals(transport, ProtocolBindings.JsonRpc, StringComparison.OrdinalIgnoreCase)
            ? ProtocolBindings.JsonRpc
            : string.Equals(transport, ProtocolBindings.Grpc, StringComparison.OrdinalIgnoreCase)
                ? ProtocolBindings.Grpc
                : string.Equals(transport, ProtocolBindings.HttpJson, StringComparison.OrdinalIgnoreCase)
                    ? ProtocolBindings.HttpJson
                    : transport;
    }

    private static JsonObject SchemeToV1(string name, JsonObject scheme)
    {
        var type = V03Json.Str(scheme, "type");
        var inner = new JsonObject();
        CopyString(scheme, inner, "description");
        switch (type)
        {
            case "apiKey":
                inner["location"] = V03Json.Str(scheme, "in") ?? "header";
                inner["name"] = V03Json.Str(scheme, "name") ?? "";
                return new JsonObject { ["apiKeySecurityScheme"] = inner };
            case "http":
                inner["scheme"] = V03Json.Str(scheme, "scheme") ?? "Bearer";
                CopyString(scheme, inner, "bearerFormat");
                return new JsonObject { ["httpAuthSecurityScheme"] = inner };
            case "oauth2":
                inner["flows"] = FlowsToV1(V03Json.OptObj(scheme, "flows"));
                CopyString(scheme, inner, "oauth2MetadataUrl");
                return new JsonObject { ["oauth2SecurityScheme"] = inner };
            case "openIdConnect":
                inner["openIdConnectUrl"] = V03Json.Str(scheme, "openIdConnectUrl") ?? "";
                return new JsonObject { ["openIdConnectSecurityScheme"] = inner };
            case "mutualTLS":
                return new JsonObject { ["mtlsSecurityScheme"] = inner };
            default:
                // Dropping it would let the card read as open access. Refuse the card instead.
                throw new JsonException($"security scheme '{name}' has unknown type '{type}'");
        }
    }

    private static JsonObject FlowsToV1(JsonObject? flows)
    {
        var o = new JsonObject();
        if (flows is null) return o;
        foreach (var name in new[] { "authorizationCode", "clientCredentials", "implicit", "password" })
        {
            if (V03Json.OptObj(flows, name) is not { } flow) continue;
            var copy = new JsonObject();
            foreach (var key in new[] { "authorizationUrl", "tokenUrl", "refreshUrl" }) CopyString(flow, copy, key);
            copy["scopes"] = (flow["scopes"] as JsonObject)?.DeepClone() ?? new JsonObject();
            o[name] = copy;
        }

        return o;
    }

    private static JsonArray RequirementsToV1(JsonArray security)
    {
        var result = new JsonArray();
        foreach (var item in security)
        {
            var entry = V03Json.Obj(item, "security entry");
            var schemes = new JsonObject();
            foreach (var (name, scopes) in entry)
                schemes[name] = new JsonObject { ["list"] = (scopes as JsonArray)?.DeepClone() ?? new JsonArray() };
            V03Json.Append(result, new JsonObject { ["schemes"] = schemes });
        }

        return result;
    }

    private static void CopyString(JsonObject from, JsonObject to, string name)
    {
        if (V03Json.Str(from, name) is { } value) to[name] = value;
    }
}