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
        private const byte TagEnd = 0x08;

        private sealed class VdfNode
        {
            public byte Tag;
            public string Key;
            public List<VdfNode> MapChildren;
            public string StringValue;
            public int IntValue;
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
