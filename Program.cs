using Microsoft.Extensions.Configuration;
using Octokit;
using Spectre.Console;
using System.Text;
using System.Text.RegularExpressions;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var token = config["GitHub:PersonalAccessToken"];
var defaultOutputDir = config["GitHub:DefaultOutputDirectory"];

AnsiConsole.Write(new FigletText("GitHub Releases").Color(Color.Blue));
AnsiConsole.Write(new Rule("[grey]Summary Generator[/]").RuleStyle("grey"));
AnsiConsole.WriteLine();

// Prompt for repository
var repoInput = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]GitHub repository[/] [grey](owner/repo)[/]:")
        .Validate(input =>
        {
            var parts = input.Trim().Split('/');
            return parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1])
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]Please enter a valid repository in the format owner/repo[/]");
        }));

var repoParts = repoInput.Trim().Split('/');
var owner = repoParts[0].Trim();
var repo = repoParts[1].Trim();

// Prompt for start date
var startDate = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Start date[/] [grey](yyyy-MM-dd)[/]:")
        .Validate(input => DateTime.TryParseExact(input.Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _)
            ? ValidationResult.Success()
            : ValidationResult.Error("[red]Please enter a valid date in yyyy-MM-dd format[/]")));

// Prompt for end date
var endDate = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]End date[/] [grey](yyyy-MM-dd)[/]:")
        .Validate(input =>
        {
            if (!DateTime.TryParseExact(input.Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var end))
                return ValidationResult.Error("[red]Please enter a valid date in yyyy-MM-dd format[/]");

            var start = DateTime.ParseExact(startDate.Trim(), "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture);

            return end >= start
                ? ValidationResult.Success()
                : ValidationResult.Error("[red]End date must be on or after the start date[/]");
        }));

var rangeStart = DateTime.ParseExact(startDate.Trim(), "yyyy-MM-dd",
    System.Globalization.CultureInfo.InvariantCulture);
var rangeEnd = DateTime.ParseExact(endDate.Trim(), "yyyy-MM-dd",
    System.Globalization.CultureInfo.InvariantCulture).AddDays(1).AddTicks(-1);

// Prompt for output path
var defaultFileName = $"{owner}-{repo}-releases-{startDate}-to-{endDate}.md"
    .Replace('/', '-');
var defaultPath = string.IsNullOrWhiteSpace(defaultOutputDir)
    ? Path.Combine(Directory.GetCurrentDirectory(), defaultFileName)
    : Path.Combine(defaultOutputDir, defaultFileName);

var outputPath = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Output file path[/]:")
        .DefaultValue(defaultPath)
        .Validate(input =>
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(input.Trim()));
                return dir != null && Directory.Exists(dir)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]Directory does not exist[/]");
            }
            catch
            {
                return ValidationResult.Error("[red]Invalid file path[/]");
            }
        }));

// Prompt for label filter (optional)
var labelFilterInput = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Label filter[/] [grey](leave blank to include all PRs)[/]:")
        .AllowEmpty());
var labelFilter = string.IsNullOrWhiteSpace(labelFilterInput) ? null : labelFilterInput.Trim();

AnsiConsole.WriteLine();

// Fetch releases and process bodies
IReadOnlyList<Release> releases = [];
List<(Release release, string? body)> processedReleases = [];
int releasesProcessed = 0;         // count of releases the Phase 2 loop has visited
bool labelFilterRemovedReleases = false; // true if at least one release was omitted by the label filter

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .SpinnerStyle(Style.Parse("blue"))
    .StartAsync($"Fetching releases from [blue]{owner}/{repo}[/]...", async ctx =>
    {
        var github = BuildGitHubClient(token);

        try
        {
            var apiOptions = new ApiOptions { PageSize = 100 };
            var allReleases = await github.Repository.Release.GetAll(owner, repo, apiOptions);

            releases = allReleases
                .Where(r => r.PublishedAt.HasValue &&
                            r.PublishedAt.Value.UtcDateTime >= rangeStart.ToUniversalTime() &&
                            r.PublishedAt.Value.UtcDateTime <= rangeEnd.ToUniversalTime())
                .OrderByDescending(r => r.PublishedAt)
                .ToList();

            ctx.Status($"Found [green]{releases.Count}[/] release(s) in range.");

            foreach (var release in releases)
            {
                releasesProcessed++;
                var body = !string.IsNullOrWhiteSpace(release.Body)
                    ? StripContributorsSections(release.Body)
                    : null;
                processedReleases.Add((release, body));
            }
        }
        catch (AuthorizationException)
        {
            AnsiConsole.MarkupLine("[red]Authentication failed.[/] Check your PersonalAccessToken in appsettings.json.");
            throw;
        }
        catch (NotFoundException)
        {
            AnsiConsole.MarkupLine($"[red]Repository not found:[/] {owner}/{repo}");
            throw;
        }
    });

