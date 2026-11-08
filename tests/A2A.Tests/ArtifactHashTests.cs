using System.Security.Cryptography;
using System.Text.Json;
using A2A.Integrity;
using A2A.Validation;

namespace A2A.Tests;

/// <summary>
/// Hash vectors come from <c>shasum</c> / Python <c>hashlib</c>, not from .NET: the expected values are the SHA-256 of the
/// exact bytes in each comment.
/// </summary>
public sealed class ArtifactHashTests
{
    private static JsonElement Json(string json, int maxDepth = 64)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = maxDepth });
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Text_part_hashes_its_utf8_bytes()
    {
        // "hello"
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", ArtifactHash.Compute([Part.FromText("hello")]));
        // "héllo wörld ✓" as UTF-8, no BOM, no normalization
        Assert.Equal("sha256:c2a59c71097b678dc5af2eb1f98ddc575b63948b0fa6740071a945673aaada4d", ArtifactHash.Compute([Part.FromText("héllo wörld ✓")]));
    }

    [Fact]
    public void No_parts_hash_to_the_empty_digest()
    {
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ArtifactHash.Compute([]));
    }

    [Fact]
    public void Raw_part_hashes_the_decoded_bytes()
    {
        // 00 01 ff 10
        Assert.Equal("sha256:fc7541dc002faf298d19af780cb2df1dbbadec82831a088473e62be9e8174f24",
            ArtifactHash.Compute([Part.FromBytes(new byte[] { 0x00, 0x01, 0xff, 0x10 }, "application/octet-stream")]));
    }

    [Fact]
    public void Url_part_hashes_the_url_string_as_sent()
    {
        // "https://example.com/Files/a.pdf?x=1&y=2"
        Assert.Equal("sha256:7dff2a607fbe38da5b0bbea0b0b3d8d3f0c86f5d5f8c5d08954b6b05b8f741c4",
            ArtifactHash.Compute([Part.FromUrl(new Uri("https://example.com/Files/a.pdf?x=1&y=2"))]));
        // "https://example.com/Files/a%20b.pdf?x=1&y=2": the escape is kept (Uri.ToString would turn it into a space)
        Assert.Equal("sha256:c83e7734a8817455b9d5eee85a140ed862dac41cc64e9a6a80598936273cccfb",
            ArtifactHash.Compute([Part.FromUrl(new Uri("https://example.com/Files/a%20b.pdf?x=1&y=2"))]));
    }

    [Fact]
    public void Data_part_hashes_the_rfc_8785_form()
    {
        // Canonical form: {"a":"é","b":1,"c":[true,null],"d":{"x":1.5,"y":100}}
        var shuffled = Json("""{"d":{"y":100,"x":1.5},"c":[true,null],"b":1.0,"a":"é"}""");
        Assert.Equal("sha256:5dbd0b9306cf8d36a00e852376b65fce40df34f8ebfb9c56dfcd2ecf1e3c89f1", ArtifactHash.Compute([Part.FromData(shuffled)]));
        // Already canonical: the same bytes, the same hash.
        var canonical = Json("""{"a":"é","b":1,"c":[true,null],"d":{"x":1.5,"y":100}}""");
        Assert.Equal("sha256:5dbd0b9306cf8d36a00e852376b65fce40df34f8ebfb9c56dfcd2ecf1e3c89f1", ArtifactHash.Compute([Part.FromData(canonical)]));
        // {"a":"é","b":1}
        Assert.Equal("sha256:aa58fba8483623bed37c1b02edfccbdd9a53123837c20bfa4cb4049993a2872e",
            ArtifactHash.Compute([Part.FromData(Json("""{"b":1.0,"a":"é"}"""))]));
    }

    [Fact]
    public void Parts_are_hashed_in_order_with_nothing_between_them()
    {
        // "foobar"
        const string foobar = "sha256:c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";
        Assert.Equal(foobar, ArtifactHash.Compute([Part.FromText("foo"), Part.FromBytes("bar"u8.ToArray(), "text/plain")]));
        Assert.Equal(foobar, ArtifactHash.Compute([Part.FromText("foob"), Part.FromText("ar")]));
        Assert.Equal(foobar, ArtifactHash.Compute([Part.FromText("foobar")]));
        // "hi" 01 02 03 "https://example.com/x" {"k":1}
        Assert.Equal("sha256:8d51bf57281e3fa6c493b0b4c36dce95f55e295fe2d55bfd56cc11e9ff970b13", ArtifactHash.Compute(
        [
            Part.FromText("hi"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream"),
            Part.FromUrl(new Uri("https://example.com/x")), Part.FromData(Json("""{"k":1}""")),
        ]));
    }

    [Fact]
    public void Empty_text_part_adds_no_bytes()
    {
        Assert.Equal(ArtifactHash.Compute([Part.FromText("hello")]), ArtifactHash.Compute([Part.FromText("hello"), Part.FromText("")]));
    }

    [Fact]
    public void Metadata_name_and_media_type_are_not_hashed()
    {
        var plain = Part.FromText("hello");
        var dressed = new TextPart("hello")
        {
            Filename = "a.txt", MediaType = "text/plain", Metadata = new() { ["k"] = Json("1") },
        };
        Assert.Equal(ArtifactHash.Compute([plain]), ArtifactHash.Compute([dressed]));
    }

    [Fact]
    public void Update_continues_a_running_hash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        ArtifactHash.Update(hash, [Part.FromText("foo")]);
        Assert.Equal("sha256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae", ArtifactHash.Format(hash.GetCurrentHash()));
        ArtifactHash.Update(hash, [Part.FromText("bar")]);
        Assert.Equal("sha256:c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2", ArtifactHash.Format(hash.GetCurrentHash()));
    }

    [Fact]
    public void Number_outside_the_double_range_cannot_be_hashed_and_adds_nothing()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var parts = new Part[] { Part.FromText("x"), Part.FromData(Json("""{"n":1e999}""")) };
        Assert.Throws<InvalidOperationException>(() => ArtifactHash.Update(hash, parts));
        Assert.Throws<InvalidOperationException>(() => ArtifactHash.Compute(parts));
        // Nothing was added: the digest is still the one of no bytes.
        Assert.Equal("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", ArtifactHash.Format(hash.GetCurrentHash()));
    }

    [Fact]
    public void Deep_json_is_hashed()
    {
        var json = new string('[', 100) + "1" + new string(']', 100);
        var element = Json(json, maxDepth: 200);
        // The canonical form of nested arrays is the same text.
        Assert.Equal(ArtifactHash.Compute([Part.FromText(json)]), ArtifactHash.Compute([Part.FromData(element)]));
    }

    [Fact]
    public void Null_part_is_refused()
    {
        Assert.Throws<ArgumentException>(() => ArtifactHash.Compute([Part.FromText("a"), null!]));
    }

    [Fact]
    public void Format_needs_a_32_byte_digest_and_writes_lowercase_hex()
    {
        Assert.Throws<ArgumentException>(() => ArtifactHash.Format(new byte[31]));
        Assert.Equal("sha256:" + new string('0', 62) + "ab", ArtifactHash.Format([.. new byte[30], 0x00, 0xab]));
    }

    [Fact]
    public void Validator_allows_empty_parts_only_when_told_to()
    {
        var empty = new Artifact { ArtifactId = "a", Parts = [] };
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, Assert.Throws<A2AException>(() => A2AValidator.ValidateArtifact(empty, "artifact")).Code);
        Assert.Throws<A2AException>(() => A2AValidator.ValidateArtifact(empty, "artifact", allowEmptyParts: false));
        A2AValidator.ValidateArtifact(empty, "artifact", allowEmptyParts: true);
        // A null part and a missing id stay invalid.
        Assert.Throws<A2AException>(() => A2AValidator.ValidateArtifact(new Artifact { ArtifactId = "a", Parts = [null!] }, "artifact", allowEmptyParts: true));
        Assert.Throws<A2AException>(() => A2AValidator.ValidateArtifact(new Artifact { ArtifactId = " ", Parts = [] }, "artifact", allowEmptyParts: true));
        // A part with empty content is a normal part.
        A2AValidator.ValidateArtifact(new Artifact { ArtifactId = "a", Parts = [Part.FromText("")] }, "artifact");
    }
}
