using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WmaoRuInstaller
{
    internal sealed class BundlePatchResult
    {
        public int UpdatedRows;
        public uint BundleCrc;
        public int BundleSize;
    }

    internal static class BundleStringPatcher
    {
        public const string EnglishBundleFileName = "localization-string-tables-english_assets_all.bundle";
        private const uint CatalogMagic = 0x0DE38942U;
        private const long MenusLockedEntryId = 1282142286594048L;
        private const int Lz4ChunkSize = 0x20000; // 128 KB

        private sealed class FsBlock
        {
            public uint UncompressedSize;
            public uint CompressedSize;
            public ushort Flags;
        }

        private sealed class FsNode
        {
            public long Offset;
            public long Size;
            public uint Flags;
            public string Path;
            public byte[] Data;
        }

        public static BundlePatchResult PatchBundleAndCatalog(
            string origBundlePath,
            string origCatalogPath,
            Dictionary<string, Dictionary<string, string>> mergedTables,
            string outBundlePath,
            string outCatalogPath)
        {
            byte[] rawBundle = File.ReadAllBytes(origBundlePath);
            byte[] rawCatalog = File.ReadAllBytes(origCatalogPath);

            int updatedRows;
            uint newCrc;
            byte[] newBundleBytes = PatchUnityFsBundleBytes(rawBundle, mergedTables, out updatedRows, out newCrc);
            int newBundleSize = newBundleBytes.Length;

            byte[] newCatalogBytes = PatchCatalogBundleEntry(rawCatalog, EnglishBundleFileName, newCrc, newBundleSize);

            EnsureParentDirectory(outBundlePath);
            EnsureParentDirectory(outCatalogPath);
            File.WriteAllBytes(outBundlePath, newBundleBytes);
            File.WriteAllBytes(outCatalogPath, newCatalogBytes);

            return new BundlePatchResult
            {
                UpdatedRows = updatedRows,
                BundleCrc = newCrc,
                BundleSize = newBundleSize
            };
        }

        public static byte[] PatchUnityFsBundleBytes(
            byte[] rawBundle,
            Dictionary<string, Dictionary<string, string>> mergedTables,
            out int totalUpdatedRows,
            out uint uncompressedCrc)
        {
            if (rawBundle == null || rawBundle.Length < 32)
            {
                throw new InvalidDataException("Файл AssetBundle слишком мал.");
            }

            int pos = 0;
            string signature = BinaryUtils.ReadCStringUtf8(rawBundle, ref pos);
            if (signature != "UnityFS")
            {
                throw new InvalidDataException("Ожидалась сигнатура UnityFS, получена: " + signature);
            }

            uint formatVersion = BinaryUtils.ReadUInt32BE(rawBundle, pos);
            pos += 4;
            string playerVersion = BinaryUtils.ReadCStringUtf8(rawBundle, ref pos);
            string engineVersion = BinaryUtils.ReadCStringUtf8(rawBundle, ref pos);

            long bundleSize = BinaryUtils.ReadInt64BE(rawBundle, pos);
            pos += 8;
            uint compBiSize = BinaryUtils.ReadUInt32BE(rawBundle, pos);
            pos += 4;
            uint uncompBiSize = BinaryUtils.ReadUInt32BE(rawBundle, pos);
            pos += 4;
            uint archiveFlags = BinaryUtils.ReadUInt32BE(rawBundle, pos);
            pos += 4;

            if (bundleSize != rawBundle.Length)
            {
                throw new InvalidDataException("Размер UnityFS в заголовке не совпадает с фактическим размером файла.");
            }

            if (formatVersion >= 7U)
            {
                pos = (pos + 15) & ~15;
            }

            byte[] compBiBytes = new byte[(int)compBiSize];
            if ((archiveFlags & 0x80U) != 0)
            {
                Buffer.BlockCopy(rawBundle, rawBundle.Length - (int)compBiSize, compBiBytes, 0, (int)compBiSize);
            }
            else
            {
                Buffer.BlockCopy(rawBundle, pos, compBiBytes, 0, (int)compBiSize);
                pos += (int)compBiSize;
            }

            byte[] biBytes = DecompressByMode(compBiBytes, 0, compBiBytes.Length, (int)uncompBiSize, (int)(archiveFlags & 0x3FU));
            int biPos = 16; // skip 16-byte uncompressedDataHash
            int blocksCount = BinaryUtils.ReadInt32BE(biBytes, biPos);
            biPos += 4;

            List<FsBlock> blocks = new List<FsBlock>(blocksCount);
            long totalUncompStreamLen = 0;
            for (int i = 0; i < blocksCount; i++)
            {
                FsBlock blk = new FsBlock
                {
                    UncompressedSize = BinaryUtils.ReadUInt32BE(biBytes, biPos),
                    CompressedSize = BinaryUtils.ReadUInt32BE(biBytes, biPos + 4),
                    Flags = BinaryUtils.ReadUInt16BE(biBytes, biPos + 8)
                };
                biPos += 10;
                totalUncompStreamLen += blk.UncompressedSize;
                blocks.Add(blk);
            }

            int nodesCount = BinaryUtils.ReadInt32BE(biBytes, biPos);
            biPos += 4;
            List<FsNode> nodes = new List<FsNode>(nodesCount);
            for (int i = 0; i < nodesCount; i++)
            {
                long nOff = BinaryUtils.ReadInt64BE(biBytes, biPos);
                long nSize = BinaryUtils.ReadInt64BE(biBytes, biPos + 8);
                uint nFlags = BinaryUtils.ReadUInt32BE(biBytes, biPos + 16);
                biPos += 20;
                string nPath = BinaryUtils.ReadCStringUtf8(biBytes, ref biPos);
                nodes.Add(new FsNode { Offset = nOff, Size = nSize, Flags = nFlags, Path = nPath });
            }

            if ((archiveFlags & 0x200U) != 0)
            {
                pos = (pos + 15) & ~15;
            }

            byte[] uncompStream = new byte[(int)totalUncompStreamLen];
            int writeOff = 0;
            for (int i = 0; i < blocks.Count; i++)
            {
                FsBlock blk = blocks[i];
                byte[] dec = DecompressByMode(rawBundle, pos, (int)blk.CompressedSize, (int)blk.UncompressedSize, blk.Flags & 0x3F);
                pos += (int)blk.CompressedSize;
                Buffer.BlockCopy(dec, 0, uncompStream, writeOff, dec.Length);
                writeOff += dec.Length;
            }

            int updatedCounter = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                FsNode node = nodes[i];
                byte[] nodeBytes = new byte[(int)node.Size];
                Buffer.BlockCopy(uncompStream, (int)node.Offset, nodeBytes, 0, (int)node.Size);

                if ((node.Flags & 4U) != 0)
                {
                    nodeBytes = SerializedFileV23.Rebuild(nodeBytes, delegate(long pathId, int classId, byte[] objBytes)
                    {
                        if (classId != SerializedFileV23.ClassIdMonoBehaviour)
                        {
                            return objBytes;
                        }
                        int rowsChanged;
                        byte[] patchedObj = PatchStringTableMonoBehaviour(objBytes, mergedTables, out rowsChanged);
                        updatedCounter += rowsChanged;
                        return patchedObj;
                    });
                }
                node.Data = nodeBytes;
            }

            if (updatedCounter == 0)
            {
                throw new InvalidDataException("В бандле локализации не найдено ни одной строки StringTable для обновления.");
            }

            totalUpdatedRows = updatedCounter;
            return PackUnityFsBundle(formatVersion, playerVersion, engineVersion, nodes, out uncompressedCrc);
        }

        private static byte[] PatchStringTableMonoBehaviour(
            byte[] raw,
            Dictionary<string, Dictionary<string, string>> mergedTables,
            out int rowsUpdated)
        {
            rowsUpdated = 0;
            // PPtr<GameObject> m_GameObject (12) + uint8 m_Enabled aligned (4) + PPtr<MonoScript> m_Script (12) = 28 bytes
            if (raw.Length < 40)
            {
                return raw;
            }

            int pos = 28;
            string objName = BinaryUtils.ReadAlignedUtf8(raw, ref pos);
            if (!objName.EndsWith("_en", StringComparison.Ordinal))
            {
                return raw;
            }

            string collectionName = objName.Substring(0, objName.Length - 3);
            Dictionary<string, string> translations;
            if (!mergedTables.TryGetValue(collectionName, out translations))
            {
                return raw;
            }

            // LocaleIdentifier m_LocaleId (aligned string m_Code)
            BinaryUtils.ReadAlignedUtf8(raw, ref pos);

            // PPtr<SharedTableData> m_SharedData (12 bytes)
            pos += 12;

            // MetadataCollection m_Metadata (int32 count + count * int64 rid)
            int tableMetaCount = BinaryUtils.ReadInt32LE(raw, pos);
            pos += 4 + tableMetaCount * 8;

            int tableDataOffset = pos;
            int rowCount = BinaryUtils.ReadInt32LE(raw, pos);
            pos += 4;

            if (rowCount == 0 && collectionName != "Menus")
            {
                return raw;
            }

            List<long> rowIds = new List<long>(rowCount + 1);
            List<string> rowStrings = new List<string>(rowCount + 1);
            List<byte[]> rowMetaBlobs = new List<byte[]>(rowCount + 1);
            HashSet<long> existingIds = new HashSet<long>();

            for (int i = 0; i < rowCount; i++)
            {
                long entryId = BinaryUtils.ReadInt64LE(raw, pos);
                pos += 8;
                string origLocalized = BinaryUtils.ReadAlignedUtf8(raw, ref pos);
                int rowMetaCount = BinaryUtils.ReadInt32LE(raw, pos);
                int metaBlobLen = 4 + rowMetaCount * 8;
                byte[] metaBlob = new byte[metaBlobLen];
                Buffer.BlockCopy(raw, pos, metaBlob, 0, metaBlobLen);
                pos += metaBlobLen;

                string idKey = entryId.ToString(CultureInfo.InvariantCulture);
                string newLocalized;
                if (translations.TryGetValue(idKey, out newLocalized))
                {
                    rowsUpdated++;
                }
                else
                {
                    newLocalized = origLocalized;
                }

                rowIds.Add(entryId);
                rowStrings.Add(newLocalized);
                rowMetaBlobs.Add(metaBlob);
                existingIds.Add(entryId);
            }

            string lockedKey = MenusLockedEntryId.ToString(CultureInfo.InvariantCulture);
            string lockedTranslation;
            if (collectionName == "Menus"
                && !existingIds.Contains(MenusLockedEntryId)
                && translations.TryGetValue(lockedKey, out lockedTranslation))
            {
                rowIds.Add(MenusLockedEntryId);
                rowStrings.Add(lockedTranslation);
                rowMetaBlobs.Add(new byte[4]); // m_Items.size = 0
                rowsUpdated++;
            }

            int suffixOffset = pos;
            int suffixLen = raw.Length - suffixOffset;

            using (MemoryStream ms = new MemoryStream(raw.Length + 4096))
            {
                ms.Write(raw, 0, tableDataOffset);
                BinaryUtils.WriteInt32LE(ms, rowIds.Count);
                for (int i = 0; i < rowIds.Count; i++)
                {
                    BinaryUtils.WriteInt64LE(ms, rowIds[i]);
                    BinaryUtils.WriteAlignedUtf8(ms, rowStrings[i]);
                    byte[] metaBlob = rowMetaBlobs[i];
                    ms.Write(metaBlob, 0, metaBlob.Length);
                }
                if (suffixLen > 0)
                {
                    ms.Write(raw, suffixOffset, suffixLen);
                }
                return ms.ToArray();
            }
        }

        private static byte[] PackUnityFsBundle(
            uint formatVersion,
            string playerVersion,
            string engineVersion,
            List<FsNode> nodes,
            out uint uncompressedCrc)
        {
            long totalLen = 0;
            for (int i = 0; i < nodes.Count; i++)
            {
                nodes[i].Offset = totalLen;
                nodes[i].Size = nodes[i].Data.Length;
                totalLen += nodes[i].Data.Length;
            }

            byte[] uncompStream = new byte[(int)totalLen];
            for (int i = 0; i < nodes.Count; i++)
            {
                Buffer.BlockCopy(nodes[i].Data, 0, uncompStream, (int)nodes[i].Offset, nodes[i].Data.Length);
            }

            uncompressedCrc = BinaryUtils.ComputeCrc32(uncompStream);

            List<FsBlock> outBlocks = new List<FsBlock>();
            List<byte[]> compressedChunks = new List<byte[]>();

            int offset = 0;
            while (offset < uncompStream.Length)
            {
                int chunkLen = Math.Min(Lz4ChunkSize, uncompStream.Length - offset);
                byte[] lz4Chunk = Lz4Codec.CompressBlock(uncompStream, offset, chunkLen);
                if (lz4Chunk.Length < chunkLen)
                {
                    outBlocks.Add(new FsBlock
                    {
                        UncompressedSize = (uint)chunkLen,
                        CompressedSize = (uint)lz4Chunk.Length,
                        Flags = 0x0002 // LZ4
                    });
                    compressedChunks.Add(lz4Chunk);
                }
                else
                {
                    byte[] rawChunk = new byte[chunkLen];
                    Buffer.BlockCopy(uncompStream, offset, rawChunk, 0, chunkLen);
                    outBlocks.Add(new FsBlock
                    {
                        UncompressedSize = (uint)chunkLen,
                        CompressedSize = (uint)chunkLen,
                        Flags = 0x0000 // None
                    });
                    compressedChunks.Add(rawChunk);
                }
                offset += chunkLen;
            }

            byte[] rawBlocksInfo;
            using (MemoryStream biStream = new MemoryStream(256))
            {
                byte[] zeroHash = new byte[16];
                biStream.Write(zeroHash, 0, 16);
                BinaryUtils.WriteInt32BE(biStream, outBlocks.Count);
                for (int i = 0; i < outBlocks.Count; i++)
                {
                    BinaryUtils.WriteUInt32BE(biStream, outBlocks[i].UncompressedSize);
                    BinaryUtils.WriteUInt32BE(biStream, outBlocks[i].CompressedSize);
                    BinaryUtils.WriteUInt16BE(biStream, outBlocks[i].Flags);
                }

                BinaryUtils.WriteInt32BE(biStream, nodes.Count);
                for (int i = 0; i < nodes.Count; i++)
                {
                    BinaryUtils.WriteInt64BE(biStream, nodes[i].Offset);
                    BinaryUtils.WriteInt64BE(biStream, nodes[i].Size);
                    BinaryUtils.WriteUInt32BE(biStream, nodes[i].Flags);
                    BinaryUtils.WriteCStringUtf8(biStream, nodes[i].Path);
                }
                rawBlocksInfo = biStream.ToArray();
            }

            byte[] compBlocksInfo = Lz4Codec.CompressBlock(rawBlocksInfo, 0, rawBlocksInfo.Length);
            uint archiveFlags;
            byte[] finalBlocksInfo;
            if (compBlocksInfo.Length < rawBlocksInfo.Length)
            {
                // 0x200 (BlockInfoNeedPaddingAtStart) | 0x40 (BlocksAndDirectoryInfoCombined) | 0x02 (LZ4)
                archiveFlags = 0x0242U;
                finalBlocksInfo = compBlocksInfo;
            }
            else
            {
                archiveFlags = 0x0240U;
                finalBlocksInfo = rawBlocksInfo;
            }

            using (MemoryStream bundleOut = new MemoryStream(uncompStream.Length + 512))
            {
                BinaryUtils.WriteCStringUtf8(bundleOut, "UnityFS");
                BinaryUtils.WriteUInt32BE(bundleOut, formatVersion);
                BinaryUtils.WriteCStringUtf8(bundleOut, playerVersion);
                BinaryUtils.WriteCStringUtf8(bundleOut, engineVersion);

                int sizeFieldPos = (int)bundleOut.Position;
                BinaryUtils.WriteInt64BE(bundleOut, 0L); // patched at end
                BinaryUtils.WriteUInt32BE(bundleOut, (uint)finalBlocksInfo.Length);
                BinaryUtils.WriteUInt32BE(bundleOut, (uint)rawBlocksInfo.Length);
                BinaryUtils.WriteUInt32BE(bundleOut, archiveFlags);

                if (formatVersion >= 7U)
                {
                    BinaryUtils.AlignStream(bundleOut, 16);
                }

                bundleOut.Write(finalBlocksInfo, 0, finalBlocksInfo.Length);
                if ((archiveFlags & 0x200U) != 0)
                {
                    BinaryUtils.AlignStream(bundleOut, 16);
                }

                for (int i = 0; i < compressedChunks.Count; i++)
                {
                    byte[] chunk = compressedChunks[i];
                    bundleOut.Write(chunk, 0, chunk.Length);
                }

                byte[] result = bundleOut.ToArray();
                BinaryUtils.PutInt64BE(result, sizeFieldPos, result.Length);
                return result;
            }
        }

        private static byte[] DecompressByMode(byte[] src, int offset, int compLen, int uncompLen, int mode)
        {
            if (mode == 0)
            {
                if (compLen != uncompLen)
                {
                    throw new InvalidDataException("Несовпадение размера несжатого блока UnityFS.");
                }
                byte[] copy = new byte[uncompLen];
                Buffer.BlockCopy(src, offset, copy, 0, uncompLen);
                return copy;
            }
            if (mode == 2 || mode == 3)
            {
                return Lz4Codec.DecompressBlock(src, offset, compLen, uncompLen);
            }
            throw new NotSupportedException("Неподдерживаемый режим сжатия UnityFS: " + mode);
        }

        public static int LocateCatalogCrcOffset(byte[] catalogBytes, string bundleFileName)
        {
            if (catalogBytes == null || catalogBytes.Length < 16 || BinaryUtils.ReadUInt32LE(catalogBytes, 0) != CatalogMagic)
            {
                throw new InvalidDataException("Некорректный заголовок Addressables catalog.bin.");
            }

            string fullIdStr = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}\\StandaloneWindows64\\" + bundleFileName;
            byte[] fullIdBytes = BinaryUtils.Utf8NoBom.GetBytes(fullIdStr);
            byte[] prefixed = new byte[4 + fullIdBytes.Length];
            BinaryUtils.PutInt32LE(prefixed, 0, fullIdBytes.Length);
            Buffer.BlockCopy(fullIdBytes, 0, prefixed, 4, fullIdBytes.Length);

            int pos = FindPattern(catalogBytes, prefixed, 0);
            if (pos < 0)
            {
                throw new InvalidDataException("Бандл '" + bundleFileName + "' не найден в catalog.bin.");
            }

            int internalIdOff = pos + 4;
            int hashOff = internalIdOff + fullIdBytes.Length;

            byte[] rlPat = new byte[4];
            BinaryUtils.PutInt32LE(rlPat, 0, internalIdOff);

            int searchFrom = hashOff;
            while (true)
            {
                int rlPos = FindPattern(catalogBytes, rlPat, searchFrom);
                if (rlPos < 0 || rlPos + 20 > catalogBytes.Length)
                {
                    break;
                }
                uint dataOff = BinaryUtils.ReadUInt32LE(catalogBytes, rlPos + 16);
                if (dataOff + 8U <= (uint)catalogBytes.Length)
                {
                    uint payloadOff = BinaryUtils.ReadUInt32LE(catalogBytes, (int)dataOff + 4);
                    if (payloadOff + 16U <= (uint)catalogBytes.Length
                        && BinaryUtils.ReadUInt32LE(catalogBytes, (int)payloadOff) == (uint)hashOff)
                    {
                        return (int)payloadOff + 8;
                    }
                }
                searchFrom = rlPos + 1;
            }

            throw new InvalidDataException("Не удалось найти структуру AssetBundleRequestOptions в catalog.bin.");
        }

        public static byte[] PatchCatalogBundleEntry(byte[] catalogBytes, string bundleFileName, uint newCrc, int newSize)
        {
            if (newSize <= 0)
            {
                throw new ArgumentOutOfRangeException("newSize", "Размер бандла должен быть положительным.");
            }
            int crcOffset = LocateCatalogCrcOffset(catalogBytes, bundleFileName);
            byte[] updated = new byte[catalogBytes.Length];
            Buffer.BlockCopy(catalogBytes, 0, updated, 0, catalogBytes.Length);
            BinaryUtils.PutUInt32LE(updated, crcOffset, newCrc);
            BinaryUtils.PutInt32LE(updated, crcOffset + 4, newSize);
            return updated;
        }

        private static int FindPattern(byte[] haystack, byte[] needle, int startIndex)
        {
            int limit = haystack.Length - needle.Length;
            if (needle.Length == 0 || startIndex > limit)
            {
                return -1;
            }
            byte first = needle[0];
            for (int i = startIndex; i <= limit; i++)
            {
                if (haystack[i] != first)
                {
                    continue;
                }
                bool match = true;
                for (int j = 1; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i;
                }
            }
            return -1;
        }

        private static void EnsureParentDirectory(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }
    }
}
