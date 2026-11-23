using System.Text;
using System.Text.RegularExpressions;

namespace Blackbox.Core;

/// <summary>Minimal gitignore-style globs: '*', '?', '**'. A pattern without '/' matches the file name in any directory.</summary>
public static class Glob
{
    public static Regex ToRegex(string glob)
    {
        var sb = new StringBuilder("^");
        if (!glob.Contains('/'))
        {
            sb.Append("(?:.*/)?");
        }
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slash = i + 2 < glob.Length && glob[i + 2] == '/';
                sb.Append(slash ? "(?:.*/)?" : ".*");
                i += slash ? 2 : 1;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}
