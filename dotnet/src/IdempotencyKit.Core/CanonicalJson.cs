using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace IdempotencyKit.Core;

/// <summary>
/// IK-CJSON/1 (not RFC 8785): ordinal UTF-16 key ordering; non-ASCII escaped
/// as lowercase \uXXXX; exact decimal-number normalization without float rounding.
/// </summary>
internal static class CanonicalJson
{
    private const int MaximumBodyBytes = 65_536;
    private const int MaximumDepth = 32;
    private const long MaximumDecimalExponent = 1_000_000;

    internal static string Canonicalize(string bodyJson)
    {
        ArgumentNullException.ThrowIfNull(bodyJson);
        ValidateUtf16(bodyJson);
        if (Encoding.UTF8.GetByteCount(bodyJson) > MaximumBodyBytes)
            throw new ArgumentException("JSON body exceeds the 64 KiB limit.", nameof(bodyJson));

        using var document = JsonDocument.Parse(bodyJson, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = MaximumDepth
        });

        RejectUnpairedEscapes(bodyJson);
        var builder = new StringBuilder();
        WriteValue(builder, document.RootElement, 0);
        return builder.ToString();
    }

    internal static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        ValidateUtf16(value);
        var builder = new StringBuilder();
        WriteQuote(builder, value);
        return builder.ToString();
    }

    internal static string HashV1(string preimage)
        => "v1:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));

    private static void WriteValue(StringBuilder builder, JsonElement value, int depth)
    {
        if (depth > MaximumDepth)
            throw new ArgumentException("JSON nesting exceeds the supported depth.");

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = new List<JsonProperty>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                        throw new ArgumentException("Duplicate JSON object key.");
                    properties.Add(property);
                }
                properties.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
                builder.Append('{');
                for (int i = 0; i < properties.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    WriteQuote(builder, properties[i].Name);
                    builder.Append(':');
                    WriteValue(builder, properties[i].Value, depth + 1);
                }
                builder.Append('}');
                return;

            case JsonValueKind.Array:
                builder.Append('[');
                bool first = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!first) builder.Append(',');
                    WriteValue(builder, item, depth + 1);
                    first = false;
                }
                builder.Append(']');
                return;

            case JsonValueKind.String:
                WriteQuote(builder, value.GetString()!);
                return;
            case JsonValueKind.Number:
                builder.Append(NormalizeNumber(value.GetRawText()));
                return;
            case JsonValueKind.True:
                builder.Append("true");
                return;
            case JsonValueKind.False:
                builder.Append("false");
                return;
            case JsonValueKind.Null:
                builder.Append("null");
                return;
            default:
                throw new ArgumentException("Unsupported JSON value.");
        }
    }

    private static void WriteQuote(StringBuilder builder, string value)
    {
        ValidateUtf16(value);
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '"') builder.Append("\\\"");
            else if (c == '\\') builder.Append("\\\\");
            else if (c < ' ' || c > '~')
                builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(c);
        }
        builder.Append('"');
    }

    private static string NormalizeNumber(string source)
    {
        int marker = source.IndexOf('e');
        if (marker < 0) marker = source.IndexOf('E');
        string mantissa = marker < 0 ? source : source[..marker];
        long exponent = 0;
        if (marker >= 0 && !long.TryParse(source[(marker + 1)..],
            NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
            throw new ArgumentException("JSON exponent is out of range.");
        if (Math.Abs(exponent) > MaximumDecimalExponent)
            throw new ArgumentException("JSON exponent exceeds the v1 limit.");

        bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
        if (negative) mantissa = mantissa[1..];
        int dot = mantissa.IndexOf('.');
        string fraction = dot < 0 ? "" : mantissa[(dot + 1)..];
        string digits = ((dot < 0 ? mantissa : mantissa[..dot]) + fraction).TrimStart('0');
        if (digits.Length == 0) return "0";

        int trailing = 0;
        for (int i = digits.Length - 1; i >= 0 && digits[i] == '0'; i--) trailing++;
        if (trailing > 0) digits = digits[..^trailing];
        long scale = exponent - fraction.Length + trailing;
        if (Math.Abs(scale) > MaximumDecimalExponent)
            throw new ArgumentException("Normalized JSON exponent exceeds the v1 limit.");
        return (negative ? "-" : "") + digits +
            (scale == 0 ? "" : "e" + scale.ToString(CultureInfo.InvariantCulture));
    }

    private static void ValidateUtf16(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                    throw new ArgumentException("Unpaired high surrogate.");
                i++;
            }
            else if (char.IsLowSurrogate(c))
                throw new ArgumentException("Unpaired low surrogate.");
        }
    }

    private static void RejectUnpairedEscapes(string source)
    {
        bool inString = false;
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] == '"') { inString = !inString; continue; }
            if (!inString || source[i] != '\\') continue;
            if (source[i + 1] != 'u') { i++; continue; }
            int code = int.Parse(source.Substring(i + 2, 4), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            if (code is >= 0xD800 and <= 0xDBFF)
            {
                if (i + 12 > source.Length || source[i + 6] != '\\' || source[i + 7] != 'u')
                    throw new ArgumentException("Unpaired escaped high surrogate.");
                int low = int.Parse(source.Substring(i + 8, 4), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                if (low is < 0xDC00 or > 0xDFFF)
                    throw new ArgumentException("Unpaired escaped high surrogate.");
                i += 11;
            }
            else if (code is >= 0xDC00 and <= 0xDFFF)
                throw new ArgumentException("Unpaired escaped low surrogate.");
            else i += 5;
        }
    }
}
