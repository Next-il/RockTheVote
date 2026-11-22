using CounterStrikeSharp.API.Modules.Extensions;

namespace cs2_rockthevote
{
    /// <summary>
    /// Works out where maplist.txt lives: alongside the plugin's own config file, in
    /// <c>configs/plugins/&lt;plugin&gt;/</c>. Read-only data only - files the plugin writes go to
    /// <see cref="ServerLocalData"/>, because with Mounts the config folder is shared by every
    /// server of a mode on a node. (mapcooldown.txt lived here until it raced across servers; the
    /// old copy is still read once to seed the per-server file.)
    ///
    /// <para><b>Why not the plugin folder.</b> maplist.txt used to be read from next to the dll,
    /// which makes the data part of the deployment - running several servers with different map
    /// lists meant shipping a whole copy of the plugin per server.</para>
    ///
    /// <para><b>The directory is asked for, not guessed.</b> CounterStrikeSharp decides what that
    /// folder is called, and it follows the plugin's own directory name - which here is the server
    /// name, not "RockTheVote". Hardcoding either would break one setup or the other, so the path
    /// comes from <see cref="PluginConfigExtensions.GetConfigPath{T}"/>, which is whatever the
    /// framework actually used.</para>
    /// </summary>
    public static class PluginPaths
    {
        private static string? _configDirectory;

        /// <summary>
        /// Records where the config file landed. Called from OnConfigParsed, which is the first
        /// point the framework has told anyone what that path is.
        /// </summary>
        public static void Capture(Config config)
        {
            var path = config.GetConfigPath();
            var dir  = Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(dir))
                _configDirectory = dir;
        }

        /// <summary>
        /// The directory holding this plugin's config, and therefore its data files. Null before
        /// the config has been parsed.
        /// </summary>
        public static string? ConfigDirectory => _configDirectory;

        /// <summary>
        /// Full path to a data file. Null before the config has been parsed, which callers should
        /// treat as "not ready yet" rather than falling back to a guess.
        /// </summary>
        public static string? Resolve(string fileName) =>
            _configDirectory is null ? null : Path.Combine(_configDirectory, fileName);
    }
}
