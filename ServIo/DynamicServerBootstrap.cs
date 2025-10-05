using System;
using System.IO;
using System.Linq;
using System.Reflection;
using PowNet.Configuration;

namespace ServIo
{
    /// <summary>
    /// Provides a reusable bootstrap utility to compile and load dynamic server-side C# source files
    /// located under PowNetConfiguration server folder into a collectible plugin assembly.
    /// Supports multiple PowNetConfiguration property name variants for compatibility.
    /// </summary>
    public static class DynamicServerBootstrap
    {
        private static string ResolvePath(string primaryProp, string fallbackProp, string defaultSubFolder)
        {
            var t = typeof(PowNetConfiguration);
            string? val = t.GetProperty(primaryProp, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString();
            if (string.IsNullOrWhiteSpace(val))
                val = t.GetProperty(fallbackProp, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)?.ToString();
            if (string.IsNullOrWhiteSpace(val))
                val = Path.Combine(PowNetConfiguration.WorkspacePath, defaultSubFolder);
            return val!;
        }

        /// <summary>
        /// Ensures required workspace directories exist and (if any .cs files are present) builds & loads
        /// a dynamic assembly through PluginManager. Safe to call multiple times; dynamic assembly is rebuilt each call.
        /// </summary>
        /// <param name="verbose">If true writes status messages to console.</param>
        /// <returns>PluginLoadResult when a build/load occurred; otherwise null if no source files found.</returns>
        public static PluginLoadResult? EnsureDynamicServerScriptsLoaded(bool verbose = true)
        {
            try
            {
                Directory.CreateDirectory(PowNetConfiguration.WorkspacePath);
                Directory.CreateDirectory(PowNetConfiguration.ServerPath);
                Directory.CreateDirectory(PowNetConfiguration.PluginsPath);

                var serverPath = PowNetConfiguration.ServerPath;
                bool anySources = Directory.Exists(serverPath) && Directory.EnumerateFiles(serverPath, "*.cs", SearchOption.AllDirectories).Any();
                if (!anySources)
                {
                    if (verbose) Console.WriteLine($"[DynamicCode] No .cs files under '{serverPath}'. Skipping dynamic load.");
                    return null;
                }

                var result = PluginManager.LoadDynamicAssemblyFromCode();
                if (verbose)
                {
                    if (result.Success)
                        Console.WriteLine($"[DynamicCode] Loaded '{System.IO.Path.GetFileName(result.Path)}' in {result.Duration.TotalMilliseconds:N0} ms (Size {result.FileSizeBytes} bytes)");
                    else
                        Console.WriteLine($"[DynamicCode] Failed: {result.Error?.Message}");
                }
                return result;
            }
            catch (Exception ex)
            {
                if (verbose) Console.WriteLine($"[DynamicCode] Exception: {ex.Message}");
                return null;
            }
        }
    }
}
