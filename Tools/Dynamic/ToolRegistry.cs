using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Nova;

// One row per self-contained tool Nova has built (see ToolBuilder) - name,
// description/input schema (what Claude needs to call it later via
// run_tool/list_tools), where its source/compiled .dll live, which git
// commit that build corresponds to (see ToolGitVersioning), whether it's
// been approved yet (see the Gate 2 note on DynamicToolRuntime/
// NovaAssistant.ExecuteToolAsync - a brand new tool needs review before
// its first real run; an already-approved one doesn't need re-approving
// just to be reused), whether it calls a paid/metered API (self-declared
// by Claude at build_tool time - surfaced in that same first-run Gate 2
// review, since that's the point real cost actually starts), whether it
// has a real external effect on every call rather than just being read-
// only (self-declared, same pattern as UsesPaidApi - the GET vs POST/PUT
// distinction: a read-only tool is trusted for good once approved, but one
// that actually does something (sends, posts, orders, modifies external
// state) needs the same per-call Gate 2 review any built-in Gate 2 tool
// gets, not just a one-time first-run check - see NovaAssistant's gate2
// classification for run_tool), and a same-task consecutive-failure
// counter for self-repair (3 in a row triggers
// ToolGitVersioning.RevertOneVersion).
//
// Separate file from memory.db/conversation-archive.db, same reasoning as
// those two keeping apart from each other - different shape, different
// retention, no reason to share a table or slow each other down.
internal sealed record ToolRecord(
    string Name,
    string Description,
    string InputSchemaJson,
    string ProjectDir,
    string DllPath,
    string GitCommit,
    bool Approved,
    bool UsesPaidApi,
    bool HasExternalEffects,
    int ConsecutiveFailures);

