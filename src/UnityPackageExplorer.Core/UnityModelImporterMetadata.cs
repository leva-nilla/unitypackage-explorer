using System.Text.Json;

namespace UnityPackageExplorer.Core;

/// <summary>Reads explicit FBX material-name remaps; never infers a mapping by slot order.</summary>
public static class UnityModelImporterMetadata
{
    public static IReadOnlyDictionary<string, UnityAssetReference> ParseMaterialRemaps(string? text)
    {
        var result = new Dictionary<string, UnityAssetReference>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return result;
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        var importer = false;
        var inBlock = false;
        var blockIndent = 0;
        var material = false;
        string? name = null;
        while (reader.ReadLine() is { } line)
        {
            var value = line.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (!importer)
            {
                importer = indent == 0 && value == "ModelImporter:";
                continue;
            }
            if (indent == 0) break;
            if (!inBlock)
            {
                if (!value.StartsWith("externalObjects:", StringComparison.Ordinal)) continue;
                if (value["externalObjects:".Length..].Trim().Length != 0) break;
                inBlock = true;
                blockIndent = indent;
                continue;
            }
            if (indent < blockIndent || (indent == blockIndent && !value.StartsWith('-'))) break;
            if (value == "- first:")
            {
                material = false;
                name = null;
            }
            else if (value.StartsWith("type:", StringComparison.Ordinal))
                material = value["type:".Length..].Trim() == "UnityEngine:Material";
            else if (value.StartsWith("name:", StringComparison.Ordinal))
                name = Unquote(value["name:".Length..].Trim());
            else if (value.StartsWith("second:", StringComparison.Ordinal))
            {
                if (material && name is { Length: > 0 } && ParseReference(value["second:".Length..].Trim()) is { } reference
                    && !ambiguous.Contains(name))
                {
                    if (result.TryGetValue(name, out var previous) && previous != reference)
                    {
                        result.Remove(name);
                        ambiguous.Add(name);
                    }
                    else result[name] = reference;
                }
                material = false;
                name = null;
            }
        }
        return result;
    }

    private static UnityAssetReference? ParseReference(string value)
    {
        if (!value.StartsWith('{') || !value.EndsWith('}')) return null;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in value[1..^1].Split(','))
        {
            var pair = field.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) return null;
        }
        if (!fields.TryGetValue("guid", out var guid) || guid.Length != 32 || !guid.All(Uri.IsHexDigit)
            || guid.All(c => c == '0')
            || !fields.TryGetValue("fileID", out var fileId) || !long.TryParse(fileId, out var id) || id == 0
            || !fields.TryGetValue("type", out var type) || type != "2") return null;
        return new UnityAssetReference(id, guid.ToLowerInvariant());
    }

    private static string? Unquote(string value)
    {
        if (value.StartsWith('\''))
            return value.Length >= 2 && value.EndsWith('\'') ? value[1..^1].Replace("''", "'", StringComparison.Ordinal) : null;
        if (!value.StartsWith('"')) return value;
        try { return JsonSerializer.Deserialize<string>(value); }
        catch (JsonException) { return null; }
    }
}
