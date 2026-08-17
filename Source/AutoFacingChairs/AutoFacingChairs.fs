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
    let seen = HashSet<Thing>()

    member _.Seen = seen

    member _.Reset() =
        hasLastCell <- false
        lastMap <- null
        seen.Clear()

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

    let private adjacentSeatTargets =
        lazy
            let targets = HashSet<ThingDef>()
            let joyGivers = DefDatabase<JoyGiverDef>.AllDefsListForReading
            let baseType = typeof<JoyGiver_InteractBuildingSitAdjacent>

            for index = 0 to joyGivers.Count - 1 do
                let joyGiver = joyGivers.[index]

                if
                    AutoFacingLogic.isAdjacentSeatJoyGiver baseType joyGiver.requireChair joyGiver.giverClass
                    && not (isNull joyGiver.thingDefs)
                then
                    for thingIndex = 0 to joyGiver.thingDefs.Count - 1 do
                        let def = joyGiver.thingDefs.[thingIndex]

                        if not (isNull def) then
                            targets.Add(def) |> ignore

            targets

    type private PlacementProfile =
        { Facility: CompProperties_Facility
          FacilityTargets: HashSet<ThingDef>
          FacilityMapDefs: ThingDef array
          FacilitySearchKind: FacilitySearchKind
          FacilityGridRadius: int }

    let private profiles = Dictionary<ThingDef, PlacementProfile>()
    let private emptyTargetDefs = HashSet<ThingDef>()
    let private emptyMapDefs: ThingDef array = [||]

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

    let private toRot4 facing =
        match facing with
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
        match tryInteractionCellFacing thing def cell with
        | Some facing -> Some facing
        | None -> tryAdjacentSeatFacing thing def cell

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
        let mutable vanillaMask = 0

        for rotation in rotations do
            if
                CompAffectedByFacilities.CanPotentiallyLinkTo_Static(
                    placingDef,
                    placingPos,
                    rotation,
                    targetDef,
                    target.Position,
                    target.Rotation,
                    map
                )
            then
                vanillaMask <- vanillaMask ||| rotationBit rotation

        if vanillaMask = 0 then
            None
        else
            Some(preferTargetRotation vanillaMask targetDef target.Rotation)

    let private radiusForFacilityProps (props: CompProperties_Facility) =
        if props.mustBePlacedFacingThingLinear then
            max 8 (int (Math.Ceiling(float props.maxDistance)) + 2)
        else
            max 2 (int (Math.Ceiling(float props.maxDistance)) + 2)

    let private addMapDef (defs: HashSet<ThingDef>) (def: ThingDef) =
        if not (isNull def) then
            defs.Add(def) |> ignore

    let private buildProfile (def: ThingDef) =
        let facility = def.GetCompProperties<CompProperties_Facility>()

        if isNull facility then
            { Facility = null
              FacilityTargets = emptyTargetDefs
              FacilityMapDefs = emptyMapDefs
              FacilitySearchKind = FacilitySearchKind.BoundedOrListed
              FacilityGridRadius = 0 }
        else
            let targets = HashSet<ThingDef>()
            let mapDefs = HashSet<ThingDef>()

            if not (isNull facility.linkableBuildings) then
                for index = 0 to facility.linkableBuildings.Count - 1 do
                    let target = facility.linkableBuildings.[index]

                    if not (isNull target) && targets.Add(target) then
                        addMapDef mapDefs target
                        addMapDef mapDefs target.blueprintDef
                        addMapDef mapDefs target.installBlueprintDef
                        addMapDef mapDefs target.frameDef

            let searchKind =
                AutoFacingLogic.facilitySearchKind
                    facility.mustBePlacedAdjacent
                    facility.mustBePlacedAdjacentCardinalToBedHead
                    facility.mustBePlacedAdjacentCardinalToAndFacingBedHead

            let gridRadius =
                match searchKind with
                | FacilitySearchKind.BoundedOrListed -> radiusForFacilityProps facility + max def.size.x def.size.z
                | _ -> 0

            { Facility = facility
              FacilityTargets = targets
              FacilityMapDefs = mapDefs |> Seq.toArray
              FacilitySearchKind = searchKind
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

            for index = 0 to things.Count - 1 do
                seen.Add(things.[index]) |> ignore

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

    let private findFacing
        (state: PlacementState)
        (map: Map)
        (placingDef: ThingDef)
        (placingCell: IntVec3)
        (currentRotation: Rot4)
        =
        let mutable allowed = allRotationsMask
        let mutable hasConstraint = false
        let seen = state.Seen
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
                match tryFacilityConstraintMask map placingDef placingCell thing targetDef with
                | Some mask -> applyMask mask
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

        if
            allowed <> 0
            && not (isNull profile.Facility)
            && profile.FacilityTargets.Count > 0
        then
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

                        match findFacing state map placingDef cell currentRotation with
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
