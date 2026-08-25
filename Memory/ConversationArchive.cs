using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ElBruno.LocalEmbeddings;
using Microsoft.Data.Sqlite;

namespace Nova;

// Persistent archive of completed tasks - a separate file from
// data/memory.db (MemoryStore) deliberately: memory holds small, curated,
// durable facts meant to be found again; this holds raw per-task
// transcripts, much higher volume and a different retention shape, so it
// shouldn't share a table with (or slow down) memory's own search.
//
// Exists to keep NovaAssistant._conversation from growing without bound
// across a long-running session - once a task cleanly finishes, its whole
// exchange lands here and gets evicted from the live conversation (see
// NovaAssistant.ArchiveCompletedTaskAsync), replaced by a short rolling
// summary rather than either an ever-growing transcript or a hard reset
// that loses all continuity. search_conversation_history is how Claude
// reaches back into this on demand, the same way search_memory already
// works for durable facts - same brute-force cosine-similarity approach,
// for the same reasoning (see MemoryStore's own doc comment): nowhere near
// enough rows for a vector index to matter yet.
internal static class ConversationArchive
{
    public static void Initialize(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                summary TEXT NOT NULL,
                transcript TEXT NOT NULL,
                summary_embedding BLOB
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public static async Task Save(string dbPath, LocalEmbeddingGenerator embeddingGenerator, DateTime startedAtUtc, string summary, string transcript)
    {
        byte[] embeddingBytes = await EmbeddingMath.EmbedToBytesAsync(embeddingGenerator, summary);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO tasks (started_at, ended_at, summary, transcript, summary_embedding) VALUES ($startedAt, $endedAt, $summary, $transcript, $embedding)";
        cmd.Parameters.AddWithValue("$startedAt", startedAtUtc.ToString("o"));
        cmd.Parameters.AddWithValue("$endedAt", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$summary", summary);
        cmd.Parameters.AddWithValue("$transcript", transcript);
        cmd.Parameters.AddWithValue("$embedding", embeddingBytes);
        cmd.ExecuteNonQuery();
    }

    public static async Task<string> Search(string dbPath, LocalEmbeddingGenerator embeddingGenerator, IReadOnlyDictionary<string, JsonElement> input)
    {
        // Same tunable/reasoning as MemoryStore.Search - matched exactly
        // rather than picking a different number for a structurally
        // identical search.
        const float SemanticThreshold = 0.2f;
        const int MaxResults = 5;
        string query = input["query"].GetString()!;
        float[] queryVector = (await embeddingGenerator.GenerateEmbeddingAsync(query)).Vector.ToArray();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT started_at, summary, transcript, summary_embedding FROM tasks";
        using var reader = cmd.ExecuteReader();

        var scored = new List<(string StartedAt, string Summary, string Transcript, float Score)>();
        while (reader.Read())
        {
            string startedAt = reader.GetString(0);
            string summary = reader.GetString(1);
            string transcript = reader.GetString(2);

            bool keywordHit = summary.Contains(query, StringComparison.OrdinalIgnoreCase)
                || transcript.Contains(query, StringComparison.OrdinalIgnoreCase);
            float score = keywordHit ? 1f : 0f;

            if (!reader.IsDBNull(3))
            {
                float[] storedVector = EmbeddingMath.BytesToFloats(reader.GetFieldValue<byte[]>(3));
                float similarity = EmbeddingMath.CosineSimilarity(queryVector, storedVector);
                score = Math.Max(score, keywordHit ? score : similarity);
                if (!keywordHit && similarity < SemanticThreshold)
                {
                    continue;
                }
            }
            else if (!keywordHit)
            {
                continue;
            }

            scored.Add((startedAt, summary, transcript, score));
        }

        List<(string StartedAt, string Summary, string Transcript, float Score)> top = scored
            .OrderByDescending(r => r.Score)
            .Take(MaxResults)
            .ToList();

        if (top.Count == 0)
        {
            return "No matching past tasks found.";
        }

        // Full transcript only for the single best match, summary-only for
        // the rest - keeps a loosely-matching "did I do X recently" query
        // cheap even when several old tasks rank close together, while
        // still giving Claude the real detail for whichever one actually
        // matters most.
        var sb = new StringBuilder();
        for (int i = 0; i < top.Count; i++)
        {
            (string startedAt, string summary, string transcript, _) = top[i];
            sb.AppendLine(i == 0
                ? $"[{startedAt}] {summary}\nFull transcript:\n{transcript}"
                : $"[{startedAt}] {summary}");
        }

        return sb.ToString().TrimEnd();
    }

}
