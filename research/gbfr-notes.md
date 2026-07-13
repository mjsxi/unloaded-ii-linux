# Granblue Fantasy: Relink — Adapter Research Notes

Source-derived from `vendor/gbfrelink.utility.manager` and `vendor/GBFRDataTools` (2026-07-11).

## Identity

- Steam App ID: `881020`
- Executable: `granblue_fantasy_relink.exe`
- Reloaded app ID (confirmed from utility manager's `SupportedAppId`): `granblue_fantasy_relink.exe`
- Additional executables in `SupportedAppId` (Endless Ragnarok beta/demo):
  - `granblue_fantasy_relink_endless_ragnarok_beta_test.exe`
  - `granblue_fantasy_relink_endless_ragnarok_demo.exe`

## gbfrelink.utility.manager (v2.0.1 at time of clone)

- ModId: `gbfrelink.utility.manager`
- R2R mod: `x86/gbfrelink.utility.manager.dll` + `x64/...`
- **Hard dependencies** (from `ModDependencies`) — the drop-in package must bundle these,
  since stock Reloaded resolves missing deps by downloading from NuGet feeds via the GUI:
  - `Reloaded.Memory.SigScan.ReloadedII`
  - `reloaded.sharedlib.hooks`
  - `reloaded.universal.redirector`
- README note: it does **not** use the Reloaded file redirector at runtime (game crashes
  with many redirected files) — it has its own `data.i`-based mechanism (`DataManager.cs`,
  code shared with GBFRDataTools). The redirector is still a declared dependency though.
- Key sources to study for data.i behavior: `DataManager.cs`, `Archive.cs`,
  `ModFileCacheRegistry.cs`.

## Implications for the adapter

- `adapters/gbfr/adapter.json` `requiredMods` needs the utility manager **plus its three
  dependencies** as bundled mods.
- Backup target: `data.i` in the game root (utility manager rewrites/appends to it).
- Doctor checks: presence of utility manager + 3 deps, `data.i` hash/backup state,
  executable found, proxy DLL state.

## Open questions

- [ ] Which exact files does the utility manager modify besides `data.i`? (read DataManager.cs)
- [ ] Where does it place extracted mod output (`data/` folder?) — affects uninstall/restore.
- [ ] Version pinning: which utility-manager release to bundle for 0.1.
