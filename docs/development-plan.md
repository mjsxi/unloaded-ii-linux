# Reloaded Drop-In Runtime
## Development Plan — Granblue Fantasy: Relink First, Multi-Game Later

**Working name:** Reloaded Drop-In  
**Initial target:** Granblue Fantasy: Relink  
**Initial platform:** Linux / Steam / Proton  
**Long-term target:** A game-agnostic, drag-and-drop frontend for Reloaded-II-compatible games

---

## 1. Project Goal

The intended user experience is:

```text
1. Extract Reloaded Drop-In into a game's directory.
2. Put compatible mods into a `mods/` folder.
3. Launch the game normally through Steam.
4. Never manually open Reloaded-II for routine use.
```

The project should feel closer to **REFramework** or dropping `.pak` files into an Unreal Engine mod directory than operating a separate Windows mod manager.

The first release will support **Granblue Fantasy: Relink**. The internal architecture should avoid hard-coding Relink behavior into the core so additional games can be supported through small game-specific adapters.

---

## 2. What This Project Is—and Is Not

### It is

- A simplified frontend and packaging layer around Reloaded-II.
- A local mod-discovery system.
- An automatic Reloaded application/configuration generator.
- A Steam/Proton bootstrapper.
- A framework for adding per-game setup logic.
- A way to launch games normally from Steam after installation.

### It is not

- A complete rewrite of Reloaded-II.
- A replacement for every game-specific mod loader.
- A mod downloader or Nexus/GameBanana client in version 0.1.
- An in-game settings overlay in version 0.1.
- A universal ZIP installer initially.
- A tool for games protected by anti-cheat systems that block injection.

Keeping Reloaded-II as the underlying runtime preserves compatibility with existing Reloaded mods and avoids rebuilding its injection, dependency, configuration, and lifecycle systems.

---

## 3. Target User Experience

### Installation

The user extracts a release archive into the game directory:

```text
Game Directory/
├── Game.exe
├── winmm.dll
├── mods/
│   └── PUT_MODS_HERE.txt
└── reloaded-dropin/
    ├── runtime/
    ├── adapters/
    ├── generated/
    ├── logs/
    └── dropin.toml
```

The proxy DLL name may differ by game. Common candidates include:

```text
winmm.dll
dinput8.dll
version.dll
```

### Adding mods

The user extracts Reloaded-compatible mod folders into:

```text
Game Directory/mods/
```

Example:

```text
mods/
├── gbfrelink.utility.manager/
│   ├── ModConfig.json
│   └── ...
├── CharacterCostume/
│   ├── ModConfig.json
│   └── ...
└── BetterHUD/
    ├── ModConfig.json
    └── ...
```

### Launching

The user presses **Play** in Steam.

The system then:

```text
Steam starts the game
        ↓
pre-launch helper scans `mods/`
        ↓
game adapter validates required components
        ↓
Reloaded configuration is generated
        ↓
proxy/ASI loader starts Reloaded-II
        ↓
Reloaded loads enabled mods
        ↓
game starts normally
```

The Reloaded-II desktop GUI should not appear during routine use.

---

## 4. Why Use a Game-Adapter Architecture?

Reloaded-II itself is game-agnostic, but individual games can require substantially different setup.

Examples of game-specific differences include:

- Executable name.
- Steam App ID.
- Preferred proxy DLL.
- Proton prefix.
- Required foundational mods.
- Required file registration or archive modification.
- Known incompatible injection methods.
- Load-order requirements.
- Files that should be backed up.
- Game-specific mod folder formats.
- Whether configuration changes require preprocessing.

Therefore, the project should have two layers:

```text
Generic Core
├── game detection
├── mod discovery
├── manifest parsing
├── dependency validation
├── Reloaded config generation
├── logging
├── installation/update logic
└── launch integration

Game Adapter
├── game identity
├── executable names
├── Steam App ID
├── required built-in mods
├── injection settings
├── backup rules
├── preprocessing hooks
└── validation rules
```

