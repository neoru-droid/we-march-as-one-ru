using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;

namespace WmaoRuInstaller
{
    internal sealed class PayloadValidationResult
    {
        public bool IsValid;
        public bool IsTempArchiveDir;
        public readonly List<string> MissingFiles = new List<string>();
        public string UserMessage = "";
    }

    internal static class InstallerDiagnostics
    {
        public const string UnextractedBadgeText = "⚠ Архив не распакован целиком";
        public const string UnextractedArchiveMessage =
            "ВНИМАНИЕ: Не найдены папки data и tools рядом с программой (возможно, файл запущен прямо внутри архива .zip без распаковки). "
            + "Закройте это окно, распакуйте ВЕСЬ архив целиком в любую отдельную папку и запустите WeMarchAsOne-RU-Setup.exe оттуда.";

        private const int Win32SharingViolation = 32;
        private const int Win32LockViolation = 33;
        private const int Win32ErrorCancelled = 1223;

        private static readonly string[] RequiredDataFiles = new string[]
        {
            @"data\fonts\Sora-Regular.ttf",
            @"data\fonts\Anton-Regular.ttf",
            @"data\fonts\BigShouldersDisplay-ExtraBold.ttf",
            @"data\fonts\Sora-Bold.ttf",
            @"data\fonts\BigShouldersDisplay-Black.ttf",
            @"data\translations_ru.json"
        };

        private static readonly string[] RequiredToolFiles = new string[]
        {
            @"tools\Patcher.exe",
            @"tools\RuModRuntime.dll",
            @"tools\Mono.Cecil.dll",
            @"tools\Mono.Cecil.Rocks.dll"
        };

        public static PayloadValidationResult ValidatePayload(string appBaseDir)
        {
            return ValidatePayload(appBaseDir, false);
        }

        public static PayloadValidationResult ValidatePayload(string appBaseDir, bool skipAssemblyPatch)
        {
            PayloadValidationResult result = new PayloadValidationResult();
            if (PayloadProvider.HasEmbeddedPayload())
            {
                result.IsValid = true;
                return result;
            }

            string baseDir = string.IsNullOrWhiteSpace(appBaseDir)
                ? ""
                : appBaseDir.Trim().TrimEnd('\\', '/');

            result.IsTempArchiveDir = IsTemporaryArchiveDirectory(baseDir);

            CheckRelativeFiles(baseDir, RequiredDataFiles, result.MissingFiles);
            if (!skipAssemblyPatch)
            {
                CheckRelativeFiles(baseDir, RequiredToolFiles, result.MissingFiles);
            }

            if (result.MissingFiles.Count > 0 || result.IsTempArchiveDir)
            {
                result.IsValid = false;
                result.UserMessage = UnextractedArchiveMessage;
                return result;
            }

            result.IsValid = true;
            return result;
        }

        public static void EnsurePayloadComplete(string appBaseDir, bool skipAssemblyPatch)
        {
            PayloadValidationResult check = ValidatePayload(appBaseDir, skipAssemblyPatch);
            if (!check.IsValid)
            {
                throw new InvalidOperationException(check.UserMessage);
            }
        }

