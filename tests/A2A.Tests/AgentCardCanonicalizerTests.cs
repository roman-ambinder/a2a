using System.Text;
using System.Text.Json.Nodes;
using A2A.Signing;

namespace A2A.Tests;

/// <summary>
///     <see cref="AgentCardCanonicalizer" />: which members of a card are signed, and the paths it reports for what it
///     removes. The cross-SDK byte vectors are in <see cref="CanonicalizationVectorTests" />.
/// </summary>
public class AgentCardCanonicalizerTests
{
    // Every member of the A2A v1.0 AgentCard schema (lf.a2a.v1), each with a value that is not empty, so none may be
    // removed. A schema entry with a wrong or missing name would drop its member from the signed content.
    private const string FullCard = """
        {
          "name": "Full Agent",
          "description": "Uses every field.",
          "supportedInterfaces": [
            { "url": "https://agent.example/a2a", "protocolBinding": "JSONRPC", "tenant": "t1", "protocolVersion": "1.0" }
          ],
          "provider": { "url": "https://provider.example", "organization": "Example Org" },
          "version": "1.2.3",
          "documentationUrl": "https://agent.example/docs",
          "capabilities": {
            "streaming": true,
            "pushNotifications": false,
            "extendedAgentCard": true,
            "extensions": [
              { "uri": "https://ext.example/v1", "description": "An extension.", "required": true,
                "params": { "level": 0, "enabled": false, "nested": { "k": "v" } } }
            ]
          },
          "securitySchemes": {
            "key": { "apiKeySecurityScheme": { "description": "API key.", "location": "header", "name": "X-Api-Key" } },
            "bearer": { "httpAuthSecurityScheme": { "description": "Bearer.", "scheme": "Bearer", "bearerFormat": "JWT" } },
            "oauth": {
              "oauth2SecurityScheme": {
                "description": "OAuth.",
                "oauth2MetadataUrl": "https://auth.example/.well-known/oauth-authorization-server",
                "flows": {
                  "authorizationCode": {
                    "authorizationUrl": "https://auth.example/authorize", "tokenUrl": "https://auth.example/token",
                    "refreshUrl": "https://auth.example/refresh", "scopes": { "read": "Read." }, "pkceRequired": true
                  },
                  "clientCredentials": {
                    "tokenUrl": "https://auth.example/token", "refreshUrl": "https://auth.example/refresh",
                    "scopes": { "read": "Read." }
                  },
                  "implicit": {
                    "authorizationUrl": "https://auth.example/authorize", "refreshUrl": "https://auth.example/refresh",
                    "scopes": { "read": "Read." }
                  },
                  "password": {
                    "tokenUrl": "https://auth.example/token", "refreshUrl": "https://auth.example/refresh",
                    "scopes": { "read": "Read." }
                  },
                  "deviceCode": {
                    "deviceAuthorizationUrl": "https://auth.example/device", "tokenUrl": "https://auth.example/token",
                    "refreshUrl": "https://auth.example/refresh", "scopes": { "read": "Read." }
                  }
                }
              }
            },
            "oidc": { "openIdConnectSecurityScheme": { "description": "OIDC.", "openIdConnectUrl": "https://auth.example/.well-known/openid-configuration" } },
            "mtls": { "mtlsSecurityScheme": { "description": "Mutual TLS." } }
          },
          "securityRequirements": [ { "schemes": { "oauth": { "list": [ "read" ] } } } ],
          "defaultInputModes": [ "text/plain" ],
          "defaultOutputModes": [ "application/json" ],
          "skills": [
            {
              "id": "s1", "name": "Skill", "description": "Does a thing.", "tags": [ "t" ], "examples": [ "do it" ],
              "inputModes": [ "text/plain" ], "outputModes": [ "text/plain" ],
              "securityRequirements": [ { "schemes": { "key": { "list": [ "any" ] } } } ]
            }
          ],
          "iconUrl": "https://agent.example/icon.png"
        }
        """;

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Every_schema_member_is_signed()
    {
        var card = Parse(FullCard);

        Assert.Empty(AgentCardCanonicalizer.PrunedPaths(card));
        Assert.True(JsonNode.DeepEquals(card, AgentCardCanonicalizer.SignedContent(card)),
            AgentCardCanonicalizer.SignedContent(card).ToJsonString());
    }

    [Fact]
    public void Every_schema_member_is_in_the_canonical_bytes()
    {
        var bytes = Encoding.UTF8.GetString(AgentCardCanonicalizer.Canonicalize(Parse(FullCard)));

        foreach (var value in new[]
                 {
                     "\"t1\"", "\"Example Org\"", "\"1.2.3\"", "\"JWT\"", "\"X-Api-Key\"", "\"https://auth.example/device\"",
                     "\"Mutual TLS.\"", "\"https://auth.example/.well-known/openid-configuration\"", "\"do it\"",
                     "\"https://agent.example/icon.png\"", "\"pkceRequired\":true", "\"required\":true"
                 })
            Assert.Contains(value, bytes);
    }

