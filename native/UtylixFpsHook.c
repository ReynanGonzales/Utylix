/*
 * UtylixFpsHook - Utylix's own frame limiter (it does what RivaTuner's limiter does, and nothing else).
 *
 * Utylix puts this DLL into a program that is in its "Memory and CPU limits" list with a frames-per-second limit. Inside the program it
 * swaps the Present function of Direct3D 9 / Direct3D 9Ex / DXGI (Direct3D 10, 11, 12) swap chains for one that first waits until the next frame
 * is due, so the program cannot show more frames per second than the limit. It does not read, change or send anything of the program.
 *
 * The limit comes from a small text file next to this DLL, "limit-<process id>.txt" (a number; missing / 0 = no limit), looked at twice a second,
 * so Utylix changes or lifts a limit without touching the program again. Nothing is written anywhere by this DLL.
 *
 * No C runtime and no import table: everything is found at run time through the module list of the process (so the same source builds for
 * 32-bit and 64-bit programs without the Windows SDK). Build: tools\build-fpshook.bat.
 */
/* (no #include: the Windows SDK is not needed, and <intrin.h> would want its headers; the few compiler intrinsics are declared here) */
#ifdef _WIN64
unsigned __int64 __readgsqword(unsigned long offset);
#pragma intrinsic(__readgsqword)
#else
unsigned long __readfsdword(unsigned long offset);
#pragma intrinsic(__readfsdword)
#endif
void _mm_pause(void);
#pragma intrinsic(_mm_pause)
__int64 _InterlockedCompareExchange64(__int64 volatile *dest, __int64 exchange, __int64 comparand);
#pragma intrinsic(_InterlockedCompareExchange64)

typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef unsigned long long u64;
typedef long long i64;
typedef void *PVOID;
typedef int BOOL;
typedef long HRESULT;
typedef u32 DWORD;
#define WINAPI __stdcall
#define NULL ((void *)0)

#ifdef _WIN64
#define PTR_SIZE 8
typedef u64 uptr;                                                   /* (SIZE_T: as wide as a pointer - 32-bit Windows functions take 4 bytes here, not 8) */
#else
#define PTR_SIZE 4
typedef u32 uptr;
#endif

int _fltused = 0;                                                   /* (the linker wants this wherever floating point is used without the C runtime) */

/* ---------- finding Windows functions without an import table ---------- */
typedef void *(WINAPI *PFN_GetProcAddress)(void *module, const char *name);
typedef void *(WINAPI *PFN_LoadLibraryA)(const char *name);
typedef void *(WINAPI *PFN_GetModuleHandleW)(const u16 *name);
typedef void *(WINAPI *PFN_CreateThread)(void *attr, uptr stack, DWORD(WINAPI *start)(void *), void *param, DWORD flags, DWORD *id);
typedef BOOL(WINAPI *PFN_QPC)(i64 *);
typedef void(WINAPI *PFN_Sleep)(DWORD);
typedef void *(WINAPI *PFN_CreateWaitableTimerExW)(void *attr, const u16 *name, DWORD flags, DWORD access);
typedef BOOL(WINAPI *PFN_SetWaitableTimer)(void *timer, const i64 *due, long period, void *routine, void *arg, BOOL resume);
typedef DWORD(WINAPI *PFN_WaitForSingleObject)(void *h, DWORD ms);
typedef void *(WINAPI *PFN_CreateFileW)(const u16 *name, DWORD access, DWORD share, void *sec, DWORD disp, DWORD flags, void *tmpl);
typedef BOOL(WINAPI *PFN_ReadFile)(void *h, void *buf, DWORD n, DWORD *read, void *ov);
typedef BOOL(WINAPI *PFN_CloseHandle)(void *h);
typedef DWORD(WINAPI *PFN_GetModuleFileNameW)(void *module, u16 *buf, DWORD n);
typedef DWORD(WINAPI *PFN_GetCurrentProcessId)(void);
typedef BOOL(WINAPI *PFN_VirtualProtect)(void *addr, uptr size, DWORD prot, DWORD *old);
typedef void *(WINAPI *PFN_VirtualAlloc)(void *addr, uptr size, DWORD type, DWORD prot);
typedef BOOL(WINAPI *PFN_FlushInstructionCache)(void *process, void *addr, uptr size);
typedef void *(WINAPI *PFN_CreateWindowExA)(DWORD ex, const char *cls, const char *title, DWORD style, int x, int y, int w, int h, void *parent, void *menu, void *inst, void *param);
typedef BOOL(WINAPI *PFN_DestroyWindow)(void *h);

typedef BOOL(WINAPI *PFN_WriteFile)(void *h, const void *buf, DWORD n, DWORD *written, void *ov);
static PFN_WriteFile pWriteFile;

#ifdef UTYLIX_DEBUG                                                 /* (only for testing: a trace next to the limit file) */
static u16 g_logPath[300];
static void dbg(const char *text, u32 value);
#else
#define dbg(text, value) ((void)0)
#endif

