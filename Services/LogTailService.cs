using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NoBorders; // LogLevel

namespace NoBorders.Services
{
    /// <summary>A single Activity Log row (MIGRATION_PLAN.md Phase 6.4).</summary>
    public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Message);

    /// <summary>
    /// DI-registered singleton that tails <c>AppLogger.LogPath</c> (noborders.log)
    /// for the Razor Activity Log view (screen 4a) — net-new functionality, since
    /// the log file itself was the only pre-existing piece (Phase 6.4).
    ///
    /// Polls on a <see cref="System.Threading.Timer"/> (750ms) rather than
    /// <see cref="FileSystemWatcher"/>: this app already leans on plain WinForms
    /// timers everywhere (EnforceTimer, ClipTimer, etc.), and FileSystemWatcher is
    /// known to drop/duplicate events under rapid appends and needs its own error-
    /// recovery path if the watched handle becomes invalid — a poll that reads
    /// only the bytes appended since the last read (tracked via <see cref="_lastLength"/>)
    /// is simpler to reason about and just as responsive at this interval.
    ///
    /// Parsing is deliberately conservative about what it claims to know:
    /// <see cref="AppLogger.Log"/> writes `[timestamp] [LEVEL] message`, where
    /// `message` is otherwise free-form prose (~80 call sites across the app, most
    /// never annotated beyond the default Info level — see AppLogger's doc
    /// comment). "Source" has no real metadata behind it at all; it's recovered
    /// with a best-effort heuristic — text before the first bare `": "` (e.g.
    /// "HotkeyAdd: ..." → source "HotkeyAdd"), or the exception logger's own
    /// "ERROR in {context}: ..." shape — falling back to "NoBorders" rather than
    /// guessing. This is a display convenience, not authoritative data.
    /// </summary>
    public sealed class LogTailService : IDisposable
    {
        // Caps memory for a long-running background app — oldest entries drop
        // first. Comfortably above what a "N lines" footer counter would ever
        // need to show meaningfully in the UI.
        private const int MaxEntries = 5000;

        private static readonly Regex EntryHeader = new(
            @"^\[(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\](?:\s\[(?<lvl>\w+)\])?\s(?<rest>.*)$",
            RegexOptions.Compiled);

        private static readonly Regex SourcePrefix = new(
            @"^([A-Za-z][A-Za-z0-9 ]*): (.*)$", RegexOptions.Compiled);

        private readonly string _path;
        private readonly System.Threading.Timer _timer;
        private readonly object _lock = new();

        private long _lastLength;
        private readonly List<LogEntry> _entries = new();
        private readonly List<LogEntry> _pending = new(); // buffered while Paused

        /// <summary>Raised on the timer thread whenever entries change (new lines
        /// tailed in, paused batch flushed, or the log cleared) — subscribers
        /// (Razor components) already know to marshal via InvokeAsync(StateHasChanged),
        /// same convention as AppStateService.Changed.</summary>
        public event Action? Changed;

        public bool Paused { get; private set; }

        public LogTailService(string path)
        {
            _path = path;
            ReadInitial();
            _timer = new System.Threading.Timer(_ => Poll(), null, 750, 750);
        }

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_lock) return _entries.ToArray(); }
        }

        public int PendingCount
        {
            get { lock (_lock) return _pending.Count; }
        }

        public void TogglePause()
        {
            lock (_lock)
            {
                Paused = !Paused;
                if (!Paused) FlushPendingLocked();
            }
            Changed?.Invoke();
        }

        /// <summary>Truncates the real log file — the footer's "Clear Log" action.
        /// AppLogger keeps appending to the same path afterward with no special
        /// handling needed; the next poll sees length 0 &lt; _lastLength and
        /// resets, same path as external truncation/rotation.</summary>
        public void ClearLog()
        {
            try { File.WriteAllText(_path, string.Empty); } catch { /* best-effort, same as AppLogger.Log's own try/catch */ }
            lock (_lock)
            {
                _entries.Clear();
                _pending.Clear();
                _lastLength = 0;
            }
            Changed?.Invoke();
        }

        private void ReadInitial()
        {
            try
            {
                if (!File.Exists(_path)) { _lastLength = 0; return; }
                string text = File.ReadAllText(_path, Encoding.UTF8);
                _lastLength = new FileInfo(_path).Length;
                ParseAndAppend(text, toPending: false);
            }
            catch { /* first read is best-effort — Poll() will catch up once the file is readable */ }
        }

        private void Poll()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    if (_lastLength != 0) { lock (_lock) { _entries.Clear(); _lastLength = 0; } Changed?.Invoke(); }
                    return;
                }

                long currentLength = new FileInfo(_path).Length;
                if (currentLength == _lastLength) return;

                if (currentLength < _lastLength)
                {
                    // File shrank — cleared or rotated externally. Re-read from
                    // scratch rather than trying to reconcile a diff against
                    // content that no longer exists.
                    lock (_lock) { _entries.Clear(); _pending.Clear(); _lastLength = 0; }
                }

                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(_lastLength, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string newText = reader.ReadToEnd();
                _lastLength = stream.Length;

                bool paused;
                lock (_lock) paused = Paused;
                ParseAndAppend(newText, toPending: paused);
                Changed?.Invoke();
            }
            catch { /* transient I/O (e.g. mid-write) — next tick retries */ }
        }

        private void ParseAndAppend(string text, bool toPending)
        {
            if (string.IsNullOrEmpty(text)) return;

            var lines = text.Split('\n');
            var target = new List<LogEntry>();
            DateTime curTime = default;
            LogLevel curLevel = LogLevel.Info;
            string curSource = "NoBorders";
            StringBuilder? curMessage = null;

            void Flush()
            {
                if (curMessage != null)
                    target.Add(new LogEntry(curTime, curLevel, curSource, curMessage.ToString().TrimEnd('\r')));
            }

            foreach (var rawLine in lines)
            {
                string line = rawLine.TrimEnd('\r');
                if (line.Length == 0 && curMessage == null) continue;

                var m = EntryHeader.Match(line);
                if (m.Success)
                {
                    Flush();
                    curTime = DateTime.TryParse(m.Groups["ts"].Value, out var t) ? t : DateTime.Now;
                    curLevel = Enum.TryParse<LogLevel>(m.Groups["lvl"].Value, ignoreCase: true, out var lvl) ? lvl : LogLevel.Info;
                    string rest = m.Groups["rest"].Value;
                    (curSource, rest) = SplitSource(rest);
                    curMessage = new StringBuilder(rest);
                }
                else if (curMessage != null)
                {
                    // Continuation line (e.g. an exception stack trace) — belongs
                    // to the entry currently being built.
                    curMessage.Append('\n').Append(line);
                }
            }
            Flush();

            if (target.Count == 0) return;

            lock (_lock)
            {
                var into = toPending ? _pending : _entries;
                into.AddRange(target);
                TrimLocked(into);
            }
        }

        private static (string source, string message) SplitSource(string rest)
        {
            const string errorPrefix = "ERROR in ";
            if (rest.StartsWith(errorPrefix, StringComparison.Ordinal))
            {
                int colon = rest.IndexOf(':', errorPrefix.Length);
                if (colon > errorPrefix.Length)
                    return (rest[errorPrefix.Length..colon], rest[(colon + 2)..]);
            }

            var sm = SourcePrefix.Match(rest);
            if (sm.Success) return (sm.Groups[1].Value, sm.Groups[2].Value);

            return ("NoBorders", rest);
        }

        private void FlushPendingLocked()
        {
            if (_pending.Count == 0) return;
            _entries.AddRange(_pending);
            _pending.Clear();
            TrimLocked(_entries);
        }

        private static void TrimLocked(List<LogEntry> list)
        {
            if (list.Count > MaxEntries)
                list.RemoveRange(0, list.Count - MaxEntries);
        }

        public void Dispose() => _timer.Dispose();
    }
}
