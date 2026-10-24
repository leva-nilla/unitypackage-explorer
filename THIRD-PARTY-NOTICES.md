# Third-party notices

UnityPackage Explorer includes the following third-party software in its Windows x64 application. License texts and notices are installed beside the application under `Licenses`.

| Component | Version | License |
| --- | --- | --- |
| CommunityToolkit.Common, Diagnostics, and Mvvm | 8.3.2 | MIT |
| Cyotek.Drawing.BitmapFont | 2.0.4 | MIT |
| HelixToolkit and HelixToolkit.SharpDX packages | 3.1.2 | MIT |
| Microsoft.CodeAnalysis.Common and CSharp | 4.11.0 | MIT; package third-party notices are included |
| Microsoft.Extensions.DependencyInjection.Abstractions and Logging.Abstractions | 8.0.2 | MIT |
| SharpAssimp | 6.0.6 | MIT |
| Assimp native library bundled by SharpAssimp | 6.0.6 package build | BSD 3-Clause; the package notice also lists bundled components |
| SharpDX packages | 4.2.0 | MIT |
| SharpCompress | 0.50.7 | MIT |
| Microsoft .NET 10 Windows runtime | Runtime selected by the build SDK | Microsoft .NET Library License; Microsoft third-party notices included |
| Microsoft Visual C++ Runtime DLLs | Version selected by the build Visual Studio installation | Redistributed under the applicable Visual Studio license terms |

The project source is under the MIT License in [`LICENSE`](LICENSE). This does not change the licenses of the separately listed components. Microsoft runtime binaries are copied from the Visual Studio redistributable files into the application-local folder so setup does not install machine-wide components or require administrator access. Their version and file list are recorded in the installed `Licenses/Microsoft-Visual-Cpp-Redist.txt`.

The .NET runtime license and third-party notice files are copied from the .NET SDK used to produce the release. NuGet component license files and notices are collected from the exact restored package versions during setup creation. See [`ThirdPartyLicenses`](ThirdPartyLicenses/) for checked-in upstream license texts.

The RAR test fixture embedded in the test suite is the 127-byte `Rar5.comment.plain.rar` fixture from SharpCompress `tests/TestArchives/Archives`, distributed with SharpCompress under the MIT License. Its SHA-256 is `9cd2ca8ea137e220987da76b33863f4be0e74fe166d1459d79241d0e721da2ed`.

`tests/UnityPackageExplorer.Tests/TestData/Rar5.solid.rar` comes from [SharpCompress 0.50.7 test archives](https://github.com/adamhathcock/sharpcompress/blob/0.50.7/tests/TestArchives/Archives/Rar5.solid.rar), under the same license. Its SHA-256 is `3043d68c5a81f46adcbb5393632d243944ead44eb383a677ac82e68afdde27cf`. It is used only as opaque test data and is not bundled in the application or executed.
