#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shobjidl.h>
#include <shlwapi.h>
#include <iostream>

// Native activation smoke test for the generated .NET COM host. Run after the
// test package has registered the handler for the current user. A hidden child
// window exercises SetWindow/GetWindow without displaying a separate UI.
int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) {
        std::wcerr << L"Usage: PreviewHandlerSmokeTest <trusted .unitypackage/.zip>\n";
        return 2;
    }
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
    if (FAILED(initialized)) return 3;

    const CLSID handler = { 0x70c67cc6, 0x949b, 0x4b5d, { 0xa8, 0xf2, 0x78, 0x44, 0xd5, 0xc2, 0x9e, 0x76 } };
    IPreviewHandler* preview = nullptr;
    HRESULT result = CoCreateInstance(handler, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&preview));
    if (SUCCEEDED(result)) {
        RECT bounds{ 0, 0, 900, 600 };
        HWND parent = CreateWindowExW(0, L"STATIC", L"Preview COM test", WS_OVERLAPPED,
            0, 0, bounds.right, bounds.bottom, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
        if (!parent) result = HRESULT_FROM_WIN32(GetLastError());
        else {
            result = preview->SetWindow(parent, &bounds);
            IInitializeWithFile* initialize = nullptr;
            if (SUCCEEDED(result)) result = preview->QueryInterface(IID_PPV_ARGS(&initialize));
            if (SUCCEEDED(result)) result = initialize->Initialize(argv[1], STGM_READ);
            if (SUCCEEDED(result)) result = preview->DoPreview();
            if (initialize) initialize->Release();
            if (SUCCEEDED(result)) {
                MSG message{};
                for (int i = 0; i < 10 && PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE); ++i) {
                    TranslateMessage(&message); DispatchMessageW(&message);
                }
                IOleWindow* oleWindow = nullptr;
                result = preview->QueryInterface(IID_PPV_ARGS(&oleWindow));
                HWND child = nullptr;
                if (SUCCEEDED(result)) result = oleWindow->GetWindow(&child);
                if (SUCCEEDED(result) && !child) result = E_FAIL;
                if (oleWindow) oleWindow->Release();
            }
            const HRESULT unloaded = preview->Unload();
            if (SUCCEEDED(result)) result = unloaded;
            if (SUCCEEDED(result)) {
                IInitializeWithStream* initializeStream = nullptr;
                result = preview->QueryInterface(IID_PPV_ARGS(&initializeStream));
                IStream* source = nullptr;
                if (SUCCEEDED(result)) result = SHCreateStreamOnFileEx(argv[1], STGM_READ, FILE_ATTRIBUTE_NORMAL, FALSE, nullptr, &source);
                if (SUCCEEDED(result)) result = initializeStream->Initialize(source, STGM_READ);
                if (SUCCEEDED(result)) result = preview->DoPreview();
                if (SUCCEEDED(result)) result = preview->Unload();
                if (source) source->Release();
                if (initializeStream) initializeStream->Release();
            }
            DestroyWindow(parent);
        }
        preview->Release();
    }
    CoUninitialize();
    if (FAILED(result)) {
        std::wcerr << L"Preview handler native COM lifecycle failed: 0x" << std::hex << result << L"\n";
        return 1;
    }
    std::wcout << L"Native COM activation, preview vtable, and Unload succeeded.\n";
    return 0;
}
