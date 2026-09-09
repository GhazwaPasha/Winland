using System;
using System.Collections.Generic;

namespace Winland.Services;

/// <param name="Name">Claude Code's own auto-derived short label (e.g. "winland-e4") — not a real title, just a stable tag; see DisplayTitle for the human one.</param>
/// <param name="DisplayTitle">The real conversation title Claude Desktop shows in its sidebar (e.g. "Vitals tab icon styling"), when one has been generated yet — see ClaudeCodeActivityService for where this actually comes from. Null for a session too new to have one.</param>
/// <param name="LastActivityUtc">When the session's transcript file was last written to — the closest available proxy for "how recently was this session actually doing something", used to badge a row Active vs Idle. Falls back to StartedAtUtc for a session that hasn't produced a transcript yet.</param>
public sealed record ClaudeCodeSessionInfo(string Name, string? DisplayTitle, string ProjectName, DateTime StartedAtUtc, DateTime LastActivityUtc, string Kind);

public sealed record ClaudeCodeActivitySnapshot(IReadOnlyList<ClaudeCodeSessionInfo> Sessions);

/// <summary>
/// Which Claude Code sessions are currently running, and basic detail about
/// each — where, since when, what kind, and (when available) its real
/// title. The process list itself is backed by <c>~/.claude/sessions/*.json</c>
/// — one small file per running Claude Code process, written by Claude Code
/// itself for exactly this kind of external tool (there's a sibling
/// <c>~/.claude/notch/</c> directory that makes the intent fairly explicit).
/// This is what the AI tab's active-session list runs on — a plain process
/// registry with no inferred field semantics for the process-list part,
/// unlike the account-usage-percentage approach it replaced (an unofficial
/// Claude Desktop cache file that turned out unreliable to read). Verified
/// against actually-running process ids rather than trusted at face value,
/// so a stale session file left behind by a crashed process doesn't get
/// counted.
/// </summary>
public interface IClaudeCodeActivityService
{
    ClaudeCodeActivitySnapshot GetSnapshot();
}
