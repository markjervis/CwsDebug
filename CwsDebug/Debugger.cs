// -----------------------------------------------------------------------------
//  CwsDebug — Self-contained CWS debug log page for Crestron SIMPL#Pro programs.
//
//  Author:   Mark Jervis
//  Company:  Stage Front Presentation Systems
//  Version:  1.2.0
//
//  Builds as a Program Library (Crestron.SimplSharp.SDK.ProgramLibrary) and is
//  referenced by a SIMPL#Pro program. It hosts a small web page on the Crestron
//  Web Server (CWS) that streams the program's own log messages plus, on a
//  hardware appliance, the processor error log — with live search, level
//  filtering, light/dark themes and text export.
// -----------------------------------------------------------------------------

using System.Reflection;            // Assembly.GetCallingAssembly() — host program name
using System.Text;                  // StringBuilder for JSON responses
using System.Text.RegularExpressions; // parsing the console "err" output
using Crestron.SimplSharp;          // CrestronEnvironment, ErrorLog, InitialParametersClass, CTimer
using Crestron.SimplSharp.WebScripting; // HttpCwsServer / IHttpCwsHandler (CWS hosting)
// Crestron's own filesystem types for the persisted log; aliased so they win over System.IO,
// which ImplicitUsings also imports (otherwise File/Directory/Path are ambiguous).
using File         = Crestron.SimplSharp.CrestronIO.File;
using Directory    = Crestron.SimplSharp.CrestronIO.Directory;
using Path         = Crestron.SimplSharp.CrestronIO.Path;
using StreamReader = Crestron.SimplSharp.CrestronIO.StreamReader;
using StreamWriter = Crestron.SimplSharp.CrestronIO.StreamWriter;

namespace CwsDebug
{
    /// <summary>
    /// Self-contained debug log page served via Crestron Web Server (CWS).
    ///
    /// Exposed as a process-wide singleton: the whole public API is static, so any class
    /// in the program can log to it without being handed an instance and without the
    /// classes becoming coupled. The single backing instance is created on first use, so
    /// logging works before Register() is called.
    ///
    /// Usage:
    ///   // InitializeSystem():
    ///   CwsDebugPage.Register("/debugger");
    ///   // anywhere in the program, on any thread:
    ///   CwsDebugPage.LogInfo("Connected to {0}:{1}", host, port);
    ///   // Appliance: http://&lt;processor&gt;/cws/debugger/debugger.html
    ///   // VC-4:      http://&lt;processor&gt;/VirtualControl/Rooms/&lt;roomId&gt;/cws/debugger.html
    ///
    /// The CWS server is released automatically when the program stops or restarts (via
    /// CrestronEnvironment.ProgramStatusEventHandler), with a finalizer as a backstop, so
    /// calling Dispose()/Shutdown() by hand is optional.
    /// </summary>
    public sealed class CwsDebugPage : IHttpCwsHandler, IDisposable
    {
        /// <summary>One immutable log line: severity, text, a formatted timestamp and a sequence id.</summary>
        private readonly struct LogEntry
        {
            public readonly string Level;      // "Info" | "Notice" | "Warning" | "Error" | "Fatal"
            public readonly string Message;    // the formatted message text
            public readonly string Timestamp;  // "yyyy-MM-dd HH:mm:ss.fff"
            public readonly long   Seq;        // monotonic id; lets the page fetch only new entries (0 = N/A)
            public LogEntry(string level, string message, string timestamp, long seq)
            {
                Level = level; Message = message; Timestamp = timestamp; Seq = seq;
            }
        }

        // ── Singleton ────────────────────────────────────────────────────────────────
        // The class must stay an instance because CWS requires an IHttpCwsHandler *object*
        // (HttpRequestHandler = this); a C# static class cannot implement an interface. So we
        // keep one private instance and expose a static API over it, which gives the "use it
        // anywhere, only one of it" behaviour of a static class without the coupling of passing
        // an instance around. _instance is volatile for safe double-checked locking on ARM.
        private static readonly object       _gate = new();   // guards lazy creation of _instance
        private static volatile CwsDebugPage? _instance;      // the single backing instance

        private static CwsDebugPage Instance
        {
            get
            {
                if (_instance != null) return _instance;      // fast path, no lock once created
                lock (_gate) return _instance ??= new CwsDebugPage();
            }
        }

        private HttpCwsServer? _server;                     // null until Register() runs; guards double-registration
        private readonly List<LogEntry> _log  = new();      // in-memory ring of application log entries (capped at MaxEntries)
        private readonly object         _lock = new();      // guards _log against concurrent CWS request threads
        private bool   _disposed;                           // true once the server has been torn down
        private bool   _errorLogAvailable;                  // true only on a hardware appliance (see Register)
        private string _pageTitle = "Debugger";             // header name; resolved per-platform in Register()

        private long      _seq;                             // last sequence id handed out (see LogEntry.Seq)
        private bool      _dirty;                            // _log changed since the last successful disk flush
        private string?   _persistPath;                      // file the log is mirrored to; null until Register()
        private string?   _statePath;                        // file storing the _loggingEnabled flag
        private CTimer?   _flushTimer;                        // periodic disk-flush timer; null until Register()
        private bool      _loggingEnabled = true;            // master record switch; persisted across restarts

        private int _maxEntries = DefaultMaxEntries;        // ring cap; overridable via Register(maxEntries:)

        // Caps and cadences. _maxEntries bounds memory and the JSON payload; the log is a true ring —
        // once full, the oldest line is dropped as each new one arrives. It defaults to DefaultMaxEntries
        // and can be raised/lowered per program when Register() is called. FlushIntervalMs is how often a
        // changed buffer is mirrored to disk (plus a final flush on program stop), which both bounds flash
        // wear and bounds how much is lost to a hard power-cut between flushes.
        private const int DefaultMaxEntries = 2000;
        private const int FlushIntervalMs   = 5000;