static PFN_GetProcAddress pGetProcAddress;
static PFN_LoadLibraryA pLoadLibraryA;
static PFN_GetModuleHandleW pGetModuleHandleW;
static PFN_CreateThread pCreateThread;
static PFN_QPC pQPC, pQPF;
static PFN_Sleep pSleep;
static PFN_CreateWaitableTimerExW pCreateWaitableTimerExW;
static PFN_SetWaitableTimer pSetWaitableTimer;
static PFN_WaitForSingleObject pWaitForSingleObject;
static PFN_CreateFileW pCreateFileW;
static PFN_ReadFile pReadFile;
static PFN_CloseHandle pCloseHandle;
static PFN_GetModuleFileNameW pGetModuleFileNameW;
static PFN_GetCurrentProcessId pGetCurrentProcessId;
static PFN_VirtualProtect pVirtualProtect;
static PFN_VirtualAlloc pVirtualAlloc;
static PFN_FlushInstructionCache pFlushInstructionCache;
static PFN_CreateWindowExA pCreateWindowExA;
static PFN_DestroyWindow pDestroyWindow;

static int ieq_w(const u16 *a, int alen, const char *b)           /* wide (length in bytes) against ASCII, ignoring case */
{
    int n = alen / 2, i;
    for (i = 0; i < n; i++)
    {
        u16 c = a[i]; char d = b[i];
        if (d == 0) return 0;
        if (c >= 'A' && c <= 'Z') c += 32;
        if (d >= 'A' && d <= 'Z') d += 32;
        if (c != (u16)d) return 0;
    }
    return b[n] == 0;
}

static int streq(const char *a, const char *b)
{
    while (*a && *a == *b) { a++; b++; }
    return *a == *b;
}

static u8 *module_by_name(const char *name)                       /* from the loader's own list: no function call needed */
{
#ifdef _WIN64
    u8 *peb = (u8 *)__readgsqword(0x60);
    u8 *head = *(u8 **)(peb + 0x18) + 0x20;
    int dllBase = 0x20, lenAt = 0x48, bufAt = 0x50;
#else
    u8 *peb = (u8 *)__readfsdword(0x30);
    u8 *head = *(u8 **)(peb + 0x0C) + 0x14;
    int dllBase = 0x10, lenAt = 0x24, bufAt = 0x28;
#endif
    u8 *p;
    for (p = *(u8 **)head; p != head; p = *(u8 **)p)
    {
        u16 len = *(u16 *)(p + lenAt);
        const u16 *buf = *(const u16 **)(p + bufAt);
        if (buf && ieq_w(buf, len, name)) return *(u8 **)(p + dllBase);
    }
    return NULL;
}

static void *export_of(u8 *base, const char *name)                /* the export table of a loaded DLL (forwarders followed one step) */
{
    u32 pe = *(u32 *)(base + 0x3C);
    u8 *opt = base + pe + 4 + 20;
    u32 expRva, expSize;
    u8 *exp;
    u32 names, funcs, ords, count, i;
    if (*(u16 *)opt == 0x20B) { expRva = *(u32 *)(opt + 0x70); expSize = *(u32 *)(opt + 0x74); }
    else { expRva = *(u32 *)(opt + 0x60); expSize = *(u32 *)(opt + 0x64); }
    if (!expRva) return NULL;
    exp = base + expRva;
    count = *(u32 *)(exp + 0x18); funcs = *(u32 *)(exp + 0x1C); names = *(u32 *)(exp + 0x20); ords = *(u32 *)(exp + 0x24);
    for (i = 0; i < count; i++)
    {
        const char *n = (const char *)(base + ((u32 *)(base + names))[i]);
        if (streq(n, name))
        {
            u32 rva = ((u32 *)(base + funcs))[((u16 *)(base + ords))[i]];
            if (rva >= expRva && rva < expRva + expSize)           /* "KERNELBASE.Name": look in that DLL */
            {
                char dll[40]; const char *f = (const char *)(base + rva); int k = 0;
                while (f[k] && f[k] != '.' && k < 30) { dll[k] = f[k]; k++; }
                dll[k++] = '.'; dll[k++] = 'd'; dll[k++] = 'l'; dll[k++] = 'l'; dll[k] = 0;
                {
                    u8 *other = module_by_name(dll);
                    const char *sym = f;
                    while (*sym && *sym != '.') sym++;
                    if (*sym == '.') sym++;
                    return other ? export_of(other, sym) : NULL;
                }
            }
            return base + rva;
        }
    }
    return NULL;
}

