#include <windows.h>
#include <shellapi.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <strsafe.h>
#include <atomic>
#include <new>
#include <string>
#include <vector>

#pragma comment(lib, "Advapi32.lib")
#pragma comment(lib, "Ole32.lib")
#pragma comment(lib, "Shell32.lib")
#pragma comment(lib, "Shlwapi.lib")

namespace
{
    const CLSID CLSID_UnityPackageExplorerCommand =
        { 0x1c490a47, 0x4d85, 0x4aa0, { 0xa2, 0xa3, 0x4a, 0x78, 0x27, 0xb5, 0xda, 0x79 } };
    std::atomic<long> g_objectCount = 0;
    std::atomic<long> g_serverLocks = 0;

    bool IsSupportedPath(PCWSTR path)
    {
        if (path == nullptr)
        {
            return false;
        }

        const wchar_t* extension = PathFindExtensionW(path);
        return extension != nullptr &&
            (_wcsicmp(extension, L".zip") == 0 ||
             _wcsicmp(extension, L".unitypackage") == 0 ||
             _wcsicmp(extension, L".prefab") == 0);
    }

    HRESULT GetSelectedPath(IShellItemArray* items, std::wstring& path)
    {
        if (items == nullptr)
        {
            return E_INVALIDARG;
        }

        DWORD count = 0;
        HRESULT result = items->GetCount(&count);
        if (FAILED(result))
        {
            return result;
        }
        if (count != 1)
        {
            return E_INVALIDARG;
        }

        IShellItem* item = nullptr;
        result = items->GetItemAt(0, &item);
        if (FAILED(result))
        {
            return result;
        }

        PWSTR filePath = nullptr;
        result = item->GetDisplayName(SIGDN_FILESYSPATH, &filePath);
        item->Release();
        if (FAILED(result))
        {
            return result;
        }

        path.assign(filePath);
        CoTaskMemFree(filePath);
        return IsSupportedPath(path.c_str()) ? S_OK : E_INVALIDARG;
    }

    HRESULT GetApplicationPath(std::wstring& path)
    {
        constexpr PCWSTR keyPath = L"Software\\UnityPackageExplorer\\Install";
        constexpr PCWSTR valueName = L"AppPath";
        DWORD size = 0;
        LSTATUS status = RegGetValueW(HKEY_CURRENT_USER, keyPath, valueName, RRF_RT_REG_SZ, nullptr, nullptr, &size);
        if (status != ERROR_SUCCESS || size < sizeof(wchar_t))
        {
            return HRESULT_FROM_WIN32(status == ERROR_SUCCESS ? ERROR_FILE_NOT_FOUND : status);
        }

        std::vector<wchar_t> buffer(size / sizeof(wchar_t));
        status = RegGetValueW(HKEY_CURRENT_USER, keyPath, valueName, RRF_RT_REG_SZ, nullptr, buffer.data(), &size);
        if (status != ERROR_SUCCESS)
        {
            return HRESULT_FROM_WIN32(status);
        }

        path.assign(buffer.data());
        return PathFileExistsW(path.c_str()) ? S_OK : HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
    }

