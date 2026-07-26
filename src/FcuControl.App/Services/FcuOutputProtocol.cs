using FcuControl.Core;

namespace FcuControl.App.Services;

internal sealed class FcuOutputProtocol
{
    private static readonly byte[] Destination = [0x10, 0xBB];
    private static readonly byte[] SetValuesHeader = [0x02, 0x01, 0, 0, 0, 0, 0, 0, 0, 0x20, 0, 0, 0];
    private static readonly byte[] RefreshHeader = [0x03, 0x01, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly IReadOnlyDictionary<char, bool[]> Characters = new Dictionary<char, bool[]>
    {
        ['0'] = [true, true, true, true, true, true, false],
        ['1'] = [false, true, true, false, false, false, false],
        ['2'] = [true, true, false, true, true, false, true],
        ['3'] = [true, true, true, true, false, false, true],
        ['4'] = [false, true, true, false, false, true, true],
        ['5'] = [true, false, true, true, false, true, true],
        ['6'] = [true, false, true, true, true, true, true],
        ['7'] = [true, true, true, false, false, false, false],
        ['8'] = [true, true, true, true, true, true, true],
        ['9'] = [true, true, true, true, false, true, true],
        ['-'] = [false, false, false, false, false, false, true],
        [' '] = [false, false, false, false, false, false, false]
    };

    private int _messageCounter;

    public IReadOnlyList<byte[]> BuildDisplayMessages(IReadOnlyDictionary<OutputTargetKind, int?> values)
    {
        var setValues = BuildCommand(0x31, SetValuesHeader);
        WriteNumber(setValues, values.GetValueOrDefault(OutputTargetKind.Speed), 3, SpeedDigit(4), SpeedDigit(5), SpeedDigit(6));
        WriteNumber(setValues, values.GetValueOrDefault(OutputTargetKind.Heading), 3, GeneralDigit(7), GeneralDigit(8), GeneralDigit(9));
        WriteNumber(setValues, values.GetValueOrDefault(OutputTargetKind.Altitude), 5,
            GeneralDigit(11), GeneralDigit(12), GeneralDigit(13), GeneralDigit(14), GeneralDigit(15));
        WriteNumber(setValues, values.GetValueOrDefault(OutputTargetKind.VerticalSpeed), 4,
            GeneralDigit(16), GeneralDigit(17), GeneralDigit(18), GeneralDigit(19));
        var refresh = BuildCommand(0x11, RefreshHeader);
        return WrapDisplayCommands([setValues, refresh]);
    }

    public static byte[] BuildLightMessage(byte type, bool enabled)
    {
        return [0x02, Destination[0], Destination[1], 0, 0, 0x03, 0x49, type, enabled ? (byte)1 : (byte)0, 0, 0, 0, 0, 0];
    }

    public static byte[] BuildBrightnessMessage(byte channel, int percent)
    {
        var clamped = Math.Clamp(percent, 0, 100);
        var scaled = (byte)((clamped * 255 + 50) / 100);
        return [0x02, Destination[0], Destination[1], 0, 0, 0x03, 0x49, channel, scaled, 0, 0, 0, 0, 0];
    }

    public static byte[] BuildHeartbeatMessage() =>
        [0x02, 0x01, 0x00, 0, 0, 0x01, 0x00, 0x00, 0, 0, 0, 0, 0, 0];

    private byte[] BuildCommand(int size, byte[] header)
    {
        var command = new byte[size];
        command[0] = Destination[0];
        command[1] = Destination[1];
        header.CopyTo(command, 4);
        return command;
    }

    private IReadOnlyList<byte[]> WrapDisplayCommands(IReadOnlyList<byte[]> commands)
    {
        const int headerEnd = 3;
        var reports = new List<byte[]>();
        var timestamp = GetTimeBytes();
        var message = NewReport();
        var currentIndex = headerEnd;

        for (var commandIndex = 0; commandIndex < commands.Count; commandIndex++)
        {
            var command = commands[commandIndex];
            for (var index = 0; index < command.Length; index++)
            {
                currentIndex++;
                message[currentIndex] = index switch
                {
                    8 => timestamp[0],
                    9 => timestamp[1],
                    10 => timestamp[2],
                    _ => command[index]
                };

                var last = commandIndex == commands.Count - 1 && index == command.Length - 1;
                if (currentIndex == 59 || last)
                {
                    message[headerEnd] = (byte)(currentIndex - headerEnd);
                    reports.Add(message);
                    message = NewReport();
                    currentIndex = headerEnd;
                }
            }
        }

        return reports;
    }

    private byte[] NewReport()
    {
        var report = new byte[64];
        report[0] = 0xF0;
        report[2] = (byte)(Interlocked.Increment(ref _messageCounter) & 0xFF);
        return report;
    }

    private static byte[] GetTimeBytes()
    {
        var now = DateTime.Now;
        return [(byte)(now.Millisecond / 4), (byte)(now.Second * 3), (byte)now.Minute];
    }

    private static void WriteNumber(byte[] command, int? value, int width, params SegmentBit[][] digits)
    {
        var text = !value.HasValue
            ? new string(' ', width)
            : value.Value < 0
                ? new string('-', width)
                : Math.Clamp(value.Value, 0, 100).ToString().PadLeft(width, ' ');
        if (text.Length > width) text = text[^width..];

        for (var digit = 0; digit < width; digit++)
        {
            var segments = Characters.TryGetValue(text[digit], out var found) ? found : Characters[' '];
            for (var segment = 0; segment < 7; segment++)
            {
                var bit = digits[digit][segment];
                var absoluteIndex = 17 + bit.ByteIndex;
                if (segments[segment]) command[absoluteIndex] |= (byte)(1 << bit.BitIndex);
                else command[absoluteIndex] &= (byte)~(1 << bit.BitIndex);
            }
        }
    }

    private static SegmentBit[] SpeedDigit(int dataByte) =>
    [
        new(dataByte, 7), new(dataByte, 6), new(dataByte, 5), new(dataByte, 4),
        new(dataByte, 1), new(dataByte, 3), new(dataByte, 2)
    ];

    private static SegmentBit[] GeneralDigit(int dataByte) =>
    [
        new(dataByte + 1, 3), new(dataByte + 1, 2), new(dataByte + 1, 1), new(dataByte + 1, 0),
        new(dataByte, 5), new(dataByte, 7), new(dataByte, 6)
    ];

    private readonly record struct SegmentBit(int ByteIndex, int BitIndex);
}