This lets Granblue Fantasy: Relink be the first supported adapter instead of becoming the entire architecture.

---

## 5. Proposed Repository Structure

```text
reloaded-dropin/
├── README.md
├── LICENSE
├── Directory.Build.props
├── ReloadedDropIn.sln
│
├── src/
│   ├── ReloadedDropIn.Core/
│   │   ├── Discovery/
│   │   ├── Manifests/
│   │   ├── Dependencies/
│   │   ├── Configuration/
│   │   ├── Filesystem/
│   │   └── Logging/
│   │
│   ├── ReloadedDropIn.Cli/
│   │   ├── Commands/
│   │   └── Program.cs
│   │
│   ├── ReloadedDropIn.Bootstrap/
│   │   ├── Launch/
│   │   ├── Proton/
│   │   └── Steam/
│   │
│   ├── ReloadedDropIn.Adapter.Abstractions/
│   │   ├── IGameAdapter.cs
│   │   ├── GameIdentity.cs
│   │   └── AdapterContext.cs
│   │
│   ├── ReloadedDropIn.Adapter.GBFR/
│   │   ├── GbfrAdapter.cs
│   │   ├── GbfrValidation.cs
│   │   ├── GbfrBackup.cs
│   │   └── adapter.json
│   │
│   └── ReloadedDropIn.Tests/
│
├── adapters/
│   └── gbfr/
│       ├── adapter.json
│       ├── bundled-mods/
│       └── templates/
│
├── scripts/
│   ├── install-dev.sh
│   ├── find-steam-game.sh
│   ├── reset-test-install.sh
│   └── package-release.sh
│
├── research/
│   ├── validation.md
│   ├── reload-config-notes.md
│   └── gbfr-notes.md
│
├── vendor/
│   ├── Reloaded-II/
│   ├── gbfrelink.utility.manager/
│   ├── GBFRDataTools/
│   └── Relink-Mod-Manager/
│
└── dist/
```

---

## 6. Adapter Interface

A minimal game-adapter contract might look like:

```csharp
public interface IGameAdapter
{
    string Id { get; }
    string DisplayName { get; }

    IReadOnlyList<string> ExecutableNames { get; }
    IReadOnlyList<uint> SteamAppIds { get; }

    GameDetectionResult Detect(AdapterContext context);

    ValidationResult ValidateInstallation(AdapterContext context);

    IReadOnlyList<RequiredMod> GetRequiredMods();

    InjectionConfiguration GetInjectionConfiguration(
        AdapterContext context);

    Task BeforeGenerateConfigurationAsync(
        AdapterContext context,
        CancellationToken cancellationToken);

    Task AfterGenerateConfigurationAsync(
        AdapterContext context,
        CancellationToken cancellationToken);

    Task CreateBackupsAsync(
        AdapterContext context,
        CancellationToken cancellationToken);
}
```

The core should not know anything about `data.i`, Relink, Cygames, or `gbfrelink.utility.manager`. Those belong in the Granblue adapter.

---

## 7. Game Adapter Manifest

Adapters should also include declarative metadata so many future games can be added without writing much code.

Example:

```json
{
  "id": "gbfr",
  "displayName": "Granblue Fantasy: Relink",
  "steamAppIds": [881020],
  "executables": [
    "granblue_fantasy_relink.exe"
  ],
  "platforms": [
    "windows",
    "linux-proton"
  ],
  "injection": {
    "preferredProxy": "winmm.dll",
    "wineOverride": "winmm=n,b"
  },
  "requiredMods": [
    {
      "id": "gbfrelink.utility.manager",
      "bundled": true,
      "enabled": true
    }
  ],
  "backups": [
    "data.i"
  ]
}
```

A future adapter that only needs a different executable and proxy DLL might be entirely declarative. Games with special preprocessing can include a compiled adapter module.

---

## 8. Granblue Fantasy: Relink Requirements

Granblue Fantasy: Relink uses Steam App ID:

