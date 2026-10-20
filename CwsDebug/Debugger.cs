// -----------------------------------------------------------------------------
//  CwsDebug — Self-contained CWS debug log page for Crestron SIMPL#Pro programs.
//
//  Author:   Mark Jervis
//  Company:  Stage Front Presentation Systems
//  Version:  1.7.0
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
using System.Text.Json;             // reading a Construct .cse2j contract manifest
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
        private int       _flushing;                         // 0/1 guard: one FlushToDisk at a time (see FlushToDisk)
        private string?   _statePath;                        // file storing the _loggingEnabled flag
        private CTimer?   _flushTimer;                        // periodic disk-flush timer; null until Register()
        private bool      _loggingEnabled = true;            // master record switch; persisted across restarts

        private int _maxEntries = DefaultMaxEntries;        // ring cap; overridable via Register(maxEntries:)

        // ── Device join watch (Phase 1: read-only) ─────────────────────────────────────
        // Registered devices, addressed by joins from the page (Toolbox-style) — see the
        // "Device join watch" region below. _devLock guards these dictionaries.
        //
        // LOCK ORDERING INVARIANT: code holding _lock must NEVER take _devLock — that inversion
        // would deadlock. _lock-holding code (Append/flush/serve-logs) never touches device
        // state; keep it that way. The reverse direction is avoided too: the join paths build
        // their log lines and error replies while holding _devLock but EMIT them after releasing
        // it (see MarkSigActivity, ServeJoins, WriteJoin), so in practice the two locks are never
        // held at once in either order.
        //
        // Related, and easy to get wrong: do not call into the SDK's event accessors
        // (SigChange +=/-=) while holding _devLock. The SDK holds its own event lock while
        // dispatching to handlers, and those handlers call MarkSigActivity, which takes _devLock —
        // so subscribing under _devLock takes the pair in the opposite order from the dispatch
        // path. Register/unregister therefore queue that work and run it after the lock is
        // released (see UnhookLocked / RunDeferred).
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

        // Driving the Out direction (out of the panel) resolves one SDK method per output-sig
        // collection type and caches it here; a null cached value means that type could not be
        // resolved on this SDK build, so those controls hide themselves rather than throwing.
        // Guarded by _injectGate. See the Out-direction region for the full rationale.
        private readonly object _injectGate = new();
        private readonly Dictionary<Type, System.Reflection.MethodInfo?> _injectMethods = new();
        private System.Reflection.MethodInfo? _serialInject;   // the 4-arg string overload, resolved by scan
        private object? _serialEnc, _serialMode;               // boxed enum args for the serial overload
        private bool _serialProbed;

        // Construct/CH5 contract support: a device registered via RegisterContract additionally
        // gets an entry here, mapping the manifest's dotted item paths to the underlying smart-
        // object join. Guarded by _devLock, same as _devices. Never populated for a plain
        // RegisterDevice() call, so "c~" keys simply fail to resolve against such a device.
        private readonly Dictionary<string, Dictionary<string, ContractItem>> _contracts = new(StringComparer.OrdinalIgnoreCase);

        // Smart-object ID -> friendly label, per device. Purely a display aid for the Add-bar
        // (so "SO 8" can show as "SO 8 (Main.Dpad)") — never consulted by ResolveKey, so a
        // wrong or missing label can never misdirect a watch/force the way item-pairing could.
        // Populated from whichever source is available: a Construct contract's manifest
        // (derived, not authored — see DeriveContractSoNames) or an SGD's own Hint= fields
        // (see ParseSgdSmartObjectNames). Either, both, or neither may be present for a device.
        private readonly Dictionary<string, Dictionary<uint, string>> _soNames = new(StringComparer.OrdinalIgnoreCase);

        // Per-signal friendly names, contract devices only — device name -> SigNameKey() -> leaf
        // name (see DeriveContractNames). Same "display only, never consulted by ResolveKey"
        // guarantee as _soNames.
        private readonly Dictionary<string, Dictionary<string, string>> _sigNames = new(StringComparer.OrdinalIgnoreCase);

        private const int MaxWatchKeys      = 128;          // per-request cap; the watch set is client-supplied
        private const int MaxKeyLen         = 256;          // per-key cap; keys become cache dictionary keys
        private const int MaxBodyLen        = 131072;       // api/joins POST body cap (128 KB)
        private const int ErrorRetryMs      = 5000;         // how often an errored watch key is re-resolved
        private const int JoinCacheMax      = 512;          // resolution-cache bound
        private const int JoinCacheEvictMs  = 300000;       // entries idle this long are evicted (5 min)
        private const int TraceTtlMs        = 15000;        // tracing lapses this long after the page last asked
        private const int TraceMinGapMs     = 100;          // floor between analog/serial trace entries

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
        // Written from CWS request threads and read by the idle check. Accessed only through
        // Interlocked/Volatile: a plain long is NOT guaranteed atomic on a 32-bit runtime (the
        // 4-Series ARM target), and a torn read here would either disarm mid-session or fail to
        // disarm when it should — both bad for something whose whole job is a safety timeout.
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
        // Dev builds carry a 4th number that bumps on every rebuild (1.4.0.1, 1.4.0.16, ...) so
        // the page's own version string proves exactly which build is on the processor. A
        // release drops back to plain x.x.x — bump this to match the csproj's <Version> at
        // release time, same as every other version-bump-in-3-places step.
        private const string Version = "1.7.0";

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
                // Each tick first publishes any CwsConsole Print fragment that has waited ~2 s for a
                // newline (so "Connecting..." shows up), then persists. Guarded so it can't kill the tick.
                _flushTimer = new CTimer(_ =>
                {
                    try { CwsConsole.FlushPending(2000); } catch { /* never let the tick die */ }
                    FlushToDisk();
                }, FlushIntervalMs, FlushIntervalMs);
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

        // Entry point for the CwsErrorLog / CwsConsole compatibility shims (CrestronCompat.cs),
        // which pick the level by name ("Info"/"Notice"/"Warning"/"Error"). Same path as Log*.
        internal static void Record(string level, string message, object?[] args) => Instance.Append(level, message, args);

        // string.Format that never throws back into the caller: a mismatched template/args pair
        // (e.g. LogInfo("{0} {1}", x)) or stray braces fall back to the raw template with the
        // args appended, so a malformed log call still records something rather than crashing
        // the program at its least-expected point — inside a logging statement.
        internal static string SafeFormat(string message, object?[] args)
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
        private void Append(string level, string message, object?[] args)
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
        ///
        /// <paramref name="sgdPath"/> is optional: pass the same .sgd file given to
        /// LoadSmartObjects() and each smart object's design-time name (VT Pro-e's "Hint",
        /// e.g. "Button List" for Smart Object ID=1) is shown next to its number in the
        /// Add-bar, instead of the number alone. Purely a display aid — resolution is still
        /// by number underneath, so a name that can't be parsed just leaves the number
        /// unlabeled rather than blocking registration. May be relative (resolves against the
        /// application directory) or omitted entirely.
        /// </summary>
        public static void RegisterDevice(string name, BasicTriList device, string? sgdPath = null)
            => Instance.RegisterDeviceInternal(name, device, sgdPath);

        /// <summary>Drops a registered device. Call before disposing it. Returns false if the name was not registered.</summary>
        public static bool UnregisterDevice(string name)
            => _instance?.UnregisterDeviceInternal(name) ?? false;

        /// <summary>Drops every registered device.</summary>
        public static void UnregisterAllDevices()
            => _instance?.UnregisterAllDevicesInternal();

        /// <summary>
        /// Like <see cref="RegisterDevice"/>, but also loads a Crestron Construct contract
        /// manifest (the "&lt;Project&gt;.cse2j" file under a Construct project's
        /// "output\contract\mapping\" folder) so the device's joins can additionally be
        /// addressed by their Construct item path (e.g. "Main.B1.Press") instead of only by
        /// raw join number. The host program must ship the .cse2j as content so it exists on
        /// the processor (e.g. a Content/CopyToOutputDirectory entry in the .csproj) — this is
        /// a build artifact of the Construct project, not something SIMPL#Pro copies for you.
        /// <paramref name="cse2jPath"/> may be relative, in which case it resolves against the
        /// application directory.
        ///
        /// If the manifest is missing or unparseable, the device still registers exactly as
        /// RegisterDevice would (numeric/SO addressing keeps working) — only contract-name
        /// addressing is unavailable, and a Warning is written to the log so this is never a
        /// silent failure. Undo with the existing UnregisterDevice(name).
        /// </summary>
        public static void RegisterContract(string name, BasicTriListWithSmartObject device, string cse2jPath)
            => Instance.RegisterContractInternal(name, device, cse2jPath);

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
            set { var i = Instance; i._armed = value; System.Threading.Interlocked.Exchange(ref i._lastArmActivityMs, Environment.TickCount64); }
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
                Environment.TickCount64 - System.Threading.Interlocked.Read(ref _lastArmActivityMs) > _armTimeoutMs)
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
            System.Threading.Interlocked.Exchange(ref _lastArmActivityMs, Environment.TickCount64);

            if (!_writesAllowed)
            { WriteForceError(context, 403, "writes disabled by host", devName, k, v); return; }
            CheckArmIdle();
            if (!_armed)
            { WriteForceError(context, 403, "writes not armed", devName, k, v); return; }

            // Rate limit: forces are CIP traffic to a real panel on an unauthenticated endpoint.
            // NOTE on the two blocks below: every WriteForceError is emitted AFTER the lock is
            // released, never inside it. WriteForceError -> AppendAudit takes _lock, and this
            // file's ordering invariant is one-way (a thread may take _lock holding nothing, but
            // must never take _devLock while holding _lock). Emitting from inside _devLock would
            // invert that, and would also hold the device lock across a socket write — stalling
            // the sig-dispatch thread for the duration of every rejected force.
            long nowMs = Environment.TickCount64;
            bool rateLimited;
            lock (_devLock)
            {
                if (nowMs - _forceWinMs >= 1000) { _forceWinMs = nowMs; _forceCount = 0; }
                rateLimited = ++_forceCount > MaxForcesPerSecond;
            }
            if (rateLimited)
            { WriteForceError(context, 429, "rate limited", devName, k, v); return; }

            // Resolve the key (shared cache with the watch path).
            WatchSlot? slot = null;
            lock (_devLock)
            {
                if (TryResolveDeviceLocked(devName, out var canonName, out var device) && device != null)
                {
                    devName = canonName;                  // cache under the REGISTERED spelling
                    var cacheKey = devName + "|" + k;
                    if (!_joinCache.TryGetValue(cacheKey, out slot!))
                    {
                        _contracts.TryGetValue(devName, out var cmap);
                        slot = ResolveKey(device, k, cmap);
                        slot.ResolvedMs = nowMs;
                        _joinCache[cacheKey] = slot;
                    }
                    slot.LastSeenMs = nowMs;
                }
            }
            if (slot == null)
            { WriteForceError(context, 404, "unknown device", devName, k, v); return; }

            if (slot.Error != null)
            { WriteForceError(context, 400, slot.Error, devName, k, v); return; }
            if (slot.ReadOnly)
            { WriteForceError(context, 403, "read-only (output sigs cannot be forced)", devName, k, v); return; }

            System.Threading.Interlocked.Exchange(ref _lastArmActivityMs, nowMs);   // any accepted attempt refreshes the arm window

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
                        if (!TryDecodeB64Url(v.Substring(4), out var text))
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

        // ── Driving the Out direction (out of the panel) ──────────────────────────────────
        // api/join (above) drives the In direction, INTO the panel: an input sig, which changes
        // what the panel SHOWS. This drives the Out direction, OUT OF the panel: an output sig,
        // which changes what the system DOES — it updates the sig AND raises the device's change
        // event, so the program's own SigChange handlers, and Construct contract events (which sit
        // on that same event), run exactly as if the panel had been used. Both directions offer
        // the same Pulse / High / Low actions; only the reach differs.
        //
        // The point of doing it this way is that it needs NO per-signal wiring in the host program:
        // any Out row becomes a working control on any device.
        //
        // Same gates as the In direction: host kill-switch, arm, rate limit, full audit. This is a
        // strictly bigger action (it runs program logic, not just a value write), so it is never on
        // a lighter footing.
        //
        // Mechanically it asks the device's own output-sig collection to update a join and fire its
        // change event — the identical path inbound panel traffic takes. It is reached reflectively
        // and RESOLVED ONCE PER COLLECTION TYPE, cached, and DEGRADES to "unavailable" (hiding the
        // Out controls) rather than throwing if a future SDK build changes shape. A resolve failure
        // is therefore a soft, visible loss of one feature, never a crash.

        // Resolves (and caches) the collection's UpdateValueWithOutIncrementWithEvent overload for
        // the given parameter shape. Returns null if this SDK build does not expose it.
        private System.Reflection.MethodInfo? InjectMethodFor(Type collType, Type[] sig)
        {
            lock (_injectGate)
            {
                if (_injectMethods.TryGetValue(collType, out var m)) return m;
                try
                {
                    m = collType.GetMethod("UpdateValueWithOutIncrementWithEvent",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                        null, sig, null);
                }
                catch { m = null; }
                _injectMethods[collType] = m;
                return m;
            }
        }

        // True if the Out direction can be driven for this device — used to gate the UI so those
        // controls only appear when the mechanism actually resolved.
        private bool CanPress(BasicTriList device)
        {
            try { return InjectMethodFor(device.BooleanOutput.GetType(), new[] { typeof(uint), typeof(bool) }) != null; }
            catch { return false; }
        }

        // POST api/press?dev=&k=&act=pulse|high|low|send&v=&n=
        // Drives an Out (out-of-panel) signal so the program's own handlers run, as if the panel
        // had been used. Kept at this path for URL stability; "press" reads fine for what it does.
        private void PressJoin(HttpCwsContext context)
        {
            string? devName = null, k = null, act = null, v = null, label = null;
            try
            {
                devName = context.Request.QueryString["dev"];
                k       = context.Request.QueryString["k"];
                act     = context.Request.QueryString["act"];
                v       = context.Request.QueryString["v"];
                label   = context.Request.QueryString["n"];
            }
            catch { }

            if (!string.IsNullOrEmpty(label))
            {
                if (label.Length > 64) label = label.Substring(0, 64);
                var lb = new StringBuilder(label.Length);
                foreach (var c in label) if (c >= ' ') lb.Append(c);
                label = lb.ToString();
            }

            if (string.IsNullOrEmpty(k) || string.IsNullOrEmpty(act))
            { WritePressError(context, 400, "missing k or act", devName, k, act); return; }

            System.Threading.Interlocked.Exchange(ref _lastArmActivityMs, Environment.TickCount64);
            if (!_writesAllowed)
            { WritePressError(context, 403, "writes disabled by host", devName, k, act); return; }
            CheckArmIdle();
            if (!_armed)
            { WritePressError(context, 403, "writes not armed", devName, k, act); return; }

            long nowMs = Environment.TickCount64;
            bool rateLimited;
            lock (_devLock)   // shares the force rate limiter — both are mutations to a live system
            {
                if (nowMs - _forceWinMs >= 1000) { _forceWinMs = nowMs; _forceCount = 0; }
                rateLimited = ++_forceCount > MaxForcesPerSecond;
            }
            if (rateLimited)
            { WritePressError(context, 429, "rate limited", devName, k, act); return; }

            // Resolve the slot and grab the concrete output collection under the lock; the actual
            // event-raising dispatch happens off-lock (and off-thread) below.
            WatchSlot? slot = null;
            object? coll = null;
            string? resolveErr = null;
            lock (_devLock)
            {
                if (!TryResolveDeviceLocked(devName, out var canonName, out var device) || device == null)
                { resolveErr = "unknown device"; }
                else
                {
                    devName = canonName;
                    var cacheKey = devName + "|" + k;
                    if (!_joinCache.TryGetValue(cacheKey, out slot!))
                    {
                        _contracts.TryGetValue(devName, out var cmap);
                        slot = ResolveKey(device, k, cmap);
                        slot.ResolvedMs = nowMs;
                        _joinCache[cacheKey] = slot;
                    }
                    slot.LastSeenMs = nowMs;

                    if (slot.Error != null) resolveErr = slot.Error;
                    else if (!slot.ReadOnly) resolveErr = "this endpoint drives Out (out-of-panel) signals; use api/join for In";
                    else coll = OutputCollectionFor(device, slot);   // null if the SO/collection is gone
                }
            }

            if (resolveErr != null) { WritePressError(context, 400, resolveErr, devName, k, act); return; }
            if (coll == null)       { WritePressError(context, 500, "signal not reachable", devName, k, act); return; }

            // Build the injection as a closure, validating the value/act synchronously so a bad
            // request is a clean 400 rather than a swallowed background failure. Only the actual
            // dispatch is deferred.
            var slotType = slot!.Type;
            var join     = slot.Number;
            string auditVal;
            Action? doInject;
            switch (slotType)
            {
                case 'd':
                {
                    var m = InjectMethodFor(coll.GetType(), new[] { typeof(uint), typeof(bool) });
                    if (m == null) { WritePressError(context, 501, "signal simulation unavailable on this SDK", devName, k, act); return; }
                    var c = coll;
                    // high/low/pulse are the canonical names, matching the page's controls and the
                    // In direction's vocabulary. press/release/tap are kept as accepted aliases:
                    // they describe the same three actions and cost one line each to honour.
                    switch (act)
                    {
                        case "high": case "press":
                            auditVal = "high";  doInject = () => m.Invoke(c, new object[] { join, true }); break;
                        case "low": case "release":
                            auditVal = "low";   doInject = () => m.Invoke(c, new object[] { join, false }); break;
                        case "pulse": case "tap":
                            auditVal = "pulse"; doInject = () => { m.Invoke(c, new object[] { join, true }); m.Invoke(c, new object[] { join, false }); }; break;
                        default: WritePressError(context, 400, "act must be pulse, high or low for a digital", devName, k, act); return;
                    }
                    break;
                }
                case 'a':
                {
                    if (act != "send") { WritePressError(context, 400, "act must be send for an analog", devName, k, act); return; }
                    if (!ushort.TryParse((v ?? "").Trim(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var num))
                    { WritePressError(context, 400, "bad analog value (0-65535)", devName, k, act); return; }
                    var m = InjectMethodFor(coll.GetType(), new[] { typeof(uint), typeof(ushort) });
                    if (m == null) { WritePressError(context, 501, "signal simulation unavailable on this SDK", devName, k, act); return; }
                    var c = coll;
                    auditVal = num.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    doInject = () => m.Invoke(c, new object[] { join, num });
                    break;
                }
                default:  // 's'
                {
                    if (act != "send") { WritePressError(context, 400, "act must be send for a serial", devName, k, act); return; }
                    if (v == null || !v.StartsWith("b64:", StringComparison.Ordinal) || !TryDecodeB64Url(v.Substring(4), out var text))
                    { WritePressError(context, 400, "serial value must be base64url (\"b64:<data>\")", devName, k, act); return; }
                    if (text.Length > 1024) { WritePressError(context, 400, "string exceeds 1024 chars", devName, k, act); return; }
                    foreach (var ch in text) if (ch < ' ' && ch != '\t')
                    { WritePressError(context, 400, "control characters not allowed", devName, k, act); return; }
                    if (!EnsureSerialInject(coll.GetType()))
                    { WritePressError(context, 501, "signal simulation unavailable on this SDK", devName, k, act); return; }
                    var m = _serialInject!; var c = coll; var enc = _serialEnc!; var mode = _serialMode!;
                    auditVal = text;
                    doInject = () => m.Invoke(c, new object[] { join, text, enc, mode });
                    break;
                }
            }

            // Off the CWS thread: the injection raises the device's change event, so the host's
            // SigChange handlers (and contract events) run SYNCHRONOUSLY on the calling thread. A
            // slow or blocking handler must not stall the web server, and the handler may re-enter
            // CwsDebug (e.g. set feedback, log) — so it runs on a worker, not the request thread.
            var work = doInject;
            var auditWhat = string.IsNullOrEmpty(label) ? k : label + " (" + k + ")";
            var addr = ClientAddress(context);
            var devForAudit = devName ?? "?";
            CrestronInvoke.BeginInvoke(_ =>
            {
                try { work!(); }
                catch (Exception e) { ErrorLog.Error("CwsDebugPage: press dispatch failed: {0}", e.Message); }
            });

            AppendAudit("Info", SafeFormat("Panel sim: {0} {1} -> {2} (from {3})",
                new object[] { devForAudit, auditWhat, auditVal, addr }));

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write("{\"ok\":true,\"dispatched\":true,\"k\":" + JsonString(k) + "}", true);
        }

        // The output-sig collection that owns an Out slot's join: the smart object's when the slot
        // is SO-scoped, otherwise the device's. Returns null if the smart object is gone.
        private static object? OutputCollectionFor(BasicTriList device, WatchSlot slot)
        {
            try
            {
                if (slot.SmartObjectId.HasValue)
                {
                    if (device is not BasicTriListWithSmartObject soDev || soDev.SmartObjects == null) return null;
                    var so = soDev.SmartObjects[slot.SmartObjectId.Value];
                    if (so == null) return null;
                    return slot.Type switch { 'd' => so.BooleanOutput, 'a' => so.UShortOutput, _ => (object)so.StringOutput };
                }
                return slot.Type switch { 'd' => device.BooleanOutput, 'a' => device.UShortOutput, _ => (object)device.StringOutput };
            }
            catch { return null; }
        }

        // The serial overload takes two SDK enums; resolve the method by shape and derive its enum
        // arguments (UTF-16, clear-and-end) from the method's own parameter types, so nothing here
        // hard-references a possibly-internal enum. Resolved once.
        private bool EnsureSerialInject(Type collType)
        {
            lock (_injectGate)
            {
                if (_serialProbed) return _serialInject != null;
                _serialProbed = true;
                try
                {
                    foreach (var m in collType.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                    {
                        if (m.Name != "UpdateValueWithOutIncrementWithEvent") continue;
                        var p = m.GetParameters();
                        if (p.Length != 4) continue;
                        if (p[0].ParameterType != typeof(uint) || p[1].ParameterType != typeof(string)) continue;
                        if (!p[2].ParameterType.IsEnum || !p[3].ParameterType.IsEnum) continue;
                        // eStringEncoding.eEncodingUTF16 == 2 ; eInboundTextModes.e_InboundTextModeClearAndEnd == 3.
                        // Replace the whole buffer with the value in one shot, matching a text-entry commit.
                        _serialEnc  = Enum.ToObject(p[2].ParameterType, 2);
                        _serialMode = Enum.ToObject(p[3].ParameterType, 3);
                        _serialInject = m;
                        break;
                    }
                }
                catch { _serialInject = null; }
                return _serialInject != null;
            }
        }

        private void WritePressError(HttpCwsContext context, int status, string message,
            string? dev, string? k, string? act)
        {
            AppendAudit("Notice", SafeFormat("Panel sim rejected: {0} {1} ({2}) -> {3} from {4}",
                new object[] { dev ?? "?", k ?? "?", act ?? "?", message, ClientAddress(context) }));
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
                    System.Threading.Interlocked.Exchange(ref _lastArmActivityMs, Environment.TickCount64);
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
                    System.Threading.Interlocked.Exchange(ref _lastArmActivityMs, Environment.TickCount64);
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

        private void RegisterDeviceInternal(string name, BasicTriList device, string? sgdPath = null)
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

            // Optional: label smart-object numbers from the SGD's own design-time names.
            // Same non-fatal shape as RegisterContract's manifest load — a bad or missing
            // file only costs the labels, never the registration itself.
            Dictionary<uint, string>? sgdNames = null;
            if (!string.IsNullOrEmpty(sgdPath))
            {
                try
                {
                    var path = System.IO.Path.IsPathRooted(sgdPath)
                        ? sgdPath
                        : Path.Combine(Directory.GetApplicationDirectory(), sgdPath);
                    if (!File.Exists(path))
                        throw new System.IO.FileNotFoundException("SGD file not found", path);
                    using (var reader = new StreamReader(path)) sgdNames = ParseSgdSmartObjectNames(reader);
                }
                catch (Exception e)
                {
                    ErrorLog.Error("CwsDebugPage: RegisterDevice(\"{0}\") failed to load SO names from \"{1}\": {2}",
                        name, sgdPath, e.Message);
                    Append("Warning", SafeFormat("Smart object names not loaded for \"{0}\" from \"{1}\": {2} — SO numbers still work",
                        new object[] { name, sgdPath, e.Message }), Array.Empty<object>());
                }
            }

            int soHookCount = -1;
            var deferred = new List<Action>();
            lock (_devLock)
            {
                if (_devices.TryGetValue(name, out var old)) // replacement: unhook + invalidate
                {
                    UnhookLocked(name, old, deferred);
                    DropCacheFor(name);
                }
                _devices[name] = device;
                if (sgdNames != null) _soNames[name] = sgdNames;
                else _soNames.Remove(name);
                _sigNames.Remove(name);   // per-signal names only ever come from RegisterContract
                // A contract map describes ONE specific device's joins, so it must not survive
                // this name being bound to a different device. RegisterContract reinstalls it
                // immediately after calling this, so the contract path is unaffected; what this
                // prevents is a plain RegisterDevice(name, otherDevice) inheriting the previous
                // occupant's manifest and then resolving c~ paths — silently, and forceably —
                // against unrelated joins on the new device.
                _contracts.Remove(name);

                // Subscribe to the device's SigChange so output transitions (button presses,
                // pulses) are observed the moment they happen rather than sampled at the poll
                // cadence — a press+release inside one 2 s window would otherwise be invisible.
                // The handler runs no host code and only touches _devLock-guarded state.
                // The '+=' itself is deferred out of the lock for the same reason as '-=' —
                // see UnhookLocked. Publishing the hook before attaching it is safe: an event
                // that has not been attached yet simply does not fire.
                SigEventHandler hook = (_, args) => OnDeviceSigChange(name, args);
                _devHooks[name] = hook;
                deferred.Add(() =>
                {
                    try { device.SigChange += hook; }
                    catch { /* torn down mid-registration — watch still works, pulses degrade to sampling */ }
                });

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
                            SmartObjectSigChangeEventHandler soHook = (_, args) => MarkSigActivity(name, args.Sig, args.SmartObjectArgs.ID);
                            hooks.Add(new KeyValuePair<SmartObject, SmartObjectSigChangeEventHandler>(so, soHook));
                        }
                    }
                    catch { /* enumeration failed partway; attach whatever was collected */ }
                    if (hooks.Count > 0)
                    {
                        _soHooks[name] = hooks;
                        deferred.Add(() =>
                        {
                            foreach (var kv in hooks)
                            { try { kv.Key.SigChange += kv.Value; } catch { } }
                        });
                    }
                    soHookCount = hooks.Count;
                }
            }
            RunDeferred(deferred);        // SDK '+='/'-=' outside the lock — see UnhookLocked
            Append("Notice", SafeFormat("Device \"{0}\" registered for signals", new object[] { name }), Array.Empty<object>());
            if (sgdNames != null)
                Append("Notice", SafeFormat("Smart object names loaded for \"{0}\": {1} from \"{2}\"",
                    new object[] { name, sgdNames.Count, sgdPath! }), Array.Empty<object>());
            // Diagnostic: how many smart objects were actually present (and so hooked for live
            // activity/logging) at the MOMENT of registration. A device whose smart objects are
            // created lazily by later code (e.g. a Construct contract's own component wiring
            // running after this call) can register with zero hooked here even though its joins
            // still resolve fine by number — that only breaks the instant activity lamp/trace,
            // never plain polling, so it is worth knowing rather than guessing at.
            if (soHookCount >= 0)
                Append("Info", SafeFormat("Device \"{0}\": {1} smart object(s) hooked for activity",
                    new object[] { name, soHookCount }), Array.Empty<object>());
        }

        // ── Construct/CH5 contracts ──────────────────────────────────────────────────────
        // A contract path is just a name for an existing (SmartObjectId, Join, Type, Direction)
        // tuple — the .cse2j manifest is the only new thing here. Once a path resolves to a
        // ContractItem, ResolveKey turns it into the exact same WatchSlot/Sig that numeric and
        // SGD-name addressing produce, so watch, force, arm-gating, audit and tracing all work
        // for a contract item with no changes of their own.

        /// <summary>One contract item resolved from the manifest: which smart-object join it is,
        /// its signal type, and which direction (state = to-panel/forceable, event =
        /// from-panel/read-only).
        ///
        /// Deliberately NOT carrying a "paired opposite-direction path": states and events are
        /// numbered by two INDEPENDENT counters per smart object, so a shared (Type, SO, Join)
        /// tuple does not mean the same logical control — confirmed on the real Masters 26 Test
        /// manifest, where SO1/join5 is "MainVisibilityJoin" on the state side and the wholly
        /// unrelated "Main.B1.Press" on the event side, purely by numbering coincidence. Unlike
        /// plain smart-object addressing (where the shared-join-number heuristic was checked
        /// against real per-object data before shipping), no reliable general rule for
        /// contract-item pairing was found — so none is offered rather than risk a UI control
        /// silently reading/writing the wrong signal under the wrong label.</summary>
        private sealed class ContractItem
        {
            public uint SmartObjectId;
            public uint Join;
            public char Type;       // 'd' | 'a' | 's'
            public bool ReadOnly;   // true = event (from panel); false = state (to panel)
        }

        private void RegisterContractInternal(string name, BasicTriListWithSmartObject device, string cse2jPath)
        {
            // Registers the device exactly as RegisterDevice does — same validation, same
            // SigChange hooks, same replace/unregister semantics. Contract addressing is a
            // strictly additive lookup layer on top.
            RegisterDeviceInternal(name, device);

            Dictionary<string, ContractItem>? map = null;
            int dupes = 0;
            try
            {
                var path = System.IO.Path.IsPathRooted(cse2jPath)
                    ? cse2jPath
                    : Path.Combine(Directory.GetApplicationDirectory(), cse2jPath);
                if (!File.Exists(path))
                    throw new System.IO.FileNotFoundException("contract manifest not found", path);
                string json;
                using (var reader = new StreamReader(path)) json = reader.ReadToEnd();
                map = ParseContractManifest(json, out dupes);
            }
            catch (Exception e)
            {
                // Not fatal: the device is already registered and works via so#./numeric
                // addressing. Only contract-name addressing is unavailable — but that must
                // never be a SILENT failure, so it goes to both logs at a visible severity.
                ErrorLog.Error("CwsDebugPage: RegisterContract(\"{0}\") failed to load \"{1}\": {2}",
                    name, cse2jPath, e.Message);
                Append("Warning", SafeFormat("Contract \"{0}\" not loaded from \"{1}\": {2} — joins still reachable by number",
                    new object[] { name, cse2jPath, e.Message }), Array.Empty<object>());
            }

            // A CH5 smart object's sig collections (unlike an SGD-loaded one) create each Sig
            // wrapper lazily, on first access — so until something reads a given join, it does
            // not exist to enumerate, and the Add-bar's discovery picker (api/smartobjects) only
            // ever shows joins someone already happened to touch. Reading every declared item
            // once here, right at registration, materializes the whole contract up front so the
            // picker shows everything from the start rather than growing one join at a time as
            // rows get added. Best-effort: one bad item (a join the manifest describes but the
            // panel doesn't actually have) is skipped, never fails the registration.
            if (map != null)
            {
                foreach (var item in map.Values)
                {
                    try
                    {
                        var cso = device.SmartObjects?[item.SmartObjectId];
                        if (cso == null) continue;
                        _ = item.Type switch
                        {
                            'd' => item.ReadOnly ? cso.BooleanOutput[item.Join] : (Sig)cso.BooleanInput[item.Join],
                            'a' => item.ReadOnly ? cso.UShortOutput[item.Join]  : (Sig)cso.UShortInput[item.Join],
                            _   => item.ReadOnly ? cso.StringOutput[item.Join]  : (Sig)cso.StringInput[item.Join],
                        };
                    }
                    catch { /* one unreachable declared item is not worth failing registration over */ }
                }
            }

            lock (_devLock)
            {
                if (map != null)
                {
                    _contracts[name] = map;
                    // Overwrites whatever RegisterDeviceInternal just set (nothing, since
                    // contracts don't have an sgdPath) with names derived from the manifest
                    // itself — display-only, so overwriting here is exactly what should happen.
                    var (soNames, sigNames) = DeriveContractNames(map);
                    _soNames[name] = soNames;
                    _sigNames[name] = sigNames;
                }
                else
                {
                    _contracts.Remove(name);
                    _soNames.Remove(name);
                    _sigNames.Remove(name);
                }
                // Invalidate again now that the map is actually in place. RegisterDeviceInternal
                // dropped the cache at the START of this call, but loading and parsing the
                // manifest happens after that — so a page polling a c~ key during that window
                // cached a "no contract item" error against a device that was, a moment later,
                // going to have the contract. Resolution errors are never retried, so without
                // this that row would show an error for the life of the program.
                DropCacheFor(name);
            }

            if (map != null)
                Append("Notice", SafeFormat("Contract \"{0}\" loaded: {1} item(s) from \"{2}\"",
                    new object[] { name, map.Count, cse2jPath }), Array.Empty<object>());
            if (dupes > 0)
                Append("Warning", SafeFormat("Contract \"{0}\": {1} duplicate item path(s) — first declaration kept, direction may be ambiguous",
                    new object[] { name, dupes }), Array.Empty<object>());
        }

        // Parses a Construct .cse2j manifest into a flat path -> ContractItem map. Never
        // returns partial garbage silently: any structural surprise (missing "signals" object,
        // a value of the wrong JSON kind) throws, so the caller's catch logs ONE clear reason
        // rather than RegisterContract quietly ending up with an empty or wrong table. Tolerant
        // of the real quirks found in Construct output: states/events are asymmetric shapes
        // (join-number-keyed vs path-keyed), a page-level path can have no dot, and two
        // list-like components in the same project can use entirely different join-numbering
        // schemes — none of that matters here since this just enumerates whatever keys exist.
        //
        // <paramref name="dupes"/> counts item paths that appeared more than once. The first
        // occurrence always wins — deliberately, and states are read first, so a path declared as
        // both a state and an event stays FORCEABLE rather than silently becoming read-only. The
        // count is reported to the log rather than resolved silently: this file's whole stance on
        // contracts is that a coincidence between the state and event sides must not be quietly
        // interpreted, and a duplicate path is exactly that kind of ambiguity.
        private static Dictionary<string, ContractItem> ParseContractManifest(string json, out int dupes)
        {
            dupes = 0;
            var map = new Dictionary<string, ContractItem>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(json);
            var signals = doc.RootElement.GetProperty("signals");

            // states: { "boolean"|"numeric"|"string": { "<smartObjectId>": { "<join>": "<path>" } } }
            if (signals.TryGetProperty("states", out var states))
            {
                foreach (var typeProp in states.EnumerateObject())
                {
                    var type = SigTypeChar(typeProp.Name);
                    if (type == '\0') continue;                 // unknown signal-type key: skip, don't fail the load
                    foreach (var soProp in typeProp.Value.EnumerateObject())
                    {
                        if (!uint.TryParse(soProp.Name, out var soId)) continue;
                        foreach (var joinProp in soProp.Value.EnumerateObject())
                        {
                            if (!uint.TryParse(joinProp.Name, out var join)) continue;
                            if (joinProp.Value.ValueKind != JsonValueKind.String) continue;
                            var path = joinProp.Value.GetString();
                            if (string.IsNullOrEmpty(path)) continue;
                            if (map.ContainsKey(path)) { dupes++; continue; }   // first wins
                            map[path] = new ContractItem { SmartObjectId = soId, Join = join, Type = type, ReadOnly = false };
                        }
                    }
                }
            }

            // events: { "boolean"|"numeric"|"string": { "<path>": { "joinId": N, "smartObjectId": M } } }
            if (signals.TryGetProperty("events", out var events))
            {
                foreach (var typeProp in events.EnumerateObject())
                {
                    var type = SigTypeChar(typeProp.Name);
                    if (type == '\0') continue;
                    foreach (var pathProp in typeProp.Value.EnumerateObject())
                    {
                        var path = pathProp.Name;
                        if (!pathProp.Value.TryGetProperty("joinId", out var joinEl) ||
                            !pathProp.Value.TryGetProperty("smartObjectId", out var soEl)) continue;
                        // TryGet, not Get: GetUInt32 THROWS on a value that is a JSON string, a
                        // negative, or out of range, and that exception would propagate out of
                        // this method and lose the ENTIRE manifest over one malformed item. The
                        // states loop above is already tolerant the same way; match it, so one
                        // odd entry costs one item rather than all contract-name addressing.
                        if (!soEl.TryGetUInt32(out var soId) || !joinEl.TryGetUInt32(out var join)) continue;
                        if (map.ContainsKey(path)) { dupes++; continue; }   // first (state) wins — see below
                        map[path] = new ContractItem
                        {
                            SmartObjectId = soId,
                            Join = join,
                            Type = type,
                            ReadOnly = true,
                        };
                    }
                }
            }

            // No pairing pass: see the "deliberately NOT carrying" note on ContractItem — a
            // shared (Type, SmartObjectId, Join) between a state and an event does not reliably
            // mean the same logical control, so nothing is inferred from it here.
            return map;
        }

        // Maps a .cse2j signal-type key to the same type character used everywhere else in
        // this file. Returns '\0' for anything unrecognized so the caller can skip it rather
        // than crash the whole manifest load over one unexpected key.
        private static char SigTypeChar(string cse2jType) => cse2jType switch
        {
            "boolean" => 'd',
            "numeric" => 'a',
            "string"  => 's',
            _         => '\0',
        };

        // Derives display-only labels from a contract's own item paths: a smart-object name
        // (for the Add-bar's SO# field) and a per-signal name (for the named-sig picker inside
        // an SO, replacing CH5's generic "Bool Sig Number N").
        //
        // The SO name is the LONGEST COMMON DOT-BOUNDED PREFIX shared by every item on that SO
        // — not just "everything before one item's last dot". That distinction matters for a
        // component like an H-Button-List, which puts many buttons' signals on ONE smart
        // object: "Main.HButtonList.Button1.ButtonPress", "...Button2.ButtonPress", plus
        // SO-wide ones like "Main.HButtonList.ListVisibleJoin". Taking "everything before the
        // last dot" of whichever item happened to be seen first would misname the SO after one
        // specific button; the common prefix across ALL of them ("Main.HButtonList") is right
        // regardless of which item is looked at.
        //
        // Each item's own signal name is then whatever is LEFT after stripping that common
        // prefix — "Button3.ButtonPress" for a button's press, "ListVisibleJoin" for the
        // SO-wide one, "Left"/"Right"/... for a Dpad. Stripping a prefix common to the whole
        // group can never collide two different items, since their full paths were already
        // unique keys in the manifest. When an SO has no usable common prefix (a mix of
        // unrelated page-level items, e.g. one page's B1/B2/NewSlider all landing on SO1 with
        // nothing but "Main" in common) the item just keeps its full contract path as its name
        // instead — more to read, but never wrong and never a false pairing.
        private static (Dictionary<uint, string> SoNames, Dictionary<string, string> SigNames)
            DeriveContractNames(Dictionary<string, ContractItem> map)
        {
            var pathsBySo = new Dictionary<uint, List<string>>();
            foreach (var kv in map)
            {
                if (!pathsBySo.TryGetValue(kv.Value.SmartObjectId, out var list))
                    pathsBySo[kv.Value.SmartObjectId] = list = new List<string>();
                list.Add(kv.Key);
            }

            var fullPrefixes = new Dictionary<uint, string>();   // used for signal-name stripping
            var soNames = new Dictionary<uint, string>();        // shortened, for display only
            foreach (var kv in pathsBySo)
            {
                // A page-level, dot-less path (e.g. "MainVisibilityJoin") shares no dot boundary
                // with anything, so leaving it in the character-by-character reduction can
                // collapse the WHOLE group's prefix to "" even though every other item on that
                // same SO clearly belongs to one common component (e.g. everything else here is
                // "Main.<Widget>.<Signal>"). It contributes nothing to a shared prefix either
                // way (nothing anchors to it, per the '.' boundary check right below), so it is
                // excluded from the computation rather than allowed to poison it — its own name
                // is unaffected, since it already falls back to its full path regardless.
                var dotted = kv.Value.FindAll(p => p.IndexOf('.') >= 0);
                if (dotted.Count == 0) continue;
                var prefix = CommonDotPrefix(dotted);
                if (prefix.Length == 0) continue;
                fullPrefixes[kv.Key] = prefix;
                soNames[kv.Key] = ShortenSoDisplayName(prefix);
            }

            var sigNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in map)
            {
                var item = kv.Value;
                string leaf = kv.Key;
                if (fullPrefixes.TryGetValue(item.SmartObjectId, out var prefix)
                    && kv.Key.Length > prefix.Length + 1 && kv.Key.StartsWith(prefix, StringComparison.Ordinal)
                    && kv.Key[prefix.Length] == '.')
                {
                    leaf = kv.Key.Substring(prefix.Length + 1);
                }
                sigNames[SigNameKey(item.SmartObjectId, item.Type, item.ReadOnly, item.Join)] = leaf;
            }

            return (soNames, sigNames);
        }

        // Longest prefix shared by every path in the group, trimmed back to the last full '.'
        // segment so it can never cut a token in half (e.g. "Button1"/"Button2" sharing the
        // literal characters "Button" must trim back to nothing beyond their own common parent,
        // not stop mid-word). A single-path group degenerates to "everything before its own
        // last dot", matching a plain non-indexed component. Signal-name stripping (see
        // DeriveContractNames) always uses this FULL prefix, unshortened — only the SO's own
        // display name shortens it further (see ShortenSoDisplayName).
        private static string CommonDotPrefix(List<string> paths)
        {
            var common = paths[0];
            for (int i = 1; i < paths.Count && common.Length > 0; i++)
            {
                var p = paths[i];
                int len = 0, max = Math.Min(common.Length, p.Length);
                while (len < max && common[len] == p[len]) len++;
                common = common.Substring(0, len);
            }
            var lastDot = common.LastIndexOf('.');
            return lastDot > 0 ? common.Substring(0, lastDot) : "";
        }

        // A repeated list item (e.g. ButtonWList's Items[0]) gets one smart object per instance,
        // so the common prefix above continues PAST the index into Construct's own internal
        // per-widget wrapper structure — confirmed on the real Masters 26 Test manifest, where
        // every item under "Items[0].ButtonWidgetCONTRACT.WidgetButton" repeats that exact
        // wrapper name. None of that continuation past the index distinguishes anything; it is
        // boilerplate the SO's DISPLAY name doesn't need, even though signal names still strip
        // the full, unshortened prefix (so a signal's own name keeps whatever the wrapper name
        // added, which is still safely unique — just more to read). Truncating right after the
        // last "]" keeps the part that actually identifies which item this SO is. A prefix with
        // no "]" (a non-indexed component) is returned unchanged.
        private static string ShortenSoDisplayName(string prefix)
        {
            var lastBracket = prefix.LastIndexOf(']');
            return lastBracket > 0 ? prefix.Substring(0, lastBracket + 1) : prefix;
        }

        // Composite lookup key for a per-signal friendly name, sharing the same type/direction
        // grammar as watch keys elsewhere in this file (e.g. "od5" = output-direction digital
        // join 5) so it reads consistently for anyone already used to that grammar.
        private static string SigNameKey(uint soId, char type, bool readOnly, uint join) =>
            soId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
            + (readOnly ? "o" : "") + type + join.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Reads smart-object ID -> design-time name out of a VT Pro-e .sgd file. The format is
        // not XML/JSON — it is Crestron's own "[block]key=value[/block]" text format — but every
        // smart object's block carries a "Hint" line of the exact shape
        // "Hint=<ControlName> (Smart Object ID=<N>)", which is already the friendliest label
        // available (it is what VT Pro-e itself shows for the object) and needs no other field
        // in the block to be useful. Line-by-line, tolerant of any block structure around it;
        // an .sgd with no such lines simply yields an empty map rather than an error, since the
        // format is "whatever this VT Pro-e version wrote" and not a contract to validate against.
        // Takes an open reader rather than the whole file as a string: an SGD for a large project
        // is multi-megabyte, and buffering all of it just to run a per-line regex is a big
        // transient allocation on a constrained heap at startup. Matching per line also bounds
        // the regex's work per call, so a pathological single-line file cannot turn into one
        // enormous match attempt.
        private static Dictionary<uint, string> ParseSgdSmartObjectNames(StreamReader reader)
        {
            var names = new Dictionary<uint, string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.IndexOf("Hint", StringComparison.Ordinal) < 0) continue;   // cheap pre-filter
                Match m;
                try { m = SgdHintRegex.Match(line); }
                catch (RegexMatchTimeoutException) { continue; }                    // pathological line: skip it
                if (!m.Success) continue;
                if (!uint.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var soId)) continue;
                var label = m.Groups[1].Value.Trim();
                if (label.Length == 0) continue;
                if (!names.ContainsKey(soId)) names[soId] = label;   // first Hint for an ID wins
            }
            return names;
        }

        // No RegexOptions.Compiled: this runs once per registration, so paying JIT to build a
        // compiled matcher costs more than it saves. The timeout is a backstop only — the pattern
        // has no nested quantifier to blow up on — but it is free insurance against a
        // pathological line in a file this class does not control the shape of.
        private static readonly Regex SgdHintRegex =
            new(@"Hint\s*=\s*(.+?)\s*\(Smart Object ID\s*=\s*(\d+)\)",
                RegexOptions.None, TimeSpan.FromSeconds(2));

        private bool UnregisterDeviceInternal(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var deferred = new List<Action>();
            bool found;
            lock (_devLock)
            {
                found = _devices.TryGetValue(name, out var device);
                if (found)
                {
                    UnhookLocked(name, device!, deferred);
                    _devices.Remove(name);
                    _contracts.Remove(name);
                    _soNames.Remove(name);
                    _sigNames.Remove(name);
                    DropCacheFor(name);
                }
            }
            RunDeferred(deferred);        // SDK '-=' outside the lock — see UnhookLocked
            return found;
        }

        private void UnregisterAllDevicesInternal()
        {
            var deferred = new List<Action>();
            lock (_devLock)
            {
                foreach (var kv in _devices) UnhookLocked(kv.Key, kv.Value, deferred);
                _devices.Clear();
                _contracts.Clear();
                _soNames.Clear();
                _sigNames.Clear();
                _joinCache.Clear();
            }
            RunDeferred(deferred);
        }

        // Runs queued attach/detach work after _devLock has been released. Each item already
        // guards itself; this only makes sure one failure cannot skip the rest.
        private static void RunDeferred(List<Action> deferred)
        {
            foreach (var a in deferred) { try { a(); } catch { } }
        }

        // Forgets a device's hooks and QUEUES the actual detach work for the caller to run after
        // releasing _devLock. Caller holds _devLock.
        //
        // The bookkeeping (dictionary removal) must happen under the lock; the SDK '-=' must NOT.
        // Subscribing/unsubscribing enters the SDK's own event lock, and the SDK holds that same
        // lock while dispatching handlers — handlers which call MarkSigActivity, which takes
        // _devLock. Doing '-=' under _devLock therefore takes the two locks in the opposite order
        // from the dispatch path and can deadlock, most likely exactly when a busy panel is being
        // re-registered. Deferring costs nothing: once the hook is out of the dictionaries an
        // in-flight callback is already a no-op as far as this class is concerned.
        private void UnhookLocked(string name, BasicTriList device, List<Action> deferred)
        {
            if (_devHooks.TryGetValue(name, out var hook))
            {
                _devHooks.Remove(name);
                deferred.Add(() => { try { device.SigChange -= hook; } catch { /* already disposed */ } });
            }
            if (_soHooks.TryGetValue(name, out var soHooks))
            {
                _soHooks.Remove(name);
                deferred.Add(() =>
                {
                    foreach (var kv in soHooks)
                    { try { kv.Key.SigChange -= kv.Value; } catch { /* already disposed */ } }
                });
            }
        }

        private void OnDeviceSigChange(string deviceName, SigEventArgs args) => MarkSigActivity(deviceName, args.Sig);

        // Sig-event handler core (device joins and smart-object sigs alike): marks any watched
        // slot resolving to this sig as changed, so the next poll reports it even if the value
        // has already snapped back. For digitals, the activity counter only advances on the
        // RISING edge — a press-and-release is one activity blip on the page, not two — while
        // the rev still bumps on both edges so the state badge tracks correctly. Exceptions
        // must never escape into Crestron's event dispatch.
        //
        // <paramref name="smartObjectId"/> enables a fallback match by (SmartObjectId, Number,
        // Type) when reference equality misses. This matters specifically for smart objects
        // that are not backed by an SGD (Construct/CH5's XpanelForHtml5): those sig collections
        // create their Sig wrapper objects lazily and do not guarantee the SAME instance comes
        // back on every access, unlike an SGD-loaded panel's pre-populated, stable sig graph —
        // confirmed live on the Masters 26 Test panel, where a Dpad press reached the program's
        // own contract event handler but never reached this hook via ReferenceEquals. Scoped to
        // ReadOnly (event/output-direction) slots only: those are addressed purely by number
        // with no matching Input-side object to collide with, so the fallback cannot cross-match
        // the wrong direction the way it could for a plain forceable join. On a fallback match,
        // the slot adopts the live Sig going forward, so later polls read the current object too.
        private void MarkSigActivity(string deviceName, Sig? sig, uint? smartObjectId = null)
        {
            try
            {
                if (sig == null) return;
                bool countActivity = true;
                eSigType sigType = eSigType.NA;
                try { sigType = sig.Type; if (sigType == eSigType.Bool) countActivity = sig.BoolValue; } catch { }

                var prefix = deviceName + "|";
                List<string>? traces = null;
                long nowMs = Environment.TickCount64;
                lock (_devLock)
                {
                    foreach (var kv in _joinCache)
                    {
                        if (!kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        var slot = kv.Value;
                        bool isMatch = slot.Sig != null && ReferenceEquals(slot.Sig, sig);
                        if (!isMatch && smartObjectId.HasValue && slot.ReadOnly
                            && slot.SmartObjectId == smartObjectId.Value && slot.Number == sig.Number
                            && SigTypeMatches(slot.Type, sigType))
                        {
                            isMatch = true;
                            slot.Sig = sig;   // adopt the live object so future polls stay in sync too
                        }
                        if (!isMatch) continue;
                        if (countActivity) slot.Changes++;
                        slot.Rev = ++_joinRev;

                        if (slot.Trace)
                        {
                            string? line = null;
                            try { line = TraceLine(slot, kv.Key, RenderSlot(slot), nowMs); } catch { }
                            if (line != null) (traces ??= new List<string>()).Add(line);
                        }
                    }
                }
                // Emitted outside _devLock: Append takes _lock, and the ordering invariant is
                // one-way. Keeping the event handler's critical section short matters here too.
                if (traces != null)
                    foreach (var line in traces) Append("Info", line, Array.Empty<object>());
            }
            catch { /* never throw into the device's event dispatch */ }
        }

        private static bool SigTypeMatches(char slotType, eSigType sdkType) => slotType switch
        {
            'd' => sdkType == eSigType.Bool,
            'a' => sdkType == eSigType.UShort,
            's' => sdkType == eSigType.String,
            _   => false,
        };

        // Resolves a client-supplied device name (or none, when exactly one is registered) to the
        // device AND to the name as it was actually REGISTERED. Callers must use the registered
        // spelling for cache keys and for the "dev" field they echo back: device names compare
        // case-insensitively, so "Panel" and "PANEL" reach the same device but would otherwise
        // get separate _joinCache slots for the same signal — which MarkSigActivity then matches
        // both of (its prefix test is case-insensitive too), double-bumping the revision and
        // writing every traced change to the log twice. Caller holds _devLock.
        private bool TryResolveDeviceLocked(string? requested, out string name, out BasicTriList? device)
        {
            name = ""; device = null;
            if (string.IsNullOrEmpty(requested))
            {
                if (_devices.Count != 1) return false;
                foreach (var kv in _devices) { name = kv.Key; device = kv.Value; }
                return true;
            }
            foreach (var kv in _devices)
                if (string.Equals(kv.Key, requested, StringComparison.OrdinalIgnoreCase))
                { name = kv.Key; device = kv.Value; return true; }
            return false;
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
            public uint?   SmartObjectId;         // set for so<id>./c~ addressing; null for plain device joins.
                                                   // Fallback match key for MarkSigActivity — see its comment.
            public bool    ByName;               // key addressed the sig by SGD name
            public string? Error;                // resolution failure, reported per-row; retried on a timer (see ServeJoins)
            public long    ResolvedMs;           // when Error was last (re)computed, for that retry
            public string  LastRendered = "";    // last value (or error) as rendered into JSON
            public long    Rev;                  // _joinRev at the last observed change
            public long    LastSeenMs;           // for cache eviction
            public int     Changes;              // SigChange count (outputs): makes sub-poll pulses visible
            public bool    Trace;                // page asked for this signal to be logged
            public long    TraceUntilMs;         // lapses when the page stops asking (browser closed)
            public string  LastTraced = "";      // last value written to the log; de-duplicates the two sources
            public long    LastTraceMs;          // rate limit for chatty analog/serial
            public string? TraceLabel;           // the page's friendly name for this signal
        }

        // Parses and resolves one watch key against a device. Never throws: every failure —
        // malformed key, out-of-range join, missing smart object or sig name — lands in
        // slot.Error, so one bad key degrades to one errored row, never a failed poll.
        // Key grammar (numeric canonical; '~name' addresses smart-object sigs by SGD name):
        //   ("o")? ("d"|"a"|"s") <num>                          e.g. d5, od5, a12, s1
        //   "so" <id> "." ("o")? ("d"|"a"|"s") (<num>|"~"name)  e.g. so1.d3, so1.d~Item 1 Visible
        //   "c~" <contract item path>                           e.g. c~Main.B1.Press
        //     (looked up in a device's loaded Construct manifest — see RegisterContract; the
        //     manifest itself already carries type/direction/join, so there is nothing to parse)
        private static WatchSlot ResolveKey(BasicTriList device, string rawKey, Dictionary<string, ContractItem>? contractMap = null)
        {
            var slot = new WatchSlot();
            try
            {
                var key = rawKey.Trim();
                if (key.Length < 2) { slot.Error = "bad key"; return slot; }

                // Construct contract addressing: "c~<dotted item path>", e.g. "c~Main.B1.Press".
                // The manifest already resolved this to a concrete (SmartObjectId, Join, Type,
                // Direction), so this is a lookup, not a parse — no d/a/s/so grammar applies.
                if (key.StartsWith("c~", StringComparison.Ordinal))
                {
                    var path = key.Substring(2);
                    if (path.Length == 0) { slot.Error = "bad key (empty contract path)"; return slot; }
                    if (contractMap == null || !contractMap.TryGetValue(path, out var item))
                    { slot.Error = "no contract item \"" + path + "\""; return slot; }
                    if (device is not BasicTriListWithSmartObject cSoDev)
                    { slot.Error = "device has no smart objects"; return slot; }

                    slot.Type = item.Type; slot.ReadOnly = item.ReadOnly; slot.Number = item.Join; slot.ByName = true;
                    slot.SmartObjectId = item.SmartObjectId;
                    try
                    {
                        var cso = cSoDev.SmartObjects?[item.SmartObjectId];
                        if (cso != null)
                            slot.Sig = item.Type switch
                            {
                                'd' => item.ReadOnly ? cso.BooleanOutput[item.Join] : (Sig)cso.BooleanInput[item.Join],
                                'a' => item.ReadOnly ? cso.UShortOutput[item.Join]  : (Sig)cso.UShortInput[item.Join],
                                _   => item.ReadOnly ? cso.StringOutput[item.Join]  : (Sig)cso.StringInput[item.Join],
                            };
                    }
                    catch { slot.Sig = null; }
                    if (slot.Sig == null)
                    { slot.Error = "contract item \"" + path + "\" (smart object " + item.SmartObjectId + " join " + item.Join + ") out of range"; return slot; }
                    return slot;
                }

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
                    slot.SmartObjectId = soId;
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
        // Builds one "signal changed" log line when a traced slot's value actually changed
        // since the last line written for it. Both the SigChange hook and the poll call this,
        // and LastTraced stops them writing the same change twice - the hook catches
        // panel-originated changes as they happen, the poll catches program-driven ones (a
        // write to an input sig raises no event). Digital edges always log; analog and serial
        // get a floor between entries so a dragged slider cannot flood the buffer. Returns
        // null when nothing should be written; callers emit outside the lock.
        private static string? TraceLine(WatchSlot slot, string cacheKey, string rendered, long nowMs)
        {
            if (!slot.Trace || nowMs > slot.TraceUntilMs) return null;
            if (rendered == slot.LastTraced) return null;
            if (slot.Type != 'd' && slot.LastTraceMs != 0 && nowMs - slot.LastTraceMs < TraceMinGapMs) return null;

            slot.LastTraced  = rendered;
            slot.LastTraceMs = nowMs;

            var bar = cacheKey.IndexOf('|');
            var dev = bar > 0 ? cacheKey.Substring(0, bar) : "?";
            var key = bar > 0 ? cacheKey.Substring(bar + 1) : cacheKey;
            var what = string.IsNullOrEmpty(slot.TraceLabel) ? key : slot.TraceLabel + " (" + key + ")";
            return "Signal " + dev + " " + what + " -> " + rendered;
        }

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
                            _soNames.TryGetValue(kv.Key, out var soNames);   // still under _devLock
                            sb.Append(",\"smartObjects\":true,\"soIds\":[");
                            bool f2 = true;
                            foreach (var so in soDev.SmartObjects)
                            {
                                if (!f2) sb.Append(',');
                                f2 = false;
                                sb.Append("{\"id\":").Append(so.Key.ToString(System.Globalization.CultureInfo.InvariantCulture));
                                if (soNames != null && soNames.TryGetValue(so.Key, out var label))
                                    sb.Append(",\"name\":").Append(JsonString(label));
                                sb.Append('}');
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
        // Reads the POST form of api/joins: {"dev":"..","since":N,"w":"k1,k2","t":"k1:<b64>,k2"}.
        // The w/t values are the SAME comma-joined strings the query string uses — deliberately,
        // so the body changes only how they TRAVEL and every parsing rule downstream (including
        // base64url labels) stays byte-identical and is exercised by both transports.
        // Returns false only for a body that is present but unusable; an absent or empty body
        // leaves the query-string values untouched.
        private static bool ReadJoinsBody(HttpCwsContext context, ref string? dev, ref long since,
                                          ref string? w, ref string? t)
        {
            try
            {
                var stream = context.Request.InputStream;
                if (stream == null) return true;
                if (stream.CanSeek) stream.Position = 0;

                // Read the bytes directly rather than wrapping in a StreamReader: the SDK hands
                // back a MemoryStream it CACHES on the request, so disposing a reader around it
                // would dispose that cached stream too. Also bounds the read as it goes, so an
                // oversized body is rejected without being buffered in full first.
                var buf = new System.IO.MemoryStream();
                var chunk = new byte[4096];
                int n, total = 0;
                while ((n = stream.Read(chunk, 0, chunk.Length)) > 0)
                {
                    total += n;
                    if (total > MaxBodyLen) return false;
                    buf.Write(chunk, 0, n);
                }
                if (total == 0) return true;
                var json = Encoding.UTF8.GetString(buf.GetBuffer(), 0, (int)buf.Length);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                if (root.TryGetProperty("dev", out var de) && de.ValueKind == JsonValueKind.String) dev = de.GetString();
                if (root.TryGetProperty("since", out var se) && se.TryGetInt64(out var sv)) since = sv;
                if (root.TryGetProperty("w", out var we) && we.ValueKind == JsonValueKind.String) w = we.GetString();
                if (root.TryGetProperty("t", out var te) && te.ValueKind == JsonValueKind.String) t = te.GetString();
                return true;
            }
            catch { return false; }
        }

        private void ServeJoins(HttpCwsContext context)
        {
            long since = 0;
            string? devName = null, w = null, t = null;
            try
            {
                var raw = context.Request.QueryString["since"];
                if (!string.IsNullOrEmpty(raw)) long.TryParse(raw, out since);
                devName = context.Request.QueryString["dev"];
                w       = context.Request.QueryString["w"];
                t       = context.Request.QueryString["t"];
            }
            catch { /* malformed request — treated as empty below */ }

            // POST carries the same parameters in a JSON body instead of the query string. The
            // page always POSTs: the watch set and the traced set are re-sent on EVERY poll (that
            // is what makes tracing lapse by itself when the page goes away), and with dozens of
            // logged signals plus their names that set is far too big to keep pushing through a
            // URL — no maximum query-string length is documented for CWS, and exceeding whatever
            // it is would break polling for the whole device rather than degrade. GET is still
            // accepted, unchanged, so a single join is still checkable straight from a browser.
            if (string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                && !ReadJoinsBody(context, ref devName, ref since, ref w, ref t))
            { WriteJsonError(context, 400, "bad request body"); return; }

            if (string.IsNullOrEmpty(w))
            { WriteJsonError(context, 400, "missing w"); return; }

            var keys = w.Split(',');
            if (keys.Length > MaxWatchKeys)
            { WriteJsonError(context, 400, "too many watch keys"); return; }
            // Every accepted key can become a _joinCache dictionary key, so cap its length too:
            // the count cap alone still admits a handful of megabyte-long keys. MaxKeyLen is far
            // above any real key (the longest legitimate form is a contract path, and those are
            // bounded by the manifest) so this only ever rejects abuse or corruption.
            foreach (var rk in keys)
                if (rk.Length > MaxKeyLen)
                { WriteJsonError(context, 400, "watch key too long"); return; }

            // Which of these signals the page wants written to the log. Re-sent on every poll,
            // so tracing lapses on its own once the page stops asking - closing the browser
            // cannot leave a processor logging every transition forever.
            // Entries are "key" or "key:<base64url label>" - the label is the name shown on
            // the page, carried so the log reads in the same terms the user chose. base64url
            // keeps a label containing a comma, colon or percent from breaking the list.
            Dictionary<string, string?>? traced = null;
            if (!string.IsNullOrEmpty(t))
            {
                traced = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var k in t.Split(','))
                {
                    var tk = k.Trim();
                    if (tk.Length == 0) continue;
                    var colon = tk.IndexOf(':');
                    if (colon < 0) { traced[tk] = null; continue; }
                    var tkey = tk.Substring(0, colon);
                    if (tkey.Length == 0) continue;
                    traced[tkey] = TryDecodeB64Url(tk.Substring(colon + 1), out var lbl) && lbl.Length <= 64
                        ? lbl : null;
                }
            }
            var traceLines = new List<string>();

            var sb = new StringBuilder();
            bool unknownDevice = false;
            lock (_devLock)
            {
                if (!TryResolveDeviceLocked(devName, out var canonName, out var device) || device == null)
                    unknownDevice = true;
                else
                {
                devName = canonName;                      // cache under the REGISTERED spelling
                _contracts.TryGetValue(devName, out var cmap);

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
                        slot = ResolveKey(device, key, cmap);
                        slot.ResolvedMs = nowMs;
                        _joinCache[cacheKey] = slot;
                    }
                    else if (slot.Error != null && nowMs - slot.ResolvedMs >= ErrorRetryMs)
                    {
                        // A resolution error is not necessarily permanent: the thing the key needs
                        // may simply not have existed yet. Smart objects appear when the SGD is
                        // loaded, a contract map lands after its file is parsed, a lazily-created
                        // CH5 sig appears on first touch — and a page polling across program start
                        // can easily ask before any of that. Without a retry the row shows an error
                        // for the life of the program even though the same key now resolves fine.
                        // Retried on a timer rather than every poll so a genuinely bad key costs
                        // one resolve every few seconds, not four per second forever.
                        var retried = ResolveKey(device, key, cmap);
                        retried.ResolvedMs = nowMs;
                        if (retried.Error == null)
                        {
                            // Carry the tracing bookkeeping across so a row that recovers does not
                            // silently lose its Log setting mid-session. Value history deliberately
                            // starts fresh: there is none worth keeping from an errored slot, and a
                            // blank LastRendered guarantees the revision bumps so the page repaints.
                            retried.Trace        = slot.Trace;
                            retried.TraceUntilMs = slot.TraceUntilMs;
                            retried.TraceLabel   = slot.TraceLabel;
                            _joinCache[cacheKey] = retried;
                            slot = retried;
                        }
                        else slot.ResolvedMs = nowMs;   // still broken: restart the retry clock
                    }
                    slot.LastSeenMs = nowMs;

                    // Tracing is asserted per poll, so a row switched off stops at once and a
                    // vanished browser stops within TraceTtlMs.
                    string? traceLbl = null;
                    slot.Trace = traced != null && traced.TryGetValue(key, out traceLbl);
                    if (slot.Trace)
                    {
                        slot.TraceUntilMs = nowMs + TraceTtlMs;
                        slot.TraceLabel = traceLbl;
                    }

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

                    if (err == null)
                    {
                        var line = TraceLine(slot, cacheKey, rendered, nowMs);
                        if (line != null) traceLines.Add(line);
                    }

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
                // Whether this device supports simulated presses on its In rows (the mechanism
                // resolved on this SDK build). Per-device because it depends on the device's own
                // collection type; the page uses it to show or hide the Press/Tap/Release buttons.
                sb.Append(",\"canPress\":").Append(CanPress(device) ? "true" : "false");
                try { sb.Append(",\"online\":").Append(device.IsOnline ? "true" : "false"); } catch { }
                sb.Append(",\"joins\":[").Append(entries).Append("]}");
                }
            }

            // Outside _devLock, per the ordering invariant (WriteJsonError and Append both take
            // _lock; neither may be called while _devLock is held).
            if (unknownDevice)
            { WriteJsonError(context, 404, "unknown device"); return; }

            foreach (var line in traceLines) Append("Info", line, Array.Empty<object>());

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
            string? soName = null;
            Dictionary<string, string>? sigNames = null;
            bool isContract = false;
            lock (_devLock)
            {
                if (TryResolveDeviceLocked(devName, out var canonName, out var device))
                {
                    devName = canonName;                  // echo back the REGISTERED spelling
                    if (device is BasicTriListWithSmartObject { SmartObjects: not null } soDev)
                    {
                        try { so = soDev.SmartObjects[soId]; } catch { so = null; }
                    }
                    if (_soNames.TryGetValue(devName, out var names))
                        names.TryGetValue(soId, out soName);
                    _sigNames.TryGetValue(devName, out sigNames);
                    isContract = _contracts.ContainsKey(devName);
                }
            }
            if (so == null)
            { WriteJsonError(context, 404, "no smart object " + soId); return; }

            var sb = new StringBuilder();
            sb.Append("{\"dev\":").Append(JsonString(devName ?? ""));
            sb.Append(",\"id\":").Append(soId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (soName != null) sb.Append(",\"name\":").Append(JsonString(soName));
            // A contract's states and events are numbered by two INDEPENDENT counters, so a
            // shared number between them is coincidence, not a real pairing — confirmed on this
            // exact manifest, where join 1005 is BOTH "Button1Enabled" (a state) and
            // "Button5ItemPress" (a wholly unrelated event). The front end uses this flag to
            // turn the number-based "Both" pairing off entirely for a contract device, the same
            // way RegisterContract's own ContractItem already refuses to guess at pairing.
            sb.Append(",\"contract\":").Append(isContract ? "true" : "false");
            sb.Append(",\"sigs\":{");
            AppendSigList(sb, "din",  () => so.BooleanInput,  sigNames, soId, 'd', false);
            sb.Append(',');
            AppendSigList(sb, "dout", () => so.BooleanOutput, sigNames, soId, 'd', true);
            sb.Append(',');
            AppendSigList(sb, "ain",  () => so.UShortInput,   sigNames, soId, 'a', false);
            sb.Append(',');
            AppendSigList(sb, "aout", () => so.UShortOutput,  sigNames, soId, 'a', true);
            sb.Append(',');
            AppendSigList(sb, "sin",  () => so.StringInput,   sigNames, soId, 's', false);
            sb.Append(',');
            AppendSigList(sb, "sout", () => so.StringOutput,  sigNames, soId, 's', true);
            sb.Append("}}");

            context.Response.StatusCode  = 200;
            context.Response.ContentType = "application/json";
            context.Response.Write(sb.ToString(), true);
        }

        // Appends one collection as "key":[{"n":1,"name":"Item 1 Pressed"}, ...]. Any failure
        // (collection null, enumeration throws) yields an empty list rather than a failed response.
        // Non-generic on purpose: the SDK's sig-collection nullability annotations fight a
        // Sig-constrained generic, and a runtime type check is all that's actually needed.
        //
        // <paramref name="sigNames"/> (contract devices only) adds a "friendly" field with the
        // contract's own leaf name (e.g. "Left") alongside CH5's generic "name" ("Bool Sig
        // Number 5") when one is known for this exact (smart object, type, direction, join).
        // "name" is left exactly as the SDK reports it — it is what "~name" addressing resolves
        // against, so it must stay the string that actually exists on the sig regardless of any
        // friendlier label being available for display.
        private static void AppendSigList(StringBuilder sb, string key, Func<System.Collections.IEnumerable?> get,
            Dictionary<string, string>? sigNames, uint soId, char type, bool readOnly)
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
                        string? friendly = null;
                        sigNames?.TryGetValue(SigNameKey(soId, type, readOnly, num), out friendly);
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"n\":").Append(num.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"name\":").Append(JsonString(name));
                        if (friendly != null) sb.Append(",\"friendly\":").Append(JsonString(friendly));
                        sb.Append('}');
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

            // The age pass alone does NOT bound the cache: the watch set is client-supplied and
            // unauthenticated, so a caller polling fresh keys keeps every entry young and the
            // age cutoff never fires. That matters more than memory — MarkSigActivity walks this
            // whole dictionary under _devLock on the sig-dispatch thread for every signal edge,
            // so an oversized cache turns each button press into a long stall. Make the cap real
            // by dropping least-recently-seen entries until back under it. A live row re-resolves
            // on its next poll, so over-eviction costs one resolve, never correctness.
            if (_joinCache.Count <= JoinCacheMax) return;
            var byAge = new List<KeyValuePair<string, WatchSlot>>(_joinCache);
            byAge.Sort((a, b) => a.Value.LastSeenMs.CompareTo(b.Value.LastSeenMs));
            int drop = _joinCache.Count - JoinCacheMax;
            for (int i = 0; i < drop; i++) _joinCache.Remove(byAge[i].Key);
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
            // Single outermost guard. The individual handlers are careful internally, but the
            // final Response.Write is a socket write that throws when the client has already gone
            // away — and at a 250 ms signal-poll cadence a browser being closed or navigated
            // mid-request is an everyday event, not an edge case. Without this, that ordinary
            // disconnect escapes into CWS as an unhandled exception from a library callback.
            try { Route(context); }
            catch (Exception e)
            {
                // Best-effort: the response may already be partly written (or the socket gone),
                // so both the error reply and the logging are allowed to fail silently. What
                // matters is that nothing propagates back into the web server.
                try
                {
                    ErrorLog.Error("CwsDebugPage: unhandled error serving \"{0}\": {1}: {2}",
                        context.Request.Path ?? "?", e.GetType().Name, e.Message);
                }
                catch { }
                try
                {
                    context.Response.StatusCode  = 500;
                    context.Response.ContentType = "application/json";
                    context.Response.Write("{\"error\":\"internal\"}", true);
                }
                catch { }
            }
        }

        private void Route(HttpCwsContext context)
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
            else if (path.EndsWith("api/joins", StringComparison.OrdinalIgnoreCase)
                     && (method == "GET" || method == "POST"))
                ServeJoins(context);                // watched join values as JSON (POST: set in body)
            else if (path.EndsWith("api/join", StringComparison.OrdinalIgnoreCase) && method == "POST")
                WriteJoin(context);                 // force one join (arm-gated, audited)
            else if (path.EndsWith("api/press", StringComparison.OrdinalIgnoreCase) && method == "POST")
                PressJoin(context);                 // simulate panel activity on an In signal (arm-gated, audited)
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

            // Piggybacked on the log poll rather than given its own endpoint: the page needs to
            // know whether ANY device is registered so it can show or hide the Signals button,
            // and that answer can change after the page was served (devices are registered from
            // InitializeSystem, and a program restart or UnregisterDevice can take them away
            // again). Riding along here keeps it live at zero extra requests. Taken outside
            // _lock — which this method holds above — per the ordering invariant.
            int deviceCount;
            lock (_devLock) deviceCount = _devices.Count;

            var sb = new StringBuilder();
            sb.Append("{\"lastSeq\":");
            sb.Append(lastSeq);
            sb.Append(",\"devices\":").Append(deviceCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
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

        // Decodes a base64url payload (no padding, '-' and '_' for '+' and '/'). Used for text
        // that travels in a query string: the alphabet contains no '+', '%' or space, so the
        // value survives whatever URL-decoding CWS applies without a second guess.
        private static bool TryDecodeB64Url(string raw, out string text)
        {
            text = "";
            try
            {
                var b = raw.Replace('-', '+').Replace('_', '/');
                switch (b.Length % 4)
                {
                    case 2: b += "=="; break;
                    case 3: b += "=";  break;
                    case 1: return false;
                }
                text = Encoding.UTF8.GetString(Convert.FromBase64String(b));
                return true;
            }
            catch { return false; }
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
                // Fall back through the swap-sequence leftovers (see FlushToDisk): if the live file
                // is missing, a ".bak" means power was lost mid-swap with the old copy safely
                // renamed aside, and a ".tmp" means it was lost before the swap even started. Both
                // are complete files — the swap never leaves a half-written one under either name —
                // so recovering from them is strictly better than starting with no history.
                var source = _persistPath;
                if (!File.Exists(source))
                {
                    if (File.Exists(_persistPath + ".bak"))      source = _persistPath + ".bak";
                    else if (File.Exists(_persistPath + ".tmp")) source = _persistPath + ".tmp";
                    else return;
                }

                string content;
                using (var reader = new StreamReader(source)) content = reader.ReadToEnd();

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
        //
        // The swap keeps a recoverable copy on disk AT ALL TIMES. The obvious sequence — delete the
        // live file, then move the temp file onto its name — leaves a window with no log file at
        // all, and a power cut inside that window loses the entire persisted history (LoadPersisted
        // would find nothing). Instead the live file is renamed aside to ".bak" first, so every
        // instant has either the live file or the .bak present, and LoadPersisted falls back to
        // .bak (then .tmp) when the main file is missing.
        //
        // The in-progress flag makes the shutdown flush and a timer tick that is already running
        // mutually exclusive: both write the same .tmp path, and interleaving them would produce a
        // torn temp file that then gets promoted to live.
        private void FlushToDisk()
        {
            if (string.IsNullOrEmpty(_persistPath)) return;
            if (System.Threading.Interlocked.Exchange(ref _flushing, 1) == 1) return;   // a flush is already running
            try
            {
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
                    var bak = _persistPath + ".bak";
                    using (var writer = new StreamWriter(tmp, false)) writer.Write(sb.ToString());

                    // Rename aside rather than delete, so the old copy still exists under a name
                    // LoadPersisted knows to look for until the new one is safely in place.
                    if (File.Exists(_persistPath))
                    {
                        try { if (File.Exists(bak)) File.Delete(bak); } catch { }
                        File.Move(_persistPath, bak);
                    }
                    File.Move(tmp, _persistPath);
                    try { if (File.Exists(bak)) File.Delete(bak); } catch { /* stale .bak is harmless */ }
                }
                catch
                {
                    lock (_lock) _dirty = true;                  // retry on the next tick
                }
            }
            finally { System.Threading.Interlocked.Exchange(ref _flushing, 0); }
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
            // Publish any CwsConsole Print fragment still waiting for a newline, so it lands in the
            // final snapshot instead of being lost at program stop.
            try { CwsConsole.FlushPending(); } catch { /* best-effort on shutdown */ }
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
header{background:var(--bg2);padding:10px 16px;display:flex;align-items:center;justify-content:space-between;flex-wrap:wrap;gap:8px;border-bottom:2px solid var(--border);flex-shrink:0}
h1{font-size:16px;color:#f87171;letter-spacing:3px;text-transform:uppercase}
.brand{display:flex;align-items:baseline;gap:8px}
.ver{font-size:11px;color:var(--muted);letter-spacing:1px}
.controls{display:flex;align-items:center;gap:10px;flex-wrap:wrap;justify-content:flex-end}
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
.entry{display:flex;flex-wrap:wrap;gap:0 8px;align-items:baseline;padding:4px 14px;border-bottom:1px solid var(--row-border);line-height:1.5;word-break:break-word}
.badge{flex-shrink:0;font-size:10px;font-weight:bold;letter-spacing:1px;padding:1px 5px;border-radius:2px;text-transform:uppercase;min-width:50px;text-align:center}
.badge-info   {background:var(--bi-bg);color:var(--bi-fg)}
.badge-notice {background:var(--bn-bg);color:var(--bn-fg)}
.badge-warning{background:var(--bw-bg);color:var(--bw-fg)}
.badge-error  {background:var(--be-bg);color:var(--be-fg)}
.badge-fatal  {background:var(--bf-bg);color:var(--bf-fg)}
.src{flex-shrink:0;font-size:9px;font-weight:bold;letter-spacing:1px;padding:1px 4px;border-radius:2px;min-width:36px;text-align:center;border:1px solid var(--border)}
.src-app {color:var(--muted)}
.src-proc{color:var(--sp-fg)}
/* The message never shrinks below ~16em: when the row is too narrow for badge + message +
   timestamp, the timestamp wraps to its own line (right-aligned) instead of squeezing the
   message into a one-character column. */
.msg{flex:1 1 16em;min-width:0;white-space:pre-wrap}
/* An escaped non-printable char / byte (\x0D, [0D]): tinted so it can't be confused with
   the same characters typed literally in a message. */
.ctl{color:var(--muted);background:rgba(128,128,128,.18);border-radius:2px;padding:0 1px}
.bytesbtn{margin-left:auto;padding:2px 8px;font-size:10px;text-transform:none}
.ts{flex-shrink:0;margin-left:auto;color:var(--muted);font-size:11px}
.empty{color:var(--muted);padding:12px 14px;font-style:italic}
.main{flex:1;display:flex;overflow:hidden;min-height:0}
.watch{background:var(--bg2);border-right:1px solid var(--border);flex-shrink:0;width:460px;min-width:280px;display:flex;flex-direction:column;overflow:hidden}
.wsplit{width:5px;cursor:col-resize;background:var(--border);flex-shrink:0}
.wsplit:hover{background:var(--muted)}
/* Phones / narrow windows: stack the Signals panel above the log instead of beside it. The
   !important beats the inline width the drag splitter stores, which only applies side by side. */
@media (max-width:760px){
  .main{flex-direction:column}
  .watch{width:auto!important;min-width:0;max-height:55vh;border-right:0;border-bottom:1px solid var(--border)}
  .wsplit{display:none}
}
.addwatch{display:flex;gap:6px;padding:6px 14px;align-items:center;flex-wrap:wrap;border-bottom:1px solid var(--row-border)}
.addwatch select,.addwatch input{background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:4px 6px;font:12px 'Courier New',monospace;outline:none}
.addwatch input{width:70px}
.sowrap{position:relative;display:inline-block}
/* Styled replacement for a native <datalist> (unthemeable, and some browsers render it huge) —
   same idea, own markup: a scrollable panel under the SO# input, filtered as you type. */
.somenu{display:none;position:absolute;top:100%;left:0;margin-top:2px;z-index:20;min-width:200px;
  max-height:220px;overflow-y:auto;background:var(--bg2);border:1px solid var(--border);
  border-radius:3px;box-shadow:0 4px 12px rgba(0,0,0,.35)}
.somenu.open{display:block}
.somenu-item{padding:4px 8px;font:12px 'Courier New',monospace;cursor:pointer;white-space:nowrap;color:var(--text)}
.somenu-item:hover,.somenu-item.hi{background:var(--border)}
.somenu-item .n{color:var(--muted);margin-left:6px}
.somenu-empty{padding:4px 8px;font:12px 'Courier New',monospace;color:var(--muted);font-style:italic}
.wlist{flex:1;overflow-y:auto}
.wrow{display:flex;gap:8px;align-items:baseline;padding:4px 14px;border-bottom:1px solid var(--row-border);line-height:1.5}
.wkey{flex-shrink:0;color:var(--muted);min-width:110px;max-width:160px;font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
/* min-width:0 + nowrap: without them a squeezed cell wraps one character per line */
.wval{flex:0 1 auto;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.werr{flex:0 1 auto;min-width:0;color:var(--bf-fg);font-size:12px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.wx{flex-shrink:0;background:transparent;border:1px solid var(--border);color:var(--muted);padding:0 7px;border-radius:2px;cursor:pointer;font:bold 12px 'Courier New',monospace}
.wx:hover{background:#b91c1c;color:#fff;border-color:#b91c1c}
.wempty{color:var(--muted);padding:8px 14px;font-style:italic;font-size:12px}
/* Disarmed still needs to read as ""notice this"", not just another gray button next to Pause
   and Logging — a dashed amber border keeps it visually distinct without implying danger the
   way solid red (the armed/active state below) does. */
/* 2px on both states (not just disarmed) so arming doesn't shift the button's size by a pixel. */
#armBtn{border-width:2px}
#armBtn:not(.on){border-style:dashed;border-color:#d97706}
#armBtn.on{background:#b91c1c;border-color:#b91c1c;color:#fff}
#armBtn.on:hover{background:#991b1b}
.wforce{display:flex;gap:4px;flex-shrink:0}
.wf{font:bold 10px 'Courier New',monospace;letter-spacing:1px;padding:2px 7px;border-radius:2px;cursor:pointer;text-transform:uppercase;border:1px solid var(--border);background:transparent;color:var(--text)}
.wf:hover{background:var(--border)}
.wf.off{opacity:.35;cursor:not-allowed}
.wf.active{background:var(--bn-bg);color:var(--bn-fg);border-color:var(--bn-fg)}
.wt{flex-shrink:0;font:bold 9px 'Courier New',monospace;letter-spacing:1px;padding:2px 5px;border-radius:2px;cursor:pointer;border:1px solid var(--border);background:transparent;color:var(--muted)}
.wt:hover{background:var(--border)}
.wt.on{background:var(--be-bg);color:var(--be-fg);border-color:var(--be-fg)}
.wnum{width:80px;background:var(--search-bg);color:var(--text);border:1px solid var(--border);border-radius:3px;padding:2px 6px;font:12px 'Courier New',monospace;outline:none}
.wnum.dirty{border-color:var(--bw-fg)}
.wnum.pending{border-color:var(--bn-fg);opacity:.6}
.wferr{flex-shrink:0;color:var(--bf-fg);font-size:11px}
.wdir{flex-shrink:0;font-size:11px;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;margin:0 2px 0 8px}
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
    <span id=""status"" onclick=""toggleTimeFormat()"" style=""cursor:pointer"" title=""Click to switch between 12-hour and 24-hour time."">Loading...</span>
    <button id=""logBtn"" class=""toggle on"" onclick=""toggleLogging()"" title=""RECORDING switch on the processor: off means nothing is stored (persists across restarts). It does not stop this page updating - use Pause for that."">Logging On</button>
    <button id=""pauseBtn"" class=""toggle"" onclick=""togglePause()"" title=""Stop this page requesting updates. The processor keeps recording; resuming catches up. The page also stops on its own when hidden or left idle."">Pause</button>
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
  <button id=""bytesBtn"" class=""bytesbtn"" onclick=""cycleBytes()"" title=""How non-printable characters and bytes 0x80–0xFF are shown. Click to cycle."">Bytes: \x##</button>
</div>
<div class=""main"">
<div class=""watch"" id=""watchPanel"" style=""display:none"">
  <div class=""addwatch"">
    <span class=""flabel"">Add:</span>
    <select id=""awDev"" title=""Which registered device the signal belongs to."" onchange=""populateSoList()""></select>
    <select id=""awType"" onchange=""refreshSigOptions()"" title=""Signal type: Digital = true/false, Analog = 0-65535, Serial = text."">
      <option value=""d"">Digital</option>
      <option value=""a"">Analog</option>
      <option value=""s"">Serial</option>
    </select>
    <span class=""sowrap"">
      <input id=""awSo"" type=""number"" min=""1"" placeholder=""SO#"" autocomplete=""off"" title=""Smart Object ID — leave blank for a panel join. Suggestions (when known) come from the SGD or Construct contract, not typed in by hand.""
             oninput=""refreshSigOptions();showSoName();renderSoMenu()"" onfocus=""this.select();renderSoMenu(true)"" onblur=""setTimeout(closeSoMenu,150)"" onkeydown=""if(event.key==='Escape')closeSoMenu()"">
      <div id=""awSoMenu"" class=""somenu""></div>
    </span>
    <span id=""awSoName"" style=""color:var(--muted);font-size:12px;align-self:center;max-width:140px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap""></span>
    <!-- After SO#, not before: whether Both/Out/In are even meaningful choices depends on what
         the chosen smart object actually has (fillSigOptions may grey Both out entirely, or find
         nothing on one side) — deciding the SO first, then the direction, matches that dependency
         instead of asking the direction question before it can really be answered. -->
    <select id=""awDir"" title=""In = into the panel (text/feedback) — changes what the panel shows. Out = out of the panel (presses) — runs the program's own logic, as if the panel had been used. Both = one row showing both directions of the same join."" onchange=""refreshSigOptions()"">
      <option value=""b"" selected>Both</option>
      <option value="""">In</option>
      <option value=""o"">Out</option>
    </select>
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
var watchList=[];                // [{rid,dev,key,...}] in display order, persisted to localStorage
// Every row carries an opaque, persisted id, and that id — never (dev,key) — is what the row's
// buttons act on. (dev,key) is NOT unique: a paired row drops the direction prefix from its key,
// so it collides with a plain Out row on the same join, and two contract pairs can share an
// out-side join while differing only in their in-side one. Looking a row up by (dev,key) could
// therefore rename, un-log, or delete a DIFFERENT row than the one clicked.
var rowSeq=0;
function nextRid(){return 'r'+(rowSeq++);}
// Shows/hides the Signals button to match whether the program currently has any device
// registered. Registration is the only criterion — an offline panel still has joins worth
// looking at, and its rows correctly show as stale rather than vanishing. Kept live from the
// log poll (see refresh) because the page can outlive the state it was rendered with: served
// before InitializeSystem registered anything, or still open after a restart removed it.
// Closes the panel on the way out so it cannot be left open over a device list that is gone.
function setWatchAvailable(avail){
  if(avail===WATCH_AVAILABLE)return;
  WATCH_AVAILABLE=avail;
  var wb=document.getElementById('watchBtn');
  if(wb)wb.style.display=avail?'':'none';
  if(!avail&&watchOpen)toggleWatch();
}
function findRow(rid){
  for(var i=0;i<watchList.length;i++) if(watchList[i].rid===rid) return watchList[i];
  return null;
}
var joinVals={};                 // key -> last response entry for that key
var joinRev=0;                   // api/joins cursor; reset to 0 whenever the watch set changes
var levelOn={Info:true,Notice:true,Warning:true,Error:true,Fatal:true};
var LEVELS=['Info','Notice','Warning','Error','Fatal'];
var BADGE={Info:'badge-info',Notice:'badge-notice',Warning:'badge-warning',Error:'badge-error',Fatal:'badge-fatal'};
function esc(s){return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
// How non-printable chars and bytes 0x80-0xFF are displayed: 'x' -> \x0D, 'b' -> [0D], 'raw' -> as-is.
var bytesMode='x';
function ctlCode(c){
  var h=c.toString(16).toUpperCase(); if(h.length<2)h='0'+h;
  return bytesMode==='b'?'['+h+']':'\\x'+h;
}
// Single formatter shared by display (html=true: HTML-escaped, escapes wrapped in .ctl spans)
// and search/export (html=false: plain text) so the two can't drift.
// Escaped: 0x00-0x1F except TAB/LF, 0x7F, 0x80-0xFF (SIMPL# strings are one byte per char).
// A CR+LF pair stays a real line break; a lone CR is escaped. Code points > 0xFF are real Unicode.
function ctlRender(s,html){
  s=String(s);
  if(bytesMode==='raw')return html?esc(s):s;
  var out='',run='';
  for(var i=0;i<s.length;i++){
    var c=s.charCodeAt(i);
    if(c===13&&s.charCodeAt(i+1)===10){if(!html)run+='\r';continue;}
    if((c<32&&c!==9&&c!==10)||(c>=0x7F&&c<=0xFF)){
      var t=ctlCode(c);
      if(html){out+=esc(run)+'<span class=""ctl"">'+t+'</span>';run='';}
      else run+=t;
    }else run+=s.charAt(i);
  }
  return out+(html?esc(run):run);
}
function fmtText(s){return ctlRender(s,true);}
function plainText(s){return ctlRender(s,false);}
function updateBytesBtn(){
  var b=document.getElementById('bytesBtn');
  if(b)b.textContent='Bytes: '+(bytesMode==='b'?'[##]':(bytesMode==='raw'?'raw':'\\x##'));
}
function cycleBytes(){
  bytesMode=bytesMode==='x'?'b':(bytesMode==='b'?'raw':'x');
  try{localStorage.setItem('dbg-bytes',bytesMode);}catch(x){}
  updateBytesBtn();
  applyFilter();
}
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
                  '<span class=""msg"">'+fmtText(e.msg)+'</span>'+
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
var devCanPress={};   // dev name -> whether simulated presses resolved for it on this SDK build
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
// In-direction (into the panel) digital controls. Same three actions, same order (Low, Pulse,
// High - laid out like a rocker, off on the left) as the
// Out-direction set below, so the two directions read identically — what differs is only what a
// change reaches, not how you drive it. fb (optional) is the join's own current value: the
// matching High/Low button is highlighted so the state stays visible without a second badge.
function forceBtnsHtml(dev,key,fb){
  var on=armed&&writesAllowed;
  var cls=on?'wf':'wf off';
  var lock=on?'':' (arm forcing first)';
  var hi=(fb&&fb.v===true)?' active':'', lo=(fb&&fb.v===false)?' active':'';
  function b(val,txt,tip,extra){
    return '<button class=""'+cls+(extra||'')+'"" title=""'+tip+lock+'"" onclick=""forceJoin(\''+escAttr(dev)+'\',\''+escAttr(key)+'\',\''+val+'\')"">'+txt+'</button>';
  }
  return '<span class=""wforce"">'+
     b('low','Low','Hold this join low/false',lo)+
     b('pulse','Pulse','Momentary: high then immediately low')+
     b('high','High','Hold this join high/true',hi)+
     '</span>';
}
// inject=false: the editor forces the value (Out/input sig). inject=true: it SIMULATES the panel
// sending that value on a read-only In sig, so the program's handlers run (analog/serial twin of
// the digital Press buttons). The only differences are which capability gates it and which
// endpoint the Enter key hits — everything else about the cell is identical.
function editorHtml(e,j,key,inject){
  var canForce=armed&&writesAllowed&&(!inject||devCanPress[e.dev]);
  var num=j.t==='a';
  var verb=inject?'send it as a panel input':'force it';
  return '<input class=""'+(num?'wnum':'wtxt')+'"" type=""'+(num?'number':'text')+'""'+
     (num?' min=""0"" max=""65535""':'')+
     ' title=""'+(canForce?('Type a value and press Enter to '+verb+'. Esc cancels.'):'Read-only until forcing is armed.')+'""'+
     ' value=""'+escq(j.v)+'""'+
     (canForce?'':' readonly')+
     ' onfocus=""editingWatch=\''+escAttr(e.dev+'|'+key)+'\'""'+
     ' onblur=""editingWatch=null;renderWatch()""'+
     ' oninput=""this.classList.add(\'dirty\')""'+
     ' onkeydown=""'+(num?'analogKey':'serialKey')+'(event,\''+escAttr(e.dev)+'\',\''+escAttr(key)+'\',this,'+(inject?'true':'false')+')"">';
}
// Out-direction (out of the panel) digital controls: same Pulse / High / Low as the In direction,
// deliberately identical so the two read the same way. What differs is the effect — driving this
// side runs the program's own SigChange handlers and Construct contract events, exactly as if the
// panel had been used. Gated on the same arm + writes-allowed as the In direction, plus the
// mechanism having resolved on this SDK build.
function pressBtnsHtml(dev,key,fb){
  if(!devCanPress[dev])return '';
  var on=armed&&writesAllowed;
  var cls=on?'wf':'wf off';
  var lock=on?'':' (arm forcing first)';
  var hi=(fb&&fb.v===true)?' active':'', lo=(fb&&fb.v===false)?' active':'';
  function b(act,txt,tip,extra){
    return '<button class=""'+cls+(extra||'')+'"" title=""'+tip+lock+'"" onclick=""pressJoin(\''+escAttr(dev)+'\',\''+escAttr(key)+'\',\''+act+'\')"">'+txt+'</button>';
  }
  return '<span class=""wforce"">'+
     b('low','Low','Return low, as releasing a held press',lo)+
     b('pulse','Pulse','Momentary, as a quick tap on the panel: high then immediately low')+
     b('high','High','Hold high, as a press-and-hold on the panel',hi)+
     '</span>';
}
// Renders one direction's value/editor cell. Digitals: one badge; In-side gets the
// activity lamp instead of a counter. Analog/serial: value or editor, no counters.
function watchCell(e,j,key){
  if(!j)return '<span class=""wval"" style=""color:var(--muted)"">&mdash;</span>';
  if(j.err)return '<span class=""werr"">'+esc(j.err)+'</span>';
  var h='';
  if(j.t==='d'){
    h+=badgeHtml(j)+lampHtml(e.dev+'|'+key,j);
    if(!j.ro)h+=forceBtnsHtml(e.dev,key,j);    // In: changes what the panel shows
    else h+=pressBtnsHtml(e.dev,key,j);        // Out: runs the program's logic
  }else if(j.ro){
    // In analog/serial: show the value, and when presses are available offer an editor that
    // sends it as panel input (the analog/serial equivalent of a press).
    if(devCanPress[e.dev]&&armed&&writesAllowed) h+=editorHtml(e,j,key,true);
    else h+='<span class=""wval"" title=""'+escq(j.v)+'"">'+fmtText(String(j.v))+'</span>';
  }else{
    h+=editorHtml(e,j,key,false);
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
    // The label is clipped to the column width, so the tooltip leads with the full name, then the
    // signal keys, then the rename hint — one per line (&#10; is a newline inside a title).
    var tip=escq(label)+'&#10;'+escq(e.dev+' '+e.key+(e.pair?' + '+pairInKey(e):''))+'&#10;Double-click to rename';
    var h='<span class=""wkey"" title=""'+tip+'""'+
          ' ondblclick=""renameWatch(\''+escAttr(e.rid)+'\')"">'+esc(label)+'</span>';
    if(e.pair){
      var jo=joinVals[e.dev+'|'+pairInKey(e)];
      if(j&&!j.err&&j.t==='d'){
        // Paired digital: both directions' controls, each under its own label. In (into the
        // panel, e.key) changes the feedback the panel shows; Out (out of the panel,
        // pairInKey) simulates a press, so the program's own SigChange handlers run. Feedback
        // state shows as the highlighted In High/Low button; the badge and activity lamp
        // describe the Out side (the panel press), so a pulse flashes the lamp without
        // flipping the sampled badge.
        h+='<span class=""wdir"" title=""Into the panel: changes what it shows"">In</span>'+forceBtnsHtml(e.dev,e.key,j);
        if(jo&&!jo.err)h+='<span class=""wdir"" title=""Out of the panel: runs the program as a press would"">Out</span>'+
          pressBtnsHtml(e.dev,pairInKey(e),jo)+badgeHtml(jo,'Panel press state')+lampHtml(e.dev+'|'+pairInKey(e),jo);
        else if(jo&&jo.err)h+='<span class=""werr"">'+esc(jo.err)+'</span>';
        else h+='<span class=""wval"" style=""color:var(--muted)"">&mdash;</span>';
        if(j.ferr)h+='<span class=""wferr"">'+esc(j.ferr)+'</span>';
      }else{
        // No direction labels: the editable cell is always the into-panel (In) side and the
        // plain-text cell the out-of-panel (Out) side; the row tooltip carries both keys.
        h+=watchCell(e,j,e.key);
        h+=watchCell(e,jo,pairInKey(e));
      }
    }else{
      h+=watchCell(e,j,e.key);
    }
    h+='<span style=""flex:1""></span>';
    h+=traceBtnHtml(e);
    h+='<button class=""wx"" title=""Remove this signal from the list."" onclick=""removeWatch(\''+escAttr(e.rid)+'\')"">&times;</button>';
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
  var ins=(d.sigs[{d:'din',a:'ain',s:'sin'}[type]])||[];      // into the panel  (UI ""In"")
  var outs=(d.sigs[{d:'dout',a:'aout',s:'sout'}[type]])||[];  // out of the panel (UI ""Out"")
  // s.friendly (contract manifests only) is a nicer DISPLAY label than the SDK's own s.name —
  // e.g. Left instead of CH5's generic Bool Sig Number 5. Addressing is always by NUMBER (see
  // the non-Both branch below), so friendly never touches what a row actually resolves against,
  // only what it's labeled.
  var disp=function(s){return s.friendly||s.name||('#'+s.n);};
  // Every pairing rule below produces the same shape: {outNum, inNum, label}. outNum is the
  // forceable (state) side driving the row's force buttons; inNum is the read-only (event)
  // side driving its activity lamp — they are the SAME number for an SGD panel's true In/Out
  // pair, but can be TWO DIFFERENT numbers for a contract pair (see Rule B), which is exactly
  // why this is a number pair and not a single shared address.
  var pairable=[];
  if(d.contract){
    // A contract's states and events are numbered by two INDEPENDENT counters, so a shared
    // NUMBER between them is coincidence, not a pairing (confirmed on a real manifest: join
    // 1005 is both a button's Enabled STATE and a DIFFERENT button's press EVENT) — so numeric
    // matching is never used here. Two rules that hold by NAME instead:
    //   Rule A — a state and an event with the IDENTICAL full name are the same control shown
    //            from its two directions (e.g. a Visible state that is also echoed as an event).
    //   Rule B — within one repeated item (paths sharing a leading ""Word+digits"" token, e.g.
    //            ""Button5""), a *Press event pairs with that same item's *Selected state — the
    //            momentary press and the item's selected/toggled condition, shown on one row.
    outs.forEach(function(so){
      var si=ins.find(function(s){return disp(s)===disp(so);});
      if(si) pairable.push({outNum:si.n,inNum:so.n,label:disp(si)});
    });
    var group=function(s){var m=/^([A-Za-z]+\d+)/.exec(disp(s));return m?m[1]:null;};
    var byGroup={};
    ins.forEach(function(s){var g=group(s);if(g)(byGroup[g]=byGroup[g]||{ins:[],outs:[]}).ins.push(s);});
    outs.forEach(function(s){var g=group(s);if(g)(byGroup[g]=byGroup[g]||{ins:[],outs:[]}).outs.push(s);});
    Object.keys(byGroup).forEach(function(g){
      var grp=byGroup[g];
      var selected=grp.ins.filter(function(s){return /selected$/i.test(disp(s));});
      var press=grp.outs.filter(function(s){return /press$/i.test(disp(s));});
      if(selected.length===1&&press.length===1)
        pairable.push({outNum:selected[0].n,inNum:press[0].n,label:g});
    });
  }else{
    var outByNum={};
    outs.forEach(function(s){outByNum[s.n]=disp(s);});
    ins.filter(function(s){return outByNum.hasOwnProperty(s.n);}).forEach(function(s){
      pairable.push({outNum:s.n,inNum:s.n,label:disp(s)+' / '+outByNum[s.n]});
    });
  }

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
    // Value carries BOTH numbers (outNum~inNum) since a contract pair (Rule B) can legitimately
    // be two different joins — addWatch splits this back apart.
    list=pairable.map(function(p){
      return {v:p.outNum+'~'+p.inNum,t:p.label+(p.outNum===p.inNum?' ('+p.outNum+')':' ('+p.outNum+'/'+p.inNum+')')};
    });
  }else{
    // Value is the NUMBER, never the name: CH5's smart objects (unlike an SGD-loaded one)
    // don't support by-name sig lookup at all, so a name-addressed key on one of them is a
    // permanent resolution error no matter how it's spelled. Numbers work identically for
    // both, so there is no reason to ever depend on name-lookup succeeding.
    //
    // For a contract device, s.name is CH5's own generic placeholder (""Bool Sig Number 7"") on
    // any join the manifest doesn't declare — the contract has no state/event for it at all, so
    // there is nothing real to show. Listing it anyway reads as a mismatch against the SAME SO's
    // other direction, which usually does have a real contract name (e.g. Out shows ""Up"" while
    // In shows ""Bool Sig Number 7"" for a join that isn't ""Up""'s counterpart, just an unrelated
    // undeclared one on the same object) — so a contract device only offers joins that have a
    // genuine contract-derived friendly name. Manual entry via the Join# box still reaches any
    // join the picker leaves out. An SGD-loaded device keeps the old behavior: its s.name is the
    // SDK's own name as configured in SIMPL, already meaningful, not a placeholder.
    list=(dir===''?ins:outs).filter(function(s){return d.contract?s.friendly:s.name;})
      .map(function(s){return {v:String(s.n),t:disp(s)+' ('+s.n+')'};});
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
function serialKey(ev,dev,key,input,inject){
  if(ev.key==='Escape'){editingWatch=null;input.blur();return;}
  if(ev.key!=='Enter')return;
  input.classList.remove('dirty');input.classList.add('pending');input.readOnly=true;
  editingWatch=null;
  var v='b64:'+b64url(input.value);
  if(inject)pressJoin(dev,key,'send',v);else forceJoin(dev,key,v);
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
// The signals poll runs on its own, far faster cadence than the log. An unchanged reply is
// ~70 bytes and the processor answers in single-digit milliseconds, so the constraint worth
// respecting is one round in flight at a time rather than a fixed rate: that holds
// press-to-screen latency near SIGNAL_POLL_MS and self-throttles over a slow link instead of
// queueing requests.
var SIGNAL_POLL_MS=250;
var signalTimer=null, signalsInFlight=0;
function pollSignals(){
  if(!watchOpen||!watchList.length||paused)return;
  if(signalsInFlight)return;                       // previous round still outstanding
  var byDev={}, trcDev={};
  watchList.forEach(function(e){
    var arr=(byDev[e.dev]=byDev[e.dev]||[]);
    arr.push(e.key);
    if(e.pair)arr.push(pairInKey(e));               // paired rows read both directions
    if(e.trace){
      // ""key"" or ""key:<base64url label>"" - the name shown on the row travels with it so the
      // log reads in the same terms, and base64url keeps a comma or colon in a name from
      // breaking the list. Key and label are kept apart here so the label can be dropped if
      // the composed URL would get too long (see below).
      var tr=(trcDev[e.dev]=trcDev[e.dev]||[]);
      var sfx=e.label?':'+b64url(e.label):'';
      tr.push({k:e.key,s:sfx});
      if(e.pair)tr.push({k:pairInKey(e),s:sfx});    // a traced pair logs both halves
    }
  });
  var devs=Object.keys(byDev);
  signalsInFlight=devs.length;
  var changed=false, flashed=false;
  devs.forEach(function(dev){
    var trc=trcDev[dev];
    // POST, not GET: the whole watch set AND the whole traced set (with their names) are re-sent
    // on every poll — that repetition is what makes tracing lapse by itself when this page goes
    // away — so with dozens of logged signals this payload is far too big to keep pushing through
    // a URL. In a body it has no practical size limit, so the list can grow to the server's own
    // key cap without the request itself ever becoming the constraint. The w/t strings are
    // formatted exactly as the query-string form, so the server parses both identically.
    fetch('api/joins',{method:'POST',headers:{'Content-Type':'application/json'},
      body:JSON.stringify({since:joinRev,dev:dev,w:byDev[dev].join(','),
        t:trc?trc.map(function(t){return t.k+t.s;}).join(','):''})})
      .then(function(r){return r.json();}).then(function(d){
        if(d.joins&&d.joins.length){
          d.joins.forEach(function(j){
            var id=dev+'|'+j.k;
            var old=joinVals[id];
            // chg counts rising edges server-side; shown as a 1s lamp, not a number
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
          changed=true;
          lastActivity=Date.now();          // values moving means someone is working
        }
        if(typeof d.lastRev==='number'&&d.lastRev>joinRev) joinRev=d.lastRev;
        if(typeof d.armed==='boolean') syncArm(d.armed,d.writesAllowed!==false);
        if(typeof d.canPress==='boolean') devCanPress[dev]=d.canPress;
        if(typeof d.online==='boolean') setDevOnline(dev,d.online);
      }).catch(function(){}).then(function(){
        if(--signalsInFlight>0)return;
        signalsInFlight=0;
        if(changed||flashed)renderWatch();
        if(flashed)setTimeout(renderWatch,1100);   // turn the lamp back off
      });
  });
}
function startSignals(){
  stopSignals();
  signalTimer=setInterval(pollSignals,SIGNAL_POLL_MS);
  pollSignals();
}
function stopSignals(){
  if(signalTimer){clearInterval(signalTimer);signalTimer=null;}
  signalsInFlight=0;
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
        pollSignals();
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
function analogKey(ev,dev,key,input,inject){
  if(ev.key==='Escape'){editingWatch=null;input.blur();return;}
  if(ev.key!=='Enter')return;
  var raw=input.value.trim();
  var n=parseInt(raw,10);
  if(!(n>=0&&n<=65535)){input.classList.add('dirty');return;}
  input.classList.remove('dirty');input.classList.add('pending');input.readOnly=true;
  editingWatch=null;
  if(inject)pressJoin(dev,key,'send',String(n));else forceJoin(dev,key,String(n));
  input.blur();
}
// Simulate panel activity on a read-only In signal so the program's handlers run. Arm-gated the
// same as forcing; the server audits every attempt. A digital act is press|release|tap (no value);
// analog/serial use act 'send' with a value.
function pressJoin(dev,key,act,val){
  if(!(armed&&writesAllowed)){setStatus('Arm forcing first (Disarmed button)');return;}
  if(!devCanPress[dev]){setStatus('Signal simulation is not available for this device');return;}
  var id=dev+'|'+key;
  var lbl=watchLabel(dev,key);
  var u='api/press?dev='+encodeURIComponent(dev)+'&k='+encodeURIComponent(key)+'&act='+encodeURIComponent(act)+
        (val!=null?'&v='+encodeURIComponent(val):'')+(lbl?'&n='+encodeURIComponent(lbl):'');
  fetch(u,{method:'POST'}).then(function(r){return r.json();}).then(function(d){
    var j=joinVals[id];
    if(d.ok){
      // The effect is the program's logic running, not a value this call returns; the next poll
      // reflects any resulting signal changes. Just clear any prior error and refresh soon.
      if(j)delete j.ferr;
      renderWatch();
      pollSignals();
    }else if(j){
      j.ferr=d.error||'rejected';
      renderWatch();
      setTimeout(function(){if(joinVals[id]===j){delete j.ferr;renderWatch();}},4000);
    }
  }).catch(function(){
    setStatus('Press failed: connection error');
    renderWatch();
  });
}
function escAttr(s){return String(s).replace(/\\/g,'\\\\').replace(/'/g,'\\\'').replace(/""/g,'&quot;').replace(/</g,'&lt;');}
// plain HTML-attribute escaping (value="", title="") — escAttr is ONLY for JS strings inside handlers
function escq(s){return esc(String(s)).replace(/""/g,'&quot;');}
function saveWatch(){try{localStorage.setItem('dbg-watchlist',JSON.stringify(watchList));}catch(x){}}
// Tracing writes a log line on every change of a signal, so a join can be followed without
// adding anything to the program. The traced set is asserted to the processor on every poll
// (see pollSignals), so it stops by itself if this page goes away.
function traceBtnHtml(e){
  var tip=e.trace?'Logging every change of this signal. Click to stop.'
                 :'Log every change of this signal, without adding anything to the program.';
  return '<button class=""wt'+(e.trace?' on':'')+'"" title=""'+tip+'"" onclick=""toggleTrace('+
         jsq(e.rid)+')"">Log</button>';
}
function jsq(v){return ""'""+escAttr(v)+""'"";}
function toggleTrace(rid){
  var e=findRow(rid);
  if(e){
    if(e.trace)delete e.trace;else e.trace=true;
    saveWatch();renderWatch();pollSignals();
  }
}
// UI direction convention (PANEL-centric, matching the Crestron API and Toolbox):
//   ""In""  = going IN to the panel   (API InputSig  — text/feedback). Changes what the panel
//            SHOWS and nothing else.
//   ""Out"" = coming OUT of the panel (API OutputSig — presses). Drives the program's own
//            SigChange handlers and Construct contract events, so it changes what the system
//            DOES (and whatever UI the program updates in response).
// Both directions are settable and both use the same Pulse / High / Low controls; the difference
// is what a change reaches, not whether it can be changed.
// This deliberately matches the API's own naming, so the wire keys line up with the labels: the
// 'o' prefix (od5) is the OUTPUT sig, which is the side now labelled ""Out"".
function oKey(k){
  // d5 -> od5, so1.d3 -> so1.od3 : the Out (out-of-panel) twin of an In key, SAME number.
  var i=k.indexOf('.');
  return i>=0?k.substring(0,i+1)+'o'+k.substring(i+1):'o'+k;
}
// The in-side (read-only) key for a paired row. Almost always the same number as the out
// side (oKey derives it by string surgery alone) — except a contract name-based pair (see
// fillSigOptions' Rule B: an ...Press event combined with an unrelated-numbered ...Selected
// state) genuinely lives at a DIFFERENT join, so addWatch stores that explicit key on the row
// as e.key2 and this just prefers it when present. Nothing else needs to know the difference.
function pairInKey(e){return e.key2||oKey(e.key);}
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
  var addr='', autoName='', inAddr='';
  if(so&&pair){
    // Paired SO row: the picker's value is ""outNum~inNum"" (see fillSigOptions) — the same
    // number on both sides for an SGD panel's true In/Out pair, or two different numbers for
    // a contract's name-matched pair (Rule A/B there). Falling back to the Join# box only
    // supports the same-number case, same as before — a name-picked contract combo is the
    // only way to get differing numbers, so there is nothing to type manually for that.
    if(sigVal){
      var parts=sigVal.split('~');
      addr=parts[0];inAddr=parts[1];
      autoName=sigSel.options[sigSel.selectedIndex].textContent;
    }
    else if(join&&parseInt(join,10)>0){addr=join;inAddr=join;}
    else return;
  }else if(so&&sigVal){
    // sigVal is now the NUMBER (see fillSigOptions) — resolution is always by join number;
    // the option's text (SDK name, or the contract's friendlier name when known) becomes this
    // row's label, prefixed with the smart object's own name when one is known (""Dpad - Left""
    // rather than a bare ""Left"" that gives no hint which control it came from).
    addr=sigVal;
    var soHit=currentDeviceSoIds().find(function(x){return String(x.id)===so;});
    autoName=(soHit&&soHit.name?soHit.name+' - ':'')+sigSel.options[sigSel.selectedIndex].textContent;
  }else{
    if(!join||!(parseInt(join,10)>0))return;             // plain join number
    addr=join;
  }
  var key=(so?'so'+so+'.':'')+(pair?'':dir)+type+addr;   // pair rows store the In (into-panel) key
  if(!name&&autoName)name=autoName;                      // a picked sig name is its own friendly name
  var e={dev:dev,key:key,trace:true};   // Log defaults on for a newly added row
  if(pair){
    e.pair=true;
    // Only stored when it differs from the plain oKey() derivation — a same-number pair (every
    // SGD row, and a contract Rule A match) leaves this unset and pairInKey() falls back to
    // oKey() exactly as before; only a genuinely different-numbered contract Rule B combo needs
    // its own explicit in-side key.
    if(inAddr&&inAddr!==addr) e.key2=(so?'so'+so+'.':'')+'o'+type+inAddr;
  }
  // The in-side key is part of a row's identity, not just its display: two different contract
  // pairs can share an out-side join and differ only on the in side (one control's state and
  // another's event landing on the same number is a documented real case), so comparing
  // (dev,key,pair) alone would silently swallow the second one as a duplicate.
  for(var i=0;i<watchList.length;i++){
    var x=watchList[i];
    if(x.dev===dev&&x.key===key&&!!x.pair===pair&&(!pair||pairInKey(x)===pairInKey(e)))return;
  }
  if(name)e.label=name;
  e.rid=nextRid();
  watchList.push(e);
  document.getElementById('awName').value='';
  saveWatch();
  joinRev=0;               // watch set changed: next poll fetches everything
  renderWatch();
  pollSignals();
}
function renameWatch(rid){
  var e=findRow(rid);
  if(!e)return;
  var n=prompt('Name for '+e.dev+' '+e.key+':',e.label||'');
  if(n===null)return;
  n=n.trim();
  if(n)e.label=n;else delete e.label;
  saveWatch();renderWatch();
}
function removeWatch(rid){
  var e=findRow(rid);
  if(!e)return;
  watchList=watchList.filter(function(x){return x.rid!==rid;});
  delete joinVals[e.dev+'|'+e.key];
  if(e.pair)delete joinVals[e.dev+'|'+pairInKey(e)];   // a pair caches BOTH halves
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
    startSignals();
  }else{
    stopSignals();
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
var devicesCache=[];   // last api/devices response; feeds the SO#-suggestion datalist
function loadDevices(){
  // refetched on every panel open (not cached) so the online/offline label stays honest;
  // the user's selection is preserved across the refresh
  fetch('api/devices').then(function(r){return r.json();}).then(function(d){
    devicesCache=d.devices||[];
    var sel=document.getElementById('awDev');
    var prev=sel.value;
    sel.innerHTML='';
    devicesCache.forEach(function(dv){
      var o=document.createElement('option');
      o.value=dv.name;o.textContent=dv.name+(dv.online===false?' (offline)':'');
      sel.appendChild(o);
    });
    if(prev)sel.value=prev;
    populateSoList();
  }).catch(function(){});
}
// Suggestions only — so8.od5 still resolves by number even for an unlisted or unnamed SO.
// Names come from whichever of RegisterDevice's sgdPath or RegisterContract's manifest the
// host program supplied; a device registered with neither just offers bare numbers, and the
// menu stays empty (renderSoMenu below just shows nothing rather than an empty popup).
function populateSoList(){ closeSoMenu(); showSoName(); }
function currentDeviceSoIds(){
  var dev=devicesCache.find(function(dv){return dv.name===document.getElementById('awDev').value;});
  return (dev&&dev.soIds)||[];
}
function showSoName(){
  var so=document.getElementById('awSo').value.trim();
  var span=document.getElementById('awSoName');
  var hit=so&&currentDeviceSoIds().find(function(s){return String(s.id)===so;});
  span.textContent=hit&&hit.name?hit.name:'';
}
// Own scrollable dropdown instead of a native <datalist> — datalist popups can't be themed and
// on some browsers render far larger than the input they belong to. Filters as you type: a
// digit-only entry matches by ID prefix, anything else matches the name (so typing ""dpad""
// finds SO 8 without knowing its number).
// forceAll=true (on focus) always shows the whole list regardless of what's already typed —
// re-opening the box to pick a DIFFERENT SO shouldn't start pre-filtered down to the one
// already chosen. Typing after that still filters normally via oninput's own call.
function renderSoMenu(forceAll){
  var q=forceAll?'':document.getElementById('awSo').value.trim().toLowerCase();
  var all=currentDeviceSoIds();
  var list=!q?all:all.filter(function(s){
    return String(s.id).indexOf(q)===0||(s.name||'').toLowerCase().indexOf(q)>=0;
  });
  var menu=document.getElementById('awSoMenu');
  if(!all.length){menu.classList.remove('open');menu.innerHTML='';return;}
  menu.innerHTML=list.length?list.map(function(s){
    return '<div class=""somenu-item"" onmousedown=""pickSo('+s.id+')"">'+s.id+
      (s.name?'<span class=""n"">'+esc(s.name)+'</span>':'')+'</div>';
  }).join(''):'<div class=""somenu-empty"">No match</div>';
  menu.classList.add('open');
}
function closeSoMenu(){document.getElementById('awSoMenu').classList.remove('open');}
function pickSo(id){
  document.getElementById('awSo').value=id;
  closeSoMenu();showSoName();refreshSigOptions();
}
var needRender=false;  // persists across polls: set when data changes, cleared only by a render.
                       // If one fetch of a poll succeeds (appending entries / advancing lastSeq)
                       // but a sibling fetch rejects, the flag survives so the NEXT successful
                       // poll paints the stranded entries instead of losing them until new data.
function refresh(){
  var tasks=[fetch('api/logs?since='+lastSeq).then(function(r){return r.json();}).then(function(d){
    if(d.entries&&d.entries.length){
      needRender=true;
      lastActivity=Date.now();             // new log entries mean the system is busy
      debuggerEntries=debuggerEntries.concat(d.entries);
      // bound client memory to the server ring size; the server already bounds what it sends
      if(debuggerEntries.length>CLIENT_MAX) debuggerEntries=debuggerEntries.slice(debuggerEntries.length-CLIENT_MAX);
    }
    if(typeof d.lastSeq==='number') lastSeq=d.lastSeq;
    if(typeof d.devices==='number') setWatchAvailable(d.devices>0);
  })];
  if(showProc) tasks.push(fetch('api/errorlog').then(function(r){return r.json();}).then(function(d){
    var next=d.entries||[];
    // content compare, not length: the console error log is a bounded ring, so once full a
    // new entry displaces the oldest and the line count stays identical. The log is small
    // (bounded console buffer), so a stringify compare per poll is cheap and bulletproof.
    if(JSON.stringify(next)!==JSON.stringify(processorEntries)) needRender=true;
    processorEntries=next;
  }));
  Promise.all(tasks).then(function(){
    if(needRender){applyFilter();needRender=false;}
    renderStatus();
  }).catch(function(){document.getElementById('status').textContent='Connection error';});
}
var time12h=true;   // persisted; the header ""Updated"" clock only — log-entry timestamps are
                    // left as the server's own fixed format, which is unambiguous either way.
function formatTime(d){
  var h=d.getHours(),m=pad(d.getMinutes()),s=pad(d.getSeconds());
  if(!time12h) return pad(h)+':'+m+':'+s;
  var ap=h<12?'AM':'PM'; h=h%12; if(h===0)h=12;
  return h+':'+m+':'+s+' '+ap;
}
function renderStatus(){
  var note=LOGGING_ENABLED?'':'  — LOGGING OFF';
  document.getElementById('status').textContent='Updated '+formatTime(new Date())+note;
}
function toggleTimeFormat(){
  time12h=!time12h;
  try{localStorage.setItem('dbg-time-12h',time12h?'1':'0');}catch(x){}
  renderStatus();
}
function clearLogs(){
  fetch('api/clear',{method:'POST'}).then(function(){debuggerEntries=[];applyFilter();});
}
function visibleEntries(){
  var q=document.getElementById('search').value.toLowerCase().trim();
  var src=combined().filter(function(e){return levelOn[e.level]!==false;});
  return q?src.filter(function(e){
    return e.msg.toLowerCase().indexOf(q)>=0||e.level.toLowerCase().indexOf(q)>=0||
           (bytesMode!=='raw'&&plainText(e.msg).toLowerCase().indexOf(q)>=0);
  }):src;
}
function pad(n){return n<10?'0'+n:''+n;}
function exportLog(){
  var entries=visibleEntries();
  var lines=entries.map(function(e){
    var src=e.source==='Processor'?'SYS':'APP';
    return (e.ts||'')+'  ['+src+'] '+(e.level||'').toUpperCase()+'\t'+plainText(e.msg);
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
// ── When this page polls ─────────────────────────────────────────────────────
// Three separate things are easily confused, so to be explicit: the Logging button is a
// RECORDING switch on the processor and has nothing to do with this page's requests; Pause
// is a manual stop of this page's requests; and the rules below stop them on their own so a
// forgotten tab cannot poll a processor for days.
//
// Polling stops while the tab is hidden (nobody can read it anyway) and after
// IDLE_SUSPEND_MS in which neither the user touched the page nor any value arrived. Received
// changes count as activity, so watching a panel being pressed — which involves no browser
// input at all — never suspends. It resumes on a click, a keystroke, or the tab being shown.
var IDLE_SUSPEND_MS=900000;                     // 15 minutes
var lastActivity=Date.now(), suspended=false, suspendReason='';
function pollingWanted(){return !paused&&!suspended;}
function applyPolling(){
  if(pollingWanted()){
    if(!pollTimer){pollTimer=setInterval(refresh,2000);refresh();}
    if(watchOpen&&!signalTimer)startSignals();
  }else{
    if(pollTimer){clearInterval(pollTimer);pollTimer=null;}
    stopSignals();
  }
}
function noteActivity(){
  lastActivity=Date.now();
  if(suspended&&suspendReason==='idle')resumePolling();
}
function suspendPolling(reason){
  if(suspended||paused)return;
  suspended=true;suspendReason=reason;
  applyPolling();
  if(reason==='idle')setStatus('Idle — updates stopped. Click the page to resume.');
}
function resumePolling(){
  if(!suspended)return;
  suspended=false;suspendReason='';lastActivity=Date.now();
  applyPolling();
}
function togglePause(){
  // Manual stop of this page's requests. The processor keeps recording while paused, so
  // resuming fetches everything logged during the freeze (subject to the server ring cap).
  paused=!paused;
  var b=document.getElementById('pauseBtn');
  b.textContent=paused?'Resume':'Pause';
  b.classList.toggle('on',paused);
  if(paused){
    applyPolling();
    setStatus('Paused — page not updating; the processor keeps recording');
  }else{
    suspended=false;suspendReason='';lastActivity=Date.now();
    applyPolling();
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
    var t12=localStorage.getItem('dbg-time-12h'); if(t12!==null) time12h=t12==='1';
    var lv=localStorage.getItem('dbg-levels');
    if(lv){var s=JSON.parse(lv);for(var k in s){if(levelOn.hasOwnProperty(k))levelOn[k]=s[k];}}
    var bm=localStorage.getItem('dbg-bytes'); if(bm==='x'||bm==='b'||bm==='raw') bytesMode=bm;
  }catch(x){}
  updateBytesBtn();
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
      // Rows saved before row ids existed have none: give them one now, and start the counter
      // past anything already in use so a reload can never reissue a live id.
      watchList.forEach(function(e){
        if(typeof e.rid==='string'){
          var n=parseInt(e.rid.substring(1),10);
          if(n>=rowSeq)rowSeq=n+1;
        }
      });
      watchList.forEach(function(e){if(typeof e.rid!=='string')e.rid=nextRid();});
      var ww=parseInt(localStorage.getItem('dbg-watch-w')||'',10);
      if(ww>=280)document.getElementById('watchPanel').style.width=ww+'px';
      if(localStorage.getItem('dbg-watch-open')==='1'){
        watchOpen=true;
        document.getElementById('watchPanel').style.display='';
        document.getElementById('wsplit').style.display='';
        document.getElementById('watchBtn').classList.add('on');
        loadDevices();
        startSignals();
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
setInterval(function(){
  if(!pollingWanted())return;
  if(Date.now()-lastActivity>IDLE_SUSPEND_MS)suspendPolling('idle');
},30000);
document.addEventListener('visibilitychange',function(){
  if(document.hidden)suspendPolling('hidden');
  else if(suspendReason==='hidden')resumePolling();
});
document.addEventListener('mousedown',noteActivity);
document.addEventListener('keydown',noteActivity);
document.addEventListener('wheel',noteActivity);
applyPolling();
</script>
</body>
</html>";
    }
}
