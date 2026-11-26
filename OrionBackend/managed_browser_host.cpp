#include "pch.h"

#include <nethost.h>
#include <hostfxr.h>

namespace {
int startupError(const wchar_t* message, DWORD error)
{
    OutputDebugStringW(message);
    MessageBoxW(nullptr, message, L"Orion startup failed", MB_ICONERROR | MB_OK);
    return error ? static_cast<int>(error) : EXIT_FAILURE;
}
}

// Only the CEF browser process enters here. All Chromium subprocesses have
// already returned directly from CefExecuteProcess without loading .NET.
int runManagedBrowser()
{
    // The CEF bootstrap is the actual process image. The managed apphost's
    // manifest is activated for WinRT registration and does not set its DPI.
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

    const auto appDir = std::filesystem::path(moduleDir(nullptr)).parent_path().parent_path();
    const auto appExe = appDir / L"Orion.exe";
    const auto assembly = appDir / L"Orion.dll";
    const get_hostfxr_parameters parameters = {sizeof(parameters), assembly.c_str(), nullptr};
    std::vector<wchar_t> fxrPath(32768);
    size_t length = fxrPath.size();
    const auto netHostPath = appDir / L"nethost.dll";
    const HMODULE netHost = LoadLibraryExW(netHostPath.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
    const auto locate = netHost ? reinterpret_cast<decltype(&get_hostfxr_path)>(
        GetProcAddress(netHost, "get_hostfxr_path")) : nullptr;
    if (!locate || locate(fxrPath.data(), &length, &parameters) != 0)
        return startupError(L"Orion could not locate the .NET runtime.", ERROR_MOD_NOT_FOUND);
    const HMODULE fxr = LoadLibraryExW(fxrPath.data(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
    const auto run = fxr ? reinterpret_cast<hostfxr_main_startupinfo_fn>(
        GetProcAddress(fxr, "hostfxr_main_startupinfo")) : nullptr;
    if (!run) return startupError(L"Orion could not start the .NET runtime.", GetLastError());

    // Orion.exe contains the SDK-generated manifest registering WinUI's
    // unpackaged WinRT classes. Activate it from the app directory where
    // the manifest's relative DLL paths resolve.
    ACTCTXW context{sizeof(context)};
    context.dwFlags = ACTCTX_FLAG_RESOURCE_NAME_VALID | ACTCTX_FLAG_ASSEMBLY_DIRECTORY_VALID;
    context.lpSource = appExe.c_str();
    context.lpResourceName = MAKEINTRESOURCEW(1);
    context.lpAssemblyDirectory = appDir.c_str();
    const HANDLE actctx = CreateActCtxW(&context);
    ULONG_PTR cookie = 0;
    if (actctx == INVALID_HANDLE_VALUE || !ActivateActCtx(actctx, &cookie))
        return startupError(L"Orion could not activate its Windows app manifest.", GetLastError());

    int argc = 0;
    auto argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv) return startupError(L"Orion could not read its startup arguments.", GetLastError());
    const auto fxrDirectory = std::filesystem::path(fxrPath.data()).parent_path();
    // The normal apphost supplies the app folder for a self-contained runtime
    // or the .NET installation root (host/fxr/<version>/) for a shared runtime.
    const auto dotnetRoot = fxrDirectory == appDir ? appDir
        : fxrDirectory.parent_path().parent_path().parent_path();
    const DPI_AWARENESS_CONTEXT previousDpi = SetThreadDpiAwarenessContext(
        DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    const int result = run(argc, const_cast<const wchar_t**>(argv), appExe.c_str(),
                           dotnetRoot.c_str(), assembly.c_str());
    if (previousDpi)
        SetThreadDpiAwarenessContext(previousDpi);
    LocalFree(argv);
    DeactivateActCtx(0, cookie);
    ReleaseActCtx(actctx);
    return result;
}
