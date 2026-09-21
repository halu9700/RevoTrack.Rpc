using System.Text.Json;
using RevoTrack.Rpc;
using RevoTrack.Rpc.Files;

namespace RevoTrack.Rpc.Projects;

public sealed class RevoTrackProjectManager : IRevoTrackProjectManager
{
    private static readonly JsonSerializerOptions StateJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string[] ReservedWindowsNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    private readonly IRevoTrackRpcClient _rpcClient;
    private readonly RevoTrackProjectOptions _options;
    private readonly SemaphoreSlim _projectGate = new(1, 1);
    private IRevoTrackFileClient? _fileClient;
    private bool _ownsFileClient;
    private RevoTrackProjectInfo? _currentProject;
    private bool _disposed;

    public RevoTrackProjectManager(IRevoTrackRpcClient rpcClient)
        : this(rpcClient, new RevoTrackProjectOptions(), fileClient: null)
    {
    }

    public RevoTrackProjectManager(
        IRevoTrackRpcClient rpcClient,
        RevoTrackProjectOptions options)
        : this(rpcClient, options, fileClient: null)
    {
    }

    public RevoTrackProjectManager(
        IRevoTrackRpcClient rpcClient,
        RevoTrackProjectOptions options,
        IRevoTrackFileClient? fileClient)
    {
        _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _fileClient = fileClient;
        _options.Validate();
        LoadCurrentProject();
    }

    public RevoTrackProjectInfo? CurrentProject => Volatile.Read(ref _currentProject);