```text
881020
```

The expected executable is:

```text
granblue_fantasy_relink.exe
```

Relink modding commonly uses:

- Reloaded-II.
- `gbfrelink.utility.manager`.
- GBFRDataTools functionality for registering external files in `data.i`.

The Relink utility manager should initially remain an existing bundled Reloaded mod rather than having its behavior absorbed into the core.

### Relink-specific startup chain

```text
Reloaded Drop-In
        ↓
Reloaded-II runtime
        ↓
gbfrelink.utility.manager
        ↓
Relink-compatible installed mods
        ↓
external file registration / data.i handling
        ↓
Granblue Fantasy: Relink
```

### Required safety behavior

Before any tool modifies or causes modification of `data.i`:

```text
1. Confirm the game is not running.
2. Create a versioned backup.
3. Store a hash of the original file.
4. Do not overwrite the only backup.
5. Log every operation.
6. Provide a restore command.
```

Suggested backup structure:

```text
reloaded-dropin/backups/
└── gbfr/
    ├── 2026-07-11T162500Z/
    │   ├── data.i
    │   └── manifest.json
    └── latest.json
```

---

## 9. Source Code and Tools to Download

Create the workspace:

```bash
mkdir -p ~/Projects/reloaded-dropin/vendor
cd ~/Projects/reloaded-dropin/vendor
```

### Reloaded-II

```bash
git clone --recursive \
  https://github.com/Reloaded-Project/Reloaded-II.git
```

Purpose:

- Underlying mod runtime.
- DLL injection/bootstrap behavior.
- Application configuration formats.
- Mod manifest and dependency behavior.
- Portable/runtime packaging research.

Also download the portable release archive from the Reloaded-II Releases page for comparison with your source build.

### Granblue Fantasy: Relink utility manager

```bash
git clone \
  https://github.com/WistfulHopes/gbfrelink.utility.manager.git
```

Purpose:

- Existing Relink-specific Reloaded integration.
- Mod installation behavior.
- External-file registration.
- `data.i` workflow.
- Required dependency and configuration behavior.

### GBFRDataTools

```bash
git clone \
  https://github.com/Nenkai/GBFRDataTools.git
```

Purpose:

- Relink archive and `data.i` handling.
- External-file registration.
- Understanding the underlying operations used by the utility manager.

### Optional reference: Relink Mod Manager

```bash
git clone \
  https://github.com/Zetas-Workshop/Relink-Mod-Manager.git
```

Purpose:

- ZIP import behavior.
- Conflict and priority design.
- Variant selection.
- Installation preview concepts.

Do not make this the initial runtime dependency.

### Optional reference: Relink Mod Organizer

```bash
git clone \
  https://github.com/RokyZevon/RelinkModOrganizer.git
```

Purpose:

- Existing Windows/Steam Deck organization patterns.
- Compatibility observations.
- Potential overlap to avoid duplicating unnecessarily.

---

## 10. Development Dependencies on CachyOS

Install the basic toolchain:

```bash
sudo pacman -S --needed \
  git \
  base-devel \
  dotnet-sdk \
  dotnet-runtime \
  wine \
  winetricks \
  protontricks \
  jq \
  p7zip \
  cmake \
  ninja
```

Verify:

```bash
git --version
dotnet --info
wine --version
protontricks --version
cmake --version
```

The exact .NET SDK required should be determined from each repository's project files:

```bash
grep -R "<TargetFramework" -n \
  ~/Projects/reloaded-dropin/vendor \
  --include="*.csproj"
```

Do not assume every Reloaded-II desktop project will build natively on Linux. The first goal is to understand and package the runtime-facing components, not to port the current GUI.

---

## 11. Configuration Files

A user-facing configuration file might be:

```toml
schema_version = 1
adapter = "auto"
mods_directory = "./mods"
auto_enable_discovered_mods = true
validate_dependencies = true
create_backups = true

[logging]
level = "info"
keep_files = 10

[launch]
mode = "steam-wrapper"

[advanced]
reloaded_runtime = "./reloaded-dropin/runtime"
```

