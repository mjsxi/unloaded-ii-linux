using System.Runtime.InteropServices;

namespace ReloadedDropIn.Bootstrap;

/// <summary>
/// Entry point invoked by reloaded-dropin.asi via hostfxr's default component
/// delegate. Signature must stay `static int (IntPtr, int)`.
/// </summary>
public static class NativeEntry
{
    public static int RunSync(IntPtr args, int sizeBytes)
    {
        _ = sizeBytes;

        string? gameDirectory = null;
        try
        {
            gameDirectory = Marshal.PtrToStringUni(args);
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return 10;

            return SyncRunner.Run(gameDirectory);
        }
        catch (Exception ex)
        {
            // Never let an exception escape into native code.
            TryLogCrash(gameDirectory, ex);
            return 1;
        }
    }

    /// <summary>Post-load step; same contract as RunSync, called after Reloaded initialized.</summary>
    public static int RunPostLoad(IntPtr args, int sizeBytes)
    {
        _ = sizeBytes;

        string? gameDirectory = null;
        try
        {
            gameDirectory = Marshal.PtrToStringUni(args);
            if (string.IsNullOrWhiteSpace(gameDirectory))
                return 10;

            return SyncRunner.PostLoad(gameDirectory);
        }
        catch (Exception ex)
        {
            TryLogCrash(gameDirectory, ex);
            return 1;
        }
    }

    private static void TryLogCrash(string? gameDirectory, Exception ex)
    {
        try
        {
            if (gameDirectory is null)
                return;
            var logPath = Path.Combine(gameDirectory, "reloaded-dropin", "logs", "sync.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[crash] {ex}{Environment.NewLine}");
        }
        catch
        {
            // Out of options; the native side will report the nonzero exit code.
        }
    }
}
