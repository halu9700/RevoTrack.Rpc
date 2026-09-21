namespace RevoTrack.Rpc.Projects;

public enum RevoTrackRegistrationMode
{
    Track,
    Marker,
}

public enum RevoTrackScanMode
{
    SingleLine,
    ParallelLines,
    CrossLines,
}

public enum RevoTrackObjectType
{
    General,
    Black,
    MetallicShiny,
}

public enum RevoTrackModelFileKind
{
    PointCloud,
    Mesh,
}

public sealed record RevoTrackScanStartOptions
{
    public RevoTrackRegistrationMode RegistrationMode { get; init; } =
        RevoTrackRegistrationMode.Track;

    public double TargetPointDistance { get; init; } = 0.3;

    public RevoTrackScanMode ScanMode { get; init; } = RevoTrackScanMode.CrossLines;

    public RevoTrackObjectType ObjectType { get; init; } = RevoTrackObjectType.General;

    public bool Large { get; init; }
}

public sealed record RevoTrackProcessingState(
    string? ModelId,
    string? ModelName,
    string? Function,
    int Progress,
    int ElapsedTime,
    int RemainTime);
