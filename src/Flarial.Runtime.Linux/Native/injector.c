/*
 * Derived from BedrockOnLinux src/injector.c
 * Copyright (c) 2026 BedrockOnLinux contributors
 * SPDX-License-Identifier: MIT
 * (full MIT text: src/Flarial.Runtime.Linux/Native/LICENSE-BedrockOnLinux)
 * Modified: loads a list of libraries (dependencies first) and takes the process name first.
 *
 * Build (msvc-wine): see build-injector.sh in this directory.
 */
/* bol injector — load a client .dll into the running Minecraft via the classic
 * CreateRemoteThread + LoadLibraryW technique, so BedrockOnLinux can inject
 * directly without any third-party injector .exe. Built for Wine (x86_64) and
 * run inside the game's own Wine prefix so it shares the wineserver and can see
 * Minecraft.Windows.exe -- by snapshot name, or by PEB image path when Wine
 * reports no name for it. Returns 0 on success.
 *
 * Build: x86_64-w64-mingw32-gcc -O2 -municode -s injector.c -o ../bol/injector.exe
 * Usage: injector.exe <process.exe> <dll> [<dll>...]   (dependencies first, the modification last)
 *
 * Launcher payload: when the environment variable FLARIAL_LAUNCHER_PAYLOAD (UTF-8 JSON, e.g. {"access_token":"..."}) is
 * set, it is removed from the injector's own environment and attached to the loader thread of the LAST library (the
 * client) as its thread description before that thread starts. This is the same channel the Windows launcher uses
 * (FlarialClient.Loader: SetThreadDescription on the suspended LoadLibraryW thread); the client reads and clears it with
 * GetThreadDescription(GetCurrentThread()) in DLL_PROCESS_ATTACH. An environment variable (readable only by the same
 * user in /proc) is used instead of argv, which any local user can read.
 */
#include <windows.h>
#include <winternl.h>
#include <tlhelp32.h>
#include <stdio.h>
#include <wchar.h>

typedef NTSTATUS (WINAPI *NtQueryInformationProcess_t)(HANDLE, PROCESSINFOCLASS,
                                                       PVOID, ULONG, PULONG);

/* Read a process's image path out of its own PEB. Wine reports an empty
 * szExeFile for the game started by the GDK loader, so the snapshot name alone
 * never matches Minecraft.Windows.exe; the PEB still holds the real path. */
static BOOL peb_image_name(HANDLE proc, wchar_t *out, size_t cap)
{
    static NtQueryInformationProcess_t query;
    PROCESS_BASIC_INFORMATION pbi;
    RTL_USER_PROCESS_PARAMETERS params;
    PEB peb;
    ULONG got = 0;
    size_t n;

    if (!query) {
        query = (NtQueryInformationProcess_t)(void *)GetProcAddress(
            GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationProcess");
        if (!query) return FALSE;
    }
    if (query(proc, ProcessBasicInformation, &pbi, sizeof(pbi), &got)) return FALSE;
    if (!ReadProcessMemory(proc, pbi.PebBaseAddress, &peb, sizeof(peb), NULL))
        return FALSE;
    if (!ReadProcessMemory(proc, peb.ProcessParameters, &params, sizeof(params), NULL))
        return FALSE;

    n = params.ImagePathName.Length / sizeof(wchar_t);
    if (n >= cap) n = cap - 1;
    if (!ReadProcessMemory(proc, params.ImagePathName.Buffer, out,
                           n * sizeof(wchar_t), NULL))
        return FALSE;
    out[n] = 0;
    return TRUE;
}

static const wchar_t *base_name(const wchar_t *path)
{
    const wchar_t *base = path, *p;
    for (p = path; *p; ++p)
        if (*p == L'\\' || *p == L'/') base = p + 1;
    return base;
}

static DWORD find_pid(const wchar_t *name)
{
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return 0;
    PROCESSENTRY32W pe;
    pe.dwSize = sizeof(pe);
    DWORD pid = 0;
    if (Process32FirstW(snap, &pe))
        do {
            if (!_wcsicmp(pe.szExeFile, name)) { pid = pe.th32ProcessID; break; }
            if (pe.szExeFile[0]) continue;   /* named, just not the one we want */

            HANDLE proc = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ,
                                      FALSE, pe.th32ProcessID);
            if (!proc) continue;
            wchar_t image[MAX_PATH];
            if (peb_image_name(proc, image, MAX_PATH) &&
                !_wcsicmp(base_name(image), name))
                pid = pe.th32ProcessID;
            CloseHandle(proc);
            if (pid) break;
        } while (Process32NextW(snap, &pe));
    CloseHandle(snap);
    return pid;
}

