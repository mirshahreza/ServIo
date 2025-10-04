using Microsoft.AspNetCore.Http;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Models;
using PowNet.Services;
using Serilog;
using Serilog.Sinks.MSSqlServer;
using System.Data;

namespace AppEndApi
{
	public static class LogMan
	{
		private static readonly Lazy<ColumnOptions> _columnOptions = new(CreateColumnOptions);

		public static void SetupLoggers()
		{
			var loggerConf = new LoggerConfiguration().MinimumLevel.Verbose();

			loggerConf.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => (e.Level== Serilog.Events.LogEventLevel.Information))
				.WriteTo.Console(
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message}{NewLine}{Exception}"
				));

			loggerConf.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => (e.Level == Serilog.Events.LogEventLevel.Error))
				.WriteTo.File(
					path: "workspace/log/error-.txt",
					rollingInterval: RollingInterval.Hour,
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message}{NewLine}{Exception}",
					fileSizeLimitBytes: 10 * 1024 * 1024,
					retainedFileCountLimit: 30
				));

			loggerConf.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => (e.Level == Serilog.Events.LogEventLevel.Debug))
				.WriteTo.File(
					path: "workspace/log/debug-.txt",
					rollingInterval: RollingInterval.Hour,
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message}{NewLine}{Exception}",
					fileSizeLimitBytes: 10 * 1024 * 1024,
					retainedFileCountLimit: 30
				));

			loggerConf.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => (e.Level == Serilog.Events.LogEventLevel.Warning))
				.WriteTo.File(
					path: "workspace/log/warning-.txt",
					rollingInterval: RollingInterval.Hour,
					outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message}{NewLine}{Exception}",
					fileSizeLimitBytes: 10 * 1024 * 1024,
					retainedFileCountLimit: 30
				));

			loggerConf.WriteTo.Logger(lc => lc
				.Filter.ByIncludingOnly(e => (e.Level == Serilog.Events.LogEventLevel.Verbose))
				.WriteTo.MSSqlServer(
					connectionString: GetSerilogConnectionString(),
					sinkOptions: new MSSqlServerSinkOptions { 
						TableName = GetSerilogTableName(), 
						BatchPostingLimit = GetBatchPostingLimit(),
						BatchPeriod = GetBatchPeriodSeconds()
					},
					columnOptions: _columnOptions.Value // Use cached column options
				));

			Log.Logger = loggerConf.CreateLogger();
		}

		public static void LogConsole(string message)
		{
			Console.WriteLine(message);
		}

		public static void LogDebug(string message)
		{
			Log.Debug(message);
		}
		public static void LogError(string message)
		{
			Log.Error(message);
		}
		public static void LogError(Exception ex)
		{
			// Use StringBuilder to avoid multiple string concatenations
			var errorMessage = new System.Text.StringBuilder(ex.Message.Length + (ex.StackTrace?.Length ?? 0) + 10);
			errorMessage.Append(ex.Message);
			errorMessage.Append(Environment.NewLine);
			if (ex.StackTrace != null)
				errorMessage.Append(ex.StackTrace);
			Log.Error(errorMessage.ToString());
		}
		public static void LogWarning(string message)
		{
			Log.Warning(message);
		}

		public static void LogActivity(HttpContext context, UserServerObject uso, ApiCallInfo apiInfo, string commadId, bool result, string message, int duration)
		{
			Log.Logger.Verbose("{Controller}{Method}{RowId}{Result}{Message}{Duration}{ClientAgent}{ClientIp}{EventById}{EventByName}{EventOn}",
				apiInfo.ControllerName, apiInfo.ApiName, commadId, result, message, duration, context.GetClientAgent(), context.GetClientIp(), uso.Id, uso.UserName, DateTime.Now);
		}

		private static string GetSerilogConnectionString()
		{
			return PowNetConfiguration.GetConnectionStringByName(PowNetConfiguration.PowNetSection["Serilog"]?["Connection"]?.ToString() ?? "DefaultConnection");
		}
		private static string GetSerilogTableName()
		{
			return PowNetConfiguration.PowNetSection["Serilog"]?["TableName"]?.ToString() ?? "Common_ActivityLog";
		}

		private static int GetBatchPostingLimit()
		{
			return (PowNetConfiguration.PowNetSection["Serilog"]?["BatchPostingLimit"]?.ToString() ?? "100").ToIntSafe();
		}
		private static TimeSpan GetBatchPeriodSeconds()
		{
			return new TimeSpan(0, 0, (PowNetConfiguration.PowNetSection["Serilog"]?["BatchPeriodSeconds"]?.ToString() ?? "15").ToIntSafe());
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

		private static ColumnOptions GetColumnOptions()
		{
			return _columnOptions.Value;
		}
	}
}
