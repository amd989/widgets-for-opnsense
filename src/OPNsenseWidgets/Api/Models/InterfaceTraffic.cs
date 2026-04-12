namespace OPNsenseWidgets.Api.Models;

public sealed class InterfaceStats
{
    public required string DeviceName { get; init; }
    public string Name { get; init; } = string.Empty;
    public long BytesReceived { get; init; }
    public long BytesTransmitted { get; init; }
    public long PacketsReceived { get; init; }
    public long PacketsTransmitted { get; init; }
}

public sealed record TrafficSample(
    DateTime Timestamp,
    double InBitsPerSecond,
    double OutBitsPerSecond);

public sealed class TrafficData
{
    public required List<TrafficSample> History { get; init; }
    public required string InterfaceName { get; init; }
    public double CurrentInBps { get; init; }
    public double CurrentOutBps { get; init; }
    public long TotalBytesIn { get; init; }
    public long TotalBytesOut { get; init; }
    public bool HasData { get; init; }
}
