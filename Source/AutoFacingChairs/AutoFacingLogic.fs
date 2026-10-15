namespace AutoFacingChairs

open System

type internal CardinalFacing =
    | North
    | East
    | South
    | West

type internal FacilitySearchKind =
    | CardinalAdjacent
    | EightWayAdjacent
    | BoundedOrListed

module internal AutoFacingLogic =
    let isAdjacentSeatJoyGiver (baseType: Type) requireChair (giverClass: Type | null) =
        match giverClass with
        | NonNull giverClass -> requireChair && baseType.IsAssignableFrom(giverClass)
        | Null -> false

    let tryAdjacentFacing minX maxX minZ maxZ x z =
        if x = minX - 1 && z >= minZ && z <= maxZ then
            Some CardinalFacing.East
        elif x = maxX + 1 && z >= minZ && z <= maxZ then
            Some CardinalFacing.West
        elif z = minZ - 1 && x >= minX && x <= maxX then
            Some CardinalFacing.North
        elif z = maxZ + 1 && x >= minX && x <= maxX then
            Some CardinalFacing.South
        else
            None

    let facilitySearchKind mustBePlacedAdjacent bedHead bedHeadAndFacing =
        if mustBePlacedAdjacent then
            FacilitySearchKind.EightWayAdjacent
        elif bedHead || bedHeadAndFacing then
            FacilitySearchKind.CardinalAdjacent
        else
            FacilitySearchKind.BoundedOrListed

    let preferListedTargetScan targetCount gridCellCount =
        targetCount >= 0L && gridCellCount >= 0L && targetCount * 4L <= gridCellCount
