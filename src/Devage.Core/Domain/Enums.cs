namespace Devage.Core.Domain;

public enum AgentRole
{
    Agent = 0,
    Master = 1
}

public enum AgentStatus
{
    Born = 0,
    Running = 1,
    Stopped = 2,
    Killed = 3
}

public enum PlanStepStatus
{
    Pending = 0,
    WaitingApproval = 1,
    Running = 2,
    Done = 3,
    Failed = 4,
    Skipped = 5
}

public enum DecisionClass
{
    Routine = 0,
    Important = 1,
    Critical = 2
}

public enum MasterReviewStatus
{
    NotRequired = 0,
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Modified = 4
}

public enum MasterPlanDecisionKind
{
    Approve = 0,
    Reject = 1,
    Modify = 2
}
