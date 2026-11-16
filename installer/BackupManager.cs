using System;
using System.Collections.Generic;
using System.IO;

namespace WmaoRuInstaller
{
    internal sealed class BackupEntry
    {
        public bool Existed;
        public string OrigSha256;
        public string PatchedSha256;
        public string LiveOverridePath;
    }

    internal sealed class BackupManifest
    {
        public int AppId = 5189160;
        public int SchemaVersion = 2;
        public bool AchievementsUi = true;
        public string UpdatedUtc = "";
        public readonly Dictionary<string, BackupEntry> Entries =
            new Dictionary<string, BackupEntry>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CleanSourceInfo
    {
        public string Key;
        public string LivePath;
        public string BackupPath;
        public string OrigSha256;
    }

    internal static class BackupManager
    {
        public const string BackupFolderName = "ru_backup_orig";
        public const string ManifestFileName = "backup_manifest.json";

        public static string GetBackupRoot(string gameDir)
        {
            return Path.Combine(gameDir, BackupFolderName);
        }

        public static string GetManifestPath(string gameDir)
        {
            return Path.Combine(GetBackupRoot(gameDir), ManifestFileName);
        }

        public static bool HasBackup(string gameDir)
        {
            string normalized = SteamLocator.NormalizeCandidatePath(gameDir);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }
            return SteamLocator.SafeFileExists(GetManifestPath(normalized));
        }