static int resolve_all(void)
{
    u8 *k = module_by_name("kernel32.dll");
    void *u;
    if (!k) return 0;
    pGetProcAddress = (PFN_GetProcAddress)export_of(k, "GetProcAddress");
    pLoadLibraryA = (PFN_LoadLibraryA)export_of(k, "LoadLibraryA");
    if (!pGetProcAddress || !pLoadLibraryA) return 0;
#define K(var, type, name) var = (type)pGetProcAddress(k, name); if (!var) return 0;
    K(pGetModuleHandleW, PFN_GetModuleHandleW, "GetModuleHandleW")
    K(pCreateThread, PFN_CreateThread, "CreateThread")
    K(pQPC, PFN_QPC, "QueryPerformanceCounter")
    K(pQPF, PFN_QPC, "QueryPerformanceFrequency")
    K(pSleep, PFN_Sleep, "Sleep")
    K(pSetWaitableTimer, PFN_SetWaitableTimer, "SetWaitableTimer")
    K(pWaitForSingleObject, PFN_WaitForSingleObject, "WaitForSingleObject")
    K(pCreateFileW, PFN_CreateFileW, "CreateFileW")
    K(pReadFile, PFN_ReadFile, "ReadFile")
    K(pCloseHandle, PFN_CloseHandle, "CloseHandle")
    K(pWriteFile, PFN_WriteFile, "WriteFile")
    K(pGetModuleFileNameW, PFN_GetModuleFileNameW, "GetModuleFileNameW")
    K(pGetCurrentProcessId, PFN_GetCurrentProcessId, "GetCurrentProcessId")
    K(pVirtualProtect, PFN_VirtualProtect, "VirtualProtect")
    K(pVirtualAlloc, PFN_VirtualAlloc, "VirtualAlloc")
    K(pFlushInstructionCache, PFN_FlushInstructionCache, "FlushInstructionCache")
    pCreateWaitableTimerExW = (PFN_CreateWaitableTimerExW)pGetProcAddress(k, "CreateWaitableTimerExW");   /* (not on very old Windows) */
    u = pLoadLibraryA("user32.dll");
    if (!u) return 0;
    pCreateWindowExA = (PFN_CreateWindowExA)pGetProcAddress(u, "CreateWindowExA");
    pDestroyWindow = (PFN_DestroyWindow)pGetProcAddress(u, "DestroyWindow");
    return pCreateWindowExA && pDestroyWindow;
}

/* ---------- the limit and the waiting ---------- */
static volatile long g_limit;                                      /* frames per second, 0 = none */
static double g_freq;                                              /* timer ticks per second */
static double g_next;                                              /* when the next frame may be shown (ticks) */
static void *g_timer;

static double clock_ticks(void)                                    /* (the 64-bit number is put together by hand: the 32-bit build has no C runtime to convert it) */
{
    i64 t;
    u32 *w = (u32 *)&t;
    pQPC(&t);
    return (double)w[1] * 4294967296.0 + (double)w[0];
}

static volatile long g_frames;                                     /* frames shown so far (counted for the FPS display) */

static void throttle(void)
{
    long lim = g_limit;
    double now, wait, coarse;
    g_frames++;
    if (lim <= 0) { g_next = 0; return; }
    now = clock_ticks();
    {
        double interval = g_freq / (double)lim;
        if (g_next == 0 || now - g_next > interval * 4) g_next = now;           /* just switched on, or it was slow for a while: start again */
        g_next += interval;
        if (now > g_next) { g_next = now; return; }                              /* already late: no waiting */
    }
    wait = g_next - now;
    coarse = g_freq / 500.0;                                                      /* the last 2 ms are spent looking at the clock */
    if (wait > coarse)
    {
        double ms = (wait - coarse) * 1000.0 / g_freq;
        if (g_timer)
        {
            i64 due;                                                              /* 100 ns units, negative = relative */
            u32 *w = (u32 *)&due;
            u32 n = (u32)(int)(ms * 10000.0);
            w[0] = (u32)(0 - n); w[1] = 0xFFFFFFFFu;
            pSetWaitableTimer(g_timer, &due, 0, NULL, NULL, 0);
            pWaitForSingleObject(g_timer, 100);
        }
        else if (ms >= 1.0) pSleep((DWORD)(int)ms);
    }
    do { now = clock_ticks(); _mm_pause(); } while (now < g_next);
}

/* ---------- the hooks: the original Present is called after the wait ---------- */
typedef HRESULT(WINAPI *Present9_t)(void *self, const void *a, const void *b, void *wnd, const void *c);
typedef HRESULT(WINAPI *PresentEx9_t)(void *self, const void *a, const void *b, void *wnd, const void *c, DWORD flags);
typedef HRESULT(WINAPI *PresentDxgi_t)(void *self, u32 sync, u32 flags);
typedef HRESULT(WINAPI *Present1Dxgi_t)(void *self, u32 sync, u32 flags, const void *params);

static Present9_t o_Present9, o_Present9Ex;
static PresentEx9_t o_PresentEx9;
static PresentDxgi_t o_Dxgi;
static Present1Dxgi_t o_Dxgi1;

#define VT(obj) (*(void ***)(obj))
#define CALL(obj, idx, type) ((type)VT(obj)[idx])

