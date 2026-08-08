using NetworkAdapterManager.Services;

namespace NetworkAdapterManager.UI;

internal static class MainMenu
{
    public static async Task RunAsync(AdapterService adapterService)
    {
        while (true)
        {
            var entries = new List<MenuEntry>
            {
                new("Switch Network Adapter", ConsoleColor.Cyan),
                new("Enable / Disable Internet", ConsoleColor.Cyan),
                new("Exit", ConsoleColor.DarkGray)
            };

            var choice = SelectionMenu.Run(
                "ADAPTER MANAGER",
                entries,
                allowEscape: true,
                hint: "Use \u2191 \u2193 to navigate, Enter to select, Esc to exit.");

            if (choice is -1 or 2)
                return;

            switch (choice)
            {
                case 0:
                    await SwitchAdapterFlow.RunAsync(adapterService);
                    break;
                case 1:
                    await InternetToggleFlow.RunAsync(adapterService);
                    break;
            }
        }
    }
}