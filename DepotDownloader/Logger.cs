// This file is subject to the terms and conditions defined
// in file 'LICENSE', which is part of this source code package.

using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace DepotDownloader
{
    public enum LogLevel
    {
        None = 0,
        Critical = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
        Debug = 5,
        Verbose = 6
    }

    static class Logger
    {
        public static LogLevel Level { get; set; } = LogLevel.Info;
        public static string LogFilePath { get; private set; }

        private static readonly object _lock = new object();
        private static StreamWriter _fileWriter;

        public static void Initialize(string logFilePath, LogLevel level)
        {
            LogFilePath = logFilePath;
            Level = level;

            if (!string.IsNullOrEmpty(LogFilePath))
            {
                try
                {
                    // Create directory if it doesn't exist
                    var dir = Path.GetDirectoryName(LogFilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    _fileWriter = new StreamWriter(new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        AutoFlush = true
                    };
                    _fileWriter.WriteLine($"--- Log started at {DateTime.Now} ---");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to initialize log file: {ex.Message}");
                }
            }
        }

        public static void Critical(string message, params object[] args)
        {
            Log(LogLevel.Critical, message, args);
        }

        public static void Critical(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Critical, $"{message}\n{ex}", args);
        }

        public static void Error(string message, params object[] args)
        {
            Log(LogLevel.Error, message, args);
        }

        public static void Error(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Error, $"{message}\n{ex}", args);
        }

        public static void Warning(string message, params object[] args)
        {
            Log(LogLevel.Warning, message, args);
        }

        public static void Warning(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Warning, $"{message}\n{ex}", args);
        }

        public static void Info(string message, params object[] args)
        {
            Log(LogLevel.Info, message, args);
        }

        public static void Info(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Info, $"{message}\n{ex}", args);
        }

        public static void Debug(string message, params object[] args)
        {
            Log(LogLevel.Debug, message, args);
        }

        public static void Debug(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Debug, $"{message}\n{ex}", args);
        }

        public static void Verbose(string message, params object[] args)
        {
            Log(LogLevel.Verbose, message, args);
        }

        public static void Verbose(Exception ex, string message, params object[] args)
        {
            Log(LogLevel.Verbose, $"{message}\n{ex}", args);
        }

        internal static void Log(LogLevel level, string message, params object[] args)
        {
            if (level > Level) return;

            string formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string logEntry = $"[{timestamp}] [{level}] {formattedMessage}";

            lock (_lock)
            {
                // Console output
                var originalColor = Console.ForegroundColor;
                if (level == LogLevel.Error)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(formattedMessage); // Write errors to stderr
                }
                else
                {
                    if (level == LogLevel.Debug) Console.ForegroundColor = ConsoleColor.DarkGray;
                    if (level == LogLevel.Verbose) Console.ForegroundColor = ConsoleColor.DarkGray;

                    Console.WriteLine(formattedMessage);
                }
                Console.ForegroundColor = originalColor;

                // File output
                if (_fileWriter != null)
                {
                    try
                    {
                        _fileWriter.WriteLine(logEntry);
                    }
                    catch
                    {
                        // Ignore file write errors to avoid crashing
                    }
                }
            }
        }
    }

    public class DepotDownloaderLoggerProvider : ILoggerProvider
    {
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new DepotDownloaderLogger(categoryName);
        public void Dispose() { }
    }

    public class DepotDownloaderLogger : Microsoft.Extensions.Logging.ILogger
    {
        private readonly string _categoryName;
        public DepotDownloaderLogger(string categoryName) => _categoryName = categoryName;

        public IDisposable BeginScope<TState>(TState state) => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
        {
            return logLevel switch
            {
                Microsoft.Extensions.Logging.LogLevel.Trace => Logger.Level >= LogLevel.Verbose,
                Microsoft.Extensions.Logging.LogLevel.Debug => Logger.Level >= LogLevel.Debug,
                Microsoft.Extensions.Logging.LogLevel.Information => Logger.Level >= LogLevel.Info,
                Microsoft.Extensions.Logging.LogLevel.Warning => Logger.Level >= LogLevel.Warning,
                Microsoft.Extensions.Logging.LogLevel.Error => Logger.Level >= LogLevel.Error,
                Microsoft.Extensions.Logging.LogLevel.Critical => Logger.Level >= LogLevel.Critical,
                _ => false
            };
        }

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (!string.IsNullOrEmpty(_categoryName))
            {
                message = $"[{_categoryName}] {message}";
            }

            var level = logLevel switch
            {
                Microsoft.Extensions.Logging.LogLevel.Trace => LogLevel.Verbose,
                Microsoft.Extensions.Logging.LogLevel.Debug => LogLevel.Debug,
                Microsoft.Extensions.Logging.LogLevel.Information => LogLevel.Info,
                Microsoft.Extensions.Logging.LogLevel.Warning => LogLevel.Warning,
                Microsoft.Extensions.Logging.LogLevel.Error => LogLevel.Error,
                Microsoft.Extensions.Logging.LogLevel.Critical => LogLevel.Critical,
                _ => LogLevel.None
            };

            if (exception != null)
            {
                Logger.Log(level, $"{message}\n{exception}");
            }
            else
            {
                Logger.Log(level, message);
            }
        }
    }
}