/* which graphics API the program draws with, for the on-screen counter ("D3D11"): 9 for Direct3D 9, for DXGI it is asked once from the swap chain's device */
static volatile int g_api;                                         /* 0 unknown, 9, 10, 11, 12 */

static void detect_dxgi_api(void *swap)
{
    typedef HRESULT(WINAPI * GetDevice_t)(void *, const void *, void **);
    typedef u32(WINAPI * Release_t)(void *);
    static const u8 iid12[16] = { 0xf1, 0x19, 0x98, 0x18, 0xb6, 0x1d, 0x57, 0x4b, 0xbe, 0x54, 0x18, 0x21, 0x33, 0x9b, 0x85, 0xf7 };     /* ID3D12Device */
    static const u8 iid11[16] = { 0xdb, 0x6d, 0x6f, 0xdb, 0x77, 0xac, 0x88, 0x4e, 0x82, 0x53, 0x81, 0x9d, 0xf9, 0xbb, 0xf1, 0x40 };     /* ID3D11Device */
    void *dev = NULL;
    if (g_api) return;
    if (CALL(swap, 7, GetDevice_t)(swap, iid12, &dev) >= 0 && dev) { g_api = 12; CALL(dev, 2, Release_t)(dev); return; }
    dev = NULL;
    if (CALL(swap, 7, GetDevice_t)(swap, iid11, &dev) >= 0 && dev) { g_api = 11; CALL(dev, 2, Release_t)(dev); return; }
    g_api = 10;
}

static HRESULT WINAPI h_Present9(void *self, const void *a, const void *b, void *w, const void *c) { static int seen; if (!seen) { seen = 1; g_api = 9; dbg("called Present9", 0); } throttle(); return o_Present9(self, a, b, w, c); }
static HRESULT WINAPI h_Present9Ex(void *self, const void *a, const void *b, void *w, const void *c) { static int seen; if (!seen) { seen = 1; g_api = 9; dbg("called Present9Ex", 0); } throttle(); return o_Present9Ex(self, a, b, w, c); }
static HRESULT WINAPI h_PresentEx9(void *self, const void *a, const void *b, void *w, const void *c, DWORD f) { static int seen; if (!seen) { seen = 1; g_api = 9; dbg("called PresentEx9", 0); } throttle(); return o_PresentEx9(self, a, b, w, c, f); }
static HRESULT WINAPI h_Dxgi(void *self, u32 sync, u32 flags) { static int seen; if (!seen) { seen = 1; detect_dxgi_api(self); dbg("called Dxgi Present", 0); } throttle(); return o_Dxgi(self, sync, flags); }
static HRESULT WINAPI h_Dxgi1(void *self, u32 sync, u32 flags, const void *p) { static int seen; if (!seen) { seen = 1; detect_dxgi_api(self); } throttle(); return o_Dxgi1(self, sync, flags, p); }

/* ---------- putting a hook on a function ----------
 * The function's first instructions (5 bytes or a little more, whole instructions) are copied to a small stub with a jump back behind them;
 * the function itself then starts with a jump to our hook. That changes every caller at once, whatever table it takes the function from
 * (Direct3D 9 gives each device its own copy of the table, so changing a table entry is not enough). When the first instructions are
 * ones this small reader does not know, nothing is touched and the entry of the table is changed instead (what works for DXGI). */
static int modrm_len(const u8 *p)                                  /* ModRM + SIB + displacement; -1 = relative to the instruction pointer (cannot be copied) */
{
    u8 m = p[0];
    int mod = m >> 6, rm = m & 7, n = 1;
    if (mod == 3) return n;
    if (rm == 4) { u8 sib = p[1]; n++; if (mod == 0 && (sib & 7) == 5) n += 4; }
    if (mod == 0 && rm == 5)
    {
#ifdef _WIN64
        return -1;
#else
        n += 4;
#endif
    }
    if (mod == 1) n += 1; else if (mod == 2) n += 4;
    return n;
}

