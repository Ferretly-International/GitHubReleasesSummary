# Label Filter Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an optional PR label filter that limits each release's body to only lines referencing PRs with a given label, omitting releases where no PRs match.

**Architecture:** All changes are confined to `Program.cs`. The spinner block gains a Phase 2 that strips and filters release bodies, producing a `processedReleases` list of `(Release, string? body)` tuples. The markdown builder iterates that list instead of the raw `releases` collection. A new static `FilterBodyByLabelAsync` method encapsulates all PR detection, label lookup (with per-run caching), and body post-processing. A companion static `ExtractPrNumbers` method handles regex extraction.

**Tech Stack:** .NET 8, Octokit (`github.PullRequest.Get`), Spectre.Console, `System.Text.RegularExpressions`

---

## Chunk 1: Prompt + Scaffolding

### Task 1: Add the optional label filter prompt

**Files:**
- Modify: `Program.cs` — insert after the output path prompt block (currently ends around line 89)

- [ ] **Step 1: Add the label prompt**

After `AnsiConsole.WriteLine();` (the blank line after the output path prompt, line 91), insert:

```csharp
// Prompt for label filter (optional)
var labelFilterInput = AnsiConsole.Prompt(
    new TextPrompt<string>("[green]Label filter[/] [grey](leave blank to include all PRs)[/]:")
        .AllowEmpty());
var labelFilter = string.IsNullOrWhiteSpace(labelFilterInput) ? null : labelFilterInput.Trim();

AnsiConsole.WriteLine();
```

Remove the existing `AnsiConsole.WriteLine();` on line 91 so there is only one blank line before the spinner.

- [ ] **Step 2: Verify the prompt renders**

```bash
dotnet run
```

Enter a repo, dates, and output path as normal. Confirm the new `Label filter` prompt appears. Press Enter without typing to verify the app continues normally (no crash, no filtering).

- [ ] **Step 3: Commit**

```bash
git add Program.cs
git commit -m "feat: add optional label filter prompt"
```

---

### Task 2: Introduce processedReleases and restructure the spinner output

**Files:**
- Modify: `Program.cs` — around the `IReadOnlyList<Release> releases = [];` declaration and the post-spinner early-exit block

The current flow populates `releases` inside the spinner and then builds markdown outside. We need a parallel `processedReleases` list that holds `(Release release, string? body)` so Phase 2 can pass processed bodies to the markdown builder. A separate `releasesProcessed` int tracks how many releases the Phase 2 loop has iterated over (needed for the rate-limit error message).

- [ ] **Step 1: Add the processedReleases and releasesProcessed declarations**

Replace:

```csharp
// Fetch releases
IReadOnlyList<Release> releases = [];
```

With:

```csharp
// Fetch releases and process bodies
IReadOnlyList<Release> releases = [];
List<(Release release, string? body)> processedReleases = [];
int releasesProcessed = 0;         // count of releases the Phase 2 loop has visited
bool labelFilterRemovedReleases = false; // true if at least one release was omitted by the label filter
```

- [ ] **Step 2: Populate processedReleases at the end of the existing spinner lambda**

Inside the spinner's `StartAsync` lambda, after the existing `releases = allReleases...ToList();` and `ctx.Status(...)` lines (but still inside the lambda), add a loop that populates `processedReleases` for the no-filter case. This loop will be replaced in Task 5 with the full Phase 2 implementation.

At the bottom of the spinner lambda, after `ctx.Status($"Found [green]{releases.Count}[/] release(s) in range.");`, add:

```csharp
foreach (var release in releases)
{
    releasesProcessed++;
    var body = !string.IsNullOrWhiteSpace(release.Body)
        ? StripContributorsSections(release.Body)
        : null;
    processedReleases.Add((release, body));
}
```

- [ ] **Step 3: Update the post-spinner early-exit check**

Replace:

```csharp
if (releases.Count == 0)
{
    AnsiConsole.MarkupLine($"[yellow]No releases found between {startDate} and {endDate}.[/]");
    return;
}

AnsiConsole.MarkupLine($"Found [green]{releases.Count}[/] release(s). Generating markdown...");
AnsiConsole.WriteLine();
```

With:

```csharp
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
```

- [ ] **Step 4: Update the markdown builder to iterate processedReleases**

Replace the entire `foreach (var release in releases)` loop with:

```csharp
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
```

Note: `StripContributorsSections` is no longer called here — it was moved into the `processedReleases` population loop in Step 2.

- [ ] **Step 5: Update the releases count in the summary header**

The summary header block uses `releases.Count`. Update it to use `processedReleases.Count`:

Replace:
```csharp
sb.AppendLine($"> Releases: {releases.Count}");
```

With:
```csharp
sb.AppendLine($"> Releases: {processedReleases.Count}");
```

- [ ] **Step 6: Build and verify unchanged behavior**

```bash
dotnet run
```

Run with an empty label filter (press Enter). Output should be identical to before this refactor. Verify the markdown file contains the same releases and bodies as before.

- [ ] **Step 7: Commit**

