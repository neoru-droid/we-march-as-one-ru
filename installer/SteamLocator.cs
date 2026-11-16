using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WmaoRuInstaller
{
    internal static class SteamLocator
    {
        public const int TargetAppId = 5189160;
        public const string GameFolderName = "We March as One";
        public const string SchemaFileName = "UserGameStatsSchema_5189160.bin";

        public static string NormalizeCandidatePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return "";
            }

            string trimmed = rawPath.Trim().Trim('"').Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                return "";
            }

            if (trimmed.StartsWith("/", StringComparison.Ordinal))
            {
                string cleanPosix = TrimTrailingSeparators(trimmed);
                if (!SafeDirectoryExists(cleanPosix))
                {
                    string wineMapped = "Z:" + cleanPosix.Replace('/', '\\');
                    if (SafeDirectoryExists(wineMapped) || Path.DirectorySeparatorChar == '\\')
                    {
                        return TrimTrailingSeparators(wineMapped);
                    }
                }
                return cleanPosix;
            }

            if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':')
            {
                return TrimTrailingSeparators(trimmed.Replace('/', '\\'));
            }

            return TrimTrailingSeparators(trimmed);
        }

        public static string NormalizePickedGameDirectory(string rawPath)
        {
            string norm = NormalizeCandidatePath(rawPath);
            if (string.IsNullOrEmpty(norm))
            {
                return "";
            }
            if (IsValidGameDirectory(norm))
            {
                return SafeGetFullPath(norm);
            }
            try
            {
                string parent = Path.GetDirectoryName(norm);
                if (!string.IsNullOrEmpty(parent) && IsValidGameDirectory(parent))
                {
                    return SafeGetFullPath(parent);
                }
            }
            catch
            {
            }
            return norm;
        }

        public static bool IsValidGameDirectory(string dirPath)
        {
            string normalized = NormalizeCandidatePath(dirPath);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }
            try
            {
                string resPath = Path.Combine(normalized, @"WeMarchAsOne_Data\resources.assets");
                string catPath = Path.Combine(normalized, @"WeMarchAsOne_Data\StreamingAssets\aa\catalog.bin");
                return File.Exists(resPath) && File.Exists(catPath);
            }
            catch
            {
                return false;
            }
        }

        public static string GetSteamRootFromRegistry()
        {
            string[][] probes = new string[][]
            {
                new string[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath" },
                new string[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath" },
                new string[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath" }
            };

            for (int i = 0; i < probes.Length; i++)
            {
                try
                {
                    object val = Registry.GetValue(probes[i][0], probes[i][1], null);
                    string s = val as string;
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        string norm = s.Replace('/', '\\').TrimEnd('\\');
                        if (SafeDirectoryExists(norm))
                        {
                            return norm;
                        }
                    }
                }
                catch
                {
                }
            }
            return null;
        }

        public static string DetectSteamDirectory(string explicitSteamDir, string hintGameDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitSteamDir))
            {
                string norm = NormalizeCandidatePath(explicitSteamDir);
                return SafeDirectoryExists(norm) ? SafeGetFullPath(norm) : null;
            }

            List<string> candidates = new List<string>();
            string regRoot = GetSteamRootFromRegistry();
            if (!string.IsNullOrEmpty(regRoot))
            {
                candidates.Add(regRoot);
            }
            candidates.Add(@"C:\Program Files (x86)\Steam");
            candidates.Add(@"C:\Program Files\Steam");
            AddProtonSteamRoots(candidates);

            if (!string.IsNullOrWhiteSpace(hintGameDir))
            {
                TryAddSteamRootFromGameDir(candidates, hintGameDir);
            }

            List<string> driveRoots = GetSafeLocalDriveRoots();
            for (int i = 0; i < driveRoots.Count; i++)
            {
                candidates.Add(SafeCombine(driveRoots[i], "Steam"));
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string cand = candidates[i];
                if (SafeDirectoryExists(cand))
                {
                    string statsDir = SafeCombine(cand, @"appcache\stats");
                    string steamExe = SafeCombine(cand, "steam.exe");
                    string steamSh = SafeCombine(cand, "steam.sh");
                    string vdf = SafeCombine(cand, @"steamapps\libraryfolders.vdf");
                    if (SafeDirectoryExists(statsDir) || SafeFileExists(steamExe) || SafeFileExists(steamSh) || SafeFileExists(vdf))
                    {
                        return SafeGetFullPath(cand);
                    }
                }
            }
            return null;
        }

        public static string TryAutoDetectGameDirectory(string appBaseDir)
        {
            return AutoDetectGameDirectory(null, appBaseDir);
        }

        public static string AutoDetectGameDirectory(string explicitSteamDir, string appBaseDir)
        {
            List<string> candidates = new List<string>();

            if (!string.IsNullOrWhiteSpace(explicitSteamDir))
            {
                AddSteamRootCandidates(candidates, NormalizeCandidatePath(explicitSteamDir));
            }

            string steamRoot = GetSteamRootFromRegistry();
            if (!string.IsNullOrEmpty(steamRoot))
            {
                AddSteamRootCandidates(candidates, steamRoot);
            }

            AddSteamRootCandidates(candidates, @"C:\Program Files (x86)\Steam");
            AddSteamRootCandidates(candidates, @"C:\Program Files\Steam");
            AddWineSteamDeckCandidates(candidates);

            List<string> drives = GetSafeLocalDriveRoots();
            for (int i = 0; i < drives.Count; i++)
            {
                string root = drives[i];
                AddSteamRootCandidates(candidates, SafeCombine(root, "SteamLibrary"));
                AddSteamRootCandidates(candidates, SafeCombine(root, "Steam"));
                AddLibraryFolderGameCandidates(candidates, SafeCombine(root, @"Games\Steam"));
                candidates.Add(SafeCombine(root, @"Games\" + GameFolderName));
            }

            if (!string.IsNullOrWhiteSpace(appBaseDir))
            {
                string normBase = NormalizeCandidatePath(appBaseDir);
                if (!string.IsNullOrEmpty(normBase))
                {
                    candidates.Add(normBase);
                    try
                    {
                        string parent = Path.GetDirectoryName(normBase);
                        if (!string.IsNullOrWhiteSpace(parent))
                        {
                            candidates.Add(parent);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < candidates.Count; i++)
            {
                string cand = NormalizeCandidatePath(candidates[i]);
                if (string.IsNullOrEmpty(cand) || !seen.Add(cand))
                {
                    continue;
                }
                if (IsValidGameDirectory(cand))
                {
                    return SafeGetFullPath(cand);
                }
            }

            return null;
        }

        internal static List<string> ExtractLibraryGameCandidatesFromVdf(string vdfText)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrWhiteSpace(vdfText))
            {
                return result;
            }

            MatchCollection matches = Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\"");
            foreach (Match m in matches)
            {
                string libPath = m.Groups[1].Value.Replace("\\\\", "\\").Trim();
                if (string.IsNullOrEmpty(libPath))
                {
                    continue;
                }

                if (libPath.StartsWith("/", StringComparison.Ordinal))
                {
                    string posixRoot = TrimTrailingSeparators(libPath);
                    result.Add(posixRoot + "/steamapps/common/" + GameFolderName);
                    string wineRoot = "Z:" + posixRoot.Replace('/', '\\');
                    AddLibraryFolderGameCandidates(result, wineRoot);
                }
                else
                {
                    string normLib = NormalizeCandidatePath(libPath);
                    AddLibraryFolderGameCandidates(result, normLib);
                }
            }
            return result;
        }

        private static void AddLibraryFolderGameCandidates(List<string> candidates, string libraryRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                return;
            }
            string steamapps = SafeCombine(libraryRoot, "steamapps");
            string acfPath = SafeCombine(steamapps, "appmanifest_" + TargetAppId + ".acf");
            if (SafeFileExists(acfPath))
            {
                try
                {
                    string acf = File.ReadAllText(acfPath, BinaryUtils.Utf8NoBom);
                    Match m = Regex.Match(acf, "\"installdir\"\\s+\"([^\"]+)\"", RegexOptions.CultureInvariant);
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    {
                        candidates.Add(SafeCombine(steamapps, @"common\" + m.Groups[1].Value.Trim()));
                    }
                }
                catch
                {
                }
            }
            candidates.Add(SafeCombine(steamapps, @"common\" + GameFolderName));
        }

        private static void AddSteamRootCandidates(List<string> candidates, string steamRoot)
        {
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                return;
            }
            AddLibraryFolderGameCandidates(candidates, steamRoot);
            string vdfPath = SafeCombine(steamRoot, @"steamapps\libraryfolders.vdf");
            if (!SafeFileExists(vdfPath))
            {
                return;
            }

            try
            {
                string vdfText = File.ReadAllText(vdfPath, BinaryUtils.Utf8NoBom);
                List<string> vdfCandidates = ExtractLibraryGameCandidatesFromVdf(vdfText);
                for (int i = 0; i < vdfCandidates.Count; i++)
                {
                    candidates.Add(vdfCandidates[i]);
                }
            }
            catch
            {
            }
        }

        private static void AddProtonSteamRoots(List<string> roots)
        {
            roots.Add(@"Z:\home\deck\.local\share\Steam");
            roots.Add(@"Z:\home\deck\.steam\steam");
            roots.Add(@"Z:\home\deck\.steam\root");
            roots.Add(@"Z:\home\deck\.var\app\com.valvesoftware.Steam\.local\share\Steam");

            try
            {
                if (SafeDirectoryExists(@"Z:\home"))
                {
                    string[] homeDirs = Directory.GetDirectories(@"Z:\home");
                    for (int i = 0; i < homeDirs.Length; i++)
                    {
                        roots.Add(SafeCombine(homeDirs[i], @".local\share\Steam"));
                        roots.Add(SafeCombine(homeDirs[i], @".steam\steam"));
                        roots.Add(SafeCombine(homeDirs[i], @".var\app\com.valvesoftware.Steam\.local\share\Steam"));
                    }
                }
            }
            catch
            {
            }
        }

        private static void AddWineSteamDeckCandidates(List<string> candidates)
        {
            List<string> protonRoots = new List<string>();
            AddProtonSteamRoots(protonRoots);
            for (int i = 0; i < protonRoots.Count; i++)
            {
                if (SafeDirectoryExists(protonRoots[i]))
                {
                    AddSteamRootCandidates(candidates, protonRoots[i]);
                }
            }

            string[] sdRoots = new string[]
            {
                @"Z:\run\media\mmcblk0p1",
                @"Z:\run\media\mmcblk0p1\SteamLibrary"
            };
            for (int i = 0; i < sdRoots.Length; i++)
            {
                if (SafeDirectoryExists(sdRoots[i]))
                {
                    AddLibraryFolderGameCandidates(candidates, sdRoots[i]);
                }
            }

            if (SafeDirectoryExists(@"Z:\run\media"))
            {
                try
                {
                    string[] mediaEntries = Directory.GetDirectories(@"Z:\run\media");
                    for (int i = 0; i < mediaEntries.Length; i++)
                    {
                        AddLibraryFolderGameCandidates(candidates, mediaEntries[i]);
                        AddLibraryFolderGameCandidates(candidates, SafeCombine(mediaEntries[i], "SteamLibrary"));
                        try
                        {
                            string[] subMounts = Directory.GetDirectories(mediaEntries[i]);
                            for (int j = 0; j < subMounts.Length; j++)
                            {
                                AddLibraryFolderGameCandidates(candidates, subMounts[j]);
                                AddLibraryFolderGameCandidates(candidates, SafeCombine(subMounts[j], "SteamLibrary"));
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private static void TryAddSteamRootFromGameDir(List<string> candidates, string gameDir)
        {
            try
            {
                string norm = NormalizePickedGameDirectory(gameDir);
                if (string.IsNullOrEmpty(norm))
                {
                    return;
                }
                DirectoryInfo commonDir = Directory.GetParent(norm);
                if (commonDir != null && string.Equals(commonDir.Name, "common", StringComparison.OrdinalIgnoreCase))
                {
                    DirectoryInfo steamappsDir = commonDir.Parent;
                    if (steamappsDir != null && string.Equals(steamappsDir.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        DirectoryInfo rootDir = steamappsDir.Parent;
                        if (rootDir != null)
                        {
                            candidates.Add(rootDir.FullName);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static List<string> GetSafeLocalDriveRoots()
        {
            List<string> roots = new List<string>();
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                for (int i = 0; i < drives.Length; i++)
                {
                    DriveInfo d = drives[i];
                    if ((d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable) && d.IsReady)
                    {
                        if (added.Add(d.Name))
                        {
                            roots.Add(d.Name);
                        }
                    }
                }
            }
            catch
            {
            }

            for (char c = 'C'; c <= 'Z'; c++)
            {
                string root = c + @":\";
                if (!added.Contains(root) && SafeDirectoryExists(root))
                {
                    added.Add(root);
                    roots.Add(root);
                }
            }
            return roots;
        }

        public static string ResolveGameDirectory(string explicitDir, string appBaseDir)
        {
            return ResolveGameDirectory(explicitDir, null, appBaseDir);
        }

        public static string ResolveGameDirectory(string explicitDir, string explicitSteamDir, string appBaseDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                string normalized = NormalizePickedGameDirectory(explicitDir);
                if (!IsValidGameDirectory(normalized))
                {
                    throw new DirectoryNotFoundException(
                        "В указанной папке не найдены файлы игры We March as One (WeMarchAsOne_Data\\resources.assets): " + normalized);
                }
                return SafeGetFullPath(normalized);
            }

            string detected = AutoDetectGameDirectory(explicitSteamDir, appBaseDir);
            if (string.IsNullOrEmpty(detected))
            {
                throw new DirectoryNotFoundException(
                    "Не удалось автоматически найти папку игры We March as One. Укажите путь вручную.");
            }
            return detected;
        }

        public static string ResolveSteamStatsDirectory(string explicitStatsDir)
        {
            return ResolveSteamStatsDirectory(explicitStatsDir, null, null);
        }

        public static string ResolveSteamStatsDirectory(string explicitStatsDir, string explicitSteamDir, string hintGameDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitStatsDir))
            {
                string normalized = NormalizeCandidatePath(explicitStatsDir);
                if (SafeDirectoryExists(normalized))
                {
                    return SafeGetFullPath(normalized);
                }
                return null;
            }

            string steamDir = DetectSteamDirectory(explicitSteamDir, hintGameDir);
            if (!string.IsNullOrEmpty(steamDir))
            {
                string nested = SafeCombine(steamDir, @"appcache\stats");
                if (SafeDirectoryExists(nested))
                {
                    return SafeGetFullPath(nested);
                }
                if (SafeFileExists(SafeCombine(steamDir, SchemaFileName))
                    || steamDir.EndsWith(@"\appcache\stats", StringComparison.OrdinalIgnoreCase))
                {
                    return SafeGetFullPath(steamDir);
                }
            }

            List<string> fallbacks = new List<string>();
            fallbacks.Add(@"C:\Program Files (x86)\Steam\appcache\stats");
            if (SafeDirectoryExists(@"Z:\home\deck\.local\share\Steam"))
            {
                fallbacks.Add(@"Z:\home\deck\.local\share\Steam\appcache\stats");
            }
            if (SafeDirectoryExists(@"Z:\home\deck\.steam\steam"))
            {
                fallbacks.Add(@"Z:\home\deck\.steam\steam\appcache\stats");
            }

            for (int i = 0; i < fallbacks.Count; i++)
            {
                if (SafeDirectoryExists(fallbacks[i]))
                {
                    return SafeGetFullPath(fallbacks[i]);
                }
            }
            return null;
        }

        public static void AssertGameClosed(string resolvedGameDir)
        {
            string targetExe = SafeCombine(resolvedGameDir, "WeMarchAsOne.exe");
            bool targetExeExists = SafeFileExists(targetExe);

            Process[] procs;
            try
            {
                procs = Process.GetProcessesByName("WeMarchAsOne");
            }
            catch
            {
                return;
            }

            for (int i = 0; i < procs.Length; i++)
            {
                Process p = procs[i];
                try
                {
                    string procPath = null;
                    try
                    {
                        if (p.MainModule != null)
                        {
                            procPath = p.MainModule.FileName;
                        }
                    }
                    catch
                    {
                        procPath = null;
                    }

                    if (procPath != null)
                    {
                        if (string.Equals(procPath, targetExe, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                "Игра We March as One сейчас запущена (PID " + p.Id + "). Пожалуйста, закройте игру перед установкой или откатом.");
                        }
                    }
                    else if (targetExeExists)
                    {
                        throw new InvalidOperationException(
                            "Обнаружен запущенный процесс WeMarchAsOne.exe (PID " + p.Id + "). Пожалуйста, закройте игру перед продолжением.");
                    }
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        public static bool SafeFileExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        public static bool SafeDirectoryExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        private static string SafeCombine(string a, string b)
        {
            try
            {
                return Path.Combine(a, b);
            }
            catch
            {
                return a.TrimEnd('\\') + "\\" + b.TrimStart('\\');
            }
        }

        private static string SafeGetFullPath(string path)
        {
            try
            {
                return TrimTrailingSeparators(Path.GetFullPath(path));
            }
            catch
            {
                return TrimTrailingSeparators(path);
            }
        }

        private static string TrimTrailingSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }
            if (path == "/")
            {
                return "/";
            }
            if (path.Length == 3
                && char.IsLetter(path[0])
                && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/'))
            {
                return path[0] + ":\\";
            }
            return path.TrimEnd('\\', '/');
        }
    }
}
