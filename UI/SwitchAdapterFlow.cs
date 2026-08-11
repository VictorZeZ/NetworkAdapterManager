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

            ConsoleColor color;

            if (!adapter.IsEnabled)
            {
                // A disabled adapter has no IP configuration left to check, so we fall back to
                // what we last saw while it WAS enabled (e.g. right before this app disabled it
                // as part of an earlier switch) rather than treating every disabled adapter the
                // same way.
                if (adapter.LastKnownHasInternet == true)
                {
                    label += "  (Disabled \u2014 had Internet access; select to re-enable)";
                    color = ConsoleColor.Blue;
                }
                else
                {
                    label += "  (Disabled)";
                    color = ConsoleColor.DarkGray;
                }
            }
            else
            {
                if (adapter.IsActive)
                    label += "  (Active)";
                if (!adapter.HasInternet)
                    label += "  (No Internet)";

                if (adapter.IsActive && !adapter.HasInternet)
                    label += " \u2014 this is why you're offline right now; pick an adapter with Internet access instead.";

                color = (adapter.IsActive, adapter.HasInternet) switch
                {
                    (true, true) => ConsoleColor.Green,     // active and online
                    (false, true) => ConsoleColor.Yellow,   // online, but not the active one
                    (true, false) => ConsoleColor.Red,      // active, but no Internet
                    (false, false) => ConsoleColor.DarkGray // neither active nor online
                };
            }

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
        Console.WriteLine($"Enabling \"{selected.Name}\" and waiting for it to come online...");

        var success = await adapterService.SwitchToAdapterAsync(selected);

        Console.WriteLine();
        if (success)
        {
            ConsoleTheme.WriteSuccess($"\"{selected.Name}\" is now the active adapter.");
            ConsoleTheme.WriteMuted("Other adapters have been disabled; select one again to re-enable it.");
        }
        else
        {
            ConsoleTheme.WriteError($"\"{selected.Name}\" did not come online in time, so nothing else was changed.");
            ConsoleTheme.WriteMuted("Your previous adapter setup should still be intact. Try again in a moment.");
        }

        ConsoleTheme.WriteMuted("Press any key to continue...");
        Console.ReadKey(true);
    }
}