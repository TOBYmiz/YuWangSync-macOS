using Microsoft.Data.Sqlite;
using System.Text.Json;
using YuWangCloudSync.Models;

namespace YuWangSync.Core.Storage;

public interface IOperationStore : IDisposable
{
    IReadOnlyList<Account> Accounts();
    Guid? ActiveAccountId { get; set; }
    SyncSettings Settings { get; set; }
    void SaveAccount(Account account);
    void RemoveAccount(Guid account);
    Entry? GetEntry(Guid id);
    IReadOnlyList<Entry> Entries(Guid account);
    IReadOnlyList<Operation> Operations(Guid? account = null);
    Operation? GetOperation(Guid id);
    void Save(IEnumerable<Entry>? entries = null, IEnumerable<Operation>? operations = null);
    void UpdateEntry(Guid id, Func<Entry, Entry> update);
    void UpdateOperation(Guid id, Func<Operation, Operation> update);
    // Applies many read-modify-write entry updates in one transaction (one fsync).
    void UpdateEntries(IEnumerable<Entry> inserts, IEnumerable<(Guid Id, Func<Entry, Entry> Update)> updates);
    void RemoveOperations(IEnumerable<Guid> ids, IEnumerable<Operation> rewritten);
    void SaveTree(Guid account, IReadOnlyList<DiskTreeNode> nodes);
    IReadOnlyList<DiskTreeNode> Tree(Guid account);
}

