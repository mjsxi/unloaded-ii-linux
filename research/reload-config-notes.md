# Reloaded-II Configuration Notes

Source-derived notes (from `vendor/Reloaded-II` @ shallow clone, 2026-07-11).
Status: **source-verified, not yet live-verified** — a Phase 0 manual install on the
Linux box must confirm these against real generated files before we trust the generator.

## Where configuration lives

Defined in `source/Reloaded.Mod.Loader.IO/Paths.cs` and `Config/LoaderConfig.cs`:

- **Loader config:** `%APPDATA%/Reloaded-Mod-Loader-II/ReloadedII.json`
  (`Paths.ConfigFolder` — the comment warns the C++ bootstrapper hardcodes this path too).
  Under Proton this maps to
  `<prefix>/drive_c/users/steamuser/AppData/Roaming/Reloaded-Mod-Loader-II/ReloadedII.json`.
- **Portable mode:** a `portable.txt` marker file (`LoaderConfig.PortableModeFile`) switches
  paths; need to verify exact behavior in `IConfig`/`Paths` when set. This matters a lot for
  drop-in packaging.
- **Machine-local data:** `%LOCALAPPDATA%/Reloaded-Mod-Loader-II` (caches, not needed by us).

## LoaderConfig (`ReloadedII.json`)

Key fields the generator must produce (`Config/LoaderConfig.cs`):

| Field | Meaning |
|---|---|
| `LoaderPath32` / `LoaderPath64` | paths to `Reloaded.Mod.Loader.dll` (per arch) — read by the native bootstrapper, property names are load-bearing |
| `LauncherPath` | path to launcher exe |
| `Bootstrapper32Path` / `Bootstrapper64Path` | native bootstrapper DLLs |
| `ApplicationConfigDirectory` | default `Apps/` |
| `ModConfigDirectory` | default `Mods/` |
| `ModUserConfigDirectory` | default `User/Mods/` |
| `MiscConfigDirectory` | default `User/Misc/` |
| `PluginConfigDirectory` | default `Plugins/` |

Also: `EnabledPlugins`, `ShowConsole`, `FirstLaunch`, log-rotation hours, NuGet feeds.

## ApplicationConfig (`Apps/<app-dir>/AppConfig.json`)

From `Config/ApplicationConfig.cs` (`ConfigFileName = "AppConfig.json"`):

| Field | Meaning |
|---|---|
| `AppId` | Reloaded app id — by convention the executable name lowercased (verify) |
| `AppName` | display name |
| `AppLocation` | absolute path to game exe (Windows-style path under Proton) |
| `AppArguments` | launch args |
| `AutoInject` | auto-inject on process detect |
| `EnabledMods` | `string[]` of mod IDs — **the field `sync` must manage** |
| `SortedMods` | `string[]`, V3 launcher-managed display/load order |
| `PreserveDisabledModOrder` | default true on new configs |
| `DontInject` | if true, plain process launch without loader |
| `IsMsStore` | ignore for Steam |
| `PluginData` | `Dictionary<string, object>` — GBFR utility mods may stash data here (check gbfrelink) |
| `WorkingDirectory` | optional |

Load order note from source: `EnabledMods` order is *not* final — the loader reorders
for dependencies at runtime. So our generator needs correct membership + a deterministic
order, but dependency sorting is a nicety not a correctness requirement.

## ModConfig (`Mods/<mod-dir>/ModConfig.json`)

From `Config/ModConfig.cs` (`ConfigFileName = "ModConfig.json"`, icon `Preview.png`):

Fields we must **parse** during discovery:

