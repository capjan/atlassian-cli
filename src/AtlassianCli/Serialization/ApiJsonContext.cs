using System.Text.Json;
using System.Text.Json.Serialization;

namespace AtlassianCli.Serialization;

public sealed record ListResult(
    JsonElement[] Results,
    int Start,
    int Limit,
    int Size,
    long? Total,
    bool HasMore);

public sealed record OperationMessage(string Message);
public sealed record VersionInfo(string Name, string Version);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(ListResult))]
[JsonSerializable(typeof(OperationMessage))]
[JsonSerializable(typeof(VersionInfo))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