static int ilen(const u8 *p)                                       /* length of the instruction at p, 0 = not one of the ordinary ones of a function's start */
{
    int i = 0, op16 = 0, rexw = 0, m;
    u8 op;
    for (;;)
    {
        u8 b = p[i];
        if (b == 0x66) { op16 = 1; i++; }
        else if (b == 0xF2 || b == 0xF3 || b == 0x64 || b == 0x65 || b == 0x2E || b == 0x3E) i++;
        else break;
    }
#ifdef _WIN64
    if ((p[i] & 0xF0) == 0x40) { rexw = (p[i] & 8) != 0; i++; }
#endif
    op = p[i++];
    if ((op >= 0x50 && op <= 0x5F) || op == 0x90 || op == 0xC9 || op == 0xCC || op == 0x9C || op == 0x9D) return i;
#ifndef _WIN64
    if (op >= 0x40 && op <= 0x4F) return i;
#endif
    if (op == 0x6A || (op >= 0xB0 && op <= 0xB7) || op == 0xA8 || (op < 0x40 && (op & 7) == 4)) return i + 1;
    if (op == 0x68 || (op < 0x40 && (op & 7) == 5) || op == 0xA9) return i + (op16 ? 2 : 4);
    if (op >= 0xB8 && op <= 0xBF) return i + (rexw ? 8 : (op16 ? 2 : 4));
    if ((op < 0x40 && (op & 7) <= 3) || op == 0x63 || (op >= 0x84 && op <= 0x8B) || op == 0x8D || op == 0xFF)
        return (m = modrm_len(p + i)) > 0 ? i + m : 0;
    if (op == 0x80 || op == 0x83 || op == 0xC0 || op == 0xC1 || op == 0xC6 || op == 0x6B)
        return (m = modrm_len(p + i)) > 0 ? i + m + 1 : 0;
    if (op == 0x81 || op == 0xC7 || op == 0x69)
        return (m = modrm_len(p + i)) > 0 ? i + m + (op16 ? 2 : 4) : 0;
    if (op == 0x0F)
    {
        u8 o2 = p[i++];
        if ((o2 >= 0x10 && o2 <= 0x17) || (o2 >= 0x28 && o2 <= 0x2D) || (o2 >= 0x40 && o2 <= 0x4F) || (o2 >= 0x51 && o2 <= 0x5F) || (o2 >= 0x90 && o2 <= 0x9F)
            || o2 == 0x1F || o2 == 0x6E || o2 == 0x6F || o2 == 0x7E || o2 == 0x7F || o2 == 0xAF || o2 == 0xB6 || o2 == 0xB7 || o2 == 0xBE || o2 == 0xBF || o2 == 0xEF || o2 == 0xD6)
            return (m = modrm_len(p + i)) > 0 ? i + m : 0;
    }
    return 0;
}

static void *g_hooked[16];
static int g_hookedCount;

static u8 *alloc_stub(u8 *near_to)                                 /* a page of its own, within reach of a 32-bit jump from the function (64-bit) */
{
#ifdef _WIN64
    u64 base = (u64)near_to & ~0xFFFFull, i;
    for (i = 1; i < 0x7000; i++)
    {
        void *p = pVirtualAlloc((void *)(base - i * 0x10000), 0x1000, 0x3000, 0x40);
        if (p) return (u8 *)p;
        p = pVirtualAlloc((void *)(base + i * 0x10000), 0x1000, 0x3000, 0x40);
        if (p) return (u8 *)p;
    }
    return NULL;
#else
    (void)near_to;
    return (u8 *)pVirtualAlloc(NULL, 0x1000, 0x3000, 0x40);
#endif
}

static void put_jump(u8 *at, void *to)                             /* a jump anywhere from a stub */
{
#ifdef _WIN64
    int i;
    at[0] = 0xFF; at[1] = 0x25; at[2] = at[3] = at[4] = at[5] = 0;
    for (i = 0; i < 8; i++) at[6 + i] = (u8)(((u64)to) >> (8 * i));
#else
    u32 rel = (u32)((u8 *)to - (at + 5));
    int i;
    at[0] = 0xE9;
    for (i = 0; i < 4; i++) at[1 + i] = (u8)(rel >> (8 * i));
#endif
}

static int inline_hook(void *target, void *hook, void **original)
{
    u8 *t = (u8 *)target, *stub, *jumpToHook;
    int len = 0, n, i;
    DWORD old;
    u8 patch[8];
    __int64 was, now;
    for (i = 0; i < g_hookedCount; i++) if (g_hooked[i] == target) return 1;       /* (two table entries that are one function) */
    if (t[0] == 0xE9 || t[0] == 0xEB || (t[0] == 0xFF && t[1] == 0x25)) { dbg("already jumps (someone else's hook?)", t[0]); return 0; }
    while (len < 5)
    {
        n = ilen(t + len);
        if (n <= 0) { dbg("unknown instruction at", (u32)len); dbg("  first bytes", *(u32 *)t); return 0; }
        len += n;
    }
    if (len > 8) { dbg("too long to patch at once", (u32)len); return 0; }
    stub = alloc_stub(t);
    if (!stub) { dbg("no memory near the function", 0); return 0; }
    for (i = 0; i < len; i++) ((volatile u8 *)stub)[i] = t[i];
    {
        u8 *back = stub + len;
#ifdef _WIN64
        put_jump(back, t + len);
#else
        put_jump(back, t + len);
#endif
    }
    jumpToHook = stub + 32;
    put_jump(jumpToHook, hook);
    for (i = 0; i < 8; i++) ((volatile u8 *)patch)[i] = t[i];
    {
        i64 rel = (i64)(jumpToHook - (t + 5));
        patch[0] = 0xE9;
        for (i = 0; i < 4; i++) patch[1 + i] = (u8)(((u32)rel) >> (8 * i));
        for (i = 5; i < len; i++) ((volatile u8 *)patch)[i] = 0xCC;
    }
    if (!pVirtualProtect(t, 16, 0x40, &old)) return 0;
    was = *(volatile __int64 *)t;
    now = was;
    for (i = 0; i < 8; i++) ((volatile u8 *)&now)[i] = patch[i];
    _InterlockedCompareExchange64((volatile __int64 *)t, now, was);
    pVirtualProtect(t, 16, old, &old);
    pFlushInstructionCache((void *)(u64)-1, t, 16);
    *original = stub;
    if (g_hookedCount < 16) g_hooked[g_hookedCount++] = target;
    return 1;
}

