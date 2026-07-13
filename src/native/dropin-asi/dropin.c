/*
 * reloaded-dropin.asi — native bootstrap for Reloaded Drop-In.
 *
 * Loaded into the game process by Ultimate ASI Loader (deployed as the proxy
 * DLL, e.g. winmm.dll/dinput8.dll). Replaces Reloaded-II's stock bootstrapper:
 *
 *   1. Creates mods/ and reloaded-dropin/logs/ next to itself on first run.
 *   2. Hosts CoreCLR from the bundled runtime (reloaded-dropin/runtime).
 *   3. Runs the managed sync (ReloadedDropIn.Bootstrap) which scans mods/ and
 *      writes ReloadedII.json + AppConfig.json BEFORE the loader starts.
 *   4. Chain-loads Reloaded.Mod.Loader.dll via the same EntryPoint contract as
 *      the stock C++ bootstrapper (see vendor/.../Reloaded.Mod.Loader.Bootstrapper).
 *   5. Exposes Ultimate ASI Loader's InitializeASI callback so the game thread
 *      cannot enter native file-system initialization while steps 1-4 are still
 *      running on the bootstrap thread.
 *
 * Every failure path logs to reloaded-dropin/logs/bootstrap.log and lets the
 * game continue vanilla — this DLL must never take the game down with it.
 */

#include <windows.h>
#include <stdio.h>
#include <stdarg.h>

/* ------------------------------------------------------------------ */
/* hostfxr ABI (stable, documented in dotnet/runtime native hosting)   */
/* ------------------------------------------------------------------ */

#define HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER 5

typedef int(__cdecl *hostfxr_initialize_for_runtime_config_fn)(
    const wchar_t *runtime_config_path, const void *parameters, void **host_context_handle);
typedef int(__cdecl *hostfxr_get_runtime_delegate_fn)(
    void *host_context_handle, int type, void **delegate);
typedef int(__cdecl *hostfxr_close_fn)(void *host_context_handle);
typedef void(__cdecl *hostfxr_error_writer_fn)(const wchar_t *message);
typedef hostfxr_error_writer_fn(__cdecl *hostfxr_set_error_writer_fn)(
    hostfxr_error_writer_fn error_writer);

typedef int(__stdcall *load_assembly_and_get_function_pointer_fn)(
    const wchar_t *assembly_path, const wchar_t *type_name, const wchar_t *method_name,
    const wchar_t *delegate_type_name, void *reserved, void **delegate);

/* Default component delegate: static int Method(IntPtr args, int sizeBytes) */
typedef int(__stdcall *component_entry_point_fn)(void *arg, int arg_size_in_bytes);

/* ------------------------------------------------------------------ */
/* Reloaded loader EntryPoint contract (EntryPointParameter.h, V8)     */
/* ------------------------------------------------------------------ */

#define RELOADED_ENTRY_POINT_VERSION 8
#define RELOADED_FLAG_LOADED_EXTERNALLY 1

typedef struct EntryPointParameters
{
    int version;
    int flags;
    wchar_t *dll_path;
} EntryPointParameters;

/* ------------------------------------------------------------------ */
/* Globals                                                             */
/* ------------------------------------------------------------------ */

static HMODULE g_module;
static wchar_t g_module_path[MAX_PATH];
static wchar_t g_game_dir[MAX_PATH];
static FILE *g_log;

/* Entry-point hold state (see hold_game_at_entry). */
static BYTE *g_entry_address;
static BYTE g_entry_original[2];
static int g_entry_patched;
static HANDLE g_bootstrap_thread;

static void log_line(const char *format, ...)
{
    if (!g_log)
        return;

    SYSTEMTIME time;
    GetLocalTime(&time);
    fprintf(g_log, "[%02d:%02d:%02d.%03d] ", time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);

    va_list args;
    va_start(args, format);
    vfprintf(g_log, format, args);
    va_end(args);

    fprintf(g_log, "\n");
    fflush(g_log);
}

static void __cdecl hostfxr_error_to_log(const wchar_t *message)
{
    log_line("hostfxr: %ls", message);
}

/* ------------------------------------------------------------------ */
/* Small path helpers (no shlwapi dependency)                          */
/* ------------------------------------------------------------------ */

static void path_combine(wchar_t *destination, const wchar_t *base, const wchar_t *relative)
{
    swprintf(destination, MAX_PATH, L"%ls\\%ls", base, relative);
}

