namespace Devage.Tools.Teams.Graph;

internal static class WorkspaceHistory
{
    public static string LoadProjectContext(string workspaceRoot, int maxChars = 4000)
    {
        var parts = new List<string>();
        var notes = Path.Combine(workspaceRoot, "devage-notes.md");
        if (File.Exists(notes))
        {
            parts.Add("--- workspace notes ---\n" + Truncate(File.ReadAllText(notes), maxChars / 2));
        }

        var outputDir = Path.Combine(workspaceRoot, "tool-output");
        if (Directory.Exists(outputDir))
        {
            foreach (var file in Directory.EnumerateFiles(outputDir, "*.txt")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Take(3))
            {
                parts.Add($"--- {Path.GetFileName(file)} ---\n" + Truncate(File.ReadAllText(file), 800));
            }
        }

        return parts.Count == 0
            ? "(no project history found in workspace)"
            : Truncate(string.Join("\n\n", parts), maxChars);
    }

    public static string ProposeTranslatedSummary(string language, string from, string bodyPreview)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "it" : language.Trim();
        return $"""
            Proposed Teams summary ({lang}):
            - From: {from}
            - Essence: {Truncate(bodyPreview, 400)}
            - Suggested {lang} headline: [{lang.ToUpperInvariant()}] {Truncate(bodyPreview, 80)}
            """;
    }

    public static string ProposeReply(string language, string projectContext, string bodyPreview)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "it" : language.Trim();
        return $"""
            Proposed Teams reply draft ({lang}) — NOT SENT (requires Critical approval):
            ---
            Thanks for the update.

            Project context:
            {Truncate(projectContext, 500)}

            Re: {Truncate(bodyPreview, 200)}

            — Devage agent
            ---
            """;
    }

    private static string Truncate(string text, int max)
    {
        text = text.Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }
}
