using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;

namespace Nova;

// Runs once at the start of a fresh task (see NovaAssistant.ProcessTextInputAsync),
// deciding whether any previously-saved "strategy" memory (a general,
// reusable approach lesson - see Config/SystemPrompt.cs's tag guidance)
// applies to what's about to be attempted. Solves a problem search_memory's
// embedding similarity can't: a genuinely general lesson ("pull broad
// instead of guessing narrow keywords") often shares no vocabulary at all
// with a brand new, differently-worded task it should still apply to (a
// delivery-tracking spreadsheet has nothing in common, by embedding, with
// the email-search lesson it should reuse) - recognizing that connection
// needs a real judgment call, not a similarity score. Deliberately reads
// the *entire* strategy list unfiltered (MemoryStore.GetByTag, no
// embedding pre-filter) rather than narrowing it first - pre-filtering by
// similarity would just reintroduce the exact blind spot this exists to
// route around. Same cost tier and fail-closed shape as HaikuGate: any
// error, or no match, just means proceeding exactly as today, without the
// extra context - never a reason to block or slow the task down further.
internal static class StrategyRouter
{
    // Returns the matched memory's id alongside its text - StrategyReflection
    // needs the id at task end to track weak_count/rework this exact
    // strategy, not just quote its text into context.
    public static async Task<(int Id, string Text)?> FindRelevantStrategyAsync(AnthropicClient client, string memoryDbPath, string taskDescription, CancellationToken cancellationToken)
    {
        List<(int Id, string Content)> strategies = MemoryStore.GetByTag(memoryDbPath, "strategy");
        if (strategies.Count == 0)
        {
            return null;
        }

        try
        {
            string numbered = string.Join("\n", strategies.Select((s, i) => $"{i + 1}. {s.Content}"));
            MessageCreateParams parameters = new()
            {
                MaxTokens = 10,
                Model = "claude-haiku-4-5-20251001",
                System = GateSystemPrompt,
                Messages = [new MessageParam { Role = Role.User, Content = $"Task: {taskDescription}\n\nStrategies:\n{numbered}" }],
            };

            // UseWindowsForms=true adds a project-wide implicit `using
            // System.Windows.Forms;`, which collides with this SDK's own
            // Message type - fully qualified to disambiguate (same as
            // HaikuGate/GmailClient).
            Anthropic.Models.Messages.Message response = await client.Messages.Create(parameters, cancellationToken);
            string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : "")).Trim();
            if (int.TryParse(text, out int index) && index >= 1 && index <= strategies.Count)
            {
                return strategies[index - 1];
            }

            return null;
        }
        catch
        {
            return null; // fail closed - never block the task on a router error
        }
    }

    private const string GateSystemPrompt =
        "You match a new task against a short list of previously-learned strategies for a voice " +
        "assistant. Given a task description and a numbered list of general approach lessons, respond " +
        "with ONLY the number of the single strategy that clearly applies, if one genuinely does - a " +
        "real match in approach or underlying shape, even if the topic is completely different (e.g. an " +
        "email-search lesson can apply to a spreadsheet task if the underlying problem has the same " +
        "shape), not a loose or superficial connection. If none clearly apply, respond with exactly " +
        "NONE. No other text.";
}
