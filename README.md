# Crash Repair

A Cities: Skylines II mod that repairs saves crashing after mods or asset packs were removed.

Get it on [Paradox Mods](https://mods.paradoxplaza.com/mods/156593/Windows).

## The problem

You unsubscribe from a few mods or asset packs, your city still **seems to** work fine,
nothing happens for an hour, maybe more... and then the game crashes to desktop. No error,
no nothing, and you feel like you just wasted hundreds of hours on a city just for it to
get corrupted :( .

The reason? Every object that removed content ever placed in your city — cars, props,
surfaces, road markings — is still sitting in the save, pointing at something that no
longer exists. Sooner or later the simulation touches one of those broken leftovers and
the game goes down. That's exactly what happened to my own city, and it's why I made
this mod.

## How to fix your save

1. Load the affected city. The mod scans it automatically and shows what it found in the
   mod's options (nothing is deleted yet).
2. Press the **"Repair now"** button and confirm.
3. Save the city **under a new name** and keep the original file as a backup.

There is also a **"Repair automatically on load"** toggle (off by default) — with it on,
you can safely remove mods and assets without worrying about your save getting corrupted.

A detailed list of everything found is written to
`ModsData/CrashRepair/missing_prefabs_report.csv` and `Logs/CrashRepair.log`.

## How it works (the technical version)

The game keeps an empty placeholder for every missing asset a save references
(`ResolvePrefabsSystem` creates it: `PrefabData` disabled, negative index, registered as
an obsolete ID). Crash Repair treats a prefab reference as broken only when all of the
following hold:

- the target entity is gone or has no `PrefabData`, or
- it is such an obsolete placeholder **and** there is no managed `PrefabBase` behind it
  in the `PrefabSystem` registry.

Broken instances are deleted through the game's regular bulldoze pipeline (the `Deleted`
tag), so all owner-side references — road sub-lane buffers, household vehicle lists,
building sub-areas, notification icons — are cleaned up by the same vanilla systems that
handle bulldozing. Runtime entities whose references the game repairs through other
channels — `NetCompositionData`, `EffectInstance`, `LivePath` — are excluded from the
scan, mirroring the game's own `PrimaryPrefabReferencesSystem`.

## Building

Standard CSII mod toolchain (`dotnet build`, auto-deploys to the local mods folder).
Requires the Cities: Skylines II modding toolchain environment variables set up by the
official mod template.
