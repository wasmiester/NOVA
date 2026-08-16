using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nova.ToolContract;

// The one contract shared between Nova.Core (the host) and every
// dynamically-authored, self-contained tool assembly - deliberately
// minimal ("given JSON input, produce a text result") so it basically
// never needs to change. That matters more than usual here: the host and
// a tool assembly resolve this interface from *different*
// AssemblyLoadContexts (see DynamicToolRuntime), and both sides need to
// agree on the exact same type identity for the interface cast to
// succeed - which only happens because this assembly itself is shared
// (loaded once, in the default context) rather than duplicated per tool.
public interface INovaTool
{
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, JsonElement> input, CancellationToken cancellationToken);
}
