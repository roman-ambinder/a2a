namespace Commons;

public static class Consts
{
    /// <summary>
    ///     A public demo API key for the Docker samples (MultiInstance). Everyone who reads this file has it: never use it
    ///     outside a demo on localhost. Make a real one with <c>a2a keys new</c>.
    /// </summary>
    public const string TestApiKey = "a2a_bXVsdGktaW5zdGFuY2UgZGVtbyBrZXk6IHB1YmxpYyE";

    /// <summary>The hash of <see cref="TestApiKey" />: what the demo agent stores.</summary>
    public const string TestApiKeyHash = "sha256:ff225598e72edb2ba25e7d5ebcd7f39cb44c9b4855e6a4efbf51245c5fa305ce";

    public const string XApiKey = "X-Api-Key";
}