Game-specific overrides:

```toml
[game.gbfr]
backup_data_index = true
required_mods = ["gbfrelink.utility.manager"]
```

The project should generate Reloaded's internal configuration files. Users should not normally edit those generated files.

Generated state should live under:

```text
reloaded-dropin/generated/
```

That directory should be safe to delete and regenerate.

---

## 12. Mod Discovery Rules

Version 0.1 should use a strict and predictable rule:

> A discovered mod is a directory containing a valid Reloaded `ModConfig.json`.

Example scan:

```text
mods/
├── ValidMod/
│   └── ModConfig.json
├── ExtraFolder/
│   └── AnotherValidMod/
│       └── ModConfig.json
└── random-readme.txt
```

The scanner should:

1. Search to a limited depth.
2. Identify manifest roots.
3. Reject duplicate mod IDs.
4. Parse dependency metadata.
5. Detect missing dependencies.
6. Detect incompatible architectures when possible.
7. Never execute mod code during discovery.
8. Generate a deterministic enabled-mod list.
9. Log ignored files and reasons.

### Initial ZIP policy

Version 0.1:

```text
Users must extract archives into `mods/`.
```

Later:

```text
Drop ZIP/7z into `mods/inbox/`
        ↓
inspect archive safely
        ↓
show or log proposed install layout
        ↓
extract to normalized mod directory
```

Archive extraction must defend against path traversal and absolute paths.

---

## 13. Launch Strategies

### Strategy A — Steam pre-launch wrapper

Best for the proof of concept.

Steam launch option:

```bash
"/absolute/path/to/reloaded-dropin-launch" %command%
```

The wrapper:

```text
1. Finds the game directory.
2. Detects the adapter.
3. Scans mods.
4. Generates Reloaded configuration.
5. Sets the appropriate Wine DLL override.
6. Executes the original Steam command.
```

Advantages:

- Easy to debug.
- Configuration is generated before the game starts.
- No startup-order race with Reloaded.
- Does not require writing an injected bootstrap component initially.

Disadvantage:

- Requires one Steam launch-option entry.

### Strategy B — REFramework-style proxy bootstrap

Long-term target.

```text
Game.exe
winmm.dll
reloaded-dropin/
mods/
```

The proxy DLL loads the drop-in/bootstrap runtime automatically.

Advantages:

- Closest to pure drag-and-drop.
- User launches normally with no wrapper script.
- Familiar REFramework-style layout.

Risks:

- Proxy choice differs by game.
- Startup order is more difficult.
- Proxy collisions with other mods.
- More complex debugging under Proton.
- Configuration must be ready before Reloaded scans mods.

### Strategy C — Generated Reloaded configuration during installation only

Simplest possible release model.

```text
install once
        ↓
configuration generated
        ↓
Steam launches through deployed ASI loader
```

Adding or removing mods would require rerunning a sync command:

```bash
./reloaded-dropin sync
```

This is less seamless, but useful as an intermediate milestone.

### Recommended progression

```text
Milestone 1: Strategy C
Milestone 2: Strategy A
Milestone 3: Strategy B
```

---

## 14. Command-Line Interface

Proposed commands:

```bash
reloaded-dropin detect
reloaded-dropin doctor
reloaded-dropin sync
reloaded-dropin launch -- <original command>
reloaded-dropin list-mods
reloaded-dropin validate
reloaded-dropin backup
reloaded-dropin restore
reloaded-dropin reset-generated
reloaded-dropin show-log
```

Example:

```text
$ reloaded-dropin doctor

Game: Granblue Fantasy: Relink
Adapter: gbfr
Steam App ID: 881020
Executable: found
Reloaded runtime: found
Proxy DLL: winmm.dll
Wine override: configured
Required mod: gbfrelink.utility.manager found
Discovered mods: 8
Missing dependencies: 0
data.i backup: current
Status: ready
```

