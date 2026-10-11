using System.Text;

namespace UnityPackageExplorer.Core;

/// <summary>
/// Matches Unity material names to names imported from model files. Matching is
/// intentionally conservative because a false texture assignment is harder to
/// diagnose than an omitted preview texture.
/// </summary>
public static class UnityMaterialNameMatcher
{
    private const int MaximumFuzzyNameLength = 128;
    private const double FuzzyMatchThreshold = 0.84;
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
    {
        "material", "default", "defaultmaterial", "lambert", "lambert1", "phong", "phong1"
    };

    public static bool AreEquivalent(string? first, string? second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        if (left.Length == 0 || right.Length == 0) return false;
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;

        if (left.Length < 9 || right.Length < 9
            || left.Length > MaximumFuzzyNameLength || right.Length > MaximumFuzzyNameLength
            || GenericNames.Contains(left) || GenericNames.Contains(right))
            return false;

        var longest = Math.Max(left.Length, right.Length);
        return 1d - (DamerauLevenshteinDistance(left, right) / (double)longest) >= FuzzyMatchThreshold;
    }

    public static bool HaveSameNormalizedName(string? first, string? second)
    {
        var left = Normalize(first);
        var right = Normalize(second);
        return left.Length > 0 && string.Equals(left, right, StringComparison.Ordinal);
    }

    /// <summary>Returns distinct Unity materials that safely match an imported material or mesh name.</summary>
    public static IReadOnlyList<string> FindMatchingMaterialGuids(
        IEnumerable<(string Guid, string MaterialName, string? TextureName)> bindings,
        string? importedMaterialName,
        string? meshName)
    {
        return bindings.Where(binding =>
                AreEquivalent(binding.MaterialName, importedMaterialName)
                || AreEquivalent(binding.MaterialName, meshName)
                || (binding.TextureName is { Length: > 0 }
                    && (AreEquivalent(binding.TextureName, importedMaterialName) || AreEquivalent(binding.TextureName, meshName))))
            .Select(binding => binding.Guid)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        string compatible;
        try { compatible = value.Normalize(NormalizationForm.FormKC); }
        catch (ArgumentException) { compatible = value; }

        const string instanceSuffix = "(Instance)";
        compatible = compatible.Trim();
        if (compatible.EndsWith(instanceSuffix, StringComparison.OrdinalIgnoreCase))
            compatible = compatible[..^instanceSuffix.Length].TrimEnd();
        if (HasUnityNumericSuffix(compatible))
            compatible = compatible[..^4];

        var builder = new StringBuilder(compatible.Length);
        foreach (var character in compatible)
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static bool HasUnityNumericSuffix(string value)
    {
        var dot = value.LastIndexOf('.');
        if (dot <= 0 || value.Length - dot != 4) return false;
        for (var index = dot + 1; index < value.Length; index++)
            if (!char.IsAsciiDigit(value[index])) return false;
        return true;
    }

    private static int DamerauLevenshteinDistance(string first, string second)
    {
        var distance = new int[first.Length + 1, second.Length + 1];
        for (var i = 0; i <= first.Length; i++) distance[i, 0] = i;
        for (var j = 0; j <= second.Length; j++) distance[0, j] = j;
        for (var i = 1; i <= first.Length; i++)
        {
            for (var j = 1; j <= second.Length; j++)
            {
                var cost = first[i - 1] == second[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + cost);

                if (i > 1 && j > 1
                    && first[i - 1] == second[j - 2]
                    && first[i - 2] == second[j - 1])
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
            }
        }
        return distance[first.Length, second.Length];
    }
}
