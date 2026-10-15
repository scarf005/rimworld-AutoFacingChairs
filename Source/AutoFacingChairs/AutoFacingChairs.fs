namespace AutoFacingChairs

open System
open System.Collections.Generic
open HarmonyLib
open RimWorld
open UnityEngine
open Verse

type AutoFacingSettings() =
    inherit ModSettings()

    let mutable chairsOnly = false
    let mutable faceWatchBuildings = true

    member _.ChairsOnly
        with get () = chairsOnly
        and set value = chairsOnly <- value

    member _.FaceWatchBuildings
        with get () = faceWatchBuildings
        and set value = faceWatchBuildings <- value

    override _.ExposeData() =
        Scribe_Values.Look(&chairsOnly, "chairsOnly", false)
        Scribe_Values.Look(&faceWatchBuildings, "faceWatchBuildings", true)

module internal SettingsState =
    let mutable private current: AutoFacingSettings | null = null

    let setCurrent settings = current <- settings

    let chairsOnly () =
        match current with
        | null -> false
        | settings -> settings.ChairsOnly

    let faceWatchBuildings () =
        match current with
        | null -> true
        | settings -> settings.FaceWatchBuildings

type AutoFacingMod(content: ModContentPack) as this =
    inherit Mod(content)

    let mutable settings: AutoFacingSettings | null = null

    do
        LongEventHandler.ExecuteWhenFinished(fun () ->
            settings <- this.GetSettings<AutoFacingSettings>()
            SettingsState.setCurrent settings)

    override _.SettingsCategory() =
        "AutoFacingChairs_SettingsCategory".Translate().Resolve()

    override _.DoSettingsWindowContents(inRect: Rect) =
        match settings with
        | null -> ()
        | settings ->
            let listing = Listing_Standard()
            listing.Begin(inRect)

            let mutable chairsOnly = settings.ChairsOnly
            let mutable faceWatchBuildings = settings.FaceWatchBuildings

            listing.CheckboxLabeled(
                "AutoFacingChairs_ChairsOnly".Translate(),
                &chairsOnly,
                "AutoFacingChairs_ChairsOnlyDesc".Translate()
            )

            listing.CheckboxLabeled(
                "AutoFacingChairs_FaceWatchBuildings".Translate(),
                &faceWatchBuildings,
                "AutoFacingChairs_FaceWatchBuildingsDesc".Translate()
            )

            settings.ChairsOnly <- chairsOnly
            settings.FaceWatchBuildings <- faceWatchBuildings
            listing.End()

