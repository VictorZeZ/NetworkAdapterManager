using NetworkAdapterManager.Services;
using NetworkAdapterManager.UI;
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
            ConsoleTheme.WriteWarning("Warning: Adapter Manager is not running as Administrator.");
            ConsoleTheme.WriteWarning("Switching adapters and toggling Internet access will likely fail.");
            Console.WriteLine();
            ConsoleTheme.WriteMuted("Press any key to continue anyway, or close this window and re-run as Administrator...");
            Console.ReadKey(true);
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
}