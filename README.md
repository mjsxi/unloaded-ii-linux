# Unloaded-II (Reloaded Drop-In)

A drag-and-drop, multi-game frontend and packaging layer around
[Reloaded-II](https://github.com/Reloaded-Project/Reloaded-II).

**The whole install, for any supported game:** extract the game's package into
its directory, add this Steam launch option, press Play:

```text
WINEDLLOVERRIDES="winmm=n,b" %command%
```

Drop mods (folders *or* downloaded archives) into `mods/`. No mod-manager GUI,
ever — closer to REFramework or dropping `.pak` files into an Unreal mod
folder.

Every game uses the same launch option, ships the same core, and gets the same
features: zip-drop mod install, the INSERT-key in-game overlay (list, toggle,
configure mods), and automatic download + updates of its required base mods.
Game-specific disposable loader caches are cleared automatically so removing a
mod cannot leave generated output active.

## Supported games

One universal package (`./scripts/package-release.sh`) covers every supported
game — extract it into any of these game directories and the drop-in detects
which game it's in at launch:

| Game | Steam App ID | Status |
|---|---|---|
| Granblue Fantasy: Relink | 881020 | verified on hardware, tested with some mods |
| Persona 5 Royal | 1687950 | verified on hardware, tested with some mods |
| Final Fantasy XVI | 2515020 | verified on hardware, tested with some mods |

### Game support

I don't plan to support every game that works with Reloaded-II—only the games
I'm actively playing and able to test.

If you'd like to add support for another game, feel free to fork the project
and implement it. Pull requests are welcome if you'd like to contribute your
work back to the community.

Adding a game = one `IGameAdapter` implementation naming its executable and
required mod stack, registered in the bootstrap's adapter list — the single
universal package then covers it automatically.
Games whose modding works through runtime file redirection (like P5R) need
nothing else; games with unhookable I/O (like Relink) implement extra
file-work hooks. See [docs/multi-game-next-steps.md](docs/multi-game-next-steps.md).

## How it works

In one launch: a proxy DLL loads `reloaded-dropin.asi`, which freezes the game
at Ultimate ASI Loader's post-load initialization callback, hosts the bundled
.NET runtime, scans `mods/`, generates Reloaded-II's configuration, chain-loads
the Reloaded loader, applies any game-specific file work (for Relink:
archive-index rewrite + file mirroring), and releases the game. Holding that
callback also prevents P5R from initializing CriFs before its mod-file capacity
has been calculated.

Third-party base mods (each game's mod loader and its libraries) are **not
redistributed**: the first online launch downloads them from their authors'
official GitHub releases and keeps them updated afterwards.

## Layout

```
src/
  ReloadedDropIn.Core/                  game-agnostic: discovery, manifests, dependencies, config generation
  ReloadedDropIn.Bootstrap/             in-process sync pipeline + base-mod installer + archive importer
  ReloadedDropIn.Overlay/               in-game INSERT-key overlay (Dear ImGui via swapchain hook)
  ReloadedDropIn.Overlay.Faith/         FFXVI-only bridge into Faith's patched ImGui renderer
  ReloadedDropIn.Cli/                   command-line tool
  ReloadedDropIn.Adapter.Abstractions/  IGameAdapter contract
  ReloadedDropIn.Adapter.GBFR/          everything Relink-specific (data.i index, file mirror)
  ReloadedDropIn.Adapter.P5R/           everything Persona 5 Royal-specific (just the mod stack)
  ReloadedDropIn.Adapter.FFXVI/         everything Final Fantasy XVI-specific (Faith Framework mod stack)
  ReloadedDropIn.Tests/
  native/dropin-asi/                    the .asi bootstrap (C, mingw)
adapters/                               declarative adapter metadata per game
scripts/                                package-release.sh (one universal zip), diagnostics collector
research/                               config reverse-engineering notes
```

## Building

```bash
dotnet build
dotnet test
./scripts/package-release.sh   # one universal zip; needs x86_64-w64-mingw32-gcc, 7zz, vendor/ inputs (see script)
```

The release version lives in the `VERSION` file — one place, flows everywhere.

## Licensing

Unloaded-II's own code is GPL-3.0. Release packages also contain separately
licensed third-party runtime components. Their attributions, source links, and
license locations are maintained in
[`scripts/templates/THIRD-PARTY-LICENSES.txt`](scripts/templates/THIRD-PARTY-LICENSES.txt).
Game-specific base mods are not redistributed; they are downloaded directly
from their authors' official GitHub releases on first launch. The modified
Faith DX12 source, binary, and license live in the separate
[`dxd12-patch-files`](https://github.com/mjsxi/dxd12-patch-files) repository.

## Docs

- [docs/multi-game-next-steps.md](docs/multi-game-next-steps.md) — the multi-game architecture and what's next
- [docs/development-plan.md](docs/development-plan.md) — original design history