static int make_directory(const wchar_t *path)
{
    return CreateDirectoryW(path, NULL) || GetLastError() == ERROR_ALREADY_EXISTS;
}

static int file_exists(const wchar_t *path)
{
    DWORD attributes = GetFileAttributesW(path);
    return attributes != INVALID_FILE_ATTRIBUTES;
}

/* Finds reloaded-dropin\runtime\host\fxr\<version>\hostfxr.dll */
static int find_hostfxr(wchar_t *hostfxr_path)
{
    wchar_t fxr_root[MAX_PATH];
    path_combine(fxr_root, g_game_dir, L"reloaded-dropin\\runtime\\host\\fxr");

    wchar_t pattern[MAX_PATH];
    path_combine(pattern, fxr_root, L"*");

    WIN32_FIND_DATAW entry;
    HANDLE find = FindFirstFileW(pattern, &entry);
    if (find == INVALID_HANDLE_VALUE)
        return 0;

    int found = 0;
    do
    {
        if (!(entry.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY))
            continue;
        if (entry.cFileName[0] == L'.')
            continue;

        swprintf(hostfxr_path, MAX_PATH, L"%ls\\%ls\\hostfxr.dll", fxr_root, entry.cFileName);
        if (file_exists(hostfxr_path))
        {
            found = 1;
            break;
        }
    } while (FindNextFileW(find, &entry));

    FindClose(find);
    return found;
}

/* Same guard as the stock bootstrapper: loader publishes a mapped file per PID. */
static int is_reloaded_already_loaded(void)
{
    wchar_t name[128];
    swprintf(name, 128, L"Reloaded-Mod-Loader-Server-PID-%lu", GetCurrentProcessId());
    HANDLE mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name);
    if (mapping)
    {
        CloseHandle(mapping);
        return 1;
    }
    return 0;
}

/* ------------------------------------------------------------------ */
/* Entry-point hold                                                    */
/*                                                                     */
/* We are loaded during process initialization, before the exe's entry */
/* point runs. Mod configuration and file-redirect hooks must be live  */
/* BEFORE the game reads its archive index, or mods silently no-op.    */
/* So: patch the entry point into a 2-byte spin (EB FE), bootstrap on  */
/* our thread, then restore the original bytes. If we're somehow       */
/* loaded late (entry already ran), the patch is inert and harmless.   */
/* ------------------------------------------------------------------ */

static void hold_game_at_entry(void)
{
    BYTE *base = (BYTE *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *dos = (IMAGE_DOS_HEADER *)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
        return;

    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.AddressOfEntryPoint == 0)
        return;

    BYTE *entry = base + nt->OptionalHeader.AddressOfEntryPoint;
    DWORD old_protect;
    if (!VirtualProtect(entry, 2, PAGE_EXECUTE_READWRITE, &old_protect))
        return;

    g_entry_address = entry;
    g_entry_original[0] = entry[0];
    g_entry_original[1] = entry[1];
    entry[0] = 0xEB; /* jmp -2: spin in place */
    entry[1] = 0xFE;
    VirtualProtect(entry, 2, old_protect, &old_protect);
    FlushInstructionCache(GetCurrentProcess(), entry, 2);
    g_entry_patched = 1;
}

static void release_game_entry(void)
{
    if (!g_entry_patched)
        return;

    DWORD old_protect;
    if (VirtualProtect(g_entry_address, 2, PAGE_EXECUTE_READWRITE, &old_protect))
    {
        g_entry_address[0] = g_entry_original[0];
        g_entry_address[1] = g_entry_original[1];
        VirtualProtect(g_entry_address, 2, old_protect, &old_protect);
        FlushInstructionCache(GetCurrentProcess(), g_entry_address, 2);
    }

    g_entry_patched = 0;
    log_line("released game entry point");
}

/* ------------------------------------------------------------------ */
/* Bootstrap                                                           */
/* ------------------------------------------------------------------ */

static void bootstrap(void);

static DWORD WINAPI bootstrap_thread(LPVOID parameter)
{
    (void)parameter;
    bootstrap();
    /* The game must ALWAYS resume, whether we succeeded or bailed out. */
    release_game_entry();
    if (g_log)
        fflush(g_log);
    return 0;
}

