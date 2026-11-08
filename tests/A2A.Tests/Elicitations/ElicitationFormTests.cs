using System.Text.Json;
using A2A.Elicitations;

namespace A2A.Tests.Elicitations;

public class ElicitationFormTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message WithForm(string json) => new()
    {
        MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")],
        Metadata = new() { [Elicitation.MetadataKey] = Json(json) },
    };

    private static string FormJson(string propertyJson, string schemaExtra = "", string wrapperExtra = "", string required = "") =>
        "{\"id\":\"f1\"" + wrapperExtra + ",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"city\":" + propertyJson + "}"
        + (required.Length > 0 ? ",\"required\":" + required : "") + schemaExtra + "}}";

    private static ElicitationForm? Read(string json) => WithForm(json).GetElicitationForm();

    private static ElicitationForm Everything(string id = "all") => new(
    [
        ElicitationField.Text("city", "City", "Where to", required: true, minLength: 2, maxLength: 5),
        ElicitationField.Integer("nights", required: true, minimum: 1, maximum: 30),
        ElicitationField.Choice("room", ["single", "double"]),
        ElicitationField.Boolean("late"),
        ElicitationField.Number("ratio", minimum: 0, maximum: 1),
        ElicitationField.Text("email", format: ElicitationFormat.Email),
        ElicitationField.Text("day", format: ElicitationFormat.Date),
        ElicitationField.Text("at", format: ElicitationFormat.DateTime),
    ], id);

    // ---- model and caps ----

    [Fact]
    public void Constants_match_the_spec()
    {
        Assert.Equal("https://github.com/roman-ambinder/a2a/extensions/elicitation/v1", Elicitation.ExtensionUri);
        Assert.Equal(Elicitation.ExtensionUri, Elicitation.MetadataKey);
        Assert.Equal("a2a.elicitation.answered", Elicitation.AnsweredKey);
        Assert.Equal((32, 64, 200, 1000, 128, 64, 256, 4096),
            (Elicitation.MaxFields, Elicitation.MaxNameLength, Elicitation.MaxTitleLength, Elicitation.MaxDescriptionLength,
             Elicitation.MaxFormIdLength, Elicitation.MaxOptions, Elicitation.MaxOptionLength, Elicitation.MaxStringLength));
        var extension = Elicitation.CreateExtension();
        Assert.Equal(Elicitation.ExtensionUri, extension.Uri);
        Assert.False(extension.Required);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1abc")]
    [InlineData("a b")]
    [InlineData("a.b")]
    public void Factory_refuses_a_bad_name(string name) => Assert.Throws<ArgumentException>(() => ElicitationField.Text(name));

    [Fact]
    public void Factory_refuses_a_long_name_title_and_description()
    {
        Assert.Throws<ArgumentException>(() => ElicitationField.Text(new string('a', 65)));
        ElicitationField.Text(new string('a', 64));
        Assert.Throws<ArgumentException>(() => ElicitationField.Text("a", title: new string('t', 201)));
        Assert.Throws<ArgumentException>(() => ElicitationField.Text("a", description: new string('d', 1001)));
        Assert.Throws<ArgumentException>(() => ElicitationField.Text("a", title: "\ud800"));
    }

    [Fact]
    public void Factory_refuses_bad_limits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ElicitationField.Text("a", maxLength: 4097));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElicitationField.Text("a", minLength: -1));
        Assert.Throws<ArgumentException>(() => ElicitationField.Text("a", minLength: 5, maxLength: 4));
        Assert.Throws<ArgumentException>(() => ElicitationField.Integer("a", minimum: 5, maximum: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElicitationField.Number("a", maximum: double.PositiveInfinity));
    }

    [Fact]
    public void Choice_refuses_bad_options()
    {
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", []));
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", Enumerable.Range(0, 65).Select(i => "o" + i)));
        ElicitationField.Choice("a", Enumerable.Range(0, 64).Select(i => "o" + i));
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", [new string('x', 257)]));
        ElicitationField.Choice("a", [new string('x', 256)]);
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", ["x", "x"]));
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", [""]));
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", ["bad\u0007"]));
        Assert.Throws<ArgumentException>(() => ElicitationField.Choice("a", ["zero\u200Bwidth"]));
    }

    [Fact]
    public void Form_refuses_bad_fields_and_ids()
    {
        Assert.Throws<ArgumentException>(() => new ElicitationForm([]));
        Assert.Throws<ArgumentException>(() => new ElicitationForm([ElicitationField.Text("a"), ElicitationField.Boolean("a")]));
        Assert.Throws<ArgumentException>(() => new ElicitationForm(Enumerable.Range(0, 33).Select(i => ElicitationField.Text("f" + i))));
        _ = new ElicitationForm(Enumerable.Range(0, 32).Select(i => ElicitationField.Text("f" + i)));
        Assert.Throws<ArgumentException>(() => new ElicitationForm([ElicitationField.Text("a")], id: "has space"));
        Assert.Throws<ArgumentException>(() => new ElicitationForm([ElicitationField.Text("a")], id: new string('i', 129)));
        _ = new ElicitationForm([ElicitationField.Text("a")], id: new string('i', 128));
    }

    [Fact]
    public void Form_gets_a_new_id_when_none_is_given()
    {
        var a = new ElicitationForm([ElicitationField.Text("a")]);
        var b = new ElicitationForm([ElicitationField.Text("a")]);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(32, a.Id.Length);
        Assert.DoesNotContain('-', a.Id);
    }

    // ---- wire round trip and strict reading (D4, D5) ----

    [Fact]
    public void Form_round_trips_through_the_metadata()
    {
        var form = Everything();
        var read = new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")] }.SetElicitationForm(form).GetElicitationForm();
        Assert.NotNull(read);
        Assert.Equal("all", read.Id);
        Assert.Equal(form.Fields.Count, read.Fields.Count);
        foreach (var expected in form.Fields)
        {
            var actual = read.Fields.Single(f => f.Name == expected.Name);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Description, actual.Description);
            Assert.Equal(expected.Required, actual.Required);
            Assert.Equal(expected.MinLength, actual.MinLength);
            Assert.Equal(expected.MaxLength, actual.MaxLength);
            Assert.Equal(expected.Minimum, actual.Minimum);
            Assert.Equal(expected.Maximum, actual.Maximum);
            Assert.Equal(expected.Format, actual.Format);
            Assert.Equal(expected.Options, actual.Options);
        }
    }

    [Fact]
    public void Wire_shape_is_the_one_of_the_spec()
    {
        var form = new ElicitationForm([ElicitationField.Text("city", "City", maxLength: 100, required: true), ElicitationField.Choice("room", ["single", "double"])], "f1");
        var json = new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")] }.SetElicitationForm(form).Metadata![Elicitation.MetadataKey].GetRawText();
        Assert.Equal("{\"id\":\"f1\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\",\"title\":\"City\",\"maxLength\":100},"
            + "\"room\":{\"type\":\"string\",\"enum\":[\"single\",\"double\"]}},\"required\":[\"city\"]}}", json);
    }

    [Fact]
    public void Reads_whole_floats_as_integers_for_lengths()
    {
        var form = Read(FormJson("{\"type\":\"string\",\"maxLength\":100.0,\"minLength\":2.0}"));
        Assert.NotNull(form);
        Assert.Equal(100, form.Fields[0].MaxLength);
        Assert.Equal(2, form.Fields[0].MinLength);
    }

    [Fact]
    public void Reads_enum_with_length_limits()
    {
        var form = Read(FormJson("{\"type\":\"string\",\"enum\":[\"a\",\"b\"],\"maxLength\":3}"));
        Assert.Equal(["a", "b"], form!.Fields[0].Options);
        Assert.Equal(3, form.Fields[0].MaxLength);
    }

    [Theory]
    [InlineData("{\"type\":\"string\",\"default\":\"x\"}")]
    [InlineData("{\"type\":\"string\",\"pattern\":\"^a+$\"}")]
    [InlineData("{\"type\":\"string\",\"format\":\"uri\"}")]
    [InlineData("{\"type\":\"string\",\"format\":\"password\"}")]
    [InlineData("{\"type\":\"string\",\"writeOnly\":true}")]
    [InlineData("{\"type\":\"string\",\"x-hint\":1}")]
    [InlineData("{\"type\":\"string\",\"$ref\":\"#/x\"}")]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"string\",\"items\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}")]
    [InlineData("{\"type\":\"string\",\"properties\":{}}")]
    [InlineData("{\"type\":\"string\",\"minimum\":1}")]
    [InlineData("{\"type\":\"integer\",\"minLength\":1}")]
    [InlineData("{\"type\":\"boolean\",\"minimum\":0}")]
    [InlineData("{\"type\":\"integer\",\"enum\":[\"a\"]}")]
    [InlineData("{\"type\":\"string\",\"enum\":[1,2]}")]
    [InlineData("{\"type\":\"string\",\"enum\":[]}")]
    [InlineData("{\"type\":\"string\",\"enum\":[\"a\",\"a\"]}")]
    [InlineData("{\"type\":\"string\",\"enum\":[\"a\"],\"format\":\"email\"}")]
    [InlineData("{\"type\":\"string\",\"title\":5}")]
    [InlineData("{\"type\":\"string\",\"minLength\":1.5}")]
    [InlineData("{\"type\":\"string\",\"minLength\":-1}")]
    [InlineData("{\"type\":\"string\",\"maxLength\":4097}")]
    [InlineData("{\"type\":\"string\",\"minLength\":5,\"maxLength\":4}")]
    [InlineData("{\"type\":\"integer\",\"minimum\":5,\"maximum\":4}")]
    [InlineData("{\"type\":\"integer\",\"minimum\":\"1\"}")]
    [InlineData("{\"type\":\"null\"}")]
    [InlineData("{\"type\":[\"string\",\"null\"]}")]
    [InlineData("{}")]
    [InlineData("\"string\"")]
    [InlineData("null")]
    public void Strict_reading_refuses_what_the_spec_leaves_out(string property) => Assert.Null(Read(FormJson(property)));

    [Theory]
    [InlineData(",\"additionalProperties\":false")]
    [InlineData(",\"$schema\":\"x\"")]
    [InlineData(",\"title\":\"x\"")]
    [InlineData(",\"default\":{}")]
    public void Strict_reading_refuses_unknown_schema_keywords(string extra) =>
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", schemaExtra: extra)));

    [Fact]
    public void Strict_reading_refuses_unknown_wrapper_keys_and_bad_wrappers()
    {
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", wrapperExtra: ",\"extra\":1")));
        Assert.Null(Read("{\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}}")); // no id
        Assert.Null(Read("{\"id\":\"f\"}"));
        Assert.Null(Read("{\"id\":5,\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}}"));
        Assert.Null(Read("{\"id\":\"f\",\"requestedSchema\":{\"properties\":{\"a\":{\"type\":\"string\"}}}}")); // no type
        Assert.Null(Read("{\"id\":\"f\",\"requestedSchema\":{\"type\":\"array\",\"properties\":{\"a\":{\"type\":\"string\"}}}}"));
        Assert.Null(Read("{\"id\":\"f\",\"requestedSchema\":{\"type\":\"object\"}}"));
        Assert.Null(Read("{\"id\":\"f\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{}}}")); // no field
        Assert.Null(Read("{\"id\":\"f\",\"requestedSchema\":\"x\"}"));
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", required: "[\"nope\"]")));
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", required: "[\"city\",\"city\"]")));
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", required: "\"city\"")));
        Assert.Null(Read(FormJson("{\"type\":\"string\"}", required: "[1]")));
    }

    [Fact]
    public void Reading_enforces_the_caps()
    {
        static string Many(int count) => "{\"id\":\"f\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{"
            + string.Join(",", Enumerable.Range(0, count).Select(i => "\"f" + i + "\":{\"type\":\"string\"}")) + "}}}";
        Assert.NotNull(Read(Many(32)));
        Assert.Null(Read(Many(33)));

        Assert.NotNull(Read("{\"id\":\"" + new string('i', 128) + "\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}}"));
        Assert.Null(Read("{\"id\":\"" + new string('i', 129) + "\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}}"));
        Assert.Null(Read("{\"id\":\"a b\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\"}}}}"));

        string Named(string name) => "{\"id\":\"f\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"" + name + "\":{\"type\":\"string\"}}}}";
        Assert.NotNull(Read(Named(new string('a', 64))));
        Assert.Null(Read(Named(new string('a', 65))));
        Assert.Null(Read(Named("1a")));
        Assert.Null(Read(Named("a b")));

        string Options(int count, int length) => FormJson("{\"type\":\"string\",\"enum\":[" + string.Join(",", Enumerable.Range(0, count).Select(i => "\"" + i.ToString("D3").PadRight(length, 'x') + "\"")) + "]}");
        Assert.NotNull(Read(Options(64, 256)));
        Assert.Null(Read(Options(65, 3)));
        Assert.Null(Read(Options(1, 257)));
        Assert.NotNull(Read(FormJson("{\"type\":\"string\",\"maxLength\":4096}")));
    }

    [Fact]
    public void Reading_sanitizes_title_and_description()
    {
        var form = Read(FormJson("{\"type\":\"string\",\"title\":\"Ci\\u202Ety\\u200B\\u0007\",\"description\":\"" + new string('d', 1500) + "\"}"));
        Assert.NotNull(form);
        Assert.Equal("City", form.Fields[0].Title);
        Assert.Equal(1000, form.Fields[0].Description!.Length);
        Assert.EndsWith("\u2026", form.Fields[0].Description);
        var long200 = Read(FormJson("{\"type\":\"string\",\"title\":\"" + new string('t', 300) + "\"}"));
        Assert.Equal(200, long200!.Fields[0].Title!.Length);
    }

    [Theory]
    [InlineData("a\\u202Eb")]
    [InlineData("a\\u200Bb")]
    [InlineData("a\\u0007b")]
    [InlineData("a\\udb40\\udc41b")] // a tag character
    public void Reading_refuses_an_option_the_sanitizer_would_change(string option) =>
        Assert.Null(Read(FormJson("{\"type\":\"string\",\"enum\":[\"" + option + "\"]}")));

    [Fact]
    public void Reading_a_lone_surrogate_gives_null_and_does_not_throw()
    {
        Assert.Null(Read(FormJson("{\"type\":\"string\",\"title\":\"\\ud800\"}")));
        Assert.Null(Read(FormJson("{\"type\":\"string\",\"enum\":[\"\\ud800\"]}")));
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public void Bad_shapes_give_null_and_never_throw(string value)
    {
        Assert.Null(Read(value));
        Assert.Null(new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")] }.GetElicitationForm());
        Assert.Null(WithForm(value).GetElicitationAnswer());
        var undefined = new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")], Metadata = new() { [Elicitation.MetadataKey] = default } };
        Assert.Null(undefined.GetElicitationForm());
        Assert.Null(undefined.GetElicitationAnswer());
    }

    [Fact]
    public void Task_form_is_read_only_while_input_is_required()
    {
        var message = new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")] }.SetElicitationForm(Everything());
        AgentTask Task(TaskState state) => new() { Id = "t", Status = new AgentTaskStatus { State = state, Message = message } };
        Assert.NotNull(Task(TaskState.InputRequired).GetElicitationForm());
        Assert.Null(Task(TaskState.Working).GetElicitationForm());
        Assert.Null(Task(TaskState.AuthRequired).GetElicitationForm());
        Assert.Null(Task(TaskState.Completed).GetElicitationForm());
        Assert.Null(new AgentTask { Id = "t", Status = new AgentTaskStatus { State = TaskState.InputRequired } }.GetElicitationForm());
    }

    // ---- Validate (D10) ----

    private static Dictionary<string, JsonElement> Content(string json) =>
        Json(json).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

    private const string Good = "{\"city\":\"Oslo\",\"nights\":2}";

    [Theory]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"room\":\"double\",\"late\":true,\"ratio\":0.5,\"email\":\"a@b.co\",\"day\":\"2026-11-08\",\"at\":\"2026-11-08T10:00:00Z\"}")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":3.0}")]
    [InlineData("{\"city\":\"\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\",\"nights\":1}")] // 5 code points
    [InlineData("{\"city\":\"Oslo\",\"nights\":30,\"at\":\"2026-11-08T10:00:00.123+01:00\"}")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":1,\"ratio\":0,\"day\":\"2024-02-29\"}")]
    public void Validate_accepts_a_good_answer(string content) => Everything().Validate(Content(content));

    [Theory]
    [InlineData("{\"nights\":2}", "city")]
    [InlineData("{\"city\":\"Oslo\"}", "nights")]
    [InlineData("{\"city\":null,\"nights\":2}", "city")]
    [InlineData("{\"city\":5,\"nights\":2}", "city")]
    [InlineData("{\"city\":\"O\",\"nights\":2}", "city")]
    [InlineData("{\"city\":\"Osloos\",\"nights\":2}", "city")]
    [InlineData("{\"city\":\"\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\\ud83d\\ude00\",\"nights\":2}", "city")] // 6 code points
    [InlineData("{\"city\":\"Oslo\",\"nights\":2.5}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":\"2\"}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":true}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":0}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":31}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":1e300}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":9007199254740994}", "nights")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"room\":\"triple\"}", "room")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"room\":1}", "room")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"late\":\"yes\"}", "late")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"ratio\":1.5}", "ratio")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"ratio\":-0.1}", "ratio")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"email\":\"no-at\"}", "email")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"email\":\"a@b\"}", "email")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"email\":\"a b@c.d\"}", "email")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"email\":\"@c.d\"}", "email")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"day\":\"2026-13-01\"}", "day")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"day\":\"2026-02-30\"}", "day")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"day\":\"26-1-1\"}", "day")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"day\":\"2026-11-08T00:00:00Z\"}", "day")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"at\":\"2026-11-08\"}", "at")]
    [InlineData("{\"city\":\"Oslo\",\"nights\":2,\"at\":\"2026-11-08T10:00:00\"}", "at")]
    public void Validate_refuses_with_the_field_named(string content, string field)
    {
        var ex = Assert.Throws<A2AException>(() => Everything().Validate(Content(content)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(Elicitation.ExtensionUri, ex.Metadata!["extension"]);
        Assert.Equal(field, ex.Metadata["field"]);
        Assert.Contains(field, ex.Message);
    }

    [Fact]
    public void Validate_refuses_an_unknown_property_without_naming_it()
    {
        var ex = Assert.Throws<A2AException>(() => Everything().Validate(Content("{\"city\":\"Oslo\",\"nights\":2,\"hostile-name\":1}")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(Elicitation.ExtensionUri, ex.Metadata!["extension"]);
        Assert.False(ex.Metadata.ContainsKey("field"));
        Assert.DoesNotContain("hostile-name", ex.Message);
    }

    [Fact]
    public void Validate_refuses_text_that_is_not_valid_utf16()
    {
        Dictionary<string, JsonElement> content;
        try { content = Content("{\"city\":\"\\ud800\",\"nights\":2}"); }
        catch (JsonException) { return; } // the JSON layer already refuses it
        var ex = Assert.Throws<A2AException>(() => Everything().Validate(content));
        Assert.Equal("city", ex.Metadata!["field"]);
    }

    [Fact]
    public void Validate_never_repeats_the_value()
    {
        const string marker = "TOP-SECRET-MARKER-42";
        foreach (var content in new[]
        {
            "{\"city\":\"" + marker + "\",\"nights\":2}",
            "{\"city\":\"Oslo\",\"nights\":\"" + marker + "\"}",
            "{\"city\":\"Oslo\",\"nights\":2,\"room\":\"" + marker + "\"}",
            "{\"city\":\"Oslo\",\"nights\":2,\"email\":\"" + marker + "\"}",
            "{\"city\":\"Oslo\",\"nights\":2,\"day\":\"" + marker + "\"}",
            "{\"city\":\"Oslo\",\"nights\":2,\"" + marker + "\":1}",
        })
        {
            var ex = Assert.Throws<A2AException>(() => Everything().Validate(Content(content)));
            Assert.DoesNotContain(marker, ex.Message);
            Assert.All(ex.Metadata!.Values, v => Assert.DoesNotContain(marker, v));
        }
    }

    [Fact]
    public void Text_without_a_maxLength_is_capped_at_4096_code_points()
    {
        var form = new ElicitationForm([ElicitationField.Text("a")]);
        form.Validate(Content("{\"a\":\"" + new string('x', 4096) + "\"}"));
        var ex = Assert.Throws<A2AException>(() => form.Validate(Content("{\"a\":\"" + new string('x', 4097) + "\"}")));
        Assert.Equal("a", ex.Metadata!["field"]);
    }

    // ---- sensitive fields (section 4) ----

    [Theory]
    [InlineData("password")]
    [InlineData("Password")]
    [InlineData("user_password")]
    [InlineData("passwd")]
    [InlineData("pwd")]
    [InlineData("passphrase")]
    [InlineData("passcode")]
    [InlineData("secret")]
    [InlineData("clientSecret")]
    [InlineData("credential")]
    [InlineData("credentials")]
    [InlineData("token")]
    [InlineData("accessToken")]
    [InlineData("access_token")]
    [InlineData("apikey")]
    [InlineData("apiKey")]
    [InlineData("api_key")]
    [InlineData("api-key")]
    [InlineData("APIKey")]
    [InlineData("privatekey")]
    [InlineData("privateKey")]
    [InlineData("private_key")]
    [InlineData("accessKey")]
    [InlineData("cardNumber")]
    [InlineData("card_number")]
    [InlineData("creditCard")]
    [InlineData("debit_card")]
    [InlineData("securityCode")]
    [InlineData("socialSecurity")]
    [InlineData("ssn")]
    [InlineData("SSN")]
    [InlineData("pin")]
    [InlineData("otp")]
    [InlineData("iban")]
    [InlineData("cvv")]
    [InlineData("cvc")]
    public void Sensitive_names_are_flagged_and_refused_on_the_server(string name)
    {
        var field = ElicitationField.Text(name);
        Assert.True(field.LooksSensitive);
        var ex = Assert.Throws<ArgumentException>(() => new ElicitationForm([field]));
        Assert.Contains(name, ex.Message);
    }

    [Theory]
    [InlineData("Your password", null)]
    [InlineData("Login", "Enter the API key for the service")]
    [InlineData("Card", "The credit card number")]
    [InlineData("socialSecurity", null)]
    [InlineData(null, "One-time otp from your phone")]
    [InlineData("Enter PIN", null)]
    [InlineData("pass_phrase_x", "the passphrase")]
    public void Sensitive_titles_and_descriptions_are_flagged(string? title, string? description)
    {
        var field = ElicitationField.Text("value", title, description);
        Assert.True(field.LooksSensitive);
        Assert.Throws<ArgumentException>(() => new ElicitationForm([field]));
    }

    [Theory]
    [InlineData("maxTokens")]
    [InlineData("max_tokens")]
    [InlineData("city")]
    [InlineData("mapping")]
    [InlineData("spinner")]
    [InlineData("opinion")]
    [InlineData("keyword")]
    [InlineData("card")]
    [InlineData("number")]
    [InlineData("nights")]
    public void Ordinary_names_are_not_flagged(string name)
    {
        var field = ElicitationField.Text(name, "Some title", "Some description");
        Assert.False(field.LooksSensitive);
        Assert.False(new ElicitationForm([field]).HasSensitiveFields);
    }

    [Fact]
    public void Server_can_opt_out_of_the_sensitive_check()
    {
        var form = new ElicitationForm([ElicitationField.Text("pin")], allowSensitiveFields: true);
        Assert.True(form.HasSensitiveFields);
    }

    [Fact]
    public void Reading_a_sensitive_form_does_not_throw_and_sets_the_flag()
    {
        var form = new ElicitationForm([ElicitationField.Text("pin"), ElicitationField.Text("city")], "f", allowSensitiveFields: true);
        var read = new Message { MessageId = "m", Role = Role.Agent, Parts = [Part.FromText("q")] }.SetElicitationForm(form).GetElicitationForm();
        Assert.NotNull(read);
        Assert.True(read.HasSensitiveFields);
        Assert.True(read.Fields.Single(f => f.Name == "pin").LooksSensitive);
        Assert.False(read.Fields.Single(f => f.Name == "city").LooksSensitive);
    }

    // ---- answers: Accept, Decline, Cancel, ToMessage, getters ----

    [Fact]
    public void Accept_checks_the_values_against_the_form()
    {
        var ex = Assert.Throws<A2AException>(() => ElicitationAnswer.Accept(Everything(), new ElicitationValues { { "city", "Oslo" } }));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("nights", ex.Metadata!["field"]);
    }

    [Fact]
    public void Accept_refuses_a_value_for_a_sensitive_field_unless_allowed()
    {
        var form = new ElicitationForm([ElicitationField.Text("city", required: true), ElicitationField.Text("pin")], "f", allowSensitiveFields: true);
        var ex = Assert.Throws<A2AException>(() => ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" }, { "pin", "1234" } }));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("pin", ex.Metadata!["field"]);
        Assert.DoesNotContain("1234", ex.Message);

        var omitted = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" } });
        Assert.Equal("Oslo", omitted.GetString("city"));
        Assert.Null(omitted.GetString("pin"));

        var allowed = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" }, { "pin", "1234" } }, allowSensitive: true);
        Assert.Equal("1234", allowed.GetString("pin"));
    }

    [Fact]
    public void Decline_and_cancel_have_no_content()
    {
        var form = Everything("f9");
        foreach (var (answer, action) in new[] { (ElicitationAnswer.Decline(form), ElicitationAction.Decline), (ElicitationAnswer.Cancel(form), ElicitationAction.Cancel) })
        {
            Assert.Equal("f9", answer.FormId);
            Assert.Equal(action, answer.Action);
            Assert.Empty(answer.Content);
        }
    }

    [Fact]
    public void Answer_round_trips_through_a_message_with_typed_getters()
    {
        var form = Everything();
        var values = new ElicitationValues { { "city", "Oslo" }, { "nights", 2 }, { "ratio", 0.25 }, { "late", true }, { "room", "single" } };
        var message = ElicitationAnswer.Accept(form, values).ToMessage("task-1", "ctx-1");
        Assert.Equal(Role.User, message.Role);
        Assert.Equal("task-1", message.TaskId);
        Assert.Equal("ctx-1", message.ContextId);
        Assert.False(string.IsNullOrEmpty(message.MessageId));

        var read = message.GetElicitationAnswer();
        Assert.NotNull(read);
        Assert.Equal("all", read.FormId);
        Assert.Equal(ElicitationAction.Accept, read.Action);
        Assert.Equal("Oslo", read.GetString("city"));
        Assert.Equal(2L, read.GetInt64("nights"));
        Assert.Equal(0.25, read.GetDouble("ratio"));
        Assert.Equal(true, read.GetBoolean("late"));
        Assert.Equal("single", read.GetString("room"));
        form.Validate(read.Content);
    }

    [Fact]
    public void Getters_return_null_for_absent_or_mistyped_values()
    {
        var answer = ElicitationAnswer.Accept(Everything(), new ElicitationValues { { "city", "Oslo" }, { "nights", 2 } });
        Assert.Null(answer.GetString("absent"));
        Assert.Null(answer.GetInt64("absent"));
        Assert.Null(answer.GetDouble("absent"));
        Assert.Null(answer.GetBoolean("absent"));
        Assert.Null(answer.GetString("nights"));
        Assert.Null(answer.GetInt64("city"));
        Assert.Null(answer.GetDouble("city"));
        Assert.Null(answer.GetBoolean("city"));
        Assert.Equal(2.0, answer.GetDouble("nights"));
    }

    [Fact]
    public void GetInt64_reads_a_whole_float_and_refuses_a_fraction()
    {
        var message = WithForm("{\"id\":\"f\",\"action\":\"accept\",\"content\":{\"a\":3.0,\"b\":3.5,\"c\":1e300,\"d\":9007199254740991,\"e\":-4}}");
        var answer = message.GetElicitationAnswer()!;
        Assert.Equal(3L, answer.GetInt64("a"));
        Assert.Null(answer.GetInt64("b"));
        Assert.Null(answer.GetInt64("c"));
        Assert.Equal(9007199254740991L, answer.GetInt64("d"));
        Assert.Equal(-4L, answer.GetInt64("e"));
    }

    [Theory]
    [InlineData(ElicitationAction.Accept, "answered")]
    [InlineData(ElicitationAction.Decline, "declined")]
    [InlineData(ElicitationAction.Cancel, "cancelled")]
    public void Default_text_names_the_action_and_never_the_values(ElicitationAction action, string word)
    {
        var form = new ElicitationForm([ElicitationField.Text("city", required: true)], "f");
        var answer = action switch
        {
            ElicitationAction.Accept => ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo-marker" } }),
            ElicitationAction.Decline => ElicitationAnswer.Decline(form),
            _ => ElicitationAnswer.Cancel(form),
        };
        var text = answer.ToMessage("t").GetText();
        Assert.Contains(word, text);
        Assert.DoesNotContain("Oslo", text);
        Assert.Equal("my own words", answer.ToMessage("t", text: "my own words").GetText());
    }

    [Fact]
    public void Values_refuse_bad_input()
    {
        var values = new ElicitationValues { { "a", "x" } };
        Assert.Throws<ArgumentException>(() => values.Add("a", 1));
        Assert.Throws<ArgumentException>(() => values.Add("b", double.NaN));
        Assert.Throws<ArgumentException>(() => values.Add("c", "\ud800"));
        Assert.Throws<ArgumentException>(() => values.Add("", true));
        Assert.Single(values);
    }

    [Theory]
    [InlineData("{\"id\":\"f\",\"action\":\"accept\"}")] // accept without content
    [InlineData("{\"id\":\"f\",\"action\":\"accept\",\"content\":[1]}")]
    [InlineData("{\"id\":\"f\",\"action\":\"accept\",\"content\":null}")]
    [InlineData("{\"id\":\"f\",\"action\":\"decline\",\"content\":{}}")]
    [InlineData("{\"id\":\"f\",\"action\":\"cancel\",\"content\":{\"a\":1}}")]
    [InlineData("{\"id\":\"f\",\"action\":\"maybe\"}")]
    [InlineData("{\"id\":\"f\",\"action\":5}")]
    [InlineData("{\"action\":\"decline\"}")]
    [InlineData("{\"id\":\"has space\",\"action\":\"decline\"}")]
    [InlineData("{\"id\":\"f\",\"action\":\"decline\",\"extra\":1}")]
    public void Answer_reading_is_strict(string json) => Assert.Null(WithForm(json).GetElicitationAnswer());

    [Theory]
    [InlineData("{\"id\":\"f\",\"action\":\"accept\",\"content\":{\"\\ud800\":1}}")]
    [InlineData("{\"id\":\"f\",\"action\":\"decline\",\"\\ud800\":1}")]
    [InlineData("{\"id\":\"f\",\"action\":\"\\ud800\"}")]
    [InlineData("{\"id\":\"\\ud800\",\"action\":\"decline\"}")]
    public void Answer_reading_returns_null_for_a_lone_surrogate_and_never_throws(string json) =>
        Assert.Null(WithForm(json).GetElicitationAnswer());

    [Fact]
    public void Answer_with_a_repeated_id_key_uses_the_last_one()
    {
        var answer = WithForm("{\"id\":\"other\",\"id\":\"f1\",\"action\":\"decline\"}").GetElicitationAnswer();
        Assert.Equal("f1", answer!.FormId);
    }

    [Fact]
    public void Answer_reading_accepts_decline_and_cancel()
    {
        Assert.Equal(ElicitationAction.Decline, WithForm("{\"id\":\"f\",\"action\":\"decline\"}").GetElicitationAnswer()!.Action);
        Assert.Equal(ElicitationAction.Cancel, WithForm("{\"id\":\"f\",\"action\":\"cancel\"}").GetElicitationAnswer()!.Action);
        Assert.Empty(WithForm("{\"id\":\"f\",\"action\":\"accept\",\"content\":{}}").GetElicitationAnswer()!.Content);
    }
}
