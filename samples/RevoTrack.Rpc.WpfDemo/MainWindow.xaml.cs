using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using RevoTrack.Rpc;
using RevoTrack.Rpc.Files;

namespace RevoTrack.Rpc.WpfDemo;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly CancellationTokenSource _windowCancellation = new();
    private readonly RevoTrackRpcClient _client = new(
        new RevoTrackRpcOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            DisconnectTimeout = TimeSpan.FromSeconds(2),
        });
    private readonly RevoTrackFileClient _fileClient;

    public MainWindow()
    {
        InitializeComponent();
        _fileClient = new RevoTrackFileClient(_client);
        MethodList.ItemsSource = RpcMethodCatalog.Methods;
        _client.ConnectionStateChanged += Client_ConnectionStateChanged;
        _client.NotificationReceived += Client_NotificationReceived;
        _client.TransportError += Client_TransportError;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        MethodList.SelectedIndex = 0;
        SetConnectionState(RevoTrackRpcConnectionState.Disconnected);
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;

        try
        {
            if (_client.IsConnected)
            {
                await _client.DisconnectAsync(_windowCancellation.Token);
                AppendMessage("INFO", "Disconnected.");
                return;
            }

            var host = HostTextBox.Text.Trim();
            if (!int.TryParse(PortTextBox.Text.Trim(), out var port))
            {
                MessageBox.Show("Port must be a number.", "Invalid Port");
                return;
            }

            await _client.ConnectAsync(host, port, _windowCancellation.Token);
            AppendMessage("INFO", $"Connected to {host}:{port}.");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppendMessage("ERROR", exception.Message);
            MessageBox.Show(exception.Message, "Connection Failed");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void MethodList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MethodList.SelectedItem is not RpcMethodExample method)
        {
            return;
        }

        SelectedMethodText.Text = method.Name;
        ParametersTextBox.Text = method.ParametersJson;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (MethodList.SelectedItem is not RpcMethodExample method)
        {
            return;
        }

        if (!_client.IsConnected)
        {
            MessageBox.Show("Connect to RevoTrack first.", "Not Connected");
            return;
        }

        SendButton.IsEnabled = false;

        try
        {
            var parameters = ParseParameters(ParametersTextBox.Text);
            AppendMessage("SEND", BuildRequestPreview(method.Name, parameters));

            var result = await _client.InvokeAsync<JsonElement?>(
                method.Name,
                parameters,
                _windowCancellation.Token);

            AppendMessage(
                "RECEIVED",
                result is null
                    ? "null"
                    : JsonSerializer.Serialize(result.Value, PrettyJsonOptions));
        }
        catch (JsonException exception)
        {
            AppendMessage("ERROR", exception.Message);
            MessageBox.Show(exception.Message, "Invalid Parameters JSON");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppendMessage("ERROR", FormatException(exception));
        }
        finally
        {
            SendButton.IsEnabled = true;
        }
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e) =>
        MessagesTextBox.Clear();

    private async void AutoExposureButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show("Connect to RevoTrack first.", "Not Connected");
            return;
        }

        AutoExposureButton.IsEnabled = false;

        try
        {
            await _client.SetScannerAutoOptionsAsync(
                cancellationToken: _windowCancellation.Token);
            AppendMessage("INFO", "Scanner exposure set to auto.");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppendMessage("ERROR", FormatException(exception));
        }
        finally
        {
            AutoExposureButton.IsEnabled = true;
        }
    }

    private async void DownloadPointCloudButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_client.IsConnected)
        {
            MessageBox.Show("Connect to RevoTrack first.", "Not Connected");
            return;
        }

        DownloadPointCloudButton.IsEnabled = false;

        try
        {
            var models = await _fileClient.GetModelsAsync(
                cancellationToken: _windowCancellation.Token);
            var model = models.FirstOrDefault(candidate => candidate.PointCloudUri is not null);
            if (model?.PointCloudUri is null)
            {
                AppendMessage("INFO", "model/list returned no point cloud file.");
                return;
            }

            var suggestedFileName = Path.GetFileName(model.PointCloudUri.LocalPath);
            if (string.IsNullOrWhiteSpace(suggestedFileName))
            {
                suggestedFileName = $"{model.ModelName}_pointCloud.ply";
            }

            var saveDialog = new SaveFileDialog
            {
                Title = "Save point cloud",
                FileName = suggestedFileName,
                DefaultExt = ".ply",
                Filter = "Point cloud files (*.ply)|*.ply|All files (*.*)|*.*",
                OverwritePrompt = true,
            };

            if (saveDialog.ShowDialog(this) != true)
            {
                return;
            }

            var result = await _fileClient.DownloadFileAsync(
                model.PointCloudUri,
                saveDialog.FileName,
                overwrite: true,
                cancellationToken: _windowCancellation.Token);

            AppendMessage(
                "DOWNLOAD",
                $"Point cloud saved:\n{result.DestinationPath}\n{result.BytesWritten:N0} bytes");
        }
        catch (OperationCanceledException) when (_windowCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppendMessage("ERROR", FormatException(exception));
        }
        finally
        {
            DownloadPointCloudButton.IsEnabled = true;
        }
    }

    private void Client_ConnectionStateChanged(
        object? sender,
        RevoTrackRpcConnectionStateChangedEventArgs e) =>
        Dispatcher.BeginInvoke(() => SetConnectionState(e.CurrentState));

    private void Client_NotificationReceived(
        object? sender,
        RevoTrackRpcNotification notification) =>
        Dispatcher.BeginInvoke(
            () => AppendMessage(
                "NOTIFY",
                $"{notification.Method}\n"
                + (notification.Parameters is null
                    ? "null"
                    : JsonSerializer.Serialize(notification.Parameters.Value, PrettyJsonOptions))));

    private void Client_TransportError(
        object? sender,
        RevoTrackRpcTransportErrorEventArgs e) =>
        Dispatcher.BeginInvoke(
            () => AppendMessage("TRANSPORT", e.Exception.Message));

    protected override async void OnClosed(EventArgs e)
    {
        _windowCancellation.Cancel();
        await _fileClient.DisposeAsync();
        await _client.DisposeAsync();
        _windowCancellation.Dispose();
        base.OnClosed(e);
    }

    private static object? ParseParameters(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.ValueKind == JsonValueKind.Null
            ? null
            : document.RootElement.Clone();
    }

    private static string BuildRequestPreview(string method, object? parameters)
    {
        var request = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
            id = "(assigned by client)",
        };

        return JsonSerializer.Serialize(request, PrettyJsonOptions);
    }

    private static string FormatException(Exception exception)
    {
        if (exception is not RevoTrackRpcException rpcException
            || rpcException.ErrorCode is null)
        {
            return exception.Message;
        }

        var builder = new StringBuilder()
            .Append("Code: ")
            .Append(rpcException.ErrorCode.Value)
            .AppendLine()
            .Append("Message: ")
            .Append(rpcException.Message);

        if (rpcException.ErrorData is not null)
        {
            builder.AppendLine()
                .Append("Data: ")
                .Append(JsonSerializer.Serialize(rpcException.ErrorData.Value, PrettyJsonOptions));
        }

        return builder.ToString();
    }

    private void SetConnectionState(RevoTrackRpcConnectionState state)
    {
        StatusText.Text = state switch
        {
            RevoTrackRpcConnectionState.Connecting => "Connecting",
            RevoTrackRpcConnectionState.Connected => "Connected",
            _ => "Disconnected",
        };

        StatusIndicator.Fill = state switch
        {
            RevoTrackRpcConnectionState.Connected => new SolidColorBrush(Color.FromRgb(41, 128, 85)),
            RevoTrackRpcConnectionState.Connecting => new SolidColorBrush(Color.FromRgb(196, 126, 32)),
            _ => new SolidColorBrush(Color.FromRgb(194, 65, 59)),
        };

        ConnectButton.Content = state == RevoTrackRpcConnectionState.Connected
            ? "Disconnect"
            : "Connect";
        HostTextBox.IsEnabled = state == RevoTrackRpcConnectionState.Disconnected;
        PortTextBox.IsEnabled = state == RevoTrackRpcConnectionState.Disconnected;
    }

    private void AppendMessage(string category, string content)
    {
        MessagesTextBox.AppendText(
            $"[{DateTime.Now:HH:mm:ss}] [{category}]\n{content}\n\n");
        MessagesTextBox.ScrollToEnd();
    }
}
