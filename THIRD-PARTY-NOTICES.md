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
| Microsoft .NET 10 Windows runtime | Runtime selected by the build SDK | Microsoft .NET Library License; Microsoft third-party notices included |
| Microsoft Visual C++ Runtime DLLs | Version selected by the build Visual Studio installation | Redistributed under the applicable Visual Studio license terms |

The project source is under the MIT License in [`LICENSE`](LICENSE). This does not change the licenses of the separately listed components. Microsoft runtime binaries are copied from the Visual Studio redistributable files into the application-local folder so setup does not install machine-wide components or require administrator access. Their version and file list are recorded in the installed `Licenses/Microsoft-Visual-Cpp-Redist.txt`.

The .NET runtime license and third-party notice files are copied from the .NET SDK used to produce the release. NuGet component license files and notices are collected from the exact restored package versions during setup creation. See [`ThirdPartyLicenses`](ThirdPartyLicenses/) for checked-in upstream license texts.
