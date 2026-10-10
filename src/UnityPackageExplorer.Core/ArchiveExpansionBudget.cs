namespace UnityPackageExplorer.Core;

/// <summary>Caps total decompressed bytes shared by one archive indexing operation.</summary>
public sealed class ArchiveExpansionBudget(long maximumBytes)
{
    private long _expandedBytes;

    public long MaximumBytes { get; } = maximumBytes > 0
        ? maximumBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumBytes));

    public bool IsExhausted => Interlocked.Read(ref _expandedBytes) >= MaximumBytes;

    public void Consume(int bytes)
    {
        if (bytes <= 0) return;
        var expandedBytes = Interlocked.Add(ref _expandedBytes, bytes);
        if (expandedBytes > MaximumBytes)
            throw new InvalidDataException($"Archive exceeds the {MaximumBytes / 1024 / 1024} MiB aggregate decompressed size limit.");
    }
}
