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
        Assert.Equal("1.0.0.6", (string?)identity.Attribute("Version"));
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

    [Fact]
    public void StaticVerbsCoverAssociationProgidsAndRemoveOnlyRecordedOwnedCommands()
    {
        var testData = Path.Combine(AppContext.BaseDirectory, "TestData");
        var installer = File.ReadAllText(Path.Combine(testData, "UnityPackageExplorer.iss"));
        var cleanupPath = Path.Combine(testData, "Cleanup-LegacyExplorerRegistration.ps1");
        var cleanupBytes = File.ReadAllBytes(cleanupPath);
        Assert.True(cleanupBytes.Length >= 3 && cleanupBytes[0] == 0xEF && cleanupBytes[1] == 0xBB && cleanupBytes[2] == 0xBF,
            "Windows PowerShell 5.1 needs a UTF-8 BOM to read localized ownership labels correctly.");
        var cleanup = File.ReadAllText(cleanupPath);

        var associationLookup = installer[installer.IndexOf("function GetAssociationProgId", StringComparison.Ordinal)..
            installer.IndexOf("procedure RegisterAssociationProgIdVerb", StringComparison.Ordinal)];
        Assert.DoesNotContain("function HasAssociationProgId", installer, StringComparison.Ordinal);
        Assert.True(associationLookup.IndexOf("FileExts\\' + Extension + '\\UserChoice", StringComparison.Ordinal) <
                    associationLookup.IndexOf("RegQueryStringValue(HKCR, Extension", StringComparison.Ordinal),
            "The active UserChoice ProgID must take priority over the extension's HKCR fallback.");
        Assert.Contains("RegKeyExists(HKCR, ProgId)", associationLookup, StringComparison.Ordinal);
        Assert.Contains("IsValidAssociationProgId", associationLookup, StringComparison.Ordinal);
        Assert.Contains("Segment = '..'", installer, StringComparison.Ordinal);
        Assert.Contains("RegisterAssociationProgIdVerb", installer, StringComparison.Ordinal);
        Assert.Contains("Software\\UnityPackageExplorer\\Install", installer, StringComparison.Ordinal);
        Assert.Contains("ProgIdVerbCount", installer, StringComparison.Ordinal);
        Assert.True(installer.IndexOf("'ProgIdVerbCount'", StringComparison.Ordinal) <
                    installer.LastIndexOf("RegisterAssociationProgIdVerb(RegisteredProgIds[", StringComparison.Ordinal),
            "Ownership state must be recorded before direct ProgID registry values are changed.");
        Assert.Contains("SystemFileAssociations\\.unitypackage\\shell\\UnityPackageExplorer.Open", installer, StringComparison.Ordinal);
        Assert.Contains("SystemFileAssociations\\.prefab\\shell\\UnityPackageExplorer.Open", installer, StringComparison.Ordinal);
        Assert.Contains("SystemFileAssociations\\.zip\\shell\\UnityPackageExplorer.Open", installer, StringComparison.Ordinal);
        Assert.False(installer.Contains("{code:GetAssociationProgId", StringComparison.Ordinal),
            "ProgID keys must not be automatically deleted by Inno because that could erase a later third-party edit.");

        var removal = cleanup[cleanup.IndexOf("function Remove-RecordedProgIdVerbs", StringComparison.Ordinal)..
            cleanup.IndexOf("function Get-OldPreviewAssociation", StringComparison.Ordinal)];
        var ownedCommandRemoval = cleanup[cleanup.IndexOf("function Remove-Owned-Shell-Command", StringComparison.Ordinal)..
            cleanup.IndexOf("function Get-RecordedProgIdVerbCount", StringComparison.Ordinal)];
        Assert.Contains("$progId -notmatch '^[A-Za-z0-9._-]+(\\\\[A-Za-z0-9._-]+)*$'", removal, StringComparison.Ordinal);
        Assert.Contains("$recordedCommand -cne $expectedCommand", removal, StringComparison.Ordinal);
        Assert.Contains("Remove-Owned-Shell-Command \"$classes\\$progId\\shell\\UnityPackageExplorer.Open\"", removal, StringComparison.Ordinal);
        Assert.Contains("Remove-RecordedProgIdVerbs", cleanup, StringComparison.Ordinal);
        Assert.Contains("Get-OwnedShellCommandCleanupPlan", ownedCommandRemoval, StringComparison.Ordinal);
        Assert.Contains("$command.DeleteValue('', $false)", ownedCommandRemoval, StringComparison.Ordinal);
        Assert.Contains("$registeredIcon -eq ($appPath + ',0')", cleanup, StringComparison.Ordinal);
        Assert.Contains("$registeredIcon -eq ('\"' + $appPath + '\",0')", cleanup, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSmokeSkipsProgidValidationWhenTheExtensionHasNoAssociation()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "windows.yml"));
        var smokeStart = workflow.IndexOf("$ownedProgIdVerbPaths =", StringComparison.Ordinal);
        var smokeEnd = workflow.IndexOf("$fixtureDirectory =", smokeStart, StringComparison.Ordinal);
        Assert.True(smokeStart >= 0 && smokeEnd > smokeStart, "The ProgID smoke check was not found.");
        var smokeCheck = workflow[smokeStart..smokeEnd];

        Assert.Contains("$extensionKey = Get-Item", smokeCheck, StringComparison.Ordinal);
        Assert.Contains("if ($extensionKey) { $progId = $extensionKey.GetValue('') }", smokeCheck, StringComparison.Ordinal);
        Assert.Contains("if (-not $progId) { continue }", smokeCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Item -LiteralPath \"Registry::HKEY_CLASSES_ROOT\\$extension\" -ErrorAction SilentlyContinue).GetValue", smokeCheck, StringComparison.Ordinal);
    }

    [Fact]
    public void InstallerSmokeAllowsUnknownProgIdDataButRejectsReferencesToUninstalledApp()
    {
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "windows.yml"));
        var uninstallStart = workflow.IndexOf("foreach ($verbPath in $ownedProgIdVerbPaths)", StringComparison.Ordinal);
        var uninstallEnd = workflow.IndexOf("if (Test-Path -LiteralPath $installDirectory)", uninstallStart, StringComparison.Ordinal);
        Assert.True(uninstallStart >= 0 && uninstallEnd > uninstallStart, "The ProgID uninstall smoke check was not found.");
        var uninstallCheck = workflow[uninstallStart..uninstallEnd];

        Assert.Contains("if (-not (Test-Path -LiteralPath $verbPath)) { continue }", uninstallCheck, StringComparison.Ordinal);
        Assert.Contains("$remainingCommand -eq $expectedCommand", uninstallCheck, StringComparison.Ordinal);
        Assert.Contains("$remainingLabel -eq 'UnityPackage Explorer で開く'", uninstallCheck, StringComparison.Ordinal);
        Assert.Contains("$remainingIcon -eq \"$app,0\"", uninstallCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Owned ProgID context menu registration remained after uninstall", uninstallCheck, StringComparison.Ordinal);
    }
}
