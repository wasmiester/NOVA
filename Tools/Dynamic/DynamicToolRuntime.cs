using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nova.ToolContract;

namespace Nova;

// The actual mechanics of building and running a self-contained tool -
// compiling its project via `dotnet build`, then loading/invoking/
// unloading its compiled assembly. Two separate concerns kept in one file
// since both only exist to serve "run one tool, once, cleanly":
//   - BuildAsync: `dotnet build`, same subprocess pattern as
//     CommandRunner.RunCommandAsync, just a different executable/timeout.
//   - RunAsync: loads the built .dll into its own collectible
//     AssemblyLoadContext, invokes its INovaTool implementation, then
//     unloads and *verifies* the unload actually completed - see the two
//     caveats called out when this was designed:
//       1. Type identity across the load boundary - handled by
//          CollectibleToolLoadContext.Load returning null for everything
//          except the tool's own primary assembly, which makes the
//          runtime fall back to resolving Nova.ToolContract (and
//          System.*) from the *default* context, where the host already
//          loaded them - so INovaTool means the same type on both sides.
//       2. Not hanging around after use - a tool is loaded, invoked, and
//          unloaded on every single call (see NovaAssistant's run_tool
//          handler), never cached/kept warm between calls. The unload is
//          verified via a WeakReference + a bounded GC-collect loop
//          (Microsoft's own documented pattern for collectible
//          AssemblyLoadContext) rather than trusting Unload() blindly -
//          if something still held a reference, that's logged instead of
//          silently leaking memory across a long-running session.
internal static class DynamicToolRuntime
{
    public static async Task<(bool Success, string Output, string? DllPath)> BuildAsync(string projectDir, string projectFileName, CancellationToken cancellationToken)
    {
        // A cold build (first NuGet restore for a brand new tool project)
        // is slower than a normal shell command - CommandRunner's 30s
        // budget is too tight here, confirmed by a real first-build taking
        // well past that during testing.
        ProcessRunner.ProcessResult result = await ProcessRunner.RunAsync(
            "dotnet", $"build \"{projectFileName}\" -c Debug", projectDir, TimeSpan.FromSeconds(120), cancellationToken);
        if (result.Outcome == ProcessRunner.RunOutcome.TimedOut)
        {
            return (false, "Build timed out after 120 seconds.", null);
        }

        const int MaxChars = 4000;
        string stdout = ProcessRunner.Truncate(result.Stdout, MaxChars);
        string stderr = ProcessRunner.Truncate(result.Stderr, MaxChars);
        string output = $"stdout:\n{stdout}\nstderr:\n{stderr}";

        if (result.ExitCode != 0)
        {
            return (false, output, null);
        }

        string? dllPath = FindBuiltDll(projectDir, Path.GetFileNameWithoutExtension(projectFileName));
        return dllPath is null
            ? (false, output + "\n(Build reported success but the compiled .dll couldn't be located.)", null)
            : (true, output, dllPath);
    }

    // Same "don't rely solely on whatever token the caller happened to
    // pass in" reasoning as BuildAsync's own 120s budget - a self-contained
    // tool is arbitrary Claude-generated code, and unlike BuildAsync's
    // subprocess (which can be hard-killed by the OS regardless of whether
    // it cooperates), a hung *in-process* call can only ever be bounded by
    // a cancellation token a tool actually checks - there's no
    // Process.Kill-equivalent for managed code sharing this process. This
    // still closes the gap for any normally-behaved tool (one that awaits
    // real I/O, which observes cancellation the way .NET async always
    // does); it doesn't protect against a tool spinning in a tight
    // synchronous loop that never yields, which nothing short of a
    // separate process could actually preempt.
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(60);

    // Everything above this point must not leak a reference to the loaded
    // assembly/context/tool instance past RunAsync's own return - that's
    // what makes the unload below actually collectible rather than a slow
    // memory leak across a long session.
    public static async Task<string> RunAsync(string dllPath, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(ExecutionTimeout);

        string result;
        WeakReference contextRef;
        try
        {
            (result, contextRef) = await ExecuteInIsolatedContextAsync(dllPath, input, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return $"Tool timed out after {ExecutionTimeout.TotalSeconds:0} seconds.";
        }

        for (int i = 0; contextRef.IsAlive && i < 10; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        if (contextRef.IsAlive)
        {
            ErrorLog.Log("DynamicToolRuntime.RunAsync", new InvalidOperationException(
                $"Tool assembly at {dllPath} did not unload after execution - something is still holding a reference to it."));
        }

        return result;
    }

    // NoInlining matters here - if the JIT inlined this into RunAsync, its
    // locals (context/assembly/instance) could stay reachable from
    // RunAsync's own stack frame longer than expected, which can prevent
    // the context from ever becoming collectible. This is the documented
    // shape for verifiably-unloadable AssemblyLoadContext usage, not a
    // stylistic choice.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(string Result, WeakReference ContextRef)> ExecuteInIsolatedContextAsync(string dllPath, IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken)
    {
        var context = new CollectibleToolLoadContext();
        string result;
        try
        {
            Assembly assembly = context.LoadFromAssemblyPath(dllPath);
            Type toolType = assembly.GetTypes().FirstOrDefault(t => typeof(INovaTool).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract)
                ?? throw new InvalidOperationException($"No INovaTool implementation found in {Path.GetFileName(dllPath)}.");
            var instance = (INovaTool)Activator.CreateInstance(toolType)!;
            result = await instance.ExecuteAsync(input, cancellationToken);
        }
        finally
        {
            context.Unload();
        }

        return (result, new WeakReference(context));
    }

    private static string? FindBuiltDll(string projectDir, string assemblyName)
    {
        string binDir = Path.Combine(projectDir, "bin", "Debug");
        if (!Directory.Exists(binDir))
        {
            return null;
        }

        // Search rather than hardcode the exact TFM subfolder (e.g.
        // "net10.0") - resilient to whichever target framework a
        // generated tool project actually specifies.
        return Directory.EnumerateFiles(binDir, $"{assemblyName}.dll", SearchOption.AllDirectories).FirstOrDefault();
    }

    private sealed class CollectibleToolLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }
}
