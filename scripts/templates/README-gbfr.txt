Reloaded Drop-In for Granblue Fantasy: Relink
==============================================
Drag-and-drop mod loading. No mod manager to run — ever.

INSTALL (Linux / Steam Deck / Proton)
-------------------------------------
1. Extract this archive into the game directory, next to
   granblue_fantasy_relink.exe. You should end up with:

     granblue_fantasy_relink.exe
     winmm.dll                <- from this archive
     reloaded-dropin.asi      <- from this archive
     reloaded-dropin/         <- from this archive
     mods/                    <- from this archive
     uninstall.sh             <- from this archive

2. In Steam: right-click the game -> Properties -> Launch Options:

     WINEDLLOVERRIDES="winmm=n,b" %command%

3. Press Play. That's the whole install.

The first launch needs internet access once: the required base mods (the
GBFR utility manager and its libraries) are not redistributed in this
package — they are downloaded directly from their authors' official
GitHub releases, so you always start on the latest versions.

ADDING MODS
-----------
Drop the downloaded mod archive (.zip/.7z/.rar) straight into mods/ —
no extracting needed. On the next launch it is unpacked into a proper
mod folder (the archive is removed once installed) and the mod loads
that same launch. Dropping a newer archive of an installed mod updates
it in place.

Extracted Reloaded-II mod folders (containing a ModConfig.json) work
exactly the same. Remove a mod's folder to remove it — game files are
cleaned up automatically on the next launch.
When the active mod set changes, Utility Manager's disposable conversion
cache is cleared too, then the mirrored game files are reconciled.

mods/_base-mods/ holds the overlay plus automatically managed base mods.
Leave that folder alone; everything else in mods/ is yours.

IN-GAME OVERLAY
---------------
Press INSERT in-game to open the mod panel: see your mods, switch them
on/off, and edit their settings. Changes apply on the next game launch.

BASE MODS: DOWNLOADED, NOT BUNDLED
----------------------------------
The required base mods (GBFR utility manager and its libraries) are not
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
generates Reloaded-II's configuration, mirrors mod files into the game's
data/ folder, and applies a rebuilt archive index (your original data.i
is backed up to reloaded-dropin/backups/ first, verified by hash).

If something doesn't work: run  ./collect-diagnostics.sh  from this
folder (game closed) — it bundles every relevant log into
dropin-diagnostics.zip right here, ready to attach to a bug report.

Or look at the logs directly:
  reloaded-dropin/logs/bootstrap.log   (loader chain)
  reloaded-dropin/logs/sync.log        (mod discovery + file operations)

UNINSTALL
---------
Close the game and run:  ./uninstall.sh
It restores every changed game file, then removes the drop-in. Also
remove the launch option from Steam properties.

SAFETY NOTES
------------
- Original data.i is backed up before any modification and re-verified
  by hash every launch; game updates are detected and re-baselined.
- Do not use with anti-cheat-protected online games.
- reloaded-dropin/generated/ is disposable; deleting it is safe.
