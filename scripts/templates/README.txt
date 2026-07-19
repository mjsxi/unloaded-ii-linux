Unloaded-II (Reloaded Drop-In)
==============================
Drag-and-drop mod loading. No mod manager to run — ever.

One package for every supported game — it detects which game you put it
in on launch:

  - Granblue Fantasy: Relink
  - Persona 5 Royal
  - Final Fantasy XVI
  - Digimon Story: Time Stranger

INSTALL (Linux / Steam Deck / Proton)
-------------------------------------
1. Extract this archive into the game directory, next to the game's
   executable (granblue_fantasy_relink.exe / P5R.exe / ffxvi.exe /
   Digimon Story Time Stranger.exe).
   You should end up with:

     <the game's .exe>
     winmm.dll                <- from this archive
     reloaded-dropin.asi      <- from this archive
     reloaded-dropin/         <- from this archive
     mods/                    <- from this archive
     uninstall.sh             <- from this archive

2. In Steam: right-click the game -> Properties -> Launch Options:

     WINEDLLOVERRIDES="winmm=n,b" %command%

3. Press Play. That's the whole install. The same launch option works
   for every supported game.

The first launch needs internet access once: the required base mods
(your game's mod loader and its libraries) are not redistributed in
this package — they are downloaded directly from their authors'
official GitHub releases, so you always start on the latest versions.

ADDING MODS
-----------
Drop downloaded mod archives (.zip/.7z/.rar) or extracted Reloaded-II
mod folders into mods/. They load automatically on the next launch.
Remove a mod (or toggle it off in the overlay) to disable it.

mods/_base-mods/ holds the required base mods — leave that folder alone;
everything else in mods/ is yours.

IN-GAME OVERLAY
---------------
In Granblue Fantasy: Relink, Persona 5 Royal, and Digimon Story: Time
Stranger, press INSERT in-game to open the Unloaded-II mod panel: see your
mods, switch them on/off, and edit their settings. Changes apply on the
next game launch.

Final Fantasy XVI uses Faith Framework's overlay and UI APIs, with its
DirectX 12 renderer replaced by a Proton-safe compatibility build downloaded
from mjsxi/dxd12-patch-files. This keeps Faith-based UI mods (such as combo
meters) and the Unloaded-II mod-management panel on the same single renderer.
Press INSERT to open the panel, or select Unloaded-II Panel from Faith's Mods
menu.
If the game or overlay fails, close the game and run collect-diagnostics.sh.

PER-GAME NOTES
--------------
Granblue Fantasy: Relink — the game reads its archives through
unhookable I/O, so the drop-in rewrites the data.i archive index and
mirrors mod files into data/ while the game starts (originals are
backed up first and restored by uninstall.sh). Mod conversion caches
are cleared automatically when your mod set changes.

Persona 5 Royal — mods are served entirely at runtime through CriFs
redirection; no game file is ever modified. Persona Essentials'
disposable merge cache is cleared automatically when your mod set
changes.

Final Fantasy XVI — the mod loader packages your active mods as
additive *.diff.pac archives in the game's data/ folder and rebuilds
them every launch. Original game files are never modified, and the
drop-in clears the generated archives whenever your mod set changes,
so a removed mod can never leave stale content behind.
After Faith Framework is downloaded or updated, the drop-in downloads and
caches its DX12 renderer replacement, then swaps only that DLL. Faith's
remaining files and public mod APIs stay unchanged. If the patch cannot be
downloaded and no cached copy exists, Faith and Faith-dependent mods stay
disabled for that launch instead of loading the crash-prone renderer.

Digimon Story: Time Stranger — mods are served entirely at runtime
through MVGL archive redirection (DSTS Mod Loader + MVGL FileLoader);
no game file is ever modified and nothing is generated on disk.

BASE MODS: DOWNLOADED, NOT BUNDLED
----------------------------------
The required base mods and FFXVI's DX12 replacement are not included in this
package — their GitHub releases are the source. The first online launch
installs the set for your game, and afterwards the drop-in checks for newer
releases at most once a week and updates automatically. Once installed,
everything works offline; update checks just fail silently.

To turn off the update checks (a missing base mod is always fetched),
edit reloaded-dropin/update.json and set "AutoUpdate": false.

HOW IT WORKS / LOGS
-------------------
On every launch, before the game starts, the drop-in detects the game,
scans mods/, generates Reloaded-II's configuration, and chain-loads the
Reloaded mod loader — then applies any game-specific file work while
the game is still starting up.

To look at the logs directly:
  reloaded-dropin/logs/bootstrap.log   (loader chain)
  reloaded-dropin/logs/sync.log        (mod discovery + decisions)

TROUBLESHOOTING
---------------
If something doesn't work — a mod not loading, the overlay not showing,
the game misbehaving — collect diagnostics and report it. The bundle is
what lets anyone actually fix the problem, so please include it.

1. Close the game.

2. Open a terminal and run the collector by its full path (replace the
   path with wherever your game actually lives):

     bash "/path/to/your/steamapps/common/<the game>/collect-diagnostics.sh"

   (If your terminal is already in the game's folder — the folder this
   README is in — a plain  ./collect-diagnostics.sh  works too.)

   It gathers every relevant log and config into a dropin-diagnostics.zip
   next to the script. It collects logs and mod lists only — no saves,
   no personal data, no game files.

3. If the game CRASHES (closes without any error dialog), the crash
   happens below our logs, so first add Proton logging to the Steam
   launch options:

     PROTON_LOG=1 WINEDLLOVERRIDES="winmm=n,b" %command%

   Launch once to reproduce the crash, run ./collect-diagnostics.sh
   again (it picks up Proton's log automatically), then restore the
   normal launch option.

4. Attach dropin-diagnostics.zip to a bug report at:

     https://github.com/mjsxi/unloaded-ii-linux/issues

   and mention: which game, what you expected, what happened instead,
   and roughly when it last worked (if it ever did).

UNINSTALL
---------
Close the game and run:  ./uninstall.sh
It restores every game file the drop-in changed (and removes any
generated archives), returning the game to vanilla. Then remove the
launch option from Steam properties.

SAFETY NOTES
------------
- Everything the drop-in changes is restored by uninstall.sh; backups
  are taken before any game file is touched.
- Do not use with anti-cheat-protected online games.
- reloaded-dropin/generated/ is disposable; deleting it is safe.