        // Private: instances are obtained only through the singleton. Hooks the program-status
        // event so the CWS server is released automatically when the program stops or restarts.
        private CwsDebugPage()
        {
            CrestronEnvironment.ProgramStatusEventHandler += OnProgramStatus;
        }

        // Base path used when the caller does not supply one. On VC-4 this default is collapsed
        // to the CWS root so the page is reachable at .../cws/debugger.html (see Register).
        private const string DefaultBasePath = "/debugger";

        // Library version, shown in the page header. Keep in step with the .csproj <Version>.
        private const string Version = "1.2.0";

        /// <summary>
        /// Starts the CWS web server. Call from InitializeSystem(), not the constructor.
        /// The header shows "{Name} - Debugger", where Name is the room name on VC-4 and the
        /// host program's namespace/assembly name on an appliance, falling back to the
        /// controller's prompt name, then the base-path segment, then "Debugger".
        ///
        /// On VC-4 the default base path is collapsed so the page is served directly at
        /// .../cws/debugger.html. Pass an explicit (non-default) base path to keep it in the
        /// URL on every platform — required if a single program hosts more than one page.
        ///
        /// maxEntries caps the in-memory (and persisted) ring buffer; older lines are dropped once
        /// it is full. It defaults to DefaultMaxEntries (2000). A value &lt;= 0 is ignored and the
        /// default is kept.
        /// </summary>
        public static void Register(string basePath = DefaultBasePath, int maxEntries = DefaultMaxEntries)
        {
            // The "friendly name" used in the appliance header is the namespace/assembly name
            // of the program that imported this library. Capture it HERE, in the public entry
            // point, so GetCallingAssembly resolves to the caller's program and not this dll —
            // reading it inside RegisterInternal would return CwsDebug.dll instead.
            string? hostName;
            try { hostName = Assembly.GetCallingAssembly().GetName().Name; }
            catch { hostName = null; }
            Instance.RegisterInternal(basePath, hostName, maxEntries);
        }

        // Actual registration, run against the singleton instance. hostName is supplied by the
        // static Register() above (see the note there on why the caller must capture it).
        private void RegisterInternal(string basePath, string? hostName, int maxEntries)
        {
            if (_server != null) return;
            var path = '/' + basePath.Trim('/');

            if (maxEntries > 0) _maxEntries = maxEntries;   // <= 0 keeps the default

            // The processor error log is read via the "err" console command, which is only
            // meaningful on a hardware appliance (RMC4 etc.). On the Server platform (VC-4)
            // there is no per-program console error log, so the feature is disabled there.
            var isAppliance = CrestronEnvironment.DevicePlatform == eDevicePlatform.Appliance;
            _errorLogAvailable = isAppliance;

            _pageTitle = ResolvePageTitle(isAppliance, path, hostName);

            // On an appliance, programs share /cws/, so register under the base path
            // (e.g. /cws/debugger/debugger.html) to avoid colliding with other handlers.
            // On VC-4 each program has its own isolated CWS namespace, so the default base
            // path is collapsed and the page is served directly at .../cws/debugger.html.
            // An explicit (non-default) base path is always honored so callers can still
            // namespace multiple handlers within one program on either platform.
            var usingDefault = string.Equals(path, DefaultBasePath, StringComparison.OrdinalIgnoreCase);
            var registerPath = (isAppliance || !usingDefault) ? path : "/";

            ErrorLog.Notice("CwsDebugPage: platform={0}; processor error log {1}; cws base \"{2}\"",
                CrestronEnvironment.DevicePlatform,
                _errorLogAvailable ? "enabled" : "disabled (Server/VC-4)",
                registerPath);

            // Persist the application log so it survives a program restart. The file lives in the
            // application directory (persistent on both appliance and VC-4) and is namespaced by the
            // base path so two pages in one program don't share a file. Load any prior contents now,
            // then start the periodic flush; a final flush also runs on program stop (see Cleanup).
            _persistPath = Path.Combine(Directory.GetApplicationDirectory(),
                "cwsdebug" + path.Replace('/', '_') + ".log");
            _statePath   = _persistPath + ".state";
            LoadLoggingState();
            LoadPersisted();
            _flushTimer = new CTimer(_ => FlushToDisk(), FlushIntervalMs, FlushIntervalMs);

            _server = new HttpCwsServer(registerPath);
            _server.HttpRequestHandler = this;
            _server.Register();
        }

        // Header name: the room name on VC-4, and on an appliance the namespace/assembly name
        // of the host program (hostName). Falls back to the controller's friendly (prompt)
        // name, then the base-path segment, then "Debugger", when a value is unavailable.
        private static string ResolvePageTitle(bool isAppliance, string path, string? hostName)
        {
            string? name;
            try
            {
                if (isAppliance)
                    name = !string.IsNullOrWhiteSpace(hostName)
                        ? hostName
                        : InitialParametersClass.ControllerPromptName;
                else
                    name = InitialParametersClass.RoomName;
            }
            catch
            {
                name = null;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                var segment = path.TrimStart('/').Split('/')[0];
                name = segment.Length > 0
                    ? char.ToUpper(segment[0]) +
                      (segment.Length > 1 ? segment.Substring(1).ToLower() : string.Empty)
                    : "Debugger";
            }

            return name.Trim();
        }

        // ── Logging API ─────────────────────────────────────────────────────────────
        // Each method accepts an optional string.Format template plus args, e.g.
        //   CwsDebugPage.LogInfo("Connected to {0}:{1}", host, port);

