using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WmaoRuInstaller
{
    internal sealed class InstallerPayload
    {
        public string Version = "1.2.0";
        public int AppId = 5189160;
        public bool IsEmbedded;
        public TranslationPayload Translations;
        public Dictionary<string, byte[]> FontFiles =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> ToolFiles =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    }

    internal static class PayloadProvider
    {
        public const string EmbeddedResourceName = "wmao_ru_payload.gz";

        private static readonly string[] RequiredToolNames = new string[]
        {
            "Patcher.exe",
            "RuModRuntime.dll",
            "Mono.Cecil.dll",
            "Mono.Cecil.Rocks.dll"
        };

        private static InstallerPayload _cachedEmbedded;
        private static readonly object CacheLock = new object();

        public static bool HasEmbeddedPayload()
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            if (asm.GetManifestResourceInfo(EmbeddedResourceName) != null)
            {
                return true;
            }
            string[] names = asm.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].EndsWith(EmbeddedResourceName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static InstallerPayload LoadPayload(string appBaseDir, bool skipAssemblyPatch)
        {
            if (HasEmbeddedPayload())
            {
                return LoadEmbeddedCached();
            }
            return LoadFromDisk(appBaseDir, skipAssemblyPatch);
        }

        public static InstallerPayload LoadEmbeddedCached()
        {
            lock (CacheLock)
            {
                if (_cachedEmbedded != null)
                {
                    return _cachedEmbedded;
                }

                Assembly asm = Assembly.GetExecutingAssembly();
                Stream resStream = asm.GetManifestResourceStream(EmbeddedResourceName);
                if (resStream == null)
                {
                    string[] names = asm.GetManifestResourceNames();
                    for (int i = 0; i < names.Length; i++)
                    {
                        if (names[i].EndsWith(EmbeddedResourceName, StringComparison.OrdinalIgnoreCase))
                        {
                            resStream = asm.GetManifestResourceStream(names[i]);
                            break;
                        }
                    }
                }
                if (resStream == null)
                {
                    throw new InvalidDataException("Встроенный ресурс wmao_ru_payload.gz не найден.");
                }

                string jsonText;
                using (resStream)
                using (GZipStream gz = new GZipStream(resStream, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gz, BinaryUtils.Utf8NoBom))
                {
                    jsonText = reader.ReadToEnd();
                }

                Dictionary<string, object> root = JsonMini.ParseObject(jsonText);
                InstallerPayload payload = new InstallerPayload();
                payload.IsEmbedded = true;

                object verObj;
                if (root.TryGetValue("version", out verObj) && verObj is string)
                {
                    payload.Version = (string)verObj;
                }

                payload.Translations = TranslationPayload.LoadFromRoot(root);
                DecodeBase64Map(root, "fonts", payload.FontFiles);
                DecodeBase64Map(root, "tools", payload.ToolFiles);

                _cachedEmbedded = payload;
                return _cachedEmbedded;
            }
        }

        private static InstallerPayload LoadFromDisk(string appBaseDir, bool skipAssemblyPatch)
        {
            InstallerDiagnostics.EnsurePayloadComplete(appBaseDir, skipAssemblyPatch);

            string fontsDir = Path.Combine(appBaseDir, @"data\fonts");
            string translationsPath = Path.Combine(appBaseDir, @"data\translations_ru.json");
            string toolsDir = Path.Combine(appBaseDir, "tools");

            InstallerPayload payload = new InstallerPayload();
            payload.IsEmbedded = false;
            payload.Translations = TranslationPayload.LoadFromFile(translationsPath);

            for (int i = 0; i < AssetsFontPatcher.FontSpecs.Length; i++)
            {
                string fileName = AssetsFontPatcher.FontSpecs[i].Value;
                string fullPath = Path.Combine(fontsDir, fileName);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("Отсутствует файл шрифта: " + fullPath, fullPath);
                }
                payload.FontFiles[fileName] = File.ReadAllBytes(fullPath);
            }

            if (!skipAssemblyPatch)
            {
                for (int i = 0; i < RequiredToolNames.Length; i++)
                {
                    string tName = RequiredToolNames[i];
                    string tPath = Path.Combine(toolsDir, tName);
                    if (!File.Exists(tPath))
                    {
                        throw new FileNotFoundException("Не найден инструмент установщика: " + tPath, tPath);
                    }
                    payload.ToolFiles[tName] = File.ReadAllBytes(tPath);
                }
            }

            return payload;
        }

        private static void DecodeBase64Map(
            Dictionary<string, object> root,
            string key,
            Dictionary<string, byte[]> target)
        {
            object rawObj;
            if (!root.TryGetValue(key, out rawObj) || !(rawObj is Dictionary<string, object>))
            {
                return;
            }
            Dictionary<string, object> map = (Dictionary<string, object>)rawObj;
            foreach (KeyValuePair<string, object> kv in map)
            {
                string b64 = kv.Value as string;
                if (!string.IsNullOrEmpty(b64))
                {
                    target[kv.Key] = Convert.FromBase64String(b64);
                }
            }
        }
    }

    internal static class SteamUserStatsReader
    {
        private static readonly Regex StatsFileRegex = new Regex(
            @"^UserGameStats_(\d+)_5189160\.bin$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static int ReadUnlockedCount(string statsDir, int totalCount)
        {
            if (string.IsNullOrEmpty(statsDir) || !SteamLocator.SafeDirectoryExists(statsDir))
            {
                return 0;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(statsDir, "UserGameStats_*_5189160.bin", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return 0;
            }

            Array.Sort(files, delegate(string a, string b)
            {
                return SafeLastWriteUtc(b).CompareTo(SafeLastWriteUtc(a));
            });

            int limit = totalCount > 0 ? totalCount : 30;
            for (int i = 0; i < files.Length; i++)
            {
                string fname = Path.GetFileName(files[i]);
                if (!StatsFileRegex.IsMatch(fname))
                {
                    continue;
                }
                int unlocked;
                if (VdfSchemaPatcher.TryReadUserUnlockedCount(files[i], limit, out unlocked))
                {
                    return unlocked;
                }
            }
            return 0;
        }

        private static DateTime SafeLastWriteUtc(string path)
        {
            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
