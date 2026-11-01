using System.Text;

namespace AtlassianCli.Output;

public static class JiraWikiMarkdownConverter
{
    public static string Convert(string wiki)
    {
        // Jira wiki syntax is not HTML. Keep complex markup verbatim instead of claiming a lossless conversion.
        var result = new StringBuilder();
        foreach (string line in wiki.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length >= 4 && line[0] == 'h' && line[1] is >= '1' and <= '6' && line[2] == '.' && line[3] == ' ')
            {
                result.Append(new string('#', line[1] - '0')).Append(' ').AppendLine(StorageMarkdownConverter.EscapeInline(line[4..]));
            }
            else
            {
                result.AppendLine(StorageMarkdownConverter.EscapeInline(line));
            }
        }

        return result.ToString().TrimEnd();
    }
}