    [Fact]
    public void Signatures_are_not_signed_and_not_reported()
    {
        var card = Parse(FullCard);
        card["signatures"] = new JsonArray(new JsonObject { ["protected"] = "e30", ["signature"] = "c2ln" });

        Assert.False(AgentCardCanonicalizer.SignedContent(card).ContainsKey("signatures"));
        Assert.Empty(AgentCardCanonicalizer.PrunedPaths(card));
        Assert.True(card.ContainsKey("signatures")); // the input is not changed
    }

    [Fact]
    public void Implicit_false_is_removed_but_a_present_false_is_kept()
    {
        var card = Parse(FullCard);
        card["capabilities"]!["extensions"]![0]!["required"] = false;
        card["securitySchemes"]!["oauth"]!["oauth2SecurityScheme"]!["flows"]!["authorizationCode"]!["pkceRequired"] = false;

        var signed = AgentCardCanonicalizer.SignedContent(card);

        Assert.Null(signed["capabilities"]!["extensions"]![0]!["required"]);
        Assert.Null(signed["securitySchemes"]!["oauth"]!["oauth2SecurityScheme"]!["flows"]!["authorizationCode"]!["pkceRequired"]);
        Assert.False(signed["capabilities"]!["pushNotifications"]!.GetValue<bool>());
        Assert.Equal(
            [
                "$.capabilities.extensions[0].required",
                "$.securitySchemes.oauth.oauth2SecurityScheme.flows.authorizationCode.pkceRequired"
            ],
            AgentCardCanonicalizer.PrunedPaths(card));
    }

    [Fact]
    public void Struct_params_keep_false_and_zero()
    {
        var signed = AgentCardCanonicalizer.SignedContent(Parse(FullCard));
        var parameters = signed["capabilities"]!["extensions"]![0]!["params"]!;

        Assert.Equal(0, parameters["level"]!.GetValue<int>());
        Assert.False(parameters["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public void Empty_values_are_removed_and_reported_by_path()
    {
        var card = Parse("""
            {
              "name": "A",
              "description": "",
              "iconUrl": null,
              "defaultInputModes": [ "text/plain", "" ],
              "skills": [ { "id": "s1", "description": "", "tags": [] } ]
            }
            """);

        var signed = AgentCardCanonicalizer.SignedContent(card);

        Assert.Equal("""{"name":"A","defaultInputModes":["text/plain"],"skills":[{"id":"s1"}]}""", signed.ToJsonString());
        Assert.Equal(
            ["$.description", "$.iconUrl", "$.defaultInputModes[1]", "$.skills[0].description", "$.skills[0].tags"],
            AgentCardCanonicalizer.PrunedPaths(card));
    }

    [Fact]
    public void Unknown_members_are_removed_and_reported_by_path()
    {
        var card = Parse("""{ "name": "A", "extra": 1, "provider": { "organization": "O", "phone": "123" } }""");

        Assert.Equal("""{"name":"A","provider":{"organization":"O"}}""", AgentCardCanonicalizer.SignedContent(card).ToJsonString());
        Assert.Equal(["$.extra", "$.provider.phone"], AgentCardCanonicalizer.PrunedPaths(card));
        Assert.Empty(AgentCardCanonicalizer.CaseVariantMembers(card));
    }

    [Theory]
    [InlineData("plain", "$.plain")]
    [InlineData("with_underscore_1", "$.with_underscore_1")]
    [InlineData("_leading", "$._leading")]
    [InlineData("x-vendor", "$[\"x-vendor\"]")]
    [InlineData("1st", "$[\"1st\"]")]
    [InlineData("has space", "$[\"has space\"]")]
    [InlineData("quote\"d", "$[\"quote\\u0022d\"]")] // JSON string escaping, as JsonValue writes it
    [InlineData("", "$[\"\"]")]
    public void Member_names_that_are_not_identifiers_use_bracket_paths(string key, string path)
    {
        var card = new JsonObject { ["name"] = "A", [key] = 1 };

        Assert.Equal([path], AgentCardCanonicalizer.PrunedPaths(card));
    }

    [Fact]
    public void Case_variant_member_is_reported_and_not_signed()
    {
        var card = Parse("""{ "name": "A", "IconUrl": "https://evil.example/icon.png" }""");

        Assert.Equal(["$.IconUrl"], AgentCardCanonicalizer.CaseVariantMembers(card));
        Assert.Equal(["$.IconUrl"], AgentCardCanonicalizer.PrunedPaths(card));
        Assert.False(AgentCardCanonicalizer.SignedContent(card).ContainsKey("IconUrl"));
    }

    [Fact]
    public void Container_emptied_by_its_children_is_removed_but_not_reported()
    {
        var card = Parse("""{ "name": "A", "provider": { "url": "" } }""");

        Assert.Equal("""{"name":"A"}""", AgentCardCanonicalizer.SignedContent(card).ToJsonString());
        Assert.Equal(["$.provider.url"], AgentCardCanonicalizer.PrunedPaths(card));
    }

    [Fact]
    public void Container_that_was_empty_is_reported()
    {
        var card = Parse("""{ "name": "A", "provider": {}, "skills": [] }""");

        Assert.Equal(["$.provider", "$.skills"], AgentCardCanonicalizer.PrunedPaths(card));
    }
}
