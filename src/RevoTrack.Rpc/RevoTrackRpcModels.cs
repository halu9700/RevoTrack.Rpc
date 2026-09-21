using System.Text.Json;

namespace RevoTrack.Rpc;

public static class RevoTrackRpcMethods
{
    public const string ScannerState = "scanner/state";
    public const string ScannerSetOptions = "scanner/setOptions";
    public const string ScannerGetOptions = "scanner/getOptions";
    public const string FileNew = "file/new";
    public const string ScanStart = "scan/start";
    public const string ScanPause = "scan/pause";
    public const string ScanResume = "scan/resume";
    public const string ScanStop = "scan/stop";
    public const string FileSave = "file/save";
    public const string FileBackToHome = "file/backToHome";
    public const string EditOneClickEdit = "edit/oneClickEdit";
    public const string ModelList = "model/list";
    public const string EditProcessingState = "edit/processingState";
    public const string ThirdToolsGoToMeasure = "thirdtools/goToMeasure";
    public const string WindowsShow = "windows/show";
    public const string Subscribe = "subscribe/subscribe";
    public const string SubscribeNotify = "subscribe/notify";
    public const string Pose = "pose";
}

public static class RevoTrackRpcErrorCode
{
    public const int Unknown = -1;
    public const int FeatureUnavailable = 2;
    public const int FeatureRunning = 3;

    public const int ScanNotReady = 50528275;
    public const int ScanNotStarted = 50528276;
    public const int ScanNotPaused = 50528277;
    public const int ScanNotCompleted = 50528278;
    public const int ProjectAlreadyExists = 50790430;
    public const int InvalidProjectName = 50790431;

    public const int ProjectNotOpen = 50790400;
    public const int FileServiceDisabled = 50855940;

    public const int JsonRpcReservedErrorStart = -32000;
    public const int JsonRpcReservedErrorEnd = -32099;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParameters = -32602;
    public const int InternalError = -32603;
    public const int ParseError = -32700;
}

public sealed record RevoTrackRpcNotification(
    string Method,
    JsonElement? Parameters,
    JsonElement RawMessage);

public sealed class RevoTrackRpcException : Exception
{
    public RevoTrackRpcException(string message)
        : base(message)
    {
    }

    public RevoTrackRpcException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public RevoTrackRpcException(
        int errorCode,
        string message,
        JsonElement? errorData = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
        ErrorData = errorData?.Clone();
    }

    public int? ErrorCode { get; }

    public JsonElement? ErrorData { get; }
}
