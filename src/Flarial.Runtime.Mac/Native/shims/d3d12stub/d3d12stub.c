// d3d12.dll stub: lets mods with a static d3d12.dll import load while the game stays on D3D11.
// Every entry point fails, so the game's D3D12 probe falls back to its D3D11 renderer.
// Install next to Minecraft.Windows.exe and run with WINEDLLOVERRIDES d3d12=n.
#include <windows.h>
HRESULT WINAPI D3D12CreateDevice(void* adapter, int level, REFIID riid, void** dev) { if (dev) *dev = NULL; return DXGI_ERROR_UNSUPPORTED; }
HRESULT WINAPI D3D12GetDebugInterface(REFIID riid, void** out) { if (out) *out = NULL; return E_NOINTERFACE; }
HRESULT WINAPI D3D12SerializeRootSignature(const void* desc, int ver, void** blob, void** err) { if (blob) *blob = NULL; if (err) *err = NULL; return E_NOTIMPL; }
HRESULT WINAPI D3D12SerializeVersionedRootSignature(const void* desc, void** blob, void** err) { if (blob) *blob = NULL; if (err) *err = NULL; return E_NOTIMPL; }
HRESULT WINAPI D3D12CreateRootSignatureDeserializer(const void* d, SIZE_T n, REFIID riid, void** out) { if (out) *out = NULL; return E_NOTIMPL; }
HRESULT WINAPI D3D12CreateVersionedRootSignatureDeserializer(const void* d, SIZE_T n, REFIID riid, void** out) { if (out) *out = NULL; return E_NOTIMPL; }
HRESULT WINAPI D3D12EnableExperimentalFeatures(UINT n, const void* iids, void* cfg, UINT* sizes) { return E_NOTIMPL; }
HRESULT WINAPI D3D12GetInterface(REFCLSID clsid, REFIID riid, void** out) { if (out) *out = NULL; return E_NOINTERFACE; }
BOOL WINAPI DllMain(HINSTANCE h, DWORD r, LPVOID p) { return TRUE; }
