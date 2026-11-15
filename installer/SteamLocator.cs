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
        public const string GameFolderName = "We March as One";
        public const string SchemaFileName = "UserGameStatsSchema_5189160.bin";

        public static bool IsValidGameDirectory(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath))
            {
                return false;
            }
            try
            {
                string resPath = Path.Combine(dirPath, @"WeMarchAsOne_Data\resources.assets");
                string catPath = Path.Combine(dirPath, @"WeMarchAsOne_Data\StreamingAssets\aa\catalog.bin");
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
                    // Ignore inaccessible registry hives
                }
            }
            return null;
        }

        public static string TryAutoDetectGameDirectory(string appBaseDir)
        {
            List<string> candidates = new List<string>();

            string steamRoot = GetSteamRootFromRegistry();
            if (!string.IsNullOrEmpty(steamRoot))
            {
                candidates.Add(SafeCombine(steamRoot, @"steamapps\common\" + GameFolderName));
                string vdfPath = SafeCombine(steamRoot, @"steamapps\libraryfolders.vdf");
                if (SafeFileExists(vdfPath))
                {
                    try
                    {
                        string vdfText = File.ReadAllText(vdfPath, BinaryUtils.Utf8NoBom);
                        MatchCollection matches = Regex.Matches(vdfText, "\"path\"\\s+\"([^\"]+)\"");
                        foreach (Match m in matches)
                        {
                            string libPath = m.Groups[1].Value.Replace("\\\\", "\\").TrimEnd('\\');
                            candidates.Add(SafeCombine(libPath, @"steamapps\common\" + GameFolderName));
                        }
                    }
                    catch
                    {
                        // Ignore malformed libraryfolders.vdf
                    }
                }
            }

            candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\" + GameFolderName);
            string[] drives = new string[] { "C", "D", "E", "F", "G", "H", "I", "J" };
            for (int i = 0; i < drives.Length; i++)
            {
                candidates.Add(drives[i] + @":\SteamLibrary\steamapps\common\" + GameFolderName);
                candidates.Add(drives[i] + @":\Steam\steamapps\common\" + GameFolderName);
                candidates.Add(drives[i] + @":\Games\" + GameFolderName);
            }

            if (!string.IsNullOrWhiteSpace(appBaseDir))
            {
                candidates.Add(appBaseDir);
                try
                {
                    string parent = Path.GetDirectoryName(appBaseDir.TrimEnd('\\'));
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        candidates.Add(parent);
                    }
                }
                catch
                {
                }
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                string cand = candidates[i];
                if (IsValidGameDirectory(cand))
                {
                    try
                    {
                        return Path.GetFullPath(cand).TrimEnd('\\');
                    }
                    catch
                    {
                        return cand.TrimEnd('\\');
                    }
                }
            }

            return null;
        }

        public static string ResolveGameDirectory(string explicitDir, string appBaseDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitDir))
            {
                string trimmed = explicitDir.Trim().Trim('"');
                if (!IsValidGameDirectory(trimmed))
                {
                    throw new DirectoryNotFoundException(
                        "В указанной папке не найдены файлы игры We March as One (WeMarchAsOne_Data\\resources.assets): " + trimmed);
                }
                return Path.GetFullPath(trimmed).TrimEnd('\\');
            }

            string detected = TryAutoDetectGameDirectory(appBaseDir);
            if (string.IsNullOrEmpty(detected))
            {
                throw new DirectoryNotFoundException(
                    "Не удалось автоматически найти папку игры We March as One. Укажите путь вручную.");
            }
            return detected;
        }

        public static string ResolveSteamStatsDirectory(string explicitStatsDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitStatsDir))
            {
                string trimmed = explicitStatsDir.Trim().Trim('"');
                if (SafeDirectoryExists(trimmed))
                {
                    return Path.GetFullPath(trimmed).TrimEnd('\\');
                }
                return null;
            }

            List<string> candidates = new List<string>();
            string steamRoot = GetSteamRootFromRegistry();
            if (!string.IsNullOrEmpty(steamRoot))
            {
                candidates.Add(SafeCombine(steamRoot, @"appcache\stats"));
            }
            candidates.Add(@"C:\Program Files (x86)\Steam\appcache\stats");

            for (int i = 0; i < candidates.Count; i++)
            {
                if (SafeDirectoryExists(candidates[i]))
                {
                    try
                    {
                        return Path.GetFullPath(candidates[i]).TrimEnd('\\');
                    }
                    catch
                    {
                        return candidates[i].TrimEnd('\\');
                    }
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
    }
}
