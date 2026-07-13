Reloaded Drop-In for Persona 5 Royal
=====================================
Drag-and-drop mod loading. No mod manager to run — ever.

INSTALL (Linux / Steam Deck / Proton)
-------------------------------------
1. Extract this archive into the game directory, next to P5R.exe.
   You should end up with:

     P5R.exe
     winmm.dll                <- from this archive
     reloaded-dropin.asi      <- from this archive
     reloaded-dropin/         <- from this archive
     mods/                    <- from this archive
     uninstall.sh             <- from this archive

2. In Steam: right-click the game -> Properties -> Launch Options:

     WINEDLLOVERRIDES="winmm=n,b" %command%

3. Press Play. That's the whole install.

The first launch needs internet access once: the required base mods (the
P5R mod loader and its libraries) are not redistributed in this package —
they are downloaded directly from their authors' official GitHub
releases, so you always start on the latest versions.

ADDING MODS
-----------
Drop downloaded mod archives (.zip/.7z/.rar) or extracted Reloaded-II
mod folders into mods/. They load automatically on the next launch.
Remove a mod (or toggle it off in the overlay) to disable it.
When the active mod set changes, Persona Essentials' disposable merge cache is
cleared automatically. The package also includes one inert internal bind entry;
this keeps CriFs from passing an empty file list to P5R after the first launch or
after the last user mod is removed.

mods/_base-mods/ holds the required base mods — leave that folder alone;
everything else in mods/ is yours.

IN-GAME OVERLAY
---------------
Press INSERT in-game to open the mod panel: see your mods, switch them
on/off, and edit their settings. Changes apply on the next game launch.

BASE MODS: DOWNLOADED, NOT BUNDLED
----------------------------------
The required base mods (p5rpc.modloader and its dependencies) are not
included in this package — their authors' GitHub releases are the only
source. The first online launch installs them, and afterwards the
drop-in checks for newer releases at most once a day and updates
automatically. Once installed, everything works offline; update checks
just fail silently.

To turn off the update checks (a missing base mod is always fetched),
edit reloaded-dropin/update.json and set "AutoUpdate": false.

HOW IT WORKS / LOGS
-------------------
On every launch, before the game starts, the drop-in scans mods/,
generates Reloaded-II's configuration, and chain-loads the Reloaded mod
loader. The game startup thread stays paused until CriFs has counted the mod
files and installed its hooks. Unlike some games, P5R needs no game-file
changes at all — mods are served to the game entirely at runtime, so your game
files are never modified.

If something doesn't work: run  ./collect-diagnostics.sh  from this
folder (game closed) — it bundles every relevant log into
dropin-diagnostics.zip right here, ready to attach to a bug report.

If the whole game closes without a Reloaded error dialog, temporarily use:

  PROTON_LOG=1 WINEDLLOVERRIDES="winmm=n,b" %command%

Launch once, then run collect-diagnostics.sh. This adds Proton's native crash
log; restore the normal launch option afterwards.

Or look at the logs directly:
  reloaded-dropin/logs/bootstrap.log   (loader chain)
  reloaded-dropin/logs/sync.log        (mod discovery + decisions)

UNINSTALL
---------
Close the game and run:  ./uninstall.sh
Then remove the launch option from Steam properties.

SAFETY NOTES
------------
- This drop-in does not modify any game files for P5R.
- Do not use with anti-cheat-protected online games.
- reloaded-dropin/generated/ is disposable; deleting it is safe.