    HRESULT LaunchPreview(PCWSTR filePath)
    {
        std::wstring appPath;
        HRESULT result = GetApplicationPath(appPath);
        if (FAILED(result))
        {
            return result;
        }

        std::wstring arguments;
        arguments.reserve(wcslen(filePath) + 3);
        arguments.push_back(L'"');
        arguments.append(filePath);
        arguments.push_back(L'"');

        HINSTANCE launchResult = ShellExecuteW(nullptr, L"open", appPath.c_str(), arguments.c_str(), nullptr, SW_SHOWNORMAL);
        if (reinterpret_cast<INT_PTR>(launchResult) > 32)
        {
            return S_OK;
        }

        const DWORD error = GetLastError();
        return HRESULT_FROM_WIN32(error == ERROR_SUCCESS ? ERROR_GEN_FAILURE : error);
    }

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        ExplorerCommand() { ++g_objectCount; }
        ~ExplorerCommand() { --g_objectCount; }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }
            *object = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(IExplorerCommand))
            {
                *object = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return ++references_; }
        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG remaining = --references_;
            if (remaining == 0)
            {
                delete this;
            }
            return remaining;
        }

        IFACEMETHODIMP GetTitle(IShellItemArray*, PWSTR* title) override
        {
            return SHStrDupW(L"UnityPackage Explorer \u3067\u958B\u304F", title);
        }

        IFACEMETHODIMP GetIcon(IShellItemArray*, PWSTR* icon) override
        {
            if (icon == nullptr)
            {
                return E_POINTER;
            }
            *icon = nullptr;

            std::wstring appPath;
            HRESULT result = GetApplicationPath(appPath);
            if (FAILED(result))
            {
                return result;
            }
            appPath.append(L",0");
            return SHStrDupW(appPath.c_str(), icon);
        }

        IFACEMETHODIMP GetToolTip(IShellItemArray*, PWSTR* tooltip) override
        {
            if (tooltip != nullptr)
            {
                *tooltip = nullptr;
            }
            return E_NOTIMPL;
        }

        IFACEMETHODIMP GetCanonicalName(GUID* commandName) override
        {
            if (commandName == nullptr)
            {
                return E_POINTER;
            }
            *commandName = CLSID_UnityPackageExplorerCommand;
            return S_OK;
        }

        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
        {
            if (state == nullptr)
            {
                return E_POINTER;
            }
            *state = ECS_HIDDEN;

            std::wstring path;
            const HRESULT result = GetSelectedPath(items, path);
            if (SUCCEEDED(result))
            {
                *state = ECS_ENABLED;
                return S_OK;
            }
            return result == E_INVALIDARG ? S_OK : result;
        }

        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override
        {
            std::wstring path;
            const HRESULT result = GetSelectedPath(items, path);
            return FAILED(result) ? result : LaunchPreview(path.c_str());
        }

        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (flags == nullptr)
            {
                return E_POINTER;
            }
            *flags = ECF_DEFAULT;
            return S_OK;
        }

        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            if (commands != nullptr)
            {
                *commands = nullptr;
            }
            return E_NOTIMPL;
        }

    private:
        std::atomic<ULONG> references_ = 1;
    };

    class ExplorerCommandFactory final : public IClassFactory
    {
    public:
        ExplorerCommandFactory() { ++g_objectCount; }
        ~ExplorerCommandFactory() { --g_objectCount; }

        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (object == nullptr)
            {
                return E_POINTER;
            }
            *object = nullptr;
            if (iid == IID_IUnknown || iid == IID_IClassFactory)
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }

        IFACEMETHODIMP_(ULONG) AddRef() override { return ++references_; }
        IFACEMETHODIMP_(ULONG) Release() override
        {
            const ULONG remaining = --references_;
            if (remaining == 0)
            {
                delete this;
            }
            return remaining;
        }

        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** object) override
        {
            if (outer != nullptr)
            {
                return CLASS_E_NOAGGREGATION;
            }
            auto* command = new (std::nothrow) ExplorerCommand();
            if (command == nullptr)
            {
                return E_OUTOFMEMORY;
            }
            const HRESULT result = command->QueryInterface(iid, object);
            command->Release();
            return result;
        }

        IFACEMETHODIMP LockServer(BOOL lock) override
        {
            if (lock)
            {
                ++g_serverLocks;
            }
            else
            {
                --g_serverLocks;
            }
            return S_OK;
        }

    private:
        std::atomic<ULONG> references_ = 1;
    };
}

STDAPI DllGetClassObject(REFCLSID classId, REFIID iid, void** object)
{
    if (classId != CLSID_UnityPackageExplorerCommand)
    {
        return CLASS_E_CLASSNOTAVAILABLE;
    }

    auto* factory = new (std::nothrow) ExplorerCommandFactory();
    if (factory == nullptr)
    {
        return E_OUTOFMEMORY;
    }
    const HRESULT result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}

STDAPI DllCanUnloadNow()
{
    return g_objectCount == 0 && g_serverLocks == 0 ? S_OK : S_FALSE;
}
