using NetworkAdapterManager.Services;

namespace NetworkAdapterManager.UI;

internal static class SwitchAdapterFlow
{
    public static async Task RunAsync(AdapterService adapterService)
    {
        Console.Clear();
        var adapters = await LoadingAnimation.RunAsync("Scanning network adapters", adapterService.GetAdaptersAsync());

        if (adapters.Count == 0)
        {
            ConsoleTheme.WriteError("No network adapters were found.");
            ConsoleTheme.WriteMuted("Press any key to go back...");
            Console.ReadKey(true);
            return;
        }

        var entries = new List<MenuEntry>(adapters.Count + 1);
        for (var i = 0; i < adapters.Count; i++)
        {
            var adapter = adapters[i];

            var label = $"{i + 1}. {adapter.Name}  [{adapter.Description}]";
            if (!string.IsNullOrEmpty(adapter.IPv4Address))
                label += $"  {adapter.IPv4Address}";
            if (adapter.IsActive)
                label += "  (Active)";
            if (!adapter.HasInternet)
                label += "  (No Internet)";

            var color = adapter.IsActive
                ? ConsoleColor.Yellow
                : adapter.HasInternet
                    ? ConsoleColor.Green
                    : ConsoleColor.DarkGray;

            entries.Add(new MenuEntry(label, color));
        }

        entries.Add(new MenuEntry("Back", ConsoleColor.DarkGray));

        var choice = SelectionMenu.Run("SWITCH NETWORK ADAPTER", entries, allowEscape: true);

        if (choice == -1 || choice == entries.Count - 1)
            return;

        var selected = adapters[choice];

        Console.Clear();
        ConsoleTheme.WriteTitle("SWITCH NETWORK ADAPTER");
        Console.WriteLine();
        Console.WriteLine($"Switching to \"{selected.Name}\"...");

        await adapterService.SwitchToAdapterAsync(selected);

        Console.WriteLine();
        ConsoleTheme.WriteSuccess($"\"{selected.Name}\" is now the active adapter.");
        ConsoleTheme.WriteMuted("Press any key to continue...");
        Console.ReadKey(true);
    }
}