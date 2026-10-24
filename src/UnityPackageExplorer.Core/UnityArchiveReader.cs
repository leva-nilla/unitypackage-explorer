namespace UnityPackageExplorer.Core;

/// <summary>Metadata shared by the supported outer archive formats.</summary>
public record UnityArchiveMember(string FullName, long Length, long CompressedLength);

/// <summary>Reads an outer archive without extracting it to disk.</summary>
public interface IUnityArchiveReader
{
    string FormatName { get; }

    IReadOnlyList<UnityArchiveMember> ReadIndex(CancellationToken cancellationToken = default);

    Stream OpenMember(string memberPath, long maximumBytes = UnityArchiveLimits.MaximumStreamedMemberBytes);

    byte[] ReadMember(string memberPath, CancellationToken cancellationToken = default);

    byte[] ReadMember(string memberPath, long maximumBytes, CancellationToken cancellationToken = default);

    void RebindPath(string path);
}

/// <summary>Bounds outer archive metadata, streaming and in-memory reads independently.</summary>
public static class UnityArchiveLimits
{
    public const long MaximumStreamedMemberBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumBufferedMemberBytes = 512L * 1024 * 1024;
    public const int MaximumEntryCount = 100_000;
    public const long MaximumDeclaredTotalBytes = 8L * 1024 * 1024 * 1024;
}
