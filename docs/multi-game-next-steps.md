# Multi-game architecture and next steps

Status as of 2026-07-13: the shared packaging pipeline and P5R adapter are
complete. Both games build from the same `VERSION` and use the same
`WINEDLLOVERRIDES="winmm=n,b" %command%` launch option. GBFR is verified on
hardware. P5R is verified on Proton through first and subsequent launches,
including automatic base-mod installation, archive import, the in-game overlay,
and a real Persona Essentials model replacement.

P5R dependency facts were verified against the live repos on 2026-07-13:
p5rpc.modloader 2.9.5 declares 11 dependencies whose transitive closure (with
CriFs.V2.Hook.Awb pulling in the AWB stream emulator) is 13 mods; all have
release assets matching the `<prefix><version>.7z` pattern the installer
parses. See P5rAdapter.GetRequiredMods for the full mapping.

## How game-agnostic is it already?

More than it looks. The GBFR-specific code was deliberately fenced behind
`IGameAdapter` from the start:

| Component | Agnostic today? |
|---|---|
| winmm.dll proxy (Ultimate ASI Loader) | yes — knows nothing about any game |
| reloaded-dropin.asi (entry hold, hostfxr, chain-load) | yes |
| Bundled .NET runtime + Reloaded loader | yes |
| Core (mod scanner, dependency resolver, config generator, overlay overrides) | yes |
| Overlay mod (INSERT panel, watermark) | yes — scans mods/, reads version.json |
| Bundled-mod auto-updater | yes — driven by `RequiredMod.UpdateRepo` metadata |
| SyncRunner pipeline | yes — compiled-in adapters are detected at runtime |
| GbfrAdapter + GbfrDataIndex + GbfrDataMirror | **no — 100% Relink-specific, and that's fine** |
| uninstall.sh | yes for supported games — its GBFR restore step self-guards |
| package-release.sh | yes — game facts live in `scripts/games/<id>.sh` |

The detection loop in SyncRunner already iterates adapters and picks the one
whose executable exists in the game folder. Adding a game = adding one adapter
project and registering it in that array.

## Why Persona 5 Royal was a good second game

Everything painful about Relink was Relink-specific. P5R (Steam AppId 1687950,
`P5R.exe`) is already the flagship Reloaded-II game:

- The community-standard setup IS Reloaded-II + `p5rpc.modloader` (Sewer56) —
  the exact ecosystem we chain-load.
- P5R reads its CPK archives through **hookable** file APIs (CRI middleware via
  Win32), so runtime redirection actually works there. No data.i-style on-disk
  index rewrite, no physical file mirror — the two hardest, riskiest parts of
  the GBFR adapter simply don't exist for P5R.
- The P5RAdapter detects `P5R.exe`, lists required mods
  (p5rpc.modloader + its library deps, with UpdateRepo metadata so they
  auto-update), validate install. `CreateBackups`/`AfterModsLoaded`/`Restore`
  are near no-ops. Keep the entry-point hold (harmless; guarantees hooks are
  live before the game's first file read).

Verification completed:
1. The full p5rpc.modloader dependency closure and release-asset naming were
   checked against the upstream releases used by the auto-updater.
2. `winmm.dll` with `WINEDLLOVERRIDES="winmm=n,b"` is verified under Proton.
3. A GameBanana model replacement was imported from an archive and rendered
   successfully through Persona Essentials.

## Packaging: stop mixing "the loader" with "the game files"

Current zip mixes three layers. Proposed split — one shared core, thin
per-game packs:

```
unloaded-ii-core-<ver>.zip          (~80MB, shared by every game)
  winmm.dll, reloaded-dropin.asi
  reloaded-dropin/runtime|loader|bootstrap   (bootstrap contains ALL adapters)

unloaded-ii-gbfr-<ver>.zip          (small)
  mods/_base-mods/reloaded.dropin.overlay/   (ours; third-party base mods
                                              are downloaded at first launch,
                                              never redistributed)
  README-GBFR.txt, uninstall.sh

unloaded-ii-p5r-<ver>.zip           (small)
  mods/_base-mods/reloaded.dropin.overlay/   (same download-not-bundle model)
  README-P5R.txt, uninstall.sh
```

Both extract into the game dir; adapters self-select by detecting the exe, so
the core is byte-identical across games. Nexus reality check: each game has
its own Nexus section and users won't hunt for a second download — so keep
publishing ONE combined zip per game (core + that game's pack merged), built
by the same script. The split is a build-system concept, not a user burden.

Concrete build-script change: `scripts/package-release.sh` keeps all generic
staging; per-game facts (BUNDLED_MODS list, required file checks, README
template, uninstaller specifics) move to sourced config files
`scripts/games/gbfr.sh`, `scripts/games/p5r.sh`. `./scripts/package-release.sh
gbfr` or `p5r` emits the combined zip for that game.

## Completed multi-game checklist

1. Per-game package configuration lives in `scripts/games/<id>.sh`.
2. The uninstaller's GBFR-only restore work self-guards on other games.
3. `ReloadedDropIn.Adapter.P5R` is registered and carries official update
   sources for its required dependency stack.
4. The D3D11 INSERT-key overlay is verified in P5R under Proton.
5. The real-mod acceptance test passed with a Persona Essentials model mod.

## Explicitly out of scope for now

- Dynamically-loaded adapter plugin DLLs (compiled-in adapters are simpler and
  the bootstrap is rebuilt per release anyway; revisit at 4+ games).
- Games whose modding isn't Reloaded-based (different loader entirely).
- Anti-cheat / online games: never.
