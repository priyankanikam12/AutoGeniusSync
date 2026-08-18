using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoGeniusSync.Json;

// The Job Card payload sends some fields with inconsistent JSON types compared
// to what these DTOs originally expected, e.g.:
//   "uniqueKey": 30261        -> JSON number, DTO property is string?
//   "jobNo": 87               -> JSON number, DTO property is string?
//   "jobDate": "2026-08-13T00:00:00" -> full ISO datetime, DTO property is DateOnly?
//
// Without these converters, System.Text.Json throws a JsonException during
// model binding (400 Bad Request) before the controller ever runs.

/// <summary>Accepts a JSON string OR number and returns it as a string.</summary>
public class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var l)
                    ? l.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);
            default:
                throw new JsonException($"Cannot convert token type {reader.TokenType} to string.");
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>
/// Accepts a JSON string in either "yyyy-MM-dd" or full ISO datetime form
/// (e.g. "2026-08-13T00:00:00") and returns a DateOnly, taking just the date part.
/// </summary>
public class FlexibleDateOnlyConverter : JsonConverter<DateOnly?>
{
    public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        var s = reader.GetString();
        if (string.IsNullOrWhiteSpace(s))
            return null;

        if (DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            return dateOnly;

        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime))
            return DateOnly.FromDateTime(dateTime);

        throw new JsonException($"Cannot convert '{s}' to DateOnly.");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd"));
        else
            writer.WriteNullValue();
    }
}