# Reloaded Drop-In Validation

## Environment

- Distribution:
- Kernel:
- Steam path:
- Proton version:
- Game version:
- Reloaded-II version:
- Adapter version:
- Utility-manager version:

## Unmodded Baseline

- [ ] Game launches from Steam.
- [ ] Game reaches main menu.
- [ ] Game closes normally.
- [ ] No existing proxy DLL conflicts.
- [ ] Original `data.i` hash recorded.

## Manual Reloaded Baseline

- [ ] Reloaded-II recognizes the game.
- [ ] Required game utility mod loads.
- [ ] Test mod loads.
- [ ] ASI/proxy loader is deployed.
- [ ] Correct proxy DLL is identified.
- [ ] Correct Wine DLL override is identified.
- [ ] Game launches with Reloaded-II closed.
- [ ] Logs are generated.

## Configuration Research

- [ ] Application config located. *(source-verified: `Apps/<id>/AppConfig.json` — see reload-config-notes.md)*
- [ ] Enabled-mod list located. *(source-verified: `EnabledMods` in AppConfig.json)*
- [ ] User-config path located.
- [ ] Dependency representation documented. *(source-verified: `ModDependencies` in ModConfig.json)*
- [ ] Relative paths tested.
- [ ] Add-mod config diff recorded.
- [ ] Disable-mod config diff recorded.

## Drop-In Scanner

- [x] Valid mod discovered. *(unit + CLI test, 2026-07-11)*
- [x] Invalid manifest rejected. *(unit test)*
- [x] Duplicate ID rejected. *(unit test)*
- [x] Missing dependency reported. *(unit + CLI test)*
- [x] Generated output is deterministic. *(unit test)*
- [x] Dry-run changes are accurate. *(unit + CLI test)*

## Granblue Adapter

- [x] Game detected by executable. *(unit + CLI test against fake install)*
- [ ] Game detected by Steam App ID.
- [x] Required utility mod detected. *(doctor check)*
- [x] `data.i` backup created. *(hash-verified baseline, real install 2026-07-11)*
- [x] Backup hash verified. *(state machine + game-update detection, unit-tested)*
- [x] Restore tested. *(CLI restore + uninstall.sh, end-to-end on fake install 2026-07-12)*
- [x] Asset mod added successfully. *(YoRHa model mod on real install, 2026-07-11)*
- [ ] Asset mod removed successfully. *(real game required)*

## Steam Wrapper

- [ ] Original command preserved.
- [x] Wine override appended correctly. *(unit-tested merge logic; wrapper itself not built yet)*
- [x] Game launches from Steam. *(entry-hold bootstrap, real install)*
- [x] Sync occurs before launch. *(in-process, pre-entry-point)*
- [x] Failure stops unsafe launch. *(sync aborts on validation error — CLI test)*
- [ ] Bypass mode works.

## Clean Installation

- [ ] Tested in clean Proton prefix.
- [ ] No prior Reloaded config required.
- [x] No Reloaded GUI launch required. *(GUI never installed at all)*
- [x] Mod installation is extract-and-play. *(validated on Bazzite box)*
- [ ] Uninstall restores original state.
