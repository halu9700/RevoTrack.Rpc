namespace RevoTrack.Rpc.Files;

public interface IRevoTrackFileClient : IAsyncDisposable
{
    Task<IReadOnlyList<RevoTrackModelInfo>> GetModelsAsync(
        IReadOnlyCollection<string>? modelTypes = null,
        CancellationToken cancellationToken = default);

    Task<RevoTrackDownloadResult> DownloadFileAsync(
        Uri url,
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RevoTrackDownloadResult>> DownloadModelAsync(
        RevoTrackModelInfo model,
        string destinationDirectory,
        RevoTrackModelFileSelection selection = RevoTrackModelFileSelection.All,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
