using System.Net;
using System.Text;
using System.Text.Json;
using RevoTrack.Rpc.Files;

namespace RevoTrack.Rpc.Tests;

public sealed class RevoTrackFileClientTests
{
    [Fact]
    public async Task GetModelsAsync_ParsesPointCloudMeshAndTextureUrls()
    {
        var rpcClient = new StubRpcClient
        {
            InvokeHandler = (method, parameters) =>
            {
                Assert.Equal(RevoTrackRpcMethods.ModelList, method);
                Assert.Equal(
                    ["pointCloud", "mesh", "texture"],
                    Assert.IsType<string[]>(parameters));

                return ParseJson(
                    """
                    {
                      "modelId": "model-1",
                      "modelName": "Demo",
                      "pointCloud": "http://127.0.0.1:53666/model/fuse.ply",
                      "mesh": [
                        "http://127.0.0.1:53666/model/fuse_mesh.ply"
                      ],
                      "texture": {
                        "jpg": "http://127.0.0.1:53666/model/fuse_mesh_tex.jpg",
                        "ply": "http://127.0.0.1:53666/model/fuse_mesh_tex.ply"
                      }
                    }
                    """);
            },
        };
        await using var client = new RevoTrackFileClient(
            rpcClient,
            new HttpClient(new FakeHttpMessageHandler(
                _ => throw new InvalidOperationException("No HTTP request expected."))));

        var models = await client.GetModelsAsync();
        var model = Assert.Single(models);

        Assert.Equal("model-1", model.ModelId);
        Assert.Equal("Demo", model.ModelName);
        Assert.Equal("http://127.0.0.1:53666/model/fuse.ply", model.PointCloudUri?.ToString());
        Assert.Single(model.MeshUris);
        Assert.Equal("http://127.0.0.1:53666/model/fuse_mesh_tex.jpg", model.Texture?.Jpg?.ToString());
        Assert.Equal("http://127.0.0.1:53666/model/fuse_mesh_tex.ply", model.Texture?.Ply?.ToString());
    }

    [Fact]
    public async Task DownloadFileAsync_WritesFileAndReportsProgress()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var content = Encoding.UTF8.GetBytes("point-cloud-data");
            await using var client = CreateFileClient(
                request => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(content),
                });
            var destinationPath = Path.Combine(temporaryDirectory, "nested", "fuse.ply");
            var progress = new InlineProgress<RevoTrackDownloadProgress>();

            var result = await client.DownloadFileAsync(
                new Uri("http://127.0.0.1:53666/model/fuse.ply"),
                destinationPath,
                progress: progress);

            Assert.Equal(content.Length, result.BytesWritten);
            Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
            Assert.NotEmpty(progress.Values);
            Assert.Equal(content.Length, progress.Values[^1].BytesReceived);
            Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WhenFileExistsAndOverwriteIsFalse_Throws()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            var destinationPath = Path.Combine(temporaryDirectory, "fuse.ply");
            await File.WriteAllTextAsync(destinationPath, "existing");
            await using var client = CreateFileClient(
                _ => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3]),
                });

            await Assert.ThrowsAsync<IOException>(
                () => client.DownloadFileAsync(
                    new Uri("http://127.0.0.1:53666/model/fuse.ply"),
                    destinationPath));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadFileAsync_WhenRequestFails_RemovesTemporaryFile()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            await using var client = CreateFileClient(
                _ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var destinationPath = Path.Combine(temporaryDirectory, "fuse.ply");

            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.DownloadFileAsync(
                    new Uri("http://127.0.0.1:53666/model/fuse.ply"),
                    destinationPath));

            Assert.False(File.Exists(destinationPath));
            Assert.Empty(Directory.GetFiles(temporaryDirectory, "*.part", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_DownloadsSelectedFiles()
    {
        var temporaryDirectory = CreateTemporaryDirectory();

        try
        {
            await using var client = CreateFileClient(
                request =>
                {
                    var bytes = Encoding.UTF8.GetBytes(request.RequestUri!.AbsolutePath);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes),
                    };
                });
            var model = new RevoTrackModelInfo(
                "model-1",
                "Demo",
                new Uri("http://127.0.0.1:53666/model/fuse.ply"),
                [new Uri("http://127.0.0.1:53666/model/fuse_mesh.ply")],
                new RevoTrackTextureFiles(
                    new Uri("http://127.0.0.1:53666/model/fuse_mesh_tex.jpg"),
                    new Uri("http://127.0.0.1:53666/model/fuse_mesh_tex.ply")));

            var results = await client.DownloadModelAsync(model, temporaryDirectory);

            Assert.Equal(4, results.Count);
            Assert.All(results, result => Assert.True(File.Exists(result.DestinationPath)));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static RevoTrackFileClient CreateFileClient(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) =>
        new(
            new StubRpcClient(),
            new HttpClient(new FakeHttpMessageHandler(responseFactory)));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "RevoTrack.Rpc.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value) => Values.Add(value);
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
