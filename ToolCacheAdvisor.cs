using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;

namespace Nova;

// Decides, per call, whether reusing an already-cached free-tool result
// from earlier this task is actually safe - not a blind "identical query,
// reuse it" rule. That blind version was tried first and is a real
// inconsistency with an already-settled project principle (see
// docs/DESIGN_DECISIONS.md's self-contained-tools section): a stale answer
// is a worse failure than the cost of a fresh call for a free API, argued
// there with a restaurant-ETA example specifically because even a rare
// staleness risk (an accident changing an ETA seconds after it's fetched)
// isn't worth trading away when a fresh call is free. Same reasoning
// applies here - an email search, a calendar lookup, a file listing can
// all genuinely change mid-task, just less dramatically than a live ETA.
// This makes the actual judgment call cheaply (Haiku, same tier as
// HaikuGate/StrategyRouter) instead of either always reusing (risks a
// stale answer) or never reusing (back to paying for every duplicate
// round-trip in full). Fails toward freshness, not reuse - any error, or
// genuine doubt, means a real call happens, matching the settled
// principle's own default rather than overriding it.
internal static class ToolCacheAdvisor
{
    public static async Task<bool> IsSafeToReuseAsync(AnthropicClient client, string toolName, string input, TimeSpan age, CancellationToken cancellationToken)
    {
        try
        {
            MessageCreateParams parameters = new()
            {
                MaxTokens = 10,
                Model = "claude-haiku-4-5-20251001",
                System = GateSystemPrompt,
                Messages = [new MessageParam { Role = Role.User, Content = $"Tool: {toolName}\nInput: {input}\nResult was fetched {age.TotalSeconds:0} seconds ago." }],
            };

            // UseWindowsForms=true adds a project-wide implicit `using
            // System.Windows.Forms;`, which collides with this SDK's own
            // Message type - fully qualified to disambiguate (same as
            // HaikuGate/StrategyRouter/GmailClient).
            Anthropic.Models.Messages.Message response = await client.Messages.Create(parameters, cancellationToken);
            string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : ""));
            return text.TrimStart().StartsWith("YES", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // fail toward a fresh call, never toward reuse
        }
    }

    private const string GateSystemPrompt =
        "You decide whether it's safe to reuse a cached tool result instead of calling the tool again, for " +
        "a voice assistant. A fresh call is the default and generally preferred - only say it's safe to " +
        "reuse when the underlying data is genuinely unlikely to have meaningfully changed in the time " +
        "given (e.g. a calendar search from a few seconds ago, a file listing that hasn't had time to " +
        "change). Say NO whenever the data could plausibly be time-sensitive, real-time, or subject to " +
        "change on short notice (new mail arriving, a live status, a count, anything where being wrong " +
        "even rarely matters more than the time saved) - when genuinely unsure, say NO. Respond with " +
        "exactly YES or NO, no other text.";
}