static int patch_slot(void **vtable, int index, void *hook, void **original)
{
    void **slot = &vtable[index];
    DWORD old;
    if (*slot == hook) return 1;
    if (inline_hook(*slot, hook, original)) return 1;
    dbg("falling back to the table entry", (u32)index);
    if (!pVirtualProtect(slot, PTR_SIZE, 0x04, &old)) return 0;                   /* PAGE_READWRITE */
    *original = *slot;
    *slot = hook;
    pVirtualProtect(slot, PTR_SIZE, old, &old);
    return 1;
}


static void *g_wnd;
static void *make_window(void) { return g_wnd ? g_wnd : (g_wnd = pCreateWindowExA(0, "STATIC", "", (DWORD)0x80000000, 0, 0, 2, 2, NULL, NULL, NULL, NULL)); }

typedef struct
{
    u32 BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount, MultiSampleType, MultiSampleQuality, SwapEffect;
    void *hDeviceWindow;
    BOOL Windowed, EnableAutoDepthStencil;
    u32 AutoDepthStencilFormat, Flags, FullScreen_RefreshRateInHz, PresentationInterval;
} D3DPP;

static void zero(void *p, int n) { volatile u8 *b = (volatile u8 *)p; while (n-- > 0) *b++ = 0; }       /* (volatile: not turned into a memset call) */

static int hook_d3d9(void)
{
    typedef void *(WINAPI * Create9_t)(u32);
    typedef HRESULT(WINAPI * Create9Ex_t)(u32, void **);
    typedef HRESULT(WINAPI * CreateDevice_t)(void *, u32, u32, void *, DWORD, D3DPP *, void **);
    typedef HRESULT(WINAPI * CreateDeviceEx_t)(void *, u32, u32, void *, DWORD, D3DPP *, void *, void **);
    typedef u32(WINAPI * Release_t)(void *);
    void *mod = pGetModuleHandleW(L"d3d9.dll");
    int ok = 0;
    D3DPP pp;
    void *wnd;
    dbg("hook_d3d9 start module", (u32)(u64)mod);
    if (!mod || !(wnd = make_window())) return 0;
    dbg("hook_d3d9 window", (u32)(u64)wnd);
    zero(&pp, sizeof pp);
    pp.BackBufferWidth = 2; pp.BackBufferHeight = 2; pp.BackBufferCount = 1; pp.SwapEffect = 1; pp.hDeviceWindow = wnd; pp.Windowed = 1;

    {   /* a plain device */
        Create9_t create = (Create9_t)pGetProcAddress(mod, "Direct3DCreate9");
        void *d3d = create ? create(32) : NULL;
        if (d3d)
        {
            void *dev = NULL;
            HRESULT hr = CALL(d3d, 16, CreateDevice_t)(d3d, 0, 1, wnd, 0x2 | 0x20 | 0x800, &pp, &dev);
            dbg("d3d9 CreateDevice", (u32)hr);
            if (hr >= 0 && dev)
            {
                dbg("d3d9 dev vtable", (u32)(u64)VT(dev));
                dbg("d3d9 vtable[17]", (u32)(u64)VT(dev)[17]);
                ok |= patch_slot(VT(dev), 17, (void *)h_Present9, (void **)&o_Present9);
                CALL(dev, 2, Release_t)(dev);
            }
            CALL(d3d, 2, Release_t)(d3d);
        }
    }
    {   /* an Ex device (what WPF and many newer programs use) */
        Create9Ex_t create = (Create9Ex_t)pGetProcAddress(mod, "Direct3DCreate9Ex");
        void *d3d = NULL;
        HRESULT hr0 = create ? create(32, &d3d) : -1;
        dbg("d3d9ex Create", (u32)hr0);
        if (hr0 >= 0 && d3d)
        {
            void *dev = NULL;
            HRESULT hr = CALL(d3d, 20, CreateDeviceEx_t)(d3d, 0, 1, wnd, 0x2 | 0x20 | 0x800, &pp, NULL, &dev);
            dbg("d3d9ex CreateDeviceEx", (u32)hr);
            if (hr >= 0 && dev)
            {
                ok |= patch_slot(VT(dev), 17, (void *)h_Present9Ex, (void **)&o_Present9Ex);
                ok |= patch_slot(VT(dev), 121, (void *)h_PresentEx9, (void **)&o_PresentEx9);
                CALL(dev, 2, Release_t)(dev);
            }
            CALL(d3d, 2, Release_t)(d3d);
        }
    }
    return ok;
}

