namespace RevoTrack.Rpc.WpfDemo;

internal sealed record RpcMethodExample(string Name, string ParametersJson);

internal static class RpcMethodCatalog
{
    public static IReadOnlyList<RpcMethodExample> Methods { get; } =
    [
        new(RevoTrack.Rpc.RevoTrackRpcMethods.ScannerState, "null"),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.ScannerSetOptions,
            """
            {
              "scanner": {
                "depthExposureType": "manual",
                "depthExposure": 2,
                "rgbExposureType": "manual",
                "rgbExposure": 50,
                "laserBrightness": 10
              }
            }
            """),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.ScannerGetOptions, "null"),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.FileNew,
            """
            {
              "name": "Demo111"
            }
            """),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.ScanStart,
            """
            {
              "registerMode": "track",
              "targetPointDistance": 0.5,
              "scanMode": "crossLines",
              "objectType": "metallicShiny",
              "large": false
            }
            """),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.ScanPause, "null"),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.ScanResume, "null"),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.ScanStop, "null"),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.FileSave, "null"),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.FileBackToHome, "null"),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.EditOneClickEdit, "null"),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.ModelList,
            """
            ["pointCloud", "mesh", "texture"]
            """),
        new(RevoTrack.Rpc.RevoTrackRpcMethods.EditProcessingState, "null"),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.ThirdToolsGoToMeasure,
            """
            {
              "modelId": "3b5feb1e-30ea-4c35-b463-8419b5e761da"
            }
            """),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.WindowsShow,
            """
            {
              "visible": false
            }
            """),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.Subscribe,
            """
            {
              "pose": {
                "scanner": true,
                "pointCloud": true
              }
            }
            """),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.SubscribeNotify,
            """
            {
              "pose": {
                "scanner": true,
                "pointCloud": true
              }
            }
            """),
        new(
            RevoTrack.Rpc.RevoTrackRpcMethods.Pose,
            """
            ["scanner", "pointCloud"]
            """),
    ];
}
