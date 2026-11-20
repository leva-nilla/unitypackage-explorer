#include <windows.h>
#include <shlobj.h>
#include <shobjidl.h>
#include <string>
#include <vector>
#include <iostream>

// Run against a DLL copy in an isolated test folder, never the installed app folder.
const CLSID BundleId = { 0x9c788fc3, 0x5f0d, 0x4b45, { 0x9e, 0xd1, 0x65, 0xe3, 0xc9, 0x05, 0xd7, 0x52 } };
using FactoryFunction = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);

IShellItemArray* Items(const std::vector<std::wstring>& paths)
{
    std::vector<PIDLIST_ABSOLUTE> ids;
    for (const auto& path : paths)
    {
        PIDLIST_ABSOLUTE id = nullptr;
        if (FAILED(SHParseDisplayName(path.c_str(), nullptr, &id, 0, nullptr))) return nullptr;
        ids.push_back(id);
    }
    IShellItemArray* result = nullptr;
    SHCreateShellItemArrayFromIDLists(static_cast<UINT>(ids.size()), const_cast<PCIDLIST_ABSOLUTE*>(ids.data()), &result);
    for (auto id : ids) CoTaskMemFree(id);
    return result;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc >= 2 && std::wstring(argv[1]) == L"--bundle-zips")
    {
        wchar_t output[32768]{};
        if (!GetEnvironmentVariableW(L"UPX_BUNDLE_SMOKE_OUTPUT", output, ARRAYSIZE(output))) return 10;
        HANDLE file = CreateFileW(output, GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return 11;
        std::wstring arguments;
        for (int index = 1; index < argc; ++index) arguments += std::wstring(argv[index]) + L"\n";
        DWORD written = 0;
        const auto bytes = static_cast<DWORD>(arguments.size() * sizeof(wchar_t));
        const bool ok = WriteFile(file, arguments.data(), bytes, &written, nullptr) && written == bytes;
        CloseHandle(file);
        return ok ? 0 : 12;
    }
    if (argc != 5) return 2; // isolated DLL, ZIP, RAR, unsupported file
    const std::wstring dllPath = argv[1];
    const auto directory = dllPath.substr(0, dllPath.find_last_of(L"\\/") + 1);
    wchar_t executable[32768]{};
    GetModuleFileNameW(nullptr, executable, ARRAYSIZE(executable));
    const auto stub = directory + L"UnityPackageExplorer.App.exe";
    if (!CopyFileW(executable, stub.c_str(), TRUE)) return 3;
    const auto output = directory + L"bundle-arguments.txt";
    SetEnvironmentVariableW(L"UPX_BUNDLE_SMOKE_OUTPUT", output.c_str());
    if (FAILED(CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED))) return 4;
    auto module = LoadLibraryW(dllPath.c_str());
    if (!module) return 5;
    auto getFactory = reinterpret_cast<FactoryFunction>(GetProcAddress(module, "DllGetClassObject"));
    IClassFactory* factory = nullptr;
    IExplorerCommand* command = nullptr;
    if (!getFactory || FAILED(getFactory(BundleId, IID_PPV_ARGS(&factory)))) return 6;
    const auto created = factory->CreateInstance(nullptr, IID_PPV_ARGS(&command));
    factory->Release();
    if (FAILED(created)) return 7;
    auto pair = Items({ argv[2], argv[3] });
    auto single = Items({ argv[2] });
    auto rarFirst = Items({ argv[3], argv[2] });
    auto unsupported = Items({ argv[2], argv[4] });
    if (!pair || !single || !rarFirst || !unsupported) return 8;
    EXPCMDSTATE state{};
    bool passed = SUCCEEDED(command->GetState(pair, FALSE, &state)) && state == ECS_ENABLED;
    passed = passed && SUCCEEDED(command->GetState(single, FALSE, &state)) && state == ECS_HIDDEN;
    passed = passed && SUCCEEDED(command->GetState(rarFirst, FALSE, &state)) && state == ECS_ENABLED;
    passed = passed && SUCCEEDED(command->GetState(unsupported, FALSE, &state)) && state == ECS_HIDDEN;
    passed = passed && SUCCEEDED(command->Invoke(pair, nullptr));
    pair->Release(); single->Release(); rarFirst->Release(); unsupported->Release();
    command->Release();
    FreeLibrary(module);
    CoUninitialize();
    HANDLE file = INVALID_HANDLE_VALUE;
    for (int attempt = 0; attempt < 100 && file == INVALID_HANDLE_VALUE; ++attempt)
    {
        Sleep(50);
        file = CreateFileW(output.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    }
    if (file == INVALID_HANDLE_VALUE) return 9;
    std::vector<wchar_t> contents(65536);
    DWORD read = 0;
    passed = passed && ReadFile(file, contents.data(), static_cast<DWORD>(contents.size() * sizeof(wchar_t)), &read, nullptr);
    CloseHandle(file);
    const std::wstring expected = std::wstring(L"--bundle-zips\n") + argv[2] + L"\n" + argv[3] + L"\n";
    passed = passed && std::wstring(contents.data(), read / sizeof(wchar_t)) == expected;
    std::cout << (passed ? "PASS native ZIP/RAR multiselect, filtering and argv forwarding\n" : "FAIL native ZIP bundle command\n");
    return passed ? 0 : 1;
}
