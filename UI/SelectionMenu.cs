namespace NetworkAdapterManager.UI;

/// <summary>A single row in a <see cref="SelectionMenu"/>.</summary>
internal sealed record MenuEntry(string Label, ConsoleColor Color);

/// <summary>
/// Generic, keyboard-driven (Up/Down/Enter/Esc) selection list used by every screen in the app.
/// </summary>
internal static class SelectionMenu
{
    /// <summary>
    /// Renders <paramref name="entries"/> and lets the user navigate with the arrow keys.
    /// Returns the selected index, or -1 if the user pressed Escape and <paramref name="allowEscape"/> is true.
    /// </summary>
    public static int Run(
        string title,
        IReadOnlyList<MenuEntry> entries,
        bool allowEscape = true,
        string? hint = null,
        string? subtitle = null)
    {
        if (entries.Count == 0)
            throw new ArgumentException("A menu needs at least one entry.", nameof(entries));

        var selected = 0;
        ConsoleKey key;

        Console.CursorVisible = false;

        do
        {
            Render(title, subtitle, entries, selected, hint);

            key = Console.ReadKey(true).Key;

            selected = key switch
            {
                ConsoleKey.UpArrow => (selected - 1 + entries.Count) % entries.Count,
                ConsoleKey.DownArrow => (selected + 1) % entries.Count,
                _ => selected
            };

            if (key == ConsoleKey.Escape && allowEscape)
                return -1;
        }
        while (key != ConsoleKey.Enter);

        return selected;
    }

    private static void Render(string title, string? subtitle, IReadOnlyList<MenuEntry> entries, int selected, string? hint)
    {
        Console.Clear();
        ConsoleTheme.WriteTitle(title);

        if (!string.IsNullOrWhiteSpace(subtitle))
            ConsoleTheme.WriteMuted(subtitle);

        Console.WriteLine();

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var isSelected = i == selected;

            if (isSelected)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = entry.Color;
                Console.WriteLine($" > {entry.Label} ");
            }
            else
            {
                Console.ForegroundColor = entry.Color;
                Console.WriteLine($"   {entry.Label} ");
            }

            Console.ResetColor();
        }

        Console.WriteLine();
        ConsoleTheme.WriteMuted(hint ?? "Use \u2191 \u2193 to navigate, Enter to select, Esc to go back.");
    }
}