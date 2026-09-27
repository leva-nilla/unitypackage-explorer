using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using UnityPackageExplorer.Core;

namespace UnityPackageExplorer.PreviewHandler;

internal sealed record PreviewContent(Bitmap Image, string Summary) : IDisposable
{
    public void Dispose() => Image.Dispose();

    public static PreviewContent Read(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".unitypackage" => ReadPackage(Path.GetFileName(path), new UnityPackageReader(path)),
            ".prefab" => ReadPrefab(path),
            _ => Create(path, "Unity アセット", ["対応形式: .unitypackage / .prefab"], null)
        };
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
                var bytes = reader.ReadAsset(imageEntry.Guid);
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
        return ReadPrefab(Path.GetFileName(path), text);
    }

    internal static PreviewContent ReadPrefab(string title, string text)
    {
        var parsed = UnityPrefabParser.Parse(text);
        var names = Flatten(parsed.Roots).Take(14).Select((x, index) => $"{new string(' ', Math.Min(index, 5) * 2)}{x}").ToArray();
        var lines = names.Concat(parsed.Warnings.Take(3).Select(x => $"注意: {x}")).ToArray();
        return Create(Path.GetFileName(title), $"Prefab hierarchy: {Flatten(parsed.Roots).Count()} objects", lines, null);
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
            var isHeader = line is "Prefab" or "注意: ";
            graphics.DrawString(Trim(line, 58), isHeader ? summaryFont : lineFont, isHeader ? mint : white, new RectangleF(322, y, 398, 27));
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
