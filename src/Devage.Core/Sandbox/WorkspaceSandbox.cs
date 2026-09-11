using System.Text.RegularExpressions;

namespace Devage.Core.Sandbox;

/// <summary>
/// Ensures all file/path operations stay inside an agent's WorkspaceRoot.
/// </summary>
public sealed class WorkspaceSandbox
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
    private readonly string _root;

    public WorkspaceSandbox(string workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(workspaceRoot))
        {
            throw new ArgumentException("Workspace root is required.", nameof(workspaceRoot));
        }

        _root = Path.GetFullPath(workspaceRoot);
        if (_root.IndexOf('\0') >= 0)
        {
            throw new ArgumentException("Workspace root contains invalid characters.", nameof(workspaceRoot));
        }

        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public string Resolve(string relativeOrAbsolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativeOrAbsolutePath);

        if (relativeOrAbsolutePath.IndexOf('\0') >= 0)
        {
            throw new UnauthorizedAccessException("Path contains a null character.");
        }

        // Block Windows device / extended paths that can escape normal sandbox checks.
        if (LooksLikeDeviceOrExtendedPath(relativeOrAbsolutePath))
        {
            throw new UnauthorizedAccessException(
                $"Path '{relativeOrAbsolutePath}' uses a disallowed device or extended path form.");
        }

        // Alternate data streams (e.g. "notes.md:hidden") are not valid sandbox targets.
        if (ContainsAlternateDataStream(relativeOrAbsolutePath))
        {
            throw new UnauthorizedAccessException(
                $"Path '{relativeOrAbsolutePath}' contains an alternate data stream separator.");
        }

        string candidate;
        try
        {
            candidate = Path.IsPathRooted(relativeOrAbsolutePath)
                ? Path.GetFullPath(relativeOrAbsolutePath)
                : Path.GetFullPath(Path.Combine(_root, relativeOrAbsolutePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new UnauthorizedAccessException($"Path '{relativeOrAbsolutePath}' is invalid.", ex);
        }

        if (!IsInsideWorkspace(candidate))
        {
            throw new UnauthorizedAccessException(
                $"Path '{candidate}' is outside workspace sandbox '{_root}'.");
        }

        return candidate;
    }

    public bool IsInsideWorkspace(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.IndexOf('\0') >= 0)
        {
            return false;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(fullPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Require a directory boundary so "C:\workspace" does not allow "C:\workspace-evil".
        return normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public string SanitizeRelative(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var cleaned = Regex.Replace(name, @"[^\w\-. ]+", "_").Trim();
        cleaned = cleaned.TrimEnd('.', ' ');
        foreach (var c in InvalidFileNameChars)
        {
            cleaned = cleaned.Replace(c, '_');
        }

        if (cleaned is "." or ".." || string.IsNullOrWhiteSpace(cleaned))
        {
            return "workspace";
        }

        return cleaned;
    }

    private static bool LooksLikeDeviceOrExtendedPath(string path)
    {
        // \\.\pipe\..., \\?\C:\..., \\.\C:\..., //?/...
        if (path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("//./", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("//?/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Relative device names like "CON", "NUL", "COM1" (with optional extension).
        var leaf = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(leaf))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(leaf);
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
               || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
               || (stem.Length == 4
                   && stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   && char.IsDigit(stem[3]))
               || (stem.Length == 4
                   && stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)
                   && char.IsDigit(stem[3]));
    }

    private static bool ContainsAlternateDataStream(string path)
    {
        // Drive-letter colon is allowed only as the second character of a rooted Windows path.
        var colonIndex = path.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        if (colonIndex == 1 && path.Length >= 2 && char.IsLetter(path[0]))
        {
            // "C:\..." or "C:relative" — the latter can escape the cwd; treat non-\ form as ADS-like abuse.
            return path.Length < 3
                   || (path[2] != '\\' && path[2] != '/');
        }

        return true;
    }
}
