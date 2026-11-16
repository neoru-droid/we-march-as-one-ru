using System;
using System.Collections.Generic;
using System.IO;

namespace WmaoRuInstaller
{
    internal static class VdfSchemaPatcher
    {
        private const byte TagMap = 0x00;
        private const byte TagString = 0x01;
        private const byte TagInt32 = 0x02;
        private const byte TagFloat32 = 0x03;
        private const byte TagPtr = 0x04;
        private const byte TagWString = 0x05;
        private const byte TagColor = 0x06;
        private const byte TagUInt64 = 0x07;
        private const byte TagEnd = 0x08;
        private const byte TagInt64 = 0x0A;

        private sealed class VdfNode
        {
            public byte Tag;
            public string Key;
            public List<VdfNode> MapChildren;
            public string StringValue;
            public int IntValue;
            public byte[] RawBytes;
        }

        public static bool TryReadUserUnlockedCount(string statsFilePath, int totalCount, out int unlockedCount)
        {
            unlockedCount = 0;
            try
            {
                byte[] raw = File.ReadAllBytes(statsFilePath);
                if (raw == null || raw.Length == 0)
                {
                    return false;
                }
                int pos = 0;
                List<VdfNode> root = ParseMap(raw, ref pos);
                VdfNode cache = FindNodeCaseInsensitive(root, "cache", TagMap);
                if (cache == null && root.Count == 1 && root[0].Tag == TagMap)
                {
                    cache = root[0];
                }
                if (cache == null || cache.MapChildren == null)
                {
                    return false;
                }

                VdfNode group = FindStatsGroup(cache.MapChildren);
                if (group == null)
                {
                    return false;
                }
                VdfNode dataNode = FindNodeCaseInsensitive(group.MapChildren, "data", TagInt32);
                uint mask = dataNode != null ? unchecked((uint)dataNode.IntValue) : 0u;
                int limit = totalCount > 0 ? totalCount : 30;
                int count = 0;
                for (int b = 0; b < limit && b < 32; b++)
                {
                    if ((mask & (1u << b)) != 0u)
                    {
                        count++;
                    }
                }
                unlockedCount = count;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static VdfNode FindStatsGroup(List<VdfNode> children)
        {
            string[] preferred = new string[] { "0", "1" };
            for (int p = 0; p < preferred.Length; p++)
            {
                VdfNode cand = FindNodeCaseInsensitive(children, preferred[p], TagMap);
                if (cand != null && FindNodeCaseInsensitive(cand.MapChildren, "data", TagInt32) != null)
                {
                    return cand;
                }
            }
            for (int i = 0; i < children.Count; i++)
            {
                VdfNode child = children[i];
                if (child.Tag == TagMap && FindNodeCaseInsensitive(child.MapChildren, "AchievementTimes", TagMap) != null)
                {
                    return child;
                }
            }
            return null;
        }

        private static VdfNode FindNodeCaseInsensitive(List<VdfNode> entries, string key, byte tag)
        {
            if (entries == null)
            {
                return null;
            }
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Tag == tag && string.Equals(entries[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    return entries[i];
                }
            }
            return null;
        }

        public static int PatchSchemaFile(
            string inputBinPath,
            Dictionary<string, AchievementTranslation> translations,
            string outputBinPath)
        {
            byte[] raw = File.ReadAllBytes(inputBinPath);
            int patchedCount;
            byte[] patched = PatchSchemaBytes(raw, translations, out patchedCount);

            string dir = Path.GetDirectoryName(outputBinPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllBytes(outputBinPath, patched);
            return patchedCount;
        }

        public static byte[] PatchSchemaBytes(
            byte[] raw,
            Dictionary<string, AchievementTranslation> translations,
            out int patchedCount)
        {
            if (raw == null || raw.Length == 0)
            {
                throw new InvalidDataException("Пустой буфер схемы достижений VDF.");
            }

            int pos = 0;
            List<VdfNode> rootEntries = ParseMap(raw, ref pos);
            if (pos != raw.Length)
            {
                throw new InvalidDataException("Лишние байты после корня VDF на смещении " + pos);
            }
            if (rootEntries.Count != 1 || rootEntries[0].Tag != TagMap)
            {
                throw new InvalidDataException("Ожидался единственный корневой узел приложения в VDF.");
            }

            List<VdfNode> rootChildren = rootEntries[0].MapChildren;
            RequireNode(rootChildren, "version", TagInt32);
            List<VdfNode> stats = RequireNode(rootChildren, "stats", TagMap).MapChildren;
            List<VdfNode> bits = FindAchievementsBits(stats);

            patchedCount = 0;
            for (int i = 0; i < bits.Count; i++)
            {
                VdfNode bitNode = bits[i];
                if (bitNode.Tag != TagMap)
                {
                    continue;
                }
                VdfNode apiNode = FindNode(bitNode.MapChildren, "name", TagString);
                if (apiNode == null)
                {
                    continue;
                }

                AchievementTranslation tr;
                if (!translations.TryGetValue(apiNode.StringValue, out tr))
                {
                    continue;
                }

                List<VdfNode> display = RequireNode(bitNode.MapChildren, "display", TagMap).MapChildren;
                List<VdfNode> nameMap = RequireNode(display, "name", TagMap).MapChildren;
                List<VdfNode> descMap = RequireNode(display, "desc", TagMap).MapChildren;

                UpsertString(nameMap, "english", tr.NameRu);
                UpsertString(nameMap, "russian", tr.NameRu);
                UpsertString(descMap, "english", tr.DescRu);
                UpsertString(descMap, "russian", tr.DescRu);
                patchedCount++;
            }

            return SerializeVdf(rootEntries);
        }

        private static List<VdfNode> ParseMap(byte[] buf, ref int pos)
        {
            List<VdfNode> list = new List<VdfNode>();
            while (pos < buf.Length)
            {
                int tagOffset = pos;
                byte tag = buf[pos++];
                if (tag == TagEnd)
                {
                    return list;
                }
                string key = BinaryUtils.ReadCStringUtf8(buf, ref pos);
                VdfNode node = new VdfNode { Tag = tag, Key = key };
                if (tag == TagMap)
                {
                    node.MapChildren = ParseMap(buf, ref pos);
                }
                else if (tag == TagString)
                {
                    node.StringValue = BinaryUtils.ReadCStringUtf8(buf, ref pos);
                }
                else if (tag == TagInt32)
                {
                    if (pos + 4 > buf.Length)
                    {
                        throw new InvalidDataException("Обрыв int32 в VDF на смещении " + pos);
                    }
                    node.IntValue = BinaryUtils.ReadInt32LE(buf, pos);
                    pos += 4;
                }
                else if (tag == TagFloat32 || tag == TagPtr || tag == TagColor)
                {
                    if (pos + 4 > buf.Length)
                    {
                        throw new InvalidDataException("Обрыв 4-байтового поля VDF на смещении " + pos);
                    }
                    node.RawBytes = new byte[4];
                    Buffer.BlockCopy(buf, pos, node.RawBytes, 0, 4);
                    pos += 4;
                }
                else if (tag == TagUInt64 || tag == TagInt64)
                {
                    if (pos + 8 > buf.Length)
                    {
                        throw new InvalidDataException("Обрыв 8-байтового поля VDF на смещении " + pos);
                    }
                    node.RawBytes = new byte[8];
                    Buffer.BlockCopy(buf, pos, node.RawBytes, 0, 8);
                    pos += 8;
                }
                else if (tag == TagWString)
                {
                    int start = pos;
                    while (pos + 1 < buf.Length && (buf[pos] != 0 || buf[pos + 1] != 0))
                    {
                        pos += 2;
                    }
                    if (pos + 1 >= buf.Length)
                    {
                        throw new InvalidDataException("Незакрытая UTF-16 строка в VDF.");
                    }
                    node.RawBytes = new byte[pos - start];
                    Buffer.BlockCopy(buf, start, node.RawBytes, 0, pos - start);
                    pos += 2;
                }
                else
                {
                    throw new InvalidDataException("Неизвестный тег VDF 0x" + tag.ToString("x2") + " на смещении " + tagOffset);
                }
                list.Add(node);
            }
            throw new InvalidDataException("Незакрытая карта VDF до конца файла.");
        }

        private static byte[] SerializeVdf(List<VdfNode> rootEntries)
        {
            using (MemoryStream ms = new MemoryStream(12288))
            {
                WriteMap(ms, rootEntries);
                return ms.ToArray();
            }
        }

        private static void WriteMap(Stream ms, List<VdfNode> entries)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                VdfNode entry = entries[i];
                ms.WriteByte(entry.Tag);
                BinaryUtils.WriteCStringUtf8(ms, entry.Key);
                if (entry.Tag == TagMap)
                {
                    WriteMap(ms, entry.MapChildren);
                }
                else if (entry.Tag == TagString)
                {
                    BinaryUtils.WriteCStringUtf8(ms, entry.StringValue);
                }
                else if (entry.Tag == TagInt32)
                {
                    BinaryUtils.WriteInt32LE(ms, entry.IntValue);
                }
                else if (entry.Tag == TagFloat32 || entry.Tag == TagPtr || entry.Tag == TagColor)
                {
                    ms.Write(entry.RawBytes, 0, 4);
                }
                else if (entry.Tag == TagUInt64 || entry.Tag == TagInt64)
                {
                    ms.Write(entry.RawBytes, 0, 8);
                }
                else if (entry.Tag == TagWString)
                {
                    ms.Write(entry.RawBytes, 0, entry.RawBytes.Length);
                    ms.WriteByte(0);
                    ms.WriteByte(0);
                }
                else
                {
                    throw new InvalidOperationException("Неподдерживаемый тег VDF: " + entry.Tag);
                }
            }
            ms.WriteByte(TagEnd);
        }

        private static VdfNode FindNode(List<VdfNode> entries, string key, byte tag)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].Tag == tag && entries[i].Key == key)
                {
                    return entries[i];
                }
            }
            return null;
        }

        private static VdfNode RequireNode(List<VdfNode> entries, string key, byte tag)
        {
            VdfNode found = FindNode(entries, key, tag);
            if (found == null)
            {
                throw new InvalidDataException("В схеме VDF отсутствует обязательное поле '" + key + "'.");
            }
            return found;
        }

        private static List<VdfNode> FindAchievementsBits(List<VdfNode> stats)
        {
            for (int i = 0; i < stats.Count; i++)
            {
                if (stats[i].Tag == TagMap)
                {
                    VdfNode typeNode = FindNode(stats[i].MapChildren, "type", TagString);
                    if (typeNode != null && typeNode.StringValue == "ACHIEVEMENTS")
                    {
                        return RequireNode(stats[i].MapChildren, "bits", TagMap).MapChildren;
                    }
                }
            }
            throw new InvalidDataException("В схеме VDF не найден блок ACHIEVEMENTS.");
        }

        private static void UpsertString(List<VdfNode> entries, string key, string value)
        {
            VdfNode existing = FindNode(entries, key, TagString);
            if (existing != null)
            {
                existing.StringValue = value;
            }
            else
            {
                entries.Add(new VdfNode { Tag = TagString, Key = key, StringValue = value });
            }
        }
    }
}
