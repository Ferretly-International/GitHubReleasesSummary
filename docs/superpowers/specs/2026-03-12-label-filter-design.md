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
- A non-empty response is stored as a string and matched using `StringComparison.OrdinalIgnoreCase` against PR label names.
- The output filename format is unchanged regardless of whether a filter is active.

---

## Order of Operations

For each release body, processing happens in this order:

1. `StripContributorsSections()` — applied first, as today (removes contributor headings, "Full Changelog" lines, collapses excess blank lines)
2. Label filtering — applied to the stripped body only when `labelFilter` is non-null

PR line detection and label filtering operate on the **already-stripped** body. Any lines removed by `StripContributorsSections` are not considered for PR detection or release-omission decisions.

---

## PR Detection

The stripped body is split into individual lines by normalizing `\r\n` to `\n` and splitting on `\n`. Each line is classified independently.

A line is classified as a **PR line** if it matches either pattern:

| Pattern | Regex | Example |
|---|---|---|
| Full GitHub pull URL | `https://github\.com/<owner>/<repo>/pull/(\d+)` | `...in https://github.com/owner/repo/pull/123` |
| Short ref | `(?<!\w)#(\d+)(?!\w)` | `...fixes #123` |

Notes:
- `<owner>` and `<repo>` in the URL pattern must be escaped with `Regex.Escape()` before interpolation, since repo names can contain regex metacharacters (e.g., dots).
- The short-ref negative lookbehind/lookahead ensures `#123` embedded in a word (e.g., `issue#123abc`) does not match. It does match `#123` preceded by whitespace, punctuation, or start-of-line.
- Short refs are assumed to refer to the current run's `owner`/`repo`. References to other repos cannot be detected and will be looked up in the current repo — this is a known limitation acceptable for the tool's use case.
- A single line may reference multiple PR numbers (e.g., `fixes #456, see https://github.com/owner/repo/pull/789`). All distinct PR numbers on the line are extracted and checked. The line is kept if **any** of those PR numbers passes the label filter.
- When a PR number appears via both patterns on the same line, it is looked up only once.

Non-PR lines (section headers, blank lines, plain text) are never filtered — they always pass through unchanged.

---

## Label Lookup

The PR label cache is a `Dictionary<int, IReadOnlyList<Label>>` initialized once for the entire run (shared across all releases, scoped to the single repo being processed).

When a label filter is active, each PR number extracted from a PR line is processed as follows:

1. Check the cache for that PR number.
2. If not cached, call `github.PullRequest.Get(owner, repo, number)`, extract the `.Labels` collection from the returned `PullRequest` object, and store that label list in the cache keyed by PR number. The progress counter increments once per API call made (not per line, not per cache hit).
3. The line is kept if any label's `Name` matches `labelFilter` using `StringComparison.OrdinalIgnoreCase`.

**Classification rules for edge cases:**

- **`NotFoundException`** — the reference was an issue, not a PR. Cache the number with an empty label list (to avoid repeat API calls). The counter still increments. On subsequent lines referencing the same number, it is a cache hit (no further API call, no counter increment) — identical to any other cached result. If **all** PR numbers on a line throw `NotFoundException`, the line is reclassified as a non-PR line (kept as-is, does not count as a true PR line for the release-omission decision). If a line has mixed results (some `NotFoundException`, some valid), it is a true PR line — the valid PR numbers determine keep/drop.
- **`AuthorizationException`** — fatal. Display a specific message indicating the token lacks permission to read pull requests (fine-grained tokens may have repo access but not PR access). Do not write any output file, and exit.
- **`RateLimitExceededException`** — fatal. Display an error message including how many releases were processed before the abort. Do not write any output file, and exit.
- **Any other exception** (including `HttpRequestException`, timeouts, or other Octokit/network failures) — fatal. Re-throw with a descriptive message (same pattern as existing `AuthorizationException` / `NotFoundException` handling in the fetch step). No output file is written.

---

## Release Filtering

When `labelFilter` is non-null, the following steps are applied to each release body after `StripContributorsSections()`:

Processing is done in a **single pass** over the lines:

1. Split into lines (CRLF normalized to LF).
2. For each line, apply PR detection. If PR refs are found, make API calls as needed to resolve labels (consulting the cache). After all refs on a line are resolved: if all threw `NotFoundException`, reclassify as a non-PR line (keep it). Otherwise it is a true PR line: keep it if any valid PR number carries the label, drop it if none do.
3. Re-join the remaining lines, then collapse consecutive blank lines (a blank line is one that is empty or contains only whitespace) down to a maximum of one blank line, and remove all leading and trailing blank lines so the result starts and ends with non-blank content (or is empty). This step always runs (dropping PR lines can create new consecutive blanks or leading/trailing blanks that `StripContributorsSections` did not previously see).
4. Check whether the output of step 3 is entirely blank or whitespace-only.

Release-omission decision:

| Condition | Result |
|---|---|
| No label filter active | Release included as-is (current behavior) |
| Label filter active, no true PR lines existed (only non-PR lines, or all refs threw `NotFoundException`) | Release included with body from step 3 |
| Label filter active, ≥1 true PR line matched | Release included with filtered body from step 3 |
| Label filter active, true PR lines existed but 0 matched | Release omitted |
| Label filter active, filtered body from step 3 is entirely blank | Release omitted (covers the case where matched PR lines plus their surrounding context collapse to nothing) |

**Clarification:** A release where all `#NNN` references resolved to issues (all `NotFoundException`) has no true PR lines and is included as-is — the label filter has nothing to act on. A release where true PR lines existed but none carried the label is omitted, even if non-PR lines (headers, plain text) would remain in the body.

The summary header block (`> Releases: N`) reflects only the count of releases actually written to output.

---

## Progress Display

The existing Spectre.Console `Status` context is used for both phases. The spinner message is updated in-place within the same `Status` call:

1. **Phase 1:** `"Fetching releases from {owner}/{repo}..."` — existing behavior
2. **Phase 2:** `"Checking PR labels... ({n} refs checked)"` — active only when a label filter is set. `n` is the total number of GitHub API calls made (one per unique uncached PR number, including `NotFoundException` calls). Cache hits and fatal-error aborts do not increment `n`.

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
- Retry logic or delay handling for GitHub secondary rate limits (Octokit's default behavior is relied upon)
- Handling PRs with more than the default number of labels returned by the GitHub API (label truncation is not a concern in practice)