- `ModId` (unique key), `ModName`, `ModAuthor`, `ModVersion`, `ModDescription`
- `ModDll`, `ModR2RManagedDll32/64`, `ModNativeDll32/64` — used to classify managed/R2R/native
- `ModDependencies` (`string[]` of mod IDs — hard deps)
- `OptionalDependencies` (`string[]`)
- `SupportedAppId` (`string[]` of Reloaded app IDs; empty + `IsUniversalMod` semantics)
- `IsLibrary` (libraries aren't user-enabled; loaded as deps)
- `IsUniversalMod`
- `Tags`, `PluginData`, `CanUnload`, `HasExports`

Discovery in stock Reloaded: `ConfigReader<T>.ReadConfigurations(dir, ConfigFileName, token, depth)` —
Apps scan uses depth 2. Need to confirm mod scan depth (likely 1–2 below `Mods/`).

## The full injection chain (source-verified 2026-07-11)

Traced through `AsiLoaderDeployer.cs`, `Reloaded.Mod.Loader.Bootstrapper/dllmain.cpp` +
`LoaderConfig.cpp`, and `Reloaded.Mod.Loader/Loader.cs`:

```
game loads winmm.dll            (= Ultimate ASI Loader, renamed)
  → loads *.asi in game root    (= Reloaded.Mod.Loader.Bootstrapper.asi, renamed .dll)
    → reads %APPDATA%\Reloaded-Mod-Loader-II\ReloadedII.json   [HARDCODED PATH]
      → hosts CoreCLR via LoaderPath64 + its .runtimeconfig.json
        → Reloaded.Mod.Loader.dll EntryPoint
          → loads reloaded.sharedlib.hooks from ModConfigDirectory
          → FindThisApplication(): match running exe against each AppConfig's
            AppLocation (absolute path, case-insensitive); fallback: AppId ==
            exe filename lowercased
          → loads EnabledMods for that app
```

### What "Deploy ASI Loader" actually does (`AsiLoaderDeployer.cs`)

1. Parses the game exe's PE import table; picks the **first** DLL from the supported
   64-bit list the exe imports: `version.dll, winmm.dll, wininet.dll, dsound.dll, dinput8.dll`.
2. Extracts `ASILoader64.dll` from `Loader/Asi/UltimateAsiLoader.7z` (ships in the
   Reloaded release zip) and writes it under that proxy name next to the game exe.
3. Copies `Reloaded.Mod.Loader.Bootstrapper.dll` into the game root (or `scripts/`/`plugins/`
   if ASI plugins already exist) renamed to `.asi`.

### GUI-less install is therefore fully scriptable

Everything the launcher does for setup is plain file manipulation we can replicate:

1. Extract loader runtime files from the official release zip (never run the launcher).
2. Deploy proxy DLL + bootstrapper `.asi` into the game dir (steps above).
3. Write `ReloadedII.json` into the Proton prefix at
   `pfx/drive_c/users/steamuser/AppData/Roaming/Reloaded-Mod-Loader-II/` — this is the
   ONE file that must live outside the game directory (C++ bootstrapper hardcodes it;
   `portable.txt` only affects where the *launcher-relative* directories point, and both
   bootstrapper and managed loader still read the AppData path — see `Loader.cs:27`).
   Its directory fields can point back INTO the game dir: `ModConfigDirectory` → our
   `mods/`, `ApplicationConfigDirectory` → our `generated/Apps`.
4. Generate `AppConfig.json` (already implemented in ConfigGenerator).
5. User sets `WINEDLLOVERRIDES="winmm=n,b" %command%` in Steam — the only manual step.

Paths in `ReloadedII.json` must be Windows-form as seen inside the prefix
(the game dir under `Z:\...` or via the steamapps mount). The AppId fallback in
`FindThisApplication` is our insurance if path normalization differs.

Note: `LauncherPath` should point at a real file (bootstrapper error paths validate it);
ship the launcher exe in the bundle without ever executing it.

## Open questions for Phase 0/1 live verification

- [x] ~~Exact portable-mode behavior of `portable.txt`~~ — answered from source: launcher-side
      only; the AppData `ReloadedII.json` is always required (see injection chain above).
- [x] ~~What app id string for Relink~~ — loader falls back to exe filename lowercased;
      utility manager's `SupportedAppId` confirms `granblue_fantasy_relink.exe`.
- [ ] `AppLocation` format under Proton (Z:\ path? C:\ path inside prefix?) — mitigated by
      the AppId fallback, but verify what the loader logs.
- [x] ~~ASI-loader deployment file set~~ — answered from source (see injection chain above).
- [ ] Does `gbfrelink.utility.manager` write `PluginData` into AppConfig or user configs?
- [ ] JSON serializer settings (indentation, casing) — match them so diffs stay clean
- [ ] `User/Mods/` per-mod user config shape (`ModUserConfig`)
