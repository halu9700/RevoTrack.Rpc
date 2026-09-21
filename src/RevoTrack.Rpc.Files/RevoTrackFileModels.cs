namespace RevoTrack.Rpc.Files;

public sealed record RevoTrackTextureFiles(Uri? Jpg, Uri? Ply);

public sealed record RevoTrackModelInfo(
    string ModelId,
    string ModelName,
    Uri? PointCloudUri,
    IReadOnlyList<Uri> MeshUris,
    RevoTrackTextureFiles? Texture);

[Flags]
public enum RevoTrackModelFileSelection
{
    None = 0,
    PointCloud = 1,
    Mesh = 2,
    TextureJpg = 4,
    TexturePly = 8,
    Textures = TextureJpg | TexturePly,
    All = PointCloud | Mesh | Textures,
}

public sealed record RevoTrackDownloadProgress(
    Uri SourceUri,
    string DestinationPath,
    long BytesReceived,
    long? TotalBytes)
{
    public double? Percentage => TotalBytes is > 0
        ? (double)BytesReceived / TotalBytes.Value * 100
        : null;
}

public sealed record RevoTrackDownloadResult(
    Uri SourceUri,
    string DestinationPath,
    long BytesWritten);
