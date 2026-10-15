namespace AutoFacingChairs.Tests

open System
open System.IO
open System.Text
open AutoFacingChairs
open Xunit

type AutoFacingLogicTests() =
    [<Fact>]
    member _.``seated joy giver accepts the base type``() =
        let actual = AutoFacingLogic.isSeatedJoyGiver typeof<Stream> true typeof<Stream>

        Assert.True(actual)

    [<Fact>]
    member _.``seated joy giver accepts derived types``() =
        let actual =
            AutoFacingLogic.isSeatedJoyGiver typeof<Stream> true typeof<MemoryStream>

        Assert.True(actual)

    [<Fact>]
    member _.``seated joy giver rejects definitions that do not use a seat``() =
        let actual =
            AutoFacingLogic.isSeatedJoyGiver typeof<Stream> false typeof<MemoryStream>

        Assert.False(actual)

    [<Fact>]
    member _.``seated joy giver rejects unrelated giver types``() =
        let actual =
            AutoFacingLogic.isSeatedJoyGiver typeof<Stream> true typeof<StringBuilder>

        Assert.False(actual)

    [<Fact>]
    member _.``seated joy giver rejects a missing giver type``() =
        let actual = AutoFacingLogic.isSeatedJoyGiver typeof<Stream> true null

        Assert.False(actual)

    [<Fact>]
    member _.``left edge faces east``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 9 21
        Assert.Equal<CardinalFacing option>(Some CardinalFacing.East, actual)

    [<Fact>]
    member _.``right edge faces west``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 13 21
        Assert.Equal<CardinalFacing option>(Some CardinalFacing.West, actual)

    [<Fact>]
    member _.``bottom edge faces north``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 11 19
        Assert.Equal<CardinalFacing option>(Some CardinalFacing.North, actual)

    [<Fact>]
    member _.``top edge faces south``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 11 23
        Assert.Equal<CardinalFacing option>(Some CardinalFacing.South, actual)

    [<Fact>]
    member _.``diagonal cell is not an adjacent seat position``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 9 19
        Assert.Equal<CardinalFacing option>(None, actual)

    [<Fact>]
    member _.``inside cell is not an adjacent seat position``() =
        let actual = AutoFacingLogic.tryAdjacentFacing 10 12 20 22 11 21
        Assert.Equal<CardinalFacing option>(None, actual)

    [<Fact>]
    member _.``bed-head facilities use cardinal perimeter search``() =
        let actual = AutoFacingLogic.facilitySearchKind false true false
        Assert.Equal(FacilitySearchKind.CardinalAdjacent, actual)

    [<Fact>]
    member _.``facing bed-head facilities use cardinal perimeter search``() =
        let actual = AutoFacingLogic.facilitySearchKind false false true
        Assert.Equal(FacilitySearchKind.CardinalAdjacent, actual)

    [<Fact>]
    member _.``generic adjacent facilities use eight-way perimeter search``() =
        let actual = AutoFacingLogic.facilitySearchKind true false false
        Assert.Equal(FacilitySearchKind.EightWayAdjacent, actual)

    [<Fact>]
    member _.``generic adjacency takes precedence over bed-head adjacency``() =
        let actual = AutoFacingLogic.facilitySearchKind true true true
        Assert.Equal(FacilitySearchKind.EightWayAdjacent, actual)

    [<Fact>]
    member _.``non-adjacent facilities use bounded or listed search``() =
        let actual = AutoFacingLogic.facilitySearchKind false false false
        Assert.Equal(FacilitySearchKind.BoundedOrListed, actual)

    [<Theory>]
    [<InlineData(0L, 529L, true)>]
    [<InlineData(100L, 529L, true)>]
    [<InlineData(132L, 529L, true)>]
    [<InlineData(133L, 529L, false)>]
    [<InlineData(500L, 529L, false)>]
    member _.``listed scan is used only when four rotation checks beat grid probes``
        (targetCount: int64, gridCellCount: int64, expected: bool)
        =
        let actual = AutoFacingLogic.preferListedTargetScan targetCount gridCellCount
        Assert.Equal(expected, actual)

    [<Fact>]
    member _.``hard constraints win over a watch area``() =
        let actual = AutoFacingLogic.resolveRotation true 0b0010 0b1111 0b0001 2
        Assert.Equal<int option>(Some 1, actual)

    [<Fact>]
    member _.``conflicting hard constraints keep the current rotation despite a watch area``() =
        let actual = AutoFacingLogic.resolveRotation true 0 0b1111 0b0001 2
        Assert.Equal<int option>(None, actual)

    [<Fact>]
    member _.``watch area turns an unconstrained seat``() =
        let actual = AutoFacingLogic.resolveRotation false 0b1111 0b1111 0b0100 0
        Assert.Equal<int option>(Some 2, actual)

    [<Fact>]
    member _.``unconstrained seat outside a watch area keeps its rotation``() =
        let actual = AutoFacingLogic.resolveRotation false 0b1111 0b1111 0 3
        Assert.Equal<int option>(None, actual)

    [<Fact>]
    member _.``current rotation stays when it satisfies the mask``() =
        let actual = AutoFacingLogic.resolveRotation false 0b1111 0b1111 0b1010 3
        Assert.Equal<int option>(Some 3, actual)

    [<Fact>]
    member _.``target preferences narrow the allowed rotations``() =
        let actual = AutoFacingLogic.resolveRotation true 0b1010 0b1000 0 1
        Assert.Equal<int option>(Some 3, actual)

    [<Fact>]
    member _.``target preferences yield when no allowed rotation satisfies them``() =
        let actual = AutoFacingLogic.resolveRotation true 0b1010 0b0101 0 0
        Assert.Equal<int option>(Some 1, actual)
