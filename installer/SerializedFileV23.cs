using System;
using System.IO;

namespace WmaoRuInstaller
{
    internal delegate byte[] SerializedObjectMutator(long pathId, int classId, byte[] objectBytes);

    internal static class SerializedFileV23
    {
        public const int ClassIdMonoBehaviour = 114;
        public const int ClassIdFont = 128;

        public static byte[] Rebuild(byte[] rawFile, SerializedObjectMutator mutator)
        {
            if (rawFile == null || rawFile.Length < 48)
            {
                throw new InvalidDataException("Unity SerializedFile слишком мал для заголовка v23.");
            }

            uint version = BinaryUtils.ReadUInt32BE(rawFile, 8);
            if (version != 23U)
            {
                throw new InvalidDataException("Ожидался Unity SerializedFile версии 23, получен: " + version);
            }

            byte endianFlag = rawFile[16];
            if (endianFlag != 0)
            {
                throw new InvalidDataException("Поддерживается только little-endian SerializedFile (endian=0).");
            }

            uint metadataSize = BinaryUtils.ReadUInt32BE(rawFile, 20);
            long origFileSize = BinaryUtils.ReadInt64BE(rawFile, 24);
            long dataOffset = BinaryUtils.ReadInt64BE(rawFile, 32);

            if (origFileSize != rawFile.Length || dataOffset < 48 + metadataSize || dataOffset > rawFile.Length)
            {
                throw new InvalidDataException("Некорректные смещения заголовка SerializedFile v23.");
            }

            int pos = 48;
            BinaryUtils.ReadCStringUtf8(rawFile, ref pos); // unityVersion
            pos += 4; // targetPlatform
            bool enableTypeTree = rawFile[pos++] != 0;

            int typeCount = BinaryUtils.ReadInt32LE(rawFile, pos);
            pos += 4;
            if (typeCount < 0 || typeCount > 100000)
            {
                throw new InvalidDataException("Некорректное число типов в SerializedFile: " + typeCount);
            }

            int[] classIds = new int[typeCount];
            for (int i = 0; i < typeCount; i++)
            {
                int classId = BinaryUtils.ReadInt32LE(rawFile, pos);
                pos += 4;
                pos += 1; // isStrippedType
                pos += 2; // scriptTypeIndex
                if (classId == ClassIdMonoBehaviour)
                {
                    pos += 16; // scriptId Hash128
                }
                pos += 16; // oldTypeHash Hash128

                if (enableTypeTree)
                {
                    pos += 16; // typeTreeContentHash Hash128
                    int ttBlobSize = BinaryUtils.ReadInt32LE(rawFile, pos);
                    pos += 4;
                    if (ttBlobSize < 0 || pos + ttBlobSize > dataOffset)
                    {
                        throw new InvalidDataException("Некорректный размер TypeTree блоба: " + ttBlobSize);
                    }
                    pos += ttBlobSize;

                    int depCount = BinaryUtils.ReadInt32LE(rawFile, pos);
                    pos += 4;
                    if (depCount < 0 || pos + depCount * 4 > dataOffset)
                    {
                        throw new InvalidDataException("Некорректный массив зависимостей типа.");
                    }
                    pos += depCount * 4;
                }

                classIds[i] = classId;
            }

            int objectCount = BinaryUtils.ReadInt32LE(rawFile, pos);
            pos += 4;
            pos = (pos + 3) & ~3; // 4-byte align before ObjectInfo table
            int objectTableOffset = pos;

            long objectTableEnd = (long)objectTableOffset + (long)objectCount * 24L;
            if (objectCount < 0 || objectTableEnd > 48L + metadataSize)
            {
                throw new InvalidDataException("Таблица объектов SerializedFile выходит за пределы метаданных.");
            }

            byte[] headerAndMeta = new byte[(int)dataOffset];
            Buffer.BlockCopy(rawFile, 0, headerAndMeta, 0, (int)dataOffset);

            using (MemoryStream dataStream = new MemoryStream(Math.Max(1024, (int)(rawFile.Length - dataOffset))))
            {
                for (int i = 0; i < objectCount; i++)
                {
                    int entryOff = objectTableOffset + i * 24;
                    long pathId = BinaryUtils.ReadInt64LE(rawFile, entryOff);
                    long relStart = BinaryUtils.ReadInt64LE(rawFile, entryOff + 8);
                    uint byteSize = BinaryUtils.ReadUInt32LE(rawFile, entryOff + 16);
                    int typeId = BinaryUtils.ReadInt32LE(rawFile, entryOff + 20);

                    if (typeId < 0 || typeId >= typeCount)
                    {
                        throw new InvalidDataException("Некорректный индекс типа объекта #" + pathId);
                    }

                    long absStart = dataOffset + relStart;
                    if (absStart < dataOffset || absStart + byteSize > rawFile.Length)
                    {
                        throw new InvalidDataException("Данные объекта #" + pathId + " выходят за пределы файла.");
                    }

                    byte[] origObj = new byte[(int)byteSize];
                    Buffer.BlockCopy(rawFile, (int)absStart, origObj, 0, (int)byteSize);

                    byte[] updatedObj = mutator(pathId, classIds[typeId], origObj);
                    if (updatedObj == null)
                    {
                        updatedObj = origObj;
                    }

                    long newRelStart = dataStream.Position;
                    BinaryUtils.PutInt64LE(headerAndMeta, entryOff + 8, newRelStart);
                    BinaryUtils.PutUInt32LE(headerAndMeta, entryOff + 16, (uint)updatedObj.Length);

                    dataStream.Write(updatedObj, 0, updatedObj.Length);
                    BinaryUtils.AlignStream(dataStream, 8);
                }

                long newFileSize = dataOffset + dataStream.Length;
                BinaryUtils.PutInt64BE(headerAndMeta, 24, newFileSize);

                byte[] result = new byte[(int)newFileSize];
                Buffer.BlockCopy(headerAndMeta, 0, result, 0, headerAndMeta.Length);
                byte[] streamBuf = dataStream.GetBuffer();
                Buffer.BlockCopy(streamBuf, 0, result, headerAndMeta.Length, (int)dataStream.Length);
                return result;
            }
        }
    }
}