        public static bool IsRussifierInstalled(string gameDir)
        {
            string normalized = SteamLocator.NormalizeCandidatePath(gameDir);
            if (string.IsNullOrEmpty(normalized) || !HasBackup(normalized))
            {
                return false;
            }
            try
            {
                BackupManifest mf = ReadManifestOrEmpty(normalized);
                BackupEntry resEntry;
                if (!mf.Entries.TryGetValue(@"WeMarchAsOne_Data\resources.assets", out resEntry)
                    || string.IsNullOrEmpty(resEntry.PatchedSha256))
                {
                    return false;
                }
                string liveRes = Path.Combine(normalized, @"WeMarchAsOne_Data\resources.assets");
                if (!SteamLocator.SafeFileExists(liveRes))
                {
                    return false;
                }
                string liveSha = BinaryUtils.ComputeFileSha256Hex(liveRes);
                return string.Equals(liveSha, resEntry.PatchedSha256, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static BackupManifest ReadManifestOrEmpty(string gameDir)
        {
            BackupManifest manifest = new BackupManifest();
            string normalized = SteamLocator.NormalizeCandidatePath(gameDir);
            if (string.IsNullOrEmpty(normalized))
            {
                return manifest;
            }
            string manifestPath = GetManifestPath(normalized);
            if (!SteamLocator.SafeFileExists(manifestPath))
            {
                return manifest;
            }

            string raw = File.ReadAllText(manifestPath, BinaryUtils.Utf8NoBom);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return manifest;
            }

            Dictionary<string, object> root = JsonMini.ParseObject(raw);
            object achUiObj;
            if (root.TryGetValue("achievements_ui", out achUiObj) && achUiObj is bool)
            {
                manifest.AchievementsUi = (bool)achUiObj;
            }

            object entriesObj;
            if (root.TryGetValue("entries", out entriesObj) && entriesObj is Dictionary<string, object>)
            {
                Dictionary<string, object> entriesDict = (Dictionary<string, object>)entriesObj;
                foreach (KeyValuePair<string, object> kv in entriesDict)
                {
                    Dictionary<string, object> item = kv.Value as Dictionary<string, object>;
                    if (item == null)
                    {
                        continue;
                    }
                    BackupEntry entry = new BackupEntry
                    {
                        Existed = GetBool(item, "existed", true),
                        OrigSha256 = GetString(item, "orig_sha256"),
                        PatchedSha256 = GetString(item, "patched_sha256"),
                        LiveOverridePath = GetString(item, "live_path")
                    };
                    manifest.Entries[kv.Key] = entry;
                }
            }

            return manifest;
        }

        public static void WriteManifest(string gameDir, BackupManifest manifest)
        {
            string backupRoot = GetBackupRoot(gameDir);
            if (!Directory.Exists(backupRoot))
            {
                Directory.CreateDirectory(backupRoot);
            }

            Dictionary<string, object> entriesJson = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, BackupEntry> kv in manifest.Entries)
            {
                Dictionary<string, object> item = new Dictionary<string, object>(StringComparer.Ordinal);
                item["existed"] = kv.Value.Existed;
                item["orig_sha256"] = kv.Value.OrigSha256;
                item["patched_sha256"] = kv.Value.PatchedSha256;
                if (!string.IsNullOrEmpty(kv.Value.LiveOverridePath))
                {
                    item["live_path"] = kv.Value.LiveOverridePath;
                }
                entriesJson[kv.Key] = item;
            }

            Dictionary<string, object> doc = new Dictionary<string, object>(StringComparer.Ordinal);
            doc["app_id"] = manifest.AppId;
            doc["schema_version"] = manifest.SchemaVersion;
            doc["achievements_ui"] = manifest.AchievementsUi;
            doc["updated_utc"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            doc["entries"] = entriesJson;

            string json = JsonMini.SerializePretty(doc);
            File.WriteAllText(GetManifestPath(gameDir), json, BinaryUtils.Utf8NoBom);
        }

        public static CleanSourceInfo PrepareCleanSource(
            string gameDir,
            BackupManifest manifest,
            string key,
            string livePath,
            Action<string> log)
        {
            if (!File.Exists(livePath))
            {
                throw new FileNotFoundException("Не найден обязательный файл игры: " + livePath, livePath);
            }

            string backupRoot = GetBackupRoot(gameDir);
            string backupPath = Path.Combine(backupRoot, key);
            string liveSha = BinaryUtils.ComputeFileSha256Hex(livePath);

            BackupEntry existing;
            if (manifest.Entries.TryGetValue(key, out existing) && File.Exists(backupPath))
            {
                string bakSha = BinaryUtils.ComputeFileSha256Hex(backupPath);
                if (!string.IsNullOrEmpty(existing.OrigSha256)
                    && !string.Equals(bakSha, existing.OrigSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "Нарушена целостность резервной копии '" + key + "' (SHA-256 не совпадает с манифестом).");
                }

                bool liveIsOurPatched = !string.IsNullOrEmpty(existing.PatchedSha256)
                    && string.Equals(liveSha, existing.PatchedSha256, StringComparison.OrdinalIgnoreCase);
                bool liveIsBackedUpOrig = string.Equals(liveSha, bakSha, StringComparison.OrdinalIgnoreCase);

                if (liveIsOurPatched || liveIsBackedUpOrig)
                {
                    return new CleanSourceInfo
                    {
                        Key = key,
                        LivePath = livePath,
                        BackupPath = backupPath,
                        OrigSha256 = bakSha
                    };
                }

                // Live file differs from both patched_sha256 and orig_sha256:
                // Steam updated the game to a newer version, so refresh backup from new official file.
                CopyAndVerify(livePath, backupPath, liveSha);
                if (log != null)
                {
                    log("  • Обнаружено обновление игры для " + key + ": резервная копия обновлена.");
                }
                return new CleanSourceInfo
                {
                    Key = key,
                    LivePath = livePath,
                    BackupPath = backupPath,
                    OrigSha256 = liveSha
                };
            }

            CopyAndVerify(livePath, backupPath, liveSha);
            return new CleanSourceInfo
            {
                Key = key,
                LivePath = livePath,
                BackupPath = backupPath,
                OrigSha256 = liveSha
            };
        }

        public static int RestoreOriginals(string gameDir, string explicitSteamStatsDir, Action<string> log)
        {
            string backupRoot = GetBackupRoot(gameDir);
            string manifestPath = GetManifestPath(gameDir);
            if (!File.Exists(manifestPath))
            {
                throw new InvalidOperationException(
                    "Резервная копия не найдена (" + BackupFolderName + "). В папке игры уже находятся оригинальные файлы.");
            }

            BackupManifest manifest = ReadManifestOrEmpty(gameDir);
            if (manifest.Entries.Count == 0)
            {
                throw new InvalidDataException("Манифест резервной копии пуст или повреждён.");
            }

            int restoredCount = 0;
            List<string> keys = new List<string>(manifest.Entries.Keys);
            keys.Sort(StringComparer.Ordinal);

            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                BackupEntry entry = manifest.Entries[key];
                bool isSteamSchema = key.StartsWith("steam_stats\\", StringComparison.OrdinalIgnoreCase);

                string livePath;
                if (isSteamSchema)
                {
                    livePath = ResolveSteamSchemaLivePath(explicitSteamStatsDir, entry.LiveOverridePath);
                    if (string.IsNullOrEmpty(livePath))
                    {
                        continue;
                    }
                }
                else
                {
                    livePath = Path.Combine(gameDir, key);
                }

                string backupPath = Path.Combine(backupRoot, key);
                if (!entry.Existed)
                {
                    if (File.Exists(livePath))
                    {
                        File.Delete(livePath);
                    }
                    continue;
                }

                if (!File.Exists(backupPath))
                {
                    if (isSteamSchema)
                    {
                        continue;
                    }
                    throw new FileNotFoundException("Не найден файл резервной копии: " + backupPath, backupPath);
                }

                string bakSha = BinaryUtils.ComputeFileSha256Hex(backupPath);
                if (!string.IsNullOrEmpty(entry.OrigSha256)
                    && !string.Equals(bakSha, entry.OrigSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Несовпадение SHA-256 резервной копии для '" + key + "'.");
                }

                if (!isSteamSchema && File.Exists(livePath) && !string.IsNullOrEmpty(entry.PatchedSha256))
                {
                    string currentLiveSha = BinaryUtils.ComputeFileSha256Hex(livePath);
                    bool matchesPatched = string.Equals(currentLiveSha, entry.PatchedSha256, StringComparison.OrdinalIgnoreCase);
                    bool matchesOrig = string.Equals(currentLiveSha, bakSha, StringComparison.OrdinalIgnoreCase);
                    if (!matchesPatched && !matchesOrig)
                    {
                        if (log != null)
                        {
                            log("  • Пропущен " + key + " (файл уже обновлён новой версией в Steam).");
                        }
                        continue;
                    }
                }

                if (isSteamSchema)
                {
                    try
                    {
                        CopyAndVerify(backupPath, livePath, bakSha);
                        restoredCount++;
                    }
                    catch (Exception ex)
                    {
                        if (log != null)
                        {
                            log("  • Примечание: не удалось откатить схему Steam (" + InstallerDiagnostics.FormatUserError(ex) + ").");
                        }
                    }
                }
                else
                {
                    CopyAndVerify(backupPath, livePath, bakSha);
                    restoredCount++;
                }
            }

            try
            {
                Directory.Delete(backupRoot, true);
            }
            catch
            {
                // Non-fatal if Explorer has folder handle open
            }

            AssertNoBakFiles(gameDir);
            return restoredCount;
        }

        public static void CopyAndVerify(string srcPath, string dstPath, string expectedSha256)
        {
            string dstDir = Path.GetDirectoryName(dstPath);
            if (!string.IsNullOrEmpty(dstDir) && !Directory.Exists(dstDir))
            {
                Directory.CreateDirectory(dstDir);
            }
            File.Copy(srcPath, dstPath, true);
            string actualSha = BinaryUtils.ComputeFileSha256Hex(dstPath);
            if (!string.Equals(actualSha, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Проверка SHA-256 не пройдена после записи файла: " + dstPath);
            }
        }

        public static void AssertNoBakFiles(string gameDir)
        {
            string[] baks = Directory.GetFiles(gameDir, "*.bak", SearchOption.AllDirectories);
            if (baks.Length > 0)
            {
                throw new InvalidOperationException("В папке игры обнаружен посторонний .bak файл: " + baks[0]);
            }
        }

        private static string ResolveSteamSchemaLivePath(string explicitStatsDir, string savedOverridePath)
        {
            if (!string.IsNullOrWhiteSpace(explicitStatsDir))
            {
                return Path.Combine(explicitStatsDir, SteamLocator.SchemaFileName);
            }
            if (!string.IsNullOrWhiteSpace(savedOverridePath))
            {
                return savedOverridePath;
            }
            string detected = SteamLocator.ResolveSteamStatsDirectory(null);
            if (!string.IsNullOrEmpty(detected))
            {
                return Path.Combine(detected, SteamLocator.SchemaFileName);
            }
            return null;
        }

        private static bool GetBool(Dictionary<string, object> dict, string key, bool defaultValue)
        {
            object v;
            if (dict.TryGetValue(key, out v) && v is bool)
            {
                return (bool)v;
            }
            return defaultValue;
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            object v;
            if (dict.TryGetValue(key, out v))
            {
                return v as string;
            }
            return null;
        }
    }
}
