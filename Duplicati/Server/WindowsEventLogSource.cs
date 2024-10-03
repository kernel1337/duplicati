// Copyright (C) 2024, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using Duplicati.Library.Logging;

namespace Duplicati.Server
{
    /// <summary>
    /// Writes log messages to the Windows Event Log
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class WindowsEventLogSource : ILogDestination, IDisposable
    {
        /// <summary>
        /// The event log to write to
        /// </summary>
        private readonly EventLog m_eventLog;

        /// <summary>
        /// Initializes a new instance of the <see cref="WindowsEventLogSource"/> class.
        /// </summary>
        /// <param name="source">The source of the log messages</param>
        /// <param name="log">The log to write to</param>
        public WindowsEventLogSource(string source, string log = "Application")
        {
            m_eventLog = new EventLog
            {
                Source = source,
                Log = log
            };
        }

        /// <summary>
        /// Checks if the source exists
        /// </summary>
        /// <param name="source">The source to check</param>
        /// <returns>True if the source exists</returns>
        public static bool SourceExists(string source)
            => EventLog.SourceExists(source);

        /// <summary>
        /// Creates a new event source
        /// </summary>
        /// <param name="source">The source to create</param>
        /// <param name="log">The log to write to</param>
        public static void CreateEventSource(string source, string log = "Application")
        {
            if (!SourceExists(source))
                EventLog.CreateEventSource(source, log);
        }

        /// <inheritdoc />
        public void Dispose() => m_eventLog.Dispose();

        /// <inheritdoc />
        public void WriteMessage(LogEntry entry)
            => m_eventLog.WriteEntry(entry.AsString(true), ToEventLogType(entry.Level));

        /// <summary>
        /// Converts a log message type to an windows event log type
        /// </summary>
        /// <param name="level">The log message type</param>
        /// <returns>The windows event log type</returns>
        private static EventLogEntryType ToEventLogType(LogMessageType level)
        {
            return level switch
            {
                LogMessageType.ExplicitOnly => EventLogEntryType.Information,
                LogMessageType.Profiling => EventLogEntryType.Information,
                LogMessageType.Verbose => EventLogEntryType.Information,
                LogMessageType.Retry => EventLogEntryType.Warning,
                LogMessageType.Information => EventLogEntryType.Information,
                LogMessageType.DryRun => EventLogEntryType.Information,
                LogMessageType.Warning => EventLogEntryType.Warning,
                LogMessageType.Error => EventLogEntryType.Error,
                _ => EventLogEntryType.Information
            };
        }
    }
}