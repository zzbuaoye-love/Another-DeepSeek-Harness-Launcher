#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <setupapi.h>
#include <shellapi.h>
#include <objbase.h>
#include <filesystem>
#include <string>
#include <vector>
#include <stdexcept>
#include <cstdint>

namespace fs = std::filesystem;
static constexpr char marker[] = "ADL_CAB_PAYLOAD1";
struct Extraction { fs::path root; DWORD error = ERROR_SUCCESS; };
static UINT CALLBACK ExtractCallback(PVOID context, UINT notification, UINT_PTR param1, UINT_PTR)
{
    auto* extraction = static_cast<Extraction*>(context);
    if (notification == SPFILENOTIFY_NEEDNEWCABINET) { extraction->error = ERROR_INVALID_DATA; return ERROR_CANCELLED; }
    if (notification != SPFILENOTIFY_FILEINCABINET) return NO_ERROR;
    auto* info = reinterpret_cast<FILE_IN_CABINET_INFO_W*>(param1);
    try
    {
        fs::path relative(info->NameInCabinet);
        if (relative.is_absolute() || relative.has_root_path() || std::wstring(info->NameInCabinet).find(L':') != std::wstring::npos)
            throw std::runtime_error("Unsafe archive path");
        for (const auto& part : relative) if (part == L".." || part == L".") throw std::runtime_error("Unsafe archive path");
        auto target = extraction->root / relative;
        if (target.wstring().size() >= MAX_PATH) throw std::runtime_error("Path too long");
        fs::create_directories(target.parent_path());
        wcscpy_s(info->FullTargetName, target.c_str());
        return FILEOP_DOIT;
    }
    catch (...) { extraction->error = ERROR_INVALID_DATA; return FILEOP_ABORT; }
}
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR commandLine, int)
{
    fs::path temp;
    HANDLE source = INVALID_HANDLE_VALUE, output = INVALID_HANDLE_VALUE;
    try
    {
        wchar_t self[MAX_PATH], tempRoot[MAX_PATH];
        if (!GetModuleFileNameW(nullptr, self, MAX_PATH) || !GetTempPathW(MAX_PATH, tempRoot)) throw std::runtime_error("Unable to locate setup");
        GUID id; if (FAILED(CoCreateGuid(&id))) throw std::runtime_error("Unable to create temporary directory");
        wchar_t unique[40]; StringFromGUID2(id, unique, 40);
        temp = fs::path(tempRoot) / L"AnotherDSHL" / unique;
        fs::create_directories(temp);
        fs::path cabinet = temp / L"payload.cab";
        source = CreateFileW(self, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
        LARGE_INTEGER size, offset;
        if (source == INVALID_HANDLE_VALUE || !GetFileSizeEx(source, &size) || size.QuadPart < sizeof(uint64_t) + sizeof(marker) - 1) throw std::runtime_error("Invalid setup");
        offset.QuadPart = size.QuadPart - sizeof(uint64_t) - sizeof(marker) + 1;
        if (!SetFilePointerEx(source, offset, nullptr, FILE_BEGIN)) throw std::runtime_error("Invalid setup");
        uint64_t length = 0; char actual[sizeof(marker) - 1]; DWORD read;
        if (!ReadFile(source, &length, sizeof(length), &read, nullptr) || read != sizeof(length) ||
            !ReadFile(source, actual, sizeof(actual), &read, nullptr) || read != sizeof(actual) || memcmp(actual, marker, sizeof(actual)) != 0 ||
            length == 0 || length > static_cast<uint64_t>(offset.QuadPart)) throw std::runtime_error("Invalid payload");
        offset.QuadPart -= length;
        SetFilePointerEx(source, offset, nullptr, FILE_BEGIN);
        output = CreateFileW(cabinet.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (output == INVALID_HANDLE_VALUE) throw std::runtime_error("Unable to extract payload");
        std::vector<char> buffer(1024 * 1024);
        while (length)
        {
            DWORD count = static_cast<DWORD>((std::min)(length, static_cast<uint64_t>(buffer.size()))), written;
            if (!ReadFile(source, buffer.data(), count, &read, nullptr) || read != count || !WriteFile(output, buffer.data(), read, &written, nullptr) || written != read) throw std::runtime_error("Incomplete payload");
            length -= read;
        }
        CloseHandle(source); source = INVALID_HANDLE_VALUE; CloseHandle(output); output = INVALID_HANDLE_VALUE;
        Extraction extraction{temp};
        if (!SetupIterateCabinetW(cabinet.c_str(), 0, ExtractCallback, &extraction) || extraction.error != ERROR_SUCCESS) throw std::runtime_error("Unable to decompress payload");
        fs::remove(cabinet);
        auto executable = temp / L"AnotherDSHL.Installer.exe";
        std::wstring command = L"\"" + executable.wstring() + L"\" " + commandLine;
        STARTUPINFOW startup{sizeof(startup)}; PROCESS_INFORMATION process{};
        if (!CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, FALSE, 0, nullptr, temp.c_str(), &startup, &process)) throw std::runtime_error("Unable to start WinUI installer");
        CloseHandle(process.hThread); WaitForSingleObject(process.hProcess, INFINITE);
        DWORD exitCode = 1; GetExitCodeProcess(process.hProcess, &exitCode); CloseHandle(process.hProcess);
        std::error_code ignored; fs::remove_all(temp, ignored);
        return static_cast<int>(exitCode);
    }
    catch (...)
    {
        if (source != INVALID_HANDLE_VALUE) CloseHandle(source);
        if (output != INVALID_HANDLE_VALUE) CloseHandle(output);
        if (!temp.empty()) { std::error_code ignored; fs::remove_all(temp, ignored); }
        MessageBoxW(nullptr, L"无法解压或启动安装程序。请确认磁盘空间充足，并重新获取完整的安装包", L"ADL 安装程序", MB_OK | MB_ICONERROR);
        return 1;
    }
}
