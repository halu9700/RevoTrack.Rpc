using System.Text.Json;
using RevoTrack.Rpc;

namespace RevoTrack.Rpc.Files;

public sealed class RevoTrackFileClient : IRevoTrackFileClient
{
    private const int CopyBufferSize = 81920;
    private static readonly string[] DefaultModelTypes = ["pointCloud", "mesh", "texture"];

    private readonly IRevoTrackRpcClient _rpcClient;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;

    public RevoTrackFileClient(IRevoTrackRpcClient rpcClient)
        : this(
            rpcClient,
            new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan,
            },
            ownsHttpClient: true)
    {
    }

    public RevoTrackFileClient(IRevoTrackRpcClient rpcClient, HttpClient httpClient)
        : this(rpcClient, httpClient, ownsHttpClient: false)
    {
    }

    private RevoTrackFileClient(
        IRevoTrackRpcClient rpcClient,
        HttpClient httpClient,
        bool ownsHttpClient)
    {
        _rpcClient = rpcClient ?? throw new ArgumentNullException(nameof(rpcClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _ownsHttpClient = ownsHttpClient;
    }

    public async Task<IReadOnlyList<RevoTrackModelInfo>> GetModelsAsync(
        IReadOnlyCollection<string>? modelTypes = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var requestedTypes = modelTypes is null or { Count: 0 }
            ? DefaultModelTypes
            : modelTypes.Distinct(StringComparer.Ordinal).ToArray();
        var result = await _rpcClient.InvokeAsync(
            RevoTrackRpcMethods.ModelList,
            requestedTypes,
            cancellationToken).ConfigureAwait(false);

        if (result is null || result.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        return ParseModels(result.Value);
    }

    public async Task<RevoTrackDownloadResult> DownloadFileAsync(
        Uri url,
        string destinationPath,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ValidateHttpUri(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullPath = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination path must include a directory.", nameof(destinationPath));

        Directory.CreateDirectory(directory);

        if (File.Exists(fullPath) && !overwrite)
        {
            throw new IOException($"The destination file already exists: '{fullPath}'.");
        }

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.part");

        try
        {
            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            var bytesWritten = 0L;
            progress?.Report(new RevoTrackDownloadProgress(url, fullPath, 0, totalBytes));

            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                             .ConfigureAwait(false))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[CopyBufferSize];

                while (true)
                {
                    var bytesRead = await source.ReadAsync(buffer, cancellationToken)
                        .ConfigureAwait(false);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    await destination.WriteAsync(
                        buffer.AsMemory(0, bytesRead),
                        cancellationToken).ConfigureAwait(false);
                    bytesWritten += bytesRead;
                    progress?.Report(
                        new RevoTrackDownloadProgress(
                            url,
                            fullPath,
                            bytesWritten,
                            totalBytes));
                }
            }

            File.Move(temporaryPath, fullPath, overwrite);
            return new RevoTrackDownloadResult(url, fullPath, bytesWritten);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public async Task<IReadOnlyList<RevoTrackDownloadResult>> DownloadModelAsync(
        RevoTrackModelInfo model,
        string destinationDirectory,
        RevoTrackModelFileSelection selection = RevoTrackModelFileSelection.All,
        bool overwrite = false,
        IProgress<RevoTrackDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (selection == RevoTrackModelFileSelection.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                selection,
                "At least one model file type must be selected.");
        }

        var files = BuildDownloadPlan(model, selection);
        if (files.Count == 0)
        {
            return [];
        }

        var results = new List<RevoTrackDownloadResult>(files.Count);

        foreach (var (url, fileName) in files)
        {
            var destinationPath = Path.Combine(destinationDirectory, fileName);
            results.Add(
                await DownloadFileAsync(
                    url,
                    destinationPath,
                    overwrite,
                    progress,
                    cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;

        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static IReadOnlyList<RevoTrackModelInfo> ParseModels(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var modelId = GetString(root, "modelId") ?? string.Empty;
        var modelName = GetString(root, "modelName") ?? modelId;
        var pointCloud = GetUri(root, "pointCloud");
        var meshUris = GetUriList(root, "mesh");
        RevoTrackTextureFiles? texture = null;

        if (root.TryGetProperty("texture", out var textureElement)
            && textureElement.ValueKind == JsonValueKind.Object)
        {
            texture = new RevoTrackTextureFiles(
                GetUri(textureElement, "jpg"),
                GetUri(textureElement, "ply"));
        }

        return
        [
            new RevoTrackModelInfo(
                modelId,
                modelName,
                pointCloud,
                meshUris,
                texture),
        ];
    }

    private static IReadOnlyList<(Uri Url, string FileName)> BuildDownloadPlan(
        RevoTrackModelInfo model,
        RevoTrackModelFileSelection selection)
    {
        var baseName = SanitizeFileName(
            string.IsNullOrWhiteSpace(model.ModelName)
                ? model.ModelId
                : model.ModelName);
        var files = new List<(Uri Url, string FileName)>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (selection.HasFlag(RevoTrackModelFileSelection.PointCloud)
            && model.PointCloudUri is not null)
        {
            AddFile(
                files,
                usedNames,
                model.PointCloudUri,
                $"{baseName}_pointCloud.ply");
        }

        if (selection.HasFlag(RevoTrackModelFileSelection.Mesh))
        {
            for (var index = 0; index < model.MeshUris.Count; index++)
            {
                AddFile(
                    files,
                    usedNames,
                    model.MeshUris[index],
                    $"{baseName}_mesh_{index + 1}.ply");
            }
        }

        if (selection.HasFlag(RevoTrackModelFileSelection.TextureJpg)
            && model.Texture?.Jpg is not null)
        {
            AddFile(
                files,
                usedNames,
                model.Texture.Jpg,
                $"{baseName}_texture.jpg");
        }

        if (selection.HasFlag(RevoTrackModelFileSelection.TexturePly)
            && model.Texture?.Ply is not null)
        {
            AddFile(
                files,
                usedNames,
                model.Texture.Ply,
                $"{baseName}_texture.ply");
        }

        return files;
    }

    private static void AddFile(
        ICollection<(Uri Url, string FileName)> files,
        ISet<string> usedNames,
        Uri url,
        string fallbackFileName)
    {
        var fileName = Path.GetFileName(url.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = fallbackFileName;
        }

        fileName = SanitizeFileName(fileName);
        var uniqueName = fileName;
        var suffix = 2;

        while (!usedNames.Add(uniqueName))
        {
            var extension = Path.GetExtension(fileName);
            var name = Path.GetFileNameWithoutExtension(fileName);
            uniqueName = $"{name}_{suffix++}{extension}";
        }

        files.Add((url, uniqueName));
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(
            fileName.Select(character => invalidCharacters.Contains(character) ? '_' : character)
                .ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            ? "model"
            : sanitized;
    }

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Uri? GetUri(JsonElement element, string propertyName) =>
        TryCreateHttpUri(GetString(element, propertyName));

    private static IReadOnlyList<Uri> GetUriList(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return [];
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            var uri = TryCreateHttpUri(property.GetString());
            return uri is null ? [] : [uri];
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var uris = new List<Uri>();

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var uri = TryCreateHttpUri(item.GetString());
            if (uri is not null)
            {
                uris.Add(uri);
            }
        }

        return uris;
    }

    private static Uri? TryCreateHttpUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return IsHttpUri(uri) ? uri : null;
    }

    private static void ValidateHttpUri(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri || !IsHttpUri(url))
        {
            throw new ArgumentException(
                "The download URL must be an absolute HTTP or HTTPS URL.",
                nameof(url));
        }
    }

    private static bool IsHttpUri(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

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

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
