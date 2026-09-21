using System.Text.Json;
using RevoTrack.Rpc.Files;
using RevoTrack.Rpc.Projects;

namespace RevoTrack.Rpc.Tests;

public sealed class RevoTrackProjectWorkflowTests
{
    [Fact]
    public async Task ScanCommands_InvokeExpectedRpcMethodsAndParameters()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var rpcClient = new StubRpcClient();
            await using var manager = new RevoTrackProjectManager(
                rpcClient,
                CreateOptions(temporaryDirectory),
                new StubFileClient());

            await manager.CreateProjectAsync("Demo");
            await manager.StartScanAsync(
                new RevoTrackScanStartOptions
                {
                    RegistrationMode = RevoTrackRegistrationMode.Marker,
                    TargetPointDistance = 1.25,
                    ScanMode = RevoTrackScanMode.ParallelLines,
                    ObjectType = RevoTrackObjectType.MetallicShiny,
                    Large = true,
                });
            await manager.PauseScanAsync();
            await manager.ResumeScanAsync();
            await manager.StopScanAsync();

            Assert.Equal(
                [
                    RevoTrackRpcMethods.FileNew,
                    RevoTrackRpcMethods.ScanStart,
                    RevoTrackRpcMethods.ScanPause,
                    RevoTrackRpcMethods.ScanResume,
                    RevoTrackRpcMethods.ScanStop,
                ],
                rpcClient.Invocations.Select(invocation => invocation.Method));

            using var parameters = JsonDocument.Parse(
                JsonSerializer.Serialize(rpcClient.Invocations[1].Parameters));
            Assert.Equal(
                "marker",
                parameters.RootElement.GetProperty("registerMode").GetString());
            Assert.Equal(
                "parallelLines",
                parameters.RootElement.GetProperty("scanMode").GetString());
            Assert.Equal(
                "metallicShiny",
                parameters.RootElement.GetProperty("objectType").GetString());
            Assert.True(parameters.RootElement.GetProperty("large").GetBoolean());
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task RunOneClickEditAsync_WaitsUntilProcessingIsIdle()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var processingStateCalls = 0;
            var rpcClient = new StubRpcClient
            {
                InvokeHandler = (method, _) =>
                {
                    if (method != RevoTrackRpcMethods.EditProcessingState)
                    {
                        return null;
                    }

                    processingStateCalls++;
                    return processingStateCalls == 1
                        ? ParseJson(
                            """
                            {
                              "modelId": "model-1",
                              "modelName": "Demo",
                              "func": "edit/oneClickEdit",
                              "progress": 45,
                              "elapsedTime": 5,
                              "remainTime": 5
                            }
                            """)
                        : null;
                },
            };
            await using var manager = new RevoTrackProjectManager(
                rpcClient,
                CreateOptions(temporaryDirectory),
                new StubFileClient());
            await manager.CreateProjectAsync("Demo");

            var state = await manager.RunOneClickEditAsync(
                waitForCompletion: true,
                pollingInterval: TimeSpan.FromMilliseconds(1),
                timeout: TimeSpan.FromSeconds(1));

            Assert.Equal(45, state?.Progress);
            Assert.Equal(4, processingStateCalls);
            Assert.Contains(
                rpcClient.Invocations,
                invocation => invocation.Method == RevoTrackRpcMethods.EditOneClickEdit);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SavePointCloudAsync_UsesCustomFileNameAndDownloadsOnlyPointCloud()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var pointCloudUri = new Uri("http://127.0.0.1/model/fuse.ply");
            var fileClient = new StubFileClient
            {
                ModelsFactory = _ =>
                [
                    new RevoTrackModelInfo(
                        "model-1",
                        "Demo",
                        pointCloudUri,
                        [],
                        null),
                ],
            };
            var rpcClient = new StubRpcClient();
            await using var manager = new RevoTrackProjectManager(
                rpcClient,
                CreateOptions(temporaryDirectory),
                fileClient);
            await manager.CreateProjectAsync("Demo");
            var destinationPath = Path.Combine(temporaryDirectory, "custom.ply");

            var result = await manager.SavePointCloudAsync(
                destinationPath,
                overwrite: true);

            Assert.Equal(destinationPath, result.DestinationPath);
            Assert.Contains(
                rpcClient.Invocations,
                invocation => invocation.Method == RevoTrackRpcMethods.FileSave);
            Assert.Single(fileClient.RequestedModelTypes);
            Assert.Equal(
                ["pointCloud"],
                fileClient.RequestedModelTypes[0]);
            Assert.Single(fileClient.Downloads);
            Assert.Equal(pointCloudUri, fileClient.Downloads[0].Uri);
            Assert.Equal(destinationPath, fileClient.Downloads[0].DestinationPath);
            Assert.True(fileClient.Downloads[0].Overwrite);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveMeshAsync_WhenMultipleMeshFilesExist_Throws()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var fileClient = new StubFileClient
            {
                ModelsFactory = _ =>
                [
                    new RevoTrackModelInfo(
                        "model-1",
                        "Demo",
                        null,
                        [
                            new Uri("http://127.0.0.1/model/mesh-1.ply"),
                            new Uri("http://127.0.0.1/model/mesh-2.ply"),
                        ],
                        null),
                ],
            };
            await using var manager = new RevoTrackProjectManager(
                new StubRpcClient(),
                CreateOptions(temporaryDirectory),
                fileClient);
            await manager.CreateProjectAsync("Demo");

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.SaveMeshAsync(
                    Path.Combine(temporaryDirectory, "mesh.ply")));

            Assert.Contains("multiple mesh", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(fileClient.Downloads);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveMeshAsync_WithSingleMesh_UsesCustomFileName()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var meshUri = new Uri("http://127.0.0.1/model/fuse_mesh.ply");
            var fileClient = new StubFileClient
            {
                ModelsFactory = _ =>
                [
                    new RevoTrackModelInfo(
                        "model-1",
                        "Demo",
                        null,
                        [meshUri],
                        null),
                ],
            };
            await using var manager = new RevoTrackProjectManager(
                new StubRpcClient(),
                CreateOptions(temporaryDirectory),
                fileClient);
            await manager.CreateProjectAsync("Demo");
            var destinationPath = Path.Combine(temporaryDirectory, "custom-mesh.ply");

            var result = await manager.SaveMeshAsync(destinationPath);

            Assert.Equal(destinationPath, result.DestinationPath);
            Assert.Equal(["mesh"], fileClient.RequestedModelTypes[0]);
            Assert.Equal(meshUri, Assert.Single(fileClient.Downloads).Uri);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task StartScanAsync_WithoutCurrentProject_Throws()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            await using var manager = new RevoTrackProjectManager(
                new StubRpcClient(),
                CreateOptions(temporaryDirectory),
                new StubFileClient());

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.StartScanAsync());
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
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
            "RevoTrack.Rpc.WorkflowTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
