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
    Entry? FindRemoteEntry(Guid account, ulong disk, ulong remote, bool isDirectory);
    IReadOnlyList<Entry> Entries(Guid account);
    IReadOnlyList<Entry> EntriesInFolder(Guid account, Guid parent);
    IReadOnlyList<Entry> EntriesForBrowser(Guid account, Guid? parent);
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
    // Immutable records stay indexed in memory. State projection and repeated task lookups
    // therefore avoid reopening SQLite cursors and deserializing unchanged rows.
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, Dictionary<Guid, Entry>> _accountEntries = [];
    private readonly Dictionary<Guid, HashSet<Guid>> _accountDirectories = [];
    private readonly Dictionary<(Guid Account, Guid? Parent), HashSet<Guid>> _children = [];
    private readonly Dictionary<(Guid Account, ulong Disk, ulong Remote, bool IsDirectory), HashSet<Guid>> _remoteEntries = [];
    private readonly Dictionary<Guid, Account> _accounts = [];
    private readonly Dictionary<Guid, Operation> _operations = [];
    private readonly Dictionary<Guid, List<Guid>> _accountOperations = [];
    private readonly Dictionary<Guid, long> _operationOrder = [];
    private readonly Dictionary<Guid, IReadOnlyList<DiskTreeNode>> _trees = [];
    private readonly Dictionary<Guid, string> _operationJson = [];
    private SyncSettings _settings = new();
    private Guid? _activeAccountId;
    private bool _hasActiveAccount;
    private long _nextOperationOrder;
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public OperationStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using (var cmd = _db.CreateCommand())
        {
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
        }

        foreach (var account in Read<Account>("SELECT json FROM accounts ORDER BY id")) _accounts[account.Id] = account;
        foreach (var entry in Read<Entry>("SELECT json FROM entries")) Cache(entry);
        LoadPreferences();
        LoadTrees();
        LoadOperations();

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
            while (reader.Read()) result.Add(Deserialize<T>(reader.GetString(0)));
            return result;
        }
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json)
        ?? throw new IOException("本地记录损坏，停止自动写入。");

    private void LoadPreferences()
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT key,json FROM preferences";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var key = reader.GetString(0); var value = reader.GetString(1);
            if (key == "sync-settings") _settings = Deserialize<SyncSettings>(value);
            else if (key == "active-account") { _activeAccountId = Deserialize<Guid>(value); _hasActiveAccount = true; }
        }
    }

    private void LoadTrees()
    {
        using var cmd = _db.CreateCommand(); cmd.CommandText = "SELECT account,json FROM trees";
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) _trees[Guid.Parse(reader.GetString(0))] = Deserialize<List<DiskTreeNode>>(reader.GetString(1));
    }

    private void LoadOperations()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT id,account,json FROM operations ORDER BY created,rowid";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0)); var account = Guid.Parse(reader.GetString(1));
            var json = reader.GetString(2); var operation = Deserialize<Operation>(json);
            _operations[id] = operation; _operationJson[id] = json; _operationOrder[id] = ++_nextOperationOrder;
            if (!_accountOperations.TryGetValue(account, out var bucket)) _accountOperations[account] = bucket = [];
            bucket.Add(id);
        }
    }

    public IReadOnlyList<Account> Accounts()
    { lock (_gate) return _accounts.Values.OrderBy(a => a.Id).ToArray(); }

    public SyncSettings Settings
    {
        get { lock (_gate) return _settings; }
        set
        {
            lock (_gate)
            {
                if (_settings == value) return;
                var json = JsonSerializer.Serialize(value, Json);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO preferences(key,json) VALUES('sync-settings',$j) ON CONFLICT(key) DO UPDATE SET json=excluded.json WHERE preferences.json IS NOT excluded.json";
                cmd.Parameters.AddWithValue("$j", json); cmd.ExecuteNonQuery();
                _settings = value;
            }
        }
    }

    public Guid? ActiveAccountId
    {
        get { lock (_gate) return _activeAccountId; }
        set
        {
            lock (_gate)
            {
                if (value is null)
                {
                    if (!_hasActiveAccount) return;
                    Write("DELETE FROM preferences WHERE key='active-account'");
                    _activeAccountId = null; _hasActiveAccount = false; return;
                }
                if (_hasActiveAccount && _activeAccountId == value) return;
                var json = JsonSerializer.Serialize(value.Value, Json);
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "INSERT INTO preferences(key,json) VALUES('active-account',$j) ON CONFLICT(key) DO UPDATE SET json=excluded.json WHERE preferences.json IS NOT excluded.json";
                cmd.Parameters.AddWithValue("$j", json); cmd.ExecuteNonQuery();
                _activeAccountId = value; _hasActiveAccount = true;
            }
        }
    }

    public IReadOnlyList<Entry> Entries(Guid account)
    { lock (_gate) return _accountEntries.TryGetValue(account, out var entries) ? entries.Values.ToArray() : []; }

    public Entry? FindRemoteEntry(Guid account, ulong disk, ulong remote, bool isDirectory)
    {
        if (remote == 0) return null;
        lock (_gate)
            return _remoteEntries.TryGetValue((account, disk, remote, isDirectory), out var ids)
                ? ids.Select(id => _entries[id]).FirstOrDefault() : null;
    }

    public IReadOnlyList<Entry> EntriesInFolder(Guid account, Guid parent)
    {
        lock (_gate)
            return _children.TryGetValue((account, parent), out var children)
                ? children.Select(id => _entries[id]).ToArray() : [];
    }

    // The native browser needs all folders for its outline but files from only the open
    // folder. This index avoids scanning every cached file for each state snapshot.
    public IReadOnlyList<Entry> EntriesForBrowser(Guid account, Guid? parent)
    {
        lock (_gate)
        {
            var ids = new HashSet<Guid>();
            if (_accountDirectories.TryGetValue(account, out var directories)) ids.UnionWith(directories);
            if (_children.TryGetValue((account, parent), out var children)) ids.UnionWith(children);
            return ids.Select(id => _entries[id]).ToArray();
        }
    }

    public Entry? GetEntry(Guid id) { lock (_gate) return _entries.GetValueOrDefault(id); }

    private void RemoveFromIndexes(Entry entry)
    {
        if (_accountEntries.TryGetValue(entry.AccountId, out var accountEntries)) accountEntries.Remove(entry.Id);
        if (entry.IsDirectory && _accountDirectories.TryGetValue(entry.AccountId, out var directories)) directories.Remove(entry.Id);
        if (_children.TryGetValue((entry.AccountId, entry.ParentId), out var children)) children.Remove(entry.Id);
        if (entry.RemoteId != 0 && _remoteEntries.TryGetValue((entry.AccountId, entry.DiskId, entry.RemoteId, entry.IsDirectory), out var remoteEntries))
            remoteEntries.Remove(entry.Id);
    }

    private void Cache(Entry entry)
    {
        if (_entries.TryGetValue(entry.Id, out var previous)) RemoveFromIndexes(previous);
        _entries[entry.Id] = entry;
        if (!_accountEntries.TryGetValue(entry.AccountId, out var bucket)) _accountEntries[entry.AccountId] = bucket = [];
        bucket[entry.Id] = entry;
        if (entry.IsDirectory)
        {
            if (!_accountDirectories.TryGetValue(entry.AccountId, out var directories)) _accountDirectories[entry.AccountId] = directories = [];
            directories.Add(entry.Id);
        }
        if (!_children.TryGetValue((entry.AccountId, entry.ParentId), out var children)) _children[(entry.AccountId, entry.ParentId)] = children = [];
        children.Add(entry.Id);
        if (entry.RemoteId != 0)
        {
            var key = (entry.AccountId, entry.DiskId, entry.RemoteId, entry.IsDirectory);
            if (!_remoteEntries.TryGetValue(key, out var remoteEntries)) _remoteEntries[key] = remoteEntries = [];
            remoteEntries.Add(entry.Id);
        }
    }

    public Operation? GetOperation(Guid id)
    { lock (_gate) return _operations.GetValueOrDefault(id); }

    public IReadOnlyList<Operation> Operations(Guid? account = null)
    {
        lock (_gate)
        {
            IEnumerable<Guid> ids = account is { } a
                ? _accountOperations.GetValueOrDefault(a) ?? []
                : _operations.Keys;
            return ids.Select(id => _operations[id]).OrderBy(o => o.Created).ThenBy(o => _operationOrder[o.Id]).ToArray();
        }
    }

    private void Cache(Operation operation, string? json = null)
    {
        if (_operations.TryGetValue(operation.Id, out var previous) && previous.AccountId != operation.AccountId
            && _accountOperations.TryGetValue(previous.AccountId, out var old)) old.Remove(operation.Id);
        var isNew = !_operations.ContainsKey(operation.Id);
        _operations[operation.Id] = operation;
        if (!_accountOperations.TryGetValue(operation.AccountId, out var bucket)) _accountOperations[operation.AccountId] = bucket = [];
        if (!bucket.Contains(operation.Id)) bucket.Add(operation.Id);
        if (isNew) _operationOrder[operation.Id] = ++_nextOperationOrder;
        _operationJson[operation.Id] = json ?? JsonSerializer.Serialize(operation, Json);
    }

    public IReadOnlyList<DiskTreeNode> Tree(Guid account)
    { lock (_gate) return _trees.TryGetValue(account, out var tree) ? tree.ToArray() : []; }

    public void SaveTree(Guid account, IReadOnlyList<DiskTreeNode> nodes)
    {
        lock (_gate)
        {
            if (_trees.TryGetValue(account, out var old) && TreesEqual(old, nodes)) return;
            var json = JsonSerializer.Serialize(nodes, Json);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO trees(account,json) VALUES($id,$j) ON CONFLICT(account) DO UPDATE SET json=excluded.json WHERE trees.json IS NOT excluded.json";
            cmd.Parameters.AddWithValue("$id", account.ToString()); cmd.Parameters.AddWithValue("$j", json); cmd.ExecuteNonQuery();
            _trees[account] = nodes.ToArray();
        }
    }

    private static bool TreesEqual(IReadOnlyList<DiskTreeNode> left, IReadOnlyList<DiskTreeNode> right)
    {
        if (left.Count != right.Count) return false;
        for (var i = 0; i < left.Count; i++)
            if (left[i].Id != right[i].Id || left[i].NodeType != right[i].NodeType
                || left[i].Name != right[i].Name || left[i].ParentId != right[i].ParentId) return false;
        return true;
    }

    public void SaveAccount(Account account)
    {
        lock (_gate)
        {
            if (_accounts.TryGetValue(account.Id, out var existing) && existing == account) return;
            var json = JsonSerializer.Serialize(account, Json);
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO accounts(id,json) VALUES($id,$j) ON CONFLICT(id) DO UPDATE SET json=excluded.json WHERE accounts.json IS NOT excluded.json";
            cmd.Parameters.AddWithValue("$id", account.Id.ToString()); cmd.Parameters.AddWithValue("$j", json); cmd.ExecuteNonQuery();
            _accounts[account.Id] = account;
        }
    }

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
            if (_accountEntries.Remove(account, out var removed))
                foreach (var entry in removed.Values) { _entries.Remove(entry.Id); RemoveFromIndexes(entry); }
            if (_accountOperations.Remove(account, out var operationIds))
                foreach (var id in operationIds) { _operations.Remove(id); _operationOrder.Remove(id); _operationJson.Remove(id); }
            _accounts.Remove(account); _trees.Remove(account);
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
            var entryBatch = entries?.GroupBy(e => e.Id).Select(g => g.Last()).ToArray() ?? [];
            var operationBatch = operations?.GroupBy(o => o.Id).Select(g => g.Last()).ToArray() ?? [];
            if (!HasEntryChanges(entryBatch) && !HasOperationChanges(operationBatch)) return;
            using var tx = _db.BeginTransaction();
            var (savedEntries, savedOperations) = SaveCore(tx, entryBatch, operationBatch);
            tx.Commit();
            foreach (var entry in savedEntries) Cache(entry);
            foreach (var operation in savedOperations) Cache(operation.Operation, operation.Json);
        }
    }

    private bool HasEntryChanges(IEnumerable<Entry> entries) => entries.Any(entry => !_entries.TryGetValue(entry.Id, out var old) || old != entry);

    private bool HasOperationChanges(IEnumerable<Operation> operations)
    {
        foreach (var operation in operations)
        {
            if (!_operations.TryGetValue(operation.Id, out var old)) return true;
            if (old == operation) continue;
            var json = JsonSerializer.Serialize(operation, Json);
            if (!_operationJson.TryGetValue(operation.Id, out var oldJson) || oldJson != json
                || old.AccountId != operation.AccountId || old.Created != operation.Created) return true;
        }
        return false;
    }

    private (List<Entry> Entries, List<(Operation Operation, string Json)> Operations) SaveCore(
        SqliteTransaction tx, IEnumerable<Entry>? entries, IEnumerable<Operation>? operations)
    {
        var savedEntries = new List<Entry>(); var savedOperations = new List<(Operation, string)>();
        using var entryCmd = _db.CreateCommand(); entryCmd.Transaction = tx;
        entryCmd.CommandText = "INSERT INTO entries(id,account,json) VALUES($id,$a,$j) ON CONFLICT(id) DO UPDATE SET account=excluded.account,json=excluded.json WHERE entries.account IS NOT excluded.account OR entries.json IS NOT excluded.json";
        var entryId = entryCmd.Parameters.AddWithValue("$id", ""); var entryAccount = entryCmd.Parameters.AddWithValue("$a", ""); var entryJson = entryCmd.Parameters.AddWithValue("$j", "");
        entryCmd.Prepare();
        foreach (var entry in entries ?? [])
        {
            if (_entries.TryGetValue(entry.Id, out var old) && old == entry) continue;
            var json = JsonSerializer.Serialize(entry, Json);
            entryId.Value = entry.Id.ToString(); entryAccount.Value = entry.AccountId.ToString(); entryJson.Value = json;
            entryCmd.ExecuteNonQuery(); savedEntries.Add(entry);
        }

        using var operationCmd = _db.CreateCommand(); operationCmd.Transaction = tx;
        operationCmd.CommandText = "INSERT INTO operations(id,account,created,json) VALUES($id,$a,$c,$j) ON CONFLICT(id) DO UPDATE SET account=excluded.account,created=excluded.created,json=excluded.json WHERE operations.account IS NOT excluded.account OR operations.created IS NOT excluded.created OR operations.json IS NOT excluded.json";
        var operationId = operationCmd.Parameters.AddWithValue("$id", ""); var operationAccount = operationCmd.Parameters.AddWithValue("$a", "");
        var operationCreated = operationCmd.Parameters.AddWithValue("$c", ""); var operationJson = operationCmd.Parameters.AddWithValue("$j", "");
        operationCmd.Prepare();
        foreach (var operation in operations ?? [])
        {
            if (_operations.TryGetValue(operation.Id, out var old) && old == operation) continue;
            var json = JsonSerializer.Serialize(operation, Json);
            if (_operationJson.TryGetValue(operation.Id, out var oldJson) && oldJson == json
                && _operations.TryGetValue(operation.Id, out var cached)
                && cached.AccountId == operation.AccountId && cached.Created == operation.Created) continue;
            operationId.Value = operation.Id.ToString(); operationAccount.Value = operation.AccountId.ToString();
            operationCreated.Value = operation.Created.ToString("O"); operationJson.Value = json;
            operationCmd.ExecuteNonQuery(); savedOperations.Add((operation, json));
        }
        return (savedEntries, savedOperations);
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
            var batch = changed.Values.ToArray();
            if (!HasEntryChanges(batch)) return;
            using var tx = _db.BeginTransaction();
            var (savedEntries, _) = SaveCore(tx, batch, null);
            tx.Commit();
            foreach (var entry in savedEntries) Cache(entry);
        }
    }

    public void RemoveOperations(IEnumerable<Guid> ids, IEnumerable<Operation> rewritten)
    {
        lock (_gate)
        {
            var removals = ids.Distinct().Where(_operations.ContainsKey).ToArray();
            var rewrites = rewritten.GroupBy(o => o.Id).Select(g => g.Last()).ToArray();
            if (removals.Length == 0 && !HasOperationChanges(rewrites)) return;
            using var tx = _db.BeginTransaction();
            foreach (var id in removals)
            {
                using var cmd = _db.CreateCommand(); cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM operations WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id.ToString()); cmd.ExecuteNonQuery();
            }
            var (_, savedOperations) = SaveCore(tx, null, rewrites);
            tx.Commit();
            foreach (var id in removals)
            {
                if (_operations.Remove(id, out var operation) && _accountOperations.TryGetValue(operation.AccountId, out var bucket)) bucket.Remove(id);
                _operationOrder.Remove(id); _operationJson.Remove(id);
            }
            foreach (var operation in savedOperations) Cache(operation.Operation, operation.Json);
        }
    }

    public void UpdateEntry(Guid id, Func<Entry, Entry> update)
    { lock (_gate) { var item = GetEntry(id) ?? throw new IOException("本地条目已不存在。"); Save(entries: [update(item)]); } }

    public void UpdateOperation(Guid id, Func<Operation, Operation> update)
    { lock (_gate) { var item = GetOperation(id) ?? throw new IOException("操作记录已不存在。"); Save(operations: [update(item)]); } }

    public void Dispose() { lock (_gate) _db.Dispose(); }
}
