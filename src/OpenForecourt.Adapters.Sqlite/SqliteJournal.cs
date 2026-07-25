using System.Globalization;
using Microsoft.Data.Sqlite;
using OpenForecourt.Abstractions.Domain;
using OpenForecourt.Abstractions.Ports;

namespace OpenForecourt.Adapters.Sqlite;

/// <summary>
/// The crash-safe transaction journal: SQLite in WAL mode (CLAUDE.md section 5).
/// </summary>
/// <remarks>
/// <para>
/// Writes are idempotent upserts keyed by transaction id, so replaying the same lifecycle
/// step twice is harmless and the table always holds the <b>latest</b> state per
/// transaction. WAL mode with <c>synchronous=NORMAL</c> makes every committed
/// <see cref="AppendAsync"/> durable across an application crash — which is exactly the
/// kill-point recovery the phase tests exercise.
/// </para>
/// <para>
/// A single writer connection is held for the journal's lifetime, guarded by a semaphore:
/// SQLite permits only one writer, and serialising here avoids <c>SQLITE_BUSY</c> without a
/// retry loop. Throughput is not a concern for a forecourt's transaction rate.
/// </para>
/// <para>
/// ponytail: <c>synchronous=NORMAL</c> survives process crash but can lose the last commit
/// on a hard power cut without a checkpoint; upgrade to <c>FULL</c> if true power-loss
/// durability is ever required (it is slower).
/// </para>
/// </remarks>
public sealed class SqliteJournal : ITransactionJournal, IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private SqliteJournal(SqliteConnection connection) => _connection = connection;

    /// <summary>
    /// Opens (creating if needed) the journal at <paramref name="dataSource"/> and applies the
    /// schema and WAL pragmas. Use <c>":memory:"</c> only in tests — an in-memory database is
    /// not crash-safe and vanishes when the connection closes.
    /// </summary>
    public static async Task<SqliteJournal> OpenAsync(string dataSource, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;",
            cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, Schema, cancellationToken).ConfigureAwait(false);
        return new SqliteJournal(connection);
    }

    /// <inheritdoc />
    public async Task AppendAsync(TransactionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO transactions
                    (tx_id, pump_id, pump_state, status, amount_minor, currency,
                     token, stan, auth_code, offline, created_at, updated_at, correlation_id)
                VALUES
                    ($id, $pump, $pstate, $status, $amount, $currency,
                     $token, $stan, $auth, $offline, $created, $updated, $corr)
                ON CONFLICT(tx_id) DO UPDATE SET
                    pump_state = excluded.pump_state,
                    status = excluded.status,
                    amount_minor = excluded.amount_minor,
                    currency = excluded.currency,
                    token = excluded.token,
                    stan = excluded.stan,
                    auth_code = excluded.auth_code,
                    offline = excluded.offline,
                    updated_at = excluded.updated_at;
                """;
            Bind(command, context);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TransactionContext>> ReadIncompleteAsync(CancellationToken cancellationToken) =>
        QueryAsync(
            "WHERE status IN ($intent, $sent, $approved) ORDER BY created_at",
            command =>
            {
                command.Parameters.AddWithValue("$intent", (int)TransactionStatus.Intent);
                command.Parameters.AddWithValue("$sent", (int)TransactionStatus.HostRequestSent);
                command.Parameters.AddWithValue("$approved", (int)TransactionStatus.Approved);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<TransactionContext>> ReadAllAsync(CancellationToken cancellationToken) =>
        QueryAsync("ORDER BY created_at DESC", static _ => { }, cancellationToken);

    /// <inheritdoc />
    public async Task<TransactionContext?> TryGetAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "WHERE tx_id = $id",
            command => command.Parameters.AddWithValue("$id", transactionId.ToString()),
            cancellationToken).ConfigureAwait(false);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private async Task<IReadOnlyList<TransactionContext>> QueryAsync(
        string whereOrderBy, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText =
            "SELECT tx_id, pump_id, pump_state, status, amount_minor, currency, " +
            "token, stan, auth_code, offline, created_at, updated_at, correlation_id " +
            "FROM transactions " + whereOrderBy + ";";
        bind(command);

        var results = new List<TransactionContext>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(Map(reader));
        }

        return results;
    }

    private static void Bind(SqliteCommand command, TransactionContext c)
    {
        command.Parameters.AddWithValue("$id", c.TransactionId.ToString());
        command.Parameters.AddWithValue("$pump", c.PumpId);
        command.Parameters.AddWithValue("$pstate", (int)c.State);
        command.Parameters.AddWithValue("$status", (int)c.Status);
        command.Parameters.AddWithValue("$amount", c.Amount.Minor);
        command.Parameters.AddWithValue("$currency", c.Amount.CurrencyCode);
        command.Parameters.AddWithValue("$token", (object?)c.Token ?? DBNull.Value);
        command.Parameters.AddWithValue("$stan", (object?)c.Stan ?? DBNull.Value);
        command.Parameters.AddWithValue("$auth", (object?)c.AuthorisationCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$offline", c.Offline ? 1 : 0);
        command.Parameters.AddWithValue("$created", c.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", c.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$corr", c.CorrelationId.ToString());
    }

    private static TransactionContext Map(SqliteDataReader r) => new(
        TransactionId: Guid.Parse(r.GetString(0)),
        PumpId: r.GetInt32(1),
        State: (PumpState)r.GetInt32(2),
        Status: (TransactionStatus)r.GetInt32(3),
        Amount: new Money(r.GetInt64(4), r.GetString(5)),
        Token: r.IsDBNull(6) ? null : r.GetString(6),
        Stan: r.IsDBNull(7) ? null : r.GetString(7),
        AuthorisationCode: r.IsDBNull(8) ? null : r.GetString(8),
        Offline: r.GetInt32(9) != 0,
        CreatedAt: DateTimeOffset.Parse(r.GetString(10), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        UpdatedAt: DateTimeOffset.Parse(r.GetString(11), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        CorrelationId: Guid.Parse(r.GetString(12)));

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private const string Schema =
        """
        CREATE TABLE IF NOT EXISTS transactions (
            tx_id          TEXT    PRIMARY KEY,
            pump_id        INTEGER NOT NULL,
            pump_state     INTEGER NOT NULL,
            status         INTEGER NOT NULL,
            amount_minor   INTEGER NOT NULL,
            currency       TEXT    NOT NULL,
            token          TEXT,
            stan           TEXT,
            auth_code      TEXT,
            offline        INTEGER NOT NULL,
            created_at     TEXT    NOT NULL,
            updated_at     TEXT    NOT NULL,
            correlation_id TEXT    NOT NULL
        );
        """;
}
