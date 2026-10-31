namespace PowerToysExtensions.Core;

public static class Markdown
{
    private static readonly ReverseMarkdown.Converter Converter = CreateConverter();

    /// <summary>Converts HTML (wiki pages, notes, mails) to GitHub-flavored markdown.</summary>
    public static string FromHtml(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Converter.Convert(html);

    /// <summary>Escapes characters that markdown would interpret in a plain text value.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : System.Text.RegularExpressions.Regex.Replace(text, @"([\\`*_{}\[\]()#+\-.!|<>])", @"\$1");

    private static ReverseMarkdown.Converter CreateConverter()
    {
        var config = new ReverseMarkdown.Config { GithubFlavored = true };
        config.Formatting.RemoveComments = true;
        config.Tags.Unknown = ReverseMarkdown.Config.UnknownTagsOption.Bypass;
        return new ReverseMarkdown.Converter(config);
    }
}
