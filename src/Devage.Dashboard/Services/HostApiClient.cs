using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devage.Core.Contracts;

namespace Devage.Dashboard.Services;

public sealed class HostApiClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public HostApiClient(HttpClient http) => _http = http;

    public async Task<HostHealthDto?> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<HostHealthDto>("/health", JsonOptions, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<AgentDto>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<AgentDto>>("/api/agents", JsonOptions, cancellationToken);
        return result ?? [];
    }

    public async Task<AgentDto?> GetAgentAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AgentDto>($"/api/agents/{Uri.EscapeDataString(idOrName)}", JsonOptions, cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<AgentStatusDto?> GetStatusAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetFromJsonAsync<AgentStatusDto>(
                $"/api/agents/{Uri.EscapeDataString(idOrName)}/status",
                JsonOptions,
                cancellationToken);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ActionLogDto>> GetLogsAsync(
        string idOrName,
        int take = 100,
        CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<ActionLogDto>>(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/logs?take={take}",
            JsonOptions,
            cancellationToken);
        return result ?? [];
    }

    public async Task<PlanDto?> GetActivePlanAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/plans/active",
            cancellationToken);

        if (response.StatusCode is System.Net.HttpStatusCode.NoContent or System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlanDto>(JsonOptions, cancellationToken);
    }

    public async Task<IReadOnlyList<PlanSummaryDto>> ListPlansAsync(
        string idOrName,
        int take = 10,
        CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<PlanSummaryDto>>(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/plans?take={take}",
            JsonOptions,
            cancellationToken);
        return result ?? [];
    }

    public async Task<IReadOnlyList<PendingConfirmationDto>> ListConfirmationsAsync(
        Guid? agentId = null,
        CancellationToken cancellationToken = default)
    {
        var url = agentId is Guid id
            ? $"/api/confirmations?agentId={id}"
            : "/api/confirmations";
        var result = await _http.GetFromJsonAsync<List<PendingConfirmationDto>>(url, JsonOptions, cancellationToken);
        return result ?? [];
    }

    public async Task<bool> ResolveConfirmationAsync(
        Guid stepId,
        bool approved,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/api/confirmations/{stepId}",
            new ConfirmationDecisionRequest(approved, comment),
            JsonOptions,
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StartAgentAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/start",
            new StartAgentRequest(null),
            JsonOptions,
            cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> StopAgentAsync(string idOrName, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            $"/api/agents/{Uri.EscapeDataString(idOrName)}/stop",
            content: null,
            cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