    public async Task<RevoTrackProjectInfo> CreateProjectAsync(
        string projectName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateProjectName(projectName);

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentProject is not null)
            {
                throw new InvalidOperationException(
                    $"Project '{CurrentProject.Name}' is already recorded. "
                    + "Delete or forget it before creating another project.");
            }

            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.FileNew,
                new { name = projectName },
                cancellationToken).ConfigureAwait(false);

            var project = CreateProjectInfo(projectName, DateTimeOffset.UtcNow);
            Volatile.Write(ref _currentProject, project);
            await SaveCurrentProjectAsync(project, cancellationToken).ConfigureAwait(false);
            return project;
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task<RevoTrackProjectDeleteResult> DeleteCurrentProjectAsync(
        bool closeProjectFirst = true,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var project = CurrentProject;
            if (project is null)
            {
                return new RevoTrackProjectDeleteResult(
                    Deleted: false,
                    ProjectName: null,
                    DirectoryPath: null,
                    ReclaimedBytes: null,
                    CloseInvoked: false);
            }

            ValidateProjectName(project.Name);
            var projectDirectory = ResolveProjectDirectory(project.Name);
            var closeInvoked = false;

            if (closeProjectFirst)
            {
                if (!_rpcClient.IsConnected)
                {
                    throw new RevoTrackRpcException(
                        "The RevoTrack client must be connected to close the project before deletion.");
                }

                closeInvoked = true;

                try
                {
                    await _rpcClient.InvokeAsync(
                        RevoTrackRpcMethods.FileBackToHome,
                        parameters: null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (RevoTrackRpcException exception)
                    when (exception.ErrorCode == RevoTrackRpcErrorCode.ProjectNotOpen)
                {
                }

                await Task.Delay(_options.CloseProjectDelay, cancellationToken)
                    .ConfigureAwait(false);
            }

            long? reclaimedBytes = null;
            var deleted = false;

            if (Directory.Exists(projectDirectory))
            {
                EnsureDirectoryIsNotReparsePoint(projectDirectory);
                reclaimedBytes = TryGetDirectorySize(projectDirectory);
                await DeleteDirectoryWithRetryAsync(projectDirectory, cancellationToken)
                    .ConfigureAwait(false);
                deleted = true;
            }

            ClearCurrentProject();

            return new RevoTrackProjectDeleteResult(
                Deleted: deleted,
                ProjectName: project.Name,
                DirectoryPath: projectDirectory,
                ReclaimedBytes: reclaimedBytes,
                CloseInvoked: closeInvoked);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task ForgetCurrentProjectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearCurrentProject();
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task StartScanAsync(
        RevoTrackScanStartOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();
        options ??= new RevoTrackScanStartOptions();
        ValidateScanOptions(options);

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.ScanStart,
                new
                {
                    registerMode = GetRegistrationModeValue(options.RegistrationMode),
                    targetPointDistance = options.TargetPointDistance,
                    scanMode = GetScanModeValue(options.ScanMode),
                    objectType = GetObjectTypeValue(options.ObjectType),
                    large = options.Large,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task PauseScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.ScanPause,
                parameters: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task ResumeScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.ScanResume,
                parameters: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task StopScanAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.ScanStop,
                parameters: null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task<RevoTrackProcessingState?> GetProcessingStateAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await GetProcessingStateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public async Task<RevoTrackProcessingState?> RunOneClickEditAsync(
        bool waitForCompletion = true,
        TimeSpan? pollingInterval = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();

        pollingInterval ??= TimeSpan.FromMilliseconds(500);
        timeout ??= TimeSpan.FromMinutes(30);

        if (pollingInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollingInterval),
                pollingInterval,
                "The polling interval must be greater than zero.");
        }

        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The timeout must be greater than zero.");
        }

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.EditOneClickEdit,
                parameters: null,
                cancellationToken).ConfigureAwait(false);

            if (!waitForCompletion)
            {
                return await GetProcessingStateCoreAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutCancellation.CancelAfter(timeout.Value);
            RevoTrackProcessingState? lastState = null;
            var idleConfirmations = 0;

            try
            {
                while (true)
                {
                    var state = await GetProcessingStateCoreAsync(timeoutCancellation.Token)
                        .ConfigureAwait(false);

                    if (state is not null)
                    {
                        lastState = state;
                        idleConfirmations = 0;
                    }
                    else
                    {
                        idleConfirmations++;

                        // The RPC may return before processing has visibly started.
                        if (idleConfirmations >= 3)
                        {
                            return lastState;
                        }
                    }

                    await Task.Delay(pollingInterval.Value, timeoutCancellation.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested
                    && timeoutCancellation.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"One-click processing did not complete within {timeout.Value}.");
            }
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public Task<RevoTrackDownloadResult> SavePointCloudAsync(
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SaveModelFileAsync(
            RevoTrackModelFileKind.PointCloud,
            destinationPath,
            overwrite,
            progress,
            cancellationToken);

    public Task<RevoTrackDownloadResult> SaveMeshAsync(
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        SaveModelFileAsync(
            RevoTrackModelFileKind.Mesh,
            destinationPath,
            overwrite,
            progress,
            cancellationToken);

    public async Task<RevoTrackDownloadResult> SaveModelFileAsync(
        RevoTrackModelFileKind fileKind,
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureCurrentProject();

        await _projectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rpcClient.InvokeAsync(
                RevoTrackRpcMethods.FileSave,
                parameters: null,
                cancellationToken).ConfigureAwait(false);

            var modelType = GetModelTypeValue(fileKind);
            var models = await GetFileClient().GetModelsAsync(
                [modelType],
                cancellationToken).ConfigureAwait(false);
            var model = models.FirstOrDefault();
            if (model is null)
            {
                throw new InvalidOperationException(
                    $"model/list returned no '{modelType}' file for the current project.");
            }

            var uri = fileKind switch
            {
                RevoTrackModelFileKind.PointCloud => model.PointCloudUri
                    ?? throw new InvalidOperationException(
                        "model/list did not return a pointCloud URL."),
                RevoTrackModelFileKind.Mesh => GetSingleMeshUri(model),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(fileKind),
                    fileKind,
                    "Unsupported model file kind."),
            };

            return await GetFileClient().DownloadFileAsync(
                uri,
                destinationPath,
                overwrite,
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _projectGate.Release();
        }
    }

    public Task<RevoTrackProjectDeleteResult> CloseAndDeleteCurrentProjectAsync(
        CancellationToken cancellationToken = default) =>
        DeleteCurrentProjectAsync(
            closeProjectFirst: true,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_ownsFileClient && _fileClient is not null)
        {
            await _fileClient.DisposeAsync().ConfigureAwait(false);
        }

        _projectGate.Dispose();
    }

    private IRevoTrackFileClient GetFileClient()
    {
        ThrowIfDisposed();

        if (_fileClient is null)
        {
            _fileClient = new RevoTrackFileClient(_rpcClient);
            _ownsFileClient = true;
        }

        return _fileClient;
    }

    private async Task<RevoTrackProcessingState?> GetProcessingStateCoreAsync(
        CancellationToken cancellationToken)
    {
        var result = await _rpcClient.InvokeAsync(
            RevoTrackRpcMethods.EditProcessingState,
            parameters: null,
            cancellationToken).ConfigureAwait(false);

        if (result is null
            || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var root = result.Value;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RevoTrackProcessingState(
            ModelId: GetString(root, "modelId"),
            ModelName: GetString(root, "modelName"),
            Function: GetString(root, "func"),
            Progress: GetInt32(root, "progress"),
            ElapsedTime: GetInt32(root, "elapsedTime"),
            RemainTime: GetInt32(root, "remainTime"));
    }

    private static Uri GetSingleMeshUri(RevoTrackModelInfo model) =>
        model.MeshUris.Count switch
        {
            0 => throw new InvalidOperationException(
                "model/list did not return a mesh URL."),
            1 => model.MeshUris[0],
            _ => throw new InvalidOperationException(
                "model/list returned multiple mesh files. "
                + "Specify the desired mesh explicitly with RevoTrackFileClient."),
        };

    private static void ValidateScanOptions(RevoTrackScanStartOptions options)
    {
        if (options.TargetPointDistance is < 0.10 or > 3.00)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.TargetPointDistance,
                "Target point distance must be between 0.10 and 3.00.");
        }
    }

    private static string GetRegistrationModeValue(RevoTrackRegistrationMode mode) =>
        mode switch
        {
            RevoTrackRegistrationMode.Track => "track",
            RevoTrackRegistrationMode.Marker => "marker",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private static string GetScanModeValue(RevoTrackScanMode mode) =>
        mode switch
        {
            RevoTrackScanMode.SingleLine => "singleLine",
            RevoTrackScanMode.ParallelLines => "parallelLines",
            RevoTrackScanMode.CrossLines => "crossLines",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };

    private static string GetObjectTypeValue(RevoTrackObjectType objectType) =>
        objectType switch
        {
            RevoTrackObjectType.General => "general",
            RevoTrackObjectType.Black => "black",
            RevoTrackObjectType.MetallicShiny => "metallicShiny",
            _ => throw new ArgumentOutOfRangeException(
                nameof(objectType),
                objectType,
                null),
        };

    private static string GetModelTypeValue(RevoTrackModelFileKind fileKind) =>
        fileKind switch
        {
            RevoTrackModelFileKind.PointCloud => "pointCloud",
            RevoTrackModelFileKind.Mesh => "mesh",
            _ => throw new ArgumentOutOfRangeException(
                nameof(fileKind),
                fileKind,
                null),
        };

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.TryGetInt32(out var value)
            ? value
            : 0;

    private void EnsureCurrentProject()
    {
        if (CurrentProject is null)
        {
            throw new InvalidOperationException(
                "No project is recorded. Call CreateProjectAsync first.");
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private void LoadCurrentProject()
    {
        var stateFilePath = _options.GetResolvedStateFilePath();
        if (stateFilePath is null || !File.Exists(stateFilePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(stateFilePath);
            var state = JsonSerializer.Deserialize<ProjectState>(json, StateJsonOptions);
            if (state is null)
            {
                return;
            }

            ValidateProjectName(state.ProjectName);
            var project = CreateProjectInfo(state.ProjectName, state.CreatedAtUtc);
            Volatile.Write(ref _currentProject, project);
        }
        catch (JsonException)
        {
            ClearCurrentProject();
        }
        catch (IOException)
        {
            ClearCurrentProject();
        }
        catch (UnauthorizedAccessException)
        {
            ClearCurrentProject();
        }
        catch (InvalidOperationException)
        {
            ClearCurrentProject();
        }
        catch (ArgumentException)
        {
            ClearCurrentProject();
        }
    }

    private async Task SaveCurrentProjectAsync(
        RevoTrackProjectInfo project,
        CancellationToken cancellationToken)
    {
        var stateFilePath = _options.GetResolvedStateFilePath();
        if (stateFilePath is null)
        {
            return;
        }

        var stateDirectory = Path.GetDirectoryName(stateFilePath);
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new InvalidOperationException("The project state file path has no parent directory.");
        }

        Directory.CreateDirectory(stateDirectory);

        var temporaryPath = Path.Combine(
            stateDirectory,
            $".{Path.GetFileName(stateFilePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            var json = JsonSerializer.Serialize(
                new ProjectState(project.Name, project.CreatedAtUtc),
                StateJsonOptions);
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporaryPath, stateFilePath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private void ClearCurrentProject()
    {
        Volatile.Write(ref _currentProject, null);

        var stateFilePath = _options.GetResolvedStateFilePath();
        if (stateFilePath is not null)
        {
            TryDeleteFile(stateFilePath);
        }
    }

    private RevoTrackProjectInfo CreateProjectInfo(
        string projectName,
        DateTimeOffset createdAtUtc) =>
        new(
            projectName,
            ResolveProjectDirectory(projectName),
            createdAtUtc);

    private string ResolveProjectDirectory(string projectName)
    {
        var projectsRootPath = _options.GetResolvedProjectsRootPath();
        var rootWithSeparator = projectsRootPath.EndsWith(
            Path.DirectorySeparatorChar)
                ? projectsRootPath
                : projectsRootPath + Path.DirectorySeparatorChar;
        var projectDirectory = Path.GetFullPath(
            Path.Combine(projectsRootPath, projectName));

        if (!projectDirectory.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The project directory resolved outside of the configured projects root.");
        }

        return projectDirectory;
    }

    private void ValidateProjectName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        if (projectName.Length > 30)
        {
            throw new ArgumentException(
                "The project name cannot exceed 30 characters.",
                nameof(projectName));
        }

        if (projectName is "." or ".."
            || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || projectName.EndsWith(' '))
        {
            throw new ArgumentException(
                "The project name is not a valid Windows directory name.",
                nameof(projectName));
        }

        var baseName = Path.GetFileNameWithoutExtension(projectName);
        if (ReservedWindowsNames.Contains(baseName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The project name is reserved by Windows.",
                nameof(projectName));
        }

        if (_options.ProtectedProjectNames.Contains(projectName))
        {
            throw new InvalidOperationException(
                $"Project '{projectName}' is protected from deletion.");
        }
    }

    private static void EnsureDirectoryIsNotReparsePoint(string directoryPath)
    {
        var attributes = File.GetAttributes(directoryPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                $"Refusing to delete reparse-point directory '{directoryPath}'.");
        }
    }

    private async Task DeleteDirectoryWithRetryAsync(
        string directoryPath,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception exception)
                when (attempt < _options.DeleteRetryCount
                    && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(_options.DeleteRetryDelay, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static long? TryGetDirectorySize(string directoryPath)
    {
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            long size = 0;

            foreach (var filePath in Directory.EnumerateFiles(
                         directoryPath,
                         "*",
                         options))
            {
                size += new FileInfo(filePath).Length;
            }

            return size;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ProjectState(
        string ProjectName,
        DateTimeOffset CreatedAtUtc);
}
