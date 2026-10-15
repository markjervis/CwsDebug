// -----------------------------------------------------------------------------
//  Crestron API compatibility shims.
//
//  Lets existing code that calls Crestron's static ErrorLog.* and CrestronConsole.Print*
//  start writing to the CwsDebug page by changing a single using-alias, with no edits at
//  the call sites:
//
//      // using Crestron.SimplSharp;                     // before: ErrorLog / CrestronConsole
//      using ErrorLog        = CwsDebug.CwsErrorLog;
//      using CrestronConsole = CwsDebug.CwsConsole;
//
//  Both classes expose the COMPLETE public static surface of the real SDK class (same
//  names, parameter names, parameter types, ref/out-ness and return types), so every
//  existing call binds unchanged. The logging methods (ErrorLog.*, Print, PrintLine) are
//  no-throw. CwsConsole's other members (console commands, ConsoleCommandResponse,
//  SendControlSystemCommand, ConsoleRegistered) are pure forwards to the real
//  CrestronConsole: same results, same exceptions.
// -----------------------------------------------------------------------------

using System.Text;                  // StringBuilder — CwsConsole partial-line buffer

namespace CwsDebug
{
    /// <summary>
    /// Drop-in replacement for Crestron's static <c>ErrorLog</c>, writing to the CwsDebug page.
    /// Use via <c>using ErrorLog = CwsDebug.CwsErrorLog;</c>.
    /// <para>
    /// IMPORTANT: by default these calls write ONLY to the debug page's buffer, NOT to the
    /// processor error log. Set <see cref="MirrorToErrorLog"/> to also forward each call to the
    /// real error log — note that on an appliance each message will then appear twice (once in
    /// the app log on the debug page, once in the processor error log view).
    /// </para>
    /// <para>
    /// Page recording honours <see cref="CwsDebugPage.LoggingEnabled"/> and
    /// <see cref="CwsDebugPage.MinLevel"/> like the Log* methods. Level mapping: Info to Info,
    /// Notice to Notice, Warn to Warning, Error and Exception to Error, Ok to Notice.
    /// </para>
    /// </summary>
    public static class CwsErrorLog
    {
        private static volatile bool _mirror;

        // Two distinct, non-IFormattable stand-ins for argument 0, used by UsesArg0AsTemplate.
        private sealed class Sentinel
        {
            private readonly string _text;
            public Sentinel(string text) => _text = text;
            public override string ToString() => _text;
        }
        private static readonly object[] _sentinelA = { new Sentinel("\u0001A\u0002") };
        private static readonly object[] _sentinelB = { new Sentinel("\u0001B\u0002") };

        // True when msginfo is a valid string.Format template that actually uses argument 0
        // (e.g. {0}, {0:X}, {0,8}, {0,-8:N2}, {0 }, {00}, {{{0}}}). Rather than re-implementing
        // .NET's composite-format grammar, ask the formatter itself (same culture as
        // CwsDebugPage.SafeFormat): format with two different sentinels; the outputs differ only
        // if arg 0 is referenced. A throw (reference to {1}, malformed braces, null) or equal
        // outputs (no arg-0 placeholder, or only an escaped {{0}}) means "not a template".
        internal static bool UsesArg0AsTemplate(string? msginfo)
        {
            if (msginfo == null || msginfo.IndexOf('{') < 0) return false;   // fast path: no braces
            try { return !string.Equals(string.Format(msginfo, _sentinelA), string.Format(msginfo, _sentinelB), StringComparison.Ordinal); }
            catch { return false; }
        }

        /// <summary>
        /// When true, every call is ALSO forwarded, with the original arguments, to the matching
        /// overload of Crestron.SimplSharp.ErrorLog. Defaults to false. Mirroring is independent
        /// of <see cref="CwsDebugPage.LoggingEnabled"/> and <see cref="CwsDebugPage.MinLevel"/>:
        /// it happens even when the page buffer drops the message. Expect each message to show up
        /// twice on an appliance (debug page plus processor error log). Exceptions thrown by the
        /// real error log (e.g. FormatException on stray braces) are swallowed.
        /// </summary>
        public static bool MirrorToErrorLog { get => _mirror; set => _mirror = value; }

