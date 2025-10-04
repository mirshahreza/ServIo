using Microsoft.AspNetCore.Http;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Models;
using PowNet.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.MSSqlServer;
using System.Data;
using System.IO;

namespace ServIo
{
	public static class LogManager
	{
		private static readonly Lazy<ColumnOptions> _columnOptions = new(CreateColumnOptions);
		private static readonly LoggingLevelSwitch _levelSwitch = new(LogEventLevel.Verbose);
		private static readonly object _initLock = new();
		private static bool _initialized;
		private static string? _logDirectory; // cached resolved log directory

		private const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message}{NewLine}{Exception}";
		private const string DefaultRelativeLogDir = "workspace/log"; // default relative path
		private const int MessageColumnMax = 128;

		public static LoggingLevelSwitch LevelSwitch => _levelSwitch; // expose for dynamic level changes

		public static void SetupLoggers()
		{
			if (_initialized) return; // fast path
			lock (_initLock)
			{
				if (_initialized) return;

				EnsureLogDirectory();
				var logDir = GetLogDirectory();

				var loggerConf = new LoggerConfiguration()
					.MinimumLevel.ControlledBy(_levelSwitch)
					.Enrich.FromLogContext()
					// Environment enricher methods removed due to missing extensions in current package version
					.Enrich.WithProcessId()
					.Enrich.WithThreadId();

				// Console (Information only)
				loggerConf.WriteTo.Logger(lc => lc
					.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Information)
					.WriteTo.Async(a => a.Console(outputTemplate: OutputTemplate)));

				// File sinks per level (reduced repetition)
				var fileSinks = new (LogEventLevel Level, string FileName)[]
				{
					(LogEventLevel.Error,   "error-.txt"),
					(LogEventLevel.Debug,   "debug-.txt"),
					(LogEventLevel.Warning, "warning-.txt")
				};
				foreach (var (level, file) in fileSinks)
					AddLeveledFileSink(loggerConf, level, Path.Combine(logDir, file));

				// Database sink (Verbose activity logs)
				loggerConf.WriteTo.Logger(lc => lc
					.Filter.ByIncludingOnly(e => e.Level == LogEventLevel.Verbose)
					.WriteTo.Async(a => a.MSSqlServer(
						connectionString: GetSerilogConnectionString(),
						sinkOptions: new MSSqlServerSinkOptions
						{
							TableName = GetSerilogTableName(),
							BatchPostingLimit = GetBatchPostingLimit(),
							BatchPeriod = GetBatchPeriodSeconds()
						},
						columnOptions: _columnOptions.Value)));

				Log.Logger = loggerConf.CreateLogger();
				_initialized = true;
			}
		}

		// Allow programmatic override BEFORE initialization
		public static void SetLogDirectory(string path)
		{
			if (string.IsNullOrWhiteSpace(path)) return;
			lock (_initLock)
			{
				if (_initialized)
				{
					Log.Warning("Attempt to change log directory after initialization ignored: {Path}", path);
					return;
				}
				_logDirectory = NormalizeLogDirectory(path.Trim());
			}
		}

		// Dynamic level change helper
		public static void SetMinimumLevel(LogEventLevel level) => _levelSwitch.MinimumLevel = level;

		private static void AddLeveledFileSink(LoggerConfiguration root, LogEventLevel level, string path)
		{
			root.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => e.Level == level)
				.WriteTo.Async(a => a.File(
					path: path,
					rollingInterval: RollingInterval.Hour,
					outputTemplate: OutputTemplate,
					fileSizeLimitBytes: GetFileSizeLimit(),
					retainedFileCountLimit: GetRetainedFileCount(),
					shared: false,
					buffered: GetBufferedWrite())));
		}

		public static void LogConsole(string message) => Console.WriteLine(message);

		public static void LogDebug(string message) => Log.Debug(message);
		public static void LogError(string message) => Log.Error(message);
		public static void LogError(Exception ex) => Log.Error(ex, ex.Message);
		public static void LogWarning(string message) => Log.Warning(message);

		public static void LogActivity(HttpContext context, UserServerObject uso, ApiCallInfo apiInfo, string commandId, bool result, string message, int duration)
		{
			if (context == null || uso == null || apiInfo == null) return;
			if (apiInfo.ControllerName == null || apiInfo.ApiName == null) return;

			var safeMessage = Truncate(message, MessageColumnMax);
			var now = DateTime.UtcNow;

			var logger = Log.Logger
				.ForContext("Controller", apiInfo.ControllerName)
				.ForContext("Method", apiInfo.ApiName)
				.ForContext("RowId", commandId)
				.ForContext("Result", result)
				.ForContext("Message", safeMessage)
				.ForContext("Duration", duration)
				.ForContext("ClientAgent", context.GetClientAgent())
				.ForContext("ClientIp", context.GetClientIp())
				.ForContext("EventById", uso.Id)
				.ForContext("EventByName", uso.UserName)
				.ForContext("EventOn", now)
				.ForContext("CorrelationId", GetCorrelationId(context));

			logger.Verbose("Activity Controller={Controller} Method={Method} RowId={RowId} Result={Result} Duration={Duration}ms ClientIp={ClientIp} CorrelationId={CorrelationId}");
		}

		private static string? GetCorrelationId(HttpContext context) =>
			context.Items.TryGetValue("CorrelationId", out var val) ? val?.ToString() : context.TraceIdentifier;

		private static string Truncate(string value, int max) =>
			string.IsNullOrEmpty(value) ? value : (value.Length <= max ? value : value[..max]);

		private static string GetSerilogConnectionString() =>
			PowNetConfiguration.GetConnectionStringByName(PowNetConfiguration.PowNetSection["Serilog"]?["Connection"]?.ToString() ?? "DefaultConnection");

		private static string GetSerilogTableName() =>
			PowNetConfiguration.PowNetSection["Serilog"]?["TableName"]?.ToString() ?? "Common_ActivityLog";

		private static int GetBatchPostingLimit() =>
			(PowNetConfiguration.PowNetSection["Serilog"]?["BatchPostingLimit"]?.ToString() ?? "100").ToIntSafe();

		private static TimeSpan GetBatchPeriodSeconds() =>
			new(0, 0, (PowNetConfiguration.PowNetSection["Serilog"]?["BatchPeriodSeconds"]?.ToString() ?? "15").ToIntSafe());

		private static int GetFileSizeLimit() =>
			(PowNetConfiguration.PowNetSection["Serilog"]?["FileSizeLimitBytes"]?.ToString() ?? (10 * 1024 * 1024).ToString()).ToIntSafe();

		private static int GetRetainedFileCount() =>
			(PowNetConfiguration.PowNetSection["Serilog"]?["RetainedFileCount"]?.ToString() ?? "30").ToIntSafe();

		private static bool GetBufferedWrite()
		{
			var raw = PowNetConfiguration.PowNetSection["Serilog"]?["BufferedFileWrite"]?.ToString();
			return bool.TryParse(raw, out var b) && b;
		}

		private static ColumnOptions CreateColumnOptions()
		{
			var columnOptions = new ColumnOptions();
			columnOptions.Store.Remove(StandardColumn.MessageTemplate);
			columnOptions.Store.Remove(StandardColumn.Message);
			columnOptions.Store.Remove(StandardColumn.Exception);
			columnOptions.Store.Remove(StandardColumn.Level);
			columnOptions.Store.Remove(StandardColumn.LogEvent);
			columnOptions.Store.Remove(StandardColumn.Properties);
			columnOptions.Store.Remove(StandardColumn.TimeStamp);
			columnOptions.Id.ColumnName = "Id";
			columnOptions.AdditionalColumns =
			[
				new SqlColumn() { ColumnName = "Controller", DataType = SqlDbType.VarChar, DataLength = 64 },
				new SqlColumn() { ColumnName = "Method", DataType = SqlDbType.VarChar, DataLength = 64 },
				new SqlColumn() { ColumnName = "RowId", DataType = SqlDbType.VarChar, DataLength = 64 },
				new SqlColumn() { ColumnName = "Result", DataType = SqlDbType.Bit },
				new SqlColumn() { ColumnName = "Message", DataType = SqlDbType.VarChar, DataLength = 128 },
				new SqlColumn() { ColumnName = "Duration", DataType = SqlDbType.Int },
				new SqlColumn() { ColumnName = "ClientAgent", DataType = SqlDbType.NVarChar, DataLength = 256 },
				new SqlColumn() { ColumnName = "ClientIp", DataType = SqlDbType.VarChar, DataLength = 32 },
				new SqlColumn() { ColumnName = "EventById", DataType = SqlDbType.Int },
				new SqlColumn() { ColumnName = "EventByName", DataType = SqlDbType.NVarChar, DataLength = 64 },
				new SqlColumn() { ColumnName = "EventOn", DataType = SqlDbType.DateTime },
			];
			return columnOptions;
		}

		private static void EnsureLogDirectory()
		{
			var dir = GetLogDirectory();
			if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
		}

		private static string GetLogDirectory()
		{
			if (_logDirectory != null) return _logDirectory;
			var configured = PowNetConfiguration.PowNetSection["Serilog"]?["LogDirectory"]?.ToString();
			var env = Environment.GetEnvironmentVariable("SERILOG_LOG_DIR");
			var candidate = !string.IsNullOrWhiteSpace(env) ? env : !string.IsNullOrWhiteSpace(configured) ? configured : DefaultRelativeLogDir;
			_logDirectory = NormalizeLogDirectory(candidate);
			return _logDirectory;
		}

		private static string NormalizeLogDirectory(string path)
		{
			path = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
			if (!Path.IsPathRooted(path)) path = Path.Combine(AppContext.BaseDirectory, path);
			return path;
		}
	}
}