        /// <summary>Severity levels, in ascending order, for use with <see cref="MinLevel"/>.</summary>
        public enum DebugLevel { Info = 0, Notice = 1, Warning = 2, Error = 3, Fatal = 4 }

        /// <summary>
        /// Minimum severity that is recorded. Messages below this level are dropped at the source —
        /// never stored, persisted or served — which trims noise and memory/bandwidth in production
        /// (e.g. set to DebugLevel.Warning to silence Info/Notice). Defaults to Info (record all).
        /// This is distinct from the page's level-filter buttons, which only hide already-stored
        /// entries client-side. Settable at any time from any thread.
        /// </summary>
        public static DebugLevel MinLevel { get; set; } = DebugLevel.Info;

        /// <summary>
        /// Master record switch. When false, every Log* call is a no-op — nothing is stored, persisted
        /// or served (fatals are still mirrored to the system error log via LogFatal). Defaults to true.
        /// The state is persisted, so it survives a program restart: turn it on for a debugging session
        /// and off when finished. Also toggleable from the page's "Logging" button.
        /// </summary>
        public static bool LoggingEnabled
        {
            get => Instance._loggingEnabled;
            set => Instance.SetLoggingEnabled(value);
        }

        // Maps a level name to its DebugLevel rank; unknown names are treated as Info (always kept).
        private static DebugLevel RankOf(string level) => level switch
        {
            "Notice"  => DebugLevel.Notice,
            "Warning" => DebugLevel.Warning,
            "Error"   => DebugLevel.Error,
            "Fatal"   => DebugLevel.Fatal,
            _         => DebugLevel.Info,
        };

        /// <summary>Records an informational message.</summary>
        public static void LogInfo  (string message, params object[] args) => Instance.Append("Info",   message, args);

        /// <summary>Records a notice-level message.</summary>
        public static void LogNotice(string message, params object[] args) => Instance.Append("Notice", message, args);

        /// <summary>Records a warning-level message.</summary>
        public static void LogWarning(string message, params object[] args) => Instance.Append("Warning", message, args);

        /// <summary>Records an error-level message.</summary>
        public static void LogError (string message, params object[] args) => Instance.Append("Error",  message, args);

        /// <summary>Records a fatal message and also writes it to the processor error log.</summary>
        public static void LogFatal (string message, params object[] args)
        {
            ErrorLog.Error(message, args);          // mirror fatals to the system error log
            Instance.Append("Fatal", message, args);
        }

        /// <summary>
        /// Records an exception at error level, including its full type, message and stack trace
        /// (and any inner exceptions) via Exception.ToString() — far more useful on hardware than
        /// just ex.Message. An optional context describes what was being attempted.
        /// </summary>
        public static void LogException(Exception? ex, string? context = null)
        {
            var detail = ex?.ToString() ?? "(null exception)";
            var msg    = string.IsNullOrWhiteSpace(context) ? detail : context + Environment.NewLine + detail;
            // No args -> Append does not run string.Format, so any '{'/'}' in the trace is safe.
            Instance.Append("Error", msg, Array.Empty<object>());
        }

        // Formats the message (only when args are supplied), stamps it with a millisecond-resolution
        // timestamp, assigns the next sequence id, and appends it under the lock so concurrent CWS
        // request threads stay consistent. Enforces the ring cap and marks the buffer dirty so the
        // next flush mirrors it to disk.
        private void Append(string level, string message, object[] args)
        {
            if (!_loggingEnabled) return;           // master switch off — record nothing
            if (RankOf(level) < MinLevel) return;   // below the configured floor — drop it entirely

            var msg = args.Length > 0 ? string.Format(message, args) : message;
            var ts  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            lock (_lock)
            {
                _log.Add(new LogEntry(level, msg, ts, ++_seq));
                if (_log.Count > _maxEntries) _log.RemoveRange(0, _log.Count - _maxEntries);
                _dirty = true;
            }
        }

        // ── Request routing ──────────────────────────────────────────────────────────
        // CWS invokes this for every request under the registered base path. Routes are
        // matched on the path suffix so they resolve whether the page sits at
        // /cws/debugger.html (VC-4) or /cws/debugger/debugger.html (appliance). The page's
        // JavaScript fetches the api/* endpoints with relative URLs, so they line up either way.

