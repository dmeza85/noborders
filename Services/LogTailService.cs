using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NoBorders;

namespace NoBorders.Services
{
    public sealed record LogEntry(long Seq, DateTime Time, LogLevel Level, string Source, string Message);

    public sealed class LogTailService : IDisposable
    {
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
        private long _nextSeq;
        private readonly List<LogEntry> _entries = new();
        private readonly List<LogEntry> _pending = new();

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

        public void ClearLog()
        {
            try { File.WriteAllText(_path, string.Empty); } catch { }
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
            catch { }
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
            catch { }
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
                    target.Add(new LogEntry(System.Threading.Interlocked.Increment(ref _nextSeq), curTime, curLevel, curSource, curMessage.ToString().TrimEnd('\r')));
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
