using System.Data;
using System.Data.Common;
using DemoWebAPI.Data;
using DemoWebAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace DemoWebAPI.Services;

public sealed class DatabaseExplorerService
{
    private const string SchemaName = "aml_task";
    private const int MaxRowsLimit = 500;

    private readonly AmlDbContext _dbContext;

    public DatabaseExplorerService(AmlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<DatabaseHealthResponse> GetHealthAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select
                current_database(),
                current_user,
                version(),
                now();
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new DataException("Database health query did not return a row.");
        }

        return new DatabaseHealthResponse(
            "ok",
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetDateTime(3));
    }

    public async Task<IReadOnlyList<TableResponse>> GetTablesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            select table_schema, table_name, table_type
            from information_schema.tables
            where table_schema = @schema
            order by table_name;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "schema", SchemaName);

        return await ReadListAsync(command, reader => new TableResponse(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2)), cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnResponse>> GetColumnsAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureTableExistsAsync(tableName, cancellationToken);

        const string sql = """
            select column_name, data_type, is_nullable, ordinal_position
            from information_schema.columns
            where table_schema = @schema
              and table_name = @table
            order by ordinal_position;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "schema", SchemaName);
        AddParameter(command, "table", tableName);

        return await ReadListAsync(command, reader => new ColumnResponse(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2).Equals("YES", StringComparison.OrdinalIgnoreCase),
            reader.GetInt32(3)), cancellationToken);
    }

    public async Task<TableCountResponse> GetRowCountAsync(
        string tableName,
        CancellationToken cancellationToken)
    {
        await EnsureTableExistsAsync(tableName, cancellationToken);

        var sql = $"select count(*) from {QuoteIdentifier(SchemaName)}.{QuoteIdentifier(tableName)};";

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new DataException("Count query did not return a value."));

        return new TableCountResponse(SchemaName, tableName, count);
    }

    public async Task<TableRowsResponse> GetRowsAsync(
        string tableName,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await EnsureTableExistsAsync(tableName, cancellationToken);

        var safeLimit = Math.Clamp(limit, 1, MaxRowsLimit);
        var safeOffset = Math.Max(offset, 0);
        var columns = await GetColumnsAsync(tableName, cancellationToken);
        var sql = $"""
            select *
            from {QuoteIdentifier(SchemaName)}.{QuoteIdentifier(tableName)}
            limit @limit offset @offset;
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "limit", safeLimit);
        AddParameter(command, "offset", safeOffset);

        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i)
                    ? null
                    : NormalizeValue(reader.GetValue(i));
            }

            rows.Add(row);
        }

        return new TableRowsResponse(SchemaName, tableName, safeLimit, safeOffset, columns, rows);
    }

    private async Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new ArgumentException("Table name is required.", nameof(tableName));
        }

        const string sql = """
            select exists (
                select 1
                from information_schema.tables
                where table_schema = @schema
                  and table_name = @table
            );
            """;

        await using var command = await CreateCommandAsync(sql, cancellationToken);
        AddParameter(command, "schema", SchemaName);
        AddParameter(command, "table", tableName);

        var exists = (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!exists)
        {
            throw new KeyNotFoundException($"Table '{SchemaName}.{tableName}' was not found.");
        }
    }

    private async Task<DbCommand> CreateCommandAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private static async Task<IReadOnlyList<T>> ReadListAsync<T>(
        DbCommand command,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(map(reader));
        }

        return result;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string QuoteIdentifier(string identifier) =>
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    private static object NormalizeValue(object value) =>
        value switch
        {
            DateTime dateTime => dateTime,
            DateOnly dateOnly => dateOnly,
            TimeOnly timeOnly => timeOnly,
            Guid guid => guid,
            byte[] bytes => Convert.ToBase64String(bytes),
            _ => value
        };
}
