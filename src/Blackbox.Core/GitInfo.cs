namespace Blackbox.Core;

/// <summary>Reads branch and HEAD straight from .git: spawning git costs tens of ms per hook on Windows.</summary>
public static class GitInfo
{
    public static (string? Branch, string? Head) Read(string root)
    {
        try
        {
            var gitDir = FindGitDir(root);
            if (gitDir is null)
            {
                return (null, null);
            }
            var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return (null, head); // detached HEAD
            }
            var refName = head[5..];
            var branch = refName.StartsWith("refs/heads/", StringComparison.Ordinal) ? refName[11..] : refName;

            // Worktrees keep refs in the common dir.
            var commonFile = Path.Combine(gitDir, "commondir");
            var commonDir = File.Exists(commonFile) ? Path.GetFullPath(Path.Combine(gitDir, File.ReadAllText(commonFile).Trim())) : gitDir;
            foreach (var dir in new[] { gitDir, commonDir })
            {
                var refFile = Path.Combine(dir, refName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(refFile))
                {
                    return (branch, File.ReadAllText(refFile).Trim());
                }
            }
            var packed = Path.Combine(commonDir, "packed-refs");
            if (File.Exists(packed))
            {
                var line = File.ReadLines(packed).FirstOrDefault(l => l.EndsWith(" " + refName, StringComparison.Ordinal));
                return (branch, line?.Split(' ')[0]);
            }
            return (branch, null); // unborn branch
        }
        catch (IOException)
        {
            return (null, null);
        }
    }

    private static string? FindGitDir(string root)
    {
        var dot = Path.Combine(root, ".git");
        if (Directory.Exists(dot))
        {
            return dot;
        }
        if (File.Exists(dot))
        {
            var text = File.ReadAllText(dot).Trim();
            if (text.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return Path.GetFullPath(Path.Combine(root, text[7..].Trim()));
            }
        }
        return null;
    }
}
