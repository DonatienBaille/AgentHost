using System.Globalization;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace AgentHost.Api.Services;

/// <summary>
/// Projects a YAML document to a JSON tree, keeping every key and resolving plain scalars with the
/// YAML 1.2 core schema (so <c>default: 5</c> stays the number 5 and <c>default: "5"</c> stays the
/// string "5").
///
/// This is **not** a second manifest parser: it never validates, defaults or reshapes anything.
/// <see cref="IAgentManifestParser"/> remains the only authority on what a manifest means. This
/// exists because that parser answers a different question — it applies
/// <c>IgnoreUnmatchedProperties</c> and deserializes free-form subtrees
/// (<c>spec.inputs</c>, <c>spec.outputs</c>) into <c>object</c>, which YamlDotNet fills with
/// strings. Both behaviours are right for running an agent and wrong for telling a UI "here is
/// everything your form does not cover, verbatim".
/// </summary>
public static class YamlDocumentProjection
{
    /// <summary>
    /// Returns the first document of <paramref name="yaml"/> as JSON, or null for an empty input.
    /// Throws the underlying <see cref="YamlDotNet.Core.YamlException"/> on malformed YAML; callers
    /// that already validated through <see cref="IAgentManifestParser"/> will not reach that.
    /// </summary>
    public static JsonNode? ToJson(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return null;

        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0) return null;

        return Convert(stream.Documents[0].RootNode);
    }

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMappingNode mapping => ConvertMapping(mapping),
        YamlSequenceNode sequence => ConvertSequence(sequence),
        YamlScalarNode scalar => ConvertScalar(scalar),
        // An alias resolves to the node it points at, so anchors are expanded rather than kept.
        _ => null,
    };

    private static JsonObject ConvertMapping(YamlMappingNode mapping)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in mapping.Children)
        {
            // Non-scalar keys are legal YAML and meaningless as JSON; their textual form keeps the
            // entry visible to the caller instead of silently dropping it.
            var name = key is YamlScalarNode s ? s.Value ?? string.Empty : key.ToString();
            // A duplicate key is invalid YAML but YamlStream tolerates it; last one wins, as it
            // does in the typed parser.
            obj[name] = Convert(value);
        }
        return obj;
    }

    private static JsonArray ConvertSequence(YamlSequenceNode sequence)
    {
        var array = new JsonArray();
        foreach (var child in sequence.Children)
            array.Add(Convert(child));
        return array;
    }

    private static JsonNode? ConvertScalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? string.Empty;

        // Quoted, literal and folded scalars are strings by construction — only a plain scalar is
        // subject to the core schema's type resolution.
        if (scalar.Style != YamlDotNet.Core.ScalarStyle.Plain && scalar.Style != YamlDotNet.Core.ScalarStyle.Any)
            return JsonValue.Create(text);

        if (text.Length == 0 || text is "~" or "null" or "Null" or "NULL")
            return null;

        if (text is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (text is "false" or "False" or "FALSE") return JsonValue.Create(false);

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            && IsCanonicalNumber(text))
            return JsonValue.Create(integer);

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
            && IsCanonicalNumber(text)
            && !double.IsInfinity(real) && !double.IsNaN(real))
            return JsonValue.Create(real);

        return JsonValue.Create(text);
    }

    /// <summary>
    /// Guards against .NET's parsers being more permissive than YAML's core schema: "1_000",
    /// " 12" and "1d" are strings in YAML, and <c>double.TryParse</c> would take some of them.
    /// </summary>
    private static bool IsCanonicalNumber(string text)
    {
        var i = 0;
        if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
        var seenDigit = false;
        for (; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsAsciiDigit(c)) { seenDigit = true; continue; }
            if (c is '.') continue;
            if (c is 'e' or 'E')
            {
                if (i + 1 < text.Length && (text[i + 1] == '+' || text[i + 1] == '-')) i++;
                continue;
            }
            return false;
        }
        return seenDigit;
    }
}
