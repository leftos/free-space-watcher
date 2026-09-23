using System.Runtime.InteropServices;

namespace FreeSpaceWatcher.Service.Native;

/// <summary>Reads the NT device each drive letter points at.</summary>
public static partial class DosDevices
{
    private const int BufferLength = 1024;

    /// <summary>Returns the NT device a drive letter points at, e.g. <c>\Device\HarddiskVolume3</c>.</summary>
    /// <param name="letter">The drive letter.</param>
    /// <returns>The first target of the letter, or null when the letter is not defined.</returns>
    public static string? Query(char letter)
    {
        char[] buffer = new char[BufferLength];
        uint length = QueryDosDevice(letter + ":", buffer, BufferLength);
        if (length == 0)
        {
            return null;
        }

        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)length : end);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint QueryDosDevice(string deviceName, [Out] char[] targetPath, uint maxLength);
}
