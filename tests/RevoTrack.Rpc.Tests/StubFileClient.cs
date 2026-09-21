using RevoTrack.Rpc.Files;

namespace RevoTrack.Rpc.Tests;

internal sealed class StubFileClient : IRevoTrackFileClient
{
    public Func<IReadOnlyCollection<string>?, IReadOnlyList<RevoTrackModelInfo>>
        ModelsFactory { get; init; } = _ => [];

    public Func<Uri, string, bool, RevoTrackDownloadResult> DownloadHandler { get; init; } =
        (uri, path, _) => new RevoTrackDownloadResult(uri, path, 0);

    public List<IReadOnlyCollection<string>?> RequestedModelTypes { get; } = [];

    public List<(Uri Uri, string DestinationPath, bool Overwrite)> Downloads { get; } = [];

    public Task<IReadOnlyList<RevoTrackModelInfo>> GetModelsAsync(
        IReadOnlyCollection<string>? modelTypes = null,
        CancellationToken cancellationToken = default)
    {
        RequestedModelTypes.Add(modelTypes);
        return Task.FromResult(ModelsFactory(modelTypes));
    }

    public Task<RevoTrackDownloadResult> DownloadFileAsync(
        Uri url,
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Downloads.Add((url, destinationPath, overwrite));
        return Task.FromResult(DownloadHandler(url, destinationPath, overwrite));
    }

    public Task<IReadOnlyList<RevoTrackDownloadResult>> DownloadModelAsync(
        RevoTrackModelInfo model,
        string destinationDirectory,
        RevoTrackModelFileSelection selection = RevoTrackModelFileSelection.All,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RevoTrackDownloadResult>>([]);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
