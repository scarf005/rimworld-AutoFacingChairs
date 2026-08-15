# Auto-Facing Chairs

> [!CAUTION]
> Obviously vibecoded

RimWorld 1.6 quality-of-life mod.

When you place a sittable chair at a chair-using furniture position, the chair automatically rotates to face the furniture.

- Uses the target furniture's actual interaction-cell definition and `interactionCellIconReverse` value.
- Supports dining tables and other `surfaceType=Eat` furniture by facing chairs toward the occupied footprint.
- `Patches/ChairUsingFurniture.xml` tags all XML defs that declare an eating surface; XML inheritance carries the tag to vanilla and modded child defs.
- Works with built furniture, blueprints, and construction frames.
- Only snaps when the mouse enters a new map cell, so Q/E remains available for manual rotation afterwards.
- If overlapping interaction cells demand conflicting rotations, the mod leaves the current rotation unchanged.
- Intended to work with modded furniture and chairs that use vanilla `ThingDef` interaction-cell or eating-surface semantics.

## Requirements

- RimWorld 1.6
- Harmony (`brrainz.harmony`)
- FSharp.Core (`latta.fsharp.core`)

## Build

```sh
just build
```

The project targets .NET Framework 4.7.2. It pins `FSharp.Core` 4.7.2 for ABI compatibility but excludes the runtime assembly from the mod output; the shared FSharp.Core RimWorld mod supplies it at runtime.

## Formatting

```sh
just fmt
```

Fantomas is pinned as a repository-local .NET tool in `.config/dotnet-tools.json`.

## Local development

```sh
just fmt
just build
just install
just enable
```

`RIMWORLD_DIR` and `RIMWORLD_MODS_CONFIG` can override the default local paths used by `just install` and `just enable`.
