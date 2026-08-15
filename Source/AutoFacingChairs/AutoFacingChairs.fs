namespace AutoFacingChairs

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open HarmonyLib
open RimWorld
open UnityEngine
open Verse

[<AllowNullLiteral>]
type AutoFacingSettings() =
    inherit ModSettings()

    let mutable chairsOnly = false

    member _.ChairsOnly
        with get () = chairsOnly
        and set value = chairsOnly <- value

    override _.ExposeData() =
        Scribe_Values.Look(&chairsOnly, "chairsOnly", false)

module internal SettingsState =
    let mutable private current: AutoFacingSettings = null

    let setCurrent settings = current <- settings

    let chairsOnly () =
        not (isNull current) && current.ChairsOnly

type AutoFacingMod(content: ModContentPack) as this =
    inherit Mod(content)

    let mutable settings: AutoFacingSettings = null

    do
        LongEventHandler.ExecuteWhenFinished(fun () ->
            settings <- this.GetSettings<AutoFacingSettings>()
            SettingsState.setCurrent settings)

    override _.SettingsCategory() =
        "AutoFacingChairs_SettingsCategory".Translate().Resolve()

    override _.DoSettingsWindowContents(inRect: Rect) =
        if not (isNull settings) then
            let listing = Listing_Standard()
            listing.Begin(inRect)

            let mutable chairsOnly = settings.ChairsOnly

            listing.CheckboxLabeled(
                "AutoFacingChairs_ChairsOnly".Translate(),
                &chairsOnly,
                "AutoFacingChairs_ChairsOnlyDesc".Translate()
            )

            settings.ChairsOnly <- chairsOnly
            listing.End()

type internal PlacementState() =
    let mutable hasLastCell = false
    let mutable lastCell = Unchecked.defaultof<IntVec3>
    let mutable lastMap: Map = null

    member _.Reset() =
        hasLastCell <- false
        lastMap <- null

    member _.IsSame(map: Map, cell: IntVec3) =
        hasLastCell && obj.ReferenceEquals(lastMap, map) && lastCell = cell

    member _.Remember(map: Map, cell: IntVec3) =
        hasLastCell <- true
        lastMap <- map
        lastCell <- cell

