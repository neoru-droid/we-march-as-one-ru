using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace WmaoRuInstaller
{
    internal sealed class InstallOptions
    {
        public string GameDir;
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
            Stopwatch sw = Stopwatch.StartNew();

            string fontsDir = Path.Combine(appBaseDir, @"data\fonts");
            string translationsPath = Path.Combine(appBaseDir, @"data\translations_ru.json");
            string toolsDir = Path.Combine(appBaseDir, "tools");
            string patcherExe = Path.Combine(toolsDir, "Patcher.exe");
            string runtimeDll = Path.Combine(toolsDir, "RuModRuntime.dll");

            if (!Directory.Exists(fontsDir))
            {
                throw new DirectoryNotFoundException("Не найдена папка шрифтов установщика: " + fontsDir);
            }
            if (!File.Exists(translationsPath))
            {
                throw new FileNotFoundException("Не найден файл переводов: " + translationsPath, translationsPath);
            }
            if (!options.SkipAssemblyPatch)
            {
                if (!File.Exists(patcherExe))
                {
                    throw new FileNotFoundException("Не найден инструмент патчинга сборки: " + patcherExe, patcherExe);
                }
                if (!File.Exists(runtimeDll))
                {
                    throw new FileNotFoundException("Не найден модуль интерфейса: " + runtimeDll, runtimeDll);
                }
            }

            string gameDir = SteamLocator.ResolveGameDirectory(options.GameDir, appBaseDir);
            SteamLocator.AssertGameClosed(gameDir);

            LogLine(log, "Папка игры: " + gameDir);
            TranslationPayload payload = TranslationPayload.LoadFromFile(translationsPath);

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
                int patchedFonts = AssetsFontPatcher.PatchResourcesFile(srcResources.BackupPath, fontsDir, stagedResources);
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
                    string managedDir = Path.Combine(gameDir, @"WeMarchAsOne_Data\Managed");
                    RunAssemblyPatcher(
                        patcherExe,
                        srcAssembly.BackupPath,
                        runtimeDll,
                        managedDir,
                        stagedAssembly,
                        options.EnableAchievementsUi,
                        log);
                }
                else
                {
                    LogLine(log, "[4/4] Модификация Assembly-CSharp.dll пропущена по флагу.");
                }

                // Optional Steam stats schema localization
                CleanSourceInfo srcSteamSchema = null;
                string stagedSteamSchema = null;
                string steamStatsDir = SteamLocator.ResolveSteamStatsDirectory(options.SteamStatsDir);
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
                        catch (Exception ex)
                        {
                            srcSteamSchema = null;
                            stagedSteamSchema = null;
                            LogLine(log, "  • Примечание: схема Steam пропущена (" + ex.Message + ").");
                        }
                    }
                }

                // Commit staged files atomically and record SHA-256 hashes
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
                    catch (Exception ex)
                    {
                        LogLine(log, "  • Примечание: не удалось записать схему Steam (" + ex.Message + ").");
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
            Stopwatch sw = Stopwatch.StartNew();
            string gameDir = SteamLocator.ResolveGameDirectory(explicitGameDir, appBaseDir);
            SteamLocator.AssertGameClosed(gameDir);

            LogLine(log, "Папка игры: " + gameDir);
            LogLine(log, "Восстановление нетронутых оригинальных файлов из резервной копии...");
            int count = BackupManager.RestoreOriginals(gameDir, explicitSteamStatsDir, log);
            sw.Stop();
            LogLine(
                log,
                string.Format(
                    "Готово! Восстановлено оригинальных файлов: {0} (проверка SHA-256 пройдена, {1:0.00} сек.).",
                    count,
                    sw.Elapsed.TotalSeconds));
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
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }
            if (value.EndsWith("\\", StringComparison.Ordinal))
            {
                return "\"" + value + "\\\"";
            }
            return "\"" + value + "\"";
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
