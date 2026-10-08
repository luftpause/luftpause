using System.Text.Json;

namespace Luftpause.Runtime.Tests;

public class ToolArgumentsTests
{
    private static readonly JsonElement Schema = Json("""
        {"type": "object",
         "properties": {"query": {"type": "string"}, "limit": {"type": "integer"}},
         "required": ["query"],
         "additionalProperties": false}
        """);

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Well_formed_arguments_pass()
    {
        Assert.Empty(ToolArguments.Problems(Schema, Json("""{"query": "Nile", "limit": 3}""")));
    }

    [Fact]
    public void The_schema_passed_back_as_arguments_is_explained()
    {
        var problems = ToolArguments.Problems(Schema, Json("""{"properties": {"query": "Amazon river"}, "required": ["query"], "type": "object"}"""));

        Assert.Contains("missing required 'query'", problems);
        Assert.Contains("unexpected 'properties'", problems);
        Assert.Contains("unexpected 'type'", problems);
    }

    [Fact]
    public void Wrong_types_are_named()
    {
        var problems = ToolArguments.Problems(Schema, Json("""{"query": 42, "limit": 2.5}"""));

        Assert.Contains("'query' should be string, got Number", problems);
        Assert.Contains("'limit' should be integer, got Number", problems);
    }

    [Fact]
    public void Extra_fields_pass_when_the_schema_allows_them()
    {
        var open = Json("""{"type": "object", "properties": {"query": {"type": "string"}}, "required": ["query"]}""");

        Assert.Empty(ToolArguments.Problems(open, Json("""{"query": "Nile", "lang": "en"}""")));
    }

    [Fact]
    public void Non_object_arguments_are_rejected()
    {
        Assert.Single(ToolArguments.Problems(Schema, Json("""["Nile"]""")));
    }

    [Fact]
    public void The_expected_shape_lists_fields_with_types_and_optionality()
    {
        Assert.Equal("""{"query": <string>, "limit": <integer> (optional)}""", ToolArguments.Expected(Schema));
    }
}

public class ToolArgumentCoercionTests
{
    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {"type": "object",
         "properties": {"query": {"type": "string"}, "limit": {"type": "integer"}, "exact": {"type": "boolean"}},
         "required": ["query"]}
        """).RootElement.Clone();

    private static JsonElement Coerce(string arguments) =>
        ToolArguments.Coerce(Schema, JsonDocument.Parse(arguments).RootElement);

    [Fact]
    public void Numeric_and_boolean_strings_become_the_schema_type()
    {
        var coerced = Coerce("""{"query": "Volga", "limit": "5", "exact": "true"}""");

        Assert.Equal(5, coerced.GetProperty("limit").GetInt64());
        Assert.True(coerced.GetProperty("exact").GetBoolean());
        Assert.Empty(ToolArguments.Problems(Schema, coerced));
    }

    [Fact]
    public void Strings_that_do_not_parse_are_left_for_validation_to_reject()
    {
        var coerced = Coerce("""{"query": "Volga", "limit": "five"}""");

        Assert.Equal("five", coerced.GetProperty("limit").GetString());
        Assert.Contains("'limit' should be integer, got String", ToolArguments.Problems(Schema, coerced));
    }

    [Fact]
    public void String_properties_stay_strings_even_when_they_look_numeric()
    {
        Assert.Equal("1984", Coerce("""{"query": "1984"}""").GetProperty("query").GetString());
    }
}
