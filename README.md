# GitHub Releases Summary

A .NET 8 console application that fetches GitHub releases for a repository within a date range and collates them into a single Markdown file. Contributor sections are automatically stripped from each release's notes.

## Features

- Interactive ANSI console UI powered by [Spectre.Console](https://spectreconsole.net/)
- Fetches releases via the GitHub API using [Octokit.net](https://github.com/octokit/octokit.net)
- Filters releases by a user-supplied date range
- Optional label filter: limits each release to only lines referencing PRs that carry a specific GitHub label; releases with no matching PRs are omitted
- Strips `## New Contributors`, `## Contributors`, and `**Full Changelog**` footers from each release body
- Outputs a clean, single Markdown file with a table-of-contents-friendly heading per release
- Optional GitHub Personal Access Token for private repos and higher API rate limits

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

## Setup

1. Copy `appsettings.example.json` to `appsettings.json`:

   ```bash
   cp appsettings.example.json appsettings.json
   ```

2. Edit `appsettings.json` and fill in your settings:

   ```json
   {
     "GitHub": {
       "PersonalAccessToken": "ghp_YourTokenHere",
       "DefaultOutputDirectory": "C:/Users/YourName/Documents"
     }
   }
   ```

   | Setting | Required | Description |
   |---|---|---|
   | `PersonalAccessToken` | No* | GitHub PAT. Needed for private repos; increases rate limit from 60 to 5,000 req/hr for public repos. |
   | `DefaultOutputDirectory` | No | Pre-populates the output file path prompt. Defaults to the current working directory. |

   *For public repos with light usage you can leave the token empty.

## Running

```bash
dotnet run
```

You will be prompted for:

| Prompt | Example |
|---|---|
| GitHub repository | `dotnet/aspnetcore` |
| Start date | `2024-01-01` |
| End date | `2024-12-31` |
| Output file path | _(pre-filled, press Enter to accept)_ |
| Label filter | `Release Notes` _(leave blank to include all PRs)_ |

The generated Markdown file will be written to the path you confirm. The label filter is optional — pressing Enter without typing skips filtering entirely.

## Creating a GitHub Personal Access Token

1. Go to **GitHub → Settings → Developer settings → Personal access tokens → Fine-grained tokens**
2. Click **Generate new token**
3. Select the appropriate "Resource owner"
4. Under **Repository permissions**, grant:
   - **Contents** — Read-only (required for private repos; not needed for public repos)
   - **Pull requests** — Read-only (required when using the label filter)
5. Copy the token into `appsettings.json`

> **Note:** Classic PATs with the `repo` scope also work. If you see an "Access denied" error during label lookup, your fine-grained token is missing the **Pull requests** read permission.

## Output Format

```markdown
# owner/repo — Release Notes

> Generated: 2024-03-11 09:00
> Period: 2024-01-01 to 2024-12-31
> Releases: 5

---

## [v1.2.0](https://github.com/owner/repo/releases/tag/v1.2.0)

**Tag:** `v1.2.0` | **Published:** 2024-06-15

...release notes (contributors section removed)...

---
```

## Notes

- The app fetches up to 100 releases per page. Repositories with more than 100 releases will return only the most recent 100 from the API; all returned releases are then filtered by date.
- `appsettings.json` is excluded from version control via `.gitignore` to avoid committing tokens. Use `appsettings.example.json` as the reference template.
