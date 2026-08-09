using NetworkAdapterManager.Services;
using NetworkAdapterManager.UI;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

// This app only runs on Windows (WMI adapter control, WindowsIdentity, etc.).
// Declaring it here means every call site in the project is treated as
// Windows-only by the platform-compatibility analyzer, instead of needing
// [SupportedOSPlatform("windows")] scattered across every method that touches
// System.Management or System.Security.Principal.
[assembly: SupportedOSPlatform("windows")]

namespace NetworkAdapterManager;

internal static class Program
{
    // Windows' ERROR_CANCELLED: returned when the user clicks "No" on the UAC prompt.
    private const int ErrorCancelled = 1223;

    private static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.Title = "Adapter Manager";
        Console.CursorVisible = false;

        if (!OperatingSystem.IsWindows())
        {
            ConsoleTheme.WriteError("This application only supports Windows.");
            return;
        }

        if (!AdapterService.IsRunningAsAdministrator())
        {
            // Either the user approves elevation (a new elevated process is started and this
            // one exits) or they don't (nothing further should run). Either way, stop here.
            TryRelaunchElevated();
            return;
        }

        var adapterService = new AdapterService();

        try
        {
            await MainMenu.RunAsync(adapterService);
        }
        finally
        {
            Console.CursorVisible = true;
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Explains why elevation is needed and, if the user agrees, restarts the application
    /// as Administrator via a UAC prompt. Never lets the current, unelevated process continue.
    /// </summary>
    private static void TryRelaunchElevated()
    {
        ConsoleTheme.WriteWarning("Adapter Manager is not running as Administrator.");
        ConsoleTheme.WriteWarning("Managing network adapters requires elevated privileges.");
        Console.WriteLine();
        ConsoleTheme.WriteMuted("Press R to restart as Administrator, or any other key to exit.");

        var key = Console.ReadKey(true).Key;
        if (key != ConsoleKey.R)
        {
            ConsoleTheme.WriteMuted("Exiting.");
            return;
        }

        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            ConsoleTheme.WriteError("Could not determine the application path to restart.");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true,
            Verb = "runas"
        };

        // Preserve whatever arguments this instance was started with.
        foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            startInfo.ArgumentList.Add(arg);

        try
        {
            Process.Start(startInfo);
            ConsoleTheme.WriteMuted("Restarting as Administrator...");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            ConsoleTheme.WriteError("Elevation was cancelled. Exiting.");
        }
        catch (Exception ex)
        {
            ConsoleTheme.WriteError($"Failed to restart as Administrator: {ex.Message}");
        }
    }
}