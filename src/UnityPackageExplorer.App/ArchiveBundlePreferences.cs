using Microsoft.Win32;

namespace UnityPackageExplorer.App;

internal sealed class ArchiveBundlePreferences(RegistryKey root)
{
    private const string Path = @"Software\UnityPackageExplorer\ArchiveBundlePreferences";

    internal bool MergePackages
    {
        get
        {
            using var key = root.OpenSubKey(Path);
            return key?.GetValue(nameof(MergePackages)) is 1;
        }
        set
        {
            using var key = root.CreateSubKey(Path);
            key.SetValue(nameof(MergePackages), value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
