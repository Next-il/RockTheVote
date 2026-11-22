using System.Text.RegularExpressions;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;

namespace cs2_rockthevote
{
    /// <summary>
    /// Files this plugin writes at runtime: <c>csgo/rockthevote/&lt;name&gt;-&lt;port&gt;&lt;ext&gt;</c>
    /// under the server's own game directory.
    ///
    /// <para><b>Why not beside the config.</b> The config folder (configs/plugins/RockTheVote) and
    /// the plugin folder are Mounts bind mounts, one folder shared by every server of a mode on a
    /// node. A file several servers rewrite there races at restart waves (Allocator and MVP Music,
    /// 2026-11-19). csgo/rockthevote is no mount target, and the port in the name is a second guard:
    /// ports are unique per node, and a mount is per node.</para>
    ///
    /// <para><b>Checked at runtime.</b> On Linux the folder must sit on the same mount as the game
    /// directory itself (per /proc/self/mountinfo). If someone ever mounts it, callers get no path
    /// and keep their data in memory only.</para>
    /// </summary>
    internal static class ServerLocalData
    {
        // The mount layout does not change while the server runs; checked once per folder.
        private static string? _checkedDirectory;
        private static string? _checkedDirectoryProblem;

        /// <summary>
        /// The path for this server, or null with the reason. <paramref name="notYet"/> means try
        /// again later: before the first map has started, hostport may still hold its default.
        /// Main thread only, it reads convars.
        /// </summary>
        public static string? Resolve(string name, string extension, out string? problem, out bool notYet)
        {
            problem = null;
            notYet = false;
            try
            {
                if (string.IsNullOrEmpty(Server.MapName))
                {
                    notYet = true;
                    problem = "the server has not started a map yet";
                    return null;
                }

                int port = ConVar.Find("hostport")?.GetPrimitiveValue<int>() ?? 0;
                if (port <= 0)
                {
                    notYet = true;
                    problem = "the server port is not known yet";
                    return null;
                }

                var gameDir = Path.GetFullPath(Server.GameDirectory);
                var dir = Path.Combine(gameDir, "csgo", "rockthevote");

                if (_checkedDirectory != dir)
                {
                    _checkedDirectory = dir;
                    _checkedDirectoryProblem = SharedMountProblem(dir, gameDir);
                }

                if (_checkedDirectoryProblem is not null)
                {
                    problem = _checkedDirectoryProblem;
                    return null;
                }

                return Path.Combine(dir, $"{name}-{port}{extension}");
            }
            catch (Exception ex)
            {
                problem = $"could not work out the path: {ex.Message}";
                return null;
            }
        }

        /// <summary>Writes a temp file and renames it over the old one, so a reader never sees half a file.</summary>
        public static void WriteAtomic(string path, string contents)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var temp = path + ".tmp";
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }

        /// <summary>
        /// Null when <paramref name="dir"/> is on the same mount as the game directory, i.e. the
        /// server's own volume. Non-Linux hosts have no mountinfo; the port in the name still guards.
        /// </summary>
        private static string? SharedMountProblem(string dir, string gameDir)
        {
            const string mountInfo = "/proc/self/mountinfo";
            if (!File.Exists(mountInfo))
                return null;

            var mountPoints = new List<string>();
            foreach (var line in File.ReadLines(mountInfo))
            {
                // "36 35 98:0 /root /mount/point rw,... - ext4 /dev/x rw": the 5th field is the mount point.
                var fields = line.Split(' ');
                if (fields.Length > 4)
                    mountPoints.Add(UnescapeMountPath(fields[4]));
            }

            var dirMount = CoveringMount(dir, mountPoints);
            var gameMount = CoveringMount(gameDir, mountPoints);
            return dirMount == gameMount
                ? null
                : $"{dir} is inside the bind mount {dirMount}, which may be shared with other servers";
        }

        private static string? CoveringMount(string path, List<string> mountPoints) =>
            mountPoints.Where(m => IsUnder(path, m)).MaxBy(m => m.Length);

        private static bool IsUnder(string path, string mount) =>
            mount == "/"
            || path == mount
            || path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal);

        // mountinfo writes space, tab, newline and backslash as \040, \011, \012, \134.
        private static string UnescapeMountPath(string field) =>
            Regex.Replace(field, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
    }
}
