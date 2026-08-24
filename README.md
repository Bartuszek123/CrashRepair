# Crash Repair

A Cities: Skylines II mod that repairs saves crashing after mods or asset packs were removed.

Get it on [Paradox Mods](https://mods.paradoxplaza.com/mods/156593/Windows).

## The problem

It probably looks like this: you unsubscribe from a few mods or asset packs, your city still seems to work fine, nothing happens for an hour, maybe more... and then the game crashes to desktop (not very good). No error, no nothing, and YOU feel like you just wasted HUNDREDS of hours on a city just for it to get corrupted :( .

The reason? every object that removed content ever placed in your city — cars, props, surfaces, road markings — is still sitting in the save, pointing at something that no longer exists (because you uninstalled the mod). Sooner or later the simulation touches one of those broken leftovers and the game goes down (very bad).

It's exactly what happened to my city, and it's why I made this mod. If your save loads but keeps crashing after you changed your playset, there is a good chance it can be repaired with this mod (yippe!! your save is not gone forever :)).

## How to fix your save

1. Load the affected city. The mod scans it automatically and shows what it found in the mod's options (nothing is deleted yet, just showing what it found for now).
2. Press "Repair now" button (also in the mod's options) and confirm.
3. Save the city **under a new name** and keep the original file as a backup.

That's it. My city that kept crashing within an hour ran for hours afterwards without a single problem (yippe its fixed now).

There is also a **"Repair automatically on load"** toggle (off by default) — with it on,
you can safely remove mods and assets without worrying about your save getting corrupted.

A detailed list of everything found is written to
`ModsData/CrashRepair/missing_prefabs_report.csv` and `Logs/CrashRepair.log`.

## How it works

The game keeps an empty placeholder for every missing asset a save references
(`ResolvePrefabsSystem` creates it: `PrefabData` disabled, negative index, registered as
an obsolete ID). Crash Repair treats a prefab reference as broken only when all of the
following hold:

- the target entity is gone or has no `PrefabData`, or
- it is such an obsolete placeholder **and** there is no managed `PrefabBase` behind it
  in the `PrefabSystem` registry.

Broken instances are deleted through the game's regular bulldoze pipeline (the `Deleted`
tag), so all owner-side references (road sub-lane buffers, household vehicle lists,
building sub-areas, notification icons) are cleaned up by the same vanilla systems that
handle bulldozing. Runtime entities whose references the game repairs through other
channels (`NetCompositionData`, `EffectInstance`, `LivePath`) are excluded from the
scan, mirroring the game's own `PrimaryPrefabReferencesSystem`.

## Removal

The mod itself is safe to remove at any time because it doesn't add any data to your save
files. Repairs you already saved stay, of course.

## Tips

- Check out my other mod, [Realistic Vehicle Colors](https://mods.paradoxplaza.com/mods/143394/Windows) :)

## Building

Standard CSII mod toolchain (`dotnet build`, auto-deploys to the local mods folder).
Requires the Cities: Skylines II modding toolchain environment variables set up by the
official mod template.
