using RevoTrack.Rpc.Files;

namespace RevoTrack.Rpc.Projects;

public interface IRevoTrackProjectManager : IAsyncDisposable
{
    RevoTrackProjectInfo? CurrentProject { get; }

    Task<RevoTrackProjectInfo> CreateProjectAsync(
        string projectName,
        CancellationToken cancellationToken = default);

    Task<RevoTrackProjectDeleteResult> DeleteCurrentProjectAsync(
        bool closeProjectFirst = true,
        CancellationToken cancellationToken = default);

    Task ForgetCurrentProjectAsync(CancellationToken cancellationToken = default);

    Task StartScanAsync(
        RevoTrackScanStartOptions? options = null,
        CancellationToken cancellationToken = default);

    Task PauseScanAsync(CancellationToken cancellationToken = default);

    Task ResumeScanAsync(CancellationToken cancellationToken = default);

    Task StopScanAsync(CancellationToken cancellationToken = default);

    Task<RevoTrackProcessingState?> GetProcessingStateAsync(
        CancellationToken cancellationToken = default);

    Task<RevoTrackProcessingState?> RunOneClickEditAsync(
        bool waitForCompletion = true,
        TimeSpan? pollingInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    Task<RevoTrackDownloadResult> SavePointCloudAsync(
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RevoTrackDownloadResult> SaveMeshAsync(
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RevoTrackDownloadResult> SaveModelFileAsync(
        RevoTrackModelFileKind fileKind,
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<RevoTrackProjectDeleteResult> CloseAndDeleteCurrentProjectAsync(
        CancellationToken cancellationToken = default);
}