        void IHttpCwsHandler.ProcessRequest(HttpCwsContext context)
        {
            var path   = context.Request.Path       ?? string.Empty;
            var method = context.Request.HttpMethod ?? "GET";

            if (path.EndsWith("debugger.html", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeHtml(context);                 // the single-page UI
            else if (path.EndsWith("api/logs", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeLogs(context);                 // application log as JSON
            else if (path.EndsWith("api/errorlog", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeErrorLog(context);             // processor error log as JSON (appliance only)
            else if (path.EndsWith("api/clear", StringComparison.OrdinalIgnoreCase) && method == "POST")
                ClearLogs(context);                 // empties the in-memory application log
            else if (path.EndsWith("api/logging", StringComparison.OrdinalIgnoreCase))
                ServeLogging(context, method);      // GET reads / POST toggles the master logging switch
            else
            {
                context.Response.StatusCode  = 404; // anything else is unknown
                context.Response.ContentType = "text/plain";
                context.Response.Write("Not found", true);
            }
        }

        // Serves the page itself, with the resolved title and error-log availability baked in.
        private void ServeHtml(HttpCwsContext context)
        {
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "text/html";
            context.Response.Write(BuildHtml(_pageTitle, _errorLogAvailable, _loggingEnabled, _maxEntries), true);
        }

        // Returns the application log as JSON: {"lastSeq":N,"entries":[{level,msg,ts,source}, ...]}.
        // The page passes ?since=<lastSeq> so only entries newer than it are sent; the returned
        // lastSeq is what the page sends next time. since<=0 (first load) returns the whole buffer.
        private void ServeLogs(HttpCwsContext context)
        {
            long since = 0;
            try
            {
                var raw = context.Request.QueryString["since"];
                if (!string.IsNullOrEmpty(raw)) long.TryParse(raw, out since);
            }
            catch { since = 0; }

            // Copy under the lock, then build the response outside it to keep the lock short.
            List<LogEntry> snapshot;
            long lastSeq;
            lock (_lock)
            {
                snapshot = since > 0 ? _log.FindAll(e => e.Seq > since) : new List<LogEntry>(_log);
                lastSeq  = _seq;
            }

            var sb = new StringBuilder();
            sb.Append("{\"lastSeq\":");
            sb.Append(lastSeq);
            sb.Append(",\"entries\":[");
            bool first = true;
            foreach (var e in snapshot)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(string.Format("{{\"level\":{0},\"msg\":{1},\"ts\":{2},\"source\":\"Debugger\"}}",
                    JsonString(e.Level), JsonString(e.Message), JsonString(e.Timestamp)));
            }
            sb.Append("]}");

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // Empties the in-memory application log (the processor error log is not affected). _seq is
        // left untouched so the page's ?since cursor stays valid and only genuinely new entries
        // arrive afterwards. Marked dirty so the cleared state is mirrored to disk on the next flush.
        private void ClearLogs(HttpCwsContext context)
        {
            lock (_lock) { _log.Clear(); _dirty = true; }
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"cleared\":true}", true);
        }

        // Master logging switch endpoint. GET returns the current state; POST?on=1|0 sets it (and
        // persists it). Always responds with {"enabled":<bool>} so the page can update its button.
        private void ServeLogging(HttpCwsContext context, string method)
        {
            if (method == "POST")
            {
                var on = _loggingEnabled;
                try
                {
                    var raw = context.Request.QueryString["on"];
                    if (!string.IsNullOrEmpty(raw)) on = raw == "1";
                }
                catch { /* leave unchanged on a malformed request */ }
                SetLoggingEnabled(on);
            }

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"enabled\":" + (_loggingEnabled ? "true" : "false") + "}", true);
        }

        // ── Processor error log ───────────────────────────────────────────────────────

        // Matches a console "err" line: "<n>. <Level>: <task> # <timestamp> # <message>"
        private static readonly Regex ErrLineRegex =
            new Regex(@"^\d+\.\s*(\w+):\s*([^#]*?)\s*#\s*([^#]*?)\s*#\s*(.*)$", RegexOptions.Compiled);

        // Returns the processor error log as JSON, including an "available" flag so the page
        // can hide the feature on platforms (VC-4) where it does not apply.
        private void ServeErrorLog(HttpCwsContext context)
        {
            var entries = _errorLogAvailable ? ReadProcessorErrorLog() : new List<LogEntry>();

            var sb = new StringBuilder();
            sb.Append("{\"available\":");
            sb.Append(_errorLogAvailable ? "true" : "false");
            sb.Append(",\"entries\":[");
            bool first = true;
            foreach (var e in entries)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(string.Format("{{\"level\":{0},\"msg\":{1},\"ts\":{2},\"source\":\"Processor\"}}",
                    JsonString(e.Level), JsonString(e.Message), JsonString(e.Timestamp)));
            }
            sb.Append("]}");

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // Runs the console "err" command, then parses each recognised line into a LogEntry.
        // Any failure (command unavailable, unexpected output) yields an empty list rather
        // than throwing, so a missing error log never breaks the page.
        private static List<LogEntry> ReadProcessorErrorLog()
        {
            var result   = new List<LogEntry>();
            string output = string.Empty;

            try
            {
                if (!CrestronConsole.SendControlSystemCommand("err", ref output) || string.IsNullOrEmpty(output))
                    return result;
            }
            catch
            {
                return result;                      // console command not supported here
            }

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                var m = ErrLineRegex.Match(line);
                if (!m.Success) continue;           // skip headers / non-matching lines

                var level = NormalizeLevel(m.Groups[1].Value);
                var task  = m.Groups[2].Value.Trim();
                var ts    = m.Groups[3].Value.Trim();
                var msg   = m.Groups[4].Value.Trim();
                if (task.Length > 0) msg = task + ": " + msg;   // prefix the originating task

                result.Add(new LogEntry(level, msg, ts, 0));     // Seq is N/A for processor-log lines
            }
            return result;
        }

        // Title-cases a raw level token ("ERROR" -> "Error") so it matches the page's badges.
        private static string NormalizeLevel(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Info";
            var s = raw.Trim();
            return char.ToUpper(s[0]) + (s.Length > 1 ? s.Substring(1).ToLower() : string.Empty);
        }

        // Minimal JSON string encoder: wraps in quotes and escapes the characters that would
        // otherwise break the response (backslash, quote, CR, LF). Sufficient for log text.
        private static string JsonString(string s) =>
            '"' + s.Replace("\\", "\\\\")
                   .Replace("\"", "\\\"")
                   .Replace("\r", "\\r")
                   .Replace("\n", "\\n") + '"';

        // ── Persistence ────────────────────────────────────────────────────────────────
        // The application log is mirrored to a file so it survives a program restart. The on-disk
        // format is one line per entry: "Level<TAB>Timestamp<TAB>EscapedMessage", where the message
        // has its backslashes, tabs and newlines escaped so each entry stays on a single line.

        // Loads any previously persisted entries into _log. Called once from Register(), before the
        // server starts serving. Loaded history is placed ahead of anything logged before Register(),
        // the whole thing is capped to MaxEntries (keeping the newest), and sequence ids are reassigned
        // so they stay monotonic. Any failure leaves the in-memory log as-is — a bad file never throws.
        private void LoadPersisted()
        {
            if (string.IsNullOrEmpty(_persistPath)) return;
            try
            {
                if (!File.Exists(_persistPath)) return;

                string content;
                using (var reader = new StreamReader(_persistPath)) content = reader.ReadToEnd();

                var lines  = content.Split('\n');
                var loaded = new List<LogEntry>(lines.Length);
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (string.IsNullOrEmpty(line)) continue;
                    var parts = line.Split('\t');
                    if (parts.Length < 3) continue;             // skip malformed lines
                    loaded.Add(new LogEntry(parts[0], Unescape(parts[2]), parts[1], 0));
                }

                lock (_lock)
                {
                    var merged = new List<LogEntry>(loaded);
                    merged.AddRange(_log);                       // keep anything logged before Register()
                    if (merged.Count > _maxEntries)
                        merged.RemoveRange(0, merged.Count - _maxEntries);

                    _log.Clear();
                    _seq = 0;
                    foreach (var e in merged)
                        _log.Add(new LogEntry(e.Level, e.Message, e.Timestamp, ++_seq));
                    _dirty = false;                              // freshly loaded state matches disk
                }
            }
            catch { /* corrupt/unreadable persisted log is non-fatal */ }
        }

        // Writes the current buffer to disk if it has changed since the last flush. Runs on the flush
        // timer and once more on program stop. Writes to a temp file then swaps it in, so a flush
        // interrupted mid-write cannot corrupt the live file. On any failure the dirty flag is restored
        // so the next tick retries.
        private void FlushToDisk()
        {
            if (string.IsNullOrEmpty(_persistPath)) return;

            List<LogEntry> snapshot;
            lock (_lock)
            {
                if (!_dirty) return;
                snapshot = new List<LogEntry>(_log);
                _dirty   = false;
            }

            try
            {
                var sb = new StringBuilder();
                foreach (var e in snapshot)
                    sb.Append(e.Level).Append('\t').Append(e.Timestamp).Append('\t')
                      .Append(Escape(e.Message)).Append('\n');

                var tmp = _persistPath + ".tmp";
                using (var writer = new StreamWriter(tmp, false)) writer.Write(sb.ToString());
                if (File.Exists(_persistPath)) File.Delete(_persistPath);
                File.Move(tmp, _persistPath);
            }
            catch
            {
                lock (_lock) _dirty = true;                      // retry on the next tick
            }
        }

        // Sets the master logging switch and persists the new state so it survives a restart.
        private void SetLoggingEnabled(bool enabled)
        {
            _loggingEnabled = enabled;
            SaveLoggingState();
        }

        // Loads the persisted master-switch state. Called from Register(); a missing or unreadable
        // file leaves the default (enabled) in place. Stored as a single character: "1" on, "0" off.
        private void LoadLoggingState()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            try
            {
                if (!File.Exists(_statePath)) return;
                string content;
                using (var reader = new StreamReader(_statePath)) content = reader.ReadToEnd();
                _loggingEnabled = content.Trim() != "0";
            }
            catch { /* unreadable state -> keep default (enabled) */ }
        }

        // Writes the master-switch state to disk. Best-effort: a failure never propagates.
        private void SaveLoggingState()
        {
            if (string.IsNullOrEmpty(_statePath)) return;
            try
            {
                using var writer = new StreamWriter(_statePath, false);
                writer.Write(_loggingEnabled ? "1" : "0");
            }
            catch { /* best-effort */ }
        }

        // Escapes the characters that would otherwise break the one-line-per-entry on-disk format.
        private static string Escape(string s) =>
            s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

        // Reverses Escape(): turns \\, \t, \r, \n back into their literal characters.
        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 't' ? '\t' : n == 'r' ? '\r' : n == 'n' ? '\n' : n);
                }
                else sb.Append(s[i]);
            }
            return sb.ToString();
        }

        // ── Teardown ───────────────────────────────────────────────────────────────────
        // Cleanup is automatic: the constructor subscribes to ProgramStatusEventHandler, so the
        // CWS server is released when the program stops or restarts. The finalizer is a backstop
        // for the unmanaged server registration should that event ever not fire. Calling
        // Dispose()/Shutdown() explicitly is therefore optional but harmless.

        // Fired by Crestron when the program's run state changes; tear down on stop/restart.
        private void OnProgramStatus(eProgramStatusEventType status)
        {
            if (status == eProgramStatusEventType.Stopping)
                Cleanup();
        }

        /// <summary>Stops the CWS server and releases it. Safe to call more than once.</summary>
        public void Dispose()
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }

        /// <summary>Convenience alias for Dispose() on the singleton; rarely needed (see above).</summary>
        public static void Shutdown() => _instance?.Dispose();

        // Finalizer backstop: releases the CWS server if neither the program-status event nor an
        // explicit Dispose() ran. As a static-rooted singleton this only runs at AppDomain
        // teardown — which is exactly the program stop/restart we want to be covered against.
        ~CwsDebugPage() => Cleanup();

        // The single idempotent teardown path shared by Dispose(), the finalizer and the
        // program-status handler. Unhooks the event and releases the CWS server exactly once.
        private void Cleanup()
        {
            if (_disposed) return;                  // idempotent
            _disposed = true;

            try { CrestronEnvironment.ProgramStatusEventHandler -= OnProgramStatus; }
            catch { /* environment already torn down */ }

            // Stop the periodic flush, then write one final snapshot so the log captured right up to
            // the stop/restart survives. FlushToDisk is a no-op when nothing has changed.
            if (_flushTimer != null)
            {
                try { _flushTimer.Stop(); _flushTimer.Dispose(); } catch { /* already torn down */ }
                _flushTimer = null;
            }
            try { FlushToDisk(); } catch { /* best-effort on shutdown */ }

            if (_server != null)
            {
                try { _server.Dispose(); } catch { /* already torn down */ }
                _server = null;
            }
        }

        // ── HTML ─────────────────────────────────────────────────────────────────────

        // Produces the final page by substituting the runtime values (header text and the
        // error-log availability flag) into the static template below.
        private static string BuildHtml(string pageTitle, bool errorLogAvailable, bool loggingEnabled, int maxEntries)
        {
            var heading = pageTitle + " - Debugger";
            return BuildHtmlTemplate()
                .Replace("__TITLE__", heading)
                .Replace("__VERSION__", Version)
                .Replace("__ERRORLOG__", errorLogAvailable ? "true" : "false")
                .Replace("__LOGGING__", loggingEnabled ? "true" : "false")
                .Replace("__MAXENTRIES__", maxEntries.ToString());
        }

        // The complete single-page UI (markup + CSS + JavaScript) as a verbatim string, so the
        // library has no external content files to deploy. Placeholders __TITLE__ / __ERRORLOG__
        // are filled in by BuildHtml. The script polls api/logs every 2s and renders client-side.
        private static string BuildHtmlTemplate() => @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1.0"">