```bash
git add Program.cs
git commit -m "refactor: route release bodies through processedReleases list"
```

---

## Chunk 2: PR Detection and Label Filtering

### Task 3: Add ExtractPrNumbers helper

**Files:**
- Modify: `Program.cs` — add a new static method after `StripContributorsSections`

`ExtractPrNumbers` takes a single line and both compiled regex patterns, and returns the set of distinct PR numbers found on that line. Using a `HashSet<int>` ensures a PR number appearing via both URL and short-ref on the same line is only returned once.

- [ ] **Step 1: Add the static method**

After the closing brace of `StripContributorsSections`, add:

```csharp
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
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Program.cs
git commit -m "feat: add ExtractPrNumbers helper for PR line detection"
```

---

### Task 4: Add FilterBodyByLabelAsync

**Files:**
- Modify: `Program.cs` — add a new static async method after `ExtractPrNumbers`

This method performs the single-pass filter described in the spec. It returns a `(string? filteredBody, bool include)` tuple:
- `include = false` means the release should be omitted entirely
- `include = true` with a non-null `filteredBody` means include with the filtered body
- `include = true` with `filteredBody = null` means include with no body (treated as "no release notes")

The method receives:
- `body` — the already-stripped release body
- `owner`, `repo` — for PR lookups
- `labelFilter` — the label name to match (case-insensitive, `OrdinalIgnoreCase`)
- `github` — the authenticated `GitHubClient`
- `prLabelCache` — shared `Dictionary<int, IReadOnlyList<Label>>` across all releases in the run
- `knownIssues` — shared `HashSet<int>` tracking numbers confirmed to be issues (NotFoundException)
- `urlPattern`, `shortRefPattern` — the compiled regexes built by the caller
- `onApiCall` — callback invoked once per actual GitHub API call (used to update the spinner counter)

**Blank-line collapsing** uses a line-by-line approach (not a regex) to correctly handle whitespace-only lines: iterate the result lines, skip any blank line that immediately follows another blank line. A blank line is one where `string.IsNullOrWhiteSpace(line)` is true.

- [ ] **Step 1: Add the static async method**

```csharp
/// <summary>
/// Filters a release body to only PR lines whose PRs carry the specified label.
/// Non-PR lines always pass through. Returns (filteredBody, include):
///   include=false  → release should be omitted
///   include=true   → release should be included (filteredBody may be null/empty)
/// </summary>
static async Task<(string? filteredBody, bool include)> FilterBodyByLabelAsync(
    string body,
    string owner,
    string repo,
    string labelFilter,
    GitHubClient github,
    Dictionary<int, IReadOnlyList<Label>> prLabelCache,
    HashSet<int> knownIssues,
    Regex urlPattern,
    Regex shortRefPattern,
    Action onApiCall)
{
    var lines = body.Replace("\r\n", "\n").Split('\n');
    var resultLines = new List<string>();
    bool hadTruePrLines = false;
    bool anyMatched = false;

    foreach (var line in lines)
    {
        var prNumbers = ExtractPrNumbers(line, urlPattern, shortRefPattern);

        if (prNumbers.Count == 0)
        {
            // Non-PR line — always keep
            resultLines.Add(line);
            continue;
        }

        // Resolve labels for each PR number on this line
        bool hasAnyTruePr = false;
        bool lineMatched = false;

        foreach (var prNum in prNumbers)
        {
            if (knownIssues.Contains(prNum))
                continue; // already confirmed to be an issue, skip

            if (!prLabelCache.TryGetValue(prNum, out var labels))
            {
                try
                {
                    var pr = await github.PullRequest.Get(owner, repo, prNum);
                    labels = pr.Labels;
                    prLabelCache[prNum] = labels;
                    onApiCall();
                }
                catch (NotFoundException)
                {
                    // Reference is an issue, not a PR
                    knownIssues.Add(prNum);
                    onApiCall();
                    continue;
                }
                // AuthorizationException, RateLimitExceededException, and all
                // other exceptions propagate to the caller as fatal errors.
            }

            hasAnyTruePr = true;
            if (labels.Any(l => l.Name.Equals(labelFilter, StringComparison.OrdinalIgnoreCase)))
                lineMatched = true;
        }

        if (!hasAnyTruePr)
        {
            // All refs on this line were issues — reclassify as non-PR line, keep it
            resultLines.Add(line);
            continue;
        }

        // True PR line
        hadTruePrLines = true;
        if (lineMatched)
        {
            anyMatched = true;
            resultLines.Add(line);
        }
        // else: drop the line (PR line that didn't match the label)
    }

    // Determine include/omit before post-processing
    if (hadTruePrLines && !anyMatched)
        return (null, false); // true PR lines existed but none matched → omit

    // Post-process: collapse consecutive blank lines to max 1, trim leading/trailing blanks
    var collapsed = new List<string>();
    bool lastWasBlank = false;
    foreach (var l in resultLines)
    {
        bool isBlank = string.IsNullOrWhiteSpace(l);
        if (isBlank && lastWasBlank) continue; // skip consecutive blank
        collapsed.Add(l);
        lastWasBlank = isBlank;
    }
    var joined = string.Join("\n", collapsed).Trim();

    if (hadTruePrLines && string.IsNullOrWhiteSpace(joined))
        return (null, false); // filtered body is entirely blank → omit

    return (string.IsNullOrWhiteSpace(joined) ? null : joined, true);
}
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Program.cs
git commit -m "feat: add FilterBodyByLabelAsync for per-release body filtering"
```