module internal AutoFacing =
    // One slot suffices: only the selected designator updates, and selecting one
    // resets it. Designators outlive games, so hold a map id and no things.
    let mutable private lastMapId = -1
    let mutable private lastCell = IntVec3.Invalid
    let private seen = HashSet<Thing>()

    let private placingRot =
        AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot")

    let private rotations = [| Rot4.North; Rot4.East; Rot4.South; Rot4.West |]

    let private allRotationsMask = 0b1111

    let private rotationBit (rotation: Rot4) = 1 <<< rotation.AsInt

    let private isSeat (def: ThingDef) =
        match def.building with
        | null -> false
        | building -> building.isSittable

    let private isRotatableFurniture (def: ThingDef) = def.rotatable && def.building <> null

    let private shouldHandle (def: ThingDef) =
        isRotatableFurniture def && (not (SettingsState.chairsOnly ()) || isSeat def)

    let private buildableThingDef (thing: Thing) =
        match thing.def.entityDefToBuild with
        | :? ThingDef as def -> def
        | _ -> thing.def

    let private seatedJoyTargets (baseType: Type) (usesSeat: JoyGiverDef -> bool) =
        let targets = HashSet<ThingDef>()

        for joyGiver in DefDatabase<JoyGiverDef>.AllDefsListForReading do
            match joyGiver.thingDefs with
            | NonNull defs when AutoFacingLogic.isSeatedJoyGiver baseType (usesSeat joyGiver) joyGiver.giverClass ->
                for def in defs do
                    match def with
                    | null -> ()
                    | def -> targets.Add(def) |> ignore
            | _ -> ()

        targets

    let private adjacentSeatTargets =
        lazy seatedJoyTargets typeof<JoyGiver_InteractBuildingSitAdjacent> _.requireChair

    type private PlacementProfile =
        { FacilityTargets: HashSet<ThingDef>
          FacilityMapDefs: ThingDef array
          FacilitySearchKind: FacilitySearchKind
          FacilityFacesLinearly: bool
          FacilityGridRadius: int }

    let private profiles = Dictionary<ThingDef, PlacementProfile>()
    let private emptyTargetDefs = HashSet<ThingDef>()
    let private emptyMapDefs: ThingDef array = [||]

    let private hasInteractionCellAt (def: ThingDef) (center: IntVec3) (rotation: Rot4) (cell: IntVec3) =
        match def.multipleInteractionCellOffsets with
        | NonNull offsets when offsets.Count > 0 ->
            offsets.Exists(fun offset -> ThingUtility.InteractionCell(offset, center, rotation) = cell)
        | _ ->
            def.hasInteractionCell
            && ThingUtility.InteractionCell(def.interactionCellOffset, center, rotation) = cell

    let private tryInteractionCellFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        match def.interactionCellIcon with
        | NonNull icon when isSeat icon && hasInteractionCellAt def thing.Position thing.Rotation cell ->
            Some(
                if def.interactionCellIconReverse then
                    thing.Rotation.Opposite
                else
                    thing.Rotation
            )
        | _ -> None

    let private toRot4 =
        function
        | CardinalFacing.North -> Rot4.North
        | CardinalFacing.East -> Rot4.East
        | CardinalFacing.South -> Rot4.South
        | CardinalFacing.West -> Rot4.West

    let private tryAdjacentSeatFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        let usesAdjacentSeat =
            def.surfaceType = SurfaceType.Eat || adjacentSeatTargets.Value.Contains(def)

        if not usesAdjacentSeat then
            None
        else
            let rect = GenAdj.OccupiedRect(thing.Position, thing.Rotation, def.size)

            AutoFacingLogic.tryAdjacentFacing rect.minX rect.maxX rect.minZ rect.maxZ cell.x cell.z
            |> Option.map toRot4

    let private tryChairFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        tryInteractionCellFacing thing def cell
        |> Option.orElseWith (fun () -> tryAdjacentSeatFacing thing def cell)

    let private hasMultipleRotations mask = (mask &&& (mask - 1)) <> 0

    let private preferTargetRotation (mask: int) (targetDef: ThingDef) (targetRotation: Rot4) =
        if not targetDef.rotatable || not (hasMultipleRotations mask) then
            mask
        else
            let same = rotationBit targetRotation

            if (mask &&& same) <> 0 then
                same
            else
                let opposite = rotationBit targetRotation.Opposite

                if (mask &&& opposite) <> 0 then opposite else mask

    // Facilities link only within line of sight unless requiresLOS is off, and
    // the public static link check leaves that test to this private one.
    let private facilityHasLineOfSight =
        AccessTools.MethodDelegate<Func<ThingDef, IntVec3, Rot4, ThingDef, IntVec3, Rot4, Map, bool>>(
            AccessTools.Method(
                typeof<CompAffectedByFacilities>,
                "IsPotentiallyValidFacilityForMe_Static",
                [| typeof<ThingDef>
                   typeof<IntVec3>
                   typeof<Rot4>
                   typeof<ThingDef>
                   typeof<IntVec3>
                   typeof<Rot4>
                   typeof<Map> |]
            )
        )

    let private tryFacilityConstraintMask
        (map: Map)
        (placingDef: ThingDef)
        (placingPos: IntVec3)
        facesLinearly
        (target: Thing)
        (targetDef: ThingDef)
        =
        // Vanilla reads the edifice in front of a linear facility without a
        // bounds check, so rotations facing off the map must not be probed.
        let canLink rotation =
            (not facesLinearly
             || ContainmentUtility.GetLinearBuildingFrontCell(placingDef, placingPos, rotation).InBounds(map))
            && CompAffectedByFacilities.CanPotentiallyLinkTo_Static(
                placingDef,
                placingPos,
                rotation,
                targetDef,
                target.Position,
                target.Rotation,
                map
            )
            && facilityHasLineOfSight.Invoke(
                placingDef,
                placingPos,
                rotation,
                targetDef,
                target.Position,
                target.Rotation,
                map
            )

        match
            rotations
            |> Array.fold
                (fun mask rotation ->
                    if canLink rotation then
                        mask ||| rotationBit rotation
                    else
                        mask)
                0
        with
        | 0 -> None
        | mask -> Some mask

    let private radiusForFacilityProps (props: CompProperties_Facility) =
        let minimum = if props.mustBePlacedFacingThingLinear then 8 else 2
        max minimum (int (Math.Ceiling(float props.maxDistance)) + 2)

    let private addMapDef (defs: HashSet<ThingDef>) (def: ThingDef | null) =
        match def with
        | null -> ()
        | def -> defs.Add(def) |> ignore

    let private addBuildableMapDefs (defs: HashSet<ThingDef>) (def: ThingDef) =
        addMapDef defs def
        addMapDef defs def.blueprintDef
        addMapDef defs def.installBlueprintDef
        addMapDef defs def.frameDef

    // Pawns pick seats for these joy givers by the watch area, and prefer a
    // seat that faces the building.
    let private watchMapDefs =
        lazy
            let defs = HashSet<ThingDef>()

            for def in seatedJoyTargets typeof<JoyGiver_WatchBuilding> _.desireSit do
                if def.building <> null then
                    addBuildableMapDefs defs def

            defs |> Seq.toArray

    let private watchCellRect =
        AccessTools.MethodDelegate<Func<ThingDef, IntVec3, Rot4, int, CellRect>>(
            AccessTools.Method(typeof<WatchBuildingUtility>, "GetWatchCellRect")
        )

    let private everPossibleToWatchFrom =
        AccessTools.MethodDelegate<Func<IntVec3, IntVec3, Map, bool, ThingDef, bool>>(
            AccessTools.Method(typeof<WatchBuildingUtility>, "EverPossibleToWatchFrom")
        )

    let private watchFacingMask (map: Map) (cell: IntVec3) =
        let mutable mask = 0

        for mapDef in watchMapDefs.Value do
            for thing in map.listerThings.ThingsOfDef(mapDef) do
                let def = buildableThingDef thing

                for direction in rotations do
                    if
                        (not def.rotatable || direction = thing.Rotation)
                        && watchCellRect.Invoke(def, thing.Position, thing.Rotation, direction.AsInt).Contains(cell)
                        && everPossibleToWatchFrom.Invoke(cell, thing.Position, map, false, def)
                    then
                        mask <- mask ||| rotationBit direction.Opposite

        mask

    let private buildProfile (def: ThingDef) =
        match def.GetCompProperties<CompProperties_Facility>() with
        | null ->
            { FacilityTargets = emptyTargetDefs
              FacilityMapDefs = emptyMapDefs
              FacilitySearchKind = FacilitySearchKind.BoundedOrListed
              FacilityFacesLinearly = false
              FacilityGridRadius = 0 }
        | facility ->
            let targets = HashSet<ThingDef>()
            let mapDefs = HashSet<ThingDef>()

            match facility.linkableBuildings with
            | null -> ()
            | buildings ->
                for target in buildings do
                    match target with
                    | NonNull target when targets.Add(target) -> addBuildableMapDefs mapDefs target
                    | _ -> ()

            let searchKind =
                AutoFacingLogic.facilitySearchKind
                    facility.mustBePlacedAdjacent
                    facility.mustBePlacedAdjacentCardinalToBedHead
                    facility.mustBePlacedAdjacentCardinalToAndFacingBedHead

            let gridRadius =
                match searchKind with
                | FacilitySearchKind.BoundedOrListed -> radiusForFacilityProps facility + max def.size.x def.size.z
                | _ -> 0

            { FacilityTargets = targets
              FacilityMapDefs = mapDefs |> Seq.toArray
              FacilitySearchKind = searchKind
              FacilityFacesLinearly = facility.mustBePlacedFacingThingLinear
              FacilityGridRadius = gridRadius }

    let private profileFor (def: ThingDef) =
        let mutable profile = Unchecked.defaultof<PlacementProfile>

        if profiles.TryGetValue(def, &profile) then
            profile
        else
            let profile = buildProfile def
            profiles.Add(def, profile)
            profile

    let private addThingsAtCell (map: Map) (seen: HashSet<Thing>) (cell: IntVec3) =
        if cell.InBounds(map) then
            let things = map.thingGrid.ThingsListAtFast(cell)

            for thing in things do
                seen.Add(thing) |> ignore

    let private addCardinalPerimeter (map: Map) (seen: HashSet<Thing>) (rect: CellRect) =
        let minX = rect.minX
        let maxX = rect.maxX
        let minZ = rect.minZ
        let maxZ = rect.maxZ

        for x = minX to maxX do
            addThingsAtCell map seen (IntVec3(x, 0, minZ - 1))
            addThingsAtCell map seen (IntVec3(x, 0, maxZ + 1))

        for z = minZ to maxZ do
            addThingsAtCell map seen (IntVec3(minX - 1, 0, z))
            addThingsAtCell map seen (IntVec3(maxX + 1, 0, z))

    let private addEightWayPerimeter (map: Map) (seen: HashSet<Thing>) (rect: CellRect) =
        let minX = rect.minX - 1
        let maxX = rect.maxX + 1
        let minZ = rect.minZ - 1
        let maxZ = rect.maxZ + 1

        for x = minX to maxX do
            addThingsAtCell map seen (IntVec3(x, 0, minZ))
            addThingsAtCell map seen (IntVec3(x, 0, maxZ))

        for z = minZ + 1 to maxZ - 1 do
            addThingsAtCell map seen (IntVec3(minX, 0, z))
            addThingsAtCell map seen (IntVec3(maxX, 0, z))

    let private collectAdjacentFacilityCandidates
        (map: Map)
        (placingDef: ThingDef)
        (placingCell: IntVec3)
        searchKind
        (seen: HashSet<Thing>)
        =
        seen.Clear()

        let rotationCount =
            if placingDef.size.x = 1 && placingDef.size.z = 1 then
                1
            else
                rotations.Length

        for rotationIndex = 0 to rotationCount - 1 do
            let rect =
                GenAdj.OccupiedRect(placingCell, rotations.[rotationIndex], placingDef.size)

            match searchKind with
            | FacilitySearchKind.CardinalAdjacent -> addCardinalPerimeter map seen rect
            | FacilitySearchKind.EightWayAdjacent -> addEightWayPerimeter map seen rect
            | FacilitySearchKind.BoundedOrListed -> ()

    let private boundedGridRect (map: Map) (placingCell: IntVec3) radius =
        let minX = max 0 (placingCell.x - radius)
        let maxX = min (map.Size.x - 1) (placingCell.x + radius)
        let minZ = max 0 (placingCell.z - radius)
        let maxZ = min (map.Size.z - 1) (placingCell.z + radius)
        CellRect(minX, minZ, maxX - minX + 1, maxZ - minZ + 1)

    let private listedTargetCountAtMost (map: Map) (mapDefs: ThingDef array) limit =
        let mutable count = 0L
        let mutable index = 0

        while count * 4L <= limit && index < mapDefs.Length do
            count <- count + int64 (map.listerThings.ThingsOfDef(mapDefs.[index]).Count)
            index <- index + 1

        count

    let private chairCandidateOffsets =
        lazy
            let offsets =
                HashSet<IntVec3> [ IntVec3(-1, 0, 0); IntVec3(1, 0, 0); IntVec3(0, 0, -1); IntVec3(0, 0, 1) ]

            let addInteractionOffset (offset: IntVec3) =
                for rotation in rotations do
                    let rotated = offset.RotatedBy(rotation)

                    offsets.Add(IntVec3(-rotated.x, 0, -rotated.z)) |> ignore

            for def in DefDatabase<ThingDef>.AllDefsListForReading do
                match def.interactionCellIcon, def.multipleInteractionCellOffsets with
                | NonNull icon, NonNull interactionOffsets when isSeat icon && interactionOffsets.Count > 0 ->
                    for offset in interactionOffsets do
                        addInteractionOffset offset
                | NonNull icon, _ when isSeat icon && def.hasInteractionCell ->
                    addInteractionOffset def.interactionCellOffset
                | _ -> ()

            offsets |> Seq.toArray

    let private findFacing (map: Map) (placingDef: ThingDef) (placingCell: IntVec3) (currentRotation: Rot4) =
        let mutable allowed = allRotationsMask
        let mutable preferred = allRotationsMask
        let mutable hasConstraint = false
        let profile = profileFor placingDef

        let applyMask mask =
            hasConstraint <- true
            allowed <- allowed &&& mask

        let processChairThing (thing: Thing) =
            let targetDef = buildableThingDef thing

            match tryChairFacing thing targetDef placingCell with
            | Some facing -> applyMask (rotationBit facing)
            | None -> ()

        let processFacilityThing (thing: Thing) =
            let targetDef = buildableThingDef thing

            if profile.FacilityTargets.Contains(targetDef) then
                match
                    tryFacilityConstraintMask map placingDef placingCell profile.FacilityFacesLinearly thing targetDef
                with
                | Some mask ->
                    applyMask mask
                    preferred <- preferred &&& preferTargetRotation mask targetDef thing.Rotation
                | None -> ()

        // Chair lookup only touches cells that can contain an interaction or
        // adjacent-seat target. The scratch set is reused between mouse cells.
        if isSeat placingDef then
            seen.Clear()

            let offsets = chairCandidateOffsets.Value
            let mutable offsetIndex = 0

            while allowed <> 0 && offsetIndex < offsets.Length do
                let candidateCell = placingCell + offsets.[offsetIndex]

                if candidateCell.InBounds(map) then
                    let things = map.thingGrid.ThingsListAtFast(candidateCell)
                    let mutable thingIndex = 0

                    while allowed <> 0 && thingIndex < things.Count do
                        let thing = things.[thingIndex]

                        if seen.Add(thing) then
                            processChairThing thing

                        thingIndex <- thingIndex + 1

                offsetIndex <- offsetIndex + 1

        if allowed <> 0 && profile.FacilityTargets.Count > 0 then
            match profile.FacilitySearchKind with
            | FacilitySearchKind.CardinalAdjacent
            | FacilitySearchKind.EightWayAdjacent ->
                // Adjacent facilities only need the perimeter of their possible
                // occupied rectangles. This is O(width + height), and 1x1
                // furniture touches exactly 4 or 8 cells.
                collectAdjacentFacilityCandidates map placingDef placingCell profile.FacilitySearchKind seen

                let mutable enumerator = seen.GetEnumerator()

                while allowed <> 0 && enumerator.MoveNext() do
                    processFacilityThing enumerator.Current

            | FacilitySearchKind.BoundedOrListed ->
                let rect = boundedGridRect map placingCell profile.FacilityGridRadius

                let gridCellCount =
                    int64 (rect.maxX - rect.minX + 1) * int64 (rect.maxZ - rect.minZ + 1)

                let listedTargetCount =
                    listedTargetCountAtMost map profile.FacilityMapDefs gridCellCount

                if AutoFacingLogic.preferListedTargetScan listedTargetCount gridCellCount then
                    // ListerThings is indexed by ThingDef, so sparse target
                    // types avoid probing hundreds or thousands of empty cells.
                    let mutable defIndex = 0

                    while allowed <> 0 && defIndex < profile.FacilityMapDefs.Length do
                        let things = map.listerThings.ThingsOfDef(profile.FacilityMapDefs.[defIndex])

                        let mutable thingIndex = 0

                        while allowed <> 0 && thingIndex < things.Count do
                            processFacilityThing things.[thingIndex]
                            thingIndex <- thingIndex + 1

                        defIndex <- defIndex + 1
                else
                    // Dense target types fall back to the bounded ThingGrid, so
                    // cost stays limited by the facility's configured range.
                    seen.Clear()

                    let mutable z = rect.minZ

                    while allowed <> 0 && z <= rect.maxZ do
                        let mutable x = rect.minX

                        while allowed <> 0 && x <= rect.maxX do
                            let things = map.thingGrid.ThingsListAtFast(IntVec3(x, 0, z))

                            let mutable thingIndex = 0

                            while allowed <> 0 && thingIndex < things.Count do
                                let thing = things.[thingIndex]

                                if seen.Add(thing) then
                                    processFacilityThing thing

                                thingIndex <- thingIndex + 1

                            x <- x + 1

                        z <- z + 1

        seen.Clear()

        let watch =
            if not hasConstraint && isSeat placingDef && SettingsState.faceWatchBuildings () then
                watchFacingMask map placingCell
            else
                0

        AutoFacingLogic.resolveRotation hasConstraint allowed preferred watch currentRotation.AsInt
        |> Option.map Rot4

    let reset () = lastCell <- IntVec3.Invalid

    let tryApply (designator: Designator_Place) =
        match Find.CurrentMap, designator.PlacingDef with
        | null, _ -> ()
        | map, (:? ThingDef as placingDef) when shouldHandle placingDef ->
            let cell = UI.MouseCell()

            let overArchitectPanel =
                ArchitectCategoryTab.InfoRect.Contains(UI.MousePositionOnUIInverted)

            if
                not overArchitectPanel
                && cell.InBounds(map)
                && (cell <> lastCell || map.uniqueID <> lastMapId)
            then
                lastMapId <- map.uniqueID
                lastCell <- cell

                let rotation = &placingRot.Invoke(designator)

                match findFacing map placingDef cell rotation with
                | Some facing when facing <> rotation -> rotation <- facing
                | _ -> ()
        | _ -> ()

