using DemoWebAPI.Models;
using DemoWebAPI.Services;

namespace DemoWebAPI.Endpoints;

public static class BusinessRuleEndpoints
{
    public static IEndpointRouteBuilder MapBusinessRuleEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api")
            .WithTags("Business rules");

        api.MapPost("/projects/{projectId:guid}/sprints/{sprintId:guid}/complete", async (
                Guid projectId,
                Guid sprintId,
                BusinessRulesService businessRules,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await businessRules.CompleteSprintAsync(projectId, sprintId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new ErrorResponse(StatusCodes.Status404NotFound, exception.Message));
                }
                catch (BusinessRuleConflictException exception)
                {
                    return Results.Conflict(new ErrorResponse(StatusCodes.Status409Conflict, exception.Message));
                }
            })
            .WithName("CompleteSprint");

        api.MapGet("/projects/{projectId:guid}/issues/{issueId:guid}/history", async (
                Guid projectId,
                Guid issueId,
                BusinessRulesService businessRules,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await businessRules.GetIssueHistoryAsync(projectId, issueId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new ErrorResponse(StatusCodes.Status404NotFound, exception.Message));
                }
            })
            .WithName("GetIssueHistory");

        api.MapDelete("/projects/{projectId:guid}/statuses/{statusId:guid}", async (
                Guid projectId,
                Guid statusId,
                BusinessRulesService businessRules,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await businessRules.DeleteStatusAsync(projectId, statusId, cancellationToken);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new ErrorResponse(StatusCodes.Status404NotFound, exception.Message));
                }
                catch (BusinessRuleConflictException exception)
                {
                    return Results.Conflict(new ErrorResponse(StatusCodes.Status409Conflict, exception.Message));
                }
            })
            .WithName("DeleteStatus");

        api.MapPatch("/projects/{projectId:guid}/issues/{issueId:guid}/position", async (
                Guid projectId,
                Guid issueId,
                MoveIssueRequest request,
                BusinessRulesService businessRules,
                CancellationToken cancellationToken) =>
            {
                try
                {
                    var result = await businessRules.MoveIssueAsync(projectId, issueId, request, cancellationToken);
                    return Results.Ok(result);
                }
                catch (KeyNotFoundException exception)
                {
                    return Results.NotFound(new ErrorResponse(StatusCodes.Status404NotFound, exception.Message));
                }
                catch (BusinessRuleConflictException exception)
                {
                    return Results.Conflict(new ErrorResponse(StatusCodes.Status409Conflict, exception.Message));
                }
            })
            .WithName("MoveIssuePosition");

        return app;
    }
}
