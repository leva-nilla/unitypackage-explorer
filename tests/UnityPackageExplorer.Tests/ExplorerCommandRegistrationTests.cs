using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace UnityPackageExplorer.Tests;

public sealed class ExplorerCommandRegistrationTests
{
    [Fact]
    public void PackagedManifestUsesWildcardAndNativeCommandFiltersSupportedFiles()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var manifest = XDocument.Load(Path.Combine(testData, "AppxManifest.xml"));
        XNamespace desktop5 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5";
        var itemTypes = manifest.Descendants(desktop5 + "ItemType").ToArray();

        XNamespace foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        var identity = Assert.Single(manifest.Descendants(foundation + "Identity"));
        Assert.Equal("1.0.0.5", (string?)identity.Attribute("Version"));
        var itemType = Assert.Single(itemTypes);
        Assert.Equal("*", (string?)itemType.Attribute("Type"));
        Assert.Single(itemType.Elements(desktop5 + "Verb"));

        var source = File.ReadAllText(Path.Combine(testData, "UnityPackageExplorerShellCommand.cpp"));
        var start = source.IndexOf("bool IsSupportedPath(PCWSTR path)", StringComparison.Ordinal);
        var end = source.IndexOf("HRESULT GetSelectedPath(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The native supported-path filter was not found.");
        var filter = source[start..end];

        var extensions = Regex.Matches(filter, """_wcsicmp\(extension, L"(?<extension>\.[^"]+)"\)""")
            .Select(match => match.Groups["extension"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { ".prefab", ".unitypackage", ".zip" }, extensions);
        Assert.Contains("GetFileAttributesW(path)", filter, StringComparison.Ordinal);
        Assert.Contains("FILE_ATTRIBUTE_DIRECTORY", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyVerbUsesPackagedCommandAndUninstallChecksOwnership()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var installer = File.ReadAllText(Path.Combine(testData, "Install-UnityPackageExplorerContextMenu.ps1"));
        var uninstaller = File.ReadAllText(Path.Combine(testData, "Uninstall-UnityPackageExplorerContextMenu.ps1"));

        Assert.Contains("$classes\\*\\shell\\UnityPackageExplorerOpen", installer, StringComparison.Ordinal);
        Assert.Contains("ExplorerCommandHandler", installer, StringComparison.Ordinal);
        Assert.Contains("SparsePackageFullName", installer, StringComparison.Ordinal);
        Assert.Contains("$registeredHandler -eq $legacyContextMenuClsid", uninstaller, StringComparison.Ordinal);
        Assert.Contains("$legacyContextMenuClsid -eq '{1C490A47-4D85-4AA0-A2A3-4A7827B5DA79}'", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerCanSkipBuildWhileStillCheckingExistingOutputs()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var installer = File.ReadAllText(Path.Combine(testData, "Install-UnityPackageExplorerContextMenu.ps1"));

        Assert.Contains("[switch]$SkipBuild", installer, StringComparison.Ordinal);
        Assert.Contains("if ($SkipBuild)", installer, StringComparison.Ordinal);
        Assert.Contains("Skipping publish/build and reusing the verified installed files.", installer, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-Path -LiteralPath $appPath))", installer, StringComparison.Ordinal);
        Assert.Contains("if (-not (Test-Path -LiteralPath $comHost))", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerPublishesAndRegistersPerUserApplicationOutsideTheRepository()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var installer = File.ReadAllText(Path.Combine(testData, "Install-UnityPackageExplorerContextMenu.ps1"));
        var packageInstaller = File.ReadAllText(Path.Combine(testData, "Install-ExplorerContextMenuPackage.ps1"));
        var packageBuilder = File.ReadAllText(Path.Combine(testData, "Build-ExplorerContextMenuPackage.ps1"));
        var uninstaller = File.ReadAllText(Path.Combine(testData, "Uninstall-UnityPackageExplorerContextMenu.ps1"));

        Assert.Contains("Programs\\UnityPackageExplorer", installer, StringComparison.Ordinal);
        Assert.Contains("dotnet publish $appProject -c Release -r win-x64 --self-contained true", installer, StringComparison.Ordinal);
        Assert.Contains("-p:BaseOutputPath=$baseOutputPath", installer, StringComparison.Ordinal);
        Assert.Contains("[Environment]::GetFolderPath('Programs')", installer, StringComparison.Ordinal);
        Assert.Contains("Install destination is non-empty and is not a verified UnityPackage Explorer installation", installer, StringComparison.Ordinal);
        Assert.Contains("ExternalLocation = $installDirectory", installer, StringComparison.Ordinal);
        Assert.Contains("-ExternalLocation $appOutput", packageInstaller, StringComparison.Ordinal);
        Assert.Contains("if ($ExternalLocation)", packageBuilder, StringComparison.Ordinal);
        Assert.Contains("アプリ本体のファイルは削除せず", uninstaller, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -LiteralPath $installDirectory", uninstaller, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeCommandLaunchesAppDirectlyAndReportsLaunchStage()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var source = File.ReadAllText(Path.Combine(testData, "UnityPackageExplorerShellCommand.cpp"));
        var launchStart = source.IndexOf("HRESULT LaunchPreview(PCWSTR filePath)", StringComparison.Ordinal);
        var launchEnd = source.IndexOf("class ExplorerCommand final", launchStart, StringComparison.Ordinal);
        Assert.True(launchStart >= 0 && launchEnd > launchStart, "The native launch method was not found.");
        var launch = source[launchStart..launchEnd];

        Assert.Contains("CreateProcessW(", launch, StringComparison.Ordinal);
        Assert.Contains("GetSiblingApplicationPath", source, StringComparison.Ordinal);
        Assert.Contains("GetModuleFileNameW", source, StringComparison.Ordinal);
        Assert.Contains("sibling UnityPackageExplorer.App.exe does not exist beside the shell DLL", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RegGetValueW(HKEY_CURRENT_USER", source, StringComparison.Ordinal);
        Assert.Contains("CreateProcessW failed (Win32 error %lu)", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("ShellExecuteW(", launch, StringComparison.Ordinal);
    }
}
