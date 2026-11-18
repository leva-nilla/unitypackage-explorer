using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

public sealed record ArchiveBundlePlan(IReadOnlyList<string> SourcePaths, string DestinationDirectory, string ArchiveName);
public sealed record ArchiveBundleProgress(string Stage, string? FileName = null);
public sealed record ArchiveBundleResult(string DirectoryPath, string ArchivePath, IReadOnlyList<string> Warnings);

/// <summary>A rollback could not restore every original. Files in RecoveryDirectory must not be deleted.</summary>
public sealed class ArchiveBundleRecoveryException(string message, string recoveryDirectory, Exception inner)
    : IOException(message, inner)
{
    public string RecoveryDirectory { get; } = recoveryDirectory;
}

/// <summary>Builds and verifies a derived ZIP before moving any originals. Never overwrites a path.</summary>
public static class ArchiveBundleService
{
    public const int MaximumSourceCount = 64;

    public static ArchiveBundlePlan Plan(IEnumerable<string> sourcePaths, string folderName)
    {
        ValidateLeafName(folderName);
        var sources = sourcePaths.Select(Path.GetFullPath).ToArray();
        if (sources.Length is < 2 or > MaximumSourceCount)
            throw new ArgumentException($"ZIPを2～{MaximumSourceCount}個選択してください。");
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("同じZIPが複数回指定されています。");
        var parent = Path.GetDirectoryName(sources[0])!;
        foreach (var source in sources)
        {
            if (!string.Equals(parent, Path.GetDirectoryName(source), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("同じフォルダーにあるZIPを選択してください。");
            if (!Path.GetExtension(source).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("統合できるのはZIPファイルだけです。");
            AssertOrdinaryPath(source);
            if (!File.Exists(source)) throw new FileNotFoundException("元ZIPが見つかりません。", source);
        }
        var destination = Path.Combine(parent, folderName);
        if (Path.Exists(destination)) throw new IOException("同名のフォルダーまたはファイルがあります。別の名前に変更してください。");
        var archiveName = folderName + "_統合.zip";
        ValidateLeafName(archiveName);
        if (sources.Any(source => Path.GetFileName(source).Equals(archiveName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("統合ZIPの名前が元ZIPと重なります。フォルダー名を変更してください。");
        return new ArchiveBundlePlan(Array.AsReadOnly(sources), destination, archiveName);
    }

    public static Task<ArchiveBundleResult> CreateAsync(ArchiveBundlePlan plan,
        IProgress<ArchiveBundleProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Create(plan, progress, cancellationToken), cancellationToken);

    private static ArchiveBundleResult Create(ArchiveBundlePlan requested, IProgress<ArchiveBundleProgress>? progress, CancellationToken token)
    {
        // Revalidate the public plan immediately before doing any filesystem work.
        var plan = Plan(requested.SourcePaths, Path.GetFileName(requested.DestinationDirectory));
        if (!string.Equals(plan.DestinationDirectory, requested.DestinationDirectory, StringComparison.OrdinalIgnoreCase)
            || plan.ArchiveName != requested.ArchiveName)
            throw new ArgumentException("保存先の指定が一致しません。");
        token.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(plan.DestinationDirectory)!;
        var staging = Path.Combine(parent, ".UnityPackageExplorer-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var archivePath = Path.Combine(staging, plan.ArchiveName);
        var moved = new List<(string Original, string Staged)>();
        try
        {
            var sources = new List<(string Path, byte[] Hash)>();
            var expected = new Dictionary<string, (long Length, byte[] Hash)>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var source in plan.SourcePaths)
                {
                    token.ThrowIfCancellationRequested();
                    progress?.Report(new("ZIPをまとめています", Path.GetFileName(source)));
                    using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var sourceHash = Hash(file, token);
                    file.Position = 0;
                    using var input = new ZipArchive(file, ZipArchiveMode.Read);
                    if (input.Entries.Count > UnityArchiveLimits.MaximumEntryCount)
                        throw new InvalidDataException("ZIP内の項目数が上限を超えています。");
                    var added = 0;
                    foreach (var entry in input.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        // Only packages carry over. Text files, textures and other members stay
                        // in the original ZIP, which is kept in the destination folder.
                        if (!IsPackageEntry(entry)) continue;
                        // Packages are flattened to the ZIP root, so the member path itself is
                        // discarded. It is still validated to reject links and unsafe entries.
                        var name = Path.GetFileName(SafeMemberName(entry));
                        if (expected.Count >= UnityArchiveLimits.MaximumEntryCount || expected.ContainsKey(name))
                            throw new InvalidDataException($"同じ名前の .unitypackage が複数あります。名前を変えてから再実行してください。\n{name}");
                        if (entry.Length > UnityArchiveLimits.MaximumStreamedMemberBytes
                            || entry.Length > UnityArchiveLimits.MaximumDeclaredTotalBytes - total)
                            throw new InvalidDataException("ZIPの内容が安全な読込みサイズ上限を超えています。");
                        using var from = entry.Open();
                        // unitypackage/PNG and other assets are commonly compressed already.
                        var target = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        using var to = target.Open();
                        var copied = CopyAndHash(from, to, entry.Length, token);
                        total = checked(total + copied.Length);
                        expected.Add(name, copied);
                        added++;
                    }
                    if (added == 0)
                        throw new InvalidDataException($".unitypackage を含まない ZIP があります。元ZIPは移動していません。\n{Path.GetFileName(source)}");
                    sources.Add((source, sourceHash));
                }
            }
            progress?.Report(new("統合ZIPを検証しています"));
            VerifyArchive(archivePath, expected, token);
            var warnings = VerifyReferences(archivePath, staging, token);
            PreserveInternetZone(plan.SourcePaths, archivePath);
            // Originals stay at their original paths through all validation/cancellation.
            token.ThrowIfCancellationRequested();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("元ZIPをフォルダーへ移動しています", Path.GetFileName(source.Path)));
                AssertOrdinaryPath(source.Path);
                using (var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (!Hash(file, token).AsSpan().SequenceEqual(source.Hash))
                        throw new IOException("作業中に元ZIPが変更されたため、中止しました。");
                AssertOrdinaryPath(staging);
                var target = Path.Combine(staging, Path.GetFileName(source.Path));
                File.Move(source.Path, target, overwrite: false);
                moved.Add((source.Path, target));
                using var movedFile = File.OpenRead(target);
                if (!Hash(movedFile, token).AsSpan().SequenceEqual(source.Hash))
                    throw new IOException("移動した元ZIPの内容が一致しません。");
            }
            token.ThrowIfCancellationRequested();
            // Atomic publish in the same parent. A racing existing destination is never merged.
            AssertOrdinaryPath(staging);
            Directory.Move(staging, plan.DestinationDirectory);
            return new(plan.DestinationDirectory, Path.Combine(plan.DestinationDirectory, plan.ArchiveName), warnings);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var item in moved.AsEnumerable().Reverse())
            {
                try
                {
                    AssertOrdinaryPath(staging);
                    AssertOrdinaryPath(Path.GetDirectoryName(item.Original)!);
                    File.Move(item.Staged, item.Original, overwrite: false);
                }
                catch (Exception rollback) { rollbackErrors.Add(rollback); }
            }
            if (rollbackErrors.Count > 0)
                throw new ArchiveBundleRecoveryException(
                    "一部の元ZIPを元の場所へ戻せませんでした。作業フォルダーに保管しています。削除せず内容を確認してください。\n" + staging,
                    staging, new AggregateException(new[] { error }.Concat(rollbackErrors)));
            // Delete only our known output and empty folders, never recursively remove originals.
            try
            {
                AssertOrdinaryPath(staging);
                if (File.Exists(archivePath)) File.Delete(archivePath);
                Directory.Delete(staging, recursive: false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static void VerifyArchive(string path, Dictionary<string, (long Length, byte[] Hash)> expected, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != expected.Count) throw new InvalidDataException("統合ZIPの項目数が一致しません。");
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!expected.TryGetValue(entry.FullName, out var original) || entry.Length != original.Length)
                throw new InvalidDataException("統合ZIPの内容が一致しません。");
            using var input = entry.Open();
            var actual = CopyAndHash(input, Stream.Null, original.Length, token);
            if (!actual.Hash.AsSpan().SequenceEqual(original.Hash))
                throw new InvalidDataException("統合ZIPの検証に失敗しました。元ZIPは移動しません。");
        }
    }

    private static IReadOnlyList<string> VerifyReferences(string path, string staging, CancellationToken token)
    {
        var spill = Path.Combine(staging, ".validation");
        Directory.CreateDirectory(spill);
        try
        {
            using var catalog = UnityAssetCatalog.OpenZip(path, token, expandedCacheSpillDirectory: spill);
            if (catalog.Warnings.Count > 0)
                throw new InvalidDataException("パッケージを安全に確認できませんでした。\n" + string.Join("\n", catalog.Warnings.Take(4)));
            var duplicates = 0;
            foreach (var group in catalog.Entries.Where(entry => entry.HasAsset && entry.HasMeta)
                .GroupBy(entry => entry.Guid, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1))
            {
                token.ThrowIfCancellationRequested();
                byte[]? assetHash = null, metaHash = null;
                foreach (var entry in group)
                {
                    var asset = catalog.ReadAsset(entry, cancellationToken: token) ?? throw new InvalidDataException("GUIDの内容を確認できません。");
                    var meta = catalog.ReadAsset(entry, "asset.meta", token) ?? throw new InvalidDataException("GUIDのmetaを確認できません。");
                    var nextAsset = SHA256.HashData(asset);
                    var nextMeta = SHA256.HashData(meta);
                    if (assetHash is not null && (!assetHash.AsSpan().SequenceEqual(nextAsset) || !metaHash.AsSpan().SequenceEqual(nextMeta)))
                        throw new InvalidDataException($"同じGUIDに異なる内容があります。版の違う素材が混在していないか確認してください。\nGUID: {group.Key}");
                    assetHash = nextAsset;
                    metaHash = nextMeta;
                }
                duplicates++;
            }
            return duplicates == 0 ? [] : [$"同じ内容のGUIDが{duplicates}件重複しています。パッケージをまたぐ参照は曖昧になり、表示できない場合があります。"];
        }
        finally
        {
            // Never mask the original validation failure or delete a nonempty recovery folder.
            try { Directory.Delete(spill, recursive: false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static (long Length, byte[] Hash) CopyAndHash(Stream input, Stream output, long maximum, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("ZIPの内容が宣言サイズを超えています。");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        if (total != maximum) throw new InvalidDataException("ZIPの内容が途中で途切れています。");
        return (total, hash.GetHashAndReset());
    }

    private static byte[] Hash(Stream stream, CancellationToken token)
        => CopyAndHash(stream, Stream.Null, stream.Length, token).Hash;

    private static bool IsPackageEntry(ZipArchiveEntry entry)
        => entry.Name.Length > 0 && entry.Name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);

    private static string SafeMemberName(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (name.StartsWith('/') || name.Length == 0) throw new InvalidDataException("ZIP内に不正なパスがあります。");
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & 0x400) != 0)
            throw new InvalidDataException("ZIP内のリンクや特殊ファイルは統合できません。");
        foreach (var segment in name.TrimEnd('/').Split('/')) ValidateLeafName(segment);
        return name;
    }

    public static void ValidateLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name.EndsWith(' ') || name.EndsWith('.')
            || name.Any(c => c < 32 || "<>:\"/\\|?*".Contains(c))
            || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            throw new ArgumentException("名前に使えない文字・予約名・末尾の空白やピリオドがあります。");
    }

    private static void AssertOrdinaryPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンク・ジャンクションを経由する場所では統合しません。");
    }

    private static void PreserveInternetZone(IReadOnlyList<string> sources, string target)
    {
        if (!OperatingSystem.IsWindows()) return;
        var zone = 0;
        foreach (var source in sources)
        {
            try
            {
                using var input = new FileStream(source + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 64 * 1024) throw new InvalidDataException("元ZIPのインターネット由来情報が大きすぎます。");
                using var reader = new StreamReader(input);
                var match = Regex.Match(reader.ReadToEnd(), @"(?m)^ZoneId=([0-4])\s*$");
                if (match.Success) zone = Math.Max(zone, int.Parse(match.Groups[1].Value));
            }
            catch (FileNotFoundException) { }
        }
        if (zone > 0) File.WriteAllText(target + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId={zone}\r\n");
    }
}
