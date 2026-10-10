# Microsoft Visual C++ Runtime deployment

The setup copies the x64 runtime DLLs required by the bundled Assimp native library from the Visual Studio redistributable directory into the application directory. The DLLs are versioned with the Visual Studio toolset used for the build. This is application-local deployment; setup does not install or service the machine-wide Visual C++ Redistributable.

Redistribution is subject to the applicable Microsoft Visual Studio Software License Terms and its Distributable Code list. Microsoft explains the permitted files, locations, and servicing tradeoff here:

- https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files
- https://learn.microsoft.com/en-us/cpp/windows/choosing-a-deployment-method

The exact DLL filenames and toolset version included in a build are written to `Licenses/Microsoft-Visual-Cpp-Redist.txt` beside the installed application.
