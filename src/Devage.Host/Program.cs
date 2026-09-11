using Devage.Core.Contracts;
using Devage.Core.Logging;
using Devage.Core.Planning;
using Devage.Host.Logging;
using Devage.Host.Plugins;
using Devage.Host.Runtime;
using Devage.Host.Services;
using Devage.Llm;
using Devage.Persistence;
using Devage.Tools.Abstractions;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DevageHost";
});

builder.WebHost.UseUrls(builder.Configuration["Host:Url"] ?? "http://127.0.0.1:5088");

builder.Services.AddDevagePersistence(builder.Configuration);
builder.Services.Configure<OpenAiOptions>(builder.Configuration.GetSection(OpenAiOptions.SectionName));
builder.Services.AddDevageLlm();
builder.Services.AddHttpClient("devage-web");

builder.Services.AddSingleton<IToolRegistry, PluginToolRegistry>();
builder.Services.AddSingleton<ConfirmationBroker>();
builder.Services.AddSingleton<IUserConfirmationGate>(sp => sp.GetRequiredService<ConfirmationBroker>());
builder.Services.AddSingleton<MasterApprovalBroker>();
builder.Services.AddSingleton<IMasterApprovalGate>(sp => sp.GetRequiredService<MasterApprovalBroker>());
builder.Services.AddSingleton<IActionLogger, TripleActionLogger>();
builder.Services.AddSingleton<IPlanEngine, LlmPlanEngine>();
builder.Services.AddSingleton<IDecisionClassifier, LlmDecisionClassifier>();
builder.Services.AddSingleton<AgentRuntimeService>();
builder.Services.AddSingleton<AgentRuntimeServiceBridge>();
builder.Services.AddScoped<AgentService>();
builder.Services.AddHostedService<GraphToolPollingHostedService>();

var app = builder.Build();
var startedAt = DateTimeOffset.UtcNow;

try
{
    await app.Services.MigrateDevageDatabaseAsync();
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Database migration failed. Ensure PostgreSQL is running (docker compose up -d).");
    throw;
}

var runtime = app.Services.GetRequiredService<AgentRuntimeService>();
await runtime.ResumeRunningAgentsAsync();

app.MapGet("/health", (AgentRuntimeServiceBridge bridge) =>
    Results.Ok(new HostHealthDto(true, typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0", startedAt, bridge.RunningCount)));

app.MapGet("/api/tools", (AgentService agents) => Results.Ok(agents.ListAvailableTools()));

app.MapGet("/api/agents", async (AgentService agents, CancellationToken ct) =>
    Results.Ok(await agents.ListAsync(ct)));

app.MapGet("/api/agents/{idOrName}", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    var agent = await agents.ResolveAsync(idOrName, ct);
    if (agent is null)
    {
        return Results.NotFound();
    }

    var dto = await agents.GetAsync(agent.Id, ct);
    return dto is null ? Results.NotFound() : Results.Ok(dto);
});

app.MapPost("/api/agents/born", async (BornAgentRequest request, AgentService agents, CancellationToken ct) =>
{
    try
    {
        var created = await agents.BornAsync(request, ct);
        return Results.Created($"/api/agents/{created.Id}", created);
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/agents/{idOrName}/start", async (string idOrName, StartAgentRequest? body, AgentService agents, CancellationToken ct) =>
{
    try
    {
        await agents.StartAsync(idOrName, body?.Goal, ct);
        return Results.Ok(new { status = "Running" });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/agents/{idOrName}/stop", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        await agents.StopAsync(idOrName, ct);
        return Results.Ok(new { status = "Stopped" });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/agents/{idOrName}/kill", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        await agents.KillAsync(idOrName, ct);
        return Results.Ok(new { status = "Killed" });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/agents/{idOrName}/status", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.StatusAsync(idOrName, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/agents/{idOrName}/logs", async (string idOrName, int? take, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.LogsAsync(idOrName, take ?? 100, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/agents/{idOrName}/plans", async (string idOrName, int? take, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.ListPlansAsync(idOrName, take ?? 20, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/agents/{idOrName}/plans/active", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        var plan = await agents.GetActivePlanAsync(idOrName, ct);
        return plan is null ? Results.NoContent() : Results.Ok(plan);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/agents/{idOrName}/plans/{planId:guid}", async (string idOrName, Guid planId, AgentService agents, CancellationToken ct) =>
{
    try
    {
        var plan = await agents.GetPlanAsync(idOrName, planId, ct);
        return plan is null ? Results.NotFound() : Results.Ok(plan);
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/agents/{idOrName}/master", async (string idOrName, AssignMasterRequest body, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.AssignMasterAsync(idOrName, body, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/masters/{idOrName}/agents", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.ListSubordinatesAsync(idOrName, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/masters/{idOrName}/pending-plans", async (string idOrName, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.ListPendingMasterPlansAsync(idOrName, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/plans/pending", async (string? master, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.ListPendingMasterPlansAsync(master, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/plans/{planId:guid}/master-decision", async (Guid planId, MasterPlanDecisionRequest body, AgentService agents, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await agents.DecideMasterPlanAsync(planId, body, ct));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/confirmations", (ConfirmationBroker broker, Guid? agentId) =>
    Results.Ok(broker.ListPending(agentId)));

app.MapPost("/api/confirmations/{stepId:guid}", (Guid stepId, ConfirmationDecisionRequest body, ConfirmationBroker broker) =>
{
    if (!broker.TryResolve(stepId, body.Approved))
    {
        return Results.NotFound(new { error = "No pending confirmation for step." });
    }

    return Results.Ok(new { stepId, approved = body.Approved });
});

app.Logger.LogInformation("Devage.Host listening. Control API ready.");
await app.RunAsync();

public partial class Program;
