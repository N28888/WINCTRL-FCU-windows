using FcuControl.App.Services;
using FcuControl.Core;

namespace FcuControl.Core.Tests;

public sealed class FcuOutputProtocolTests
{
    [Fact]
    public void DisplayUpdate_IsSplitIntoValidHidReports()
    {
        var protocol = new FcuOutputProtocol();
        var values = Enum.GetValues<OutputTargetKind>()
            .Where(target => target <= OutputTargetKind.VerticalSpeed)
            .ToDictionary(target => target, _ => (int?)100);

        var reports = protocol.BuildDisplayMessages(values);

        Assert.Equal(2, reports.Count);
        Assert.All(reports, report =>
        {
            Assert.Equal(64, report.Length);
            Assert.Equal(0xF0, report[0]);
            Assert.InRange(report[3], 1, 56);
        });
        Assert.NotEqual(reports[0][2], reports[1][2]);
    }

    [Fact]
    public void DisplayValuesCanBeComparedWithoutTransportCounterOrTimestamp()
    {
        int?[] first = [25, 80, null, -1];
        int?[] same = [25, 80, null, -1];
        int?[] changed = [26, 80, null, -1];

        Assert.True(first.AsSpan().SequenceEqual(same));
        Assert.False(first.AsSpan().SequenceEqual(changed));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 128)]
    [InlineData(100, 255)]
    [InlineData(101, 255)]
    public void BrightnessMessage_ClampsAndScalesToOneByte(int percent, int expected)
    {
        var message = FcuOutputProtocol.BuildBrightnessMessage(0x09, percent);

        Assert.Equal(14, message.Length);
        Assert.Equal(0x49, message[6]);
        Assert.Equal(0x09, message[7]);
        Assert.Equal(expected, message[8]);
    }

    [Fact]
    public void LocLightMessage_UsesChannelThreeAndZeroToTurnOff()
    {
        var enabled = FcuOutputProtocol.BuildLightMessage(0x03, true);
        var disabled = FcuOutputProtocol.BuildLightMessage(0x03, false);

        Assert.Equal(0x03, enabled[7]);
        Assert.Equal(1, enabled[8]);
        Assert.Equal(0, disabled[8]);
    }

    [Fact]
    public void HeartbeatMessageMatchesTheWinwingRootDeviceCommand()
    {
        var message = FcuOutputProtocol.BuildHeartbeatMessage();

        Assert.Equal(14, message.Length);
        Assert.Equal(new byte[] { 0x02, 0x01, 0x00, 0, 0, 0x01, 0x00, 0x00 }, message[..8]);
        Assert.All(message[8..], value => Assert.Equal(0, value));
    }
}