The `doctor` command will be one of the most important usability features on Linux.

---

## 15. Logging

Every run should create a structured log.

Example:

```text
reloaded-dropin/logs/latest.log
reloaded-dropin/logs/2026-07-11T162500Z.log
```

Include:

- Tool version.
- Adapter version.
- Game path.
- Steam App ID.
- Proton version when detectable.
- Proxy DLL.
- Wine override.
- Reloaded version.
- Discovered mod IDs and versions.
- Dependency resolution.
- Generated file paths.
- Backup operations.
- Errors and stack traces.
- Final launch command with secrets or user-specific tokens redacted.

Also write a machine-readable report:

```text
reloaded-dropin/logs/latest.json
```

This will make issue reports far easier to diagnose.

---

## 16. Implementation Phases

## Phase 0 — Research and Known-Good Manual Installation

Goal:

> Establish a working baseline before writing custom code.

Tasks:

- Install Reloaded-II for Relink under Proton.
- Add Relink as an application.
- Install and enable `gbfrelink.utility.manager`.
- Install one harmless test mod.
- Deploy the ASI loader.
- Confirm Relink launches from Steam with Reloaded-II closed.
- Record the proxy DLL and required Wine override.
- Locate generated application and mod configuration.
- Snapshot all relevant files.
- Record Reloaded, Proton, and mod versions.

Success criteria:

```text
Reloaded-II GUI is closed.
Steam launches Relink.
The test mod is active.
Reloaded logs are generated.
```

---

## Phase 1 — Configuration Reverse Engineering

Goal:

> Reproduce the minimum Reloaded configuration without opening the GUI.

Tasks:

- Identify application configuration files.
- Identify enabled-mod list.
- Identify per-game config.
- Identify global versus portable paths.
- Identify dependency metadata.
- Test relative paths.
- Compare configuration before and after adding a mod.
- Compare configuration before and after disabling a mod.
- Document fields that are required versus optional.

Deliverable:

```text
research/reload-config-notes.md
```

Success criteria:

```text
A manually edited/generated config enables a test mod successfully.
```

---

## Phase 2 — Generic Mod Scanner and Config Generator

Goal:

> Turn `mods/` into valid Reloaded configuration.

Tasks:

- Create .NET solution.
- Implement mod-directory scanning.
- Parse `ModConfig.json`.
- Detect duplicate IDs.
- Resolve basic dependencies.
- Generate deterministic configuration.
- Add logs and validation output.
- Add dry-run mode.
- Add unit tests using sample manifests.

Command:

```bash
reloaded-dropin sync --dry-run
```

Success criteria:

```text
Dropping a valid mod folder into `mods/` and running `sync`
makes it load on the next Relink launch.
```

---

## Phase 3 — Granblue Adapter

Goal:

> Isolate every Relink-specific behavior in one adapter.

Tasks:

- Detect executable and Steam App ID.
- Require `gbfrelink.utility.manager`.
- Validate Relink installation.
- Back up `data.i`.
- Confirm compatibility with the utility manager.
- Add Relink-specific doctor checks.
- Add restore behavior.
- Test adding, removing, and updating an asset mod.

Success criteria:

```text
Core contains no Relink-specific paths or names.
Deleting the GBFR adapter removes all Relink-specific behavior.
```

---

## Phase 4 — Steam Launch Wrapper

Goal:

> Launch normally from Steam and sync mods automatically.

Tasks:

- Implement command passthrough.
- Preserve Steam/Proton arguments.
- Set or append `WINEDLLOVERRIDES`.
- Run sync before launch.
- Abort safely on validation failure.
- Provide a bypass flag.
- Avoid launching duplicate game processes.

Success criteria:

```text
The user adds one Steam launch option.
After that, adding a mod requires only extracting it into `mods/`
and pressing Play.
```

---

## Phase 5 — Portable Release Packaging

Goal:

> Produce a release archive users can extract into the game directory.

Tasks:

