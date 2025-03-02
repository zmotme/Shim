using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Scoop.ShimExtensions
{
    /// <summary>
    /// Represents an environment variable option with its name, value and whether it should override an existing value.
    /// </summary>
    public class EnvOption
    {
        public string Name { get; }
        public string Value { get; }
        public bool Override { get; }

        public EnvOption(string name, string value, string assigner)
        {
            Name = name;
            Value = value;
            // If no assigner, then override; otherwise, override if assigner is not "??=".
            Override = string.IsNullOrEmpty(assigner) || (assigner != "??=");
        }
    }

    /// <summary>
    /// Provides functionality to resolve version macros, expand environment variables and export environment options.
    /// </summary>
    public static class VersionManager
    {
        public static string ShimDir { get; private set; } = string.Empty;
        public static string CurrentDir { get; private set; } = Environment.CurrentDirectory;

        private const string DP0 = "%~dp0";
        private const string GlobalVersionFileName = "version";
        private const string VersionsFolder = "versions";

        // Matches strings like "versions\${.python-version}" (only lowercase letters and hyphens).
        // Group1: full macro with folder. Group2: macro portion (e.g. "${.python-version}"). Group3: version file name (e.g. ".python-version")
        private static readonly Regex ReVersionFile = new Regex(
            @"(versions[\\/](\$\{(\.[a-z\-]+)\}))",
            RegexOptions.Compiled);

        /// <summary>
        /// Sets the shim directory using the provided config file.
        /// </summary>
        public static void SetShimPath(string configFile)
        {
            if (!string.IsNullOrEmpty(configFile))
            {
                ShimDir = Path.GetDirectoryName(configFile);
            }
        }

        /// <summary>
        /// Expands environment variables, substitutes DP0 with the shim directory,
        /// and replaces any version macro with the resolved version.
        /// </summary>
        public static string ExtendPath(string value, string shimPath)
        {
            // Update ShimDir if needed.
            ShimDir = Path.GetDirectoryName(shimPath);
            if (string.IsNullOrEmpty(value))
                return value;

            // First, expand environment variables and substitute DP0.
            string fixedPath = ExtendEnv(value);

            // Process version macro if present.
            fixedPath = ReplaceVersionMacro(fixedPath, value);

            return fixedPath;
        }

        /// <summary>
        /// Iterates through the provided environment options and exports them (optionally overriding existing vars).
        /// Also replaces any occurrence of "${path}" with the provided path.
        /// </summary>
        public static void ExportEnvs(List<EnvOption> envs, string path, string args)
        {
            foreach (EnvOption env in envs)
            {
                // Expand macros and substitute "${path}".
                string value = ExtendEnv(env.Value).Replace("${path}", path);
                // Try converting to absolute path.
                value = TryConvertToFullPath(value);

                if (env.Override)
                {
                    Environment.SetEnvironmentVariable(env.Name, value);
                }
                else
                {
                    string currentValue = Environment.GetEnvironmentVariable(env.Name);
                    if (string.IsNullOrEmpty(currentValue))
                    {
                        Environment.SetEnvironmentVariable(env.Name, value);
                    }
                }
            }
        }

        /// <summary>
        /// Expands environment variables in the string and replaces DP0 with ShimDir.
        /// </summary>
        public static string ExtendEnv(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return Environment.ExpandEnvironmentVariables(value).Replace(DP0, ShimDir);
        }

        /// <summary>
        /// Replaces any version macro found within the expanded path.
        /// </summary>
        /// <param name="fixedPath">The expanded and intermediate fixed path.</param>
        /// <param name="originalValue">The original input value (before expansion).</param>
        /// <returns>The path with the version macro replaced by its resolved version.</returns>
        private static string ReplaceVersionMacro(string fixedPath, string originalValue)
        {
            Match match = ReVersionFile.Match(fixedPath);
            if (!match.Success)
                return fixedPath;

            // Group extraction:
            // fullMacro: "versions/${.python-version}"
            // versionMacro: "${.python-version}"
            // versionFileName: ".python-version"
            string fullMacro = match.Groups[1].Value.Trim();
            string versionMacro = match.Groups[2].Value.Trim();
            string versionFileName = match.Groups[3].Value.Trim();

            // Check for override in environment variables.
            string envMacroName = versionMacro.ToUpper().Replace(".", "").Replace("-", "_");
            string envMacroValue = Environment.GetEnvironmentVariable(envMacroName);
            if (!string.IsNullOrEmpty(envMacroValue))
            {
                // Replace the full macro with the environment value.
                return fixedPath.Replace(fullMacro, envMacroValue);
            }

            // Use the substring before the macro in the original value as prefix.
            int macroIndex = originalValue.IndexOf(fullMacro, StringComparison.Ordinal);
            string pathPrefix = (macroIndex != -1 && macroIndex > 0)
                                  ? originalValue.Substring(0, macroIndex - 1)
                                  : string.Empty;

            // Resolve the version from file.
            string versionPart = FindVersionInParents(versionFileName, pathPrefix);
            return fixedPath.Replace(versionMacro, versionPart);
        }

        /// <summary>
        /// Walks upward from CurrentDir to locate a version file in the "versions" folder.
        /// If not found, attempts to read a global version file from the global prefix.
        /// </summary>
        private static string FindVersionInParents(string versionFileName, string globalPrefix)
        {
            DirectoryInfo currentDir = new DirectoryInfo(CurrentDir);
            while (currentDir != null)
            {
                string candidatePath = Path.Combine(currentDir.FullName, VersionsFolder, versionFileName);
                string version = ReadVersionFile(candidatePath);
                if (!string.IsNullOrEmpty(version))
                    return version;

                currentDir = currentDir.Parent;
            }

            // Global fallback.
            string globalVersionPath = Path.Combine(globalPrefix, GlobalVersionFileName);
            return ReadVersionFile(globalVersionPath);
        }

        /// <summary>
        /// Reads and returns trimmed content of a file at the given file path, if it exists.
        /// </summary>
        private static string ReadVersionFile(string filePath)
        {
            return File.Exists(filePath) ? File.ReadAllText(filePath).Trim() : string.Empty;
        }

        /// <summary>
        /// Attempts to convert a path string to a full absolute path.
        /// If conversion fails, returns the original path.
        /// </summary>
        private static string TryConvertToFullPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return path;

            try
            {
                string fullPath = Path.GetFullPath(path);
                if (!string.IsNullOrEmpty(fullPath) &&
                    fullPath.StartsWith(Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath;
                }
            }
            catch (ArgumentException)
            {
                // Invalid path format.
            }
            catch (NotSupportedException)
            {
                // File system does not support the specified path.
            }
            return path;
        }
    }
}
