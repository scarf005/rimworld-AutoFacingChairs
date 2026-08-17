namespace AutoFacingChairs.Tests

open System
open System.IO
open System.Text
open AutoFacingChairs
open Xunit

type AutoFacingLogicTests() =
    [<Fact>]
    member _.``adjacent seat joy giver accepts the base type``() =
        let actual =
            AutoFacingLogic.isAdjacentSeatJoyGiver typeof<Stream> true typeof<Stream>

        Assert.True(actual)

    [<Fact>]
    member _.``adjacent seat joy giver accepts derived types``() =
        let actual =
            AutoFacingLogic.isAdjacentSeatJoyGiver typeof<Stream> true typeof<MemoryStream>

        Assert.True(actual)

    [<Fact>]
    member _.``adjacent seat joy giver rejects definitions that do not require a chair``() =
        let actual =
            AutoFacingLogic.isAdjacentSeatJoyGiver typeof<Stream> false typeof<MemoryStream>

        Assert.False(actual)

    [<Fact>]
    member _.``adjacent seat joy giver rejects unrelated giver types``() =
        let actual =
            AutoFacingLogic.isAdjacentSeatJoyGiver typeof<Stream> true typeof<StringBuilder>

        Assert.False(actual)

    [<Fact>]
    member _.``adjacent seat joy giver rejects a missing giver type``() =
        let actual =
            AutoFacingLogic.isAdjacentSeatJoyGiver typeof<Stream> true (Unchecked.defaultof<Type>)

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
