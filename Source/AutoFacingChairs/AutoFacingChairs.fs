namespace AutoFacingChairs

open System.Runtime.CompilerServices
open HarmonyLib
open RimWorld
open Verse

[<AllowNullLiteral>]
type ChairFacingSurfaceExtension() =
    inherit DefModExtension()

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

type internal FacingSearch =
    | NotFound
    | Found of Rot4
    | Conflict

module internal ChairFacing =
    let private states = ConditionalWeakTable<Designator_Build, PlacementState>()

    let private placingRotField =
        AccessTools.Field(typeof<Designator_Place>, "placingRot") |> Option.ofObj

    let private furnitureGroups =
        [| ThingRequestGroup.BuildingArtificial
           ThingRequestGroup.Blueprint
           ThingRequestGroup.BuildingFrame |]

    let private stateFor (designator: Designator_Build) =
        states.GetOrCreateValue(designator)

    let private isChair (def: ThingDef) =
        def.rotatable && not (isNull def.building) && def.building.isSittable

    let private buildableThingDef (thing: Thing) =
        match thing.def.entityDefToBuild with
        | :? ThingDef as def -> def
        | _ -> thing.def

    let private hasInteractionCellAt
        (def: ThingDef)
        (center: IntVec3)
        (rotation: Rot4)
        (cell: IntVec3)
        =
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
            && isChair icon
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

    let private hasPerimeterSeating (def: ThingDef) =
        def.surfaceType = SurfaceType.Eat
        || def.HasModExtension<ChairFacingSurfaceExtension>()

    let private tryPerimeterFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        if not (hasPerimeterSeating def) then
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

    let private tryFurnitureFacing (thing: Thing) (def: ThingDef) (cell: IntVec3) =
        match tryInteractionCellFacing thing def cell with
        | Some facing -> Some facing
        | None -> tryPerimeterFacing thing def cell

    let private mergeFacing current candidate =
        match current with
        | NotFound -> Found candidate
        | Found facing when facing = candidate -> current
        | Found _ -> Conflict
        | Conflict -> Conflict

    let private findFacingInGroup
        (map: Map)
        (chairCell: IntVec3)
        (group: ThingRequestGroup)
        initial
        =
        let things = map.listerThings.ThingsInGroup(group)
        let mutable result = initial
        let mutable index = 0
        let mutable searching = true

        while searching && index < things.Count do
            let thing = things.[index]
            let targetDef = buildableThingDef thing

            match tryFurnitureFacing thing targetDef chairCell with
            | Some facing ->
                result <- mergeFacing result facing

                match result with
                | Conflict -> searching <- false
                | _ -> ()
            | None -> ()

            index <- index + 1

        result

    let private findFacing (map: Map) (chairCell: IntVec3) =
        let mutable result = NotFound
        let mutable index = 0

        while index < furnitureGroups.Length do
            result <- findFacingInGroup map chairCell furnitureGroups.[index] result

            match result with
            | Conflict -> index <- furnitureGroups.Length
            | _ -> index <- index + 1

        match result with
        | Found facing -> Some facing
        | NotFound
        | Conflict -> None

    let reset (designator: Designator_Build) =
        let state = stateFor designator
        state.Reset()

    let tryApply (designator: Designator_Build) =
        match placingRotField, designator.PlacingDef with
        | Some placingRot, (:? ThingDef as chairDef) when isChair chairDef ->
            let map = Find.CurrentMap

            if not (isNull map) then
                let cell = UI.MouseCell()
                let overArchitectPanel =
                    ArchitectCategoryTab.InfoRect.Contains(UI.MousePositionOnUIInverted)

                if not overArchitectPanel && cell.InBounds(map) then
                    let state = stateFor designator

                    if not (state.IsSame(map, cell)) then
                        state.Remember(map, cell)

                        match findFacing map cell with
                        | Some facing -> placingRot.SetValue(designator, facing)
                        | None -> ()
        | _ -> ()

[<HarmonyPatch(typeof<Designator_Place>, "Selected")>]
type internal DesignatorPlaceSelectedPatch private () =
    [<HarmonyPostfix>]
    static member Postfix(__instance: Designator_Place) =
        match __instance with
        | :? Designator_Build as designator -> ChairFacing.reset designator
        | _ -> ()

[<HarmonyPatch(typeof<Designator_Place>, "SelectedUpdate")>]
type internal DesignatorPlaceSelectedUpdatePatch private () =
    [<HarmonyPrefix>]
    static member Prefix(__instance: Designator_Place) =
        match __instance with
        | :? Designator_Build as designator -> ChairFacing.tryApply designator
        | _ -> ()

[<StaticConstructorOnStartup>]
type internal AutoFacingChairsMod private () =
    static do Harmony("scarf.chairsnap").PatchAll()
