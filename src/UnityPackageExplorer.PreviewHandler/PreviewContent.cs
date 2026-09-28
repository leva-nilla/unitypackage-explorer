using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.PreviewHandler;

internal sealed record PreviewContent(Bitmap Image, string Summary) : IDisposable
{
    private const long MaximumPreviewImageBytes = 64L * 1024 * 1024;

    public void Dispose() => Image.Dispose();

    public static PreviewContent Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".unitypackage" => ReadPackage(Path.GetFileName(path), new UnityPackageReader(path)),
            ".zip" => ReadZip(path),
            ".prefab" => ReadPrefab(path),
            _ => Create(path, "Unity アセット", ["対応形式: .unitypackage / .zip / .prefab"], null)
        };
    }

    private static PreviewContent ReadZip(string path)
        => ReadZip(Path.GetFileName(path), new UnityZipReader(path));

    internal static PreviewContent ReadZip(string title, UnityZipReader reader)
    {
        var catalog = UnityAssetCatalog.OpenZip(reader);
        var assets = catalog.Entries.Where(x => x.HasAsset).ToArray();
        var packages = assets.Where(x => x.PackageMemberPath is not null).Select(x => x.PackageMemberPath!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var loose = assets.Where(x => x.ZipMemberPath is not null).ToArray();
        var lines = new List<string>();
        if (packages.Length > 0)
        {
            lines.Add($"同梱 UnityPackage: {string.Join("、", packages.Take(3).Select(Path.GetFileName))}{(packages.Length > 3 ? $" ほか {packages.Length - 3:N0} 個" : string.Empty)}");
        }
        var prefabPaths = assets.Where(x => x.Extension == ".prefab").Select(x => x.AssetPathName ?? x.PathName).ToArray();
        if (prefabPaths.Length > 0)
            lines.Add($"Prefab: {string.Join("、", prefabPaths.Take(4).Select(Path.GetFileNameWithoutExtension))}{(prefabPaths.Length > 4 ? $" ほか {prefabPaths.Length - 4:N0} 個" : string.Empty)}");
        var topFolders = assets.Select(x => x.PackageMemberPath is null ? x.PathName : x.AssetPathName ?? x.PathName)
            .Select(x => x.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Select(parts => parts.Length > 1 && string.Equals(parts[0], "Assets", StringComparison.OrdinalIgnoreCase) ? parts[1] : parts.FirstOrDefault())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        if (topFolders.Length > 0) lines.Add($"上位フォルダー: {string.Join("、", topFolders)}");
        var kindCounts = assets.GroupBy(x => x.Kind)
            .OrderBy(x => x.Key).Select(x => $"{x.Key}: {x.Count():N0}");
        lines.AddRange(kindCounts);
        lines.AddRange(loose.Take(3).Select(x => $"ZIP直収録: {x.PathName}"));
        lines.AddRange(catalog.Warnings.Take(3).Select(x => $"注意: {x}"));

        Bitmap? image = null;
        var representativeImage = assets.Where(x => x.Kind == "Image").Take(8);
        foreach (var entry in representativeImage)
        {
            try
            {
                var bytes = catalog.ReadAsset(entry, maximumBytes: MaximumPreviewImageBytes);
                if (bytes is not null) image = DecodeImage(bytes);
            }
            catch (Exception) { }
            if (image is not null)
            {
                lines.Insert(0, $"代表画像: {entry.PathName}");
                break;
            }
        }
        return Create(Path.GetFileName(title), $"UnityPackage {packages.Length:N0} · assets {assets.Length:N0} · direct files {loose.Length:N0}", lines, image);
    }

    internal static PreviewContent ReadPackage(string title, UnityPackageReader reader) => ReadPackage(reader, title);

    private static PreviewContent ReadPackage(UnityPackageReader reader, string title)
    {
        var index = reader.ReadIndex();
        var assets = index.Where(x => x.HasAsset).ToArray();
        var kindCounts = assets.GroupBy(x => x.Kind).OrderBy(x => x.Key).ToArray();
        var prefabNames = assets.Where(x => x.Extension == ".prefab").Select(x => Path.GetFileName(x.PathName)).Take(8).ToArray();
        var lines = kindCounts.Select(x => $"{x.Key}: {x.Count():N0}").Concat(prefabNames.Length == 0 ? [] : ["", "Prefab", .. prefabNames]).ToArray();
        Bitmap? image = null;
        foreach (var imageEntry in assets.Where(x => x.Kind == "Image"))
        {
            try
            {
                var bytes = reader.ReadAsset(imageEntry.Guid, maximumBytes: MaximumPreviewImageBytes);
                if (bytes is not null) image = DecodeImage(bytes);
            }
            catch (Exception) { }
            if (image is not null) break;
        }
        return Create(Path.GetFileName(title), $"{assets.Length:N0} files", lines, image);
    }

    private static PreviewContent ReadPrefab(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var parsed = UnityPrefabParser.Parse(text);
        Bitmap? image = null;
        string? texturePath = null;
        var textureWarnings = new List<string>();
        try
        {
            var catalog = UnityAssetCatalog.OpenProjectPrefab(path);
            var ownGuid = File.Exists(path + ".meta") ? UnityPackageReader.ReadGuid(File.ReadAllText(path + ".meta", Encoding.UTF8)) : null;
            var contextEntry = ownGuid is null ? null : catalog.FindByGuid(ownGuid);
            var rootTransformIds = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
            parsed = UnityPrefabParser.Parse(text, sourceGuid =>
            {
                if (rootTransformIds.TryGetValue(sourceGuid, out var cached)) return cached;
                long? rootTransformId = null;
                try
                {
                    var sourceBytes = catalog.ReadAssetByGuid(sourceGuid, referenceEntry: contextEntry);
                    if (sourceBytes is not null)
                        rootTransformId = UnityPrefabParser.FindRootTransformFileId(Encoding.UTF8.GetString(sourceBytes));
                }
                catch (Exception) { }
                rootTransformIds[sourceGuid] = rootTransformId;
                return rootTransformId;
            });
            var texturePreview = ReadReferencedTexture(catalog, parsed, contextEntry);
            image = texturePreview.Image;
            texturePath = texturePreview.Path;
            textureWarnings.AddRange(texturePreview.Warnings);
        }
        catch (Exception exception)
        {
            textureWarnings.Add($"参照テクスチャを検索できませんでした: {exception.Message}");
        }
        return ReadPrefab(Path.GetFileName(path), parsed, image, texturePath, textureWarnings);
    }

    internal static PreviewContent ReadPrefab(string title, string text)
    {
        var parsed = UnityPrefabParser.Parse(text);
        return ReadPrefab(title, parsed, null, null);
    }

    private static PreviewContent ReadPrefab(string title, PrefabParseResult parsed, Bitmap? image, string? texturePath, IReadOnlyList<string>? textureWarnings = null)
    {
        var names = Flatten(parsed.Roots).Take(14).Select((x, index) => $"{new string(' ', Math.Min(index, 5) * 2)}{x}").ToArray();
        var lines = (texturePath is null ? Enumerable.Empty<string>() : new[] { $"参照テクスチャ: {texturePath}" })
            .Concat(parsed.Warnings.Take(3).Select(x => $"注意: {x}"))
            .Concat((textureWarnings ?? []).Take(3).Select(x => $"注意: {x}"))
            .Concat(names).ToArray();
        return Create(Path.GetFileName(title), $"Prefab hierarchy: {Flatten(parsed.Roots).Count()} objects", lines, image);
    }

    private static (Bitmap? Image, string? Path, IReadOnlyList<string> Warnings) ReadReferencedTexture(UnityAssetCatalog catalog, PrefabParseResult parsed, UnityPackageEntry? contextEntry)
    {
        var warnings = new List<string>();
        Bitmap? image = null;
        string? imagePath = null;
        foreach (var material in EnumerateNodes(parsed.Roots).SelectMany(x => x.Materials).Take(24))
        {
            if (material.Guid is null)
            {
                if (material.FileId != 0) warnings.Add($"Material fileID {material.FileId} has no GUID and cannot be resolved.");
                continue;
            }
            var materialEntry = catalog.FindByGuid(material.Guid, referenceEntry: contextEntry);
            if (materialEntry is null)
            {
                warnings.Add($"Material GUID {material.Guid} was not found.");
                continue;
            }
            if (catalog.ReadAsset(materialEntry) is null)
            {
                warnings.Add($"Material data is missing: {materialEntry.PathName}");
                continue;
            }
            foreach (var unsupportedGuid in catalog.ReadUnsupportedMaterialTextureGuids(material.Guid, referenceEntry: contextEntry).Take(8))
            {
                var unsupportedPath = catalog.ResolvePath(unsupportedGuid, referenceEntry: contextEntry);
                warnings.Add(unsupportedPath is null
                    ? $"Texture GUID {unsupportedGuid} referenced by material '{materialEntry.PathName}' was not found."
                    : $"Texture {unsupportedPath} uses a material slot this preview cannot render.");
            }
            foreach (var textureGuid in catalog.ReadMaterialTextureGuids(material.Guid, referenceEntry: contextEntry).Take(8))
            {
                var textureEntry = catalog.FindByGuid(textureGuid, referenceEntry: contextEntry);
                if (textureEntry is null)
                {
                    warnings.Add($"Texture GUID {textureGuid} referenced by material '{materialEntry.PathName}' was not found.");
                    continue;
                }
                if (textureEntry.Kind != "Image")
                {
                    warnings.Add($"Texture format is not supported: {textureEntry.PathName}");
                    continue;
                }
                try
                {
                    var bytes = catalog.ReadAsset(textureEntry, maximumBytes: MaximumPreviewImageBytes);
                    if (bytes is null)
                    {
                        warnings.Add($"Texture data is missing: {textureEntry.PathName}");
                        continue;
                    }
                    if (image is null)
                    {
                        image = DecodeImage(bytes);
                        imagePath = textureEntry.PathName;
                    }
                }
                catch (Exception exception) { warnings.Add($"Texture could not be previewed: {textureEntry.PathName} ({exception.Message})"); }
            }
        }
        return (image, imagePath, warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<PrefabNode> EnumerateNodes(IEnumerable<PrefabNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children)) yield return child;
        }
    }

    private static IEnumerable<string> Flatten(IEnumerable<PrefabNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.Name;
            foreach (var child in Flatten(node.Children)) yield return child;
        }
    }

    private static Bitmap DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var source = System.Drawing.Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        return new Bitmap(source);
    }

    private static PreviewContent Create(string title, string summary, IReadOnlyList<string> lines, Bitmap? image)
    {
        var bitmap = new Bitmap(760, 390, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(22, 27, 34));
        using var mint = new SolidBrush(Color.FromArgb(117, 213, 194));
        using var white = new SolidBrush(Color.FromArgb(232, 237, 243));
        using var muted = new SolidBrush(Color.FromArgb(152, 165, 180));
        using var card = new SolidBrush(Color.FromArgb(32, 40, 51));
        using var titleFont = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brandFont = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel);
        using var summaryFont = new Font("Segoe UI", 12, FontStyle.Regular, GraphicsUnit.Pixel);
        using var lineFont = new Font("Segoe UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.FillRectangle(mint, 0, 0, 7, 390);
        graphics.DrawString("UNITY ASSET", brandFont, mint, 28, 24);
        graphics.DrawString(Trim(title, 52), titleFont, white, new RectangleF(28, 50, 690, 32));
        graphics.DrawString(summary, summaryFont, muted, 28, 88);

        var imageBounds = new Rectangle(28, 122, 264, 236);
        graphics.FillRoundedRectangle(card, imageBounds, 10);
        if (image is not null)
        {
            var size = Fit(image.Size, new Size(imageBounds.Width - 20, imageBounds.Height - 20));
            var target = new Rectangle(imageBounds.X + (imageBounds.Width - size.Width) / 2, imageBounds.Y + (imageBounds.Height - size.Height) / 2, size.Width, size.Height);
            graphics.DrawImage(image, target);
            image.Dispose();
        }
        else
        {
            using var outline = new Pen(Color.FromArgb(72, 88, 103), 2);
            graphics.DrawRoundedRectangle(outline, new Rectangle(imageBounds.X + 54, imageBounds.Y + 43, 156, 140), 12);
            using var placeholderFont = new Font("Segoe UI", 19, FontStyle.Bold, GraphicsUnit.Pixel);
            graphics.DrawString("ASSET", placeholderFont, mint, imageBounds.X + 92, imageBounds.Y + 100);
        }

        var y = 126f;
        foreach (var line in lines.Take(10))
        {
            var isHeader = line.StartsWith("Prefab:", StringComparison.Ordinal)
                || line.StartsWith("上位フォルダー:", StringComparison.Ordinal)
                || line.StartsWith("注意:", StringComparison.Ordinal);
            var font = isHeader ? summaryFont : lineFont;
            var color = isHeader ? mint : white;
            var bounds = new RectangleF(322, y, 398, 27);
            graphics.DrawString(FitToWidth(graphics, font, line, bounds.Width), font, color, bounds);
            y += 30;
            if (y > 352) break;
        }
        return new PreviewContent(bitmap, summary);
    }

    private static Size Fit(Size source, Size bounds)
    {
        var scale = Math.Min((double)bounds.Width / Math.Max(source.Width, 1), (double)bounds.Height / Math.Max(source.Height, 1));
        return new Size(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
    }

    private static string Trim(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string FitToWidth(Graphics graphics, Font font, string text, float width)
    {
        if (graphics.MeasureString(text, font).Width <= width) return text;
        const string ellipsis = "…";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (graphics.MeasureString(text[..middle] + ellipsis, font).Width <= width) low = middle;
            else high = middle - 1;
        }
        return text[..low] + ellipsis;
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle rect, int radius)
    {
        using var path = Rounded(rect, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle rect, int radius)
    {
        using var path = Rounded(rect, radius);
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath Rounded(Rectangle rect, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
