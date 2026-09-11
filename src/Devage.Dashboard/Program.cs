using Devage.Dashboard.Components;
using Devage.Dashboard.Services;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["Dashboard:Url"] ?? "http://127.0.0.1:5188");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var hostUrl = builder.Configuration["Host:Url"]
    ?? Environment.GetEnvironmentVariable("DEVAGE_HOST_URL")
    ?? "http://127.0.0.1:5088";

builder.Services.AddHttpClient<HostApiClient>(client =>
{
    client.BaseAddress = new Uri(hostUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(15);
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Logger.LogInformation("Devage.Dashboard listening. Host API: {HostUrl}", hostUrl);
await app.RunAsync();
