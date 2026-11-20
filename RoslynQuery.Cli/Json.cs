using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using RoslynQuery.Protocol;

namespace RoslynQuery.Cli;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(RunRequest))]
[JsonSerializable(typeof(RunResponse))]
[JsonSerializable(typeof(BridgeHostInfo))]
internal sealed partial class CliJson : JsonSerializerContext;

/// <summary>
/// The generated context with the default encoder swapped out: the default escapes <c>'</c>, <c>+</c>, <c>&lt;</c> and
/// every non-ASCII character as \uXXXX, which is pure token overhead for the code snippets this prints.
/// </summary>
internal static class CliJsonOut
{
    public static readonly JsonTypeInfo<RunResponse> Compact = Make(false);
    public static readonly JsonTypeInfo<RunResponse> Pretty = Make(true);

    private static JsonTypeInfo<RunResponse> Make(bool indented) =>
        new CliJson(new JsonSerializerOptions(CliJson.Default.Options)
        {
            WriteIndented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }).RunResponse;
}
