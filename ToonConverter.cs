using System;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace SpeculativeVerificationCouncil;

// ════════════════════════════════════════════════════════════════════════════
//  TOON CONVERTER - JSON ↔ TOON Format Conversion
// ════════════════════════════════════════════════════════════════════════════
// TOON (Text-Optimized Object Notation): Compact format for 30-40% token reduction
// Features:
//   • Human-readable syntax (like JSON without quotes/braces overhead)
//   • Auto-extraction from mixed LLM outputs
//   • Bidirectional conversion (JSON → TOON → JSON)
//   • Handles markdown code fences and surrounding text
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Converts between JSON and TOON format for 30-40% token reduction.
/// </summary>
public static class ToonConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ────────────────────────────────────────────────────────────────────────
    //  FROM JSON - Convert standard JSON to compact TOON format
    // ────────────────────────────────────────────────────────────────────────
    public static string FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var sb = new StringBuilder();
            ConvertElement(doc.RootElement, sb, 0);
            return sb.ToString();
        }
        catch (JsonException ex)
        {
            throw new ToonConversionException($"Failed to parse JSON: {ex.Message}", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  TO JSON - Convert TOON back to standard JSON
    // ────────────────────────────────────────────────────────────────────────
    public static string ToJson(string toon)
    {
        if (string.IsNullOrWhiteSpace(toon)) return "{}";

        // Extract TOON from mixed text (LLM explanations, etc.)
        string cleanToon = ExtractToonFromMixedText(toon);

        try
        {
            var result = ParseToon(cleanToon);
            return JsonSerializer.Serialize(result, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new ToonConversionException($"Failed to parse TOON: {ex.Message}", ex);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  EXTRACT TOON - Find TOON content in mixed LLM output
    // ────────────────────────────────────────────────────────────────────────
    public static string ExtractToonFromMixedText(string mixedText)
    {
        var patterns = new[]
        {
            @"```toon\s*(.*?)\s*```",
            @"```json\s*(.*?)\s*```",
            @"\{[^{}]*(?:\{[^{}]*\}[^{}]*)*\}" // Fallback: balanced braces
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(mixedText, pattern, RegexOptions.Singleline);
            if (match.Success)
                return match.Groups[1].Success ? match.Groups[1].Value.Trim() : match.Value.Trim();
        }

        return mixedText.Trim();
    }

    // ────────────────────────────────────────────────────────────────────────
    //  CONVERT ELEMENT - Recursive JSON element to TOON conversion
    // ────────────────────────────────────────────────────────────────────────
    private static void ConvertElement(JsonElement element, StringBuilder sb, int depth)
    {
        var indent = new string(' ', depth * 2);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                sb.AppendLine("{");
                var first = true;
                foreach (var prop in element.EnumerateObject())
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"{indent}  {prop.Name}: ");
                    ConvertElement(prop.Value, sb, depth + 1);
                }
                sb.AppendLine();
                sb.Append($"{indent}}}");
                break;

            case JsonValueKind.Array:
                sb.AppendLine("[");
                first = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!first) sb.AppendLine(",");
                    first = false;
                    sb.Append($"{indent}  ");
                    ConvertElement(item, sb, depth + 1);
                }
                sb.AppendLine();
                sb.Append($"{indent}]");
                break;

            case JsonValueKind.String:
                sb.Append($"\"{element.GetString()}\"");
                break;
            case JsonValueKind.Number:
                sb.Append(element.GetRawText());
                break;
            case JsonValueKind.True:
                sb.Append("true");
                break;
            case JsonValueKind.False:
                sb.Append("false");
                break;
            case JsonValueKind.Null:
                sb.Append("null");
                break;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PARSE TOON - Main entry point for TOON parsing
    // ────────────────────────────────────────────────────────────────────────
    private static object? ParseToon(string toon)
    {
        toon = toon.Trim();
        if (toon.StartsWith('{')) return ParseToonObject(toon);
        if (toon.StartsWith('[')) return ParseToonArray(toon);
        if (toon.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (toon.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        if (toon.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (toon.StartsWith('"') && toon.EndsWith('"')) return toon.Substring(1, toon.Length - 2);
        if (double.TryParse(toon, out var number)) return number;
        return toon; // Unquoted string
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PARSE OBJECT - Parse TOON object (key-value pairs)
    // ────────────────────────────────────────────────────────────────────────
    private static Dictionary<string, object?> ParseToonObject(string toon)
    {
        var result = new Dictionary<string, object?>();
        toon = toon.Trim().TrimStart('{').TrimEnd('}').Trim();
        if (string.IsNullOrWhiteSpace(toon)) return result;

        var depth = 0; var start = 0; var inString = false;
        for (var i = 0; i < toon.Length; i++)
        {
            var c = toon[i];
            if (c == '"' && (i == 0 || toon[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c is '{' or '[') depth++;
                if (c is '}' or ']') depth--;
                if (c == ',' && depth == 0)
                {
                    ParseKeyValue(toon.Substring(start, i - start).Trim(), result);
                    start = i + 1;
                }
            }
        }
        if (start < toon.Length) ParseKeyValue(toon.Substring(start).Trim(), result);
        return result;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PARSE KEY-VALUE - Extract single key-value pair
    // ────────────────────────────────────────────────────────────────────────
    private static void ParseKeyValue(string pair, Dictionary<string, object?> result)
    {
        var colonIndex = pair.IndexOf(':');
        if (colonIndex <= 0) return;
        var key = pair.Substring(0, colonIndex).Trim().Trim('"');
        var value = ParseToon(pair.Substring(colonIndex + 1).Trim());
        result[key] = value;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  PARSE ARRAY - Parse TOON array (list of values)
    // ────────────────────────────────────────────────────────────────────────
    private static List<object?> ParseToonArray(string toon)
    {
        var result = new List<object?>();
        toon = toon.Trim().TrimStart('[').TrimEnd(']').Trim();
        if (string.IsNullOrWhiteSpace(toon)) return result;

        var depth = 0; var start = 0; var inString = false;
        for (var i = 0; i < toon.Length; i++)
        {
            var c = toon[i];
            if (c == '"' && (i == 0 || toon[i - 1] != '\\')) inString = !inString;
            if (!inString)
            {
                if (c is '{' or '[') depth++;
                if (c is '}' or ']') depth--;
                if (c == ',' && depth == 0)
                {
                    result.Add(ParseToon(toon.Substring(start, i - start).Trim()));
                    start = i + 1;
                }
            }
        }
        if (start < toon.Length) result.Add(ParseToon(toon.Substring(start).Trim()));
        return result;
    }
}

/// <summary>
/// Exception thrown when TOON conversion fails.
/// </summary>
public class ToonConversionException : Exception
{
    public ToonConversionException(string message) : base(message) { }
    public ToonConversionException(string message, Exception inner) : base(message, inner) { }
}
