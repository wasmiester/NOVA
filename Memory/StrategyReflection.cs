using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Anthropic;
using Anthropic.Models.Messages;

namespace Nova;

// Runs once at the end of every task (see NovaAssistant.ArchiveCompletedTaskAsync),
// closing the loop StrategyRouter opens at the start: judges whether the
// approach actually taken was efficient for what the task needed, then
// lets the caller decide whether a strategy memory should be created,
// reinforced, or reworked as a result - the same "notice how you solved
// something, not just what you learned" instinct save_memory already asks
// Claude for, just applied automatically instead of depending on her
// remembering to do it mid-task.
//
// Two-call shape, same cost-tiering as StrategyRouter/HaikuGate: a cheap
// classification call (JudgeAsync) runs on every non-trivial task, and the
// more expensive step - actually writing new or reworked strategy text
// (WriteStrategyAsync) - only runs on the rarer occasions the
// classification says it's actually warranted, not on every task.
//
// The "how many misses before a rework" threshold deliberately lives in
// code at the call site, not in the classifier's own judgment here -
// JudgeAsync only ever answers "was *this* task's approach efficient,"
// never asked to track a running count itself, since trusting a model to
// count reliably across separate calls is a worse bet than a plain
// integer column (see MemoryStore.IncrementWeakCount).
internal static class StrategyReflection
{
    public enum Judgment
    {
        Skip,
        Efficient,
        Inefficient,
        Mismatched,
    }

    public static async Task<Judgment> JudgeAsync(AnthropicClient client, string transcriptText, int roundCount, bool strategyWasActive, CancellationToken cancellationToken)
    {
        // A single-round task (a quick lookup, a direct answer) isn't
        // complex enough for a strategy to matter either way - skip the
        // call entirely rather than spend tokens classifying the obvious.
        if (roundCount < 2)
        {
            return Judgment.Skip;
        }

        try
        {
            string context = strategyWasActive
                ? "A saved strategy was matched and used for this task."
                : "No saved strategy was used for this task - it ran from scratch.";
            string prompt =
                $"{context} The task took {roundCount} rounds of tool calls. Judge the approach actually " +
                "taken, not just whether the task eventually succeeded:\n\n" +
                "EFFICIENT - the approach was reasonable for what the task actually needed (broad, batched " +
                "searches; no repeated or wasted calls).\n" +
                "INEFFICIENT - the task succeeded but the approach was needlessly slow or wasteful (repeated " +
                "calls one item at a time, redundant searches, guessing narrow terms instead of a broad " +
                "pull).\n" +
                "MISMATCHED - a strategy was used but didn't actually fit this task's real shape, so it was " +
                "the wrong lesson to reach for here (only ever answer this if a strategy was actually used).\n" +
                "SKIP - the task was too trivial, unusual, or inconclusive to learn anything general from " +
                "either way.\n\n" +
                "Respond with exactly one of those four words, no other text.\n\nTranscript:\n" + transcriptText;

            MessageCreateParams parameters = new()
            {
                MaxTokens = 10,
                Model = "claude-haiku-4-5-20251001",
                Messages = [new MessageParam { Role = Role.User, Content = prompt }],
            };

            // UseWindowsForms=true adds a project-wide implicit `using
            // System.Windows.Forms;`, which collides with this SDK's own
            // Message type - fully qualified to disambiguate (same as
            // HaikuGate/StrategyRouter/GmailClient).
            Anthropic.Models.Messages.Message response = await client.Messages.Create(parameters, cancellationToken);
            string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : "")).Trim();
            return text.ToUpperInvariant() switch
            {
                "EFFICIENT" => Judgment.Efficient,
                "INEFFICIENT" => Judgment.Inefficient,
                "MISMATCHED" => Judgment.Mismatched,
                _ => Judgment.Skip,
            };
        }
        catch
        {
            return Judgment.Skip; // fail closed - never write anything on a reflection error
        }
    }

    // Only called when JudgeAsync's result actually calls for new or
    // reworked strategy text - the more expensive half of this two-call
    // shape, so it stays rare in practice. Uses Sonnet rather than Haiku
    // on purpose: this text gets reused across every future task
    // StrategyRouter matches it against, so its quality compounds in a
    // way JudgeAsync's one-off categorical answer doesn't - worth paying
    // for a stronger model on the rare calls that actually reach here.
    public static async Task<string?> WriteStrategyAsync(AnthropicClient client, string transcriptText, CancellationToken cancellationToken)
    {
        try
        {
            string prompt =
                "Write a short, reusable strategy describing the approach that worked for the task below - " +
                "one to three sentences, phrased so it generalizes to a different task with the same " +
                "underlying shape (the same test a person would apply: does it still make sense if you swap " +
                "out every specific name, company, or file for a different one?). Describe the *approach*, " +
                "not this specific task's outcome. No preamble, just the strategy text itself.\n\nTranscript:\n" + transcriptText;

            MessageCreateParams parameters = new()
            {
                MaxTokens = 200,
                Model = "claude-sonnet-5",
                Messages = [new MessageParam { Role = Role.User, Content = prompt }],
            };

            Anthropic.Models.Messages.Message response = await client.Messages.Create(parameters, cancellationToken);
            string text = string.Concat(response.Content.Select(block => block.TryPickText(out var textBlock) ? textBlock.Text : "")).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