module internal AutoFacing =
    let private states = ConditionalWeakTable<Designator_Build, PlacementState>()

    let private placingRotField =
        AccessTools.Field(typeof<Designator_Place>, "placingRot") |> Option.ofObj

    let private rotations = [| Rot4.North; Rot4.East; Rot4.South; Rot4.West |]

    let private allRotationsMask = 0b1111

    let private rotationBit (rotation: Rot4) =
        if rotation = Rot4.North then 0b0001
        elif rotation = Rot4.East then 0b0010
        elif rotation = Rot4.South then 0b0100
        else 0b1000

    let private stateFor (designator: Designator_Build) = states.GetOrCreateValue(designator)

    let private isSeat (def: ThingDef) =
        not (isNull def.building) && def.building.isSittable

    let private isRotatableFurniture (def: ThingDef) =
        def.rotatable && not (isNull def.building)

    let private shouldHandle (def: ThingDef) =
        isRotatableFurniture def && (not (SettingsState.chairsOnly ()) || isSeat def)

    let private buildableThingDef (thing: Thing) =
        match thing.def.entityDefToBuild with
        | :? ThingDef as def -> def
        | _ -> thing.def

    let private containsDef (defs: List<ThingDef>) (def: ThingDef) = not (isNull defs) && defs.Contains(def)

    let private hasInteractionCellAt (def: ThingDef) (center: IntVec3) (rotation: Rot4) (cell: IntVec3) =
        let offsets = def.multipleInteractionCellOffsets

        if not (isNull offsets) && offsets.Count > 0 then
            let mutable found = false
            let mutable index = 0

            while not found && index < offsets.Count do
                found <- ThingUtility.InteractionCell(offsets.[index], center, rotation) = cell

                index <- index + 1

            found
        else
            def.hasInteractionCell
            && ThingUtility.InteractionCell(def.interactionCellOffset, center, rotation) = cell

    let private tryInteractionCellFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        let icon = def.interactionCellIcon

        if
            not (isNull icon)
            && isSeat icon
            && hasInteractionCellAt def thing.Position thing.Rotation cell
        then
            Some(
                if def.interactionCellIconReverse then
                    thing.Rotation.Opposite
                else
                    thing.Rotation
            )
        else
            None

    let private tryTableFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        if def.surfaceType <> SurfaceType.Eat then
            None
        else
            let rect = GenAdj.OccupiedRect(thing.Position, thing.Rotation, def.size)

            if cell.x = rect.minX - 1 && cell.z >= rect.minZ && cell.z <= rect.maxZ then
                Some Rot4.East
            elif cell.x = rect.maxX + 1 && cell.z >= rect.minZ && cell.z <= rect.maxZ then
                Some Rot4.West
            elif cell.z = rect.minZ - 1 && cell.x >= rect.minX && cell.x <= rect.maxX then
                Some Rot4.North
            elif cell.z = rect.maxZ + 1 && cell.x >= rect.minX && cell.x <= rect.maxX then
                Some Rot4.South
            else
                None

    let private tryChairFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        match tryInteractionCellFacing thing def cell with
        | Some facing -> Some facing
        | None -> tryTableFacing thing def cell

    let private validRotationsMask predicate =
        let mutable mask = 0

        for rotation in rotations do
            if predicate rotation then
                mask <- mask ||| rotationBit rotation

        mask

    let private hasMultipleRotations mask = mask <> 0 && (mask &&& (mask - 1)) <> 0

    let private preferTargetRotation (mask: int) (targetDef: ThingDef) (targetRotation: Rot4) =
        if mask = 0 || not targetDef.rotatable || not (hasMultipleRotations mask) then
            mask
        else
            let same = rotationBit targetRotation

            if (mask &&& same) <> 0 then
                same
            else
                let opposite = rotationBit targetRotation.Opposite

                if (mask &&& opposite) <> 0 then opposite else mask


    let private tryFacilityConstraintMask
        (map: Map)
        (placingDef: ThingDef)
        (placingPos: IntVec3)
        (target: Thing)
        (targetDef: ThingDef)
        =
        let placingFacility = placingDef.GetCompProperties<CompProperties_Facility>()

        let targetAffected =
            targetDef.GetCompProperties<CompProperties_AffectedByFacilities>()

        let mutable hasConstraint = false
        let mutable allowed = allRotationsMask

        let applyRelation mask =
            if mask <> 0 then
                hasConstraint <- true
                allowed <- allowed &&& mask

        if
            not (isNull placingFacility)
            && not (isNull targetAffected)
            && containsDef targetAffected.linkableFacilities placingDef
        then
            let vanillaMask =
                validRotationsMask (fun rotation ->
                    CompAffectedByFacilities.CanPotentiallyLinkTo_Static(
                        placingDef,
                        placingPos,
                        rotation,
                        targetDef,
                        target.Position,
                        target.Rotation,
                        map
                    ))

            let mask = preferTargetRotation vanillaMask targetDef target.Rotation

            applyRelation mask

        if hasConstraint then Some allowed else None

    let private radiusForFacilityProps (props: CompProperties_Facility) =
        if
            props.mustBePlacedAdjacent
            || props.mustBePlacedAdjacentCardinalToBedHead
            || props.mustBePlacedAdjacentCardinalToAndFacingBedHead
        then
            2
        elif props.mustBePlacedFacingThingLinear then
            max 8 (int (Math.Ceiling(float props.maxDistance)) + 2)
        else
            max 2 (int (Math.Ceiling(float props.maxDistance)) + 2)

    let private facilitySearchRadius (placingDef: ThingDef) =
        let facility = placingDef.GetCompProperties<CompProperties_Facility>()

        if isNull facility then
            0
        else
            radiusForFacilityProps facility + max placingDef.size.x placingDef.size.z

    let private chairCandidateOffsets =
        lazy
            let offsets =
                HashSet<IntVec3> [ IntVec3(-1, 0, 0); IntVec3(1, 0, 0); IntVec3(0, 0, -1); IntVec3(0, 0, 1) ]

            let addInteractionOffset (offset: IntVec3) =
                for rotation in rotations do
                    let rotated = offset.RotatedBy(rotation)

                    offsets.Add(IntVec3(-rotated.x, 0, -rotated.z)) |> ignore

            let defs = DefDatabase<ThingDef>.AllDefsListForReading

            for index = 0 to defs.Count - 1 do
                let def = defs.[index]
                let icon = def.interactionCellIcon

                if not (isNull icon) && isSeat icon then
                    let interactionOffsets = def.multipleInteractionCellOffsets

                    if not (isNull interactionOffsets) && interactionOffsets.Count > 0 then
                        for offsetIndex = 0 to interactionOffsets.Count - 1 do
                            addInteractionOffset interactionOffsets.[offsetIndex]
                    elif def.hasInteractionCell then
                        addInteractionOffset def.interactionCellOffset

            offsets |> Seq.toArray

    let private findFacing (map: Map) (placingDef: ThingDef) (placingCell: IntVec3) (currentRotation: Rot4) =
        let mutable allowed = allRotationsMask
        let mutable hasConstraint = false
        let seen = HashSet<Thing>()

        let applyMask mask =
            hasConstraint <- true
            allowed <- allowed &&& mask

        let processThing (thing: Thing) =
            if seen.Add(thing) then
                let targetDef = buildableThingDef thing

                if isSeat placingDef then
                    match tryChairFacing thing targetDef placingCell with
                    | Some facing -> applyMask (rotationBit facing)
                    | None -> ()

                match tryFacilityConstraintMask map placingDef placingCell thing targetDef with
                | Some mask -> applyMask mask
                | None -> ()

        // Chairs use the sparse interaction/table lookup.
        if isSeat placingDef then
            let offsets = chairCandidateOffsets.Value
            let mutable offsetIndex = 0

            while allowed <> 0 && offsetIndex < offsets.Length do
                let candidateCell = placingCell + offsets.[offsetIndex]

                if candidateCell.InBounds(map) then
                    let things = map.thingGrid.ThingsListAtFast(candidateCell)

                    let mutable thingIndex = 0

                    while allowed <> 0 && thingIndex < things.Count do
                        processThing things.[thingIndex]
                        thingIndex <- thingIndex + 1

                offsetIndex <- offsetIndex + 1

        // Facilities use a bounded ThingGrid search.
        //
        // This covers vanilla and modded furniture using
        // CompFacility / CompAffectedByFacilities without defName
        // checks or a whole-map Thing scan.
        let radius = facilitySearchRadius placingDef

        if allowed <> 0 && radius > 0 then
            let minX = max 0 (placingCell.x - radius)
            let maxX = min (map.Size.x - 1) (placingCell.x + radius)
            let minZ = max 0 (placingCell.z - radius)
            let maxZ = min (map.Size.z - 1) (placingCell.z + radius)

            let mutable z = minZ

            while allowed <> 0 && z <= maxZ do
                let mutable x = minX

                while allowed <> 0 && x <= maxX do
                    let things = map.thingGrid.ThingsListAtFast(IntVec3(x, 0, z))

                    let mutable thingIndex = 0

                    while allowed <> 0 && thingIndex < things.Count do
                        processThing things.[thingIndex]
                        thingIndex <- thingIndex + 1

                    x <- x + 1

                z <- z + 1

        if not hasConstraint || allowed = 0 then
            None
        elif (allowed &&& rotationBit currentRotation) <> 0 then
            Some currentRotation
        else
            rotations
            |> Array.tryFind (fun rotation -> (allowed &&& rotationBit rotation) <> 0)

    let reset (designator: Designator_Build) = (stateFor designator).Reset()

    let tryApply (designator: Designator_Build) =
        match placingRotField, designator.PlacingDef with
        | Some placingRot, (:? ThingDef as placingDef) when shouldHandle placingDef ->

            let map = Find.CurrentMap

            if not (isNull map) then
                let cell = UI.MouseCell()

                let overArchitectPanel =
                    ArchitectCategoryTab.InfoRect.Contains(UI.MousePositionOnUIInverted)

                if not overArchitectPanel && cell.InBounds(map) then
                    let state = stateFor designator

                    if not (state.IsSame(map, cell)) then
                        state.Remember(map, cell)

                        let currentRotation = placingRot.GetValue(designator) :?> Rot4

                        match findFacing map placingDef cell currentRotation with
                        | Some facing when facing <> currentRotation -> placingRot.SetValue(designator, facing)
                        | _ -> ()
        | _ -> ()

[<HarmonyPatch(typeof<Designator_Place>, "Selected")>]
type internal DesignatorPlaceSelectedPatch private () =
    [<HarmonyPostfix>]
    static member Postfix(__instance: Designator_Place) =
        match __instance with
        | :? Designator_Build as designator -> AutoFacing.reset designator
        | _ -> ()

[<HarmonyPatch(typeof<Designator_Place>, "SelectedUpdate")>]
type internal DesignatorPlaceSelectedUpdatePatch private () =
    [<HarmonyPrefix>]
    static member Prefix(__instance: Designator_Place) =
        match __instance with
        | :? Designator_Build as designator -> AutoFacing.tryApply designator
        | _ -> ()

[<StaticConstructorOnStartup>]
type internal AutoFacingBootstrap private () =
    static do Harmony("scarf.chairsnap").PatchAll()
