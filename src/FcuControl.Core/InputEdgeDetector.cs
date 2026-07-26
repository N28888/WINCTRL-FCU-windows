namespace FcuControl.Core;

public sealed class InputEdgeDetector
{
    private readonly Dictionary<byte, byte[]> _previousReports = [];
    private readonly Dictionary<string, DateTimeOffset> _lastActivation = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _minimumInterval;

    public InputEdgeDetector(TimeSpan? minimumInterval = null)
    {
        _minimumInterval = minimumInterval ?? TimeSpan.FromMilliseconds(4);
    }

    public IReadOnlyList<HidControlActivation> ProcessReport(ReadOnlySpan<byte> report, DateTimeOffset timestamp)
    {
        if (report.Length < 2)
        {
            return [];
        }

        var reportId = report[0];
        if (!_previousReports.TryGetValue(reportId, out var previous) || previous.Length != report.Length)
        {
            _previousReports[reportId] = report.ToArray();
            return [];
        }

        var activations = new List<HidControlActivation>();
        for (var byteIndex = 1; byteIndex < report.Length; byteIndex++)
        {
            var rising = (byte)(report[byteIndex] & ~previous[byteIndex]);
            if (rising == 0)
            {
                continue;
            }

            for (var bit = 0; bit < 8; bit++)
            {
                if ((rising & (1 << bit)) == 0)
                {
                    continue;
                }

                var controlId = $"R{reportId:X2}:B{byteIndex:X2}:b{bit}";
                if (_lastActivation.TryGetValue(controlId, out var last) && timestamp - last < _minimumInterval)
                {
                    continue;
                }

                _lastActivation[controlId] = timestamp;
                activations.Add(new HidControlActivation(controlId, InputTrigger.Press, timestamp));
            }
        }

        report.CopyTo(previous);
        return activations;
    }

    public void Reset()
    {
        _previousReports.Clear();
        _lastActivation.Clear();
    }
}

