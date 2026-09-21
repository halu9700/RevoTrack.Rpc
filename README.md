# RevoTrack.Rpc

基于 .NET 10 的 RevoTrack-RPC/1.0 TCP 通讯库。核心库只使用 .NET BCL，不包含任何第三方依赖。

## 项目结构

```text
RevoTrack.Rpc/
├─ src/RevoTrack.Rpc/                 核心通讯库
├─ src/RevoTrack.Rpc.Files/           HTTP 模型文件下载
├─ src/RevoTrack.Rpc.Projects/        工程状态与安全删除
├─ tests/RevoTrack.Rpc.Tests/         协议与集成测试
└─ samples/RevoTrack.Rpc.WpfDemo/     WPF 测试 Demo
```

## 核心能力

- `RevoTrack-RPC/1.0` 自定义帧编解码
- JSON-RPC 2.0 请求、响应和通知
- 自动匹配请求 `id`
- 支持并发请求和乱序响应
- 支持 `IAsyncEnumerable<RevoTrackRpcNotification>`
- 支持连接状态、通知和传输错误事件
- 映射 RevoTrack 自定义错误码
- 无第三方运行时依赖

`RevoTrack.Rpc.Files` 额外提供：

- 调用 `model/list` 并解析点云、网格和纹理 URL
- 将单个 URL 下载到指定文件路径
- 按模型和文件类型下载到指定目录
- 下载进度、覆盖控制和失败清理
- 通过临时 `.part` 文件完成原子保存

`RevoTrack.Rpc.Projects` 额外提供：

- 调用 `file/new` 创建工程并记录当前工程名
- 将当前工程状态持久化到 `%LOCALAPPDATA%\RevoTrack.Rpc\current-project.json`
- 调用 `file/backToHome` 关闭工程后删除工程目录
- 默认工程根目录为 `%APPDATA%\RevoTrack\Projects`
- 校验路径穿越、Windows 保留名和受保护目录

## 构建和测试

```powershell
cd "path/to/RevoTrack.Rpc"
dotnet build RevoTrack.Rpc.slnx -c Release
dotnet test RevoTrack.Rpc.slnx -c Release
```

## 运行 WPF Demo

```powershell
dotnet run --project samples/RevoTrack.Rpc.WpfDemo/RevoTrack.Rpc.WpfDemo.csproj -c Release
```

Demo 默认连接 `127.0.0.1:53665`。选择方法、编辑参数 JSON 后点击 `Send`，响应和通知会显示在消息区域。

## 基本用法

```csharp
using System.Text.Json;
using RevoTrack.Rpc;

await using var client = new RevoTrackRpcClient(
    new RevoTrackRpcOptions
    {
        ConnectTimeout = TimeSpan.FromSeconds(5),
    });

client.NotificationReceived += (_, notification) =>
{
    Console.WriteLine($"{notification.Method}: {notification.Parameters}");
};

await client.ConnectAsync("127.0.0.1", 53665);

var state = await client.InvokeAsync<JsonElement>(
    RevoTrackRpcMethods.ScannerState);

Console.WriteLine(state.GetProperty("scanner").GetProperty("connectState").GetString());

await client.InvokeAsync(
    RevoTrackRpcMethods.ScanStop);
```

## 自动曝光

`scanner/setOptions` 默认示例使用 manual 模式。大多数场景可以直接调用：

```csharp
await client.SetScannerAutoOptionsAsync();
```

该方法发送：

```json
{
  "scanner": {
    "depthExposureType": "auto",
    "rgbExposureType": "auto"
  }
}
```

需要时可选设置激光亮度：

```csharp
await client.SetScannerAutoOptionsAsync(laserBrightness: 10);
```

## 模型下载

```csharp
using RevoTrack.Rpc.Files;

await using var fileClient = new RevoTrackFileClient(client);

var models = await fileClient.GetModelsAsync();
var progress = new Progress<RevoTrackDownloadProgress>(value =>
{
    if (value.Percentage is not null)
    {
        Console.WriteLine($"{value.Percentage:F1}%");
    }
});

foreach (var model in models)
{
    var results = await fileClient.DownloadModelAsync(
        model,
        @"D:\Models",
        RevoTrackModelFileSelection.All,
        overwrite: false,
        progress);

    foreach (var result in results)
    {
        Console.WriteLine(result.DestinationPath);
    }
}
```

也可以直接下载单个 URL：

```csharp
await fileClient.DownloadFileAsync(
    new Uri("http://127.0.0.1:53666/Project/model.ply"),
    @"D:\Models\model.ply");
```

## 设计约束

