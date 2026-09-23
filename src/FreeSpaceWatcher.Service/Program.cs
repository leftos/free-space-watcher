using FreeSpaceWatcher.Core.Ipc;
using FreeSpaceWatcher.Service;

return await ServiceHost.RunAsync(args, PipeProtocol.PipeName);
