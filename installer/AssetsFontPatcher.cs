using System;
using System.Collections.Generic;
using System.IO;

namespace WmaoRuInstaller
{
    internal static class AssetsFontPatcher
    {
        public static readonly KeyValuePair<string, string>[] FontSpecs = new KeyValuePair<string, string>[]
        {
            new KeyValuePair<string, string>("Sora-Regular", "Sora-Regular.ttf"),
            new KeyValuePair<string, string>("Anton-Regular", "Anton-Regular.ttf"),
            new KeyValuePair<string, string>("BigShouldersDisplay-ExtraBold", "BigShouldersDisplay-ExtraBold.ttf"),
            new KeyValuePair<string, string>("Sora-Bold", "Sora-Bold.ttf"),
            new KeyValuePair<string, string>("BigShouldersDisplay-Black", "BigShouldersDisplay-Black.ttf")
        };

        public static bool HasValidFontMagic(byte[] data, int offset, int length)
        {
            if (data == null || length < 12 || offset < 0 || offset + length > data.Length)
            {
                return false;
            }
            byte b0 = data[offset];
            byte b1 = data[offset + 1];
            byte b2 = data[offset + 2];
            byte b3 = data[offset + 3];

            bool isTrueType = (b0 == 0x00 && b1 == 0x01 && b2 == 0x00 && b3 == 0x00);
            bool isOpenType = (b0 == (byte)'O' && b1 == (byte)'T' && b2 == (byte)'T' && b3 == (byte)'O');
            bool isAppleTrue = (b0 == (byte)'t' && b1 == (byte)'r' && b2 == (byte)'u' && b3 == (byte)'e');
            return isTrueType || isOpenType || isAppleTrue;
        }

        public static Dictionary<string, byte[]> LoadFontPayloads(string fontsDir)
        {
            Dictionary<string, byte[]> payloads = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < FontSpecs.Length; i++)
            {
                string assetName = FontSpecs[i].Key;
                string fileName = FontSpecs[i].Value;
                string fullPath = Path.Combine(fontsDir, fileName);
                if (!File.Exists(fullPath))
                {
                    throw new FileNotFoundException("Отсутствует файл кириллического шрифта: " + fullPath, fullPath);
                }
                byte[] bytes = File.ReadAllBytes(fullPath);
                if (!HasValidFontMagic(bytes, 0, bytes.Length))
                {
                    throw new InvalidDataException("Повреждён заголовок шрифта TTF/OTF: " + fullPath);
                }
                payloads[assetName] = bytes;
            }
            return payloads;
        }

        public static int PatchResourcesFile(string origResourcesPath, string fontsDir, string outResourcesPath)
        {
            Dictionary<string, byte[]> payloads = LoadFontPayloads(fontsDir);
            byte[] rawAssets = File.ReadAllBytes(origResourcesPath);
            HashSet<string> matchedFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            byte[] patchedAssets = SerializedFileV23.Rebuild(rawAssets, delegate(long pathId, int classId, byte[] objBytes)
            {
                if (classId != SerializedFileV23.ClassIdFont)
                {
                    return objBytes;
                }
                return PatchFontObjectIfMatched(pathId, objBytes, payloads, matchedFonts);
            });

            if (matchedFonts.Count != FontSpecs.Length)
            {
                List<string> missing = new List<string>();
                for (int i = 0; i < FontSpecs.Length; i++)
                {
                    if (!matchedFonts.Contains(FontSpecs[i].Key))
                    {
                        missing.Add(FontSpecs[i].Key);
                    }
                }
                throw new InvalidDataException(
                    "В resources.assets не найдены ожидаемые шрифты: " + string.Join(", ", missing.ToArray()));
            }

            string outDir = Path.GetDirectoryName(outResourcesPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
            {
                Directory.CreateDirectory(outDir);
            }
            File.WriteAllBytes(outResourcesPath, patchedAssets);
            return matchedFonts.Count;
        }

        private static byte[] PatchFontObjectIfMatched(
            long pathId,
            byte[] objBytes,
            Dictionary<string, byte[]> payloads,
            HashSet<string> matchedFonts)
        {
            int pos = 0;
            string fontName = BinaryUtils.ReadAlignedUtf8(objBytes, ref pos);
            byte[] newTtf;
            if (!payloads.TryGetValue(fontName, out newTtf))
            {
                return objBytes;
            }

            // Fixed fields between m_Name and m_CharacterRects:
            // float m_LineSpacing (4) + PPtr m_DefaultMaterial (12) + float m_FontSize (4) +
            // PPtr m_Texture (12) + int m_AsciiStartOffset (4) + float m_Tracking (4) +
            // int m_CharacterSpacing (4) + int m_CharacterPadding (4) + int m_ConvertCase (4) = 52 bytes
            if (pos + 64 > objBytes.Length)
            {
                throw new InvalidDataException("Объект шрифта '" + fontName + "' (#" + pathId + ") повреждён.");
            }
            pos += 52;

            int charRectsCount = BinaryUtils.ReadInt32LE(objBytes, pos);
            pos += 4;
            if (charRectsCount != 0)
            {
                throw new InvalidDataException(
                    "Шрифт '" + fontName + "' (#" + pathId + ") содержит статические CharacterRects (" + charRectsCount + ").");
            }

            int kerningCount = BinaryUtils.ReadInt32LE(objBytes, pos);
            pos += 4;
            if (kerningCount < 0 || pos + kerningCount * 8 + 8 > objBytes.Length)
            {
                throw new InvalidDataException("Некорректная таблица кернинга в шрифте '" + fontName + "'.");
            }
            pos += kerningCount * 8;

            // float m_PixelScale (4 bytes)
            pos += 4;

            int fontDataSizeOffset = pos;
            int oldTtfSize = BinaryUtils.ReadInt32LE(objBytes, fontDataSizeOffset);
            int oldTtfOffset = fontDataSizeOffset + 4;
            if (!HasValidFontMagic(objBytes, oldTtfOffset, oldTtfSize))
            {
                throw new InvalidDataException(
                    "Не удалось подтвердить сигнатуру m_FontData в шрифте '" + fontName + "' (#" + pathId + ").");
            }

            int oldAlignedEnd = (oldTtfOffset + oldTtfSize + 3) & ~3;
            if (oldAlignedEnd > objBytes.Length)
            {
                throw new InvalidDataException("Некорректное выравнивание m_FontData в шрифте '" + fontName + "'.");
            }

            int suffixLen = objBytes.Length - oldAlignedEnd;
            int newPad = (4 - (newTtf.Length & 3)) & 3;
            int newTotalLen = fontDataSizeOffset + 4 + newTtf.Length + newPad + suffixLen;

            byte[] outObj = new byte[newTotalLen];
            Buffer.BlockCopy(objBytes, 0, outObj, 0, fontDataSizeOffset);
            BinaryUtils.PutInt32LE(outObj, fontDataSizeOffset, newTtf.Length);
            Buffer.BlockCopy(newTtf, 0, outObj, fontDataSizeOffset + 4, newTtf.Length);
            if (suffixLen > 0)
            {
                Buffer.BlockCopy(objBytes, oldAlignedEnd, outObj, fontDataSizeOffset + 4 + newTtf.Length + newPad, suffixLen);
            }

            matchedFonts.Add(fontName);
            return outObj;
        }
    }
}
