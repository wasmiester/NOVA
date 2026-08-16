using System.Collections.Generic;
using System.Text.Json;

namespace Nova;

internal sealed record PendingToolCall(string Id, string Name, Dictionary<string, JsonElement> Input);
