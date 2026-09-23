using FreeSpaceWatcher.Native;

namespace FreeSpaceWatcher.Elevate;

/// <summary>Runs one process action as administrator for the tray, which starts it through a UAC prompt and reads its exit code.</summary>
/// <remarks>Exit codes: 0 when the action succeeded, 1 when it was refused or failed (the reason on stderr), 2 for a bad command line.</remarks>
internal static class Program
{
    private const int Succeeded = 0;
    private const int Failed = 1;
    private const int BadArguments = 2;

    private static int Main(string[] args)
    {
        if (ElevateArguments.Parse(args) is not ElevateRequest request)
        {
            Console.Error.WriteLine(ElevateArguments.Usage);
            return BadArguments;
        }

        ProcessControlResult result = ProcessGuard.Check(request.ProcessId, request.StartTime, Environment.ProcessId);
        if (result.Ok)
        {
            result = ProcessControl.Apply(request.ProcessId, request.Action);
        }

        if (result.Ok)
        {
            return Succeeded;
        }

        Console.Error.WriteLine(result.Error);
        return Failed;
    }
}
