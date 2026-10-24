using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

/// <summary>Reads the supported, serialized lilToon material data without evaluating YAML content.</summary>
internal static partial class UnityLilToonMaterialParser
{
    private static readonly IReadOnlyDictionary<string, UnityMaterialRenderMode> KnownShaders =
        new Dictionary<string, UnityMaterialRenderMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["df12117ecd77c31469c224178886498e"] = UnityMaterialRenderMode.Opaque,
            ["efa77a80ca0344749b4f19fdd5891cbe"] = UnityMaterialRenderMode.Opaque,
            ["381af8ba8e1740a41b9768ccfb0416c2"] = UnityMaterialRenderMode.Opaque,
            ["583a88005abb81a4ebbce757b4851a0d"] = UnityMaterialRenderMode.Opaque,
            ["85d6126cae43b6847aff4b13f4adb8ec"] = UnityMaterialRenderMode.Cutout,
            ["3b4aa19949601f046a20ca8bdaee929f"] = UnityMaterialRenderMode.Cutout,
            ["b957dce3d03ff5445ac989f8de643c7f"] = UnityMaterialRenderMode.Cutout,
            ["8cf5267d397b04846856f6d3d9561da0"] = UnityMaterialRenderMode.Cutout,
            ["165365ab7100a044ca85fc8c33548a62"] = UnityMaterialRenderMode.Transparent,
            ["3c79b10c7e0b2784aaa4c2f8dd17d55e"] = UnityMaterialRenderMode.Transparent,
            ["0e3ece1bd59542743bccadb21f68318e"] = UnityMaterialRenderMode.Transparent,
            ["1c12a37046f07ac4486881deaf0187ea"] = UnityMaterialRenderMode.Transparent
        };

    private static readonly IReadOnlyDictionary<string, UnityMaterialRenderMode> KnownFiles =
        new Dictionary<string, UnityMaterialRenderMode>(StringComparer.OrdinalIgnoreCase)
        {
            ["lts.shader"] = UnityMaterialRenderMode.Opaque,
            ["lts_o.shader"] = UnityMaterialRenderMode.Opaque,
            ["ltsl.shader"] = UnityMaterialRenderMode.Opaque,
            ["ltsl_o.shader"] = UnityMaterialRenderMode.Opaque,
            ["lts_cutout.shader"] = UnityMaterialRenderMode.Cutout,
            ["lts_cutout_o.shader"] = UnityMaterialRenderMode.Cutout,
            ["ltsl_cutout.shader"] = UnityMaterialRenderMode.Cutout,
            ["ltsl_cutout_o.shader"] = UnityMaterialRenderMode.Cutout,
            ["lts_trans.shader"] = UnityMaterialRenderMode.Transparent,
            ["lts_trans_o.shader"] = UnityMaterialRenderMode.Transparent,
            ["ltsl_trans.shader"] = UnityMaterialRenderMode.Transparent,
            ["ltsl_trans_o.shader"] = UnityMaterialRenderMode.Transparent
        };

    public static bool TryGetRenderMode(string? shaderGuid, string? shaderName, out UnityMaterialRenderMode mode, bool isLilToonAsset = false)
    {
        if (shaderGuid is not null && KnownShaders.TryGetValue(shaderGuid, out mode)) return true;
        var filename = !isLilToonAsset || shaderName is null ? null : Path.GetFileName(shaderName);
        if (filename is not null && KnownFiles.TryGetValue(filename + (filename.EndsWith(".shader", StringComparison.OrdinalIgnoreCase) ? "" : ".shader"), out mode)) return true;
        mode = UnityMaterialRenderMode.Unknown;
        return false;
    }

    public static LilToonMaterialData? Parse(string text, UnityMaterialRenderMode renderMode, string? shaderGuid, string? shaderName, bool isLilToonAsset = false)
    {
        var multi = IsMulti(shaderGuid, shaderName, isLilToonAsset);
        UnityMaterialRenderMode knownMode;
        if (multi)
        {
            if (!TryGetMultiRenderMode(text, shaderGuid, shaderName, isLilToonAsset, out knownMode)) return null;
        }
        else if (!TryGetRenderMode(shaderGuid, shaderName, out knownMode, isLilToonAsset)) return null;

        var properties = new Dictionary<string, object>(StringComparer.Ordinal);
        ParseScalarSection(text, "m_Floats", properties, integer: false);
        ParseScalarSection(text, "m_Ints", properties, integer: true);
        ParseColors(text, properties);
        var textures = ParseTextures(text, properties);
        var outline = IsOutline(shaderGuid, shaderName);
        var effectiveMode = renderMode == UnityMaterialRenderMode.Unknown ? knownMode : renderMode;
        var variant = multi ? (outline ? "multi-outline" : "multi") : outline ? "outline" : null;
        return new LilToonMaterialData(effectiveMode.ToString().ToLowerInvariant(), variant, properties, textures);
    }

    internal static bool TryGetMultiRenderMode(string text, string? guid, string? shaderName, bool isLilToonAsset, out UnityMaterialRenderMode mode)
    {
        mode = UnityMaterialRenderMode.Unknown;
        if (!IsMulti(guid, shaderName, isLilToonAsset)) return false;
        var match = Regex.Match(text, @"(?m)^\s*-\s*_TransparentMode:\s*(?<value>[^\r\n]*)");
        if (match.Success && !TryFinite(match.Groups["value"].Value, out _)) return false;
        var value = match.Success ? float.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture) : 0f;
        mode = value switch { 0 => UnityMaterialRenderMode.Opaque, 1 => UnityMaterialRenderMode.Cutout, 2 => UnityMaterialRenderMode.Transparent, _ => UnityMaterialRenderMode.Unknown };
        return mode != UnityMaterialRenderMode.Unknown;
    }

    private static bool IsMulti(string? guid, string? shaderName, bool trustedAsset) =>
        string.Equals(guid, "9294844b15dca184d914a632279b24e1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(guid, "51b2dee0ab07bd84d8147601ff89e511", StringComparison.OrdinalIgnoreCase)
        || (trustedAsset && shaderName is "ltsmulti" or "ltsmulti_o");

    private static void ParseScalarSection(string text, string section, IDictionary<string, object> properties, bool integer)
    {
        var body = GetSection(text, section);
        if (body is null) return;
        foreach (Match item in PropertyEntryRegex().Matches(body))
        {
            var raw = item.Groups["value"].Value.Trim();
            if (integer)
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) properties[item.Groups["name"].Value] = number;
            }
            else if (TryFinite(raw, out var number)) properties[item.Groups["name"].Value] = number;
        }
    }

    private static void ParseColors(string text, IDictionary<string, object> properties)
    {
        var body = GetSection(text, "m_Colors");
        if (body is null) return;
        foreach (Match item in PropertyEntryRegex().Matches(body))
        {
            var values = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (Match component in ColorComponentRegex().Matches(item.Groups["value"].Value))
                if (TryFinite(component.Groups["value"].Value, out var value)) values[component.Groups["name"].Value] = value;
            if (TryGet(values, "r", "x", out var x) && TryGet(values, "g", "y", out var y) && TryGet(values, "b", "z", out var z))
                properties[item.Groups["name"].Value] = new[] { x, y, z, TryGet(values, "a", "w", out var w) ? w : 1f };
        }
    }

    private static IReadOnlyList<LilToonTextureReference> ParseTextures(string text, IDictionary<string, object> properties)
    {
        var body = GetSection(text, "m_TexEnvs");
        if (body is null) return [];
        var result = new List<LilToonTextureReference>();
        foreach (Match item in TextureEntryRegex().Matches(body))
        {
            var name = item.Groups["name"].Value;
            var block = item.Groups["body"].Value;
            var texture = TextureGuidRegex().Match(block);
            var scale = VectorRegex("m_Scale").Match(block);
            var offset = VectorRegex("m_Offset").Match(block);
            var sx = ReadVector(scale, "x", 1f); var sy = ReadVector(scale, "y", 1f);
            var ox = ReadVector(offset, "x", 0f); var oy = ReadVector(offset, "y", 0f);
            if (texture.Success && !IsZeroGuid(texture.Groups["guid"].Value)
                && sx.HasValue && sy.HasValue && ox.HasValue && oy.HasValue)
                result.Add(new LilToonTextureReference(name, texture.Groups["guid"].Value.ToLowerInvariant(), sx.Value, sy.Value, ox.Value, oy.Value));
            properties[name + "_ST"] = new[] { sx ?? 1f, sy ?? 1f, ox ?? 0f, oy ?? 0f };
        }
        return result;
    }

    private static string? GetSection(string text, string name)
    {
        var match = Regex.Match(text, $"(?m)^(?<indent>[ \\t]*){Regex.Escape(name)}:[ \\t]*(?:\\r?\\n|$)");
        if (!match.Success) return null;
        var indent = match.Groups["indent"].Length;
        var start = match.Index + match.Length;
        var end = text.Length;
        foreach (Match line in Regex.Matches(text[start..], "(?m)^(?<indent>[ \\t]*)(?<key>[A-Za-z][A-Za-z0-9_]*):"))
            if (line.Groups["indent"].Length <= indent) { end = start + line.Index; break; }
        return text[start..end];
    }

    private static bool TryFinite(string raw, out float value) =>
        float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static bool TryGet(IReadOnlyDictionary<string, float> values, string first, string second, out float value) =>
        values.TryGetValue(first, out value) || values.TryGetValue(second, out value);

    private static float? ReadVector(Match match, string component, float fallback)
    {
        if (!match.Success) return fallback;
        var part = Regex.Match(match.Groups["body"].Value, $"(?i)(?:^|,)[ \\t]*{component}[ \\t]*:[ \\t]*(?<value>[^,}}]+)");
        return part.Success && TryFinite(part.Groups["value"].Value, out var value) ? value : null;
    }

    private static bool IsZeroGuid(string guid) => guid.All(c => c == '0');

    internal static bool IsOutline(string? guid, string? shaderName) =>
        (guid is not null && guid is "efa77a80ca0344749b4f19fdd5891cbe" or "583a88005abb81a4ebbce757b4851a0d" or "3b4aa19949601f046a20ca8bdaee929f" or "8cf5267d397b04846856f6d3d9561da0" or "3c79b10c7e0b2784aaa4c2f8dd17d55e" or "1c12a37046f07ac4486881deaf0187ea" or "51b2dee0ab07bd84d8147601ff89e511")
        || (shaderName?.EndsWith("_o", StringComparison.OrdinalIgnoreCase) == true);

    [GeneratedRegex("(?m)^\\s*-\\s*(?<name>_[A-Za-z0-9_]+):\\s*(?<value>[^\\r\\n]*)")]
    private static partial Regex PropertyEntryRegex();
    [GeneratedRegex("(?i)(?<name>[rgba]|[xyzw]):\\s*(?<value>[-+0-9.eE]+)")]
    private static partial Regex ColorComponentRegex();
    [GeneratedRegex("(?ms)^\\s*-\\s*(?<name>_[A-Za-z0-9_]+):\\s*(?<body>.*?)(?=^\\s*-\\s*_[A-Za-z0-9_]+:|^\\S|\\z)")]
    private static partial Regex TextureEntryRegex();
    [GeneratedRegex("(?i)m_Texture:\\s*\\{[^}]*guid:\\s*(?<guid>[0-9a-f]{32})")]
    private static partial Regex TextureGuidRegex();
    private static Regex VectorRegex(string name) => new($"(?is){Regex.Escape(name)}:\\s*\\{{(?<body>[^}}]*)\\}}");
}
