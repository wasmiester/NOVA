// NovaTerminalRelay: a small standalone console app, launched by the main
// Nova process as a separate child process purely to get a real, separate
// console window the user can type into normally - Nova's own console is
// already busy being the conversation log. Spawns cmd.exe with redirected
// pipes (plain-text pass-through, not a full ConPTY): colors and
// interactive full-screen tools (vim, an interactive rebase, etc.) won't
// behave right, but build/test/git output works fine, which is the actual
// point of this. Relays keystrokes in and output back out to this window
// like a normal terminal, and additionally mirrors every output chunk
// across a named pipe back to the main Nova process so she can watch along.
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

if (args.Length < 1)
{
    Console.WriteLine("Usage: NovaTerminalRelay <pipeName>");
    return;
}

string pipeName = args[0];
Console.Title = "Nova-watched terminal";
Console.WriteLine("=== Nova is watching this terminal - plain text only, no colors or interactive tools (vim, etc.) ===");
Console.WriteLine();

var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
StreamWriter? pipeWriter = null;
try
{
    await pipeClient.ConnectAsync(2000);
    pipeWriter = new StreamWriter(pipeClient) { AutoFlush = true };
}
catch
{
    // Main Nova process isn't listening (maybe it exited) - the terminal
    // still works standalone, just without anything watching it.
    Console.WriteLine("(Couldn't connect back to Nova - this terminal will work, but she won't be watching it.)");
    Console.WriteLine();
}

var psi = new ProcessStartInfo("cmd.exe")
{
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
};

using Process shell = Process.Start(psi)!;

async Task RelayOutputAsync(StreamReader reader)
{
    var buffer = new char[1024];
    while (true)
    {
        int read = await reader.ReadAsync(buffer.AsMemory());
        if (read == 0)
        {
            return;
        }

        string chunk = new(buffer, 0, read);
        Console.Write(chunk);
        if (pipeWriter is not null)
        {
            try
            {
                await pipeWriter.WriteAsync(chunk);
            }
            catch
            {
                pipeWriter = null; // Nova disconnected - keep the terminal itself working
            }
        }
    }
}

Task stdoutTask = RelayOutputAsync(shell.StandardOutput);
Task stderrTask = RelayOutputAsync(shell.StandardError);

_ = Task.Run(async () =>
{
    var buffer = new char[256];
    while (true)
    {
        int read = await Console.In.ReadAsync(buffer.AsMemory());
        if (read == 0)
        {
            return;
        }

        await shell.StandardInput.WriteAsync(buffer.AsMemory(0, read));
        await shell.StandardInput.FlushAsync();
    }
});

await shell.WaitForExitAsync();
await Task.WhenAll(stdoutTask, stderrTask);

Console.WriteLine();
Console.WriteLine("[shell exited - press Enter to close this window]");
Console.ReadLine();
