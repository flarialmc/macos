#include <windows.h>
__declspec(dllexport) DWORD WINAPI marker(void *unused) { (void)unused; return 0x13572468; }
/* Ensure the DLL contains a relocation even with RIP-relative generated code. */
__declspec(dllexport) void *anchor = (void *)marker;
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{ (void)instance; (void)reason; (void)reserved; return TRUE; }
