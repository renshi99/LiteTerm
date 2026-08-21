using System.Globalization;
using System.Text;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Security;
using LiteTerm.Core.Servers;
using Microsoft.Data.Sqlite;

namespace LiteTerm.Infrastructure.Data;

/// <summary>
/// 使用版本化 SQLite 架构保存服务器公开资料和经 DPAPI 保护的凭据。
/// </summary>
public sealed class SqliteServerProfileRepository : IServerProfileRepository
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly ISecretProtector _secretProtector;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SqliteServerProfileRepository(string databasePath, ISecretProtector secretProtector)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        ArgumentNullException.ThrowIfNull(secretProtector);

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true
        }.ToString();
        _secretProtector = secretProtector;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ApplyMigrationsAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<IReadOnlyList<ServerProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, group_name, host, port, username, auth_type, private_key_path,
                   default_remote_path, connect_timeout_ms, keep_alive_interval_ms, remark,
                   created_at_utc, updated_at_utc, last_connected_at_utc
            FROM server_profile
            ORDER BY name COLLATE NOCASE, id;
            """;

        var profiles = new List<ServerProfile>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            profiles.Add(ReadProfile(reader));
        }

        return profiles;
    }

    public async Task<ServerProfile?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateServerId(id);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, group_name, host, port, username, auth_type, private_key_path,
                   default_remote_path, connect_timeout_ms, keep_alive_interval_ms, remark,
                   created_at_utc, updated_at_utc, last_connected_at_utc
            FROM server_profile
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProfile(reader) : null;
    }

    public async Task SaveAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_profile (
                id, name, group_name, host, port, username, auth_type, private_key_path,
                default_remote_path, connect_timeout_ms, keep_alive_interval_ms, remark,
                created_at_utc, updated_at_utc, last_connected_at_utc)
            VALUES (
                $id, $name, $groupName, $host, $port, $username, $authenticationType, $privateKeyPath,
                $defaultRemotePath, $connectTimeoutMilliseconds, $keepAliveIntervalMilliseconds, $remark,
                $createdAt, $updatedAt, $lastConnectedAt)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                group_name = excluded.group_name,
                host = excluded.host,
                port = excluded.port,
                username = excluded.username,
                auth_type = excluded.auth_type,
                private_key_path = excluded.private_key_path,
                default_remote_path = excluded.default_remote_path,
                connect_timeout_ms = excluded.connect_timeout_ms,
                keep_alive_interval_ms = excluded.keep_alive_interval_ms,
                remark = excluded.remark,
                updated_at_utc = excluded.updated_at_utc,
                last_connected_at_utc = excluded.last_connected_at_utc;
            """;
        AddProfileParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        ValidateServerId(id);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_profile WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ServerCredential?> GetCredentialAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        ValidateServerId(serverId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT password_cipher, private_key_passphrase_cipher
            FROM server_credential
            WHERE server_id = $serverId;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ServerCredential(
            serverId,
            ReadProtectedText(reader, 0),
            ReadProtectedText(reader, 1));
    }

    public async Task SaveCredentialAsync(ServerCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        credential.Validate();

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO server_credential (server_id, password_cipher, private_key_passphrase_cipher)
            VALUES ($serverId, $passwordCipher, $privateKeyPassphraseCipher)
            ON CONFLICT(server_id) DO UPDATE SET
                password_cipher = excluded.password_cipher,
                private_key_passphrase_cipher = excluded.private_key_passphrase_cipher;
            """;
        command.Parameters.AddWithValue("$serverId", credential.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$passwordCipher", ProtectOrDbNull(credential.Password));
        command.Parameters.AddWithValue("$privateKeyPassphraseCipher", ProtectOrDbNull(credential.PrivateKeyPassphrase));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCredentialAsync(Guid serverId, CancellationToken cancellationToken = default)
    {
        ValidateServerId(serverId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM server_credential WHERE server_id = $serverId;";
        command.Parameters.AddWithValue("$serverId", serverId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyMigrationsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migration (
                    version INTEGER NOT NULL PRIMARY KEY,
                    applied_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!await IsMigrationAppliedAsync(connection, transaction, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false))
        {
            await ApplyInitialSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO schema_migration (version, applied_at_utc) VALUES ($version, $appliedAt);";
            command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            command.Parameters.AddWithValue("$appliedAt", ToStorageValue(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsMigrationAppliedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM schema_migration WHERE version = $version);";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) == 1;
    }

    private static async Task ApplyInitialSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE server_profile (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                group_name TEXT NULL,
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                username TEXT NOT NULL,
                auth_type INTEGER NOT NULL,
                private_key_path TEXT NULL,
                default_remote_path TEXT NULL,
                connect_timeout_ms INTEGER NOT NULL,
                keep_alive_interval_ms INTEGER NOT NULL,
                remark TEXT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                last_connected_at_utc TEXT NULL
            );

            CREATE TABLE server_credential (
                server_id TEXT NOT NULL PRIMARY KEY,
                password_cipher BLOB NULL,
                private_key_passphrase_cipher BLOB NULL,
                FOREIGN KEY (server_id) REFERENCES server_profile(id) ON DELETE CASCADE
            );

            CREATE INDEX ix_server_profile_name ON server_profile(name COLLATE NOCASE);
            CREATE INDEX ix_server_profile_group_name ON server_profile(group_name COLLATE NOCASE);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void AddProfileParameters(SqliteCommand command, ServerProfile profile)
    {
        command.Parameters.AddWithValue("$id", profile.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", profile.Name.Trim());
        command.Parameters.AddWithValue("$groupName", ToDbValue(profile.GroupName));
        command.Parameters.AddWithValue("$host", profile.Host.Trim());
        command.Parameters.AddWithValue("$port", profile.Port);
        command.Parameters.AddWithValue("$username", profile.Username.Trim());
        command.Parameters.AddWithValue("$authenticationType", (int)profile.AuthenticationType);
        command.Parameters.AddWithValue("$privateKeyPath", ToDbValue(profile.PrivateKeyPath));
        command.Parameters.AddWithValue("$defaultRemotePath", ToDbValue(profile.DefaultRemotePath));
        command.Parameters.AddWithValue("$connectTimeoutMilliseconds", checked((long)profile.ConnectTimeout.TotalMilliseconds));
        command.Parameters.AddWithValue("$keepAliveIntervalMilliseconds", checked((long)profile.KeepAliveInterval.TotalMilliseconds));
        command.Parameters.AddWithValue("$remark", ToDbValue(profile.Remark));
        command.Parameters.AddWithValue("$createdAt", ToStorageValue(profile.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToStorageValue(profile.UpdatedAt));
        command.Parameters.AddWithValue("$lastConnectedAt", profile.LastConnectedAt is { } lastConnectedAt
            ? ToStorageValue(lastConnectedAt)
            : DBNull.Value);
    }

    private ServerProfile ReadProfile(SqliteDataReader reader)
    {
        var authenticationType = (SshAuthenticationType)reader.GetInt32(6);
        if (!Enum.IsDefined(authenticationType))
        {
            throw new InvalidDataException("服务器资料包含不支持的认证方式。");
        }

        var profile = new ServerProfile(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            ReadNullableString(reader, 2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetString(5),
            authenticationType,
            ReadNullableString(reader, 7),
            ReadNullableString(reader, 8),
            TimeSpan.FromMilliseconds(reader.GetInt64(9)),
            TimeSpan.FromMilliseconds(reader.GetInt64(10)),
            ReadNullableString(reader, 11),
            ParseStorageValue(reader.GetString(12)),
            ParseStorageValue(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseStorageValue(reader.GetString(14)));
        profile.Validate();
        return profile;
    }

    private string? ReadProtectedText(SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Encoding.UTF8.GetString(_secretProtector.Unprotect((byte[])reader.GetValue(ordinal)));
    }

    private object ProtectOrDbNull(string? plaintext)
    {
        return plaintext is null
            ? DBNull.Value
            : _secretProtector.Protect(Encoding.UTF8.GetBytes(plaintext));
    }

    private static object ToDbValue(string? value) => value is null ? DBNull.Value : value;

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string ToStorageValue(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStorageValue(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateServerId(Guid serverId)
    {
        if (serverId == Guid.Empty)
        {
            throw new ArgumentException("服务器标识不能为空。", nameof(serverId));
        }
    }
}
