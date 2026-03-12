# Label Filter for PR Lines — Design Spec

**Date:** 2026-03-12
**Feature:** Optional label-based filtering of PR lines within GitHub release notes

---

## Overview

Add an optional runtime prompt that lets the user specify a GitHub label name. When provided, only PR lines referencing a pull request that carries that label are kept in each release's body. Releases with no matching PR lines are omitted entirely from the output. When no label is entered, the tool behaves exactly as today.

---

## User Input

A new optional prompt is added after the existing output path prompt:

```
Label filter (leave blank to include all PRs):
```

- Input is trimmed. An empty response sets `labelFilter = null` (no filtering).
- A non-empty response is stored as a string and matched case-insensitively against PR label names.
- The output filename format is unchanged regardless of whether a filter is active.

---

## PR Detection

A line in a release body is classified as a **PR line** if it matches either pattern:

| Pattern | Example |
|---|---|
| Full GitHub pull URL | `https://github.com/{owner}/{repo}/pull/123` |
| Short ref | `#123` |

For short refs, `owner` and `repo` from the current run are assumed.

Non-PR lines (section headers, blank lines, plain text) are never filtered — they always pass through unchanged.

---

## Label Lookup

When a label filter is active, each PR line is processed as follows:

1. Extract the PR number from the matched pattern.
2. Check an in-memory cache (`Dictionary<int, IReadOnlyList<Label>>`).
3. If not cached, call `github.PullRequest.Get(owner, repo, number)` and store the result in the cache.
4. Keep the line if any label's `Name` matches `labelFilter` (case-insensitive); otherwise drop it.

**Edge case:** `#123`-style references may point to issues rather than PRs. If `PullRequest.Get()` throws `NotFoundException`, the line is treated as a non-PR line and kept as-is.

---

## Release Filtering

After applying line-level filtering to a release body:

| Condition | Result |
|---|---|
| No label filter active | Release included as-is (current behavior) |
| Label filter active, ≥1 PR line matched | Release included with filtered body |
| Label filter active, 0 PR lines matched | Entire release section omitted |

The summary header block (`> Releases: N`) reflects only the count of releases actually written to output.

---

## Progress Display

The existing Spectre.Console spinner is extended to a two-phase status:

1. **"Fetching releases from {owner}/{repo}..."** — existing behavior, unchanged
2. **"Checking PR labels... ({n} of {total})"** — shown while making `PullRequest.Get()` calls when a label filter is active

---

## What Does Not Change

- `appsettings.json` schema — no new config keys
- Output filename format
- `StripContributorsSections` logic
- All existing prompts and their validation

---

## Out of Scope

- Filtering by multiple labels simultaneously
- Storing the label filter as a config default
- Modifying the filename to reflect the active filter
