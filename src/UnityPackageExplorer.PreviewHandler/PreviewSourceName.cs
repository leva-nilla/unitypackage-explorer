using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace UnityPackageExplorer.PreviewHandler;

internal readonly record struct PreviewSourceSpoolResult(string ReadPath, string? DisplayName)
{
    public string SelectorSourceName => DisplayName ?? ReadPath;
}

internal static class PreviewSourceName
{
    public static PreviewSourceSpoolResult CreateSpoolResult(string readPath, string? displayName)
        => new(readPath, NormalizeStreamName(displayName));

    public static string? ReadStreamDisplayName(IStream stream)
    {
        try
        {
            stream.Stat(out var statistics, 0); // STATFLAG_DEFAULT requests pwcsName.
            return NormalizeStreamName(statistics.pwcsName);
        }
        catch (COMException) { return null; }
    }

    public static string? NormalizeStreamName(string? streamName)
    {
        if (string.IsNullOrWhiteSpace(streamName)) return null;
        var fileName = Path.GetFileName(streamName.Trim());
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }
}
