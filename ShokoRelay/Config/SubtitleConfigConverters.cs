using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShokoRelay.Config;

/// <summary>Isolates malformed rule lists without changing how other configuration properties are deserialized.</summary>
internal sealed class SubtitleRenameRulesConverter : JsonConverter<List<SubtitleRenameRule>>
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc/>
    public override List<SubtitleRenameRule> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            return [.. document.RootElement.Deserialize<SubtitleRenameRule[]>(options) ?? []];
        }
        catch (JsonException ex)
        {
            s_logger.Warn(ex, "Config: Invalid subtitle rules -> Keeping original subtitle names");
            return [];
        }
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, List<SubtitleRenameRule> value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.ToArray(), options);
}

/// <summary>Reads only string entries from the subtitle format preference, leaving extension validation to the configuration normalizer.</summary>
internal sealed class SubtitleFormatPreferenceConverter : JsonConverter<List<string>>
{
    /// <inheritdoc/>
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var formats = new List<string>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return formats;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String)
                continue;
            try
            {
                formats.Add(entry.Deserialize<string>(options)!);
            }
            catch (JsonException) { } // Invalid Unicode is an invalid extension, not a failure of the whole configuration.
        }
        return formats;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.ToArray(), options);
}