/* Wine may relocate system modules differently in the game and injector.
 * Locate the module that actually owns an export (including forwarded exports),
 * then translate its RVA into the target process's copy of that module. */
static FARPROC remote_proc_address(DWORD pid, FARPROC local)
{
    HMODULE owner = NULL;
    wchar_t path[MAX_PATH];
    if (!local || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)local, &owner))
        return NULL;
    DWORD length = GetModuleFileNameW(owner, path, MAX_PATH);
    if (!length || length >= MAX_PATH) return NULL;
    ULONG_PTR offset = (ULONG_PTR)local - (ULONG_PTR)owner;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid);
    if (snapshot == INVALID_HANDLE_VALUE) return NULL;
    MODULEENTRY32W module;
    module.dwSize = sizeof(module);
    FARPROC remote = NULL;
    if (Module32FirstW(snapshot, &module)) do {
        if (!_wcsicmp(module.szModule, base_name(path)) && offset < module.modBaseSize) {
            remote = (FARPROC)(module.modBaseAddr + offset);
            break;
        }
    } while (Module32NextW(snapshot, &module));
    CloseHandle(snapshot);
    return remote;
}

typedef HRESULT (WINAPI *SetThreadDescription_t)(HANDLE, PCWSTR);

#define PAYLOAD_ENV L"FLARIAL_LAUNCHER_PAYLOAD"
#define PAYLOAD_MAX_CHARS (64 * 1024)   /* the client rejects anything larger */

/* Takes the payload out of the environment (NULL when absent or too large). The launcher builds it with the JSON
 * serializer, which escapes everything outside ASCII, so Wine's Unix-to-UTF-16 conversion of the variable is exact. */
static wchar_t *take_payload(void)
{
    DWORD need = GetEnvironmentVariableW(PAYLOAD_ENV, NULL, 0);
    if (!need) return NULL;
    wchar_t *wide = (wchar_t *)HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, (SIZE_T)need * sizeof(wchar_t));
    if (!wide) return NULL;
    DWORD got = GetEnvironmentVariableW(PAYLOAD_ENV, wide, need);
    SetEnvironmentVariableW(PAYLOAD_ENV, NULL);
    if (got && got < need && got <= PAYLOAD_MAX_CHARS) return wide;
    SecureZeroMemory(wide, (SIZE_T)need * sizeof(wchar_t));
    HeapFree(GetProcessHeap(), 0, wide);
    return NULL;
}

/* Attaches the payload to a not yet started thread. Failure is only a warning (as upstream, which ignores the result):
 * the client then simply runs signed out. */
static void describe_thread(HANDLE th, const wchar_t *payload)
{
    SetThreadDescription_t set = NULL;
    const wchar_t *modules[] = { L"kernelbase.dll", L"kernel32.dll" };
    for (int i = 0; i < 2 && !set; ++i) {
        HMODULE m = GetModuleHandleW(modules[i]);
        if (m) set = (SetThreadDescription_t)(void *)GetProcAddress(m, "SetThreadDescription");
    }
    if (!set) { fwprintf(stderr, L"WARN payload not delivered: SetThreadDescription is unavailable\n"); return; }
    HRESULT hr = set(th, payload);
    if (FAILED(hr)) fwprintf(stderr, L"WARN payload not delivered: SetThreadDescription 0x%08lx\n", (unsigned long)hr);
    else fwprintf(stderr, L"INFO payload delivered (%u chars)\n", (unsigned)wcslen(payload));
}

/* Loads one library into the target through LoadLibraryW on a remote thread. With a payload the thread is created
 * suspended and described before it runs, so the library's DllMain sees it on its own (current) thread.
 * Returns 0 on success or the exit code to report. */
