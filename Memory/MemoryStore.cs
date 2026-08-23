using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ElBruno.LocalEmbeddings;
using Microsoft.Data.Sqlite;

namespace Nova;

// Hybrid retrieval: exact keyword/tag matches always rank first, backed up
// by cosine-similarity semantic search over locally-computed embeddings
// stored as BLOBs in the same table. A brute-force scan rather than a
// vector-search extension (sqlite-vec) - the memory table is realistically
// dozens to low hundreds of personal entries, never enough for that to
// matter, and it avoids taking on another native-extension compatibility
// risk (see the FlaUI/.NET-Framework-only saga a few steps back).
internal static class MemoryStore
{
    public static void Initialize(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    content TEXT NOT NULL,
                    tags TEXT NOT NULL DEFAULT '',
                    created_at TEXT NOT NULL
                );
                """;
            createCmd.ExecuteNonQuery();
        }

        // Migrate databases from before semantic search existed - they won't
        // have this column yet. Existing rows get NULL and are only findable by
        // keyword until re-saved; not backfilled, to avoid re-embedding
        // everything on every startup.
        if (!HasColumn(connection, "embedding"))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE memories ADD COLUMN embedding BLOB";
            alterCmd.ExecuteNonQuery();
        }

        // Tracks how many times a strategy-tagged memory got applied but
        // the task still ran inefficiently (see StrategyReflection) -
        // meaningless for every other row, which just carries the default
        // 0 and ignores it.
        if (!HasColumn(connection, "weak_count"))
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE memories ADD COLUMN weak_count INTEGER NOT NULL DEFAULT 0";
            alterCmd.ExecuteNonQuery();
        }
    }

    private static bool HasColumn(SqliteConnection connection, string columnName)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(memories)";
        using var reader = cmd.ExecuteReader();
        int nameOrdinal = reader.GetOrdinal("name");
        while (reader.Read())
        {
            if (reader.GetString(nameOrdinal) == columnName)
            {
                return true;
            }
        }

        return false;
    }

    // Reinforcement over duplication: a save that's near-identical to an
    // existing memory strengthens/updates that row instead of always
    // creating a new disconnected one - closer to how a person's
    // understanding sharpens on repetition instead of just piling up
    // equally-weighted, redundant facts (see the roadmap's "memory that
    // evolves" direction). Needs far more confidence than Search's own 0.2
    // relatedness floor below - this is deciding "this is the same fact,
    // restated," not "this is worth surfacing together with the query."
    private const float ReinforcementThreshold = 0.75f;

    public static async Task<string> Save(string dbPath, LocalEmbeddingGenerator embeddingGenerator, IReadOnlyDictionary<string, JsonElement> input)
    {
        string content = input["content"].GetString()!;
        string tags = ToolInput.GetString(input, "tags") ?? "";
        return await SaveContent(dbPath, embeddingGenerator, content, tags);
    }

    // Extracted from Save above so StrategyReflection can save a new or
    // distinct strategy directly, without needing to fake up a tool-call-
    // shaped input dictionary just to reach the same reinforcement logic.
    public static async Task<string> SaveContent(string dbPath, LocalEmbeddingGenerator embeddingGenerator, string content, string tags)
    {
        byte[] embeddingBytes = await EmbedToBytesAsync(embeddingGenerator, content);
        float[] embeddingVector = BytesToFloats(embeddingBytes);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        (long? existingId, string? existingTags) = FindReinforcementCandidate(connection, embeddingVector);
        if (existingId is { } id)
        {
            using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE memories SET content = $content, tags = $tags, created_at = $createdAt, embedding = $embedding WHERE id = $id";
            updateCmd.Parameters.AddWithValue("$content", content);
            updateCmd.Parameters.AddWithValue("$tags", MergeTags(existingTags ?? "", tags));
            updateCmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
            updateCmd.Parameters.AddWithValue("$embedding", embeddingBytes);
            updateCmd.Parameters.AddWithValue("$id", id);
            updateCmd.ExecuteNonQuery();
            return "Reinforced an existing memory (it looked like the same fact, restated) rather than saving a duplicate.";
        }

        using var insertCmd = connection.CreateCommand();
        insertCmd.CommandText = "INSERT INTO memories (content, tags, created_at, embedding) VALUES ($content, $tags, $createdAt, $embedding)";
        insertCmd.Parameters.AddWithValue("$content", content);
        insertCmd.Parameters.AddWithValue("$tags", tags);
        insertCmd.Parameters.AddWithValue("$createdAt", DateTime.UtcNow.ToString("o"));
        insertCmd.Parameters.AddWithValue("$embedding", embeddingBytes);
        insertCmd.ExecuteNonQuery();

        return "Saved to memory.";
    }

    private static (long? Id, string? Tags) FindReinforcementCandidate(SqliteConnection connection, float[] queryVector)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, tags, embedding FROM memories WHERE embedding IS NOT NULL";
        using var reader = cmd.ExecuteReader();

        long? bestId = null;
        string? bestTags = null;
        float bestScore = ReinforcementThreshold;
        while (reader.Read())
        {
            float similarity = CosineSimilarity(queryVector, BytesToFloats(reader.GetFieldValue<byte[]>(2)));
            if (similarity >= bestScore)
            {
                bestScore = similarity;
                bestId = reader.GetInt64(0);
                bestTags = reader.GetString(1);
            }
        }

        return (bestId, bestTags);
    }

    // Keeps whatever scope/topic tags the reinforced memory already had
    // (e.g. "durable") plus anything new this save added, rather than the
    // new save's tags silently replacing them.
    private static string MergeTags(string existingTags, string newTags)
    {
        IEnumerable<string> Split(string tags) => tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        List<string> merged = Split(existingTags).Concat(Split(newTags)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(", ", merged);
    }

    // Raw, unfiltered list of every memory carrying a given tag - no
    // semantic search, no scoring, no truncation. Built for StrategyRouter:
    // a "strategy" memory (a general, reusable approach lesson - see
    // Config/SystemPrompt.cs's tag guidance) needs to reach a real LLM
    // judgment call verbatim, not get pre-filtered by embedding similarity
    // first - that similarity search is exactly what fails to connect a
    // lesson to a same-shaped-but-differently-worded new task, which is
    // the whole reason the router exists instead of just calling Search.
    // Returns each row's id alongside its content - StrategyReflection
    // needs the id to track weak_count/rework a specific strategy once
    // JudgeAsync decides one is warranted, not just its text.
    public static List<(int Id, string Content)> GetByTag(string dbPath, string tag)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, content, tags FROM memories";
        using var reader = cmd.ExecuteReader();

        var results = new List<(int, string)>();
        while (reader.Read())
        {
            string tags = reader.GetString(2);
            IEnumerable<string> tagList = tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (tagList.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                results.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        return results;
    }

    // See StrategyReflection's own doc comment for the full flow this
    // supports. One or two inefficient uses of a strategy shouldn't
    // condemn it (a real one-off, an unusual edge case) - the threshold
    // for actually reworking one lives in code at the call site, not
    // here, so this just does the plain increment-and-report-back.
    public static int IncrementWeakCount(string dbPath, int id)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.CommandText = "UPDATE memories SET weak_count = weak_count + 1 WHERE id = $id";
            updateCmd.Parameters.AddWithValue("$id", id);
            updateCmd.ExecuteNonQuery();
        }

        using var readCmd = connection.CreateCommand();
        readCmd.CommandText = "SELECT weak_count FROM memories WHERE id = $id";
        readCmd.Parameters.AddWithValue("$id", id);
        object? result = readCmd.ExecuteScalar();
        return result is long count ? (int)count : 0;
    }

    // Called when a strategy is used and the task actually went well -
    // clears any prior misses rather than letting them linger toward a
    // rework the strategy no longer needs.
    public static void ResetWeakCount(string dbPath, int id)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE memories SET weak_count = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // Replaces an existing strategy's text in place (a rework, once
    // IncrementWeakCount's own threshold is hit) rather than saving a
    // second, competing memory - recomputes the embedding since the
    // content actually changed, and resets weak_count since this is a
    // fresh start for the reworked version.
    public static async Task UpdateStrategyContent(string dbPath, LocalEmbeddingGenerator embeddingGenerator, int id, string newContent)
    {
        byte[] embeddingBytes = await EmbedToBytesAsync(embeddingGenerator, newContent);

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE memories SET content = $content, embedding = $embedding, weak_count = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$content", newContent);
        cmd.Parameters.AddWithValue("$embedding", embeddingBytes);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public static async Task<string> Search(string dbPath, LocalEmbeddingGenerator embeddingGenerator, IReadOnlyDictionary<string, JsonElement> input)
    {
        // Tunable - a controlled test showed ~0.6 for genuinely related short sentences and ~0.02 for
        // unrelated ones, but a real over-long memory entry (a whole resume saved as one blob) scored
        // only ~0.13-0.28 against clearly-relevant queries, diluted by the bulk of the text. Lowered again
        // from 0.2 (itself already lowered once from 0.35) per the roadmap's "looser retrieval" direction -
        // a saved memory that's real but slightly off-phrase from the query being invisible is a worse
        // failure than an occasional lower-relevance result Claude can just disregard.
        const float SemanticThreshold = 0.15f;
        // Raised from 10, same direction/reasoning as the threshold above -
        // the table is realistically dozens to low hundreds of rows (see
        // the roadmap's own §5), nowhere near where a wider result set
        // costs anything meaningful.
        const int MaxResults = 15;
        string query = input["query"].GetString()!;
        float[] queryVector = (await embeddingGenerator.GenerateEmbeddingAsync(query)).Vector.ToArray();

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT content, tags, created_at, embedding FROM memories";
        using var reader = cmd.ExecuteReader();

        var scored = new List<(string Content, string Tags, string CreatedAt, float Score)>();
        while (reader.Read())
        {
            string content = reader.GetString(0);
            string tags = reader.GetString(1);
            string createdAt = reader.GetString(2);

            bool keywordHit = content.Contains(query, StringComparison.OrdinalIgnoreCase)
                || tags.Contains(query, StringComparison.OrdinalIgnoreCase);
            float score = keywordHit ? 1f : 0f;

            if (!reader.IsDBNull(3))
            {
                float[] storedVector = BytesToFloats(reader.GetFieldValue<byte[]>(3));
                float similarity = CosineSimilarity(queryVector, storedVector);
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

            scored.Add((content, tags, createdAt, score));
        }

        IEnumerable<string> results = scored
            .OrderByDescending(r => r.Score)
            .Take(MaxResults)
            .Select(r => r.Tags.Length > 0 ? $"[{r.CreatedAt}] ({r.Tags}) {r.Content}" : $"[{r.CreatedAt}] {r.Content}");

        return results.Any() ? string.Join("\n", results) : "No matching memories found.";
    }

    private static async Task<byte[]> EmbedToBytesAsync(LocalEmbeddingGenerator embeddingGenerator, string text)
    {
        float[] vector = (await embeddingGenerator.GenerateEmbeddingAsync(text)).Vector.ToArray();
        byte[] bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BytesToFloats(byte[] bytes)
    {
        float[] vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f;
        float normA = 0f;
        float normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
