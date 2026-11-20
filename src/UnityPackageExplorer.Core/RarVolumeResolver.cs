using System.Globalization;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

/// <summary>Resolves a known split RAR by its on-disk naming scheme, never by archive contents.</summary>
internal static class RarVolumeResolver
{
    private const int MaximumVolumeCount = 256;
    private static readonly Regex Modern = new(@"^(?<stem>.+)\.part(?<number>[0-9]{1,9})\.rar$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Legacy = new(@"^(?<stem>.+)\.r(?<number>[0-9]{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static bool IsLegacyPart(string path) => Legacy.IsMatch(Path.GetFileName(path));

    internal static IReadOnlyList<string> Resolve(string path, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var modern = Modern.Match(fileName);
        var legacy = Legacy.Match(fileName);
        var stem = modern.Success ? modern.Groups["stem"].Value
            : legacy.Success ? legacy.Groups["stem"].Value : Path.GetFileNameWithoutExtension(path);
        var pattern = modern.Success ? Modern : Legacy;
        var firstNumber = modern.Success ? 1 : 0;
        var digits = modern.Success ? modern.Groups["number"].Length : 2;
        var firstName = stem + ".rar";
        string? legacyFirst = null;
        var numbered = new SortedDictionary<int, string>();
        foreach (var candidate in Directory.EnumerateFiles(directory, "*",
            new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(candidate);
            if (!modern.Success && string.Equals(firstName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (legacyFirst is not null) throw Duplicate(name);
                legacyFirst = candidate;
                continue;
            }
            var match = pattern.Match(name);
            if (!match.Success || !string.Equals(stem, match.Groups["stem"].Value, StringComparison.OrdinalIgnoreCase))
                continue;
            var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
            if (number < firstNumber)
                throw new InvalidDataException($"分割RARの番号が正しくありません: {name}");
            if (!numbered.TryAdd(number, candidate)) throw Duplicate(name);
            if (numbered.Count + (modern.Success ? 0 : 1) > MaximumVolumeCount)
                throw new InvalidDataException($"分割RARのファイル数が上限（{MaximumVolumeCount}）を超えています。");
        }

        var result = new List<string>();
        if (!modern.Success) result.Add(legacyFirst ?? throw Missing(firstName));
        var expected = firstNumber;
        foreach (var (number, candidate) in numbered)
        {
            if (number != expected) throw Missing(PartName(expected));
            result.Add(candidate);
            expected++;
        }
        if (result.Count < 2) throw Missing(PartName(expected));
        if (!result.Contains(path, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
            throw new InvalidDataException("選択したファイルが分割RARのセットに含まれていません。");
        return result;

        string PartName(int number) => stem + (modern.Success ? ".part" : ".r")
            + number.ToString($"D{digits}", CultureInfo.InvariantCulture) + (modern.Success ? ".rar" : "");
    }

    internal static InvalidDataException Incomplete() =>
        new("分割RARの一部が見つからないか、分割ファイルの組み合わせが正しくありません。すべてのファイルを同じフォルダーに置いてください。");

    private static InvalidDataException Missing(string fileName) =>
        new($"分割RARの一部が見つかりません: {fileName}。すべての分割ファイルを同じフォルダーに置いてください。");

    private static InvalidDataException Duplicate(string fileName) =>
        new($"同じ番号または名前の分割RARが複数あります: {fileName}");
}