internal static class ToolRegistry
{
    public static void Initialize(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS tools (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                description TEXT NOT NULL,
                input_schema_json TEXT NOT NULL,
                project_dir TEXT NOT NULL,
                dll_path TEXT NOT NULL,
                git_commit TEXT NOT NULL,
                approved INTEGER NOT NULL DEFAULT 0,
                uses_paid_api INTEGER NOT NULL DEFAULT 0,
                has_external_effects INTEGER NOT NULL DEFAULT 0,
                consecutive_failures INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();

        // Migrate a registry created before a given column existed - same
        // pattern MemoryStore uses for its own embedding column.
        AddColumnIfMissing(connection, "uses_paid_api");
        AddColumnIfMissing(connection, "has_external_effects");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string columnName)
    {
        bool hasColumn = false;
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info(tools)";
            using var reader = pragmaCmd.ExecuteReader();
            int nameOrdinal = reader.GetOrdinal("name");
            while (reader.Read())
            {
                if (reader.GetString(nameOrdinal) == columnName)
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            using var alterCmd = connection.CreateCommand();
            alterCmd.CommandText = $"ALTER TABLE tools ADD COLUMN {columnName} INTEGER NOT NULL DEFAULT 0";
            alterCmd.ExecuteNonQuery();
        }
    }

    // Insert-or-update-by-name: a brand new tool gets a fresh row. An
    // update (Nova revising an existing tool after a failure, or the user
    // asking for a change) keeps the same row but resets BOTH
    // consecutive_failures (a just-rebuilt version hasn't failed yet) AND
    // approved (a rebuild can contain genuinely different, unreviewed code
    // even though the name is unchanged - reusing a name must never let a
    // rewritten tool silently inherit trust from whatever the name used to
    // point at, which is exactly how a Gate 2 review could otherwise get
    // skipped entirely on a tool that looks identical from the outside but
    // isn't). This only affects the normal build_tool path - self-repair's
    // RevertAsync goes through UpdateAfterRevert instead, which deliberately
    // leaves approved untouched, since reverting to an already-approved
    // version is retreating to safety, not advancing to something new (see
    // the roadmap's Gate 2 asymmetry note).
    public static void Upsert(string dbPath, string name, string description, string inputSchemaJson, string projectDir, string dllPath, string gitCommit, bool usesPaidApi, bool hasExternalEffects)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO tools (name, description, input_schema_json, project_dir, dll_path, git_commit, approved, uses_paid_api, has_external_effects, consecutive_failures, created_at, updated_at)
            VALUES ($name, $description, $schema, $projectDir, $dllPath, $commit, 0, $usesPaidApi, $hasExternalEffects, 0, $now, $now)
            ON CONFLICT(name) DO UPDATE SET
                description = $description,
                input_schema_json = $schema,
                project_dir = $projectDir,
                dll_path = $dllPath,
                git_commit = $commit,
                uses_paid_api = $usesPaidApi,
                has_external_effects = $hasExternalEffects,
                approved = 0,
                consecutive_failures = 0,
                updated_at = $now;
            """;
        string now = DateTime.UtcNow.ToString("o");
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$description", description);
        cmd.Parameters.AddWithValue("$schema", inputSchemaJson);
        cmd.Parameters.AddWithValue("$projectDir", projectDir);
        cmd.Parameters.AddWithValue("$dllPath", dllPath);
        cmd.Parameters.AddWithValue("$commit", gitCommit);
        cmd.Parameters.AddWithValue("$usesPaidApi", usesPaidApi ? 1 : 0);
        cmd.Parameters.AddWithValue("$hasExternalEffects", hasExternalEffects ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
    }

    public static ToolRecord? Find(string dbPath, string name)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, description, input_schema_json, project_dir, dll_path, git_commit, approved, uses_paid_api, has_external_effects, consecutive_failures FROM tools WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public static List<ToolRecord> List(string dbPath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name, description, input_schema_json, project_dir, dll_path, git_commit, approved, uses_paid_api, has_external_effects, consecutive_failures FROM tools ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var results = new List<ToolRecord>();
        while (reader.Read())
        {
            results.Add(ReadRecord(reader));
        }

        return results;
    }

    public static void MarkApproved(string dbPath, string name)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tools SET approved = 1, updated_at = $now WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // Returns the new count, so the caller (NovaAssistant) can decide
    // whether this was the 3rd-in-a-row without a second read. Two
    // separate statements/commands rather than one batched string -
    // Microsoft.Data.Sqlite's ExecuteScalar behavior across a
    // semicolon-batched UPDATE+SELECT isn't something worth relying on
    // when a plain, unambiguous two-step is just as cheap.
    public static int RecordFailure(string dbPath, string name)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.CommandText = "UPDATE tools SET consecutive_failures = consecutive_failures + 1, updated_at = $now WHERE name = $name";
            updateCmd.Parameters.AddWithValue("$name", name);
            updateCmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
            updateCmd.ExecuteNonQuery();
        }

        using var selectCmd = connection.CreateCommand();
        selectCmd.CommandText = "SELECT consecutive_failures FROM tools WHERE name = $name";
        selectCmd.Parameters.AddWithValue("$name", name);
        object? result = selectCmd.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    public static void RecordSuccess(string dbPath, string name)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tools SET consecutive_failures = 0, updated_at = $now WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    // Called after ToolGitVersioning.RevertOneVersion successfully rebuilds
    // an older version of a failing tool - points the registry at the
    // reverted commit/dll and resets the failure streak, same as a normal
    // Upsert would, without touching approved (a revert isn't "advancing
    // to something new" - see the roadmap's Gate 2 asymmetry note).
    public static void UpdateAfterRevert(string dbPath, string name, string dllPath, string gitCommit)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE tools SET dll_path = $dllPath, git_commit = $commit, consecutive_failures = 0, updated_at = $now WHERE name = $name";
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$dllPath", dllPath);
        cmd.Parameters.AddWithValue("$commit", gitCommit);
        cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    private static ToolRecord ReadRecord(SqliteDataReader reader) => new(
        Name: reader.GetString(0),
        Description: reader.GetString(1),
        InputSchemaJson: reader.GetString(2),
        ProjectDir: reader.GetString(3),
        DllPath: reader.GetString(4),
        GitCommit: reader.GetString(5),
        Approved: reader.GetInt32(6) != 0,
        UsesPaidApi: reader.GetInt32(7) != 0,
        HasExternalEffects: reader.GetInt32(8) != 0,
        ConsecutiveFailures: reader.GetInt32(9));
}
