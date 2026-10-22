using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ZipP2P.App.Logging;

/// <summary>Uma linha do log técnico.</summary>
public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Category, string Message, string? Exception)
{
    public string LevelText => Level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => Level.ToString().ToUpperInvariant(),
    };

    public bool IsWarning => Level == LogLevel.Warning;

    public bool IsError => Level >= LogLevel.Error;

    public bool IsDebug => Level < LogLevel.Information;

    public bool HasException => Exception is not null;

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");

    public override string ToString()
    {
        var line = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{LevelText}] {Category}: {Message}";
        return Exception is null ? line : line + Environment.NewLine + Exception;
    }
}

/// <summary>
/// Log técnico do app, mantido em memória (as últimas <see cref="Capacity"/> entradas) para
/// a tela de logs, de onde pode ser copiado ou exportado.
/// </summary>
public sealed class AppLog : ILoggerProvider
{
    public const int Capacity = 5000;

    public static AppLog Instance { get; } = new();

    private readonly LinkedList<LogEntry> _entries = new();

    private AppLog()
    {
        Factory = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Debug)
            .AddFilter("Microsoft", LogLevel.Warning)
            .AddProvider(this));
    }

    /// <summary>Fábrica de loggers ligada a este log (passar às camadas P2P/sinalização).</summary>
    public ILoggerFactory Factory { get; }

    /// <summary>Disparado (em qualquer thread) quando uma entrada é adicionada.</summary>
    public event Action<LogEntry>? EntryAdded;

    public event Action? Cleared;

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_entries)
            return _entries.ToList();
    }

    public void Clear()
    {
        lock (_entries)
            _entries.Clear();
        Cleared?.Invoke();
    }

    /// <summary>Texto completo do log, com um cabeçalho de ambiente (útil para relatar problemas).</summary>
    public string Export(bool includeDebug)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ZipStream {AppVersion} — {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) — .NET {Environment.Version}");
        builder.AppendLine($"Exported at {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine(new string('-', 80));

        foreach (var entry in Snapshot())
        {
            if (includeDebug || entry.Level >= LogLevel.Information)
                builder.AppendLine(entry.ToString());
        }

        return builder.ToString();
    }

    public static string AppVersion =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "?";

    /// <summary>Registra exceções não tratadas, para que apareçam na tela de logs.</summary>
    public void CaptureUnhandledExceptions()
    {
        var logger = CreateLogger("App");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            logger.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public void Dispose()
    {
    }

    private void Add(LogEntry entry)
    {
        lock (_entries)
        {
            _entries.AddLast(entry);
            if (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }

        EntryAdded?.Invoke(entry);
    }

    private sealed class Logger(AppLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            log.Add(new LogEntry(DateTimeOffset.Now, logLevel, category, formatter(state, exception), exception?.ToString()));
        }
    }
}
