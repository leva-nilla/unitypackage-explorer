# Preview handler COM smoke test

Build this Windows native console program with the Visual C++ toolchain:

```powershell
cl /EHsc /std:c++17 PreviewHandlerSmokeTest.cpp ole32.lib shell32.lib user32.lib
```

Run it after the test package has registered the current user's preview handler:

```powershell
PreviewHandlerSmokeTest.exe C:\path\to\trusted-sample.unitypackage
```

It activates the handler by CLSID, calls `SetWindow`, initializes once through
`IInitializeWithFile` and once through a marshaled `IInitializeWithStream`, checks
the child HWND through a separately queried `IOleWindow`, and unloads both
requests. Use a trusted local sample so Windows Attachment Manager policy does
not suppress preview. The source archive is read-only.