- 核心库不自动重试有副作用的 RPC 请求。
- `scan/start` 调用后应监听 `scan/stateChanged`，等待状态到达 `ready` 后再执行扫描。
- `subscribe/notify` 属于高频通知，库使用有界通道，默认丢弃最旧通知以避免内存持续增长。
- TCP 服务端可能限制连接数量，建议每个进程复用一个客户端实例。
- `model/list` 返回的 HTTP URL 生命周期较短，应在返回后尽快下载。

## 完整业务流程示例

以下示例按实际业务顺序，完整覆盖建立连接、创建工程、扫描控制、一键处理、
保存点云或网格、回到主界面并删除工程的全部步骤：

```csharp
using RevoTrack.Rpc;
using RevoTrack.Rpc.Projects;

await using var client = new RevoTrackRpcClient(
    new RevoTrackRpcOptions
    {
        Host = "127.0.0.1",
        Port = 53665,
    });

// 1. 建立连接。
await client.ConnectAsync();

// 大多数扫描场景直接使用 auto 曝光。
await client.SetScannerAutoOptionsAsync();

// 2. 创建顶层业务管理器。
await using var projectManager = new RevoTrackProjectManager(client);

// 3. 创建工程。
var project = await projectManager.CreateProjectAsync("Scan20260921");

// 4-7. 扫描控制。
await projectManager.StartScanAsync(
    new RevoTrackScanStartOptions
    {
        RegistrationMode = RevoTrackRegistrationMode.Track,
        TargetPointDistance = 0.3,
        ScanMode = RevoTrackScanMode.CrossLines,
        ObjectType = RevoTrackObjectType.General,
        Large = false,
    });

await projectManager.PauseScanAsync();
await projectManager.ResumeScanAsync();
await projectManager.StopScanAsync();

// 8. 一键处理。默认轮询 processingState，等待处理结束。
await projectManager.RunOneClickEditAsync();

// 9. pointCloud 或 mesh 二选一，自定义保存位置和文件名。
await projectManager.SavePointCloudAsync(@"D:\Output\my-cloud.ply");
// await projectManager.SaveMeshAsync(@"D:\Output\my-mesh.ply");

// 10. file/save、file/backToHome 并删除记录的工程目录。
var deletion = await projectManager.CloseAndDeleteCurrentProjectAsync();

Console.WriteLine($"Project deleted: {deletion.Deleted}");
Console.WriteLine($"Directory: {deletion.DirectoryPath}");
Console.WriteLine($"Reclaimed bytes: {deletion.ReclaimedBytes}");
```

`SavePointCloudAsync` 和 `SaveMeshAsync` 会先调用 `file/save`，随后调用
`model/list` 并下载所选类型。如果 `model/list` 返回多个 mesh 文件，
`SaveMeshAsync` 会抛出异常，避免静默遗漏文件；此时可以使用
`RevoTrackFileClient` 明确选择要下载的 mesh URL。

## 工程路径与状态

默认工程根目录使用环境变量解析：

```text
%APPDATA%\RevoTrack\Projects
```

当前工程状态保存到：

```text
%LOCALAPPDATA%\RevoTrack.Rpc\current-project.json
```

只需要工程生命周期时，也可以单独使用：

```csharp
using RevoTrack.Rpc.Projects;

var projectManager = new RevoTrackProjectManager(client);

var project = await projectManager.CreateProjectAsync("Scan20260921");

// 执行新建、扫描、处理和保存流程。
await client.InvokeAsync(RevoTrackRpcMethods.FileSave);

// 先调用 file/backToHome 释放工程，再递归删除工程目录。
var deletion = await projectManager.DeleteCurrentProjectAsync();

Console.WriteLine(deletion.DirectoryPath);
Console.WriteLine(deletion.ReclaimedBytes);
```

如果 RevoTrack 已经退出，可以跳过 RPC 关闭步骤：

```csharp
await projectManager.DeleteCurrentProjectAsync(closeProjectFirst: false);
```

安全限制：

- 只删除管理器记录的当前工程，不提供任意路径递归删除。
- 工程名最多 30 个字符，并拒绝非法字符、`.`、`..` 和 Windows 保留名。
- 删除目标必须位于配置的 `ProjectsRootPath` 内。
- `GlobalMarkerLibrary` 默认受保护。
- 重解析点目录不会被递归删除。
- 删除失败时保留当前工程记录，便于重试。

## License

This project is licensed under the Apache License 2.0.
See [LICENSE](LICENSE) for the full license text.

RevoTrack is a trademark of its respective owner. This project is an
independent, unofficial client and is not affiliated with or endorsed by
the RevoTrack vendor. Use of the RevoTrack server software requires a
valid license from the vendor.
