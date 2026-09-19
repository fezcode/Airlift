using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Airlift.Core;

public sealed class StateStore
{
    private readonly string _connectionString;
    public string Root { get; }
    public StateStore(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Fezcode", "Airlift"));
        Directory.CreateDirectory(Root);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "airlift.db"), DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS documents (kind TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(kind,id));";
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }
    public void Put<T>(string kind, string id, T value)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO documents(kind,id,body) VALUES($kind,$id,$body) ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$body", JsonSerializer.Serialize(value, JsonData.Options)); cmd.ExecuteNonQuery();
    }
    public T? Get<T>(string kind, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT body FROM documents WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string json ? JsonData.Read<T>(json) : default;
    }
    public IReadOnlyList<T> All<T>(string kind)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT body FROM documents WHERE kind=$kind ORDER BY id"; cmd.Parameters.AddWithValue("$kind", kind);
        using var reader = cmd.ExecuteReader(); var result = new List<T>(); while (reader.Read()) result.Add(JsonData.Read<T>(reader.GetString(0))); return result;
    }
    public void Remove(string kind, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = "DELETE FROM documents WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id); cmd.ExecuteNonQuery();
    }
    public FileStream AcquireOperationLock() => new(Path.Combine(Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    public void RecoverInterruptedOperations()
    {
        // The process lock proves no other Airlift process is currently mutating package state.
        using var lease = AcquireOperationLock();
        foreach (var op in All<Operation>("operations").Where(o => o.Active))
            Put("operations", op.Id, op with { Status = "Interrupted", Message = "Airlift stopped before completion. Refresh inventory and review before retrying." });
    }
}
