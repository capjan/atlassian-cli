using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AtlassianCli.Serialization;

public static class JsonValues
{
    public static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static string Text(JsonElement value, params string[] path)
    {
        foreach (string key in path)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out value))
            {
                return "";
            }
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    public static int RequiredInt(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out int number) || number < 0)
        {
            throw new InvalidDataException($"API response is missing a non-negative integer '{name}'.");
        }

        return number;
    }

    public static JsonElement[] RequiredArray(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"API response is missing array '{name}'.");
        }

        JsonElement[] results = array.EnumerateArray().Select(item => item.Clone()).ToArray();
        if (results.Any(item => item.ValueKind != JsonValueKind.Object))
        {
            throw new InvalidDataException($"API response '{name}' contains a non-object item.");
        }

        return results;
    }

    public static string Segment(string value)
        => !string.IsNullOrWhiteSpace(value) && value is not ("." or "..") && !value.Any(char.IsControl)
            ? Uri.EscapeDataString(value)
            : throw new ArgumentException("Identifiers must be non-empty, non-dot path segments without control characters.");

    public static JsonElement RequireIdentity(JsonElement value, string expectedType, string? expectedId = null)
    {
        string id = Text(value, "id");
        if (id.Length == 0 || (expectedId is not null && id != expectedId))
        {
            throw new InvalidDataException($"The API accepted the request but returned no matching {expectedType} ID. Do not blindly retry a mutation.");
        }

        return value;
    }

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static JsonObject Fields(IEnumerable<string> fields, string? jsonFile)
    {
        var result = new JsonObject();
        if (jsonFile is not null)
        {
            JsonNode? parsed = JsonNode.Parse(File.ReadAllText(jsonFile));
            if (parsed is not JsonObject obj || obj.Count == 0)
            {
                throw new ArgumentException("--fields-file must contain a non-empty JSON object of issue fields.");
            }

            result = obj;
        }

        foreach (string field in fields)
        {
            int separator = field.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException($"Invalid --field '{field}'; use name=JSON. String values must be JSON-quoted.");
            }

            string name = field[..separator];
            if (result.ContainsKey(name))
            {
                throw new ArgumentException($"Duplicate issue field '{name}'.");
            }

            result[name] = JsonNode.Parse(field[(separator + 1)..]);
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("Provide at least one --field name=JSON or --fields-file.");
        }

        return result;
    }
}