public sealed class OperationStore : IOperationStore
{
    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public OperationStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS accounts(id TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS preferences(key TEXT PRIMARY KEY, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS entries(id TEXT PRIMARY KEY, account TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS entries_account ON entries(account);
            CREATE TABLE IF NOT EXISTS operations(id TEXT PRIMARY KEY, account TEXT NOT NULL, created TEXT NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS operations_account ON operations(account,created);
            CREATE TABLE IF NOT EXISTS trees(account TEXT PRIMARY KEY, json TEXT NOT NULL);
            PRAGMA user_version=1;
            """;
        cmd.ExecuteNonQuery();
        // A killed process may have sent a request without persisting its response.
        Save(operations: Operations().Where(o => o.State == OperationState.Running).Select(o => o with
        { State = o.VerificationStamp is null ? OperationState.Uncertain : OperationState.AwaitingVerification, Error = "上次运行中断，先核对云端结果。" }));
    }
    private List<T> Read<T>(string sql, params (string, object?)[] parameters)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = sql;
            foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader(); var result = new List<T>();
            while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json)
                ?? throw new IOException("本地记录损坏，停止自动写入。"));
            return result;
        }
    }
    public IReadOnlyList<Account> Accounts() => Read<Account>("SELECT json FROM accounts ORDER BY id");
    public SyncSettings Settings
    {
        get => Read<SyncSettings>("SELECT json FROM preferences WHERE key='sync-settings'").FirstOrDefault() ?? new();
        set => Write("INSERT OR REPLACE INTO preferences VALUES('sync-settings',$j)", ("$j", JsonSerializer.Serialize(value, Json)));
    }
    public Guid? ActiveAccountId
    {
        get => Read<Guid?>("SELECT json FROM preferences WHERE key='active-account'").FirstOrDefault();
        set
        {
            if (value is null) Write("DELETE FROM preferences WHERE key='active-account'");
            else Write("INSERT OR REPLACE INTO preferences VALUES('active-account',$j)", ("$j", JsonSerializer.Serialize(value, Json)));
        }
    }
    public IReadOnlyList<Entry> Entries(Guid account) => Read<Entry>("SELECT json FROM entries WHERE account=$a", ("$a", account.ToString()));
    public Entry? GetEntry(Guid id) => Read<Entry>("SELECT json FROM entries WHERE id=$id", ("$id", id.ToString())).SingleOrDefault();
    public Operation? GetOperation(Guid id) => Read<Operation>("SELECT json FROM operations WHERE id=$id", ("$id", id.ToString())).SingleOrDefault();
    public IReadOnlyList<Operation> Operations(Guid? account = null) => account is { } a
        ? Read<Operation>("SELECT json FROM operations WHERE account=$a ORDER BY created,rowid", ("$a", a.ToString()))
        : Read<Operation>("SELECT json FROM operations ORDER BY created,rowid");
    public IReadOnlyList<DiskTreeNode> Tree(Guid account) => Read<List<DiskTreeNode>>("SELECT json FROM trees WHERE account=$a", ("$a", account.ToString())).SingleOrDefault() ?? [];
    public void SaveTree(Guid account, IReadOnlyList<DiskTreeNode> nodes) => Write("INSERT OR REPLACE INTO trees VALUES($id,$j)", ("$id", account.ToString()), ("$j", JsonSerializer.Serialize(nodes, Json)));
    public void SaveAccount(Account account) => Write("INSERT OR REPLACE INTO accounts VALUES($id,$j)", ("$id", account.Id.ToString()), ("$j", JsonSerializer.Serialize(account, Json)));
    public void RemoveAccount(Guid account)
    {
        lock (_gate)
        {
            if (Operations(account).Any(o => !o.IsTerminal) || Entries(account).Any(e => e.Dirty))
                throw new InvalidOperationException("该账号仍有待办或本地修改，请先处理、导出或明确放弃。");
            using var tx = _db.BeginTransaction();
            foreach (var table in new[] { "operations", "entries", "trees", "accounts" })
            {
                using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM {table} WHERE {(table == "accounts" ? "id" : "account")}=$a";
                cmd.Parameters.AddWithValue("$a", account.ToString()); cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }
    private void Write(string sql, params (string, object?)[] values)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand(); cmd.CommandText = sql;
            foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }
    public void Save(IEnumerable<Entry>? entries = null, IEnumerable<Operation>? operations = null)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            SaveCore(tx, entries, operations);
            tx.Commit();
        }
    }
    private void SaveCore(SqliteTransaction tx, IEnumerable<Entry>? entries, IEnumerable<Operation>? operations)
    {
        foreach (var entry in entries ?? [])
        {
            using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO entries VALUES($id,$a,$j) ON CONFLICT(id) DO UPDATE SET account=$a,json=$j";
            cmd.Parameters.AddWithValue("$id", entry.Id.ToString()); cmd.Parameters.AddWithValue("$a", entry.AccountId.ToString());
            cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(entry, Json)); cmd.ExecuteNonQuery();
        }
        foreach (var op in operations ?? [])
        {
            using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO operations VALUES($id,$a,$c,$j) ON CONFLICT(id) DO UPDATE SET json=$j";
            cmd.Parameters.AddWithValue("$id", op.Id.ToString()); cmd.Parameters.AddWithValue("$a", op.AccountId.ToString());
            cmd.Parameters.AddWithValue("$c", op.Created.ToString("O")); cmd.Parameters.AddWithValue("$j", JsonSerializer.Serialize(op, Json)); cmd.ExecuteNonQuery();
        }
    }
    public void UpdateEntries(IEnumerable<Entry> inserts, IEnumerable<(Guid Id, Func<Entry, Entry> Update)> updates)
    {
        lock (_gate)
        {
            var changed = new Dictionary<Guid, Entry>();
            foreach (var entry in inserts) changed[entry.Id] = entry;
            foreach (var (id, update) in updates)
            {
                var current = changed.GetValueOrDefault(id) ?? GetEntry(id);
                if (current is not null) changed[id] = update(current);
            }
            using var tx = _db.BeginTransaction();
            SaveCore(tx, changed.Values, null);
            tx.Commit();
        }
    }
    public void RemoveOperations(IEnumerable<Guid> ids, IEnumerable<Operation> rewritten)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
            {
                using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM operations WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
            }
            SaveCore(tx, null, rewritten);
            tx.Commit();
        }
    }
    public void UpdateEntry(Guid id, Func<Entry, Entry> update)
    { lock (_gate) { var item = GetEntry(id) ?? throw new IOException("本地条目已不存在。"); Save(entries: [update(item)]); } }
    public void UpdateOperation(Guid id, Func<Operation, Operation> update)
    { lock (_gate) { var item = GetOperation(id) ?? throw new IOException("操作记录已不存在。"); Save(operations: [update(item)]); } }
    public void Dispose() { lock (_gate) _db.Dispose(); }
}