- Bundle the required Reloaded runtime files.
- Bundle the Granblue adapter.
- Bundle or retrieve the required utility-manager release in a license-compliant way.
- Add install and uninstall scripts.
- Add a `doctor` launcher.
- Add version metadata.
- Add checksums.
- Test on a clean Proton prefix.
- Test on desktop Linux and Steam Deck-style environments.

Success criteria:

```text
A clean tester can install the package without manually opening Reloaded-II.
```

---

## Phase 6 — REFramework-Style Bootstrap

Goal:

> Remove the Steam wrapper requirement where practical.

Tasks:

- Research Reloaded bootstrap entry points.
- Select or deploy proxy DLL per adapter.
- Detect proxy collisions.
- Ensure configuration is generated before mod discovery.
- Add a fallback to the Steam wrapper.
- Test cold boot, reboot, and Proton updates.
- Test games that use different graphics APIs.

Success criteria:

```text
Extract package.
Drop mods into `mods/`.
Press Play.
No custom Steam launch command beyond a DLL override, or no launch option at all.
```

---

## Phase 7 — Second Game Adapter

Goal:

> Prove the architecture is actually reusable.

Choose a second game based on:

- Existing Reloaded-II mod ecosystem.
- No anti-cheat.
- Simple application setup.
- Different executable or proxy requirements.
- Minimal game-specific preprocessing.

Success criteria:

```text
At least 80% of the implementation is reused unchanged.
Only the adapter and package metadata differ.
```

Do not claim multi-game support until this phase succeeds.

---

## Phase 8 — Optional In-Game Configuration Overlay

Only begin after the drag-and-drop loader is stable.

Potential capabilities:

- List loaded mods.
- Display versions and dependency errors.
- Edit supported configuration values.
- Open logs.
- Mark restart-required changes.
- Show adapter health.
- Trigger safe config saves.

This should be a separate built-in Reloaded mod, not part of the core launcher.

---

## 17. Validation Checklist

Create:

```text
research/validation.md
```

Use:

```markdown
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

- [ ] Application config located.
- [ ] Enabled-mod list located.
- [ ] User-config path located.
- [ ] Dependency representation documented.
- [ ] Relative paths tested.
- [ ] Add-mod config diff recorded.
- [ ] Disable-mod config diff recorded.

## Drop-In Scanner

- [ ] Valid mod discovered.
- [ ] Invalid manifest rejected.
- [ ] Duplicate ID rejected.
- [ ] Missing dependency reported.
- [ ] Generated output is deterministic.
- [ ] Dry-run changes are accurate.

## Granblue Adapter

- [ ] Game detected by executable.
- [ ] Game detected by Steam App ID.
- [ ] Required utility mod detected.
- [ ] `data.i` backup created.
- [ ] Backup hash verified.
- [ ] Restore tested.
- [ ] Asset mod added successfully.
- [ ] Asset mod removed successfully.

## Steam Wrapper

- [ ] Original command preserved.
- [ ] Wine override appended correctly.
- [ ] Game launches from Steam.
- [ ] Sync occurs before launch.
- [ ] Failure stops unsafe launch.
- [ ] Bypass mode works.

## Clean Installation

- [ ] Tested in clean Proton prefix.
- [ ] No prior Reloaded config required.
- [ ] No Reloaded GUI launch required.
- [ ] Mod installation is extract-and-play.
- [ ] Uninstall restores original state.
```

---

## 18. Testing Strategy

### Unit tests

- Manifest parsing.
- Duplicate detection.
- Dependency sorting.
- Invalid path rejection.
- Adapter selection.
- Generated configuration.
- Backup manifests.
- Wine override merging.

### Integration tests

- Temporary fake game directory.
- Sample Reloaded runtime.
- Sample mod manifests.
- Adapter detection.
- Sync command output.
- Repeat sync idempotency.
- Removal of a previously installed mod.
- Recovery from interrupted writes.

### Real-game tests