---

### Task 5: Wire Phase 2 into the spinner block

**Files:**
- Modify: `Program.cs` — inside the `StartAsync` lambda, replace the simple `processedReleases` population loop added in Task 2 with the full Phase 2 implementation.

Note: `AnsiConsole.MarkupLine` calls for fatal errors (AuthorizationException, RateLimitExceededException) are placed inside the spinner lambda — consistent with the existing pattern used for the Phase 1 AuthorizationException and NotFoundException handlers. The spinner stops before the message is displayed because the exception propagates out.

- [ ] **Step 1: Replace the simple loop in the spinner with the full Phase 2 logic**

Inside the spinner lambda, replace:

```csharp
foreach (var release in releases)
{
    releasesProcessed++;
    var body = !string.IsNullOrWhiteSpace(release.Body)
        ? StripContributorsSections(release.Body)
        : null;
    processedReleases.Add((release, body));
}
```

With:

```csharp
// Phase 2: strip and optionally filter release bodies
var prLabelCache = new Dictionary<int, IReadOnlyList<Label>>();
var knownIssues = new HashSet<int>();
int prCheckedCount = 0;

Regex? urlPattern = null;
Regex? shortRefPattern = null;
if (labelFilter != null)
{
    urlPattern = new Regex(
        $@"https://github\.com/{Regex.Escape(owner)}/{Regex.Escape(repo)}/pull/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    shortRefPattern = new Regex(@"(?<!\w)#(\d+)(?!\w)", RegexOptions.Compiled);
    ctx.Status("Checking PR labels... (0 refs checked)");
}

try
{
    foreach (var release in releases)
    {
        releasesProcessed++;
        string? body = !string.IsNullOrWhiteSpace(release.Body)
            ? StripContributorsSections(release.Body)
            : null;

        if (labelFilter != null && body != null)
        {
            var (filteredBody, include) = await FilterBodyByLabelAsync(
                body, owner, repo, labelFilter,
                github, prLabelCache, knownIssues,
                urlPattern!, shortRefPattern!,
                () =>
                {
                    prCheckedCount++;
                    ctx.Status($"Checking PR labels... ({prCheckedCount} refs checked)");
                });

            if (!include) { labelFilterRemovedReleases = true; continue; }
            body = filteredBody;
        }

        processedReleases.Add((release, body));
    }
}
catch (AuthorizationException)
{
    AnsiConsole.MarkupLine("[red]Authorization failed during PR label lookup.[/] " +
        "Your token may lack pull request read permission (check fine-grained token scopes).");
    throw;
}
catch (RateLimitExceededException)
{
    AnsiConsole.MarkupLine($"[red]GitHub rate limit exceeded[/] after processing " +
        $"[yellow]{releasesProcessed}[/] of {releases.Count} release(s). No output file was written.");
    throw;
}
```

- [ ] **Step 2: Build to confirm no compile errors**

```bash
dotnet build
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Program.cs
git commit -m "feat: wire Phase 2 label filtering into spinner block"
```

---

## Chunk 3: Manual Verification

### Task 6: End-to-end manual testing

No automated test project exists in this repo. Verify correctness through targeted manual runs.

**Test A — No filter (regression check)**

- [ ] Run the app against any public repo with releases. Leave the label filter blank.
- [ ] Confirm: output is identical in structure to before this change. All releases appear, bodies are stripped of contributor sections as before.

**Test B — Label filter with matching PRs**

- [ ] Run against a repo where you know at least one release body contains a PR reference (URL or `#NNN` style) and that PR has a known label (e.g., `"bug"` or your chosen label).
- [ ] Enter that label name at the filter prompt.
- [ ] Confirm: only PR lines whose PRs carry that label appear in the output. Non-PR lines (headings, blank lines) are kept. Releases with no matching PRs are absent from the output. The `> Releases: N` count in the header matches the number of release sections in the file.

**Test C — Label filter with no matches**

- [ ] Run with a label name that no PR in the release notes carries (e.g., a completely made-up label name).
- [ ] Confirm: the early-exit fires with the message `No releases matched the label filter "..."` and no output file is written.

**Test D — Case-insensitivity**

- [ ] If a label is named `"Release Notes"`, try entering `"release notes"` (all lowercase).
- [ ] Confirm: PRs with that label still appear in the output.

- [ ] **Commit after confirming all tests pass**

```bash
git add Program.cs
git commit -m "feat: add PR label filter with caching and Phase 2 spinner"
```

_(If earlier task commits were already made, this commit may be empty or skipped.)_
