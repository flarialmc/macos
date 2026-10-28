/* Native regression: the child reserves the parent's DLL base before loading
 * the fixture. The resolved address must match the child's reported export;
 * only then is it safe to execute a remote thread. Build with the same x64
 * toolchain as injector.c and remote-loader-fixture.dll at base 0x60000000. */
#define wmain injector_main
#include "../injector.c"
#undef wmain
#include <stdlib.h>

int wmain(int argc, wchar_t **argv)
{
    if (argc == 3 && !_wcsicmp(argv[1], L"--child")) {
        if (!VirtualAlloc((void *)0x60000000, 0x10000, MEM_RESERVE, PAGE_NOACCESS)) return 20;
        HMODULE fixture = LoadLibraryW(argv[2]);
        if (!fixture) return 21;
        FARPROC marker = GetProcAddress(fixture, "marker");
        wprintf(L"%p\n", marker); fflush(stdout);
        Sleep(30000);
        return 0;
    }
    if (argc != 2) return 2;
    HMODULE fixture = LoadLibraryW(argv[1]);
    FARPROC local = fixture ? GetProcAddress(fixture, "marker") : NULL;
    if (!local || (ULONG_PTR)fixture != 0x60000000) return 22;
    SECURITY_ATTRIBUTES security = {sizeof(security), NULL, TRUE};
    HANDLE readpipe, writepipe;
    if (!CreatePipe(&readpipe, &writepipe, &security, 0)) return 23;
    SetHandleInformation(readpipe, HANDLE_FLAG_INHERIT, 0);
    STARTUPINFOW startup = {sizeof(startup)};
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdOutput = writepipe; startup.hStdError = writepipe;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION child = {0};
    wchar_t executable[MAX_PATH], command[3 * MAX_PATH];
    GetModuleFileNameW(NULL, executable, MAX_PATH);
    swprintf(command, 3 * MAX_PATH, L"\"%ls\" --child \"%ls\"", executable, argv[1]);
    BOOL created = CreateProcessW(executable, command, NULL, NULL, TRUE,
                                  CREATE_NO_WINDOW, NULL, NULL, &startup, &child);
    CloseHandle(writepipe);
    if (!created) { CloseHandle(readpipe); return 24; }
    char response[64] = {0}; DWORD length = 0;
    BOOL read = ReadFile(readpipe, response, sizeof(response)-1, &length, NULL);
    CloseHandle(readpipe);
    ULONG_PTR expected = read ? (ULONG_PTR)_strtoui64(response, NULL, 16) : 0;
#ifdef TEST_LOCAL_ADDRESS
    FARPROC resolved = local;
#else
    FARPROC resolved = remote_proc_address(child.dwProcessId, local);
#endif
    int result = 1;
    if (expected && expected != (ULONG_PTR)local && expected == (ULONG_PTR)resolved) {
        HANDLE thread = CreateRemoteThread(child.hProcess, NULL, 0,
                     (LPTHREAD_START_ROUTINE)resolved, NULL, 0, NULL);
        DWORD code = 0;
        if (thread) {
            if (WaitForSingleObject(thread, 5000) == WAIT_OBJECT_0 &&
                GetExitCodeThread(thread, &code) && code == 0x13572468) result = 0;
            CloseHandle(thread);
        }
    }
    printf("different DLL bases: %s; resolved target export: %s; remote call: %s\n",
           expected && expected != (ULONG_PTR)local ? "yes" : "no",
           expected && expected == (ULONG_PTR)resolved ? "yes" : "no",
           result ? "failed" : "passed");
    TerminateProcess(child.hProcess, 0);
    WaitForSingleObject(child.hProcess, 5000);
    CloseHandle(child.hThread); CloseHandle(child.hProcess);
    FreeLibrary(fixture);
    return result;
}
