using Microsoft.Data.Sqlite;

namespace DeskPair.Rendezvous.Persistence;

/// <summary>Single-table SQLite store. All access goes through one connection guarded by a semaphore.</summary>
public sealed class SqlitePeerStore : IPeerStore, IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;

    public SqlitePeerStore(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        _connection = new SqliteConnection(_connectionString);
        await _connection.OpenAsync(ct).ConfigureAwait(false);
        await using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS peers (
                id TEXT PRIMARY KEY,
                uuid BLOB NOT NULL UNIQUE,
                pk BLOB NOT NULL,
                created_utc INTEGER NOT NULL,
                last_seen_utc INTEGER NOT NULL,
                version TEXT NOT NULL DEFAULT ''
            );
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredPeer>> LoadAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteCommand cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT id, uuid, pk, created_utc, last_seen_utc, version FROM peers";
            var list = new List<StoredPeer>();
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(Read(reader));
            }

            return list;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredPeer?> FindByUuidAsync(byte[] uuid, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteCommand cmd = Connection.CreateCommand();
            cmd.CommandText = "SELECT id, uuid, pk, created_utc, last_seen_utc, version FROM peers WHERE uuid = $uuid";
            cmd.Parameters.AddWithValue("$uuid", uuid);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(IReadOnlyList<StoredPeer> peers, CancellationToken ct)
    {
        if (peers.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using SqliteTransaction tx = (SqliteTransaction)await Connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using SqliteCommand cmd = Connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO peers (id, uuid, pk, created_utc, last_seen_utc, version)
                VALUES ($id, $uuid, $pk, $created, $seen, $version)
                ON CONFLICT(id) DO UPDATE SET uuid = excluded.uuid, pk = excluded.pk,
                    last_seen_utc = excluded.last_seen_utc, version = excluded.version
                """;
            SqliteParameter pId = cmd.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter pUuid = cmd.Parameters.Add("$uuid", SqliteType.Blob);
            SqliteParameter pPk = cmd.Parameters.Add("$pk", SqliteType.Blob);
            SqliteParameter pCreated = cmd.Parameters.Add("$created", SqliteType.Integer);
            SqliteParameter pSeen = cmd.Parameters.Add("$seen", SqliteType.Integer);
            SqliteParameter pVersion = cmd.Parameters.Add("$version", SqliteType.Text);
            foreach (StoredPeer p in peers)
            {
                pId.Value = p.Id;
                pUuid.Value = p.Uuid;
                pPk.Value = p.IdentityPk;
                pCreated.Value = p.CreatedUtc.ToUnixTimeSeconds();
                pSeen.Value = p.LastSeenUtc.ToUnixTimeSeconds();
                pVersion.Value = p.Version;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Connection => _connection ?? throw new InvalidOperationException("Store not initialized.");

    private static StoredPeer Read(SqliteDataReader r) => new(
        r.GetString(0),
        (byte[])r[1],
        (byte[])r[2],
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)),
        DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)),
        r.GetString(5));

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
