namespace DemoWebAPI.Models;

public sealed record DatabaseHealthResponse(
    string Status,
    string Database,
    string User,
    string ServerVersion,
    DateTime DatabaseTime);

public sealed record TableResponse(
    string Schema,
    string Name,
    string Type);

public sealed record ColumnResponse(
    string Name,
    string DataType,
    bool IsNullable,
    int Position);

public sealed record TableCountResponse(
    string Schema,
    string Table,
    long Count);

public sealed record TableRowsResponse(
    string Schema,
    string Table,
    int Limit,
    int Offset,
    IReadOnlyList<ColumnResponse> Columns,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);
