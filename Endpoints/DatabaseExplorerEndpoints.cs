using DemoWebAPI.Services;

namespace DemoWebAPI.Endpoints;

public static class DatabaseExplorerEndpoints
{
    public static IEndpointRouteBuilder MapDatabaseExplorerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Database explorer");

        api.MapGet("/health", async (
                DatabaseExplorerService database,
                CancellationToken cancellationToken) =>
            {
                var health = await database.GetHealthAsync(cancellationToken);
                return Results.Ok(health);
            })
            .WithName("GetDatabaseHealth");

        api.MapGet("/tables", async (
                DatabaseExplorerService database,
                CancellationToken cancellationToken) =>
            {
                var tables = await database.GetTablesAsync(cancellationToken);
                return Results.Ok(tables);
            })
            .WithName("GetTables");

        api.MapGet("/tables/{tableName}/columns", async (
                string tableName,
                DatabaseExplorerService database,
                CancellationToken cancellationToken) =>
            {
                var columns = await database.GetColumnsAsync(tableName, cancellationToken);
                return Results.Ok(columns);
            })
            .WithName("GetTableColumns");

        api.MapGet("/tables/{tableName}/count", async (
                string tableName,
                DatabaseExplorerService database,
                CancellationToken cancellationToken) =>
            {
                var count = await database.GetRowCountAsync(tableName, cancellationToken);
                return Results.Ok(count);
            })
            .WithName("GetTableCount");

        api.MapGet("/tables/{tableName}/rows", async (
                string tableName,
                int? limit,
                int? offset,
                DatabaseExplorerService database,
                CancellationToken cancellationToken) =>
            {
                var rows = await database.GetRowsAsync(
                    tableName,
                    limit ?? 50,
                    offset ?? 0,
                    cancellationToken);

                return Results.Ok(rows);
            })
            .WithName("GetTableRows");

        return app;
    }
}
