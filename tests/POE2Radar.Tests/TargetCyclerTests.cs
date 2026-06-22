using POE2Radar.Core.Navigation;

public class TargetCyclerTests
{
    private static readonly string[] L = { "a", "b", "c" };

    [Fact] public void Next_Advances() => Assert.Equal("b", TargetCycler.Next(L, "a"));
    [Fact] public void Next_WrapsToFirst() => Assert.Equal("a", TargetCycler.Next(L, "c"));
    [Fact] public void Next_NullCurrent_StartsAtTop() => Assert.Equal("a", TargetCycler.Next(L, null));
    [Fact] public void Next_MissingCurrent_StartsAtTop() => Assert.Equal("a", TargetCycler.Next(L, "x"));
    [Fact] public void Next_Empty_Null() => Assert.Null(TargetCycler.Next(System.Array.Empty<string>(), "a"));

    [Fact] public void Prev_Retreats() => Assert.Equal("a", TargetCycler.Prev(L, "b"));
    [Fact] public void Prev_WrapsToLast() => Assert.Equal("c", TargetCycler.Prev(L, "a"));
    [Fact] public void Prev_NullCurrent_StartsAtBottom() => Assert.Equal("c", TargetCycler.Prev(L, null));
    [Fact] public void Prev_MissingCurrent_StartsAtBottom() => Assert.Equal("c", TargetCycler.Prev(L, "x"));

    [Fact] public void AtIndex_OneBased() => Assert.Equal("a", TargetCycler.AtIndex(L, 1));
    [Fact] public void AtIndex_Last() => Assert.Equal("c", TargetCycler.AtIndex(L, 3));
    [Fact] public void AtIndex_OutOfRange_Null() => Assert.Null(TargetCycler.AtIndex(L, 4));
    [Fact] public void AtIndex_Zero_Null() => Assert.Null(TargetCycler.AtIndex(L, 0));

    [Fact] public void Single_NextWrapsToSelf() => Assert.Equal("only", TargetCycler.Next(new[] { "only" }, "only"));
}