- Clean Relink installation.
- Existing heavily modded Relink installation.
- Alternate Steam library path.
- Paths containing spaces.
- Read-only or permission failures.
- Game installed on another filesystem.
- Proton Experimental and a stable Proton version.
- Desktop mode and gaming-mode launch where available.

### Idempotency requirement

Running:

```bash
reloaded-dropin sync
```

multiple times without changing inputs must produce no meaningful changes.

---

## 19. Safety and Reliability Requirements

- Never modify files outside the detected game and project directories without explicit configuration.
- Never delete unknown user files.
- Use atomic writes for generated JSON.
- Back up before modifying game indexes or archives.
- Validate backup hashes.
- Keep generated files separate from user mods.
- Preserve existing Reloaded configuration where possible.
- Detect existing proxy DLLs before overwriting.
- Refuse to overwrite unrecognized proxy files automatically.
- Support dry-run mode.
- Include a reset-generated command.
- Include a full uninstall/restore path.
- Warn about anti-cheat and online-game risks.
- Avoid automatic code execution while inspecting archives or manifests.

---

## 20. Initial Scope

### Version 0.1

- Linux/Steam/Proton.
- Granblue Fantasy: Relink only.
- Extracted Reloaded-compatible mod folders.
- Local `mods/` directory.
- Automatic discovery and enablement.
- Basic dependency validation.
- Existing Reloaded-II runtime.
- Existing `gbfrelink.utility.manager`.
- Steam pre-launch wrapper.
- `doctor`, `sync`, `backup`, and `restore`.
- Clear text and JSON logs.
- No GUI.

### Version 0.2

- Portable release archive.
- Cleaner installation script.
- Safer automatic updates of generated state.
- Better error reporting.
- Optional archive inbox.
- One-click uninstall.

### Version 0.3

- Second game adapter.
- Formal adapter SDK documentation.
- Declarative adapters for simple games.
- Per-game compatibility tests.

### Later

- REFramework-style proxy bootstrap.
- In-game configuration overlay.
- Profiles.
- Mod conflict visualization.
- Mod update integrations.
- Windows support using the same core.

---

## 21. First Concrete Work Session

### Step 1 — Create the workspace

```bash
mkdir -p ~/Projects/reloaded-dropin/{vendor,research,src,scripts,tests,dist}
cd ~/Projects/reloaded-dropin
git init
```

### Step 2 — Clone dependencies

```bash
cd ~/Projects/reloaded-dropin/vendor

git clone --recursive \
  https://github.com/Reloaded-Project/Reloaded-II.git

git clone \
  https://github.com/WistfulHopes/gbfrelink.utility.manager.git

git clone \
  https://github.com/Nenkai/GBFRDataTools.git

git clone \
  https://github.com/Zetas-Workshop/Relink-Mod-Manager.git

git clone \
  https://github.com/RokyZevon/RelinkModOrganizer.git
```

### Step 3 — Record project target frameworks

```bash
grep -R "<TargetFramework" -n \
  ~/Projects/reloaded-dropin/vendor \
  --include="*.csproj" \
  | tee ~/Projects/reloaded-dropin/research/target-frameworks.txt
```

### Step 4 — Establish a known-good manual Relink setup

Do not write the replacement yet.

First prove:

```text
Steam can launch Relink
with Reloaded-II closed
through the deployed ASI/proxy loader
and a test mod loads.
```

### Step 5 — Snapshot the working configuration

Create:

```bash
mkdir -p \
  ~/Projects/reloaded-dropin/research/manual-working-install
```

Copy the relevant Reloaded application, mod, and user configuration there. Redact personal paths before committing anything to Git.

### Step 6 — Diff one change at a time

Capture:

```text
baseline
+ utility manager
+ one test mod
- test mod disabled
```

Use:

```bash
diff -ruN snapshot-a snapshot-b
```

This reveals the smallest configuration changes the generator must reproduce.

### Step 7 — Implement only `detect` and `doctor`

Before generating configuration, build commands that correctly report the environment.

The first executable milestone should be:

