using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;

namespace Nova;

// Shared by every ambient watcher (files, terminal output, ...): a cheap
// Haiku call deciding whether a single trigger is worth proactively
// surfacing, before ever escalating to a real (Sonnet, full-conversation)
// turn. Per the roadmap, "the Haiku ambient gate sees only the current
// trigger" - not the conversation history - which is what keeps this cheap
// enough to run continuously. Fails closed: any error is treated as "not
// worth it," never as a reason to escalate.
internal static class HaikuGate
{
    public static async Task<bool> IsWorthSurfacingAsync(AnthropicClient client, string systemPrompt, string content, CancellationToken cancellationToken)
    {
        try
        {
            MessageCreateParams parameters = new()
            {
                MaxTokens = 10,
                Model = "claude-haiku-4-5-20251001",
                System = systemPrompt,
                Messages = [new MessageParam { Role = Role.User, Content = content }],
            };

            // UseWindowsForms=true adds a project-wide implicit `using
            // System.Windows.Forms;`, which collides with this SDK's own
            // Message type - fully qualified to disambiguate.
            Anthropic.Models.Messages.Message response = await client.Messages.Create(parameters, cancellationToken);
            string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : ""));
            return text.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // fail closed - never escalate on an error
        }
    }
}
