using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FreeSpaceWatcher.Native;

/// <summary>The kernel32 and ntdll process functions the process controls call.</summary>
internal static partial class NativeMethods
{
    internal const uint ProcessTerminate = 0x0001;
    internal const uint ProcessSuspendResume = 0x0800;
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const int ErrorAccessDenied = 5;
    internal const int ErrorInvalidParameter = 87;
    internal const int ErrorInsufficientBuffer = 122;
    internal const int StatusAccessDenied = unchecked((int)0xC0000022);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        SafeProcessHandle process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime
    );

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref uint size);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int NtSuspendProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int NtResumeProcess(SafeProcessHandle process);
}
