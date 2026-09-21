using System.Text.Json;
using RevoTrack.Rpc.Projects;

namespace RevoTrack.Rpc.Tests;

public sealed class RevoTrackProjectManagerTests
{
    [Fact]
    public async Task CreateProjectAsync_CallsFileNewAndPersistsProjectState()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var rpcClient = new StubRpcClient();
            var manager = new RevoTrackProjectManager(rpcClient, options);

            var created = await manager.CreateProjectAsync("DemoProject");
            var restoredManager = new RevoTrackProjectManager(rpcClient, options);

            Assert.Equal("DemoProject", created.Name);
            Assert.Equal(
                Path.Combine(temporaryDirectory, "Projects", "DemoProject"),
                created.DirectoryPath);
            Assert.Equal("DemoProject", restoredManager.CurrentProject?.Name);
            Assert.Single(rpcClient.Invocations);
            Assert.Equal(
                RevoTrackRpcMethods.FileNew,
                rpcClient.Invocations[0].Method);

            using var parameters = JsonDocument.Parse(
                JsonSerializer.Serialize(rpcClient.Invocations[0].Parameters));
            Assert.Equal(
                "DemoProject",
                parameters.RootElement.GetProperty("name").GetString());
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteCurrentProjectAsync_ClosesProjectAndDeletesRecordedDirectory()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var rpcClient = new StubRpcClient();
            var manager = new RevoTrackProjectManager(rpcClient, options);
            var project = await manager.CreateProjectAsync("DemoProject");
            Directory.CreateDirectory(project.DirectoryPath);
            await File.WriteAllTextAsync(
                Path.Combine(project.DirectoryPath, "cloud.ply"),
                "point-cloud-data");

            var result = await manager.DeleteCurrentProjectAsync();

            Assert.True(result.Deleted);
            Assert.True(result.CloseInvoked);
            Assert.Equal(2, rpcClient.Invocations.Count);
            Assert.Equal(
                RevoTrackRpcMethods.FileBackToHome,
                rpcClient.Invocations[1].Method);
            Assert.False(Directory.Exists(project.DirectoryPath));
            Assert.Null(manager.CurrentProject);
            Assert.False(File.Exists(options.StateFilePath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteCurrentProjectAsync_WhenProjectIsAlreadyClosed_StillDeletes()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var rpcClient = new StubRpcClient();
            var manager = new RevoTrackProjectManager(rpcClient, options);
            var project = await manager.CreateProjectAsync("DemoProject");
            Directory.CreateDirectory(project.DirectoryPath);
            rpcClient.ThrowProjectNotOpenOnBackToHome = true;

            var result = await manager.DeleteCurrentProjectAsync();

            Assert.True(result.Deleted);
            Assert.False(Directory.Exists(project.DirectoryPath));
            Assert.Null(manager.CurrentProject);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteCurrentProjectAsync_WhenDirectoryIsMissing_ClearsRecord()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var manager = new RevoTrackProjectManager(new StubRpcClient(), options);
            await manager.CreateProjectAsync("MissingProject");

            var result = await manager.DeleteCurrentProjectAsync();

            Assert.False(result.Deleted);
            Assert.Null(manager.CurrentProject);
            Assert.False(File.Exists(options.StateFilePath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteCurrentProjectAsync_WhenDisconnected_DoesNotDelete()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var rpcClient = new StubRpcClient();
            var manager = new RevoTrackProjectManager(rpcClient, options);
            var project = await manager.CreateProjectAsync("DemoProject");
            Directory.CreateDirectory(project.DirectoryPath);
            rpcClient.IsConnected = false;

            await Assert.ThrowsAsync<RevoTrackRpcException>(
                () => manager.DeleteCurrentProjectAsync());

            Assert.True(Directory.Exists(project.DirectoryPath));
            Assert.NotNull(manager.CurrentProject);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ForgetCurrentProjectAsync_DoesNotDeleteDirectory()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var options = CreateOptions(temporaryDirectory);
            var manager = new RevoTrackProjectManager(new StubRpcClient(), options);
            var project = await manager.CreateProjectAsync("DemoProject");
            Directory.CreateDirectory(project.DirectoryPath);

            await manager.ForgetCurrentProjectAsync();

            Assert.Null(manager.CurrentProject);
            Assert.True(Directory.Exists(project.DirectoryPath));
            Assert.False(File.Exists(options.StateFilePath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("CON")]
    [InlineData("name.with.trailing.space ")]
    [InlineData("ProjectNameLongerThanThirtyCharacters")]
    public async Task CreateProjectAsync_RejectsUnsafeProjectNames(string projectName)
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var manager = new RevoTrackProjectManager(
                new StubRpcClient(),
                CreateOptions(temporaryDirectory));

            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => manager.CreateProjectAsync(projectName));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateProjectAsync_RejectsProtectedGlobalMarkerLibrary()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var manager = new RevoTrackProjectManager(
                new StubRpcClient(),
                CreateOptions(temporaryDirectory));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CreateProjectAsync("GlobalMarkerLibrary"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CreateProjectAsync_ExpandsEnvironmentVariables()
    {
        var testId = Guid.NewGuid().ToString("N");
        var expandedRoot = Path.Combine(
            Path.GetTempPath(),
            "RevoTrack.Rpc.EnvTests",
            testId);

        try
        {
            var options = new RevoTrackProjectOptions
            {
                ProjectsRootPath = $@"%TEMP%\RevoTrack.Rpc.EnvTests\{testId}\Projects",
                StateFilePath = $@"%TEMP%\RevoTrack.Rpc.EnvTests\{testId}\State\current.json",
                CloseProjectDelay = TimeSpan.FromMilliseconds(1),
                DeleteRetryDelay = TimeSpan.FromMilliseconds(1),
            };
            var manager = new RevoTrackProjectManager(new StubRpcClient(), options);

            var project = await manager.CreateProjectAsync("EnvProject");

            Assert.Equal(
                Path.Combine(expandedRoot, "Projects", "EnvProject"),
                project.DirectoryPath);
        }
        finally
        {
            Directory.Delete(expandedRoot, recursive: true);
        }
    }

    private static RevoTrackProjectOptions CreateOptions(string temporaryDirectory) =>
        new()
        {
            ProjectsRootPath = Path.Combine(temporaryDirectory, "Projects"),
            StateFilePath = Path.Combine(temporaryDirectory, "State", "current-project.json"),
            CloseProjectDelay = TimeSpan.FromMilliseconds(1),
            DeleteRetryDelay = TimeSpan.FromMilliseconds(1),
            DeleteRetryCount = 2,
        };

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "RevoTrack.Rpc.ProjectTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
