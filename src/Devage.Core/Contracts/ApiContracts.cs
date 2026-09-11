using System.Text.Json.Serialization;

namespace Devage.Core.Contracts;

public sealed record AgentDto(
    Guid Id,
    string Name,
    string Role,
    Guid? MasterId,
    string Status,
    string WorkspaceRoot,
    string? Goal,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ToolBindingDto> ToolBindings);

public sealed record ToolBindingDto(string ToolName, bool Enabled, string ConfigJson);

public sealed record AgentStatusDto(
    Guid Id,
    string Name,
    string Status,
    string? Goal,
    Guid? ActivePlanId,
    string? ActivePlanTitle,
    int? CurrentStepOrdinal,
    string? CurrentStepTitle,
    string? CurrentStepStatus,
    DateTimeOffset UpdatedAt);

public sealed record ActionLogDto(
    Guid Id,
    string Category,
    string Message,
    DateTimeOffset CreatedAt,
    string? DetailsJson);

public sealed record PlanDto(
    Guid Id,
    Guid AgentId,
    string Title,
    string Goal,
    bool MasterApproved,
    string MasterReviewStatus,
    string? MasterComment,
    DateTimeOffset? MasterDecidedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PlanStepDto> Steps);

public sealed record PlanStepDto(
    Guid Id,
    int Ordinal,
    string Title,
    string Description,
    string? ToolName,
    string DecisionClass,
    string Status,
    string? Justification,
    string? ResultSummary,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record PlanSummaryDto(
    Guid Id,
    string Title,
    string Goal,
    bool MasterApproved,
    string MasterReviewStatus,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int StepCount,
    string? LatestStepStatus);

public sealed record BornAgentRequest(
    string Name,
    string WorkspaceRoot,
    string? Goal,
    string Role,
    Guid? MasterId,
    IReadOnlyList<ToolBindingRequest> Tools);

public sealed record ToolBindingRequest(string ToolName, string ConfigJson);

public sealed record StartAgentRequest(string? Goal);

public sealed record ConfirmationDecisionRequest(bool Approved, string? Comment);

public sealed record PendingConfirmationDto(
    Guid AgentId,
    Guid PlanId,
    Guid StepId,
    int StepOrdinal,
    string StepTitle,
    string Description,
    string DecisionClass,
    string? Justification,
    string? ProposedViewpoint);

public sealed record AssignMasterRequest(string? MasterIdOrName);

public sealed record MasterPlanStepDto(
    string Title,
    string Description,
    string? ToolName,
    string? ToolInputJson,
    string DecisionClass,
    string? Justification);

public sealed record MasterPlanDecisionRequest(
    string Decision,
    string? Comment,
    string? ModifiedTitle,
    IReadOnlyList<MasterPlanStepDto>? ModifiedSteps);

public sealed record PendingMasterPlanStepDto(
    int Ordinal,
    string Title,
    string Description,
    string? ToolName,
    string DecisionClass,
    string? Justification);

public sealed record PendingMasterPlanDto(
    Guid PlanId,
    Guid AgentId,
    string AgentName,
    Guid MasterId,
    string MasterName,
    string Title,
    string Goal,
    string ReviewStatus,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PendingMasterPlanStepDto> Steps);

public sealed record HostHealthDto(bool Healthy, string Version, DateTimeOffset StartedAt, int RunningAgents);

public sealed record AvailableToolDto(string Name, string Description, IReadOnlyList<ToolConfigFieldDto> ConfigFields);

public sealed record ToolConfigFieldDto(string Key, string Label, string? DefaultValue, bool Required);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ApiDecisionClass
{
    Routine,
    Important,
    Critical
}