        public static bool IsTemporaryArchiveDirectory(string dirPath)
        {
            if (string.IsNullOrWhiteSpace(dirPath))
            {
                return false;
            }

            string normalized = dirPath.Replace('/', '\\');
            int tempIdx = normalized.IndexOf(@"\Temp\", StringComparison.OrdinalIgnoreCase);
            if (tempIdx < 0)
            {
                return false;
            }

            string afterTemp = normalized.Substring(tempIdx + @"\Temp\".Length);
            string[] segments = afterTemp.Split(new char[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                string seg = segments[i];
                if (seg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    || seg.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                    || seg.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Temp1_", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Temp2_", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Temp3_", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Temp4_", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Temp5_", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("7zO", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("7zE", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Rar$", StringComparison.OrdinalIgnoreCase)
                    || seg.StartsWith("Wz", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    if (identity == null)
                    {
                        return false;
                    }
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool NeedsElevationForGameDir(string gameDir)
        {
            if (string.IsNullOrWhiteSpace(gameDir) || IsAdministrator())
            {
                return false;
            }
            string norm = SteamLocator.NormalizePickedGameDirectory(gameDir);
            if (string.IsNullOrEmpty(norm) || !SteamLocator.SafeDirectoryExists(norm))
            {
                return false;
            }
            if (!CanProbeDirectoryWrite(norm))
            {
                return true;
            }
            string dataDir = Path.Combine(norm, "WeMarchAsOne_Data");
            if (SteamLocator.SafeDirectoryExists(dataDir) && !CanProbeDirectoryWrite(dataDir))
            {
                return true;
            }
            string managedDir = Path.Combine(norm, @"WeMarchAsOne_Data\Managed");
            if (SteamLocator.SafeDirectoryExists(managedDir) && !CanProbeDirectoryWrite(managedDir))
            {
                return true;
            }
            string resPath = Path.Combine(norm, @"WeMarchAsOne_Data\resources.assets");
            if (SteamLocator.SafeFileExists(resPath) && !CanOpenExistingFileForWrite(resPath))
            {
                return true;
            }
            string asmPath = Path.Combine(norm, @"WeMarchAsOne_Data\Managed\Assembly-CSharp.dll");
            if (SteamLocator.SafeFileExists(asmPath) && !CanOpenExistingFileForWrite(asmPath))
            {
                return true;
            }
            return false;
        }

        public static bool NeedsElevationForTargetFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || IsAdministrator())
            {
                return false;
            }
            string parent = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(parent) && SteamLocator.SafeDirectoryExists(parent) && !CanProbeDirectoryWrite(parent))
            {
                return true;
            }
            if (SteamLocator.SafeFileExists(filePath) && !CanOpenExistingFileForWrite(filePath))
            {
                return true;
            }
            return false;
        }

        public static string BuildUacArguments(
            string uacModeFlag,
            string gameDir,
            string steamDir,
            bool enableAchievements)
        {
            List<string> args = new List<string>();
            if (!string.IsNullOrWhiteSpace(uacModeFlag))
            {
                args.Add(uacModeFlag);
            }
            if (!string.IsNullOrWhiteSpace(gameDir))
            {
                args.Add("--game-dir");
                args.Add(QuoteCommandLineArg(gameDir.Trim()));
            }
            if (!string.IsNullOrWhiteSpace(steamDir))
            {
                args.Add("--steam-dir");
                args.Add(QuoteCommandLineArg(steamDir.Trim()));
            }
            if (!enableAchievements)
            {
                args.Add("--no-achievements");
            }
            return string.Join(" ", args.ToArray());
        }

        public static bool TryRelaunchElevated(
            string uacModeFlag,
            string exePath,
            string workingDir,
            string gameDir,
            string steamDir,
            bool enableAchievements,
            out bool cancelledByUser,
            out string errorMessage)
        {
            cancelledByUser = false;
            errorMessage = null;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                errorMessage = "Не определён путь к исполняемому файлу установщика.";
                return false;
            }

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = BuildUacArguments(uacModeFlag, gameDir, steamDir, enableAchievements),
                    WorkingDirectory = string.IsNullOrWhiteSpace(workingDir)
                        ? Path.GetDirectoryName(exePath)
                        : workingDir,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.Dispose();
                }
                return true;
            }
            catch (Win32Exception wex)
            {
                if (wex.NativeErrorCode == Win32ErrorCancelled)
                {
                    cancelledByUser = true;
                    return false;
                }
                errorMessage = wex.Message;
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        public static bool TryRelaunchAsAdmin(
            string exePath,
            string workingDir,
            string gameDir,
            bool enableAchievements)
        {
            bool cancelled;
            string err;
            return TryRelaunchElevated(
                "--uac-install",
                exePath,
                workingDir,
                gameDir,
                null,
                enableAchievements,
                out cancelled,
                out err);
        }

        public static bool IsLockedFileException(Exception ex)
        {
            IOException ioEx = ex as IOException;
            if (ioEx == null)
            {
                return false;
            }
            int win32Code = ioEx.HResult & 0xFFFF;
            return win32Code == Win32SharingViolation || win32Code == Win32LockViolation;
        }

        public static string FormatUserError(Exception ex)
        {
            if (ex == null)
            {
                return "Неизвестная ошибка.";
            }

            if (ex is UnauthorizedAccessException)
            {
                return "Отказано в доступе при записи файлов игры. "
                    + "Запустите установщик или командную строку от имени администратора. ("
                    + ex.Message + ")";
            }

            if (IsLockedFileException(ex))
            {
                return "Один из файлов игры сейчас занят другой программой "
                    + "(запущена сама игра, идёт обновление в Steam, проверка антивирусом или открыт проводник). "
                    + "Закройте эти программы и повторите попытку. Подробности: "
                    + ex.Message;
            }

            return ex.Message;
        }

        public static string QuoteCommandLineArg(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }
            string clean = value.Replace("\"", "\\\"");
            if (clean.EndsWith("\\", StringComparison.Ordinal))
            {
                return "\"" + clean + "\\\"";
            }
            return "\"" + clean + "\"";
        }

        private static bool CanProbeDirectoryWrite(string dirPath)
        {
            string probe = Path.Combine(dirPath, ".wmao_uac_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (FileStream fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                return true;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }
                }
                catch
                {
                }
            }
        }

        private static bool CanOpenExistingFileForWrite(string filePath)
        {
            try
            {
                FileAttributes attrs = File.GetAttributes(filePath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                {
                    return false;
                }
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static void CheckRelativeFiles(string baseDir, string[] relativePaths, List<string> missing)
        {
            for (int i = 0; i < relativePaths.Length; i++)
            {
                string rel = relativePaths[i];
                if (string.IsNullOrEmpty(baseDir))
                {
                    missing.Add(rel);
                    continue;
                }
                string full = Path.Combine(baseDir, rel.Replace('\\', Path.DirectorySeparatorChar));
                if (!SteamLocator.SafeFileExists(full))
                {
                    missing.Add(rel);
                }
            }
        }
    }
}
