using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TaskAssist.Core;

namespace TaskAssist.Desktop;

public sealed class OutlookClient
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    public async Task<OutlookReply> CallAsync(OutlookRequest request, CancellationToken cancellation = default)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "outlook-worker", "TaskAssist.OutlookWorker.exe");
        if (!File.Exists(executable)) return new() { Code = "worker_missing" };
        var name = "TaskAssist-" + Guid.NewGuid().ToString("N");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(name); start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process? process = null;
        try
        {
            if (cancellation.IsCancellationRequested) return new() { Code = "cancelled" };
            process = Process.Start(start);
            if (process is null) return new() { Code = "worker_start" };
            await pipe.WaitForConnectionAsync(timeout.Token);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var client) || client != process.Id) throw new IOException();
            await PipeProtocol.WriteAsync(pipe,request,timeout.Token);
            var reply = await PipeProtocol.ReadAsync<OutlookReply>(pipe,timeout.Token);
            if (reply.Protocol != 1) return new() { Code = "protocol" };
            return reply;
        }
        catch (OperationCanceledException) { return new() { Code = "timeout" }; }
        catch { return new() { Code = "unavailable" }; }
        finally
        {
            // Never enumerate/kill Outlook or other processes. Only terminate this exact child if still running.
            try { if (process is not null && !process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            finally { process?.Dispose(); }
        }
    }
}
