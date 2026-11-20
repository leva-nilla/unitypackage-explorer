using Microsoft.Win32;
using UnityPackageExplorer.App;

namespace UnityPackageExplorer.Tests;

public sealed class ArchiveBundleMenuTests
{
    private const string Verb = @"Software\Classes\SystemFileAssociations\.zip\shell\UnityPackageExplorer.Bundle";
    private const string RarVerb = @"Software\Classes\SystemFileAssociations\.rar\shell\UnityPackageExplorer.Bundle";
    private const string ClassKey = @"Software\Classes\CLSID\" + ArchiveBundleMenuRegistration.ClassId + @"\InprocServer32";

    [Fact]
    public void RegisterUpdateAndRemoveOperateOnlyOnOwnedValues()
    {
        WithRegistry((root, app) =>
        {
            ArchiveBundleMenuRegistration.Apply("register", app, root, "Test.Zip", "Test.Rar");
            ArchiveBundleMenuRegistration.Apply("register", app, root, "Test.Zip", "Test.Rar");
            using (var rar = root.OpenSubKey(RarVerb))
                Assert.Equal(ArchiveBundleMenuRegistration.ClassId, rar!.GetValue("ExplorerCommandHandler"));
            using (var verb = root.OpenSubKey(Verb, true))
            {
                Assert.Equal(ArchiveBundleMenuRegistration.ClassId, verb!.GetValue("ExplorerCommandHandler"));
                Assert.Equal("Player", verb.GetValue("MultiSelectModel"));
                verb.SetValue("", "user changed label");
                verb.SetValue("Custom", "keep");
            }
            ArchiveBundleMenuRegistration.Apply("unregister", app, root);
            using var remaining = root.OpenSubKey(Verb);
            Assert.Equal("user changed label", remaining!.GetValue(""));
            Assert.Equal("keep", remaining.GetValue("Custom"));
            Assert.Null(remaining.GetValue("ExplorerCommandHandler"));
            Assert.Null(root.OpenSubKey(RarVerb));
            Assert.Null(root.OpenSubKey(@"Software\Classes\Test.Rar\shell\UnityPackageExplorer.Bundle"));
            Assert.Null(root.OpenSubKey(ClassKey));
            ArchiveBundleMenuRegistration.Apply("unregister", app, root);
        });
    }

    [Fact]
    public void ConflictingRegistrationIsRejectedBeforeAnyWrites()
    {
        WithRegistry((root, app) =>
        {
            using (var verb = root.CreateSubKey(Verb)) verb.SetValue("ExplorerCommandHandler", "{other}");
            Assert.Throws<IOException>(() => ArchiveBundleMenuRegistration.Apply("register", app, root, "Test.Zip"));
            Assert.Null(root.OpenSubKey(ClassKey));
            using var unchanged = root.OpenSubKey(Verb);
            Assert.Equal("{other}", unchanged!.GetValue("ExplorerCommandHandler"));
        });
    }

    [Fact]
    public void ChangedHandlerIsPreservedAndPreviousProgidIsCleanedAfterAssociationChanges()
    {
        WithRegistry((root, app) =>
        {
            ArchiveBundleMenuRegistration.Apply("register", app, root, "Old.Zip");
            ArchiveBundleMenuRegistration.Apply("register", app, root, "New.Zip");
            using (var verb = root.OpenSubKey(Verb, true)) verb!.SetValue("ExplorerCommandHandler", "{other}");
            ArchiveBundleMenuRegistration.Apply("unregister", app, root);
            using var remaining = root.OpenSubKey(Verb);
            Assert.Equal("{other}", remaining!.GetValue("ExplorerCommandHandler"));
            Assert.Null(root.OpenSubKey(@"Software\Classes\Old.Zip\shell\UnityPackageExplorer.Bundle"));
            Assert.Null(root.OpenSubKey(@"Software\Classes\New.Zip\shell\UnityPackageExplorer.Bundle"));
        });
    }

    private static void WithRegistry(Action<RegistryKey, string> test)
    {
        var key = @"Software\UnityPackageExplorer.Tests\Bundle-" + Guid.NewGuid().ToString("N");
        var folder = Path.Combine(Path.GetTempPath(), "UPX-bundle-menu-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "UnityPackageExplorer.ShellExtension.dll"), [0]);
            using var root = Registry.CurrentUser.CreateSubKey(key);
            test(root, Path.Combine(folder, "UnityPackageExplorer.App.exe"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            Directory.Delete(folder, recursive: true);
        }
    }
}
