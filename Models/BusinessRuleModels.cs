namespace DemoWebAPI.Models;

public sealed record CompleteSprintResponse(
    Guid ProjectId,
    Guid SprintId,
    string SprintStatus,
    int CompletedIssues,
    int MovedBackToBacklogIssues,
    Guid BacklogStatusId);

public sealed record IssueHistoryEventResponse(
    string Source,
    string EventType,
    DateTime OccurredAt,
    string? ActorName,
    string Details);

public sealed record MoveIssueRequest(
    Guid StatusId,
    decimal? RankPosition,
    Guid? ChangedBy);

public sealed record MoveIssueResponse(
    Guid ProjectId,
    Guid IssueId,
    Guid OldStatusId,
    Guid NewStatusId,
    decimal? RankPosition,
    bool StatusChanged);

public sealed record DeleteStatusResponse(
    Guid ProjectId,
    Guid StatusId,
    string Message);

public sealed record ErrorResponse(
    int StatusCode,
    string Message);
