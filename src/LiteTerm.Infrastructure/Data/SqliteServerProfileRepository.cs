using System.Globalization;
using System.Text;
using System.Text.Json;
using LiteTerm.Core.Connections;
using LiteTerm.Core.Logs;
using LiteTerm.Core.QuickCommands;
using LiteTerm.Core.Security;
using LiteTerm.Core.Servers;
using LiteTerm.Core.Settings;
using Microsoft.Data.Sqlite;

namespace LiteTerm.Infrastructure.Data;

/// <summary>
/// 使用版本化 SQLite 架构保存服务器公开资料、经 DPAPI 保护的凭据和已知主机身份。
/// </summary>
public sealed class SqliteServerProfileRepository :
    IServerProfileRepository,
    IKnownHostStore,
    IApplicationAppearanceSettingsStore,
    IQuickCommandStore,
    IServerLogEntryStore
{
    private const int AppSettingsSchemaVersion = 3;
    private const int CurrentSchemaVersion = 4;
    private const string TerminalAppearanceSettingKey = "terminal.appearance";
    private const string ApplicationThemeSettingKey = "application.theme";
    private const string QuickCommandsSettingKey = "quick.commands";
    private const string UpsertProfileSql = """
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
    private const string UpsertCredentialSql = """
        INSERT INTO server_credential (server_id, password_cipher, private_key_passphrase_cipher)
        VALUES ($serverId, $passwordCipher, $privateKeyPassphraseCipher)
        ON CONFLICT(server_id) DO UPDATE SET
            password_cipher = excluded.password_cipher,
            private_key_passphrase_cipher = excluded.private_key_passphrase_cipher;
        """;

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string? _legacyKnownHostsPath;
    private readonly ISecretProtector _secretProtector;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public SqliteServerProfileRepository(
        string databasePath,
        ISecretProtector secretProtector,
        string? legacyKnownHostsPath = null)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("数据库路径不能为空。", nameof(databasePath));
        }

        ArgumentNullException.ThrowIfNull(secretProtector);

        _databasePath = Path.GetFullPath(databasePath);
        _legacyKnownHostsPath = string.IsNullOrWhiteSpace(legacyKnownHostsPath)
            ? null
            : Path.GetFullPath(legacyKnownHostsPath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = false
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
            await ImportLegacyKnownHostsAsync(connection, cancellationToken).ConfigureAwait(false);
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
        command.CommandText = UpsertProfileSql;
        AddProfileParameters(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveWithCredentialAsync(
        ServerProfile profile,
        ServerCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(credential);
        profile.Validate();
        credential.Validate();
        if (credential.ServerId != profile.Id)
        {
            throw new ArgumentException("凭据必须属于同一服务器资料。", nameof(credential));
        }

        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var profileCommand = connection.CreateCommand())
        {
            profileCommand.Transaction = (SqliteTransaction)transaction;
            profileCommand.CommandText = UpsertProfileSql;
            AddProfileParameters(profileCommand, profile);
            await profileCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var credentialCommand = connection.CreateCommand())
        {
            credentialCommand.Transaction = (SqliteTransaction)transaction;
            credentialCommand.CommandText = UpsertCredentialSql;
            AddCredentialParameters(credentialCommand, credential);
            await credentialCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        command.CommandText = UpsertCredentialSql;
        AddCredentialParameters(command, credential);
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

    public KnownHostVerificationResult Verify(string host, int port, HostKeyInfo hostKey)
    {
        EnsureInitialized();
        var normalizedHost = NormalizeHost(host);
        ValidatePort(port);
        ValidateHostKey(hostKey);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT algorithm, sha256_fingerprint FROM known_host WHERE host = $host AND port = $port;";
        command.Parameters.AddWithValue("$host", normalizedHost);
        command.Parameters.AddWithValue("$port", port);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new KnownHostVerificationResult(KnownHostVerificationStatus.Unknown, null);
        }

        var expected = new KnownHostEntry(normalizedHost, port, reader.GetString(0), reader.GetString(1));
        var isMatch = string.Equals(expected.Algorithm, hostKey.Algorithm, StringComparison.Ordinal)
                      && string.Equals(expected.Sha256Fingerprint, hostKey.Sha256Fingerprint, StringComparison.Ordinal);
        return new KnownHostVerificationResult(
            isMatch ? KnownHostVerificationStatus.Trusted : KnownHostVerificationStatus.Mismatch,
            expected);
    }

    public void Trust(string host, int port, HostKeyInfo hostKey)
    {
        EnsureInitialized();
        var normalizedHost = NormalizeHost(host);
        ValidatePort(port);
        ValidateHostKey(hostKey);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO known_host (host, port, algorithm, sha256_fingerprint, trusted_at_utc)
            VALUES ($host, $port, $algorithm, $fingerprint, $trustedAt)
            ON CONFLICT(host, port) DO UPDATE SET
                algorithm = excluded.algorithm,
                sha256_fingerprint = excluded.sha256_fingerprint,
                trusted_at_utc = excluded.trusted_at_utc;
            """;
        AddKnownHostParameters(command, normalizedHost, port, hostKey, DateTimeOffset.UtcNow);
        command.ExecuteNonQuery();
    }

    public async Task<TerminalAppearanceSettings> GetTerminalAppearanceAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", TerminalAppearanceSettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (value is null)
        {
            return TerminalAppearanceSettings.Default;
        }

        var settings = JsonSerializer.Deserialize<TerminalAppearanceSettings>(value)
            ?? throw new InvalidDataException("终端外观设置格式无效。");
        return settings.Normalize();
    }

    public async Task SaveTerminalAppearanceAsync(
        TerminalAppearanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalizedSettings = settings.Normalize();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_setting (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", TerminalAppearanceSettingKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalizedSettings));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ApplicationTheme> GetApplicationThemeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", ApplicationThemeSettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (value is null)
        {
            return ApplicationTheme.Dark;
        }

        if (!Enum.TryParse<ApplicationTheme>(value, ignoreCase: false, out var theme)
            || !Enum.IsDefined(theme))
        {
            throw new InvalidDataException("应用主题设置格式无效。");
        }

        return theme;
    }

    public async Task SaveApplicationAppearanceAsync(
        ApplicationTheme applicationTheme,
        TerminalAppearanceSettings terminalAppearance,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(applicationTheme))
        {
            throw new ArgumentOutOfRangeException(nameof(applicationTheme), "应用主题无效。");
        }

        ArgumentNullException.ThrowIfNull(terminalAppearance);
        var normalizedTerminalAppearance = terminalAppearance.Normalize();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await UpsertAppSettingAsync(
            connection,
            transaction,
            ApplicationThemeSettingKey,
            applicationTheme.ToString(),
            cancellationToken).ConfigureAwait(false);
        await UpsertAppSettingAsync(
            connection,
            transaction,
            TerminalAppearanceSettingKey,
            JsonSerializer.Serialize(normalizedTerminalAppearance),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<QuickCommandDefinition>> GetQuickCommandsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_setting WHERE key = $key;";
        command.Parameters.AddWithValue("$key", QuickCommandsSettingKey);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (value is null)
        {
            return QuickCommandDefinition.Defaults;
        }

        var definitions = JsonSerializer.Deserialize<List<QuickCommandDefinition>>(value)
            ?? throw new InvalidDataException("常用命令设置格式无效。");
        return QuickCommandDefinition.NormalizeAll(definitions);
    }

    public async Task SaveQuickCommandsAsync(
        IReadOnlyList<QuickCommandDefinition> definitions,
        CancellationToken cancellationToken = default)
    {
        var normalized = QuickCommandDefinition.NormalizeAll(definitions);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_setting (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", QuickCommandsSettingKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalized));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ServerLogEntry>> GetForServerAsync(
        Guid serverId,
        CancellationToken cancellationToken = default)
    {
        ValidateServerId(serverId);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, server_id, name, remote_path
            FROM server_log_entry
            WHERE server_id = $serverId
            ORDER BY name COLLATE NOCASE, id;
            """;
        command.Parameters.AddWithValue("$serverId", serverId.ToString("D"));

        var entries = new List<ServerLogEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ServerLogEntry(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3)).Normalize());
        }

        return entries;
    }

    public async Task ReplaceForServerAsync(
        Guid serverId,
        IReadOnlyList<ServerLogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        var normalized = ServerLogEntry.NormalizeAll(serverId, entries);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using (var serverCommand = connection.CreateCommand())
        {
            serverCommand.Transaction = transaction;
            serverCommand.CommandText = "SELECT EXISTS(SELECT 1 FROM server_profile WHERE id = $serverId);";
            serverCommand.Parameters.AddWithValue("$serverId", serverId.ToString("D"));
            var exists = Convert.ToInt64(
                await serverCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
            if (!exists)
            {
                throw new InvalidOperationException("日志入口所属的服务器资料不存在。");
            }
        }

        await using (var deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = "DELETE FROM server_log_entry WHERE server_id = $serverId;";
            deleteCommand.Parameters.AddWithValue("$serverId", serverId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var entry in normalized)
        {
            await using var insertCommand = connection.CreateCommand();
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = """
                INSERT INTO server_log_entry (id, server_id, name, remote_path)
                VALUES ($id, $serverId, $name, $remotePath);
                """;
            insertCommand.Parameters.AddWithValue("$id", entry.Id.ToString("D"));
            insertCommand.Parameters.AddWithValue("$serverId", entry.ServerId.ToString("D"));
            insertCommand.Parameters.AddWithValue("$name", entry.Name);
            insertCommand.Parameters.AddWithValue("$remotePath", entry.RemotePath);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAppSettingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_setting (key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
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
        if (!await IsMigrationAppliedAsync(connection, transaction, 1, cancellationToken).ConfigureAwait(false))
        {
            await ApplyInitialSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, 1, cancellationToken).ConfigureAwait(false);
        }

        if (!await IsMigrationAppliedAsync(connection, transaction, 2, cancellationToken).ConfigureAwait(false))
        {
            await ApplyKnownHostsSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, 2, cancellationToken).ConfigureAwait(false);
        }

        if (!await IsMigrationAppliedAsync(connection, transaction, AppSettingsSchemaVersion, cancellationToken).ConfigureAwait(false))
        {
            await ApplyAppSettingsSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, AppSettingsSchemaVersion, cancellationToken).ConfigureAwait(false);
        }

        if (!await IsMigrationAppliedAsync(connection, transaction, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false))
        {
            await ApplyServerLogEntriesSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RecordMigrationAsync(connection, transaction, CurrentSchemaVersion, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RecordMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO schema_migration (version, applied_at_utc) VALUES ($version, $appliedAt);";
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$appliedAt", ToStorageValue(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task ApplyKnownHostsSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE known_host (
                host TEXT NOT NULL,
                port INTEGER NOT NULL,
                algorithm TEXT NOT NULL,
                sha256_fingerprint TEXT NOT NULL,
                trusted_at_utc TEXT NOT NULL,
                PRIMARY KEY (host, port)
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyAppSettingsSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE app_setting (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyServerLogEntriesSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE server_log_entry (
                id TEXT NOT NULL PRIMARY KEY,
                server_id TEXT NOT NULL,
                name TEXT NOT NULL COLLATE NOCASE,
                remote_path TEXT NOT NULL,
                FOREIGN KEY (server_id) REFERENCES server_profile(id) ON DELETE CASCADE,
                UNIQUE (server_id, name)
            );

            CREATE INDEX ix_server_log_entry_server_id ON server_log_entry(server_id);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ImportLegacyKnownHostsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_legacyKnownHostsPath is null || !File.Exists(_legacyKnownHostsPath))
        {
            return;
        }

        await using var stream = new FileStream(_legacyKnownHostsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var entries = await JsonSerializer.DeserializeAsync<List<KnownHostEntry>>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("旧版已知主机文件格式无效。");

        foreach (var entry in entries)
        {
            var normalizedHost = NormalizeHost(entry.Host);
            ValidatePort(entry.Port);
            var hostKey = new HostKeyInfo(entry.Algorithm, entry.Sha256Fingerprint);
            ValidateHostKey(hostKey);

            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO known_host (host, port, algorithm, sha256_fingerprint, trusted_at_utc)
                VALUES ($host, $port, $algorithm, $fingerprint, $trustedAt);
                """;
            AddKnownHostParameters(command, normalizedHost, entry.Port, hostKey, DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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

    private void AddCredentialParameters(SqliteCommand command, ServerCredential credential)
    {
        command.Parameters.AddWithValue("$serverId", credential.ServerId.ToString("D"));
        command.Parameters.AddWithValue("$passwordCipher", ProtectOrDbNull(credential.Password));
        command.Parameters.AddWithValue("$privateKeyPassphraseCipher", ProtectOrDbNull(credential.PrivateKeyPassphrase));
    }

    private static void AddKnownHostParameters(
        SqliteCommand command,
        string host,
        int port,
        HostKeyInfo hostKey,
        DateTimeOffset trustedAt)
    {
        command.Parameters.AddWithValue("$host", host);
        command.Parameters.AddWithValue("$port", port);
        command.Parameters.AddWithValue("$algorithm", hostKey.Algorithm);
        command.Parameters.AddWithValue("$fingerprint", hostKey.Sha256Fingerprint);
        command.Parameters.AddWithValue("$trustedAt", ToStorageValue(trustedAt));
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

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("服务器数据存储尚未初始化。");
        }
    }

    private static string NormalizeHost(string host)
    {
        var normalizedHost = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            throw new ArgumentException("主机不能为空。", nameof(host));
        }

        return normalizedHost;
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1 和 65535 之间。");
        }
    }

    private static void ValidateHostKey(HostKeyInfo hostKey)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        if (string.IsNullOrWhiteSpace(hostKey.Algorithm) || string.IsNullOrWhiteSpace(hostKey.Sha256Fingerprint))
        {
            throw new ArgumentException("主机密钥信息无效。", nameof(hostKey));
        }
    }
}
