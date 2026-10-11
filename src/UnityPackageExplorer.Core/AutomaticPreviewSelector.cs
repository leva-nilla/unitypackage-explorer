namespace UnityPackageExplorer.Core;

/// <summary>Chooses one inexpensive, deterministic preview candidate from catalog metadata.</summary>
public static class AutomaticPreviewSelector
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".tif", ".tiff"];

    public static UnityPackageEntry? Select(IEnumerable<UnityPackageEntry> entries, string sourceName)
    {
        var available = entries.Where(x => x.HasAsset).ToArray();
        var prefabs = available.Where(x => x.Extension == ".prefab").ToArray();
        if (prefabs.Length > 0)
        {
            var source = Path.GetFileNameWithoutExtension(sourceName);
            return prefabs.OrderBy(x => IsDemo(x.PathName))
                .ThenBy(IsSecondaryPrefab)
                .ThenByDescending(x => IsSourceRelated(x.PathName, source))
                .ThenBy(x => PathDepth(x.PathName))
                .ThenBy(x => x.PathName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.PathName, StringComparer.Ordinal)
                .First();
        }
        return available.Where(x => x.Extension is ".fbx")
            .OrderBy(x => x.PathName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.PathName, StringComparer.Ordinal).FirstOrDefault()
            ?? available.Where(x => ImageExtensions.Contains(x.Extension, StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.PathName, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.PathName, StringComparer.Ordinal).FirstOrDefault();
    }

    private static bool IsDemo(string path) => path.Split('/', '\\').Any(part => part.Contains("demo", StringComparison.OrdinalIgnoreCase) || part.Contains("sample", StringComparison.OrdinalIgnoreCase));
    private static bool IsSourceRelated(string path, string source) => !string.IsNullOrWhiteSpace(source) && Path.GetFileNameWithoutExtension(path).Contains(source, StringComparison.OrdinalIgnoreCase);
    private static bool IsSecondaryPrefab(UnityPackageEntry entry)
    {
        var path = entry.AssetPathName ?? entry.PathName;
        return path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).SkipLast(1)
            .Any(segment => segment.Equals("HeadAcc", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Accessories", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("Parts", StringComparison.OrdinalIgnoreCase));
    }
    private static int PathDepth(string path) => path.Count(character => character is '/' or '\\');
}
