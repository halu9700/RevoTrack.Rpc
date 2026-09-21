namespace RevoTrack.Rpc.Projects;

public sealed class RevoTrackProjectOptions
{
    public const string DefaultProjectsRootPath = @"%APPDATA%\RevoTrack\Projects";
    public const string DefaultStateFilePath =
        @"%LOCALAPPDATA%\RevoTrack.Rpc\current-project.json";

    public string ProjectsRootPath { get; set; } = DefaultProjectsRootPath;

    public string StateFilePath { get; set; } = DefaultStateFilePath;

    public bool PersistCurrentProject { get; set; } = true;

    public TimeSpan CloseProjectDelay { get; set; } = TimeSpan.FromMilliseconds(300);

    public int DeleteRetryCount { get; set; } = 5;

    public TimeSpan DeleteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    public ISet<string> ProtectedProjectNames { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GlobalMarkerLibrary",
        };

    internal string GetResolvedProjectsRootPath() =>
        ResolveEnvironmentPath(ProjectsRootPath, nameof(ProjectsRootPath));

    internal string? GetResolvedStateFilePath()
    {
        if (!PersistCurrentProject)
        {
            return null;
        }

        return ResolveEnvironmentPath(StateFilePath, nameof(StateFilePath));
    }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(DeleteRetryCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(CloseProjectDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(DeleteRetryDelay, TimeSpan.Zero);
        _ = GetResolvedProjectsRootPath();
        _ = GetResolvedStateFilePath();
    }

    private static string ResolveEnvironmentPath(string path, string propertyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var expandedPath = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expandedPath.Contains('%'))
        {
            throw new InvalidOperationException(
                $"The path configured by {propertyName} contains an unresolved environment variable.");
        }

        return Path.GetFullPath(expandedPath);
    }
}
