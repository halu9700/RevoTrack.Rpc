namespace RevoTrack.Rpc;

public static class RevoTrackRpcClientExtensions
{
    /// <summary>
    /// Configures automatic depth and RGB exposure, the usual RevoTrack mode.
    /// </summary>
    public static Task SetScannerAutoOptionsAsync(
        this IRevoTrackRpcClient client,
        int? laserBrightness = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (laserBrightness is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(laserBrightness),
                laserBrightness,
                "Laser brightness cannot be negative.");
        }

        var scanner = new Dictionary<string, object?>
        {
            ["depthExposureType"] = "auto",
            ["rgbExposureType"] = "auto",
        };

        if (laserBrightness is not null)
        {
            scanner["laserBrightness"] = laserBrightness.Value;
        }

        return client.InvokeAsync(
            RevoTrackRpcMethods.ScannerSetOptions,
            new { scanner },
            cancellationToken);
    }
}
