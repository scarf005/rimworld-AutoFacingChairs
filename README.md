# Auto-Facing Chairs

> [!CAUTION]
> Obviously vibecoded

RimWorld 1.6 and 1.7 quality-of-life mod.

When you place a sittable chair at a chair-using furniture position, the chair automatically rotates to face the furniture.

- Uses the target furniture's actual interaction-cell definition and `interactionCellIconReverse` value.
- Supports dining tables and other `surfaceType=Eat` furniture by facing chairs toward the occupied footprint.
- Supports poker tables, chess tables, and modded furniture whose `JoyGiverDef` uses `JoyGiver_InteractBuildingSitAdjacent` with `requireChair=true`.
- Works with built furniture, blueprints, and construction frames.
- Only snaps when the mouse enters a new map cell, so Q/E remains available for manual rotation afterwards.
- If overlapping interaction cells demand conflicting rotations, the mod leaves the current rotation unchanged.
- Intended to work with modded furniture and chairs that use vanilla `ThingDef` interaction-cell or eating-surface semantics.

## Requirements

- RimWorld 1.6 or 1.7
- Harmony (`brrainz.harmony`)
- FSharp.Core (`latta.fsharp.core`) on RimWorld 1.6; [FSharp.Core (1.7)](https://steamcommunity.com/sharedfiles/filedetails/?id=3819630965) (`scarf.fsharp.core`) on RimWorld 1.7

## Build

```sh
just build
```

`Source/` targets RimWorld 1.7 (.NET 10) and references the local game's `Assembly-CSharp.dll`, `UnityStubs.dll`, and Harmony 3 from `RIMWORLD_DIR`.
The 1.6 assembly is built from the frozen `rimworld-1.6` branch (`just build-16`).

RimWorld 1.7 reference assemblies are not public yet, so the release workflow stops before building; release locally with `just package <version>`.

## Tests

```sh
just test
```

The unit tests cover adjacent-seat joy-giver detection and the four cardinal chair-facing cases.

## Formatting

```sh
just fmt
```

Fantomas is pinned as a repository-local .NET tool in `.config/dotnet-tools.json`.

## Local development

```sh
just fmt
just test
just build
just install
just enable
```

`RIMWORLD_DIR` and `RIMWORLD_MODS_CONFIG` can override the default local paths used by `just install` and `just enable`.
