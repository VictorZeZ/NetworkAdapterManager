using NetworkAdapterManager.Services;

namespace NetworkAdapterManager.UI;

internal static class InternetToggleFlow
{
    public static async Task RunAsync(AdapterService adapterService)
    {
        Console.Clear();
        var adapters = await LoadingAnimation.RunAsync("Checking Internet status", adapterService.GetAdaptersAsync());

        var internetAvailable = adapters.Any(a => a.HasInternet);

        var entries = new List<MenuEntry>
        {
            new(internetAvailable ? "Disable Internet Access" : "Enable Internet Access",
                internetAvailable ? ConsoleColor.Red : ConsoleColor.Green),
            new("Back", ConsoleColor.DarkGray)
        };

        var subtitle = $"Current status: {(internetAvailable ? "Internet is available" : "No Internet access")}";

        var choice = SelectionMenu.Run("ENABLE / DISABLE INTERNET", entries, allowEscape: true, subtitle: subtitle);

        if (choice is -1 or 1)
            return;

        if (internetAvailable)
        {
            if (!Confirm("This will disable ALL network adapters on this system. Continue? (Y/N): "))
                return;

            Console.Clear();
            Console.WriteLine("Disabling all network adapters...");
            await adapterService.DisableAllAdaptersAsync();
            Console.WriteLine();
            ConsoleTheme.WriteSuccess("Internet access has been disabled.");
        }
        else
        {
            Console.Clear();
            Console.WriteLine("Enabling all network adapters...");
            await adapterService.EnableAllAdaptersAsync();
            Console.WriteLine();
            ConsoleTheme.WriteSuccess("Internet access has been enabled.");
        }

        ConsoleTheme.WriteMuted("Press any key to continue...");
        Console.ReadKey(true);
    }

    private static bool Confirm(string message)
    {
        Console.Clear();
        ConsoleTheme.WriteWarning(message);
        var key = Console.ReadKey(true).Key;
        return key == ConsoleKey.Y;
    }
}