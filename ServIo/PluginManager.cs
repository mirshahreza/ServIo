using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace ServIo
{
	/// <summary>
	/// Manages dynamic (collectible) plugin assemblies: load, unload, and discovery.
	/// Provides result models and safety improvements.
	/// </summary>
	public static class PluginManager
	{
		/// <summary>ASP.NET Core application part manager (should be assigned during startup).</summary>
		public static ApplicationPartManager? AppPartManager;
		/// <summary>Action descriptor change provider to notify MVC to refresh its action descriptors.</summary>
		public static DynamicActionDescriptor? AppActionDescriptor;

		#region Internal state
		private static readonly ConcurrentDictionary<string, PluginHandle> _handles = new(StringComparer.OrdinalIgnoreCase);
		private static readonly ConcurrentDictionary<string, Assembly?> _dependencyCache = new(StringComparer.OrdinalIgnoreCase);
		private static int _resolverHooked;
		private static readonly object _appPartLock = new();
		private const int UnloadGcMaxIterations = 10;
		private const int UnloadGcIterationDelayMs = 100; // ms
		#endregion

		#region Public API - Load from dynamic code (build pipeline)
		/// <summary>
		/// Builds dynamic code (if any) then (re)loads its assembly in a collectible context.
		/// </summary>
		public static PluginLoadResult LoadDynamicAssemblyFromCode()
		{
			var swTotal = Stopwatch.StartNew();
			HookResolverOnce();
			FileInfo fileInfo = new(DynamicCodeService.AsmPath);

			if (fileInfo.Exists)
			{
				// Ensure previous dynamic assembly unloaded (best-effort)
				UnloadPlugin(fileInfo.FullName);
			}

			var buildActivity = StartActivity("DynamicAssembly.Build");
			try
			{
				DynamicCodeService.Build();
			}
			catch (Exception ex)
			{
				buildActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
				LogManager.LogError($"Dynamic build failed: {ex.Message}");
				return PluginLoadResult.Failed(DynamicCodeService.AsmPath, ex);
			}
			finally { buildActivity?.Dispose(); }

			fileInfo = new(DynamicCodeService.AsmPath);
			if (!fileInfo.Exists)
			{
				return PluginLoadResult.Failed(DynamicCodeService.AsmPath, new FileNotFoundException("Built assembly not found", fileInfo.FullName));
			}

			var result = LoadPluginInternal(fileInfo.FullName, isDynamic: true);
			result = result with { Duration = swTotal.Elapsed };
			return result;
		}
		#endregion

		#region Public API - Loading / Unloading
		/// <summary>Loads a plugin assembly (collectible).</summary>
		public static PluginLoadResult LoadPlugin(string dllFullPath) => LoadPluginInternal(dllFullPath, isDynamic: false);
		/// <summary>Unloads a plugin assembly.</summary>
		public static PluginUnloadResult UnloadPlugin(string pluginPath) => UnloadInternal(pluginPath);
		#endregion

		#region Public API - Bulk loading
		/// <summary>
		/// Loads all plugin DLLs found in configured plugin directory (collectible contexts). Skips already loaded & dynamic assembly.
		/// </summary>
		public static IReadOnlyList<PluginLoadResult> LoadPlugins()
		{
			HookResolverOnce();
			List<PluginLoadResult> results = new();
			if (!Directory.Exists(PowNetConfiguration.PowNetPlugins)) return results;
			string[] dllFiles = Directory.GetFiles(PowNetConfiguration.PowNetPlugins, "*.dll");

			foreach (var file in dllFiles)
			{
				if (file.Contains("DynaAsm", StringComparison.OrdinalIgnoreCase)) continue;
				var result = LoadPluginInternal(file, isDynamic: false);
				results.Add(result);
			}
			return results;
		}
		#endregion

		#region Public API - Query
		/// <summary>Returns snapshot of current plugin handles.</summary>
		public static IReadOnlyCollection<PluginHandle> GetPluginHandles() => _handles.Values.ToList().AsReadOnly();

		/// <summary>Attempts to get a plugin handle by path (case-insensitive).</summary>
		public static bool TryGetPlugin(string path, out PluginHandle handle)
		{
			var normalized = NormalizePath(path);
			return _handles.TryGetValue(normalized, out handle!);
		}
		#endregion

		#region Internal core logic
		private static PluginLoadResult LoadPluginInternal(string dllFullPath, bool isDynamic)
		{
			var sw = Stopwatch.StartNew();
			if (string.IsNullOrWhiteSpace(dllFullPath))
				return PluginLoadResult.Failed(dllFullPath, new ArgumentException("Path empty", nameof(dllFullPath)));

			string normalizedPath = NormalizePath(dllFullPath);
			if (!File.Exists(normalizedPath))
				return PluginLoadResult.Failed(normalizedPath, new FileNotFoundException("Plugin file not found", normalizedPath));

			HookResolverOnce();

			if (_handles.ContainsKey(normalizedPath))
				return PluginLoadResult.Failed(normalizedPath, new PluginAlreadyLoadedException(normalizedPath));

			var loadActivity = StartActivity("Plugin.Load", normalizedPath);
			PluginLoadContext context = new(normalizedPath);
			Assembly assembly;
			try
			{
				assembly = context.LoadFromAssemblyPath(normalizedPath);
			}
			catch (Exception ex)
			{
				TryUnloadContextSilent(context);
				loadActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
				return PluginLoadResult.Failed(normalizedPath, ex);
			}

			lock (_appPartLock)
			{
				AppPartManager?.ApplicationParts.Add(new AssemblyPart(assembly));
			}
			AppActionDescriptor?.NotifyChange();

			var fileInfo = new FileInfo(normalizedPath);
			var handle = new PluginHandle(normalizedPath, assembly, context, new WeakReference(context), DateTime.UtcNow, fileInfo.Exists ? fileInfo.Length : 0, isDynamic);
			_handles[normalizedPath] = handle;

			loadActivity?.SetTag("assembly.name", assembly.GetName().Name)
				?.SetTag("assembly.version", assembly.GetName().Version?.ToString())
				?.SetTag("plugin.dynamic", isDynamic)
				?.SetStatus(ActivityStatusCode.Ok);
			loadActivity?.Dispose();

			LogManager.LogDebug($"Plugin loaded: {assembly.GetName().Name} v{assembly.GetName().Version} (Dynamic={isDynamic}) Path={normalizedPath}");
			return PluginLoadResult.Successful(normalizedPath, assembly, sw.Elapsed, isDynamic, fileInfo.Length);
		}

		private static PluginUnloadResult UnloadInternal(string pluginPath)
		{
			var sw = Stopwatch.StartNew();
			if (string.IsNullOrWhiteSpace(pluginPath))
				return PluginUnloadResult.Failed(pluginPath, new ArgumentException("Plugin path must be provided", nameof(pluginPath)));

			string normalizedPath = NormalizePath(pluginPath);
			if (!_handles.TryRemove(normalizedPath, out var handle))
				return PluginUnloadResult.Failed(normalizedPath, new FileNotFoundException("Plugin not found in loaded list", normalizedPath));

			var unloadActivity = StartActivity("Plugin.Unload", normalizedPath);
			Exception? error = null;
			int gcLoops = 0;
			try
			{
				lock (_appPartLock)
				{
					var part = AppPartManager?.ApplicationParts.OfType<AssemblyPart>().FirstOrDefault(p => p.Assembly == handle.Assembly);
					if (part != null) AppPartManager!.ApplicationParts.Remove(part);
				}
				AppActionDescriptor?.NotifyChange();

				handle.Context.Unload();
				for (; gcLoops < UnloadGcMaxIterations && handle.ContextWeakRef.IsAlive; gcLoops++)
				{
					GC.Collect();
					GC.WaitForPendingFinalizers();
					Thread.Sleep(UnloadGcIterationDelayMs);
				}
			}
			catch (Exception ex)
			{
				error = ex;
				// revert on failure
				_handles[normalizedPath] = handle;
			}

			bool alive = handle.ContextWeakRef.IsAlive;
			if (error != null || alive)
			{
				string msg = error != null ? $"Error during plugin unload for '{normalizedPath}': {error.Message}" : $"Unload attempted, context still alive after {gcLoops} GC cycles.";
				LogManager.LogDebug(msg);
				unloadActivity?.SetStatus(ActivityStatusCode.Error, msg);
				unloadActivity?.Dispose();
				return PluginUnloadResult.Failed(normalizedPath, error ?? new InvalidOperationException("Context still alive"), sw.Elapsed, gcLoops, alive);
			}

			LogManager.LogDebug($"Plugin unloaded: {normalizedPath} (GC loops={gcLoops})");
			unloadActivity?.SetStatus(ActivityStatusCode.Ok);
			unloadActivity?.Dispose();
			return PluginUnloadResult.Successful(normalizedPath, sw.Elapsed, gcLoops);
		}
		#endregion

		#region Dependency resolving
		private static void HookResolverOnce()
		{
			if (Interlocked.Exchange(ref _resolverHooked, 1) == 0)
			{
				AssemblyLoadContext.Default.Resolving += ResolveDependencies;
			}
		}

		private static Assembly? ResolveDependencies(AssemblyLoadContext context, AssemblyName assemblyName)
		{
			string simple = assemblyName.Name ?? string.Empty;
			if (_dependencyCache.TryGetValue(simple, out var cached)) return cached;

			Assembly? resolved = null;
			try
			{
				string baseDirPath = Path.Combine(AppContext.BaseDirectory, simple + ".dll");
				if (File.Exists(baseDirPath))
					resolved = context.LoadFromAssemblyPath(baseDirPath);
				else if (Directory.Exists(PowNetConfiguration.PowNetPlugins))
				{
					string pluginCandidate = Directory.GetFiles(PowNetConfiguration.PowNetPlugins, simple + ".dll", SearchOption.AllDirectories).FirstOrDefault() ?? string.Empty;
					if (!string.IsNullOrEmpty(pluginCandidate) && File.Exists(pluginCandidate))
						resolved = context.LoadFromAssemblyPath(pluginCandidate);
				}
			}
			catch (Exception ex)
			{
				LogManager.LogDebug($"Dependency resolve failed for {simple}: {ex.Message}");
			}
			_dependencyCache[simple] = resolved; // cache null as negative lookup too
			return resolved;
		}
		#endregion

		#region Helpers & utilities
		private static string NormalizePath(string path) => Path.GetFullPath(path);
		private static void TryUnloadContextSilent(PluginLoadContext ctx) { try { ctx.Unload(); } catch { } }
		private static Activity? StartActivity(string name, string? path = null)
		{
			var activity = new Activity(name).Start();
			if (path != null) activity.AddTag("plugin.path", path);
			return activity;
		}
		#endregion
	}

	#region Models
	public sealed record PluginHandle(
		string Path,
		Assembly Assembly,
		PluginLoadContext Context,
		WeakReference ContextWeakRef,
		DateTime LoadedUtc,
		long FileSizeBytes,
		bool IsDynamic);

	public readonly record struct PluginLoadResult(
		bool Success,
		string Path,
		Assembly? Assembly,
		TimeSpan Duration,
		Exception? Error,
		bool IsDynamic,
		long FileSizeBytes)
	{
		public static PluginLoadResult Successful(string path, Assembly asm, TimeSpan duration, bool isDynamic, long size) => new(true, path, asm, duration, null, isDynamic, size);
		public static PluginLoadResult Failed(string path, Exception error) => new(false, path, null, TimeSpan.Zero, error, false, 0);
	}

	public readonly record struct PluginUnloadResult(
		bool Success,
		string Path,
		TimeSpan Duration,
		Exception? Error,
		int GcLoops,
		bool ContextStillAlive)
	{
		public static PluginUnloadResult Successful(string path, TimeSpan duration, int gcLoops) => new(true, path, duration, null, gcLoops, false);
		public static PluginUnloadResult Failed(string path, Exception error, TimeSpan? duration = null, int gcLoops = 0, bool alive = false) => new(false, path, duration ?? TimeSpan.Zero, error, gcLoops, alive);
	}
	#endregion

	#region Custom exceptions
	public class PluginLoadException : Exception { public string PluginPath { get; } public PluginLoadException(string path, Exception inner) : base($"Failed to load plugin '{path}': {inner.Message}", inner) => PluginPath = path; }
	public class PluginUnloadException : Exception { public string PluginPath { get; } public PluginUnloadException(string path, Exception inner) : base($"Failed to unload plugin '{path}': {inner.Message}", inner) => PluginPath = path; }
	public class PluginAlreadyLoadedException : Exception { public string PluginPath { get; } public PluginAlreadyLoadedException(string path) : base($"Plugin already loaded: {path}") => PluginPath = path; }
	#endregion

	public class PluginLoadContext : AssemblyLoadContext
	{
		private readonly AssemblyDependencyResolver _resolver;
		public PluginLoadContext(string pluginPath) : base(isCollectible: true) => _resolver = new AssemblyDependencyResolver(pluginPath);
		protected override Assembly? Load(AssemblyName assemblyName)
		{
			string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
			if (assemblyPath != null) return LoadFromAssemblyPath(assemblyPath);
			return null;
		}
	}
}