typedef struct
{
    u32 Width, Height, RefreshNum, RefreshDen, Format, ScanlineOrdering, Scaling;     /* DXGI_MODE_DESC */
    u32 SampleCount, SampleQuality;
    u32 BufferUsage, BufferCount;
    void *OutputWindow;
    BOOL Windowed;
    u32 SwapEffect, Flags;
} SWAPDESC;

static int hook_dxgi(void)
{
    typedef HRESULT(WINAPI * CreateDS_t)(void *adapter, u32 type, void *sw, u32 flags, const u32 *levels, u32 nlevels, u32 sdk, SWAPDESC *desc, void **swap, void **dev, u32 *level, void **ctx);
    typedef u32(WINAPI * Release_t)(void *);
    typedef HRESULT(WINAPI * QI_t)(void *, const void *, void **);
    static const u8 iidSwapChain1[16] = { 0xf7, 0x45, 0x0a, 0x79, 0x42, 0x0d, 0x76, 0x48, 0x98, 0x3a, 0x0a, 0x55, 0xcf, 0xe6, 0xf4, 0xaa };
    void *mod = pGetModuleHandleW(L"d3d11.dll");
    void *dxgi = pGetModuleHandleW(L"dxgi.dll");
    CreateDS_t create;
    SWAPDESC d;
    void *swap = NULL, *dev = NULL, *wnd;
    int ok = 0;
    if (!dxgi) return 0;
    if (!mod) mod = pLoadLibraryA("d3d11.dll");                                       /* (the programs that use d3d12 only) */
    create = mod ? (CreateDS_t)pGetProcAddress(mod, "D3D11CreateDeviceAndSwapChain") : NULL;
    wnd = make_window();
    if (!create || !wnd) return 0;
    zero(&d, sizeof d);
    d.Width = 2; d.Height = 2; d.Format = 28; d.SampleCount = 1; d.BufferUsage = 0x20; d.BufferCount = 1; d.OutputWindow = wnd; d.Windowed = 1;
    {
        HRESULT hr = create(NULL, 1, NULL, 0, NULL, 0, 7, &d, &swap, &dev, NULL, NULL);
        dbg("dxgi create swapchain", (u32)hr);
        if (hr < 0 || !swap) return 0;
    }
    ok |= patch_slot(VT(swap), 8, (void *)h_Dxgi, (void **)&o_Dxgi);
    {
        void *sc1 = NULL;
        if (CALL(swap, 0, QI_t)(swap, iidSwapChain1, &sc1) >= 0 && sc1)
        {
            ok |= patch_slot(VT(sc1), 8, (void *)h_Dxgi, (void **)&o_Dxgi);
            ok |= patch_slot(VT(sc1), 22, (void *)h_Dxgi1, (void **)&o_Dxgi1);
            CALL(sc1, 2, Release_t)(sc1);
        }
    }
    CALL(swap, 2, Release_t)(swap);
    if (dev) CALL(dev, 2, Release_t)(dev);
    return ok;
}

/* ---------- the limit file ---------- */
static u16 g_path[300];                                            /* limit-<pid>.txt: what Utylix wants (read) */
static u16 g_fpsPath[300];                                         /* fps-<pid>.txt: what the program really shows (written once a second) */

static int put_name(u16 *to, int at, const u16 *dir, int dirLen, const char *prefix)
{
    int i, k = 0, n = 0;
    u32 pid = pGetCurrentProcessId();
    char digits[12];
    static const char suffix[] = ".txt";
    for (i = 0; i < dirLen; i++) ((volatile u16 *)to)[n++] = dir[i];
    for (i = 0; prefix[i]; i++) to[n++] = (u16)prefix[i];
    if (pid == 0) digits[k++] = '0';
    while (pid) { digits[k++] = (char)('0' + pid % 10); pid /= 10; }
    while (k) to[n++] = (u16)digits[--k];
    for (i = 0; suffix[i]; i++) to[n++] = (u16)suffix[i];
    to[n] = 0;
    (void)at;
    return n;
}

static void make_path(void *self)
{
    u16 dir[300];
    int n = (int)pGetModuleFileNameW(self, dir, 260), i;
    while (n > 0 && dir[n - 1] != '\\') n--;
    i = put_name(g_path, 0, dir, n, "limit-");
    put_name(g_fpsPath, 0, dir, n, "fps-");
    n = i;
#ifdef UTYLIX_DEBUG
    for (i = 0; i <= n; i++) ((volatile u16 *)g_logPath)[i] = g_path[i];     /* (volatile: not turned into a memcpy call) */
    g_logPath[n - 3] = 'l'; g_logPath[n - 2] = 'o'; g_logPath[n - 1] = 'g';
#endif
}

