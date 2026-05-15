using System.Data;
using System.Data.Common;
using DemoWebAPI.Data;
using DemoWebAPI.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DemoWebAPI.Services;

public sealed class BusinessRulesService
{
    private readonly AmlDbContext _dbContext;

    public BusinessRulesService(AmlDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<CompleteSprintResponse> CompleteSprintAsync(
        Guid projectId,
        Guid sprintId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureProjectExistsAsync(connection, transaction, projectId, cancellationToken);
            await EnsureSprintBelongsToProjectAsync(connection, transaction, projectId, sprintId, cancellationToken);

            var backlogStatusId = await GetBacklogStatusIdAsync(connection, transaction, projectId, cancellationToken);
            var completedIssues = await CountCompletedSprintIssuesAsync(
                connection,
                transaction,
                projectId,
                sprintId,
                cancellationToken);

            var movedBackToBacklogIssues = await MoveUnfinishedSprintIssuesToBacklogAsync(
                connection,
                transaction,
                projectId,
                sprintId,
                backlogStatusId,
                cancellationToken);

            var updatedSprint = await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                update aml_task.sprints
                set status = 'completed',
                    completed_at = coalesce(completed_at, now()),
                    updated_at = now()
                where id = @sprint_id
                  and project_id = @project_id;
                """,
                cancellationToken,
                ("sprint_id", sprintId),
                ("project_id", projectId));

            if (updatedSprint == 0)
            {
                throw new KeyNotFoundException($"Sprint '{sprintId}' was not found in project '{projectId}'.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new CompleteSprintResponse(
                projectId,
                sprintId,
                "completed",
                completedIssues,
                movedBackToBacklogIssues,
                backlogStatusId);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<IssueHistoryEventResponse>> GetIssueHistoryAsync(
        Guid projectId,
        Guid issueId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureIssueBelongsToProjectAsync(connection, null, projectId, issueId, cancellationToken);

        var statusEvents = await ReadListAsync(
            connection,
            null,
            """
            select
                'status_history' as source,
                'status_changed' as event_type,
                ish.entered_at as occurred_at,
                u.full_name as actor_name,
                concat(
                    coalesce(fs.name, '<created>'),
                    ' -> ',
                    ts.name,
                    coalesce(': ' || ish.comment, '')
                ) as details
            from aml_task.issue_status_history ish
            left join aml_task.statuses fs on fs.id = ish.from_status_id
            join aml_task.statuses ts on ts.id = ish.to_status_id
            left join aml_task.users u on u.id = ish.changed_by
            where ish.issue_id = @issue_id
            order by ish.entered_at;
            """,
            reader => new IssueHistoryEventResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDateTime(2),
                ReadNullableString(reader, 3),
                reader.GetString(4)),
            cancellationToken,
            ("issue_id", issueId));

        var auditEvents = await ReadListAsync(
            connection,
            null,
            """
            select
                'audit_log' as source,
                lower(al.operation) as event_type,
                al.changed_at as occurred_at,
                u.full_name as actor_name,
                concat('record_pk=', al.record_pk::text) as details
            from aml_task.audit_log al
            left join aml_task.users u on u.id = al.changed_by
            where al.table_name = 'issues'
              and al.record_pk @> jsonb_build_object('id', to_jsonb(@issue_id::uuid))
            order by al.changed_at;
            """,
            reader => new IssueHistoryEventResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetDateTime(2),
                ReadNullableString(reader, 3),
                reader.GetString(4)),
            cancellationToken,
            ("issue_id", issueId));

        return statusEvents
            .Concat(auditEvents)
            .OrderBy(static item => item.OccurredAt)
            .ToList();
    }

    public async Task<DeleteStatusResponse> DeleteStatusAsync(
        Guid projectId,
        Guid statusId,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureStatusBelongsToProjectAsync(connection, transaction, projectId, statusId, cancellationToken);

            var issuesCount = await ExecuteScalarAsync<long>(
                connection,
                transaction,
                """
                select count(*)
                from aml_task.issues
                where project_id = @project_id
                  and status_id = @status_id
                  and deleted_at is null;
                """,
                cancellationToken,
                ("project_id", projectId),
                ("status_id", statusId));

            if (issuesCount > 0)
            {
                throw new BusinessRuleConflictException(
                    $"Cannot delete status with existing issues. Issues count: {issuesCount}.");
            }

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                delete from aml_task.statuses
                where id = @status_id
                  and project_id = @project_id;
                """,
                cancellationToken,
                ("project_id", projectId),
                ("status_id", statusId));

            await transaction.CommitAsync(cancellationToken);
            return new DeleteStatusResponse(projectId, statusId, "Status deleted.");
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new BusinessRuleConflictException("Cannot delete status because it is referenced by other records.");
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<MoveIssueResponse> MoveIssueAsync(
        Guid projectId,
        Guid issueId,
        MoveIssueRequest request,
        CancellationToken cancellationToken)
    {
        var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await EnsureStatusBelongsToProjectAsync(connection, transaction, projectId, request.StatusId, cancellationToken);

            var issue = await GetIssueStateAsync(connection, transaction, projectId, issueId, cancellationToken);
            var changedBy = request.ChangedBy ?? issue.ReporterId;
            await SetCurrentUserAsync(connection, transaction, changedBy, cancellationToken);

            await ExecuteNonQueryAsync(
                connection,
                transaction,
                """
                update aml_task.issues
                set status_id = @status_id,
                    rank_position = case when @has_rank_position then @rank_position else rank_position end,
                    updated_at = now()
                where id = @issue_id
                  and project_id = @project_id
                  and deleted_at is null;
                """,
                cancellationToken,
                ("project_id", projectId),
                ("issue_id", issueId),
                ("status_id", request.StatusId),
                ("has_rank_position", request.RankPosition.HasValue),
                ("rank_position", request.RankPosition ?? 0m));

            var statusChanged = issue.StatusId != request.StatusId;
            if (statusChanged)
            {
                await ExecuteNonQueryAsync(
                    connection,
                    transaction,
                    """
                    insert into aml_task.issue_status_history (
                        issue_id,
                        from_status_id,
                        to_status_id,
                        changed_by,
                        entered_at,
                        comment
                    )
                    values (
                        @issue_id,
                        @from_status_id,
                        @to_status_id,
                        @changed_by,
                        now(),
                        'Moved through API drag-and-drop demo'
                    );
                    """,
                    cancellationToken,
                    ("issue_id", issueId),
                    ("from_status_id", issue.StatusId),
                    ("to_status_id", request.StatusId),
                    ("changed_by", changedBy));
            }

            await transaction.CommitAsync(cancellationToken);

            return new MoveIssueResponse(
                projectId,
                issueId,
                issue.StatusId,
                request.StatusId,
                request.RankPosition ?? issue.RankPosition,
                statusChanged);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private async Task EnsureProjectExistsAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var exists = await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            "select exists(select 1 from aml_task.projects where id = @project_id);",
            cancellationToken,
            ("project_id", projectId));

        if (!exists)
        {
            throw new KeyNotFoundException($"Project '{projectId}' was not found.");
        }
    }

    private async Task EnsureSprintBelongsToProjectAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid projectId,
        Guid sprintId,
        CancellationToken cancellationToken)
    {
        var exists = await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            """
            select exists(
                select 1
                from aml_task.sprints
                where id = @sprint_id
                  and project_id = @project_id
            );
            """,
            cancellationToken,
            ("project_id", projectId),
            ("sprint_id", sprintId));

        if (!exists)
        {
            throw new KeyNotFoundException($"Sprint '{sprintId}' was not found in project '{projectId}'.");
        }
    }

    private async Task EnsureStatusBelongsToProjectAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid projectId,
        Guid statusId,
        CancellationToken cancellationToken)
    {
        var exists = await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            """
            select exists(
                select 1
                from aml_task.statuses
                where id = @status_id
                  and project_id = @project_id
            );
            """,
            cancellationToken,
            ("project_id", projectId),
            ("status_id", statusId));

        if (!exists)
        {
            throw new KeyNotFoundException($"Status '{statusId}' was not found in project '{projectId}'.");
        }
    }

    private async Task EnsureIssueBelongsToProjectAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid projectId,
        Guid issueId,
        CancellationToken cancellationToken)
    {
        var exists = await ExecuteScalarAsync<bool>(
            connection,
            transaction,
            """
            select exists(
                select 1
                from aml_task.issues
                where id = @issue_id
                  and project_id = @project_id
                  and deleted_at is null
            );
            """,
            cancellationToken,
            ("project_id", projectId),
            ("issue_id", issueId));

        if (!exists)
        {
            throw new KeyNotFoundException($"Issue '{issueId}' was not found in project '{projectId}'.");
        }
    }

    private async Task<Guid> GetBacklogStatusIdAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var values = await ReadListAsync(
            connection,
            transaction,
            """
            select id
            from aml_task.statuses
            where project_id = @project_id
              and category = 'todo'
            order by is_default desc, position
            limit 1;
            """,
            reader => reader.GetGuid(0),
            cancellationToken,
            ("project_id", projectId));

        if (values.Count == 0)
        {
            throw new BusinessRuleConflictException("Project does not have a backlog/todo status.");
        }

        return values[0];
    }

    private async Task<int> CountCompletedSprintIssuesAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid projectId,
        Guid sprintId,
        CancellationToken cancellationToken)
    {
        return await ExecuteScalarAsync<int>(
            connection,
            transaction,
            """
            select count(*)::int
            from aml_task.issues i
            join aml_task.statuses s on s.id = i.status_id
            where i.project_id = @project_id
              and i.sprint_id = @sprint_id
              and i.deleted_at is null
              and s.category = 'done';
            """,
            cancellationToken,
            ("project_id", projectId),
            ("sprint_id", sprintId));
    }

    private async Task<int> MoveUnfinishedSprintIssuesToBacklogAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid projectId,
        Guid sprintId,
        Guid backlogStatusId,
        CancellationToken cancellationToken)
    {
        return await ExecuteNonQueryAsync(
            connection,
            transaction,
            """
            update aml_task.issues i
            set sprint_id = null,
                status_id = @backlog_status_id,
                updated_at = now()
            where i.project_id = @project_id
              and i.sprint_id = @sprint_id
              and i.deleted_at is null
              and exists (
                  select 1
                  from aml_task.statuses s
                  where s.id = i.status_id
                    and s.category <> 'done'
              );
            """,
            cancellationToken,
            ("project_id", projectId),
            ("sprint_id", sprintId),
            ("backlog_status_id", backlogStatusId));
    }

    private async Task<IssueState> GetIssueStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid projectId,
        Guid issueId,
        CancellationToken cancellationToken)
    {
        var issues = await ReadListAsync(
            connection,
            transaction,
            """
            select status_id, reporter_id, rank_position
            from aml_task.issues
            where id = @issue_id
              and project_id = @project_id
              and deleted_at is null;
            """,
            reader => new IssueState(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2)),
            cancellationToken,
            ("project_id", projectId),
            ("issue_id", issueId));

        return issues.SingleOrDefault()
            ?? throw new KeyNotFoundException($"Issue '{issueId}' was not found in project '{projectId}'.");
    }

    private async Task SetCurrentUserAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await ExecuteScalarNullableAsync<string>(
            connection,
            transaction,
            "select set_config('app.current_user_id', @user_id, true);",
            cancellationToken,
            ("user_id", userId.ToString()));
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var value = await ExecuteScalarNullableAsync<T>(
            connection,
            transaction,
            sql,
            cancellationToken,
            parameters);

        return value ?? throw new DataException("Query did not return a value.");
    }

    private static async Task<T?> ExecuteScalarNullableAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);

        if (value is null || value is DBNull)
        {
            return default;
        }

        return (T)value;
    }

    private static async Task<int> ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> ReadListAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        var result = new List<T>();
        await using var command = CreateCommand(connection, transaction, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(map(reader));
        }

        return result;
    }

    private static DbCommand CreateCommand(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return command;
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string? ReadNullableString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private sealed record IssueState(Guid StatusId, Guid ReporterId, decimal? RankPosition);
}
