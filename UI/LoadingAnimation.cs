namespace NetworkAdapterManager.UI;

/// <summary>Shows a small spinner on the current console line while an awaited task runs.</summary>
internal static class LoadingAnimation
{
    private static readonly char[] Frames = ['|', '/', '-', '\\'];

    public static async Task<T> RunAsync<T>(string message, Task<T> work)
    {
        Console.CursorVisible = false;
        var frame = 0;

        while (!work.IsCompleted)
        {
            Console.Write($"\r{message}... {Frames[frame % Frames.Length]}");
            frame++;
            await Task.Delay(120);
        }

        Console.Write($"\r{new string(' ', message.Length + 6)}\r");

        return await work;
    }
}