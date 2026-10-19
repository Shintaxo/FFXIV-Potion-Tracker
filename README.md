# Potion Tracker

Potion Tracker is a Dalamud plugin that shows **locally observed party potion onsets** for each combat pull. It watches the game's Medicated status (status 49); it does not inspect item use or send results to other players. Observations stay in memory until the plugin unloads.

## Install from the custom repository

Repository URL (paste the whole URL, including `pluginmaster.json`):

```text
https://raw.githubusercontent.com/Shintaxo/FFXIV-Potion-Tracker/main/pluginmaster.json
```

1. In game, open `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Add the URL above, enable the repository entry, and save the settings.
3. Open `/xlplugins`, find **Potion Tracker**, and install it.
4. Use `/potions` to open the results window. After a locally detected combat end, a private chat message also points to that command.

This is a **custom repository**, not a listing in the official Dalamud plugin repository. It targets Dalamud API 15 (.NET 10). If Dalamud's API level changes, a new compatible build is required.

## Reading the results

The window lists the party captured at the start of the latest local pull, sorted by role (tank, healer, melee, physical ranged, caster, then unknown) while retaining party order within each role. It displays each member's class/job icon, observed potion-onset indicator, onset count, and timing. `✓` means at least one onset was detected; `X` means **none was detected**, *not* that the player did not use a potion. `Amount` is the number of detected onsets and can be an underestimate. `Prepull` denotes an onset before local combat began.

A pull begins and ends when the local `InCombat` condition changes. This is a proxy for a pull, not proof of a boss engage, wipe, or kill. Only a visible party actor's Medicated transition from absent to present counts. A status already present when observation begins is not counted. Onsets up to 30 seconds before combat can be associated with the next pull; a 4½-minute window suppresses implausibly close duplicate onsets but never creates inferred uses. Actor visibility, sampling gaps, party changes, or starting the plugin late may cause missed onsets. There is no duty whitelist. The post-combat chat message appears only in your local chat and contains a slash command, not a clickable link.

## Build and release

The project uses [Dalamud.NET.Sdk 15.0.0](https://dalamud.dev/versions/v15/) and .NET 10. A local build needs a Dalamud API-15 installation containing `Dalamud.dll`. Set `DALAMUD_HOME` to that directory, or use the SDK's default XIVLauncher development directory. On Windows, from this repository:

```powershell
dotnet build .\PotionTracker.csproj -c Release
```

On Linux, with an API-15 Dalamud distribution extracted to a directory containing `Dalamud.dll`:

```sh
DALAMUD_HOME=/path/to/dalamud dotnet build PotionTracker.csproj -c Release -p:EnableWindowsTargeting=true
```

The Release build uses DalamudPackager and produces `bin/Release/PotionTracker/latest.zip`. That ZIP contains `PotionTracker.dll` and the generated `PotionTracker.json` manifest. The source template is `PotionTracker.json`; the generated manifest in the ZIP must match the repository entry. The plugin has been compiled against the official API-15 distribution, but its in-game behavior and installation have not yet been verified.

For each new release, increment `<Version>` in `PotionTracker.csproj`, build Release, inspect the ZIP's DLL and manifest (`InternalName`, `AssemblyVersion`, `DalamudApiLevel`), create a GitHub Release with a tag matching the version and attach the ZIP as `PotionTracker.zip`, then update `pluginmaster.json` with the exact new version and release URL. Keep the previous release asset available until users can update. Dalamud API 15 requires the manifest **inside** the ZIP to be accurate; it no longer replaces it with the repository entry on install. The repository JSON is served from `main` at the raw URL above.

Reference: [Dalamud custom repository format](https://dalamud.dev/plugin-publishing/custom-repositories/), [project layout and manifest](https://dalamud.dev/plugin-development/project-layout/).
