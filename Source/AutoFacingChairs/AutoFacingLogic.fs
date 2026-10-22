namespace AutoFacingChairs

open System
open System.Numerics

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
    let isSeatedJoyGiver (baseType: Type) usesSeat (giverClass: Type | null) =
        match giverClass with
        | NonNull giverClass -> usesSeat && baseType.IsAssignableFrom(giverClass)
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

    /// Picks a rotation index (north 0 to west 3) from rotation bit masks, or
    /// None to keep the current rotation. Target rotations are only
    /// preferences, so they yield when no linking rotation satisfies all of
    /// them. Watch areas apply only when nothing else constrains the rotation.
    let resolveRotation hasConstraint allowed preferred watch current =
        let mask =
            if not hasConstraint then watch
            elif (allowed &&& preferred) = 0 then allowed
            else allowed &&& preferred

        if mask = 0 then None
        elif (mask &&& (1 <<< current)) <> 0 then Some current
        else Some(BitOperations.TrailingZeroCount mask)
