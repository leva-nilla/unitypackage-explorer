using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace UnityPackageExplorer.Core;

public sealed record ArchiveBundlePlan(IReadOnlyList<string> SourcePaths, string DestinationDirectory, string ArchiveName)
{
    public bool MergePackages { get; init; }
}
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

    public static ArchiveBundlePlan Plan(IEnumerable<string> sourcePaths, string folderName, bool mergePackages = false)
    {
        ValidateLeafName(folderName);
        var sources = sourcePaths.Select(Path.GetFullPath).ToArray();
        if (sources.Length is < 2 or > MaximumSourceCount)
            throw new ArgumentException($"ZIPまたは単体RARを2～{MaximumSourceCount}個選択してください。");
        if (sources.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sources.Length)
            throw new ArgumentException("同じファイルが複数回指定されています。");
        var parent = Path.GetDirectoryName(sources[0])!;
        foreach (var source in sources)
        {
            if (!string.Equals(parent, Path.GetDirectoryName(source), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("同じフォルダーにあるファイルを選択してください。");
            if (UnityRarReader.IsLegacyVolumePath(source))
                throw new ArgumentException("分割RARは統合ZIPの対象外です。");
            if (Path.GetExtension(source).ToLowerInvariant() is not (".zip" or ".rar"))
                throw new ArgumentException("統合できるのはZIPと単体RARだけです。");
            AssertOrdinaryPath(source);
            if (!File.Exists(source)) throw new FileNotFoundException("元ファイルが見つかりません。", source);
        }
        var destination = Path.Combine(parent, folderName);
        if (Path.Exists(destination)) throw new IOException("同名のフォルダーまたはファイルがあります。別の名前に変更してください。");
        var archiveName = folderName + "_統合.zip";
        ValidateLeafName(archiveName);
        if (sources.Any(source => Path.GetFileName(source).Equals(archiveName, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("統合ZIPの名前が元ファイルと重なります。フォルダー名を変更してください。");
        return new ArchiveBundlePlan(Array.AsReadOnly(sources), destination, archiveName) { MergePackages = mergePackages };
    }

    public static Task<ArchiveBundleResult> CreateAsync(ArchiveBundlePlan plan,
        IProgress<ArchiveBundleProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => Create(plan, progress, cancellationToken), cancellationToken);

    private static ArchiveBundleResult Create(ArchiveBundlePlan requested, IProgress<ArchiveBundleProgress>? progress, CancellationToken token)
    {
        // Revalidate the public plan immediately before doing any filesystem work.
        var plan = Plan(requested.SourcePaths, Path.GetFileName(requested.DestinationDirectory), requested.MergePackages);
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
            var (sources, expected) = BuildArchive(plan, staging, archivePath, progress, token);
            progress?.Report(new("統合ZIPを検証しています"));
            VerifyArchive(archivePath, expected, token);
            var warnings = VerifyReferences(archivePath, staging, token);
            PreserveInternetZone(plan.SourcePaths, archivePath);
            // Originals stay at their original paths through all validation/cancellation.
            token.ThrowIfCancellationRequested();
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("元ファイルをフォルダーへ移動しています", Path.GetFileName(source.Path)));
                AssertOrdinaryPath(source.Path);
                using (var file = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (!Hash(file, token).AsSpan().SequenceEqual(source.Hash))
                        throw new IOException("作業中に元ファイルが変更されたため、中止しました。");
                AssertOrdinaryPath(staging);
                var target = Path.Combine(staging, Path.GetFileName(source.Path));
                File.Move(source.Path, target, overwrite: false);
                moved.Add((source.Path, target));
                using var movedFile = File.OpenRead(target);
                if (!Hash(movedFile, token).AsSpan().SequenceEqual(source.Hash))
                    throw new IOException("移動した元ファイルの内容が一致しません。");
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
                    "一部の元ファイルを元の場所へ戻せませんでした。作業フォルダーに保管しています。削除せず内容を確認してください。\n" + staging,
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

    private static (List<(string Path, byte[] Hash)> Sources, Dictionary<string, (long Length, byte[] Hash)> Expected)
        BuildArchive(ArchiveBundlePlan plan, string staging, string archivePath, IProgress<ArchiveBundleProgress>? progress, CancellationToken token)
    {
        var sources = new List<(string Path, byte[] Hash)>();
        var expected = new Dictionary<string, (long Length, byte[] Hash)>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[128 * 1024];
        long total = 0;
        var count = 0;
        using var spool = plan.MergePackages ? CreateScratch(staging) : null;
        using var pendingPackage = plan.MergePackages ? CreateScratch(staging) : null;
        var merger = spool is null ? null : new UnityPackageMerger(spool, buffer);
        using var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var source in plan.SourcePaths)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report(new("ZIP/RARをまとめています", Path.GetFileName(source)));
            using var file = OpenSource(source);
            var sourceHash = Hash(file, token);
            file.Position = 0;
            var before = count;
            if (Path.GetExtension(source).Equals(".rar", StringComparison.OrdinalIgnoreCase))
            {
                // A stream-backed reader never searches for adjacent volumes. The original is
                // held read-only throughout hashing and all reads, just like the ZIP branch.
                new UnityRarReader(() => OpenSource(source)).VisitBundlePackages(
                    (member, stream) => AddPackage(member.FullName, member.Length, stream), token);
            }
            else
            {
                using var input = new ZipArchive(file, ZipArchiveMode.Read);
                if (input.Entries.Count > UnityArchiveLimits.MaximumEntryCount)
                    throw new InvalidDataException("ZIP内の項目数が上限を超えています。");
                foreach (var entry in input.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    if (!entry.Name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase)) continue;
                    AssertRegularZipEntry(entry);
                    using var member = entry.Open();
                    AddPackage(entry.FullName, entry.Length, member);
                }
            }
            if (count == before)
                throw new InvalidDataException($".unitypackage を含まないファイルがあります。\n{Path.GetFileName(source)}");
            sources.Add((source, sourceHash));
        }
        if (merger is not null)
        {
            progress?.Report(new("UnityPackageを1つにまとめています"));
            using var merged = CreateScratch(staging);
            merger.WriteAndVerify(merged, token);
            if (merged.Length > UnityArchiveLimits.MaximumStreamedMemberBytes)
                throw new InvalidDataException("統合したUnityPackageが読込みサイズ上限を超えています。");
            merged.Position = 0;
            WritePackage(Path.GetFileName(plan.DestinationDirectory) + ".unitypackage", merged.Length, merged);
        }
        return (sources, expected);

        void AddPackage(string entryName, long length, Stream from)
        {
            var name = Path.GetFileName(SafeMemberName(entryName));
            if (++count > UnityArchiveLimits.MaximumEntryCount)
                throw new InvalidDataException("統合する .unitypackage の数が上限を超えています。");
            if (length < 0 || length > UnityArchiveLimits.MaximumStreamedMemberBytes
                || length > UnityArchiveLimits.MaximumDeclaredTotalBytes - total)
                throw new InvalidDataException("ZIP/RARの内容が安全な読込みサイズ上限を超えています。");
            total += length;
            if (merger is null)
            {
                if (expected.ContainsKey(name))
                    throw new InvalidDataException($"同じ名前の .unitypackage が複数あります。名前を変えるか、1つのUnityPackageにまとめてください。\n{name}");
                WritePackage(name, length, from);
                return;
            }
            // Finish consuming this outer member (including its integrity check) before
            // parsing the inner TAR, which may stop reading at its end marker.
            pendingPackage!.Position = 0;
            pendingPackage.SetLength(0);
            CopyAndHash(from, pendingPackage, length, token, buffer);
            pendingPackage.Position = 0;
            merger.AddPackage(pendingPackage, name, token);
        }

        void WritePackage(string name, long length, Stream from)
        {
            // UnityPackage is already compressed.
            using var to = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
            expected.Add(name, CopyAndHash(from, to, length, token, buffer));
        }
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    private static FileStream CreateScratch(string staging)
        => new(Path.Combine(staging, $".package-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

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
                throw new InvalidDataException("統合ZIPの検証に失敗しました。元ファイルは移動しません。");
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

    internal static (long Length, byte[] Hash) CopyAndHash(Stream input, Stream output, long maximum, CancellationToken token, byte[]? reusableBuffer = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = reusableBuffer ?? new byte[128 * 1024];
        long total = 0;
        int read;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            total = checked(total + read);
            if (total > maximum) throw new InvalidDataException("アーカイブの内容が宣言サイズを超えています。");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        if (total != maximum) throw new InvalidDataException("アーカイブの内容が途中で途切れています。");
        return (total, hash.GetHashAndReset());
    }

    private static byte[] Hash(Stream stream, CancellationToken token)
        => CopyAndHash(stream, Stream.Null, stream.Length, token).Hash;

    private static void AssertRegularZipEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("ZIP内のリンクや特殊ファイルは統合できません。");
    }

    private static string SafeMemberName(string entryName)
    {
        var name = entryName.Replace('\\', '/');
        foreach (var segment in name.Split('/')) ValidateLeafName(segment);
        UnityPackageReader.GetSafeRelativePath(name);
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
                if (input.Length > 64 * 1024) throw new InvalidDataException("元ファイルのインターネット由来情報が大きすぎます。");
                using var reader = new StreamReader(input);
                var match = Regex.Match(reader.ReadToEnd(), @"(?m)^ZoneId=([0-4])\s*$");
                if (match.Success) zone = Math.Max(zone, int.Parse(match.Groups[1].Value));
            }
            catch (FileNotFoundException) { }
        }
        if (zone > 0) File.WriteAllText(target + ":Zone.Identifier", $"[ZoneTransfer]\r\nZoneId={zone}\r\n");
    }
}
