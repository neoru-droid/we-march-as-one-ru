using System;
using System.IO;

namespace WmaoRuInstaller
{
    internal static class Lz4Codec
    {
        private const int MinMatch = 4;
        private const int LastLiterals = 5;
        private const int MfLimit = 12;
        private const int MaxDistance = 65535;
        private const int HashLog = 12;
        private const int HashSize = 1 << HashLog;

        public static byte[] DecompressBlock(byte[] src, int srcOffset, int srcLength, int uncompressedLength)
        {
            if (uncompressedLength < 0)
            {
                throw new InvalidDataException("Отрицательный размер распаковки LZ4.");
            }
            if (uncompressedLength == 0)
            {
                return new byte[0];
            }

            byte[] dst = new byte[uncompressedLength];
            int ip = srcOffset;
            int ipEnd = srcOffset + srcLength;
            int op = 0;

            while (ip < ipEnd)
            {
                int token = src[ip++];
                int litLen = (token >> 4) & 0x0F;
                if (litLen == 15)
                {
                    int add;
                    do
                    {
                        if (ip >= ipEnd)
                        {
                            throw new InvalidDataException("Обрыв потока LZ4 при чтении длины литералов.");
                        }
                        add = src[ip++];
                        litLen += add;
                    }
                    while (add == 255);
                }

                if (ip + litLen > ipEnd || op + litLen > uncompressedLength)
                {
                    throw new InvalidDataException("Переполнение буфера литералов LZ4.");
                }

                if (litLen > 0)
                {
                    Buffer.BlockCopy(src, ip, dst, op, litLen);
                    ip += litLen;
                    op += litLen;
                }

                if (ip >= ipEnd)
                {
                    break;
                }

                if (ip + 2 > ipEnd)
                {
                    throw new InvalidDataException("Обрыв потока LZ4 при чтении смещения совпадения.");
                }

                int matchOffset = src[ip] | (src[ip + 1] << 8);
                ip += 2;
                int matchSrc = op - matchOffset;
                if (matchOffset <= 0 || matchSrc < 0)
                {
                    throw new InvalidDataException("Некорректное смещение совпадения LZ4: " + matchOffset);
                }

                int matchLen = (token & 0x0F) + MinMatch;
                if ((token & 0x0F) == 15)
                {
                    int add;
                    do
                    {
                        if (ip >= ipEnd)
                        {
                            throw new InvalidDataException("Обрыв потока LZ4 при чтении длины совпадения.");
                        }
                        add = src[ip++];
                        matchLen += add;
                    }
                    while (add == 255);
                }

                if (op + matchLen > uncompressedLength)
                {
                    throw new InvalidDataException("Переполнение выходного буфера LZ4 при копировании совпадения.");
                }

                if (matchOffset >= matchLen)
                {
                    Buffer.BlockCopy(dst, matchSrc, dst, op, matchLen);
                    op += matchLen;
                }
                else
                {
                    for (int i = 0; i < matchLen; i++)
                    {
                        dst[op++] = dst[matchSrc + i];
                    }
                }
            }

            if (op != uncompressedLength)
            {
                throw new InvalidDataException(
                    "Размер распакованного блока LZ4 (" + op + ") не совпадает с ожидаемым (" + uncompressedLength + ").");
            }
            return dst;
        }

        public static byte[] CompressBlock(byte[] src, int srcOffset, int srcLength)
        {
            if (srcLength == 0)
            {
                return new byte[0];
            }

            using (MemoryStream outStream = new MemoryStream(srcLength + (srcLength / 255) + 16))
            {
                if (srcLength < MfLimit + 1)
                {
                    WriteLiteralsOnly(outStream, src, srcOffset, srcLength);
                }
                else
                {
                    int[] hashTable = new int[HashSize];
                    for (int i = 0; i < HashSize; i++)
                    {
                        hashTable[i] = -1;
                    }

                    int ipEnd = srcOffset + srcLength;
                    int matchLimit = ipEnd - LastLiterals;
                    int mfLimit = ipEnd - MfLimit;

                    int anchor = srcOffset;
                    hashTable[Hash4(src, srcOffset)] = srcOffset;
                    int ip = srcOffset + 1;

                    while (ip <= mfLimit)
                    {
                        int h = Hash4(src, ip);
                        int refPos = hashTable[h];
                        hashTable[h] = ip;

                        if (refPos < srcOffset || (ip - refPos) > MaxDistance || !Equals4(src, refPos, ip))
                        {
                            int step = 1 + ((ip - anchor) >> 6);
                            ip += step;
                            continue;
                        }

                        while (ip > anchor && refPos > srcOffset && src[ip - 1] == src[refPos - 1])
                        {
                            ip--;
                            refPos--;
                        }

                        int litLen = ip - anchor;
                        int matchLen = MinMatch;
                        while (ip + matchLen < matchLimit && src[refPos + matchLen] == src[ip + matchLen])
                        {
                            matchLen++;
                        }

                        int litToken = litLen >= 15 ? 15 : litLen;
                        int matchMinus4 = matchLen - MinMatch;
                        int matchToken = matchMinus4 >= 15 ? 15 : matchMinus4;
                        outStream.WriteByte((byte)((litToken << 4) | matchToken));

                        if (litLen >= 15)
                        {
                            WriteExtraLength(outStream, litLen - 15);
                        }
                        if (litLen > 0)
                        {
                            outStream.Write(src, anchor, litLen);
                        }

                        int distance = ip - refPos;
                        outStream.WriteByte((byte)distance);
                        outStream.WriteByte((byte)(distance >> 8));

                        if (matchMinus4 >= 15)
                        {
                            WriteExtraLength(outStream, matchMinus4 - 15);
                        }

                        ip += matchLen;
                        anchor = ip;
                        if (ip <= mfLimit)
                        {
                            hashTable[Hash4(src, ip - 2)] = ip - 2;
                        }
                    }

                    int remaining = ipEnd - anchor;
                    WriteLiteralsOnly(outStream, src, anchor, remaining);
                }

                byte[] compressed = outStream.ToArray();
                byte[] verify = DecompressBlock(compressed, 0, compressed.Length, srcLength);
                for (int i = 0; i < srcLength; i++)
                {
                    if (verify[i] != src[srcOffset + i])
                    {
                        throw new InvalidOperationException("Ошибка самопроверки упаковщика LZ4 на байте " + i);
                    }
                }
                return compressed;
            }
        }

        private static void WriteLiteralsOnly(Stream outStream, byte[] src, int offset, int count)
        {
            int litToken = count >= 15 ? 15 : count;
            outStream.WriteByte((byte)(litToken << 4));
            if (count >= 15)
            {
                WriteExtraLength(outStream, count - 15);
            }
            if (count > 0)
            {
                outStream.Write(src, offset, count);
            }
        }

        private static void WriteExtraLength(Stream outStream, int extra)
        {
            while (extra >= 255)
            {
                outStream.WriteByte(255);
                extra -= 255;
            }
            outStream.WriteByte((byte)extra);
        }

        private static int Hash4(byte[] buf, int pos)
        {
            uint v = BinaryUtils.ReadUInt32LE(buf, pos);
            return (int)((v * 2654435761U) >> (32 - HashLog));
        }

        private static bool Equals4(byte[] buf, int a, int b)
        {
            return buf[a] == buf[b]
                && buf[a + 1] == buf[b + 1]
                && buf[a + 2] == buf[b + 2]
                && buf[a + 3] == buf[b + 3];
        }
    }
}