[<HarmonyPatch(typeof<Designator_Place>, "Selected")>]
type internal DesignatorPlaceSelectedPatch private () =
    [<HarmonyPostfix>]
    static member Postfix() = AutoFacing.reset ()

[<HarmonyPatch(typeof<Designator_Place>, "SelectedUpdate")>]
type internal DesignatorPlaceSelectedUpdatePatch private () =
    [<HarmonyPrefix>]
    static member Prefix(__instance: Designator_Place) = AutoFacing.tryApply __instance

// A click places at the cell under the mouse when the event arrives, which can
// differ from the cell SelectedUpdate saw earlier in the frame. An install
// designator loses its PlacingDef with the selected thing, and vanilla only
// deselects it after this prefix.
[<HarmonyPatch(typeof<DesignatorManager>, "ProcessInputEvents")>]
type internal DesignatorManagerProcessInputEventsPatch private () =
    [<HarmonyPrefix>]
    static member Prefix(__instance: DesignatorManager) =
        match __instance.SelectedDesignator with
        | :? Designator_Place as designator when designator.CanRemainSelected() -> AutoFacing.tryApply designator
        | _ -> ()

[<StaticConstructorOnStartup>]
type internal AutoFacingBootstrap private () =
    static do Harmony("scarf.chairsnap").PatchAll()