if (processedReleases.Count == 0)
{
    var reason = labelFilterRemovedReleases
        ? $"[yellow]No releases matched the label filter \"{labelFilter}\".[/]"
        : $"[yellow]No releases found between {startDate} and {endDate}.[/]";
    AnsiConsole.MarkupLine(reason);
    return;
}

AnsiConsole.MarkupLine($"Found [green]{processedReleases.Count}[/] release(s). Generating markdown...");
AnsiConsole.WriteLine();

// Build markdown
var sb = new StringBuilder();
sb.AppendLine($"# {owner}/{repo} — Release Notes");
sb.AppendLine();
sb.AppendLine($"> Generated: {DateTime.Now:yyyy-MM-dd HH:mm}  ");
sb.AppendLine($"> Period: {startDate} to {endDate}  ");
sb.AppendLine($"> Releases: {processedReleases.Count}");
sb.AppendLine();
sb.AppendLine("---");
sb.AppendLine();

foreach (var (release, releaseBody) in processedReleases)
{
    var publishedOn = release.PublishedAt?.LocalDateTime.ToString("yyyy-MM-dd") ?? "unknown";
    var tagLabel = release.TagName;
    var releaseUrl = release.HtmlUrl;

    sb.AppendLine($"## [{release.Name ?? tagLabel}]({releaseUrl})");
    sb.AppendLine();
    sb.AppendLine($"**Tag:** `{tagLabel}` | **Published:** {publishedOn}" +
                  (release.Prerelease ? " | **Pre-release**" : ""));
    sb.AppendLine();

    if (!string.IsNullOrWhiteSpace(releaseBody))
    {
        sb.AppendLine(releaseBody.Trim());
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("_No release notes provided._");
        sb.AppendLine();
    }

    sb.AppendLine("---");
    sb.AppendLine();
}

File.WriteAllText(outputPath.Trim(), sb.ToString(), Encoding.UTF8);

AnsiConsole.Write(new Rule("[green]Done[/]").RuleStyle("green"));
AnsiConsole.MarkupLine($"Markdown saved to: [blue]{outputPath.Trim()}[/]");

// -------------------------------------------------------------------------

static GitHubClient BuildGitHubClient(string? token)
{
    var client = new GitHubClient(new ProductHeaderValue("GitHubReleasesSummary"));
    if (!string.IsNullOrWhiteSpace(token))
        client.Credentials = new Credentials(token);
    return client;
}

/// <summary>
/// Removes contributor-related sections from GitHub release body markdown.
/// Strips:  "## New Contributors", "### New Contributors",
///          "## Contributors",     "### Contributors",
///          and the "**Full Changelog**: ..." footer line.
/// </summary>
static string StripContributorsSections(string body)
{
    // Remove contributor heading sections and all content under them
    // until the next heading of same or higher level (or end of string)
    body = Regex.Replace(
        body,
        @"(?m)^#{1,3} (?:New Contributors|Contributors)\s*\r?\n(?:(?!^#{1,3} ).*\r?\n?)*",
        string.Empty,
        RegexOptions.IgnoreCase);

    // Remove "**Full Changelog**: ..." lines
    body = Regex.Replace(
        body,
        @"(?m)^\*\*Full Changelog\*\*:.*$\r?\n?",
        string.Empty,
        RegexOptions.IgnoreCase);

    // Collapse 3+ consecutive blank lines down to 2
    body = Regex.Replace(body, @"(\r?\n){3,}", "\n\n");

    return body;
}

/// <summary>
/// Extracts all distinct PR numbers referenced on a single line.
/// Checks the full GitHub pull URL pattern first, then the short #NNN pattern.
/// Returns an empty set if no PR references are found.
/// </summary>
static HashSet<int> ExtractPrNumbers(string line, Regex urlPattern, Regex shortRefPattern)
{
    var numbers = new HashSet<int>();

    foreach (Match m in urlPattern.Matches(line))
        numbers.Add(int.Parse(m.Groups[1].Value));

    foreach (Match m in shortRefPattern.Matches(line))
        numbers.Add(int.Parse(m.Groups[1].Value));

    return numbers;
}
