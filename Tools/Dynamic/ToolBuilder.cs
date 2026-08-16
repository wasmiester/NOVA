using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nova;

// Orchestrates the full build_tool flow: validate the name, write the
// generated project + source to disk under SelfContainedTools/, build it
// (DynamicToolRuntime), and - only on a successful build - commit it
// (ToolGitVersioning) and register/update it (ToolRegistry). A failed
// build is reported back as-is (compiler output included) with nothing
// committed or registered, so Claude can see exactly what went wrong and
// try again rather than the registry ever pointing at a tool that doesn't
// actually compile.
internal static class ToolBuilder
{
    public static async Task<(string Message, bool Success)> BuildAsync(
        string toolsRootDir,
        string toolRegistryDbPath,
        string contractDllPath,
        string name,
        string description,
        string inputSchemaJson,
        string sourceCode,
        bool usesPaidApi,
        bool hasExternalEffects,
        CancellationToken cancellationToken)
    {
        if (!IsValidToolName(name))
        {
            return ("Invalid tool name - use only letters, digits, and underscores, starting with a letter (e.g. \"weather_lookup\"), 64 characters or fewer.", false);
        }

        ToolGitVersioning.EnsureRepo(toolsRootDir);

        string projectDir = Path.Combine(toolsRootDir, name);
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, $"{name}.csproj"), BuildProjectFile(name, contractDllPath));
        File.WriteAllText(Path.Combine(projectDir, $"{name}.cs"), sourceCode);

        (bool success, string output, string? dllPath) = await DynamicToolRuntime.BuildAsync(projectDir, $"{name}.csproj", cancellationToken);
        if (!success || dllPath is null)
        {
            return ($"Build failed for tool \"{name}\" - nothing was registered, the previous working version (if any) is untouched:\n{output}", false);
        }

        string commit = await ToolGitVersioning.CommitAsync(toolsRootDir, name, $"Build {name}", cancellationToken);
        ToolRegistry.Upsert(toolRegistryDbPath, name, description, inputSchemaJson, projectDir, dllPath, commit, usesPaidApi, hasExternalEffects);

        return ($"Tool \"{name}\" built successfully and is ready to use via run_tool. " +
                "Its first real run still needs your go-ahead before it actually executes" +
                (usesPaidApi ? ", and that review will flag that this tool uses a paid API." : ".") +
                (hasExternalEffects ? " Since it has a real external effect, every run (not just the first) will need a Gate 2 review before it executes." : ""), true);
    }

    // Self-repair: called after a tool has failed 3 times in a row within
    // the same task (see NovaAssistant's per-task failure tracking).
    // Reverting means the *source* steps back one git version and gets
    // rebuilt from that - the .dll on disk before this call reflects the
    // now-failing version, not the one being reverted to, so a rebuild is
    // required, not just a git checkout.
    public static async Task<string> RevertAsync(string toolsRootDir, string toolRegistryDbPath, string name, CancellationToken cancellationToken)
    {
        string? newCommit = await ToolGitVersioning.RevertOneVersionAsync(toolsRootDir, name, cancellationToken);
        if (newCommit is null)
        {
            return $"The \"{name}\" tool has failed 3 times in a row, but there's no earlier version to revert to.";
        }

        string projectDir = Path.Combine(toolsRootDir, name);
        (bool success, string output, string? dllPath) = await DynamicToolRuntime.BuildAsync(projectDir, $"{name}.csproj", cancellationToken);
        if (!success || dllPath is null)
        {
            return $"The \"{name}\" tool failed 3 times in a row and the previous version it reverted to doesn't build either:\n{output}";
        }

        ToolRegistry.UpdateAfterRevert(toolRegistryDbPath, name, dllPath, newCommit);
        return $"The \"{name}\" tool started failing, so I reverted it to its previous working version.";
    }

    private static bool IsValidToolName(string name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= 64
        && char.IsAsciiLetter(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static string BuildProjectFile(string name, string contractDllPath) =>
        $"""
        <Project Sdk="Microsoft.NET.Sdk">

          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
            <AssemblyName>{name}</AssemblyName>
          </PropertyGroup>

          <ItemGroup>
            <Reference Include="Nova.ToolContract">
              <HintPath>{contractDllPath}</HintPath>
            </Reference>
          </ItemGroup>

        </Project>
        """;
}
