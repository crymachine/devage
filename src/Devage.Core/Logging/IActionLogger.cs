using Devage.Core.Domain;

namespace Devage.Core.Logging;

public interface IActionLogger
{
    Task LogAsync(
        Guid agentId,
        string category,
        string message,
        Guid? planId = null,
        Guid? stepId = null,
        object? details = null,
        CancellationToken cancellationToken = default);
}
