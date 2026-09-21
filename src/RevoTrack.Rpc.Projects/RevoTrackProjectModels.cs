namespace RevoTrack.Rpc.Projects;

public sealed record RevoTrackProjectInfo(
    string Name,
    string DirectoryPath,
    DateTimeOffset CreatedAtUtc);

public sealed record RevoTrackProjectDeleteResult(
    bool Deleted,
    string? ProjectName,
    string? DirectoryPath,
    long? ReclaimedBytes,
    bool CloseInvoked);