        // Records template+args at the given level; Append formats safely. Never throws.
        private static void Rec(string level, string message, object?[] args)
        {
            try { CwsDebugPage.Record(level, message ?? string.Empty, args ?? Array.Empty<object>()); }
            catch { /* never let logging throw */ }
        }

        /// <summary>Records an error-level message (string.Format template plus args).</summary>
        public static void Error(string message, params object?[] args)
        {
            Rec("Error", message, args);
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Error(message, args); } catch { } }
        }

        /// <summary>Records an error-level message. Braces are taken literally (no formatting).</summary>
        public static void Error(string message)
        {
            Rec("Error", message, Array.Empty<object>());
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Error(message); } catch { } }
        }

        /// <summary>
        /// Records an error-level message from a (msginfo, errorstring) pair. As in the SDK,
        /// <paramref name="msginfo"/> is the method name or other context information and
        /// <paramref name="errorstring"/> the error text; the page shows "msginfo: errorstring".
        /// As a convenience, if <paramref name="msginfo"/> is a valid format template that uses
        /// argument 0 (e.g. {0}, {0:X}, {0,8}; an escaped {{0}} does not count, nor does a
        /// template that also references {1} or has malformed braces), it is instead formatted
        /// with <paramref name="errorstring"/> as argument 0. That covers the common
        /// <c>Error("Failed {0}", x)</c> call with a string x, which C# binds to this overload
        /// rather than to the params one. When mirroring is on, the call is forwarded unchanged
        /// to the real two-string overload.
        /// </summary>
        public static void Error(string msginfo, string errorstring)
        {
            try
            {
                var info = msginfo ?? string.Empty;
                if (UsesArg0AsTemplate(info)) Rec("Error", info, new object?[] { errorstring });
                else Rec("Error", info + ": " + errorstring, Array.Empty<object>());
            }
            catch { }
            // '!' only silences flow analysis (the ?? above marks msginfo maybe-null); args forwarded unchanged.
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Error(msginfo!, errorstring); } catch { } }
        }

        /// <summary>
        /// Records an error-level message: <paramref name="msg"/>, a newline, then the full
        /// Exception.ToString() (type, message, stack trace, inner exceptions). A null exception is tolerated.
        /// </summary>
        public static void Exception(string msg, Exception ex)
        {
            try { CwsDebugPage.LogException(ex, msg); } catch { }
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Exception(msg, ex); } catch { } }
        }

        /// <summary>Records an informational message (string.Format template plus args).</summary>
        public static void Info(string message, params object?[] args)
        {
            Rec("Info", message, args);
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Info(message, args); } catch { } }
        }

        /// <summary>Records an informational message. Braces are taken literally.</summary>
        public static void Info(string message)
        {
            Rec("Info", message, Array.Empty<object>());
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Info(message); } catch { } }
        }

        /// <summary>Records a notice-level message (string.Format template plus args).</summary>
        public static void Notice(string message, params object?[] args)
        {
            Rec("Notice", message, args);
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Notice(message, args); } catch { } }
        }

        /// <summary>Records a notice-level message. Braces are taken literally.</summary>
        public static void Notice(string message)
        {
            Rec("Notice", message, Array.Empty<object>());
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Notice(message); } catch { } }
        }

        /// <summary>
        /// Records an "OK" message (string.Format template plus args). The debug page has no OK
        /// level, so it is recorded at Notice level.
        /// </summary>
        public static void Ok(string message, params object?[] args)
        {
            Rec("Notice", message, args);
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Ok(message, args); } catch { } }
        }

        /// <summary>Records an "OK" message at Notice level. Braces are taken literally.</summary>
        public static void Ok(string message)
        {
            Rec("Notice", message, Array.Empty<object>());
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Ok(message); } catch { } }
        }

        /// <summary>Records a warning-level message (string.Format template plus args).</summary>
        public static void Warn(string message, params object?[] args)
        {
            Rec("Warning", message, args);
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Warn(message, args); } catch { } }
        }

        /// <summary>Records a warning-level message. Braces are taken literally.</summary>
        public static void Warn(string message)
        {
            Rec("Warning", message, Array.Empty<object>());
            if (_mirror) { try { Crestron.SimplSharp.ErrorLog.Warn(message); } catch { } }
        }
    }

    /// <summary>
    /// Drop-in replacement for Crestron's static <c>CrestronConsole</c>: <c>Print</c>/<c>PrintLine</c>
    /// write to the CwsDebug page, and every other public member of the real class is present as a
    /// pure forward, so a file can switch with <c>using CrestronConsole = CwsDebug.CwsConsole;</c>
    /// and no edits at the call sites.
    /// <para>
    /// The console is a character stream but the page is a list of entries, so output is
    /// line-buffered: <c>Print</c> text is held until a newline arrives, each complete line
    /// becomes one entry (at <see cref="PrintLevel"/>), and <c>PrintLine</c> also flushes whatever
    /// is pending. Blank lines are skipped. A pending fragment is flushed as a line once it
    /// exceeds 4096 characters. By default nothing reaches the real console; see
    /// <see cref="PassThrough"/>.
    /// </para>
    /// <para>
    /// A fragment left pending (no newline) for about 2 s is flushed as its own entry on the next
    /// periodic tick (only after <see cref="CwsDebugPage.Register(string, int)"/> has run), so
    /// <c>Print("Connecting...")</c> becomes visible. Consequently a line printed in pieces with
    /// long pauses between them can appear split across entries. Pending text is also flushed at
    /// program stop.
    /// </para>
    /// <para>
    /// <c>AddNewConsoleCommand</c>, <c>RemoveConsoleCommand</c>, <c>ConsoleCommandResponse</c>,
    /// <c>SendControlSystemCommand</c> and <c>ConsoleRegistered</c> are pure forwards to
    /// Crestron.SimplSharp.CrestronConsole: they are NOT no-throw, return exactly what the real API
    /// returns, ignore <see cref="PassThrough"/>, and record nothing on the page.
    /// </para>
    /// </summary>
    public static class CwsConsole
    {
        private const int MaxPartial = 4096;

        private static readonly object _gate = new();
        private static readonly StringBuilder _partial = new();
        private static long _pendingSince;     // TickCount64 when _partial last went empty -> non-empty; guarded by _gate
        private static volatile bool _passThrough;

        /// <summary>
        /// Level at which all console output is recorded on the debug page. Defaults to Info. At
        /// Fatal, each line is handled like <see cref="CwsDebugPage.LogFatal(string, object[])"/>:
        /// recorded on the page and also written to the processor error log.
        /// </summary>
        public static CwsDebugPage.DebugLevel PrintLevel { get; set; } = CwsDebugPage.DebugLevel.Info;

        /// <summary>
        /// When true, every call is ALSO forwarded, with the original arguments, to the real
        /// Crestron.SimplSharp.CrestronConsole.Print/PrintLine. Defaults to false. Independent of
        /// the page's <see cref="CwsDebugPage.LoggingEnabled"/>/<see cref="CwsDebugPage.MinLevel"/>.
        /// Exceptions from the real console are swallowed.
        /// </summary>
        public static bool PassThrough { get => _passThrough; set => _passThrough = value; }

        /// <summary>
        /// Appends formatted text to the line buffer; each completed line becomes one page entry
        /// and any trailing fragment stays pending until a newline or <see cref="PrintLine(string)"/>.
        /// </summary>
        public static void Print(string message, params object[] args)
        {
            Feed(message, args, flushAll: false);
            if (_passThrough) { try { Crestron.SimplSharp.CrestronConsole.Print(message, args); } catch { } }
        }

        /// <summary>Appends text (braces taken literally) to the line buffer; see <see cref="Print(string, object[])"/>.</summary>
        public static void Print(string message)
        {
            Feed(message, null, flushAll: false);
            if (_passThrough) { try { Crestron.SimplSharp.CrestronConsole.Print(message); } catch { } }
        }

        /// <summary>
        /// Appends formatted text, then flushes everything pending (including any fragment left by
        /// earlier <c>Print</c> calls) as page entries.
        /// </summary>
        public static void PrintLine(string message, params object[] args)
        {
            Feed(message, args, flushAll: true);
            if (_passThrough) { try { Crestron.SimplSharp.CrestronConsole.PrintLine(message, args); } catch { } }
        }

        /// <summary>Appends text (braces taken literally), then flushes everything pending; see <see cref="PrintLine(string, object[])"/>.</summary>
        public static void PrintLine(string message)
        {
            Feed(message, null, flushAll: true);
            if (_passThrough) { try { Crestron.SimplSharp.CrestronConsole.PrintLine(message); } catch { } }
        }

        // Single internal path. args == null means the single-string overload: no formatting.
        private static void Feed(string message, object[]? args, bool flushAll)
        {
            try
            {
                var text = args == null ? message : CwsDebugPage.SafeFormat(message, args);
                text ??= string.Empty;

                string? ready = null;   // text to emit as lines
                // LOCK ORDERING: Emit runs while _gate is held, so lines reach the page in buffer
                // order even when Print/PrintLine race each other or the periodic FlushPending.
                // Order is one-way: CwsConsole._gate -> CwsDebugPage internals (its _gate for lazy
                // singleton creation, _lock in Append). That is deadlock-free because nothing
                // holding a CwsDebugPage lock calls into CwsConsole (its only callers, the CTimer
                // tick in Register and Cleanup, run outside every lock), and Record/Append/LogFatal
                // never call back into CwsConsole. PassThrough to the real console stays outside.
                lock (_gate)
                {
                    var wasEmpty = _partial.Length == 0;
                    var restarted = false;   // buffer was emptied (and possibly refilled) by this call
                    _partial.Append(text);

                    if (flushAll)
                    {
                        ready = _partial.ToString();
                        _partial.Clear();
                        restarted = true;
                    }
                    else
                    {
                        // Invariant: between calls the buffer never holds a '\n', so only the new
                        // text needs scanning. No newline in it means no copy: just append.
                        if (text.IndexOf('\n') >= 0)
                        {
                            var s = _partial.ToString();
                            var nl = s.LastIndexOf('\n');
                            ready = s.Substring(0, nl);
                            _partial.Clear().Append(s, nl + 1, s.Length - nl - 1);
                            restarted = true;
                        }

                        // Bound memory: a long fragment with no newline is emitted as a line.
                        if (_partial.Length > MaxPartial)
                        {
                            ready = (ready == null ? string.Empty : ready + "\n") + _partial.ToString();
                            _partial.Clear();
                            restarted = true;
                        }
                    }

                    // Pending-since: reset whenever the buffer is empty; (re)start the clock when it
                    // goes empty -> non-empty (a remainder left after a split is a new fragment).
                    if (_partial.Length == 0) _pendingSince = 0;
                    else if (wasEmpty || restarted) _pendingSince = Environment.TickCount64;

                    if (ready != null) Emit(ready);   // under _gate: see LOCK ORDERING above
                }
            }
            catch { /* never let logging throw */ }
        }

        /// <summary>
        /// Emits the pending <c>Print</c> fragment (text not yet ended by a newline) as its own
        /// entry, if one exists and has been pending for at least <paramref name="minAgeMs"/>
        /// milliseconds. Called by the debug page's periodic tick (with ~2000 ms) and at program
        /// stop (with 0). Never throws.
        /// </summary>
        internal static void FlushPending(long minAgeMs = 0)
        {
            try
            {
                lock (_gate)   // Emit under _gate keeps buffer order; see LOCK ORDERING in Feed
                {
                    if (_partial.Length > 0 && Environment.TickCount64 - _pendingSince >= minAgeMs)
                    {
                        var ready = _partial.ToString();
                        _partial.Clear();
                        _pendingSince = 0;
                        Emit(ready);
                    }
                }
            }
            catch { /* never let logging throw */ }
        }

        /// <summary>
        /// Publishes any pending <c>Print</c> fragment (text not yet ended by a newline) to the
        /// debug page immediately, as its own entry, without waiting for the periodic tick. Does
        /// nothing when nothing is pending. Never throws.
        /// </summary>
        public static void Flush() => FlushPending(0);

        // Caller holds _gate. Records each non-blank line (trailing '\r' trimmed) with no args, so
        // a line is never re-formatted. At Fatal level each line goes through CwsDebugPage.LogFatal,
        // which records it AND writes it to the processor error log via a "{0}" template.
        private static void Emit(string text)
        {
            var lvl   = PrintLevel;   // read once so one batch can't mix levels
            var fatal = lvl == CwsDebugPage.DebugLevel.Fatal;
            var level = lvl.ToString();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (fatal) CwsDebugPage.LogFatal(line);
                    else CwsDebugPage.Record(level, line, Array.Empty<object>());
                }
                catch { }
            }
        }

        // ---- Pure forwards ---------------------------------------------------------------
        // The rest of CrestronConsole's public static surface, so an aliased file still compiles
        // when it registers console commands etc. These are NOT no-throw and record nothing:
        // return values and exceptions pass through from the real API unchanged.

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.AddNewConsoleCommand (5-argument
        /// overload). Not recorded on the page; return value and exceptions pass through unchanged.
        /// </summary>
        public static bool AddNewConsoleCommand(Crestron.SimplSharp.SimplSharpProConsoleCmdFunction userFunction,
            string userCmdName, string userCmdHelp, Crestron.SimplSharp.ConsoleAccessLevelEnum userAccess,
            bool bIsCommandHidden)
            => Crestron.SimplSharp.CrestronConsole.AddNewConsoleCommand(userFunction, userCmdName, userCmdHelp,
                userAccess, bIsCommandHidden);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.AddNewConsoleCommand (4-argument
        /// overload). Not recorded on the page; return value and exceptions pass through unchanged.
        /// </summary>
        public static bool AddNewConsoleCommand(Crestron.SimplSharp.SimplSharpProConsoleCmdFunction userFunction,
            string userCmdName, string userCmdHelp, Crestron.SimplSharp.ConsoleAccessLevelEnum userAccess)
            => Crestron.SimplSharp.CrestronConsole.AddNewConsoleCommand(userFunction, userCmdName, userCmdHelp,
                userAccess);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.RemoveConsoleCommand. Return value and
        /// exceptions pass through unchanged.
        /// </summary>
        public static bool RemoveConsoleCommand(string userCmdName)
            => Crestron.SimplSharp.CrestronConsole.RemoveConsoleCommand(userCmdName);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.ConsoleCommandResponse (the reply to
        /// whoever typed the console command). ALWAYS forwarded, regardless of
        /// <see cref="PassThrough"/>, and never recorded on the page. Exceptions pass through.
        /// </summary>
        public static void ConsoleCommandResponse(string message, params object[] args)
            => Crestron.SimplSharp.CrestronConsole.ConsoleCommandResponse(message, args);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.ConsoleCommandResponse (braces taken
        /// literally). ALWAYS forwarded, regardless of <see cref="PassThrough"/>, and never recorded
        /// on the page. Exceptions pass through.
        /// </summary>
        public static void ConsoleCommandResponse(string message)
            => Crestron.SimplSharp.CrestronConsole.ConsoleCommandResponse(message);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand. Return value,
        /// response and exceptions pass through unchanged; nothing is recorded on the page.
        /// </summary>
        public static bool SendControlSystemCommand(string commandToSend, ref string pResponse, uint timeOutInMsec)
            => Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand(commandToSend, ref pResponse, timeOutInMsec);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand. Return value,
        /// response and exceptions pass through unchanged; nothing is recorded on the page.
        /// </summary>
        public static bool SendControlSystemCommand(string commandToSend, ref string pResponse)
            => Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand(commandToSend, ref pResponse);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand. Note the SDK
        /// declares the StringBuilder response as <c>out</c> (not <c>ref</c>); mirrored exactly.
        /// Return value, response and exceptions pass through unchanged.
        /// </summary>
        public static bool SendControlSystemCommand(string commandToSend, out StringBuilder? pResponse, uint timeOutInMsec)
            => Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand(commandToSend, out pResponse, timeOutInMsec);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand. Note the SDK
        /// declares the StringBuilder response as <c>out</c> (not <c>ref</c>); mirrored exactly.
        /// Return value, response and exceptions pass through unchanged.
        /// </summary>
        public static bool SendControlSystemCommand(string commandToSend, out StringBuilder? pResponse)
            => Crestron.SimplSharp.CrestronConsole.SendControlSystemCommand(commandToSend, out pResponse);

        /// <summary>
        /// Pure forward to Crestron.SimplSharp.CrestronConsole.ConsoleRegistered (get-only, as in the
        /// SDK). Exceptions pass through.
        /// </summary>
        public static bool ConsoleRegistered => Crestron.SimplSharp.CrestronConsole.ConsoleRegistered;
    }
}
