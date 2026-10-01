// Test stand-in for a mod's d3d12.dll that the proxy chains to (scskiller.ini next=): forwards D3D12CreateDevice to the
// system dll and counts the calls. Only D3D12CreateDevice, like many wrappers: the proxy takes the rest from the system dll.
// After FakeNext_SwapPs it acts like a shader-replacing wrapper (ReShade with a RenoDX addon): the game gets a wrapper
// device whose CreateGraphicsPipelineState passes the desc on with that pixel shader instead of the game's.
// Never shipped (build/publish.ps1 copies named files only); `selftest chain` loads it.
#include <windows.h>
#define D3D12CreateDevice D3D12CreateDevice_h  // d3d12.h declares it without dllexport
#include <d3d12.h>
#undef D3D12CreateDevice
#include <string>

static LONG g_calls;
static std::string g_ps;

extern "C" __declspec(dllexport) LONG WINAPI FakeNext_Calls() { return g_calls; }
extern "C" __declspec(dllexport) void WINAPI FakeNext_SwapPs(const void* p, SIZE_T n) { g_ps.assign((const char*)p, n); }

// The wrapper: every slot forwards to the same slot of the wrapped device (a thunk swaps `this`), but QueryInterface and
// CreateGraphicsPipelineState. 128 slots cover ID3D12Device14.
struct Wrap {
    void** vt;
    ID3D12Device* real;
};

static HRESULT STDMETHODCALLTYPE wrap_qi(Wrap* w, REFIID riid, void** pp) {
    HRESULT hr = w->real->QueryInterface(riid, pp);
    if (SUCCEEDED(hr) && *pp == w->real) *pp = w;  // every device interface stays the wrapper, as ReShade's do
    return hr;
}

static HRESULT STDMETHODCALLTYPE wrap_gfx(Wrap* w, const D3D12_GRAPHICS_PIPELINE_STATE_DESC* d, REFIID riid, void** pp) {
    D3D12_GRAPHICS_PIPELINE_STATE_DESC c = *d;
    if (c.PS.pShaderBytecode) c.PS = {g_ps.data(), g_ps.size()};
    return w->real->CreateGraphicsPipelineState(&c, riid, pp);
}

static void** wrapper_vtable() {
    static void** vt = [] {
        const int n = 128;
        auto code = (uint8_t*)VirtualAlloc(nullptr, n * 16, MEM_COMMIT | MEM_RESERVE, PAGE_EXECUTE_READWRITE);
        auto t = new void*[n];
        for (int i = 0; i < n; ++i) {
            uint8_t* c = code + i * 16;
            const uint8_t op[] = {0x48, 0x8B, 0x49, 0x08, 0x48, 0x8B, 0x01, 0xFF, 0xA0};  // mov rcx,[rcx+8]; mov rax,[rcx]; jmp [rax+disp32]
            int32_t disp = i * 8;
            memcpy(c, op, sizeof op), memcpy(c + sizeof op, &disp, 4);
            t[i] = c;
        }
        FlushInstructionCache(GetCurrentProcess(), code, n * 16);
        t[0] = (void*)wrap_qi, t[10] = (void*)wrap_gfx;
        return t;
    }();
    return vt;
}

extern "C" __declspec(dllexport) HRESULT WINAPI D3D12CreateDevice(IUnknown* adapter, D3D_FEATURE_LEVEL fl, REFIID riid, void** pp) {
    static auto real = [] {
        wchar_t sys[MAX_PATH];
        GetSystemDirectoryW(sys, MAX_PATH);
        return (decltype(&D3D12CreateDevice_h))GetProcAddress(LoadLibraryW((std::wstring(sys) + L"\\d3d12.dll").c_str()), "D3D12CreateDevice");
    }();
    InterlockedIncrement(&g_calls);
    if (!real) return E_FAIL;
    if (g_ps.empty() || !pp) return real(adapter, fl, riid, pp);
    ID3D12Device* dev = nullptr;
    HRESULT hr = real(adapter, fl, IID_PPV_ARGS(&dev));
    if (FAILED(hr)) return hr;
    auto w = new Wrap{wrapper_vtable(), dev};  // leaked: a test process
    hr = wrap_qi(w, riid, pp);
    dev->Release();
    return hr;
}