```text
$ reloaded-dropin doctor
Status: ready
```

Only then implement `sync`.

---

## 22. Key Architectural Decisions

### Decision 1

**Use Reloaded-II as the runtime.**

Reason:

Existing mods expect Reloaded APIs, lifecycle behavior, and configuration.

### Decision 2

**Start with a Steam pre-launch wrapper.**

Reason:

It guarantees mod scanning and configuration generation occur before the injected runtime starts.

### Decision 3

**Put game-specific behavior behind adapters.**

Reason:

It permits future games without turning the core into a collection of game-name conditionals.

### Decision 4

**Keep generated state disposable.**

Reason:

Users should be able to delete and regenerate internal configuration safely.

### Decision 5

**Require extracted mod folders initially.**

Reason:

Supporting arbitrary archive layouts safely is a separate installer problem.

### Decision 6

**Delay the in-game overlay.**

Reason:

It adds graphics API, input, and runtime-hooking complexity without proving the core drag-and-drop workflow.

---

## 23. Risks

### Reloaded configuration instability

Internal file formats may change between Reloaded versions.

Mitigation:

- Pin a supported Reloaded version per release.
- Version the generator.
- Add schema detection.
- Use Reloaded libraries directly where practical instead of duplicating schemas.

### Proxy DLL conflicts

Another mod may already use `winmm.dll`, `dinput8.dll`, or `version.dll`.

Mitigation:

- Detect and hash existing files.
- Never overwrite unknown files silently.
- Allow adapter-specific alternatives.
- Retain the Steam-wrapper fallback.

### Proton differences

Proton versions and prefixes can behave differently.

Mitigation:

- Log Proton version.
- Avoid unnecessary prefix mutation.
- Test stable Proton and Proton Experimental.
- Keep launch integration reversible.

### Game updates

Relink updates may invalidate archive or `data.i` assumptions.

Mitigation:

- Hash and back up files.
- Detect unexpected game-file changes.
- Require adapter compatibility updates when necessary.
- Refuse unsafe writes after unknown changes.

### Mod packaging variation

Not all downloaded archives contain one clean Reloaded mod directory.

Mitigation:

- Start with extracted manifest directories only.
- Add archive normalization later.
- Provide precise validation errors.

---

## 24. Definition of Success

The Granblue Fantasy: Relink proof of concept is successful when:

```text
1. A clean user extracts Reloaded Drop-In into the Relink directory.
2. They place valid Reloaded-compatible mods in `mods/`.
3. They press Play in Steam.
4. The game starts with the mods active.
5. Reloaded-II's desktop GUI never needs to be opened.
6. Removing a mod folder disables it on the next launch.
7. Failures produce a useful `doctor` report and log.
8. `data.i` can be restored safely.
```

The multi-game architecture is successful when a second Reloaded-II game can be supported primarily by adding an adapter rather than changing the core.

---

## 25. Reference Sources

- Reloaded-II repository:  
  https://github.com/Reloaded-Project/Reloaded-II

- Reloaded-II documentation:  
  https://reloaded-project.github.io/Reloaded-II/

- Reloaded-II injection methods:  
  https://reloaded-project.github.io/Reloaded-II/InjectionMethods/

- Reloaded-II releases:  
  https://github.com/Reloaded-Project/Reloaded-II/releases

- Granblue Fantasy: Relink utility manager:  
  https://github.com/WistfulHopes/gbfrelink.utility.manager

- GBFRDataTools:  
  https://github.com/Nenkai/GBFRDataTools

- Relink modding installation documentation:  
  https://nenkai.github.io/relink-modding/modding/installing_mods/

- Relink Mod Manager:  
  https://github.com/Zetas-Workshop/Relink-Mod-Manager

- Relink Mod Organizer:  
  https://github.com/RokyZevon/RelinkModOrganizer

- Granblue Fantasy: Relink Steam page:  
  https://store.steampowered.com/app/881020/Granblue_Fantasy_Relink/
