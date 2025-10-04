using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using PowNet.Configuration;
using PowNet.Extensions;
using PowNet.Services;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

namespace AppEndApi
{
	public static class PluginUtil
	{
		public static ApplicationPartManager? AppPartManager;
		public static DynaDescriptor? AppActionDescriptor;
		public static readonly ConcurrentDictionary<string, (Assembly Assembly, PluginLoadContext Context, WeakReference ContextWeakRef)> AppLoadedPlugins = new();

		public static void LoadDynamicAsmFromCode()
		{
			FileInfo fileInfo;
			fileInfo = new(DynamicCodeService.AsmPath);
			if (fileInfo.Exists) UnloadPlugin(fileInfo.FullName);
            DynamicCodeService.Build();
			fileInfo = new(DynamicCodeService.AsmPath);
			AssemblyLoadContext.Default.Resolving += ResolveDependencies;
			Load(fileInfo.FullName);
		}

		public static void Load(string dllFullPath)
		{
			if (AppLoadedPlugins.ContainsKey(dllFullPath)) throw new Exception($"Plugin '{dllFullPath}' is already loaded.");

			var pluginLoadContext = new PluginLoadContext(dllFullPath);
			try
			{
				Assembly pluginAssembly = pluginLoadContext.LoadFromAssemblyPath(dllFullPath);
				AppPartManager?.ApplicationParts.Add(new AssemblyPart(pluginAssembly));
				AppLoadedPlugins[dllFullPath] = (pluginAssembly, pluginLoadContext, new WeakReference(pluginLoadContext));
				AppActionDescriptor?.NotifyChange();
			}
			catch (Exception ex)
			{
				try { pluginLoadContext.Unload(); } catch { }
				throw new Exception($"Failed to load plugin assembly: {ex.Message}");
			}
		}
		public static void UnloadPlugin(string pluginPath)
		{
			if (string.IsNullOrEmpty(pluginPath)) throw new Exception("Plugin path must be provided for unloading.");
			if (!AppLoadedPlugins.TryRemove(pluginPath, out var pluginInfo)) throw new Exception($"Plugin '{pluginPath}' was not found in the loaded plugins list.");

			var (assemblyToUnload, contextToUnload, contextWeakRef) = pluginInfo;

			try
			{
				var partToRemove = AppPartManager?.ApplicationParts.OfType<AssemblyPart>().FirstOrDefault(p => p.Assembly == assemblyToUnload);
				if (partToRemove != null) AppPartManager?.ApplicationParts.Remove(partToRemove);

				AppActionDescriptor?.NotifyChange();

				contextToUnload.Unload();

				GC.Collect();
				GC.WaitForPendingFinalizers();
				GC.Collect(); // Call again to ensure all generations are collected.

				for (int i = 0; i < 10 && contextWeakRef.IsAlive; i++)
				{
					Thread.Sleep(100);
					GC.Collect();
					GC.WaitForPendingFinalizers();
				}

				string unloadStatus = contextWeakRef.IsAlive
					? "Unload attempted, but context is still alive (likely due to lingering references)."
					: "Unload successful: PluginLoadContext has been garbage collected.";

				LogMan.LogDebug(unloadStatus);
			}
			catch (Exception ex)
			{
				LogMan.LogDebug($"Error during plugin unload for '{pluginPath}': {ex.Message}");
				AppLoadedPlugins[pluginPath] = pluginInfo;
			}
		}

		public static void LoadPlugins()
		{
			List<Assembly> loadedAssemblies = [];
			AssemblyLoadContext.Default.Resolving += ResolveDependencies;
			LoadNewDlls(loadedAssemblies);
		}
		private static void LoadNewDlls(List<Assembly> loadedAssemblies)
		{
			if (!Directory.Exists(PowNetConfiguration.PowNetPlugins)) return;
			string[] dllFiles = Directory.GetFiles(PowNetConfiguration.PowNetPlugins, "*.dll");

			foreach (string dllFile in dllFiles)
			{
				try
				{
					string assemblyName = AssemblyName.GetAssemblyName(dllFile).FullName;
					if (loadedAssemblies.Any(a => a.FullName == assemblyName) || assemblyName.ContainsIgnoreCase("DynaAsm")) continue;
					FileInfo dllInfo = new(dllFile);
					Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dllInfo.FullName);
					loadedAssemblies.Add(assembly);
				}
				catch (Exception ex)
				{
					LogMan.LogError($"Error loading DLL {Path.GetFileName(dllFile)}: {ex.Message}");
				}
			}
		}
		private static Assembly? ResolveDependencies(AssemblyLoadContext context, AssemblyName assemblyName)
		{
			string assemblyPath = Path.Combine(AppContext.BaseDirectory, assemblyName.Name + ".dll");
			if (File.Exists(assemblyPath)) return context.LoadFromAssemblyPath(assemblyPath);
			return null;
		}
	}

	public class PluginLoadContext(string pluginPath) : AssemblyLoadContext(isCollectible: true)
	{
		private AssemblyDependencyResolver _resolver = new(pluginPath);

		protected override Assembly? Load(AssemblyName assemblyName)
		{
			string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
			if (assemblyPath != null) return LoadFromAssemblyPath(assemblyPath);
			return null;
		}
	}
}