/* Ultimate ASI Loader calls this export immediately after LoadLibrary returns,
   outside this DLL's DllMain and therefore outside the Windows loader lock.
   Waiting here holds the game startup thread at a safe point while the worker
   installs Reloaded's hooks. P5R otherwise reaches CRI initialization during
   the same two-second window, before CriFs knows how many mod files to reserve. */
__declspec(dllexport) void InitializeASI(void)
{
    HANDLE thread = g_bootstrap_thread;
    if (!thread)
        return;

    WaitForSingleObject(thread, INFINITE);
    CloseHandle(thread);
    g_bootstrap_thread = NULL;
    log_line("Ultimate ASI Loader synchronization complete");
}

static void bootstrap(void)
{

    /* Resolve where we are; the .asi sits in the game root. */
    if (!GetModuleFileNameW(g_module, g_module_path, MAX_PATH))
        return;

    lstrcpyW(g_game_dir, g_module_path);
    wchar_t *last_slash = wcsrchr(g_game_dir, L'\\');
    if (!last_slash)
        return;
    *last_slash = L'\0';

    /* First-run directory layout + logging. */
    wchar_t path[MAX_PATH];
    path_combine(path, g_game_dir, L"mods");
    make_directory(path);
    path_combine(path, g_game_dir, L"reloaded-dropin");
    make_directory(path);
    path_combine(path, g_game_dir, L"reloaded-dropin\\logs");
    make_directory(path);

    path_combine(path, g_game_dir, L"reloaded-dropin\\logs\\bootstrap.log");
    g_log = _wfopen(path, L"w");

    log_line("reloaded-dropin bootstrap starting");
    log_line("module: %ls", g_module_path);
    log_line("game dir: %ls", g_game_dir);
    log_line("entry-point hold: %s", g_entry_patched ? "active" : "not installed");

    if (is_reloaded_already_loaded())
    {
        log_line("Reloaded is already loaded in this process; nothing to do");
        return;
    }

    /* Host the bundled .NET runtime. */
    wchar_t hostfxr_path[MAX_PATH];
    if (!find_hostfxr(hostfxr_path))
    {
        log_line("ERROR: hostfxr.dll not found under reloaded-dropin\\runtime\\host\\fxr; is the runtime bundled?");
        return;
    }
    log_line("hostfxr: %ls", hostfxr_path);

    HMODULE hostfxr = LoadLibraryW(hostfxr_path);
    if (!hostfxr)
    {
        log_line("ERROR: LoadLibrary(hostfxr) failed: %lu", GetLastError());
        return;
    }

    hostfxr_initialize_for_runtime_config_fn initialize =
        (hostfxr_initialize_for_runtime_config_fn)(void *)GetProcAddress(hostfxr, "hostfxr_initialize_for_runtime_config");
    hostfxr_get_runtime_delegate_fn get_delegate =
        (hostfxr_get_runtime_delegate_fn)(void *)GetProcAddress(hostfxr, "hostfxr_get_runtime_delegate");
    hostfxr_close_fn close_host =
        (hostfxr_close_fn)(void *)GetProcAddress(hostfxr, "hostfxr_close");
    hostfxr_set_error_writer_fn set_error_writer =
        (hostfxr_set_error_writer_fn)(void *)GetProcAddress(hostfxr, "hostfxr_set_error_writer");

    if (!initialize || !get_delegate || !close_host)
    {
        log_line("ERROR: hostfxr exports missing");
        return;
    }
    if (set_error_writer)
        set_error_writer(hostfxr_error_to_log);

    /* Initialize against the Reloaded loader's runtimeconfig, exactly like stock. */
    wchar_t runtime_config[MAX_PATH];
    path_combine(runtime_config, g_game_dir, L"reloaded-dropin\\loader\\Reloaded.Mod.Loader.runtimeconfig.json");
    if (!file_exists(runtime_config))
    {
        log_line("ERROR: missing %ls", runtime_config);
        return;
    }

    void *host_context = NULL;
    int rc = initialize(runtime_config, NULL, &host_context);
    /* 0 = success, 1..3 = success with different fx resolution; negative = failure. */
    if (rc < 0 || rc > 3 || !host_context)
    {
        log_line("ERROR: hostfxr_initialize_for_runtime_config failed: 0x%08x", rc);
        return;
    }

    load_assembly_and_get_function_pointer_fn load_assembly = NULL;
    rc = get_delegate(host_context, HDT_LOAD_ASSEMBLY_AND_GET_FUNCTION_POINTER, (void **)&load_assembly);
    if (rc != 0 || !load_assembly)
    {
        log_line("ERROR: hostfxr_get_runtime_delegate failed: 0x%08x", rc);
        close_host(host_context);
        return;
    }
    log_line("CoreCLR hosted");

    /* Step 1: run our managed sync — mods/ scan + config generation. */
    wchar_t sync_assembly[MAX_PATH];
    path_combine(sync_assembly, g_game_dir, L"reloaded-dropin\\bootstrap\\ReloadedDropIn.Bootstrap.dll");
    if (!file_exists(sync_assembly))
    {
        log_line("ERROR: missing %ls", sync_assembly);
        close_host(host_context);
        return;
    }

    component_entry_point_fn run_sync = NULL;
    rc = load_assembly(sync_assembly,
                       L"ReloadedDropIn.Bootstrap.NativeEntry, ReloadedDropIn.Bootstrap",
                       L"RunSync", NULL, NULL, (void **)&run_sync);
    if (rc != 0 || !run_sync)
    {
        log_line("ERROR: failed to load managed sync entry: 0x%08x", rc);
        close_host(host_context);
        return;
    }

    int sync_result = run_sync(g_game_dir, (int)((lstrlenW(g_game_dir) + 1) * sizeof(wchar_t)));
    if (sync_result != 0)
    {
        log_line("ERROR: managed sync failed with code %d; leaving game vanilla (see logs\\sync.log)", sync_result);
        close_host(host_context);
        return;
    }
    log_line("managed sync complete");

    /* Step 2: chain-load Reloaded's loader with the stock EntryPoint contract. */
    wchar_t loader_dll[MAX_PATH];
    path_combine(loader_dll, g_game_dir, L"reloaded-dropin\\loader\\Reloaded.Mod.Loader.dll");
    if (!file_exists(loader_dll))
    {
        log_line("ERROR: missing %ls", loader_dll);
        close_host(host_context);
        return;
    }

    component_entry_point_fn reloaded_initialize = NULL;
    rc = load_assembly(loader_dll,
                       L"Reloaded.Mod.Loader.EntryPoint, Reloaded.Mod.Loader",
                       L"Initialize", NULL, NULL, (void **)&reloaded_initialize);
    if (rc != 0 || !reloaded_initialize)
    {
        log_line("ERROR: failed to load Reloaded EntryPoint: 0x%08x", rc);
        close_host(host_context);
        return;
    }

    EntryPointParameters parameters;
    parameters.version = RELOADED_ENTRY_POINT_VERSION;
    parameters.flags = RELOADED_FLAG_LOADED_EXTERNALLY;
    parameters.dll_path = g_module_path;

    log_line("invoking Reloaded.Mod.Loader EntryPoint.Initialize");
    reloaded_initialize(&parameters, (int)sizeof(parameters));
    log_line("Reloaded loader initialized");

    /* Step 3: post-load adapter work (e.g. applying the rebuilt archive index
       to disk) — must happen while the game is still held at its entry point. */
    component_entry_point_fn run_post_load = NULL;
    rc = load_assembly(sync_assembly,
                       L"ReloadedDropIn.Bootstrap.NativeEntry, ReloadedDropIn.Bootstrap",
                       L"RunPostLoad", NULL, NULL, (void **)&run_post_load);
    if (rc != 0 || !run_post_load)
    {
        log_line("ERROR: failed to load managed post-load entry: 0x%08x", rc);
    }
    else
    {
        int post_result = run_post_load(g_game_dir, (int)((lstrlenW(g_game_dir) + 1) * sizeof(wchar_t)));
        log_line("post-load step returned %d%s", post_result,
                 post_result == 0 ? "" : " (see logs\\sync.log)");
    }

    /* Keep host_context alive for the life of the process (stock leaks it too). */
    return;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;

    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = instance;
        DisableThreadLibraryCalls(instance);

        /* Freeze the game at its entry point until mods are ready; the
           bootstrap thread releases it on every exit path. */
        hold_game_at_entry();

        g_bootstrap_thread = CreateThread(NULL, 0, bootstrap_thread, NULL, 0, NULL);
        if (!g_bootstrap_thread)
            release_game_entry();
    }

    return TRUE;
}
