using Kards.Ui.OrcBridge;
using Kards.Ui.OrcBridge.HandlerDsl;
using Xunit;

namespace Kards.Ui.Tests;

public class DiyTutorialTests
{
    [Theory]
    [InlineData("unit.deployed", "Self", 1, true)]
    [InlineData("unit.deployed", "OwnerSame", 1, false)]
    [InlineData("unit.deployed", "Self", 2, false)]
    [InlineData("turn.end", "Self", 1, false)]
    public void ChecksBlockTimingGuardAndCount(string signal, string guard, int count, bool expected)
    {
        var document = new HandlerDocument();
        document.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(count)));
        Assert.Equal(expected, DiyTutorialLesson.BlockEffectReady(document, signal, guard));
    }

    [Fact]
    public void EmptyCanvasAndExtraActionsDoNotCompleteLesson()
    {
        var document = new HandlerDocument();
        Assert.False(DiyTutorialLesson.BlockEffectReady(document, "unit.deployed", "Self"));
        document.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        document.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        Assert.False(DiyTutorialLesson.BlockEffectReady(document, "unit.deployed", "Self"));
    }

    [Fact]
    public void SavedBlockRoundtripStillCompletesLesson()
    {
        var document = new HandlerDocument();
        document.Actions.Add(new HandlerNode("draw").Set("count", HandlerValue.Num(1)));
        var restored = HandlerDocument.FromJson(document.ToJsonText(HandlerCatalog.Default), HandlerCatalog.Default);
        Assert.True(DiyTutorialLesson.BlockEffectReady(restored, "unit.deployed", "Self"));
    }
}
