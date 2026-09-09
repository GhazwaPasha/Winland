using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace Winland.Services;

/// <summary>See <see cref="IClaudeCodeActivityService"/>.</summary>
public sealed class ClaudeCodeActivityService : IClaudeCodeActivityService
{
    // A real title is set (by Claude Desktop, asynchronously, after the
    // first exchange) as one small standalone "custom-title" line near the
    // start of the transcript — these files can run into the megabytes for
    // a long-running session, so this caps how much of one gets read every
    // 30-second poll rather than scanning the whole thing each time.
    private const int MaxTitleScanLines = 300;

    private readonly string _sessionsDirectory;
    private readonly string _projectsDirectory;

    public ClaudeCodeActivityService()
    {
        var claudeHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _sessionsDirectory = Path.Combine(claudeHome, "sessions");
        _projectsDirectory = Path.Combine(claudeHome, "projects");
    }

    public ClaudeCodeActivitySnapshot GetSnapshot()
    {
        try
        {
            if (!Directory.Exists(_sessionsDirectory))
            {
                return new ClaudeCodeActivitySnapshot(Array.Empty<ClaudeCodeSessionInfo>());
            }

            var sessions = new List<ClaudeCodeSessionInfo>();

            // The ".key" files sitting next to each "<pid>.json" here are
            // session/auth key material — Directory.GetFiles with this
            // filter never touches them, only the plain descriptor.
            foreach (var path in Directory.GetFiles(_sessionsDirectory, "*.json"))
            {
                var descriptor = TryReadDescriptor(path);
                if (descriptor is null || !IsProcessRunning(descriptor.Pid))
                {
                    continue; // exited process — its session file just hasn't been cleaned up yet
                }

                var startedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(descriptor.StartedAt).UtcDateTime;
                var transcript = ReadTranscriptInfo(descriptor.Cwd, descriptor.SessionId);

                sessions.Add(new ClaudeCodeSessionInfo(
                    Name: string.IsNullOrWhiteSpace(descriptor.Name) ? $"pid-{descriptor.Pid}" : descriptor.Name,
                    DisplayTitle: transcript.Title,
                    ProjectName: ProjectNameFromPath(descriptor.Cwd) ?? "Unknown project",
                    StartedAtUtc: startedAtUtc,
                    LastActivityUtc: transcript.LastActivityUtc ?? startedAtUtc,
                    Kind: string.IsNullOrWhiteSpace(descriptor.Kind) ? "interactive" : descriptor.Kind));
            }

            return new ClaudeCodeActivitySnapshot(sessions);
        }
        catch
        {
            return new ClaudeCodeActivitySnapshot(Array.Empty<ClaudeCodeSessionInfo>());
        }
    }

    private static SessionDescriptor? TryReadDescriptor(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SessionDescriptor>(json);
        }
        catch
        {
            // Mid-write, corrupt, or a future format change — skip just this one file.
            return null;
        }
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // no process with this id — the session file outlived it
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Two things off the session's transcript file
    /// (<c>~/.claude/projects/&lt;slug&gt;/&lt;sessionId&gt;.jsonl</c> — a
    /// *different* file, and a different on-disk location entirely, from
    /// the pid-keyed descriptor this class otherwise reads;
    /// <see cref="ToProjectSlug"/> is Claude Code's own folder-naming
    /// convention for it) in one file touch rather than two:
    ///
    /// <list type="bullet">
    /// <item><b>Title</b> — the real title Claude Desktop's own sidebar
    /// shows (e.g. "Vitals tab icon styling"), a small standalone
    /// "custom-title" line near the start of the file.</item>
    /// <item><b>LastActivityUtc</b> — the file's own last-write time. The
    /// transcript is appended to in real time as a session actually does
    /// anything (a message, a tool call, ...), so "how long since this
    /// file last changed" is a solid proxy for "how long since this session
    /// was last active" — used to badge a row Active vs Idle. There's no
    /// live push notification for this anywhere; polling the file's own
    /// metadata every 30s is the simplest thing that works.</item>
    /// </list>
    ///
    /// Best effort throughout: a missing file, no title yet, or anything
    /// else just means the corresponding value comes back null.
    /// </summary>
    private (string? Title, DateTime? LastActivityUtc) ReadTranscriptInfo(string? cwd, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(cwd) || string.IsNullOrWhiteSpace(sessionId))
        {
            return (null, null);
        }

        try
        {
            var transcriptPath = Path.Combine(_projectsDirectory, ToProjectSlug(cwd), $"{sessionId}.jsonl");
            if (!File.Exists(transcriptPath))
            {
                return (null, null);
            }

            var lastActivityUtc = File.GetLastWriteTimeUtc(transcriptPath);

            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            string? title = null;
            for (var i = 0; i < MaxTitleScanLines; i++)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                if (!line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<CustomTitleEntry>(line);
                    if (!string.IsNullOrWhiteSpace(entry?.CustomTitle))
                    {
                        title = entry.CustomTitle; // keep scanning — a later rename should win over an earlier one
                    }
                }
                catch
                {
                    // Malformed line — skip just this one.
                }
            }

            return (title, lastActivityUtc);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Claude Code's own transcript-folder naming: every ':', '\' and '/' in the path becomes '-'.</summary>
    private static string ToProjectSlug(string cwd)
    {
        var chars = cwd.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is ':' or '\\' or '/')
            {
                chars[i] = '-';
            }
        }

        return new string(chars);
    }

    private static string? ProjectNameFromPath(string? cwd) =>
        string.IsNullOrWhiteSpace(cwd) ? null : Path.GetFileName(cwd.TrimEnd('\\', '/'));

    private sealed record SessionDescriptor
    {
        [JsonPropertyName("pid")]
        public int Pid { get; init; }

        [JsonPropertyName("sessionId")]
        public string? SessionId { get; init; }

        [JsonPropertyName("cwd")]
        public string? Cwd { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("startedAt")]
        public long StartedAt { get; init; }

        [JsonPropertyName("kind")]
        public string? Kind { get; init; }
    }

    private sealed record CustomTitleEntry
    {
        [JsonPropertyName("customTitle")]
        public string? CustomTitle { get; init; }
    }
}
