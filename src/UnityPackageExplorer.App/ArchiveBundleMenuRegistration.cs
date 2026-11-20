using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace UnityPackageExplorer.App;

/// <summary>Per-user registration. Refuses conflicting values and removes only values still owned by this install.</summary>
internal static class ArchiveBundleMenuRegistration
{
    internal const string ClassId = "{9C788FC3-5F0D-4B45-9ED1-65E3C905D752}";
    internal const string Label = "フォルダーにまとめて統合ZIPを作成";
    private const string Suffix = @"\shell\UnityPackageExplorer.Bundle";
    private const string StatePath = @"Software\UnityPackageExplorer\ArchiveBundleMenu";
    private const string ClassPath = @"Software\Classes\CLSID\" + ClassId + @"\InprocServer32";

    internal static void Apply(string mode, string appPath, RegistryKey? registry = null, string? zipProgId = null, string? rarProgId = null)
    {
        var root = registry ?? Registry.CurrentUser;
        var dll = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(appPath))!, "UnityPackageExplorer.ShellExtension.dll");
        if (mode is not ("register" or "unregister")) throw new ArgumentException("Unknown registration mode.");
        using var oldState = root.OpenSubKey(StatePath);
        var recorded = oldState?.GetValue("VerbPaths") as string[] ?? [];
        var recordedDll = oldState?.GetValue("DllPath") as string;
        if (recordedDll is not null && !recordedDll.Equals(dll, StringComparison.OrdinalIgnoreCase))
            throw new IOException("別のインストールのZIP統合メニューが登録されています。先にその登録を解除してください。");
        foreach (var path in recorded)
            if (!IsVerbPath(path)) throw new IOException("ZIP統合メニューの所有記録が不正です。");
        var verbs = new HashSet<string>(recorded, StringComparer.OrdinalIgnoreCase);
        if (mode == "register")
        {
            if (!File.Exists(dll)) throw new FileNotFoundException("右クリックメニューのDLLがありません。", dll);
            foreach (var (extension, overrideProgId) in new[] { (".zip", zipProgId), (".rar", rarProgId) })
            {
                verbs.Add(@"Software\Classes\SystemFileAssociations\" + extension + Suffix);
                var progId = overrideProgId ?? EffectiveProgId(root, extension);
                if (progId is null) continue;
                if (!ValidProgId(progId)) throw new IOException("統合元ファイルの関連付け名が不正です。");
                verbs.Add(@"Software\Classes\" + progId + Suffix);
            }
            var expected = Values(verbs, appPath, dll).ToArray();
            // Validate every value before writing any of them.
            foreach (var group in expected.GroupBy(item => item.Path))
            {
                using var key = root.OpenSubKey(group.Key);
                if (key is null) continue;
                var identity = group.Key == ClassPath ? "" : "ExplorerCommandHandler";
                var owned = group.Key == ClassPath ? dll : ClassId;
                if ((key.ValueCount > 0 || key.SubKeyCount > 0)
                    && !string.Equals(key.GetValue(identity) as string, owned, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("所有を確認できない右クリック登録を上書きしません: " + group.Key);
                foreach (var value in group)
                    if (key.GetValueNames().Contains(value.Name)
                        && (key.GetValueKind(value.Name) != RegistryValueKind.String || !Equals(key.GetValue(value.Name), value.Value)))
                        throw new IOException("別の右クリック登録を上書きしません: " + group.Key);
            }
            using var state = root.CreateSubKey(StatePath);
            state.SetValue("DllPath", dll);
            state.SetValue("VerbPaths", verbs.ToArray(), RegistryValueKind.MultiString);
            foreach (var value in expected)
            {
                using var key = root.CreateSubKey(value.Path);
                key.SetValue(value.Name, value.Value);
            }
        }
        else
        {
            foreach (var group in Values(verbs, appPath, dll).GroupBy(item => item.Path))
            {
                using (var key = root.OpenSubKey(group.Key, writable: true))
                {
                    if (key is null) continue;
                    var identity = group.Key == ClassPath ? "" : "ExplorerCommandHandler";
                    var owned = group.Key == ClassPath ? dll : ClassId;
                    if (!string.Equals(key.GetValue(identity) as string, owned, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var value in group)
                        if (key.GetValueNames().Contains(value.Name) && key.GetValueKind(value.Name) == RegistryValueKind.String && Equals(key.GetValue(value.Name), value.Value))
                            key.DeleteValue(value.Name, throwOnMissingValue: false);
                }
                RemoveEmpty(root, group.Key);
            }
            RemoveEmpty(root, Path.GetDirectoryName(ClassPath)!);
            using (var state = root.OpenSubKey(StatePath, writable: true))
            {
                state?.DeleteValue("VerbPaths", false);
                state?.DeleteValue("DllPath", false);
            }
            RemoveEmpty(root, StatePath);
        }
    }

    private static IEnumerable<(string Path, string Name, string Value)> Values(IEnumerable<string> verbs, string app, string dll)
    {
        yield return (ClassPath, "", dll);
        yield return (ClassPath, "ThreadingModel", "Apartment");
        foreach (var verb in verbs)
        {
            yield return (verb, "", Label);
            yield return (verb, "ExplorerCommandHandler", ClassId);
            yield return (verb, "MultiSelectModel", "Player");
            yield return (verb, "Icon", app + ",0");
        }
    }

    private static bool ValidProgId(string value) => Regex.IsMatch(value, @"^[A-Za-z0-9._-]+(\\[A-Za-z0-9._-]+)*$")
        && value.Split('\\').All(segment => segment is not ("." or ".."));
    private static bool IsVerbPath(string path) => path.StartsWith(@"Software\Classes\", StringComparison.Ordinal)
        && path.EndsWith(Suffix, StringComparison.Ordinal) && ValidProgId(path[@"Software\Classes\".Length..^Suffix.Length]);
    private static string? EffectiveProgId(RegistryKey root, string extension)
    {
        using var choice = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\" + extension + @"\UserChoice");
        var value = choice?.GetValue("ProgId") as string;
        if (value is not null && ValidProgId(value)) return value;
        using var key = Registry.ClassesRoot.OpenSubKey(extension);
        value = key?.GetValue("") as string;
        return value is not null && ValidProgId(value) ? value : null;
    }
    private static void RemoveEmpty(RegistryKey root, string path)
    {
        using (var key = root.OpenSubKey(path))
            if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0) return;
        root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }
}
