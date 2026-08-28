# ChangeLog

## v1.2.0
- The repair also clears the leftovers that keep spawning broken objects and keep the load
  menu asking for a removed pack: vehicle models chosen for transit lines, trees painted on
  roads, and the map's list of required content.
- New "Advanced cleanup" option (off by default; applies to "Repair now" and, when on, to
  the automatic repair) for the remaining data that can point at missing content: company
  brands, pending building upgrades, policies, service budgets and chirps. Each is handled the way the game itself would.
- The scan reports all of those, names missing assets held by data the mod does not
  handle, says when a "missing" pack is merely disabled in the playset, and when a missing
  asset was dropped from a pack that is still enabled.
- The scan result in the options is shown one finding per line, and the log lists every
  repaired road, junction, building and vehicle.
- Junctions switched to another road type get that type's node tags, and buildings that
  fronted a deleted road are registered with it so the game finds them a new road.
- Deleting a transit line with a missing prefab now also deletes its waypoints and
  segments, as the game's own bulldoze does.
- Roads of a missing road type are now removed the way the bulldozer does it: junctions
  that other roads still use are kept (and switched to a remaining road type) instead of
  being destroyed, which crashed the game.
- The repair now runs through the same frame path as bulldozing, so the game's own cleanup
  of lots, driveways and owned vehicles applies. The automatic repair scans at load and runs
  a moment later, once the city is fully set up.
- Wording: the mod-list tidy-up from v1.1.0 is housekeeping; it does not affect the
  load menu warning.

## v1.1.0
- Repair also cleans the list of mods stored in the save (missing mods and old versions
  of mods), so the load menu stops showing the missing content warning.
- Scan result now reports how many such stale entries the save has.

## v1.0.0
- Initial release.
- Scan on every save load, report in options + CSV.
- "Repair now" button with confirmation.
- Optional "Repair automatically on load" toggle (off by default).
