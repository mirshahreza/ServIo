namespace ServIo
{
    /// <summary>
    /// Helper utility exposed for legacy / dynamic server scripts to trigger dynamic assembly (re)build & load.
    /// Wraps PluginManager.LoadDynamicAssemblyFromCode for simpler reuse.
    /// </summary>
    public static class PluginUtil
    {
        /// <summary>
        /// Builds current workspace/server sources into a dynamic assembly and loads (hot-reload style).
        /// </summary>
        public static PluginLoadResult LoadDynamicAsmFromCode()
        {
            return PluginManager.LoadDynamicAssemblyFromCode();
        }
    }
}
