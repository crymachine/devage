using System.Text.Json;

namespace Devage.Tools.Email.Graph;

internal sealed class PollStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public PollStateStore(string workspaceRoot, string fileName)
    {
        var dir = Path.Combine(workspaceRoot, ".devage");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, fileName);
    }

    public async Task<PollState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new PollState();
        }

        await using var stream = File.OpenRead(_path);
        var state = await JsonSerializer.DeserializeAsync<PollState>(stream, JsonOptions, cancellationToken);
        return state ?? new PollState();
    }

    public async Task SaveAsync(PollState state, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
    }
}

internal sealed class PollState
{
    public DateTimeOffset? LastMailCheckUtc { get; set; }
    public DateTimeOffset? LastCalendarCheckUtc { get; set; }
    public List<string> SeenMessageIds { get; set; } = [];
    public List<string> SeenEventIds { get; set; } = [];
}
