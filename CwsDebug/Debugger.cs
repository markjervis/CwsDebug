// -----------------------------------------------------------------------------
//  CwsDebug — Self-contained CWS debug log page for Crestron SIMPL#Pro programs.
//
//  Author:   Mark Jervis
//  Company:  Stage Front Presentation Systems
//  Version:  1.3.0
//
//  Builds as a Program Library (Crestron.SimplSharp.SDK.ProgramLibrary) and is
//  referenced by a SIMPL#Pro program. It hosts a small web page on the Crestron
//  Web Server (CWS) that streams the program's own log messages plus, on a
//  hardware appliance, the processor error log — with live search, level
//  filtering, light/dark themes and text export.
// -----------------------------------------------------------------------------

using System.Reflection;            // Assembly.GetCallingAssembly() — host program name
using System.Runtime.CompilerServices; // [MethodImpl(NoInlining)] — keeps GetCallingAssembly honest
using System.Text;                  // StringBuilder for JSON responses
using System.Text.RegularExpressions; // parsing the console "err" output
using Crestron.SimplSharp;          // CrestronEnvironment, ErrorLog, InitialParametersClass, CTimer
using Crestron.SimplSharp.WebScripting; // HttpCwsServer / IHttpCwsHandler (CWS hosting)
using Crestron.SimplSharpPro;       // Sig, SmartObject — device join watch
using Crestron.SimplSharpPro.DeviceSupport; // BasicTriList / BasicTriListWithSmartObject
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
        private readonly List<LogEntry> _log  = new();      // in-memory ring; holds >= _maxEntries, transiently up to ~10% more (see Append)
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

        // ── Device join watch (Phase 1: read-only) ─────────────────────────────────────
        // Registered devices, addressed by joins from the page (Toolbox-style) — see the
        // "Device join watch" region below. _devLock guards both dictionaries; no host code
        // is ever invoked under it (sig reads are plain field reads, not callbacks).
        //
        // LOCK ORDERING INVARIANT: _devLock may be held when taking _lock (AppendAudit is
        // called from join paths), but code holding _lock must NEVER take _devLock — that
        // inversion would deadlock. _lock-holding code (Append/flush/serve-logs) currently
        // never touches device state; keep it that way.
        //
        // Known, accepted costs: MarkSigActivity scans _joinCache (<= JoinCacheMax entries)
        // under _devLock on every sig event of a registered device — bounded but a hot spot
        // on a very busy panel. And with 2+ devices watched at once, the single _joinRev
        // cursor shared by the page can skip another device's sub-poll pulse blip (state
        // badges always stay correct; single-device use is immune).
        private readonly object _devLock = new();
        private readonly Dictionary<string, BasicTriList> _devices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SigEventHandler> _devHooks = new(StringComparer.OrdinalIgnoreCase); // per-device SigChange hooks, for unhook
        private readonly Dictionary<string, List<KeyValuePair<SmartObject, SmartObjectSigChangeEventHandler>>> _soHooks
            = new(StringComparer.OrdinalIgnoreCase);    // per-device smart-object SigChange hooks (SOs have their own event)
        private readonly Dictionary<string, WatchSlot> _joinCache = new(StringComparer.Ordinal); // "dev|key" -> resolved sig
        private long _joinRev;                              // bumped when any watched join's rendered value changes

        private const int MaxWatchKeys      = 128;          // per-request cap; the watch set is client-supplied
        private const int JoinCacheMax      = 512;          // resolution-cache bound
        private const int JoinCacheEvictMs  = 300000;       // entries idle this long are evicted (5 min)

        // ── Force gating (Phase 2) ─────────────────────────────────────────────────────
        // Forces mutate a live control system from an unauthenticated page, so they sit
        // behind three gates: per-direction (output sigs are structurally unwritable), the
        // host kill switch (_writesAllowed), and a server-held arm that defaults OFF at
        // every program start, is never persisted, and idles back to off. Deliberately the
        // opposite of _loggingEnabled's persistence: a debugger that re-arms itself (or
        // re-forces values) after a restart is a field-support incident waiting to happen.
        private volatile bool _writesAllowed = true;         // host kill switch (production: set false)
        private volatile bool _armed;                        // arm gate; lazy idle-disarm, no timer
        private int  _armTimeoutMs = 300000;                 // <= 0 disables auto-disarm
        private long _lastArmActivityMs;                     // last arm/force, for the idle check
        private long _forceWinMs;                            // rate-limit window start
        private int  _forceCount;                            // accepted forces in the window
        private const int MaxForcesPerSecond = 20;

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
        private const string Version = "1.3.0";

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
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Register(string basePath = DefaultBasePath, int maxEntries = DefaultMaxEntries)
        {
            // The "friendly name" used in the appliance header is the namespace/assembly name
            // of the program that imported this library. Capture it HERE, in the public entry
            // point, so GetCallingAssembly resolves to the caller's program and not this dll —
            // reading it inside RegisterInternal would return CwsDebug.dll instead. NoInlining
            // (above) stops the JIT from folding this frame away and skewing that resolution.
            string? hostName;
            try { hostName = Assembly.GetCallingAssembly().GetName().Name; }
            catch { hostName = null; }
            Instance.RegisterInternal(basePath, hostName, maxEntries);
        }

        // Actual registration, run against the singleton instance. hostName is supplied by the
        // static Register() above (see the note there on why the caller must capture it).
        private void RegisterInternal(string basePath, string? hostName, int maxEntries)
        {
            // Serialize the whole registration under _gate: two concurrent Register() calls must
            // not both pass the _server null-check and double-create servers/timers.
            lock (_gate) RegisterLocked(basePath, hostName, maxEntries);
        }

        private void RegisterLocked(string basePath, string? hostName, int maxEntries)
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
            // Persistence is initialized exactly once, even if a failed registration is retried —
            // re-running LoadPersisted() would merge the file into a buffer that already contains
            // it and duplicate every line. The timer keeps running after a failed registration on
            // purpose: the page may be unavailable, but the log still survives restarts.
            if (_persistPath == null)
            {
                _persistPath = Path.Combine(Directory.GetApplicationDirectory(),
                    "cwsdebug" + path.Replace('/', '_') + ".log");
                _statePath   = _persistPath + ".state";
                LoadLoggingState();
                LoadPersisted();
                _flushTimer = new CTimer(_ => FlushToDisk(), FlushIntervalMs, FlushIntervalMs);
            }

            var server = new HttpCwsServer(registerPath);
            server.HttpRequestHandler = this;
            try
            {
                server.Register();
            }
            catch (Exception e)
            {
                // Most likely another program on this appliance already registered the same
                // path (e.g. two programs both left at the default "/debugger"). Log it and
                // leave _server null so a bad registration never masquerades as a working one
                // (see RegisterInternal's "if (_server != null) return;" guard above).
                ErrorLog.Error("CwsDebugPage: failed to register CWS path \"{0}\" — {1}", registerPath, e.Message);
                try { server.Dispose(); } catch { /* best-effort */ }
                return;
            }

            _server = server;
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
            // Format once (safely), then mirror the finished text through a "{0}" template so a
            // message containing literal braces can't throw a FormatException out of ErrorLog's
            // own format pass. A logging call must never crash the caller.
            var msg = SafeFormat(message, args);
            try { ErrorLog.Error("{0}", msg); } catch { /* never let logging throw */ }
            Instance.Append("Fatal", msg, Array.Empty<object>());
        }

        // string.Format that never throws back into the caller: a mismatched template/args pair
        // (e.g. LogInfo("{0} {1}", x)) or stray braces fall back to the raw template with the
        // args appended, so a malformed log call still records something rather than crashing
        // the program at its least-expected point — inside a logging statement.
        private static string SafeFormat(string message, object[] args)
        {
            if (args == null || args.Length == 0) return message;   // null: caller passed (object[])null
            try { return string.Format(message, args); }
            catch   // broad on purpose: an arg's ToString()/IFormattable can throw too
            {
                try   { return message + " [" + string.Join(", ", args) + "]"; }
                catch { return message; }   // even Join can throw via ToString(); keep the template
            }
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

            var msg = SafeFormat(message, args);
            var ts  = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            lock (_lock)
            {
                _log.Add(new LogEntry(level, msg, ts, ++_seq));
                // Ring cap: trimming a List from the front is O(count), so rather than paying that
                // on every append once full, let the buffer grow one chunk (~10%) past the cap and
                // trim back down in a single operation — amortized O(1) per append. The buffer never
                // holds fewer than _maxEntries; it just transiently holds up to ~10% more.
                var chunk = Math.Max(1, _maxEntries / 10);
                if (_log.Count >= _maxEntries + chunk) _log.RemoveRange(0, _log.Count - _maxEntries);
                _dirty = true;
            }
        }

        // ── Device join watch ───────────────────────────────────────────────────────────
        // The page addresses a registered device's joins directly (d5, a12, s1, od5,
        // so1.d3, so1.d~Item 1 Visible) — no per-value registration, matching how Crestron
        // Toolbox addresses signals. Phase 1 is read-only: watching only, no forcing.

        /// <summary>
        /// Exposes a panel/device's joins to the debug page under <paramref name="name"/>.
        /// Callable more than once with distinct names; re-registering a name replaces the
        /// device. Safe from any thread and before Register(). The library holds a strong
        /// reference until UnregisterDevice()/program stop — call UnregisterDevice() before
        /// disposing the device.
        /// </summary>
        public static void RegisterDevice(string name, BasicTriList device)
            => Instance.RegisterDeviceInternal(name, device);

        /// <summary>Drops a registered device. Call before disposing it. Returns false if the name was not registered.</summary>
        public static bool UnregisterDevice(string name)
            => _instance?.UnregisterDeviceInternal(name) ?? false;

        /// <summary>Drops every registered device.</summary>
        public static void UnregisterAllDevices()
            => _instance?.UnregisterAllDevicesInternal();

        /// <summary>
        /// Host kill switch for forcing. False makes every watched join read-only regardless
        /// of the page's arm state — the intended production posture. Default true. Not persisted.
        /// </summary>
        public static bool ValueWritesAllowed
        {
            get => Instance._writesAllowed;
            set => Instance._writesAllowed = value;
        }

        /// <summary>
        /// Server-held arm gate for forcing; writes are refused unless true. Defaults to FALSE
        /// at every program start, is never persisted, and auto-disarms after
        /// <see cref="ValueArmTimeoutMs"/> of no force activity. Toggleable from the page.
        /// </summary>
        public static bool ValueWritesArmed
        {
            get { var i = Instance; i.CheckArmIdle(); return i._armed; }
            set { var i = Instance; i._armed = value; i._lastArmActivityMs = Environment.TickCount64; }
        }

        /// <summary>Idle timeout for the arm gate, in ms. Default 300000 (5 min). &lt;= 0 disables auto-disarm.</summary>
        public static int ValueArmTimeoutMs
        {
            get => Instance._armTimeoutMs;
            set => Instance._armTimeoutMs = value;
        }

        // Lazy idle-disarm: checked whenever the arm state is consulted, instead of burning a
        // CTimer on a purely reactive condition. Consequence: the page discovers the disarm on
        // its next poll rather than at the exact timeout — a one-poll lag that fails safe
        // (writes are already refused the moment the window lapses).
        private void CheckArmIdle()
        {
            if (_armed && _armTimeoutMs > 0 &&
                Environment.TickCount64 - _lastArmActivityMs > _armTimeoutMs)
            {
                _armed = false;
                AppendAudit("Notice", "Force arm timed out (idle)");
            }
        }

        // Audit entries record mutations to a live system, so they bypass BOTH the master
        // logging switch and the MinLevel floor — a force that isn't recorded shouldn't be
        // possible. They land in the normal ring buffer: searchable, exportable, persisted.
        private void AppendAudit(string level, string message)
        {
            var ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            lock (_lock)
            {
                _log.Add(new LogEntry(level, message, ts, ++_seq));
                var chunk = Math.Max(1, _maxEntries / 10);
                if (_log.Count >= _maxEntries + chunk) _log.RemoveRange(0, _log.Count - _maxEntries);
                _dirty = true;
            }
        }

        // Best-effort client address for the audit line; availability differs by platform.
        private static string ClientAddress(HttpCwsContext context)
        {
            try { return context.Request.UserHostAddress ?? "unknown"; }
            catch { return "unknown"; }
        }

        // POST api/join?dev=<name>&k=<key>&v=<value> — forces one watched join.
        // Digital: high|low|1|0|true|false|on|off|toggle|pulse. Analog: 0-65535 (invariant).
        // Serial: base64url ("b64:<data>"). The hardware probe (finding documented above
        // ProcessRequest) showed CWS URL-decodes query values ONCE, including '+' -> space —
        // so raw text would survive a correctly-encoding client, but base64url is kept as the
        // single hardcoded path: its alphabet contains no '+', '%' or spaces, so it is immune
        // to the decode entirely and to clients that forget to percent-encode.
        private void WriteJoin(HttpCwsContext context)
        {
            string? devName = null, k = null, v = null, label = null;
            try
            {
                devName = context.Request.QueryString["dev"];
                k       = context.Request.QueryString["k"];
                v       = context.Request.QueryString["v"];
                label   = context.Request.QueryString["n"];   // optional friendly name, audit only
            }
            catch { /* malformed request — caught by the null checks below */ }

            // The friendly name is page-supplied free text destined for the log: cap it and
            // strip control characters, but otherwise trust QueryString's single decode.
            if (!string.IsNullOrEmpty(label))
            {
                if (label.Length > 64) label = label.Substring(0, 64);
                var lb = new StringBuilder(label.Length);
                foreach (var c in label) if (c >= ' ') lb.Append(c);
                label = lb.ToString();
            }

            if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(v))
            { WriteForceError(context, 400, "missing k or v", devName, k, v); return; }

            // Any force ATTEMPT proves a human is at the page, so it refreshes the arm
            // window before the gates run — a rejected attempt (bad value, read-only join)
            // must not let the timeout expire under an actively-working user.
            _lastArmActivityMs = Environment.TickCount64;

            if (!_writesAllowed)
            { WriteForceError(context, 403, "writes disabled by host", devName, k, v); return; }
            CheckArmIdle();
            if (!_armed)
            { WriteForceError(context, 403, "writes not armed", devName, k, v); return; }

            // Rate limit: forces are CIP traffic to a real panel on an unauthenticated endpoint.
            long nowMs = Environment.TickCount64;
            lock (_devLock)
            {
                if (nowMs - _forceWinMs >= 1000) { _forceWinMs = nowMs; _forceCount = 0; }
                if (++_forceCount > MaxForcesPerSecond)
                { WriteForceError(context, 429, "rate limited", devName, k, v); return; }
            }

            // Resolve the key (shared cache with the watch path).
            WatchSlot slot;
            lock (_devLock)
            {
                BasicTriList? device = null;
                if (!string.IsNullOrEmpty(devName)) _devices.TryGetValue(devName, out device);
                else if (_devices.Count == 1) foreach (var kv in _devices) { devName = kv.Key; device = kv.Value; }
                if (device == null)
                { WriteForceError(context, 404, "unknown device", devName, k, v); return; }

                var cacheKey = devName + "|" + k;
                if (!_joinCache.TryGetValue(cacheKey, out slot!))
                {
                    slot = ResolveKey(device, k);
                    _joinCache[cacheKey] = slot;
                }
                slot.LastSeenMs = nowMs;
            }

            if (slot.Error != null)
            { WriteForceError(context, 400, slot.Error, devName, k, v); return; }
            if (slot.ReadOnly)
            { WriteForceError(context, 403, "read-only (output sigs cannot be forced)", devName, k, v); return; }

            _lastArmActivityMs = nowMs;                       // any accepted attempt refreshes the arm window

            // Type-specific parse + write. The sig write itself runs no host code (it drives
            // the device over CIP), so no lock is held around it.
            bool pulsed = false;
            try
            {
                switch (slot.Type)
                {
                    case 'd':
                    {
                        var raw = v.Trim().ToLowerInvariant();
                        bool? target = raw switch
                        {
                            "1" or "true" or "on" or "high" or "y" or "yes" => true,
                            "0" or "false" or "off" or "low" or "n" or "no" => false,
                            "toggle" => !slot.Sig!.BoolValue,
                            _ => null,
                        };
                        if (raw == "pulse")
                        {
                            ((BoolInputSig)slot.Sig!).Pulse();
                            pulsed = true;
                        }
                        else if (target.HasValue) ((BoolInputSig)slot.Sig!).BoolValue = target.Value;
                        else { WriteForceError(context, 400, "bad value for digital: \"" + v + "\"", devName, k, v); return; }
                        break;
                    }
                    case 'a':
                    {
                        if (!ushort.TryParse(v.Trim(), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var num))
                        { WriteForceError(context, 400, "bad value for analog (0-65535): \"" + v + "\"", devName, k, v); return; }
                        ((UShortInputSig)slot.Sig!).UShortValue = num;
                        break;
                    }
                    default:  // 's'
                    {
                        // Serial text travels base64url-encoded ("b64:..."), which uses only
                        // URL-safe characters — so the value is correct regardless of what
                        // URL-decoding CWS applies to query strings. No plain-text fallback:
                        // one unambiguous path beats a decode heuristic.
                        if (!v.StartsWith("b64:", StringComparison.Ordinal))
                        { WriteForceError(context, 400, "serial value must be base64url (\"b64:<data>\")", devName, k, v); return; }
                        string text;
                        try
                        {
                            var b = v.Substring(4).Replace('-', '+').Replace('_', '/');
                            switch (b.Length % 4)
                            {
                                case 2: b += "=="; break;
                                case 3: b += "=";  break;
                                case 1: throw new FormatException();
                            }
                            text = Encoding.UTF8.GetString(Convert.FromBase64String(b));
                        }
                        catch
                        { WriteForceError(context, 400, "bad base64 serial value", devName, k, v); return; }

                        if (text.Length > 1024)
                        { WriteForceError(context, 400, "string exceeds 1024 chars", devName, k, v); return; }
                        foreach (var c in text)
                            if (c < ' ' && c != '\t')
                            { WriteForceError(context, 400, "control characters not allowed", devName, k, v); return; }

                        ((StringInputSig)slot.Sig!).StringValue = text;
                        v = text;                        // audit the readable text, not the base64
                        break;
                    }
                }
            }
            catch (Exception e)
            { WriteForceError(context, 500, "write failed: " + e.GetType().Name, devName, k, v); return; }

            // Read back, update the slot's change tracking so the write is observable at a
            // definite rev, audit, respond.
            string rendered;
            long rev;
            try { rendered = pulsed ? "false" : RenderSlot(slot); }
            catch { rendered = ""; }
            lock (_devLock)
            {
                if (!pulsed && rendered != slot.LastRendered)
                {
                    slot.LastRendered = rendered;
                    slot.Rev = ++_joinRev;
                }
                rev = slot.Rev;
            }

            // Info, not Warning: a force from the debug page is a deliberate commissioning
            // action, not an anomaly. It still bypasses LoggingEnabled/MinLevel via
            // AppendAudit so the record is never lost.
            var what = string.IsNullOrEmpty(label) ? k : label + " (" + k + ")";
            AppendAudit("Info", SafeFormat("Force: {0} {1} -> {2} (from {3})",
                new object[] { devName ?? "?", what, v, ClientAddress(context) }));

            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"k\":").Append(JsonString(k));
            sb.Append(",\"t\":\"").Append(slot.Type).Append('"');
            if (pulsed) sb.Append(",\"pulsed\":true");
            else sb.Append(",\"v\":").Append(rendered);
            sb.Append(",\"rev\":").Append(rev).Append('}');
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // Rejected-force response + audit trail (rejections are recorded too, at Notice).
        private void WriteForceError(HttpCwsContext context, int status, string message,
            string? dev, string? k, string? v)
        {
            AppendAudit("Notice", SafeFormat("Force rejected: {0} {1} -> {2} ({3}) from {4}",
                new object[] { dev ?? "?", k ?? "?", v ?? "?", message, ClientAddress(context) }));
            context.Response.StatusCode  = status;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"ok\":false,\"error\":" + JsonString(message) + "}", true);
        }

        // api/arm — GET reads, POST?on=1|0 sets the arm gate. Arm changes are audited.
        private void ServeArm(HttpCwsContext context, string method)
        {
            // ?touch=1 — the page reports genuine user interaction (click/keystroke in the
            // watch panel, throttled). Polling deliberately does NOT count: the idle timeout
            // exists to disarm a page someone walked away from, and a poll keeps running
            // whether or not anyone is there.
            try
            {
                if (context.Request.QueryString["touch"] == "1" && _armed)
                    _lastArmActivityMs = Environment.TickCount64;
            }
            catch { }

            if (method == "POST")
            {
                bool? on = null;
                try
                {
                    var raw = context.Request.QueryString["on"];
                    if (!string.IsNullOrEmpty(raw)) on = raw == "1";
                }
                catch { /* leave unchanged */ }
                if (on.HasValue)
                {
                    _armed = on.Value;
                    _lastArmActivityMs = Environment.TickCount64;
                    AppendAudit("Notice", SafeFormat("Force arm {0} (from {1})",
                        new object[] { on.Value ? "ENABLED" : "disabled", ClientAddress(context) }));
                }
            }
            CheckArmIdle();
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"armed\":" + (_armed ? "true" : "false") +
                ",\"writesAllowed\":" + (_writesAllowed ? "true" : "false") +
                ",\"timeoutMs\":" + _armTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}", true);
        }

        private void RegisterDeviceInternal(string name, BasicTriList device)
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { ':', ',', '/' }) >= 0)
            {
                ErrorLog.Error("CwsDebugPage: invalid device name \"{0}\" (empty or contains ':' ',' '/')", name ?? "(null)");
                return;
            }
            if (device == null)
            {
                ErrorLog.Error("CwsDebugPage: RegisterDevice(\"{0}\") called with a null device", name);
                return;
            }
            lock (_devLock)
            {
                if (_devices.TryGetValue(name, out var old)) // replacement: unhook + invalidate
                {
                    UnhookLocked(name, old);
                    DropCacheFor(name);
                }
                _devices[name] = device;

                // Subscribe to the device's SigChange so output transitions (button presses,
                // pulses) are observed the moment they happen rather than sampled at the poll
                // cadence — a press+release inside one 2 s window would otherwise be invisible.
                // The handler runs no host code and only touches _devLock-guarded state.
                SigEventHandler hook = (_, args) => OnDeviceSigChange(name, args);
                try { device.SigChange += hook; _devHooks[name] = hook; }
                catch { /* device torn down mid-registration — watch still works, pulses degrade to sampling */ }

                // Smart objects raise their OWN SigChange (device-level SigChange does not
                // cover them), so each gets its own hook or SO activity lamps never light.
                // NOTE: smart objects must already be loaded (LoadSmartObjects) when the
                // device is registered — register the device after loading the SGD.
                if (device is BasicTriListWithSmartObject { SmartObjects: not null } soDev)
                {
                    var hooks = new List<KeyValuePair<SmartObject, SmartObjectSigChangeEventHandler>>();
                    try
                    {
                        foreach (var kv in soDev.SmartObjects)
                        {
                            var so = kv.Value;
                            if (so == null) continue;
                            SmartObjectSigChangeEventHandler soHook = (_, args) => MarkSigActivity(name, args.Sig);
                            so.SigChange += soHook;
                            hooks.Add(new KeyValuePair<SmartObject, SmartObjectSigChangeEventHandler>(so, soHook));
                        }
                    }
                    catch { /* partial subscription is fine; unhook handles what was added */ }
                    if (hooks.Count > 0) _soHooks[name] = hooks;
                }
            }
            Append("Notice", SafeFormat("Device \"{0}\" registered for signals", new object[] { name }), Array.Empty<object>());
        }

        private bool UnregisterDeviceInternal(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            lock (_devLock)
            {
                if (!_devices.TryGetValue(name, out var device)) return false;
                UnhookLocked(name, device);
                _devices.Remove(name);
                DropCacheFor(name);
                return true;
            }
        }

        private void UnregisterAllDevicesInternal()
        {
            lock (_devLock)
            {
                foreach (var kv in _devices) UnhookLocked(kv.Key, kv.Value);
                _devices.Clear();
                _joinCache.Clear();
            }
        }

        // Detaches a device's SigChange hook plus any smart-object hooks. Caller holds _devLock.
        private void UnhookLocked(string name, BasicTriList device)
        {
            if (_devHooks.TryGetValue(name, out var hook))
            {
                try { device.SigChange -= hook; } catch { /* device already disposed */ }
                _devHooks.Remove(name);
            }
            if (_soHooks.TryGetValue(name, out var soHooks))
            {
                foreach (var kv in soHooks)
                {
                    try { kv.Key.SigChange -= kv.Value; } catch { /* already disposed */ }
                }
                _soHooks.Remove(name);
            }
        }

        private void OnDeviceSigChange(string deviceName, SigEventArgs args) => MarkSigActivity(deviceName, args.Sig);

        // Sig-event handler core (device joins and smart-object sigs alike): marks any watched
        // slot resolving to this sig as changed, so the next poll reports it even if the value
        // has already snapped back. For digitals, the activity counter only advances on the
        // RISING edge — a press-and-release is one activity blip on the page, not two — while
        // the rev still bumps on both edges so the state badge tracks correctly. Exceptions
        // must never escape into Crestron's event dispatch.
        private void MarkSigActivity(string deviceName, Sig? sig)
        {
            try
            {
                if (sig == null) return;
                bool countActivity = true;
                try { if (sig.Type == eSigType.Bool) countActivity = sig.BoolValue; } catch { }

                var prefix = deviceName + "|";
                lock (_devLock)
                {
                    foreach (var kv in _joinCache)
                    {
                        if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var slot = kv.Value;
                        if (slot.Sig == null || !ReferenceEquals(slot.Sig, sig)) continue;
                        if (countActivity) slot.Changes++;
                        slot.Rev = ++_joinRev;
                    }
                }
            }
            catch { /* never throw into the device's event dispatch */ }
        }

        // Removes every cached resolution belonging to a device. Caller holds _devLock.
        private void DropCacheFor(string deviceName)
        {
            var prefix = deviceName + "|";
            var stale = new List<string>();
            foreach (var k in _joinCache.Keys)
                if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) stale.Add(k);
            foreach (var k in stale) _joinCache.Remove(k);
        }

        /// <summary>One watched signal: the resolved sig (or a stable resolution error) plus change tracking.</summary>
        private sealed class WatchSlot
        {
            public Sig?    Sig;                  // resolved signal; null when Error is set
            public char    Type;                 // 'd' | 'a' | 's'
            public bool    ReadOnly;             // output-direction sig (device -> program)
            public uint    Number;               // resolved join number (useful for ~name keys)
            public bool    ByName;               // key addressed the sig by SGD name
            public string? Error;                // resolution failure, reported per-row, never retried
            public string  LastRendered = "";    // last value (or error) as rendered into JSON
            public long    Rev;                  // _joinRev at the last observed change
            public long    LastSeenMs;           // for cache eviction
            public int     Changes;              // SigChange count (outputs): makes sub-poll pulses visible
        }

        // Parses and resolves one watch key against a device. Never throws: every failure —
        // malformed key, out-of-range join, missing smart object or sig name — lands in
        // slot.Error, so one bad key degrades to one errored row, never a failed poll.
        // Key grammar (numeric canonical; '~name' addresses smart-object sigs by SGD name):
        //   ("o")? ("d"|"a"|"s") <num>                          e.g. d5, od5, a12, s1
        //   "so" <id> "." ("o")? ("d"|"a"|"s") (<num>|"~"name)  e.g. so1.d3, so1.d~Item 1 Visible
        private static WatchSlot ResolveKey(BasicTriList device, string rawKey)
        {
            var slot = new WatchSlot();
            try
            {
                var key = rawKey.Trim();
                if (key.Length < 2) { slot.Error = "bad key"; return slot; }

                // Optional smart-object scope: "so<id>."
                SmartObject? so = null;
                if ((key[0] == 's' || key[0] == 'S') && key.Length > 2 && (key[1] == 'o' || key[1] == 'O') && char.IsDigit(key[2]))
                {
                    int dot = key.IndexOf('.');
                    if (dot < 3) { slot.Error = "bad key (missing '.' after smart object id)"; return slot; }
                    if (!uint.TryParse(key.Substring(2, dot - 2), System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var soId))
                    { slot.Error = "bad smart object id"; return slot; }

                    if (device is not BasicTriListWithSmartObject soDev)
                    { slot.Error = "device has no smart objects"; return slot; }
                    try { so = soDev.SmartObjects?[soId]; }
                    catch { so = null; }
                    if (so == null) { slot.Error = "no smart object " + soId; return slot; }
                    key = key.Substring(dot + 1);
                    if (key.Length < 2) { slot.Error = "bad key"; return slot; }
                }

                // Optional output direction, then the type character.
                int i = 0;
                bool output = key[0] == 'o' || key[0] == 'O';
                if (output) i++;
                if (i >= key.Length) { slot.Error = "bad key"; return slot; }
                char type = char.ToLowerInvariant(key[i]);
                if (type != 'd' && type != 'a' && type != 's') { slot.Error = "bad key (type must be d, a or s)"; return slot; }
                i++;
                slot.Type = type; slot.ReadOnly = output;

                var addr = key.Substring(i);
                if (addr.Length == 0) { slot.Error = "bad key (missing join number)"; return slot; }

                if (addr[0] == '~')                      // SGD-name addressing: smart objects only
                {
                    if (so == null) { slot.Error = "name addressing requires a smart object (so<id>.)"; return slot; }
                    var name = addr.Substring(1);
                    if (name.Length == 0) { slot.Error = "bad key (empty sig name)"; return slot; }
                    slot.ByName = true;
                    try
                    {
                        slot.Sig = type switch
                        {
                            'd' => output ? so.BooleanOutput[name] : (Sig)so.BooleanInput[name],
                            'a' => output ? so.UShortOutput[name]  : (Sig)so.UShortInput[name],
                            _   => output ? so.StringOutput[name]  : (Sig)so.StringInput[name],
                        };
                    }
                    catch { slot.Sig = null; }
                    if (slot.Sig == null)
                    { slot.Error = "no " + (output ? "output" : "input") + " sig \"" + name + "\" (SGD not loaded?)"; return slot; }
                    slot.Number = slot.Sig.Number;
                    return slot;
                }

                if (!uint.TryParse(addr, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var num) || num == 0)
                { slot.Error = "bad join number"; return slot; }
                slot.Number = num;

                try
                {
                    if (so != null)
                        slot.Sig = type switch
                        {
                            'd' => output ? so.BooleanOutput[num] : (Sig)so.BooleanInput[num],
                            'a' => output ? so.UShortOutput[num]  : (Sig)so.UShortInput[num],
                            _   => output ? so.StringOutput[num]  : (Sig)so.StringInput[num],
                        };
                    else
                        slot.Sig = type switch
                        {
                            'd' => output ? device.BooleanOutput[num] : (Sig)device.BooleanInput[num],
                            'a' => output ? device.UShortOutput[num]  : (Sig)device.UShortInput[num],
                            _   => output ? device.StringOutput[num]  : (Sig)device.StringInput[num],
                        };
                }
                catch { slot.Sig = null; }
                if (slot.Sig == null) { slot.Error = "join out of range"; return slot; }
                return slot;
            }
            catch (Exception e)
            {
                slot.Error = e.GetType().Name;
                return slot;
            }
        }

        // Renders a slot's current value as its JSON token ("true", "42", "\"text\"").
        // The rendered string doubles as the change-detection comparand.
        private static string RenderSlot(WatchSlot slot) => slot.Type switch
        {
            'd' => slot.Sig!.BoolValue ? "true" : "false",
            'a' => slot.Sig!.UShortValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _   => JsonString(slot.Sig!.StringValue ?? ""),
        };

        // GET api/devices — lists registered devices so the page can populate its picker.
        // Every device property read is individually guarded: a device that failed
        // registration must still list (marked so the page can explain dead joins).
        private void ServeDevices(HttpCwsContext context)
        {
            var sb = new StringBuilder();
            sb.Append("{\"devices\":[");
            lock (_devLock)
            {
                bool first = true;
                foreach (var kv in _devices)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    var d = kv.Value;
                    sb.Append("{\"name\":").Append(JsonString(kv.Key));
                    try { sb.Append(",\"type\":").Append(JsonString(d.GetType().Name)); } catch { }
                    try { sb.Append(",\"id\":").Append(d.ID.ToString(System.Globalization.CultureInfo.InvariantCulture)); } catch { }
                    try { sb.Append(",\"online\":").Append(d.IsOnline ? "true" : "false"); } catch { }
                    try { sb.Append(",\"registered\":").Append(d.Registered ? "true" : "false"); } catch { }
                    try
                    {
                        if (d is BasicTriListWithSmartObject { SmartObjects: not null } soDev)
                        {
                            sb.Append(",\"smartObjects\":true,\"soIds\":[");
                            bool f2 = true;
                            foreach (var so in soDev.SmartObjects)
                            {
                                if (!f2) sb.Append(',');
                                f2 = false;
                                sb.Append(so.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                            }
                            sb.Append(']');
                        }
                        else sb.Append(",\"smartObjects\":false");
                    }
                    catch { }
                    sb.Append('}');
                }
            }
            sb.Append("]}");
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // GET api/joins?since=<rev>&dev=<name>&w=<key,key,...> — reads the watched joins.
        // Same cursor contract as api/logs: since <= 0 returns every watched key, otherwise
        // only keys whose value changed since that rev. The page re-polls with since=0
        // whenever its watch set changes. Phase 1: strictly read-only.
        private void ServeJoins(HttpCwsContext context)
        {
            long since = 0;
            string? devName = null, w = null;
            try
            {
                var raw = context.Request.QueryString["since"];
                if (!string.IsNullOrEmpty(raw)) long.TryParse(raw, out since);
                devName = context.Request.QueryString["dev"];
                w       = context.Request.QueryString["w"];
            }
            catch { /* malformed request — treated as empty below */ }

            if (string.IsNullOrEmpty(w))
            { WriteJsonError(context, 400, "missing w"); return; }

            var keys = w.Split(',');
            if (keys.Length > MaxWatchKeys)
            { WriteJsonError(context, 400, "too many watch keys"); return; }

            var sb = new StringBuilder();
            lock (_devLock)
            {
                BasicTriList? device = null;
                if (!string.IsNullOrEmpty(devName)) _devices.TryGetValue(devName, out device);
                else if (_devices.Count == 1) foreach (var kv in _devices) { devName = kv.Key; device = kv.Value; }
                if (device == null)
                { WriteJsonError(context, 404, "unknown device"); return; }

                EvictJoinCache();
                long nowMs = Environment.TickCount64;

                sb.Append("{\"lastRev\":");
                var entries = new StringBuilder();
                bool first = true;
                foreach (var rawKey in keys)
                {
                    var key = rawKey.Trim();
                    if (key.Length == 0) continue;

                    var cacheKey = devName + "|" + key;
                    if (!_joinCache.TryGetValue(cacheKey, out var slot))
                    {
                        slot = ResolveKey(device, key);
                        _joinCache[cacheKey] = slot;
                    }
                    slot.LastSeenMs = nowMs;

                    // Render the current value (or carry the stable resolution error); a sig
                    // read that throws (disposed device) becomes a per-row error, not a 500.
                    string rendered;
                    string? err = slot.Error;
                    if (err == null)
                    {
                        try { rendered = RenderSlot(slot); }
                        catch (Exception e) { err = "read failed: " + e.GetType().Name; rendered = ""; }
                    }
                    else rendered = "";

                    var comparand = err != null ? "!" + err : rendered;
                    if (comparand != slot.LastRendered)
                    {
                        slot.LastRendered = comparand;
                        slot.Rev = ++_joinRev;
                    }

                    if (since > 0 && slot.Rev <= since) continue;   // unchanged — omit

                    if (!first) entries.Append(',');
                    first = false;
                    entries.Append("{\"k\":").Append(JsonString(key));
                    if (err != null)
                        entries.Append(",\"err\":").Append(JsonString(err));
                    else
                    {
                        entries.Append(",\"t\":\"").Append(slot.Type).Append('"');
                        entries.Append(",\"v\":").Append(rendered);
                        if (slot.ReadOnly) entries.Append(",\"ro\":true");
                        if (slot.ByName)
                            entries.Append(",\"n\":").Append(slot.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        if (slot.Changes > 0)
                            entries.Append(",\"chg\":").Append(slot.Changes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    entries.Append(",\"rev\":").Append(slot.Rev);
                    entries.Append('}');
                }
                sb.Append(_joinRev);
                CheckArmIdle();
                sb.Append(",\"armed\":").Append(_armed ? "true" : "false");
                sb.Append(",\"writesAllowed\":").Append(_writesAllowed ? "true" : "false");
                try { sb.Append(",\"online\":").Append(device.IsOnline ? "true" : "false"); } catch { }
                sb.Append(",\"joins\":[").Append(entries).Append("]}");
            }

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // GET api/smartobjects?dev=<name>&id=<so> — lists a smart object's sigs (number +
        // SGD name) per collection, so the page can offer named pickers instead of the user
        // guessing numbers (SO sig numbers do NOT line up with item numbers).
        private void ServeSmartObjects(HttpCwsContext context)
        {
            string? devName = null, idRaw = null;
            try
            {
                devName = context.Request.QueryString["dev"];
                idRaw   = context.Request.QueryString["id"];
            }
            catch { }

            if (!uint.TryParse(idRaw ?? "", System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var soId))
            { WriteJsonError(context, 400, "missing or bad id"); return; }

            SmartObject? so = null;
            lock (_devLock)
            {
                BasicTriList? device = null;
                if (!string.IsNullOrEmpty(devName)) _devices.TryGetValue(devName, out device);
                else if (_devices.Count == 1) foreach (var kv in _devices) { devName = kv.Key; device = kv.Value; }
                if (device is BasicTriListWithSmartObject { SmartObjects: not null } soDev)
                {
                    try { so = soDev.SmartObjects[soId]; } catch { so = null; }
                }
            }
            if (so == null)
            { WriteJsonError(context, 404, "no smart object " + soId); return; }

            var sb = new StringBuilder();
            sb.Append("{\"dev\":").Append(JsonString(devName ?? ""));
            sb.Append(",\"id\":").Append(soId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"sigs\":{");
            AppendSigList(sb, "din",  () => so.BooleanInput);
            sb.Append(',');
            AppendSigList(sb, "dout", () => so.BooleanOutput);
            sb.Append(',');
            AppendSigList(sb, "ain",  () => so.UShortInput);
            sb.Append(',');
            AppendSigList(sb, "aout", () => so.UShortOutput);
            sb.Append(',');
            AppendSigList(sb, "sin",  () => so.StringInput);
            sb.Append(',');
            AppendSigList(sb, "sout", () => so.StringOutput);
            sb.Append("}}");

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // Appends one collection as "key":[{"n":1,"name":"Item 1 Pressed"}, ...]. Any failure
        // (collection null, enumeration throws) yields an empty list rather than a failed response.
        // Non-generic on purpose: the SDK's sig-collection nullability annotations fight a
        // Sig-constrained generic, and a runtime type check is all that's actually needed.
        private static void AppendSigList(StringBuilder sb, string key, Func<System.Collections.IEnumerable?> get)
        {
            sb.Append('"').Append(key).Append("\":[");
            try
            {
                var col = get();
                if (col != null)
                {
                    bool first = true;
                    foreach (var item in col)
                    {
                        if (item is not Sig sig) continue;
                        string name;
                        uint num;
                        try { name = sig.Name ?? ""; num = sig.Number; } catch { continue; }
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"n\":").Append(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"name\":").Append(JsonString(name)).Append('}');
                    }
                }
            }
            catch { /* leave what's emitted so far; the list simply ends */ }
            sb.Append(']');
        }

        // Bounds the resolution cache: when over the cap, entries not seen for
        // JoinCacheEvictMs are dropped. Caller holds _devLock.
        private void EvictJoinCache()
        {
            if (_joinCache.Count <= JoinCacheMax) return;
            long cutoff = Environment.TickCount64 - JoinCacheEvictMs;
            var stale = new List<string>();
            foreach (var kv in _joinCache)
                if (kv.Value.LastSeenMs < cutoff) stale.Add(kv.Key);
            foreach (var k in stale) _joinCache.Remove(k);
        }

        // Small helper for the join endpoints' error responses.
        private static void WriteJsonError(HttpCwsContext context, int status, string message)
        {
            context.Response.StatusCode  = status;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"error\":" + JsonString(message) + "}", true);
        }

        // ── Request routing ──────────────────────────────────────────────────────────
        // CWS invokes this for every request under the registered base path. Routes are
        // matched on the path suffix so they resolve whether the page sits at
        // /cws/debugger.html (VC-4) or /cws/debugger/debugger.html (appliance). The page's
        // JavaScript fetches the api/* endpoints with relative URLs, so they line up either way.
        //
        // QueryString decoding (verified on a 4-series appliance and on VC-4, both platforms
        // identical): HttpCwsContext.Request.QueryString[...] returns values already
        // URL-decoded — standard percent-decoding plus '+' -> space — same as
        // HttpUtility.UrlDecode. %20 and '+' both decode to a space; %25 decodes to a literal
        // '%'; multi-byte percent escapes decode as UTF-8 (%C3%A9 -> the single codepoint 'é',
        // not two raw bytes); a bare '%' not followed by two hex digits is left as-is; '~' needs
        // no encoding either way. So any future query parameter that carries free text or a
        // smart-object signal name (dev/k/v-style joins, etc.) is ALREADY decoded once by CWS —
        // use the value as-is. Do NOT run it through UrlDecode (or any other decoder) again, and
        // do NOT add a "decode if it looks encoded" heuristic: a legitimate forced string that
        // itself contains a literal '%' (sent correctly as %25 by the client) would be corrupted
        // by a second decode pass or by a heuristic guessing it's already-decoded. One hardcoded
        // rule: trust QueryString's value, no extra decoding, ever.

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
            else if (path.EndsWith("api/devices", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeDevices(context);              // registered devices for the watch panel's picker
            else if (path.EndsWith("api/smartobjects", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeSmartObjects(context);         // a smart object's sig names/numbers (discovery)
            else if (path.EndsWith("api/joins", StringComparison.OrdinalIgnoreCase) && method == "GET")
                ServeJoins(context);                // watched join values as JSON
            else if (path.EndsWith("api/join", StringComparison.OrdinalIgnoreCase) && method == "POST")
                WriteJoin(context);                 // force one join (arm-gated, audited)
            else if (path.EndsWith("api/arm", StringComparison.OrdinalIgnoreCase))
                ServeArm(context, method);          // GET reads / POST sets the force arm gate
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
            bool hasDevices;
            lock (_devLock) hasDevices = _devices.Count > 0;
            context.Response.StatusCode  = 200;
            context.Response.ContentType = "text/html";
            context.Response.Write(BuildHtml(_pageTitle, _errorLogAvailable, _loggingEnabled, _maxEntries, hasDevices), true);
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
            // Entries are Seq-ordered, so the incremental case binary-searches for the first
            // entry newer than the cursor instead of scanning the whole buffer.
            List<LogEntry> snapshot;
            long lastSeq;
            lock (_lock)
            {
                if (since > 0)
                {
                    int lo = 0, hi = _log.Count;
                    while (lo < hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (_log[mid].Seq > since) hi = mid; else lo = mid + 1;
                    }
                    snapshot = _log.GetRange(lo, _log.Count - lo);
                }
                else snapshot = new List<LogEntry>(_log);
                lastSeq = _seq;
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

                // Reformat the console timestamp to the application log's format so the page's
                // chronological merge (a string sort on ts) orders both sources correctly.
                // Invariant culture: a culture-sensitive parse could silently swap day/month on
                // an ambiguous console date and mis-sort — worse than keeping it verbatim.
                // An unparseable timestamp is kept verbatim rather than dropped.
                if (DateTime.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    ts = dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
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

        // Minimal JSON string encoder: wraps in quotes and escapes backslash, quote and ALL
        // control characters (U+0000–U+001F). Raw control chars — a tab is the likely one in
        // log text — are illegal inside JSON strings and make the browser's JSON.parse reject
        // the whole response, so every one must be escaped, not just CR/LF.
        private static string JsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

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
                    var hadPreRegister = _log.Count > 0;         // entries logged before Register()

                    var merged = new List<LogEntry>(loaded);
                    merged.AddRange(_log);                       // keep anything logged before Register()
                    if (merged.Count > _maxEntries)
                        merged.RemoveRange(0, merged.Count - _maxEntries);

                    _log.Clear();
                    _seq = 0;
                    foreach (var e in merged)
                        _log.Add(new LogEntry(e.Level, e.Message, e.Timestamp, ++_seq));
                    // Pre-Register() entries have never been written to disk, so the buffer only
                    // matches disk when there were none. Leaving _dirty false here would let those
                    // entries be lost on restart if nothing else ever logged (flushes would no-op).
                    _dirty = hadPreRegister;
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

            // Drop device references so the static-rooted singleton doesn't hold host
            // devices past program stop.
            try { UnregisterAllDevicesInternal(); } catch { /* best-effort */ }
        }

        // ── HTML ─────────────────────────────────────────────────────────────────────

        // Produces the final page by substituting the runtime values (header text and the
        // error-log availability flag) into the static template below.
        private static string BuildHtml(string pageTitle, bool errorLogAvailable, bool loggingEnabled, int maxEntries, bool hasDevices)
        {
            // The title comes from outside the library (room name / assembly name), so encode it —
            // a name containing '<' or '&' must render as text, not be parsed as markup.
            var heading = HtmlEncode(pageTitle + " - Debugger");
            return BuildHtmlTemplate()
                .Replace("__TITLE__", heading)
                .Replace("__VERSION__", Version)
                .Replace("__ERRORLOG__", errorLogAvailable ? "true" : "false")
                .Replace("__LOGGING__", loggingEnabled ? "true" : "false")
                .Replace("__MAXENTRIES__", maxEntries.ToString())
                .Replace("__WATCH__", hasDevices ? "true" : "false");
        }

        // Minimal HTML encoder for values substituted into the page template.
        private static string HtmlEncode(string s) =>
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

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
button{background:#374151;color:#e5e7eb;border:1px solid var(--border);padding:4px 12px;border-radius:3px;cursor:pointer;font:12px 'Courier New',monospace;letter-spacing:1px;text-transform:uppercase;white-space:nowrap}
button:hover{background:#4b5563}
body.light button{background:#e5e7eb;color:#111827;border-color:#9ca3af}
body.light button:hover{background:#d1d5db}
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
.main{flex:1;display:flex;overflow:hidden;min-height:0}
.watch{background:var(--bg2);border-right:1px solid var(--border);flex-shrink:0;width:460px;min-width:280px;display:flex;flex-direction:column;overflow:hidden}
.wsplit{width:5px;cursor:col-resize;background:var(--border);flex-shrink:0}
.wsplit:hover{background:var(--muted)}
.addwatch{display:flex;gap:6px;padding:6px 14px;align-items:center;flex-wrap:wrap;border-bottom:1px solid var(--row-border)}
.addwatch select,.addwatch input{background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:4px 6px;font:12px 'Courier New',monospace;outline:none}
.addwatch input{width:70px}
.wlist{flex:1;overflow-y:auto}
.wrow{display:flex;gap:8px;align-items:baseline;padding:4px 14px;border-bottom:1px solid var(--row-border);line-height:1.5}
.wkey{flex-shrink:0;color:var(--muted);min-width:110px;max-width:160px;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
/* min-width:0 + nowrap: without them a squeezed cell wraps one character per line */
.wval{flex:0 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.werr{flex:0 1 auto;min-width:0;color:var(--bf-fg);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wx{flex-shrink:0;background:transparent;border:1px solid var(--border);color:var(--muted);padding:0 7px;border-radius:2px;cursor:pointer;font:bold 12px 'Courier New',monospace}
.wx:hover{background:#b91c1c;color:#fff;border-color:#b91c1c}
.wempty{color:var(--muted);padding:8px 14px;font-style:italic;font-size:12px}
#armBtn.on{background:#b91c1c;border-color:#b91c1c;color:#fff}
#armBtn.on:hover{background:#991b1b}
.wforce{display:flex;gap:4px;flex-shrink:0}
.wf{font:bold 10px 'Courier New',monospace;letter-spacing:1px;padding:2px 7px;border-radius:2px;cursor:pointer;text-transform:uppercase;border:1px solid var(--border);background:transparent;color:var(--text)}
.wf:hover{background:var(--border)}
.wf.off{opacity:.35;cursor:not-allowed}
.wf.active{background:var(--bn-bg);color:var(--bn-fg);border-color:var(--bn-fg)}
.wnum{width:80px;background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:2px 6px;font:12px 'Courier New',monospace;outline:none}
.wnum.dirty{border-color:var(--bw-fg)}
.wnum.pending{border-color:var(--bn-fg);opacity:.6}
.wferr{flex-shrink:0;color:var(--bf-fg);font-size:11px}
.wlamp{flex-shrink:0;display:inline-block;width:10px;height:10px;border-radius:50%;border:1px solid var(--border);background:transparent;margin-left:6px;vertical-align:middle;transition:background .15s,box-shadow .15s}
.wlamp.on{background:var(--bn-fg);border-color:var(--bn-fg);box-shadow:0 0 6px var(--bn-fg)}
.wtxt{flex:1 1 90px;min-width:60px;background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:2px 6px;font:12px 'Courier New',monospace;outline:none}
.wtxt.dirty{border-color:var(--bw-fg)}
.wtxt.pending{border-color:var(--bn-fg);opacity:.6}
.wkey{cursor:pointer}
</style>
</head>
<body>
<header>
  <div class=""brand""><h1>__TITLE__</h1><span class=""ver"">v__VERSION__</span></div>
  <div class=""controls"">
    <span id=""status"">Loading...</span>
    <button id=""logBtn"" class=""toggle on"" onclick=""toggleLogging()"" title=""Master recording switch on the processor. Off = nothing is stored (persists across restarts)."">Logging On</button>
    <button id=""pauseBtn"" class=""toggle"" onclick=""togglePause()"" title=""Freeze this page. Recording continues on the processor; resuming catches up."">Pause</button>
    <button id=""appBtn""  class=""toggle on"" onclick=""toggleSource('app')"" title=""Show/hide the program's own log messages."">Debugger</button>
    <button id=""procBtn"" class=""toggle"" onclick=""toggleSource('proc')"" title=""Show/hide the processor error log (console 'err')."">Error Log</button>
    <button id=""watchBtn"" class=""toggle"" onclick=""toggleWatch()"" title=""Open the signals panel: view and force device joins."">Signals</button>
    <button id=""themeBtn"" onclick=""toggleTheme()"" title=""Switch between dark and light themes."">Light Mode</button>
    <button id=""exportBtn"" onclick=""exportLog()"" title=""Download the currently visible log entries as a text file."">Export</button>
    <button id=""clearBtn"" onclick=""clearLogs()"" title=""Empty the application log buffer on the processor (error log unaffected)."">Clear</button>
  </div>
</header>
<div class=""searchbar"">
  <input id=""search"" type=""text"" placeholder=""Search messages..."" oninput=""applyFilter()"" title=""Filter displayed log entries by text (display only, nothing is deleted)."">
</div>
<div class=""filterbar"">
  <span class=""flabel"">Levels:</span>
  <button class=""lvl lvl-info on""    data-level=""Info""    onclick=""toggleLevel('Info')"" title=""Show/hide Info entries (display filter only)."">Info</button>
  <button class=""lvl lvl-notice on""  data-level=""Notice""  onclick=""toggleLevel('Notice')"" title=""Show/hide Notice entries (display filter only)."">Notice</button>
  <button class=""lvl lvl-warning on"" data-level=""Warning"" onclick=""toggleLevel('Warning')"" title=""Show/hide Warning entries (display filter only)."">Warning</button>
  <button class=""lvl lvl-error on""   data-level=""Error""   onclick=""toggleLevel('Error')"" title=""Show/hide Error entries (display filter only)."">Error</button>
  <button class=""lvl lvl-fatal on""   data-level=""Fatal""   onclick=""toggleLevel('Fatal')"" title=""Show/hide Fatal entries (display filter only)."">Fatal</button>
</div>
<div class=""main"">
<div class=""watch"" id=""watchPanel"" style=""display:none"">
  <div class=""addwatch"">
    <span class=""flabel"">Add:</span>
    <select id=""awDev"" title=""Which registered device the signal belongs to.""></select>
    <select id=""awType"" onchange=""refreshSigOptions()"" title=""Signal type: Digital = true/false, Analog = 0-65535, Serial = text."">
      <option value=""d"">Digital</option>
      <option value=""a"">Analog</option>
      <option value=""s"">Serial</option>
    </select>
    <select id=""awDir"" title=""Out = to the panel (text/feedback, forceable). In = from the panel (presses, read-only). Both = one row showing both directions of the same join."" onchange=""refreshSigOptions()"">
      <option value=""b"" selected>Both</option>
      <option value="""">Out</option>
      <option value=""o"">In</option>
    </select>
    <input id=""awSo"" type=""number"" min=""1"" placeholder=""SO#"" title=""Smart Object ID — leave blank for a panel join"" oninput=""refreshSigOptions()"">
    <select id=""awSig"" style=""display:none;max-width:220px"" title=""Signal inside the smart object, by SGD name"" onchange=""syncJoinBox()""></select>
    <input id=""awJoin"" type=""number"" min=""1"" placeholder=""Join #"" title=""Join number (not needed when a named signal is picked)."">
    <input id=""awName"" type=""text"" placeholder=""Name (opt)"" style=""width:120px"" title=""Optional friendly name shown on the row and in force log entries."">
    <button onclick=""addWatch()"" title=""Add this signal to the list."">+ Add</button>
    <span style=""flex:1""></span>
    <button id=""armBtn"" class=""toggle"" onclick=""toggleArm()"" title=""Safety gate for forcing values. Resets to Disarmed on every page load and auto-disarms after 5 idle minutes."">Disarmed</button>
  </div>
  <div class=""wlist"" id=""wlist""></div>
</div>
<div class=""wsplit"" id=""wsplit"" style=""display:none"" onmousedown=""splitDown(event)"" title=""Drag to resize""></div>
<div class=""log"" id=""log""></div>
</div>
<script>
var debuggerEntries=[];
var processorEntries=[];
var showApp=true, showProc=false;
var lastSeq=0;                 // highest server sequence id we have fetched (incremental polling)
var paused=false, pollTimer=null;
var ERRORLOG_AVAILABLE=__ERRORLOG__;
var LOGGING_ENABLED=__LOGGING__;
var CLIENT_MAX=__MAXENTRIES__;   // client display cap, matched to the server ring size
var WATCH_AVAILABLE=__WATCH__;   // true when the host registered at least one device
var watchOpen=false;
var watchList=[];                // [{dev,key}] in display order, persisted to localStorage
var joinVals={};                 // key -> last response entry for that key
var joinRev=0;                   // api/joins cursor; reset to 0 whenever the watch set changes
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
  // the APP/SYS source tag only means anything when both sources are shown together
  var showSrc=showApp&&showProc;
  entries.forEach(function(e){
    var row=document.createElement('div');
    row.className='entry';
    var cls=BADGE[e.level]||'badge-info';
    var proc=e.source==='Processor';
    row.innerHTML=(showSrc?'<span class=""src '+(proc?'src-proc':'src-app')+'"">'+(proc?'SYS':'APP')+'</span>':'')+
                  '<span class=""badge '+cls+'"">'+esc(e.level)+'</span>'+
                  '<span class=""msg"">'+esc(e.msg)+'</span>'+
                  (e.ts?'<span class=""ts"">'+esc(e.ts)+'</span>':'');
    log.appendChild(row);
  });
  if(atBottom)log.scrollTop=log.scrollHeight;
}
function applyFilter(){
  render(visibleEntries());
  renderWatch();
}
var armed=false, writesAllowed=true;
var editingWatch=null;    // key of an editor mid-edit; blocks re-render so polls can't clobber typing
var wflash={};            // id -> flash-until timestamp: activity lamp for In-side digitals
function badgeHtml(j,tip){
  return '<span class=""badge '+(j.v?'badge-notice':'badge-info')+'""'+(tip?' title=""'+tip+'""':'')+'>'+
         (j.v?'TRUE':'FALSE')+'</span>';
}
// Lit for as long as the signal is high (a held button stays lit), and flashed for 1s by
// a transition — which is what makes a press-and-release between two polls visible at all.
function lampHtml(id,j){
  var on=(j&&j.v===true)||(wflash[id]&&wflash[id]>Date.now());
  return '<span class=""wlamp'+(on?' on':'')+'"" title=""Lit while the signal is high; flashes on a press too quick to sample""></span>';
}
// fb (optional) is the join's own current value: the matching High/Low button is
// highlighted so the feedback state stays visible without a second TRUE/FALSE badge.
function forceBtnsHtml(dev,key,fb){
  var on=armed&&writesAllowed;
  var cls=on?'wf':'wf off';
  var lock=on?'':' (arm forcing first)';
  var hi=(fb&&fb.v===true)?' active':'', lo=(fb&&fb.v===false)?' active':'';
  return '<span class=""wforce"">'+
     '<button class=""'+cls+hi+'"" title=""Force this join high/true'+lock+'"" onclick=""forceJoin(\''+escAttr(dev)+'\',\''+escAttr(key)+'\',\'high\')"">High</button>'+
     '<button class=""'+cls+lo+'"" title=""Force this join low/false'+lock+'"" onclick=""forceJoin(\''+escAttr(dev)+'\',\''+escAttr(key)+'\',\'low\')"">Low</button>'+
     '<button class=""'+cls+'"" title=""Momentary: high then immediately low'+lock+'"" onclick=""forceJoin(\''+escAttr(dev)+'\',\''+escAttr(key)+'\',\'pulse\')"">Pulse</button>'+
     '</span>';
}
function editorHtml(e,j,key){
  var canForce=armed&&writesAllowed;
  var num=j.t==='a';
  return '<input class=""'+(num?'wnum':'wtxt')+'"" type=""'+(num?'number':'text')+'""'+
     (num?' min=""0"" max=""65535""':'')+
     ' title=""'+(canForce?'Type a value and press Enter to force it. Esc cancels.':'Read-only until forcing is armed.')+'""'+
     ' value=""'+escq(j.v)+'""'+
     (canForce?'':' readonly')+
     ' onfocus=""editingWatch=\''+escAttr(e.dev+'|'+key)+'\'""'+
     ' onblur=""editingWatch=null;renderWatch()""'+
     ' oninput=""this.classList.add(\'dirty\')""'+
     ' onkeydown=""'+(num?'analogKey':'serialKey')+'(event,\''+escAttr(e.dev)+'\',\''+escAttr(key)+'\',this)"">';
}
// Renders one direction's value/editor cell. Digitals: one badge; In-side gets the
// activity lamp instead of a counter. Analog/serial: value or editor, no counters.
function watchCell(e,j,key){
  if(!j)return '<span class=""wval"" style=""color:var(--muted)"">&mdash;</span>';
  if(j.err)return '<span class=""werr"">'+esc(j.err)+'</span>';
  var h='';
  if(j.t==='d'){
    h+=badgeHtml(j)+lampHtml(e.dev+'|'+key,j);
    if(!j.ro)h+=forceBtnsHtml(e.dev,key,j);
  }else if(j.ro){
    h+='<span class=""wval"" title=""'+escq(j.v)+'"">'+esc(String(j.v))+'</span>';
  }else{
    h+=editorHtml(e,j,key);
  }
  if(j.ferr)h+='<span class=""wferr"">'+esc(j.ferr)+'</span>';
  return h;
}
function renderWatch(){
  if(!watchOpen)return;
  if(editingWatch)return;  // an editor has focus — defer until commit/blur
  var el=document.getElementById('wlist');
  el.innerHTML='';
  if(!watchList.length){el.innerHTML='<div class=""wempty"">No signals added. Add one above.</div>';return;}
  watchList.forEach(function(e){
    var j=joinVals[e.dev+'|'+e.key];
    var row=document.createElement('div');
    row.className='wrow';
    var label=e.label||(e.dev+' '+e.key);
    var h='<span class=""wkey"" title=""'+escq(e.dev+' '+e.key+(e.pair?' + '+oKey(e.key):'')+' — double-click to rename')+'""'+
          ' ondblclick=""renameWatch(\''+escAttr(e.dev)+'\',\''+escAttr(e.key)+'\')"">'+esc(label)+'</span>';
    if(e.pair){
      var jo=joinVals[e.dev+'|'+oKey(e.key)];
      if(j&&!j.err&&j.t==='d'){
        // Paired digital: ONE state indicator — the Out (feedback) side, which is what the
        // force buttons drive and what lights on the panel — plus the activity lamp, which
        // blinks on In-side (press) activity.
        // Badge and lamp describe the SAME signal (the panel press) so they never disagree;
        // the badge is the sampled value, so a pulse flashes the lamp without flipping it.
        // Feedback state shows as the highlighted High/Low button instead.
        h+=forceBtnsHtml(e.dev,e.key,j);
        if(jo&&!jo.err)h+=badgeHtml(jo,'Panel press state')+lampHtml(e.dev+'|'+oKey(e.key),jo);
        else if(jo&&jo.err)h+='<span class=""werr"">'+esc(jo.err)+'</span>';
        else h+='<span class=""wval"" style=""color:var(--muted)"">&mdash;</span>';
        if(j.ferr)h+='<span class=""wferr"">'+esc(j.ferr)+'</span>';
      }else{
        // No direction labels: the editable cell is always the to-panel (Out) side and the
        // plain-text cell the from-panel (In) side; the row tooltip carries both keys.
        h+=watchCell(e,j,e.key);
        h+=watchCell(e,jo,oKey(e.key));
      }
    }else{
      h+=watchCell(e,j,e.key);
    }
    h+='<span style=""flex:1""></span>';
    h+='<button class=""wx"" title=""Remove this signal from the list."" onclick=""removeWatch(\''+escAttr(e.dev)+'\',\''+escAttr(e.key)+'\')"">&times;</button>';
    row.innerHTML=h;
    el.appendChild(row);
  });
}
// The Join # box is only needed when no named signal is chosen — with a name picked the
// number is redundant (and ignored), so hide it to remove the ambiguity.
function syncJoinBox(){
  var sel=document.getElementById('awSig');
  var named=sel.style.display!=='none'&&sel.value;
  document.getElementById('awJoin').style.display=named?'none':'';
}
var soCache={};           // ""dev|id"" -> discovery response; refreshed per panel session
function refreshSigOptions(){
  var so=document.getElementById('awSo').value.trim();
  var sel=document.getElementById('awSig');
  var dirSel=document.getElementById('awDir');
  var bothOpt=dirSel.options[0];
  if(!so){
    if(bothOpt&&bothOpt.value==='b'){bothOpt.disabled=false;bothOpt.textContent='Both';}
    sel.style.display='none';sel.innerHTML='';syncJoinBox();return;
  }
  var dev=document.getElementById('awDev').value;
  var ck=dev+'|'+so;
  if(soCache[ck]){fillSigOptions(soCache[ck]);return;}
  fetch('api/smartobjects?dev='+encodeURIComponent(dev)+'&id='+encodeURIComponent(so))
    .then(function(r){return r.json();}).then(function(d){
      if(d.sigs){soCache[ck]=d;fillSigOptions(d);}
      else{sel.style.display='none';syncJoinBox();}
    }).catch(function(){sel.style.display='none';syncJoinBox();});
}
// Whether a smart object's two directions can pair depends on the OBJECT, and discovery
// tells us: some partition one number sequence across directions (Button List — analog
// 1,2,3,6 in / 4,5 out, so no number exists on both sides), others mirror it (Subpage
// Reference List — serial 12 is ""text-o1"" to the panel AND ""text-i1"" back). Both is
// offered only for numbers that genuinely exist in both directions.
function fillSigOptions(d){
  var sel=document.getElementById('awSig');
  var dirSel=document.getElementById('awDir');
  var bothOpt=dirSel.options[0];
  var type=document.getElementById('awType').value;
  var ins=(d.sigs[{d:'din',a:'ain',s:'sin'}[type]])||[];      // to-panel  (UI ""Out"")
  var outs=(d.sigs[{d:'dout',a:'aout',s:'sout'}[type]])||[];  // from-panel (UI ""In"")
  var outByNum={};
  outs.forEach(function(s){outByNum[s.n]=s.name||('#'+s.n);});
  var pairable=ins.filter(function(s){return outByNum.hasOwnProperty(s.n);});

  if(bothOpt&&bothOpt.value==='b'){
    if(pairable.length){bothOpt.disabled=false;bothOpt.textContent='Both';}
    else{
      bothOpt.disabled=true;bothOpt.textContent='Both (not paired)';
      if(dirSel.value==='b')dirSel.value='';
    }
  }
  var dir=dirSel.value;
  var list=[];
  if(dir==='b'){
    // Paired rows address the SHARED NUMBER: the two halves have different names, so a
    // name can only ever resolve one side of the pair.
    list=pairable.map(function(s){
      return {v:String(s.n),t:(s.name||('#'+s.n))+' / '+outByNum[s.n]+' ('+s.n+')'};
    });
  }else{
    list=(dir===''?ins:outs).filter(function(s){return s.name;})
      .map(function(s){return {v:s.name,t:s.name+' ('+s.n+')'};});
  }
  sel.innerHTML='';
  // Nothing of this type/direction on the object: hide the picker rather than offer an
  // empty one (its absence then means ""this object has none of those"").
  if(!list.length){sel.style.display='none';syncJoinBox();return;}
  list.forEach(function(o){
    var e=document.createElement('option');
    e.value=o.v;e.textContent=o.t;
    sel.appendChild(e);
  });
  // Escape hatch last, for sigs the SGD didn't name — selecting it brings the Join # box back.
  var o0=document.createElement('option');
  o0.value='';o0.textContent='(use Join # instead)';
  sel.appendChild(o0);
  sel.selectedIndex=0;            // default to a real named sig
  sel.style.display='';
  syncJoinBox();
}
function setDevOnline(dev,online){
  var sel=document.getElementById('awDev');
  for(var i=0;i<sel.options.length;i++){
    var o=sel.options[i];
    if(o.value===dev)o.textContent=dev+(online?'':' (offline)');
  }
}
function b64url(s){
  return btoa(unescape(encodeURIComponent(s))).replace(/\+/g,'-').replace(/\//g,'_').replace(/=+$/,'');
}
function serialKey(ev,dev,key,input){
  if(ev.key==='Escape'){editingWatch=null;input.blur();return;}
  if(ev.key!=='Enter')return;
  input.classList.remove('dirty');input.classList.add('pending');input.readOnly=true;
  editingWatch=null;
  forceJoin(dev,key,'b64:'+b64url(input.value));
  input.blur();
}
var lastTouch=0;
// Report real user interaction so the server's idle arm-timeout resets. Throttled to one
// request per 30s; only sent while armed, and only for genuine input events (never polls).
function touchArm(){
  if(!armed)return;
  var now=Date.now();
  if(now-lastTouch<30000)return;
  lastTouch=now;
  fetch('api/arm?touch=1').then(function(r){return r.json();}).then(function(d){
    syncArm(!!d.armed,d.writesAllowed!==false);
  }).catch(function(){});
}
function syncArm(a,w){
  if(a===armed&&w===writesAllowed)return;
  if(armed&&!a&&w)setStatus('Force arm timed out — press Disarmed to re-arm');
  armed=a;writesAllowed=w;
  var b=document.getElementById('armBtn');
  b.textContent=!w?'Writes Off':(a?'ARMED':'Disarmed');
  b.classList.toggle('on',a&&w);
  renderWatch();
}
function toggleArm(){
  if(!writesAllowed){setStatus('Forcing disabled by the program');return;}
  fetch('api/arm?on='+(armed?'0':'1'),{method:'POST'}).then(function(r){return r.json();}).then(function(d){
    syncArm(!!d.armed,d.writesAllowed!==false);
  }).catch(function(){});
}
function setStatus(msg){document.getElementById('status').textContent=msg;}
function forceJoin(dev,key,val){
  if(!(armed&&writesAllowed)){setStatus('Arm forcing first (Disarmed button)');return;}
  var id=dev+'|'+key;
  var lbl=watchLabel(dev,key);
  fetch('api/join?dev='+encodeURIComponent(dev)+'&k='+encodeURIComponent(key)+'&v='+encodeURIComponent(val)+(lbl?'&n='+encodeURIComponent(lbl):''),{method:'POST'})
    .then(function(r){return r.json();}).then(function(d){
      var j=joinVals[id];
      if(d.ok){
        if(j&&!d.pulsed){j.v=d.v;delete j.ferr;}
        renderWatch();
      }else if(j){
        j.ferr=d.error||'rejected';
        renderWatch();
        setTimeout(function(){if(joinVals[id]===j){delete j.ferr;renderWatch();}},4000);
      }
    }).catch(function(){
      setStatus('Force failed: connection error');
      renderWatch();   // rebuild the row so a pending/readonly editor never sticks
    });
}
function analogKey(ev,dev,key,input){
  if(ev.key==='Escape'){editingWatch=null;input.blur();return;}
  if(ev.key!=='Enter')return;
  var raw=input.value.trim();
  var n=parseInt(raw,10);
  if(!(n>=0&&n<=65535)){input.classList.add('dirty');return;}
  input.classList.remove('dirty');input.classList.add('pending');input.readOnly=true;
  editingWatch=null;
  forceJoin(dev,key,String(n));
  input.blur();
}
function escAttr(s){return String(s).replace(/\\/g,'\\\\').replace(/'/g,'\\\'').replace(/""/g,'&quot;').replace(/</g,'&lt;');}
// plain HTML-attribute escaping (value="", title="") — escAttr is ONLY for JS strings inside handlers
function escq(s){return esc(String(s)).replace(/""/g,'&quot;');}
function saveWatch(){try{localStorage.setItem('dbg-watchlist',JSON.stringify(watchList));}catch(x){}}
// UI direction convention (system-centric, per user preference): ""Out"" = the program's
// output TO the panel (API InputSig — text/feedback, forceable); ""In"" = coming in FROM
// the panel (API OutputSig — presses, read-only). The wire keys keep the API's 'o' prefix.
function oKey(k){
  // d5 -> od5, so1.d3 -> so1.od3 : the from-panel twin of a to-panel key
  var i=k.indexOf('.');
  return i>=0?k.substring(0,i+1)+'o'+k.substring(i+1):'o'+k;
}
function addWatch(){
  var dev=document.getElementById('awDev').value;
  var type=document.getElementById('awType').value;
  var dir=document.getElementById('awDir').value;
  var so=document.getElementById('awSo').value.trim();
  var join=document.getElementById('awJoin').value.trim();
  var name=document.getElementById('awName').value.trim();
  var sigSel=document.getElementById('awSig');
  var sigVal=(so&&sigSel.style.display!=='none')?sigSel.value:'';
  if(!dev)return;
  if(so&&!(parseInt(so,10)>0))return;
  var pair=dir==='b';
  var addr='', autoName='';
  if(so&&pair){
    // paired SO row: the picker's value is the SHARED NUMBER (see fillSigOptions)
    if(sigVal){addr=sigVal;autoName=sigSel.options[sigSel.selectedIndex].textContent;}
    else if(join&&parseInt(join,10)>0){addr=join;}
    else return;
  }else if(so&&sigVal){
    addr='~'+sigVal;autoName=sigVal;                     // named SO sig
  }else{
    if(!join||!(parseInt(join,10)>0))return;             // plain join number
    addr=join;
  }
  var key=(so?'so'+so+'.':'')+(pair?'':dir)+type+addr;   // pair rows store the to-panel key
  if(!name&&autoName)name=autoName;                      // a picked sig name is its own friendly name
  for(var i=0;i<watchList.length;i++)
    if(watchList[i].dev===dev&&watchList[i].key===key&&!!watchList[i].pair===pair)return;
  var e={dev:dev,key:key};
  if(pair)e.pair=true;
  if(name)e.label=name;
  watchList.push(e);
  document.getElementById('awName').value='';
  saveWatch();
  joinRev=0;               // watch set changed: next poll fetches everything
  renderWatch();
  refresh();
}
function renameWatch(dev,key){
  for(var i=0;i<watchList.length;i++){
    var e=watchList[i];
    if(e.dev===dev&&e.key===key){
      var n=prompt('Name for '+dev+' '+key+':',e.label||'');
      if(n===null)return;
      n=n.trim();
      if(n)e.label=n;else delete e.label;
      saveWatch();renderWatch();
      return;
    }
  }
}
function removeWatch(dev,key){
  watchList=watchList.filter(function(e){return !(e.dev===dev&&e.key===key);});
  delete joinVals[dev+'|'+key];
  saveWatch();
  renderWatch();
}
function toggleWatch(){
  watchOpen=!watchOpen;
  document.getElementById('watchPanel').style.display=watchOpen?'':'none';
  document.getElementById('wsplit').style.display=watchOpen?'':'none';
  document.getElementById('watchBtn').classList.toggle('on',watchOpen);
  try{localStorage.setItem('dbg-watch-open',watchOpen?'1':'0');}catch(x){}
  if(watchOpen){
    loadDevices();
    joinRev=0;             // fresh open: fetch every watched value
    renderWatch();
    refresh();
  }
}
var splitDrag=false;
function splitDown(ev){splitDrag=true;ev.preventDefault();}
function splitEnd(){
  if(!splitDrag)return;
  splitDrag=false;
  try{localStorage.setItem('dbg-watch-w',String(parseInt(document.getElementById('watchPanel').style.width,10)||460));}catch(x){}
}
document.addEventListener('mousemove',function(ev){
  if(!splitDrag)return;
  if(!ev.buttons){splitEnd();return;}   // button released outside the window: end the drag
  ev.preventDefault();                   // no text selection while dragging
  var w=Math.max(280,Math.min(window.innerWidth*0.75,ev.clientX));
  document.getElementById('watchPanel').style.width=w+'px';
});
document.addEventListener('mouseup',splitEnd);
// genuine user input anywhere in the watch panel keeps the arm window alive
(function(){
  var wp=document.getElementById('watchPanel');
  if(!wp)return;
  wp.addEventListener('mousedown',touchArm);
  wp.addEventListener('keydown',touchArm);
})();
function watchLabel(dev,key){
  for(var i=0;i<watchList.length;i++){
    var e=watchList[i];
    if(e.dev===dev&&e.key===key&&e.label)return e.label;
  }
  return '';
}
function loadDevices(){
  // refetched on every panel open (not cached) so the online/offline label stays honest;
  // the user's selection is preserved across the refresh
  fetch('api/devices').then(function(r){return r.json();}).then(function(d){
    var sel=document.getElementById('awDev');
    var prev=sel.value;
    sel.innerHTML='';
    (d.devices||[]).forEach(function(dv){
      var o=document.createElement('option');
      o.value=dv.name;o.textContent=dv.name+(dv.online===false?' (offline)':'');
      sel.appendChild(o);
    });
    if(prev)sel.value=prev;
  }).catch(function(){});
}
var needRender=false;  // persists across polls: set when data changes, cleared only by a render.
                       // If one fetch of a poll succeeds (appending entries / advancing lastSeq)
                       // but a sibling fetch rejects, the flag survives so the NEXT successful
                       // poll paints the stranded entries instead of losing them until new data.
function refresh(){
  var tasks=[fetch('api/logs?since='+lastSeq).then(function(r){return r.json();}).then(function(d){
    if(d.entries&&d.entries.length){
      needRender=true;
      debuggerEntries=debuggerEntries.concat(d.entries);
      // bound client memory to the server ring size; the server already bounds what it sends
      if(debuggerEntries.length>CLIENT_MAX) debuggerEntries=debuggerEntries.slice(debuggerEntries.length-CLIENT_MAX);
    }
    if(typeof d.lastSeq==='number') lastSeq=d.lastSeq;
  })];
  if(showProc) tasks.push(fetch('api/errorlog').then(function(r){return r.json();}).then(function(d){
    var next=d.entries||[];
    // content compare, not length: the console error log is a bounded ring, so once full a
    // new entry displaces the oldest and the line count stays identical. The log is small
    // (bounded console buffer), so a stringify compare per poll is cheap and bulletproof.
    if(JSON.stringify(next)!==JSON.stringify(processorEntries)) needRender=true;
    processorEntries=next;
  }));
  if(watchOpen&&watchList.length){
    // one fetch per watched device (usually one); the ?since cursor means an unchanged
    // watch set with unchanged values costs a ~40-byte response
    var byDev={};
    watchList.forEach(function(e){
      var arr=(byDev[e.dev]=byDev[e.dev]||[]);
      arr.push(e.key);
      if(e.pair)arr.push(oKey(e.key));   // paired rows watch both directions of the join
    });
    Object.keys(byDev).forEach(function(dev){
      tasks.push(fetch('api/joins?since='+joinRev+'&dev='+encodeURIComponent(dev)+'&w='+encodeURIComponent(byDev[dev].join(','))).then(function(r){return r.json();}).then(function(d){
        if(d.joins&&d.joins.length){
          var flashed=false;
          d.joins.forEach(function(j){
            var id=dev+'|'+j.k;
            var old=joinVals[id];
            // chg counts every transition server-side (press AND release both fire); the UI
            // shows it as a 1s activity lamp rather than a number
            if(j.chg&&(!old||j.chg>(old.chg||0))){wflash[id]=Date.now()+1000;flashed=true;}
            joinVals[id]=j;
            // If this row's value changed WHILE its editor is focused, the live change wins:
            // overwrite the box in place (renders are blocked during edits, so without this
            // the box goes stale and Enter would force an outdated value back over a fresh
            // panel change, e.g. a slider move).
            if(id===editingWatch&&old&&!j.err&&String(old.v)!==String(j.v)){
              var ae=document.activeElement;
              if(ae&&(ae.className.indexOf('wnum')>=0||ae.className.indexOf('wtxt')>=0)){
                ae.value=j.v;
                ae.classList.remove('dirty');
              }
            }
          });
          needRender=true;
          if(flashed)setTimeout(function(){renderWatch();},1100);   // turn the lamp back off
        }
        if(typeof d.lastRev==='number'&&d.lastRev>joinRev) joinRev=d.lastRev;
        if(typeof d.armed==='boolean') syncArm(d.armed,d.writesAllowed!==false);
        if(typeof d.online==='boolean') setDevOnline(dev,d.online);
      }));
    });
  }
  Promise.all(tasks).then(function(){
    if(needRender){applyFilter();needRender=false;}
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
  applyFilter();   // re-render now with the new source mix; refresh() only renders on new data
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
  if(!WATCH_AVAILABLE){
    var wb=document.getElementById('watchBtn');
    if(wb) wb.style.display='none';
  }else{
    try{
      var wl=localStorage.getItem('dbg-watchlist');
      if(wl){var arr=JSON.parse(wl);if(Object.prototype.toString.call(arr)==='[object Array]')
        watchList=arr.filter(function(e){return e&&typeof e.dev==='string'&&typeof e.key==='string';});}
      var ww=parseInt(localStorage.getItem('dbg-watch-w')||'',10);
      if(ww>=280)document.getElementById('watchPanel').style.width=ww+'px';
      if(localStorage.getItem('dbg-watch-open')==='1'){
        watchOpen=true;
        document.getElementById('watchPanel').style.display='';
        document.getElementById('wsplit').style.display='';
        document.getElementById('watchBtn').classList.add('on');
        loadDevices();
      }
    }catch(x){}
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
applyFilter();   // initial paint (shows ""No entries."" until the first poll lands)
refresh();
pollTimer=setInterval(refresh,2000);
</script>
</body>
</html>";
    }
}