#ifdef UTYLIX_DEBUG
static void dbg(const char *text, u32 value)
{
    void *h = pCreateFileW(g_logPath, 0x4 /* FILE_APPEND_DATA */, 7, NULL, 4 /* OPEN_ALWAYS */, 0x80, NULL);
    char line[100]; int n = 0, i; DWORD w;
    if (h == (void *)(u64)-1 || !h) return;
    while (text[n] && n < 70) { line[n] = text[n]; n++; }
    line[n++] = ' '; line[n++] = '0'; line[n++] = 'x';
    for (i = 28; i >= 0; i -= 4) line[n++] = "0123456789abcdef"[(value >> i) & 15];
    line[n++] = '\r'; line[n++] = '\n';
    pWriteFile(h, line, (DWORD)n, &w, NULL);
    pCloseHandle(h);
}
#endif

static void read_limit(void)
{
    void *h = pCreateFileW(g_path, 0x80000000u, 7, NULL, 3, 0x80, NULL);
    long v = 0;
    if (h != (void *)(u64)-1 && h)
    {
        char buf[16]; DWORD got = 0; DWORD i;
        if (pReadFile(h, buf, 15, &got, NULL))
            for (i = 0; i < got; i++) { if (buf[i] >= '0' && buf[i] <= '9') v = v * 10 + (buf[i] - '0'); else break; }
        pCloseHandle(h);
    }
    if (v > 1000) v = 1000;
    g_limit = v;
}

/* the frames per second of the last second, with one decimal, as text ("59.9"): for the on-screen counter */
static void write_fps(double fps)
{
    void *h;
    char text[24];
    int n = 0, whole, tenth;
    DWORD w;
    if (fps < 0) fps = 0;
    if (fps > 99999) fps = 99999;
    tenth = (int)(fps * 10.0 + 0.5);
    whole = tenth / 10; tenth %= 10;
    {
        char digits[8]; int k = 0;
        if (whole == 0) digits[k++] = '0';
        while (whole) { digits[k++] = (char)('0' + whole % 10); whole /= 10; }
        while (k) text[n++] = digits[--k];
    }
    text[n++] = '.'; text[n++] = (char)('0' + tenth);
    if (g_api)                                                                         /* " D3D11" after the number */
    {
        text[n++] = ' '; text[n++] = 'D'; text[n++] = '3'; text[n++] = 'D';
        if (g_api >= 10) { text[n++] = '1'; text[n++] = (char)('0' + g_api - 10); } else text[n++] = '9';
    }
    h = pCreateFileW(g_fpsPath, 0x40000000u, 7, NULL, 2 /* CREATE_ALWAYS */, 0x80, NULL);
    if (h == (void *)(u64)-1 || !h) return;
    pWriteFile(h, text, (DWORD)n, &w, NULL);
    pCloseHandle(h);
}

static DWORD WINAPI worker(void *self)
{
    int d3d9done = 0, dxgidone = 0, tries;
    i64 f;
    long lastFrames = 0;
    double lastTime = 0;
    if (!resolve_all()) return 0;
    pQPF(&f); g_freq = (double)((u32 *)&f)[1] * 4294967296.0 + (double)((u32 *)&f)[0];
    g_timer = pCreateWaitableTimerExW ? pCreateWaitableTimerExW(NULL, NULL, 0x2 /* high resolution */, 0x1F0003) : NULL;
    make_path(self);
    dbg("worker started, timer", (u32)(u64)g_timer);
    lastTime = clock_ticks();
    for (tries = 0;; tries++)
    {
        read_limit();
        if (tries % 2 == 1)                                                            /* once a second: how many frames since last time */
        {
            double now = clock_ticks();
            long frames = g_frames;
            if (now > lastTime) write_fps((double)(frames - lastFrames) * g_freq / (now - lastTime));
            lastFrames = frames; lastTime = now;
        }
        if (tries < 240 && tries % 4 == 0 && (!d3d9done || !dxgidone))                 /* (every two seconds for two minutes: a game loads its graphics late) */
        {
            if (!d3d9done && pGetModuleHandleW(L"d3d9.dll") && hook_d3d9()) d3d9done = 1;
            if (!dxgidone && pGetModuleHandleW(L"dxgi.dll") && hook_dxgi()) dxgidone = 1;
        }
        pSleep(500);
    }
}

BOOL WINAPI DllMain(void *module, DWORD reason, void *reserved)
{
    (void)reserved;
    if (reason == 1)                                                                   /* DLL_PROCESS_ATTACH: do the work on a thread of our own */
    {
        u8 *k = module_by_name("kernel32.dll");
        PFN_CreateThread ct = k ? (PFN_CreateThread)export_of(k, "CreateThread") : NULL;
        if (ct) ct(NULL, 0, worker, module, 0, NULL);
    }
    return 1;
}