static int load_library(HANDLE h, LPTHREAD_START_ROUTINE loadlib, const wchar_t *dll, const wchar_t *payload)
{
    SIZE_T n = (wcslen(dll) + 1) * sizeof(wchar_t);
    void *rem = VirtualAllocEx(h, NULL, n, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (!rem) {
        fwprintf(stderr, L"ERR allocate process memory: %lu\n", GetLastError());
        return 5;
    }
    if (!WriteProcessMemory(h, rem, dll, n, NULL)) {
        fwprintf(stderr, L"ERR write process memory: %lu\n", GetLastError());
        VirtualFreeEx(h, rem, 0, MEM_RELEASE);
        return 5;
    }

    HANDLE th = CreateRemoteThread(h, NULL, 0, loadlib,
                                   rem, payload ? CREATE_SUSPENDED : 0, NULL);
    if (!th) {
        fwprintf(stderr, L"ERR CreateRemoteThread: %lu\n", GetLastError());
        VirtualFreeEx(h, rem, 0, MEM_RELEASE);
        return 6;
    }
    if (payload) {
        describe_thread(th, payload);
        if (ResumeThread(th) == (DWORD)-1) {
            fwprintf(stderr, L"ERR ResumeThread: %lu\n", GetLastError());
            TerminateThread(th, 0);
            CloseHandle(th);
            VirtualFreeEx(h, rem, 0, MEM_RELEASE);
            return 6;
        }
    }

    DWORD wait = WaitForSingleObject(th, 15000);
    if (wait != WAIT_OBJECT_0) {
        if (wait == WAIT_TIMEOUT)
            fwprintf(stderr, L"ERR LoadLibrary thread timed out\n");
        else
            fwprintf(stderr, L"ERR waiting for LoadLibrary thread: %lu\n",
                     GetLastError());
        /*
         * The remote thread can still be reading the path after a timeout.
         * Do not free that allocation and create a use-after-free in the
         * target; Wine will reclaim the tiny buffer when Minecraft exits.
         */
        CloseHandle(th);
        return 8;
    }
    DWORD mod = 0;                       /* low 32 bits of the loaded HMODULE */
    if (!GetExitCodeThread(th, &mod)) {
        fwprintf(stderr, L"ERR GetExitCodeThread: %lu\n", GetLastError());
        VirtualFreeEx(h, rem, 0, MEM_RELEASE);
        CloseHandle(th);
        return 9;
    }
    VirtualFreeEx(h, rem, 0, MEM_RELEASE);
    CloseHandle(th);

    if (!mod) {
        fwprintf(stderr, L"ERR LoadLibrary returned 0 (bad DLL / 32-bit / missing "
                         L"deps): %ls\n", dll);
        return 7;
    }
    return 0;
}

/* Usage: injector.exe <process.exe> <dll> [<dll> ...]
 * The libraries are loaded in order: every one but the last is a dependency of
 * the last (a failure there is only reported), the last is the actual
 * modification and decides the exit code. */
int wmain(int argc, wchar_t **argv)
{
    if (argc < 3) { fwprintf(stderr, L"usage: injector <process.exe> <dll> [<dll>...]\n"); return 2; }
    const wchar_t *proc = argv[1];
    wchar_t *payload = take_payload();   /* for the last library only; wiped before every return below */

    DWORD pid = find_pid(proc);
    if (!pid) { fwprintf(stderr, L"ERR process not found: %ls\n", proc); return 3; }

    HANDLE h = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_VM_OPERATION |
                           PROCESS_VM_WRITE | PROCESS_VM_READ |
                           PROCESS_QUERY_INFORMATION, FALSE, pid);
    if (!h) { fwprintf(stderr, L"ERR OpenProcess: %lu\n", GetLastError()); return 4; }

    FARPROC local = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    LPTHREAD_START_ROUTINE loadlib = (LPTHREAD_START_ROUTINE)remote_proc_address(pid, local);
    if (!loadlib) {
        fwprintf(stderr, L"ERR resolve LoadLibraryW in target process\n");
        CloseHandle(h);
        return 10;
    }

    int last = argc - 1;
    for (int i = 2; i < last; ++i) {
        int dep = load_library(h, loadlib, argv[i], NULL);
        if (dep) fwprintf(stderr, L"WARN dependency not loaded (%d): %ls\n", dep, argv[i]);
        /* a timed-out LoadLibrary still holds the loader lock in the target: every further remote load would
         * block behind it for its full wait, so stop here instead of stacking 15 s timeouts */
        if (dep == 8) { CloseHandle(h); return 8; }
    }

    int code = load_library(h, loadlib, argv[last], payload);
    CloseHandle(h);
    if (payload) { SecureZeroMemory(payload, wcslen(payload) * sizeof(wchar_t)); HeapFree(GetProcessHeap(), 0, payload); }
    if (code) return code;

    fwprintf(stderr, L"OK injected %ls into %ls (pid %lu)\n", argv[last], proc, pid);
    return 0;
}
