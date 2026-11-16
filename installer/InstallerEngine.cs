using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace WmaoRuInstaller
{
    internal sealed class InstallOptions
    {
        public string GameDir;
        public string SteamDir;
        public string SteamStatsDir;
        public bool EnableAchievementsUi = true;
        public bool SkipAssemblyPatch = false;
    }

    internal static class InstallerEngine
    {
        private const string RelResources = @"WeMarchAsOne_Data\resources.assets";
        private const string RelBundle = @"WeMarchAsOne_Data\StreamingAssets\aa\StandaloneWindows64\localization-string-tables-english_assets_all.bundle";
        private const string RelCatalog = @"WeMarchAsOne_Data\StreamingAssets\aa\catalog.bin";
        private const string RelAssembly = @"WeMarchAsOne_Data\Managed\Assembly-CSharp.dll";
        private const string RelSteamSchema = @"steam_stats\UserGameStatsSchema_5189160.bin";

        public static void Install(string appBaseDir, InstallOptions options, Action<string> log)
        {
            bool schemaAccessDenied;
            Install(appBaseDir, options, log, out schemaAccessDenied);
        }

        public static void Install(
            string appBaseDir,
            InstallOptions options,
            Action<string> log,
            out bool schemaAccessDenied)
        {
            schemaAccessDenied = false;
            Stopwatch sw = Stopwatch.StartNew();
            InstallerPayload loadedPayload = PayloadProvider.LoadPayload(appBaseDir, options.SkipAssemblyPatch);
            TranslationPayload payload = loadedPayload.Translations;

            string gameDir = SteamLocator.ResolveGameDirectory(options.GameDir, options.SteamDir, appBaseDir);
            SteamLocator.AssertGameClosed(gameDir);

            LogLine(log, "Папка игры: " + gameDir);

            BackupManifest manifest = BackupManager.ReadManifestOrEmpty(gameDir);
            string stagingRoot = Path.Combine(BackupManager.GetBackupRoot(gameDir), "_staging");
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, true);
            }
            Directory.CreateDirectory(stagingRoot);

            try
            {
                LogLine(log, "[1/4] Проверка и резервное копирование оригинальных файлов...");
                CleanSourceInfo srcResources = BackupManager.PrepareCleanSource(
                    gameDir, manifest, RelResources, Path.Combine(gameDir, RelResources), log);
                CleanSourceInfo srcBundle = BackupManager.PrepareCleanSource(
                    gameDir, manifest, RelBundle, Path.Combine(gameDir, RelBundle), log);
                CleanSourceInfo srcCatalog = BackupManager.PrepareCleanSource(
                    gameDir, manifest, RelCatalog, Path.Combine(gameDir, RelCatalog), log);

                CleanSourceInfo srcAssembly = null;
                if (!options.SkipAssemblyPatch)
                {
                    srcAssembly = BackupManager.PrepareCleanSource(
                        gameDir, manifest, RelAssembly, Path.Combine(gameDir, RelAssembly), log);
                }

                LogLine(log, "[2/4] Встраивание 5 кириллических шрифтов в resources.assets...");
                string stagedResources = Path.Combine(stagingRoot, RelResources);
                int patchedFonts = AssetsFontPatcher.PatchResourcesFromFileBytes(
                    srcResources.BackupPath,
                    loadedPayload.FontFiles,
                    stagedResources);
                LogLine(log, "  • Обновлено шрифтов: " + patchedFonts + " / 5");

                LogLine(log, "[3/4] Локализация таблиц текста и обновление catalog.bin...");
                string stagedBundle = Path.Combine(stagingRoot, RelBundle);
                string stagedCatalog = Path.Combine(stagingRoot, RelCatalog);
                BundlePatchResult bundleRes = BundleStringPatcher.PatchBundleAndCatalog(
                    srcBundle.BackupPath,
                    srcCatalog.BackupPath,
                    payload.Tables,
                    stagedBundle,
                    stagedCatalog);
                LogLine(
                    log,
                    string.Format(
                        "  • Переведено строк: {0} (CRC32: 0x{1:x8}, размер: {2} Б)",
                        bundleRes.UpdatedRows,
                        bundleRes.BundleCrc,
                        bundleRes.BundleSize));

                string stagedAssembly = null;
                if (!options.SkipAssemblyPatch)
                {
                    string modeLabel = options.EnableAchievementsUi
                        ? "поддержка кириллицы + экран «Достижения»"
                        : "поддержка кириллицы (без кнопки «Достижения»)";
                    LogLine(log, "[4/4] Модификация Assembly-CSharp.dll (" + modeLabel + ")...");
                    stagedAssembly = Path.Combine(stagingRoot, RelAssembly);
                    string toolsWorkDir = MaterializeToolsDirectory(loadedPayload, stagingRoot);
                    string managedDir = Path.Combine(gameDir, @"WeMarchAsOne_Data\Managed");
                    RunAssemblyPatcher(
                        Path.Combine(toolsWorkDir, "Patcher.exe"),
                        srcAssembly.BackupPath,
                        Path.Combine(toolsWorkDir, "RuModRuntime.dll"),
                        managedDir,
                        stagedAssembly,
                        options.EnableAchievementsUi,
                        log);
                }
                else
                {
                    LogLine(log, "[4/4] Модификация Assembly-CSharp.dll пропущена по флагу.");
                }

                CleanSourceInfo srcSteamSchema = null;
                string stagedSteamSchema = null;
                string steamStatsDir = SteamLocator.ResolveSteamStatsDirectory(
                    options.SteamStatsDir,
                    options.SteamDir,
                    gameDir);
                if (!string.IsNullOrEmpty(steamStatsDir) && payload.SteamAchievements.Count > 0)
                {
                    string liveSchemaPath = Path.Combine(steamStatsDir, SteamLocator.SchemaFileName);
                    if (SteamLocator.SafeFileExists(liveSchemaPath))
                    {
                        try
                        {
                            srcSteamSchema = BackupManager.PrepareCleanSource(
                                gameDir, manifest, RelSteamSchema, liveSchemaPath, log);
                            stagedSteamSchema = Path.Combine(stagingRoot, RelSteamSchema);
                            int achCount = VdfSchemaPatcher.PatchSchemaFile(
                                srcSteamSchema.BackupPath,
                                payload.SteamAchievements,
                                stagedSteamSchema);
                            LogLine(log, "  • Обновлена локальная схема достижений Steam (" + achCount + " шт.).");
                        }
                        catch (UnauthorizedAccessException)
                        {
                            srcSteamSchema = null;
                            stagedSteamSchema = null;
                            schemaAccessDenied = true;
                            LogLine(log, "  • Примечание: нет прав записи для схемы достижений Steam (Program Files). Текст, шрифты и экран достижений в игре установлены полностью.");
                        }
                        catch (Exception ex)
                        {
                            srcSteamSchema = null;
                            stagedSteamSchema = null;
                            LogLine(log, "  • Примечание: схема Steam пропущена (" + InstallerDiagnostics.FormatUserError(ex) + ").");
                        }
                    }
                }

                CommitStagedFile(stagedResources, srcResources, manifest, null);
                CommitStagedFile(stagedBundle, srcBundle, manifest, null);
                CommitStagedFile(stagedCatalog, srcCatalog, manifest, null);
                if (srcAssembly != null && stagedAssembly != null)
                {
                    CommitStagedFile(stagedAssembly, srcAssembly, manifest, null);
                }
                if (srcSteamSchema != null && stagedSteamSchema != null)
                {
                    try
                    {
                        CommitStagedFile(stagedSteamSchema, srcSteamSchema, manifest, srcSteamSchema.LivePath);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        schemaAccessDenied = true;
                        LogLine(log, "  • Примечание: нет прав записи для схемы достижений Steam (Program Files). Текст, шрифты и экран достижений в игре установлены полностью.");
                    }
                    catch (Exception ex)
                    {
                        LogLine(log, "  • Примечание: не удалось записать схему Steam (" + InstallerDiagnostics.FormatUserError(ex) + ").");
                    }
                }

                manifest.AchievementsUi = options.EnableAchievementsUi;
                BackupManager.WriteManifest(gameDir, manifest);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(stagingRoot))
                    {
                        Directory.Delete(stagingRoot, true);
                    }
                }
                catch
                {
                }
            }

            BackupManager.AssertNoBakFiles(gameDir);
            sw.Stop();
            LogLine(
                log,
                string.Format(
                    "Готово! Русификатор успешно установлен за {0:0.00} сек.",
                    sw.Elapsed.TotalSeconds));
        }

        public static void Restore(string appBaseDir, string explicitGameDir, string explicitSteamStatsDir, Action<string> log)
        {
            Restore(appBaseDir, explicitGameDir, null, explicitSteamStatsDir, log);
        }

        public static void Restore(
            string appBaseDir,
            string explicitGameDir,
            string explicitSteamDir,
            string explicitSteamStatsDir,
            Action<string> log)
        {
            Stopwatch sw = Stopwatch.StartNew();
            string gameDir = SteamLocator.ResolveGameDirectory(explicitGameDir, explicitSteamDir, appBaseDir);
            SteamLocator.AssertGameClosed(gameDir);

            string resolvedStatsDir = explicitSteamStatsDir;
            if (string.IsNullOrWhiteSpace(resolvedStatsDir) && !string.IsNullOrWhiteSpace(explicitSteamDir))
            {
                resolvedStatsDir = SteamLocator.ResolveSteamStatsDirectory(null, explicitSteamDir, gameDir);
            }

            LogLine(log, "Папка игры: " + gameDir);
            LogLine(log, "Восстановление нетронутых оригинальных файлов из резервной копии...");
            int count = BackupManager.RestoreOriginals(gameDir, resolvedStatsDir, log);
            sw.Stop();
            LogLine(
                log,
                string.Format(
                    "Готово! Восстановлено оригинальных файлов: {0} (проверка SHA-256 пройдена, {1:0.00} сек.).",
                    count,
                    sw.Elapsed.TotalSeconds));
        }

        public static int RunStatusCli(
            string appBaseDir,
            string explicitGameDir,
            string explicitSteamDir,
            string explicitSteamStatsDir)
        {
            InstallerPayload payload = PayloadProvider.LoadPayload(appBaseDir, true);
            string gameDir = !string.IsNullOrWhiteSpace(explicitGameDir)
                ? SteamLocator.NormalizePickedGameDirectory(explicitGameDir)
                : SteamLocator.AutoDetectGameDirectory(explicitSteamDir, appBaseDir);
            bool validGame = SteamLocator.IsValidGameDirectory(gameDir);

            string steamDir = SteamLocator.DetectSteamDirectory(explicitSteamDir, validGame ? gameDir : null);
            string statsDir = SteamLocator.ResolveSteamStatsDirectory(explicitSteamStatsDir, steamDir, validGame ? gameDir : null);

            int totalAch = payload.Translations.SteamAchievements.Count > 0
                ? payload.Translations.SteamAchievements.Count
                : 30;
            int unlocked = SteamUserStatsReader.ReadUnlockedCount(statsDir, totalAch);

            bool hasBackup = validGame && BackupManager.HasBackup(gameDir);
            bool installed = validGame && BackupManager.IsRussifierInstalled(gameDir);
            bool achUi = hasBackup ? BackupManager.ReadManifestOrEmpty(gameDir).AchievementsUi : true;

            Console.WriteLine("Version: " + payload.Version);
            Console.WriteLine("GameDir: " + (validGame ? gameDir : "NOT_FOUND"));
            Console.WriteLine("SteamDir: " + (!string.IsNullOrEmpty(steamDir) ? steamDir : "NOT_FOUND"));
            Console.WriteLine("Installed: " + (installed ? "true" : "false"));
            Console.WriteLine("AchievementsUi: " + (achUi ? "true" : "false"));
            Console.WriteLine("BackupManifest: " + (hasBackup ? "present" : "absent"));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "AchievementsUnlocked: {0}/{1}", unlocked, totalAch));

            return validGame ? 0 : 2;
        }

        private static string MaterializeToolsDirectory(InstallerPayload payload, string stagingRoot)
        {
            string toolsDir = Path.Combine(stagingRoot, "_tools");
            Directory.CreateDirectory(toolsDir);
            foreach (KeyValuePair<string, byte[]> kv in payload.ToolFiles)
            {
                File.WriteAllBytes(Path.Combine(toolsDir, kv.Key), kv.Value);
            }
            return toolsDir;
        }

        private static void CommitStagedFile(
            string stagedPath,
            CleanSourceInfo srcInfo,
            BackupManifest manifest,
            string liveOverridePath)
        {
            string patchedSha = BinaryUtils.ComputeFileSha256Hex(stagedPath);
            BackupManager.CopyAndVerify(stagedPath, srcInfo.LivePath, patchedSha);
            manifest.Entries[srcInfo.Key] = new BackupEntry
            {
                Existed = true,
                OrigSha256 = srcInfo.OrigSha256,
                PatchedSha256 = patchedSha,
                LiveOverridePath = liveOverridePath
            };
        }

        private static void RunAssemblyPatcher(
            string patcherExe,
            string cleanAssemblyPath,
            string runtimeDllPath,
            string managedDir,
            string outAssemblyPath,
            bool enableAchievementsUi,
            Action<string> log)
        {
            string outDir = Path.GetDirectoryName(outAssemblyPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            List<string> args = new List<string>
            {
                "patch",
                QuoteArg(cleanAssemblyPath),
                QuoteArg(runtimeDllPath),
                QuoteArg(managedDir),
                QuoteArg(outAssemblyPath)
            };
            if (!enableAchievementsUi)
            {
                args.Add("--no-achievements");
            }

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = patcherExe,
                Arguments = string.Join(" ", args.ToArray()),
                WorkingDirectory = Path.GetDirectoryName(patcherExe),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = BinaryUtils.Utf8NoBom,
                StandardErrorEncoding = BinaryUtils.Utf8NoBom
            };

            using (Process proc = Process.Start(psi))
            {
                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    string[] lines = stdout.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0
                            || lines[i].IndexOf("Предупреждение", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            LogLine(log, "  • " + lines[i].Trim());
                        }
                    }
                }

                if (proc.ExitCode != 0 || !File.Exists(outAssemblyPath))
                {
                    string details = (stderr + "\n" + stdout).Trim();
                    throw new InvalidOperationException(
                        "Ошибка модификации Assembly-CSharp.dll (код " + proc.ExitCode + "): " + details);
                }
            }
        }

        private static string QuoteArg(string value)
        {
            return InstallerDiagnostics.QuoteCommandLineArg(value);
        }

        private static void LogLine(Action<string> log, string message)
        {
            if (log != null)
            {
                log(message);
            }
        }
    }
}
