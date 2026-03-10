using System.Text;
using System.Text.RegularExpressions;

namespace ModService.Core.Matching;

public static partial class GlobPattern
{
    public static bool IsMatch(string pattern, string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentNullException.ThrowIfNull(input);

        var regex = BuildRegex(pattern);
        return regex.IsMatch(input);
    }

    private static Regex BuildRegex(string pattern)
    {
        var builder = new StringBuilder();
        builder.Append('^');

        foreach (var character in pattern)
        {
            builder.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString())
            });
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
