namespace FreeSpaceWatcher.Elevate;

internal static class Program
{
    private static int Main()
    {
        Console.Error.WriteLine("Usage: FreeSpaceWatcher.Elevate suspend|resume|kill <pid> [<process start time>]");
        return 2;
    }
}
