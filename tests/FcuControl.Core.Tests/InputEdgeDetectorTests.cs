using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class InputEdgeDetectorTests
{
    [Fact]
    public void FirstReportEstablishesBaselineWithoutActivation()
    {
        var detector = new InputEdgeDetector();

        var result = detector.ProcessReport([0x01, 0x04], DateTimeOffset.UtcNow);

        Assert.Empty(result);
    }

    [Fact]
    public void RisingEdgeActivatesOnceUntilReleased()
    {
        var detector = new InputEdgeDetector(TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        detector.ProcessReport([0x01, 0x00], now);

        var pressed = detector.ProcessReport([0x01, 0x04], now.AddMilliseconds(10));
        var held = detector.ProcessReport([0x01, 0x04], now.AddMilliseconds(20));
        detector.ProcessReport([0x01, 0x00], now.AddMilliseconds(30));
        var pressedAgain = detector.ProcessReport([0x01, 0x04], now.AddMilliseconds(40));

        Assert.Single(pressed);
        Assert.Equal("R01:B01:b2", pressed[0].ControlId);
        Assert.Empty(held);
        Assert.Single(pressedAgain);
    }

    [Fact]
    public void MultipleRisingBitsProduceStableIdentifiers()
    {
        var detector = new InputEdgeDetector(TimeSpan.Zero);
        var now = DateTimeOffset.UtcNow;
        detector.ProcessReport([0x02, 0x00, 0x00], now);

        var result = detector.ProcessReport([0x02, 0x81, 0x02], now.AddMilliseconds(10));

        Assert.Equal(3, result.Count);
        Assert.Contains(result, value => value.ControlId == "R02:B01:b0");
        Assert.Contains(result, value => value.ControlId == "R02:B01:b7");
        Assert.Contains(result, value => value.ControlId == "R02:B02:b1");
    }

    [Fact]
    public void RapidRepeatedEdgeInsideDebounceWindowIsIgnored()
    {
        var detector = new InputEdgeDetector(TimeSpan.FromMilliseconds(10));
        var now = DateTimeOffset.UtcNow;
        detector.ProcessReport([0x01, 0x00], now);

        var first = detector.ProcessReport([0x01, 0x01], now.AddMilliseconds(1));
        detector.ProcessReport([0x01, 0x00], now.AddMilliseconds(2));
        var bounce = detector.ProcessReport([0x01, 0x01], now.AddMilliseconds(3));
        detector.ProcessReport([0x01, 0x00], now.AddMilliseconds(4));
        var later = detector.ProcessReport([0x01, 0x01], now.AddMilliseconds(12));

        Assert.Single(first);
        Assert.Empty(bounce);
        Assert.Single(later);
    }
}