<title>__TITLE__</title>
<style>
:root{
  --bg:#111827;--bg2:#1f2937;--border:#374151;--text:#e5e7eb;--muted:#6b7280;
  --row-border:#1f2937;--search-bg:#0d111a;
  --bi-bg:#1e293b;--bi-fg:#94a3b8;
  --bn-bg:#1e3a5f;--bn-fg:#60a5fa;
  --bw-bg:#3a2400;--bw-fg:#fb923c;
  --be-bg:#3b2700;--be-fg:#fbbf24;
  --bf-bg:#3b0f0f;--bf-fg:#f87171;
  --sp-fg:#a78bfa;
}
body.light{
  --bg:#f3f4f6;--bg2:#ffffff;--border:#d1d5db;--text:#111827;--muted:#6b7280;
  --row-border:#e5e7eb;--search-bg:#f9fafb;
  --bi-bg:#e2e8f0;--bi-fg:#475569;
  --bn-bg:#dbeafe;--bn-fg:#1d4ed8;
  --bw-bg:#ffedd5;--bw-fg:#c2410c;
  --be-bg:#fef3c7;--be-fg:#92400e;
  --bf-bg:#fee2e2;--bf-fg:#b91c1c;
  --sp-fg:#7c3aed;
}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--text);font-family:'Courier New',monospace;font-size:13px;height:100vh;display:flex;flex-direction:column;transition:background .15s,color .15s}
header{background:var(--bg2);padding:10px 16px;display:flex;align-items:center;justify-content:space-between;border-bottom:2px solid var(--border);flex-shrink:0}
h1{font-size:16px;color:#f87171;letter-spacing:3px;text-transform:uppercase}
.brand{display:flex;align-items:baseline;gap:8px}
.ver{font-size:11px;color:var(--muted);letter-spacing:1px}
.controls{display:flex;align-items:center;gap:10px}
#status{font-size:11px;color:var(--muted);white-space:nowrap}
.searchbar{background:var(--bg2);padding:6px 14px;border-bottom:1px solid var(--border);flex-shrink:0}
#search{width:100%;background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:5px 10px;font:13px 'Courier New',monospace;outline:none}
#search::placeholder{color:var(--muted)}
#search:focus{border-color:#6b7280}
.filterbar{background:var(--bg2);padding:6px 14px;border-bottom:1px solid var(--border);flex-shrink:0;display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.flabel{font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:1px;margin-right:2px}
.lvl{font:bold 10px 'Courier New',monospace;letter-spacing:1px;padding:2px 8px;border-radius:2px;cursor:pointer;text-transform:uppercase;border:1px solid var(--border);background:transparent;color:var(--text);opacity:.4}
.lvl.on{opacity:1}
.lvl-info.on   {background:var(--bi-bg);color:var(--bi-fg);border-color:var(--bi-fg)}
.lvl-notice.on {background:var(--bn-bg);color:var(--bn-fg);border-color:var(--bn-fg)}
.lvl-warning.on{background:var(--bw-bg);color:var(--bw-fg);border-color:var(--bw-fg)}
.lvl-error.on  {background:var(--be-bg);color:var(--be-fg);border-color:var(--be-fg)}
.lvl-fatal.on  {background:var(--bf-bg);color:var(--bf-fg);border-color:var(--bf-fg)}
button{background:#374151;color:var(--text);border:1px solid var(--border);padding:4px 12px;border-radius:3px;cursor:pointer;font:12px 'Courier New',monospace;letter-spacing:1px;text-transform:uppercase;white-space:nowrap}
button:hover{background:#4b5563}
#clearBtn{background:#dc2626;border-color:#b91c1c;color:#fff}
#clearBtn:hover{background:#b91c1c}
.toggle{opacity:.5;border-style:dashed}
.toggle.on{opacity:1;border-style:solid;background:#1d4ed8;border-color:#1d4ed8;color:#fff}
.toggle.on:hover{background:#1e40af}
.log{flex:1;overflow-y:auto;padding:4px 0}
.log::-webkit-scrollbar{width:5px}
.log::-webkit-scrollbar-track{background:var(--bg)}
.log::-webkit-scrollbar-thumb{background:var(--border);border-radius:3px}
.entry{display:flex;gap:8px;align-items:baseline;padding:4px 14px;border-bottom:1px solid var(--row-border);line-height:1.5;word-break:break-word}
.badge{flex-shrink:0;font-size:10px;font-weight:bold;letter-spacing:1px;padding:1px 5px;border-radius:2px;text-transform:uppercase;min-width:50px;text-align:center}
.badge-info   {background:var(--bi-bg);color:var(--bi-fg)}
.badge-notice {background:var(--bn-bg);color:var(--bn-fg)}
.badge-warning{background:var(--bw-bg);color:var(--bw-fg)}
.badge-error  {background:var(--be-bg);color:var(--be-fg)}
.badge-fatal  {background:var(--bf-bg);color:var(--bf-fg)}
.src{flex-shrink:0;font-size:9px;font-weight:bold;letter-spacing:1px;padding:1px 4px;border-radius:2px;min-width:36px;text-align:center;border:1px solid var(--border)}
.src-app {color:var(--muted)}
.src-proc{color:var(--sp-fg)}
.msg{flex:1}
.ts{flex-shrink:0;color:var(--muted);font-size:11px}
.empty{color:var(--muted);padding:12px 14px;font-style:italic}
</style>
</head>
<body>
<header>
  <div class=""brand""><h1>__TITLE__</h1><span class=""ver"">v__VERSION__</span></div>
  <div class=""controls"">
    <span id=""status"">Loading...</span>
    <button id=""logBtn"" class=""toggle on"" onclick=""toggleLogging()"">Logging On</button>
    <button id=""pauseBtn"" class=""toggle"" onclick=""togglePause()"">Pause</button>
    <button id=""appBtn""  class=""toggle on"" onclick=""toggleSource('app')"">Debugger</button>
    <button id=""procBtn"" class=""toggle"" onclick=""toggleSource('proc')"">Error Log</button>
    <button id=""themeBtn"" onclick=""toggleTheme()"">Light Mode</button>
    <button id=""exportBtn"" onclick=""exportLog()"">Export</button>
    <button id=""clearBtn"" onclick=""clearLogs()"">Clear</button>
  </div>
</header>
<div class=""searchbar"">
  <input id=""search"" type=""text"" placeholder=""Search messages..."" oninput=""applyFilter()"">
</div>
<div class=""filterbar"">
  <span class=""flabel"">Levels:</span>
  <button class=""lvl lvl-info on""    data-level=""Info""    onclick=""toggleLevel('Info')"">Info</button>
  <button class=""lvl lvl-notice on""  data-level=""Notice""  onclick=""toggleLevel('Notice')"">Notice</button>
  <button class=""lvl lvl-warning on"" data-level=""Warning"" onclick=""toggleLevel('Warning')"">Warning</button>
  <button class=""lvl lvl-error on""   data-level=""Error""   onclick=""toggleLevel('Error')"">Error</button>
  <button class=""lvl lvl-fatal on""   data-level=""Fatal""   onclick=""toggleLevel('Fatal')"">Fatal</button>
</div>
<div class=""log"" id=""log""></div>
<script>
var debuggerEntries=[];
var processorEntries=[];
var showApp=true, showProc=false;
var lastSeq=0;                 // highest server sequence id we have fetched (incremental polling)
var paused=false, pollTimer=null;
var ERRORLOG_AVAILABLE=__ERRORLOG__;
var LOGGING_ENABLED=__LOGGING__;
var CLIENT_MAX=__MAXENTRIES__;   // client display cap, matched to the server ring size
var levelOn={Info:true,Notice:true,Warning:true,Error:true,Fatal:true};
var LEVELS=['Info','Notice','Warning','Error','Fatal'];
var BADGE={Info:'badge-info',Notice:'badge-notice',Warning:'badge-warning',Error:'badge-error',Fatal:'badge-fatal'};
function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
function combined(){
  var arr=[];
  if(showApp)  arr=arr.concat(debuggerEntries);
  if(showProc) arr=arr.concat(processorEntries);
  arr.sort(function(a,b){return a.ts<b.ts?-1:(a.ts>b.ts?1:0);});
  return arr;
}
function render(entries){
  var log=document.getElementById('log');
  var atBottom=log.scrollHeight-log.scrollTop<=log.clientHeight+4;
  log.innerHTML='';
  if(!entries.length){log.innerHTML='<div class=""empty"">No entries.</div>';return;}
  entries.forEach(function(e){
    var row=document.createElement('div');
    row.className='entry';
    var cls=BADGE[e.level]||'badge-info';
    var proc=e.source==='Processor';
    row.innerHTML='<span class=""src '+(proc?'src-proc':'src-app')+'"">'+(proc?'SYS':'APP')+'</span>'+
                  '<span class=""badge '+cls+'"">'+esc(e.level)+'</span>'+
                  '<span class=""msg"">'+esc(e.msg)+'</span>'+
                  (e.ts?'<span class=""ts"">'+esc(e.ts)+'</span>':'');
    log.appendChild(row);
  });
  if(atBottom)log.scrollTop=log.scrollHeight;
}
function applyFilter(){
  render(visibleEntries());
}
function refresh(){
  var tasks=[fetch('api/logs?since='+lastSeq).then(function(r){return r.json();}).then(function(d){
    if(d.entries&&d.entries.length){
      debuggerEntries=debuggerEntries.concat(d.entries);
      // bound client memory to the server ring size; the server already bounds what it sends
      if(debuggerEntries.length>CLIENT_MAX) debuggerEntries=debuggerEntries.slice(debuggerEntries.length-CLIENT_MAX);
    }
    if(typeof d.lastSeq==='number') lastSeq=d.lastSeq;
  })];
  if(showProc) tasks.push(fetch('api/errorlog').then(function(r){return r.json();}).then(function(d){processorEntries=d.entries||[];}));
  Promise.all(tasks).then(function(){
    applyFilter();
    var note=LOGGING_ENABLED?'':'  — LOGGING OFF';
    document.getElementById('status').textContent='Updated '+new Date().toLocaleTimeString()+note;
  }).catch(function(){document.getElementById('status').textContent='Connection error';});
}
function clearLogs(){
  fetch('api/clear',{method:'POST'}).then(function(){debuggerEntries=[];applyFilter();});
}
function visibleEntries(){
  var q=document.getElementById('search').value.toLowerCase().trim();
  var src=combined().filter(function(e){return levelOn[e.level]!==false;});
  return q?src.filter(function(e){
    return e.msg.toLowerCase().indexOf(q)>=0||e.level.toLowerCase().indexOf(q)>=0;
  }):src;
}
function pad(n){return n<10?'0'+n:''+n;}
function exportLog(){
  var entries=visibleEntries();
  var lines=entries.map(function(e){
    var src=e.source==='Processor'?'SYS':'APP';
    return (e.ts||'')+'  ['+src+'] '+(e.level||'').toUpperCase()+'\t'+e.msg;
  });
  var head=document.title+'\r\nExported '+new Date().toLocaleString()+
           '\r\nEntries: '+entries.length+
           '\r\n========================================\r\n';
  var text=head+lines.join('\r\n')+'\r\n';
  var blob=new Blob([text],{type:'text/plain'});
  var url=URL.createObjectURL(blob);
  var d=new Date();
  var stamp=d.getFullYear()+pad(d.getMonth()+1)+pad(d.getDate())+'-'+pad(d.getHours())+pad(d.getMinutes())+pad(d.getSeconds());
  var a=document.createElement('a');
  a.href=url; a.download='debugger-log-'+stamp+'.txt';
  document.body.appendChild(a); a.click(); document.body.removeChild(a);
  setTimeout(function(){URL.revokeObjectURL(url);},1000);
}
function toggleSource(which){
  if(which==='app') showApp=!showApp; else showProc=!showProc;
  document.getElementById('appBtn').classList.toggle('on',showApp);
  document.getElementById('procBtn').classList.toggle('on',showProc);
  try{localStorage.setItem('dbg-show-app',showApp?'1':'0');localStorage.setItem('dbg-show-proc',showProc?'1':'0');}catch(x){}
  refresh();
}
function toggleLevel(name){
  levelOn[name]=!levelOn[name];
  var b=document.querySelector('.lvl[data-level='+name+']');
  if(b) b.classList.toggle('on',levelOn[name]);
  try{localStorage.setItem('dbg-levels',JSON.stringify(levelOn));}catch(x){}
  applyFilter();
}
function toggleTheme(){
  var light=document.body.classList.toggle('light');
  document.getElementById('themeBtn').textContent=light?'Dark Mode':'Light Mode';
  try{localStorage.setItem('dbg-theme',light?'light':'dark');}catch(x){}
}
function togglePause(){
  // Client-side only: stops polling/rendering. The server keeps recording while paused, so
  // resuming fetches everything logged during the freeze (subject to the server ring cap).
  paused=!paused;
  var b=document.getElementById('pauseBtn');
  b.textContent=paused?'Resume':'Pause';
  b.classList.toggle('on',paused);
  if(paused){
    if(pollTimer){clearInterval(pollTimer);pollTimer=null;}
    document.getElementById('status').textContent='Paused — recording continues';
  }else{
    refresh();
    pollTimer=setInterval(refresh,2000);
  }
}
function toggleLogging(){
  // Server-side master switch. Persists across restarts; reflects the state the server reports back.
  var next=!LOGGING_ENABLED;
  fetch('api/logging?on='+(next?'1':'0'),{method:'POST'}).then(function(r){return r.json();}).then(function(d){
    LOGGING_ENABLED=!!d.enabled;
    var b=document.getElementById('logBtn');
    b.textContent=LOGGING_ENABLED?'Logging On':'Logging Off';
    b.classList.toggle('on',LOGGING_ENABLED);
  }).catch(function(){});
}
(function(){
  try{
    if(localStorage.getItem('dbg-theme')==='light'){
      document.body.classList.add('light');
      document.getElementById('themeBtn').textContent='Dark Mode';
    }
    var a=localStorage.getItem('dbg-show-app');  if(a!==null) showApp=a==='1';
    var p=localStorage.getItem('dbg-show-proc'); if(p!==null) showProc=p==='1';
    var lv=localStorage.getItem('dbg-levels');
    if(lv){var s=JSON.parse(lv);for(var k in s){if(levelOn.hasOwnProperty(k))levelOn[k]=s[k];}}
  }catch(x){}
  if(!ERRORLOG_AVAILABLE){
    showProc=false;
    var pb=document.getElementById('procBtn');
    if(pb) pb.style.display='none';
  }
  document.getElementById('appBtn').classList.toggle('on',showApp);
  document.getElementById('procBtn').classList.toggle('on',showProc);
  var lb=document.getElementById('logBtn');
  if(lb){lb.textContent=LOGGING_ENABLED?'Logging On':'Logging Off';lb.classList.toggle('on',LOGGING_ENABLED);}
  LEVELS.forEach(function(n){
    var b=document.querySelector('.lvl[data-level='+n+']');
    if(b) b.classList.toggle('on',levelOn[n]);
  });
})();
refresh();
pollTimer=setInterval(refresh,2000);
</script>
</body>
</html>";
    }
}
