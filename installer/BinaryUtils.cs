using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WmaoRuInstaller
{
    internal static class BinaryUtils
    {
        public static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false, true);
        private static readonly uint[] Crc32Table = BuildCrc32Table();

        private static uint[] BuildCrc32Table()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    c = (c & 1U) != 0 ? (0xEDB88320U ^ (c >> 1)) : (c >> 1);
                }
                table[i] = c;
            }
            return table;
        }

        public static uint ComputeCrc32(byte[] data)
        {
            return ComputeCrc32(data, 0, data.Length);
        }

        public static uint ComputeCrc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFFU;
            int end = offset + count;
            for (int i = offset; i < end; i++)
            {
                crc = Crc32Table[(crc ^ data[i]) & 0xFFU] ^ (crc >> 8);
            }
            return ~crc;
        }

        public static string ComputeFileSha256Hex(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(fs));
            }
        }

        public static string ComputeBytesSha256Hex(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(data));
            }
        }

        private static string ToLowerHex(byte[] hash)
        {
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2"));
            }
            return sb.ToString();
        }

        public static short ReadInt16LE(byte[] buf, int offset)
        {
            return (short)(buf[offset] | (buf[offset + 1] << 8));
        }

        public static ushort ReadUInt16BE(byte[] buf, int offset)
        {
            return (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

        public static int ReadInt32LE(byte[] buf, int offset)
        {
            return buf[offset]
                | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16)
                | (buf[offset + 3] << 24);
        }

        public static uint ReadUInt32LE(byte[] buf, int offset)
        {
            return (uint)ReadInt32LE(buf, offset);
        }

        public static long ReadInt64LE(byte[] buf, int offset)
        {
            uint lo = ReadUInt32LE(buf, offset);
            uint hi = ReadUInt32LE(buf, offset + 4);
            return (long)lo | ((long)hi << 32);
        }

        public static int ReadInt32BE(byte[] buf, int offset)
        {
            return (buf[offset] << 24)
                | (buf[offset + 1] << 16)
                | (buf[offset + 2] << 8)
                | buf[offset + 3];
        }

        public static uint ReadUInt32BE(byte[] buf, int offset)
        {
            return (uint)ReadInt32BE(buf, offset);
        }

        public static long ReadInt64BE(byte[] buf, int offset)
        {
            uint hi = ReadUInt32BE(buf, offset);
            uint lo = ReadUInt32BE(buf, offset + 4);
            return ((long)hi << 32) | lo;
        }

        public static void PutInt32LE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)value;
            buf[offset + 1] = (byte)(value >> 8);
            buf[offset + 2] = (byte)(value >> 16);
            buf[offset + 3] = (byte)(value >> 24);
        }

        public static void PutUInt32LE(byte[] buf, int offset, uint value)
        {
            PutInt32LE(buf, offset, (int)value);
        }

        public static void PutInt64LE(byte[] buf, int offset, long value)
        {
            PutUInt32LE(buf, offset, (uint)value);
            PutUInt32LE(buf, offset + 4, (uint)((ulong)value >> 32));
        }

        public static void PutUInt32BE(byte[] buf, int offset, uint value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        public static void PutInt64BE(byte[] buf, int offset, long value)
        {
            ulong u = (ulong)value;
            PutUInt32BE(buf, offset, (uint)(u >> 32));
            PutUInt32BE(buf, offset + 4, (uint)u);
        }

        public static void WriteInt32LE(Stream s, int value)
        {
            byte[] b = new byte[4];
            PutInt32LE(b, 0, value);
            s.Write(b, 0, 4);
        }

        public static void WriteInt64LE(Stream s, long value)
        {
            byte[] b = new byte[8];
            PutInt64LE(b, 0, value);
            s.Write(b, 0, 8);
        }

        public static void WriteUInt16BE(Stream s, ushort value)
        {
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)value);
        }

        public static void WriteInt32BE(Stream s, int value)
        {
            WriteUInt32BE(s, (uint)value);
        }

        public static void WriteUInt32BE(Stream s, uint value)
        {
            byte[] b = new byte[4];
            PutUInt32BE(b, 0, value);
            s.Write(b, 0, 4);
        }

        public static void WriteInt64BE(Stream s, long value)
        {
            byte[] b = new byte[8];
            PutInt64BE(b, 0, value);
            s.Write(b, 0, 8);
        }

        public static string ReadCStringUtf8(byte[] buf, ref int pos)
        {
            int start = pos;
            while (pos < buf.Length && buf[pos] != 0)
            {
                pos++;
            }
            if (pos >= buf.Length)
            {
                throw new InvalidDataException("Unterminated UTF-8 C-string at offset " + start + ".");
            }
            string text = Utf8NoBom.GetString(buf, start, pos - start);
            pos++;
            return text;
        }

        public static void WriteCStringUtf8(Stream s, string value)
        {
            byte[] bytes = Utf8NoBom.GetBytes(value);
            s.Write(bytes, 0, bytes.Length);
            s.WriteByte(0);
        }

        public static string ReadAlignedUtf8(byte[] buf, ref int pos)
        {
            if (pos + 4 > buf.Length)
            {
                throw new InvalidDataException("Truncated aligned string length at offset " + pos + ".");
            }
            int len = ReadInt32LE(buf, pos);
            pos += 4;
            if (len < 0 || pos + len > buf.Length)
            {
                throw new InvalidDataException("Invalid aligned string length " + len + " at offset " + (pos - 4) + ".");
            }
            string text = Utf8NoBom.GetString(buf, pos, len);
            pos = (pos + len + 3) & ~3;
            return text;
        }

        public static void WriteAlignedUtf8(Stream s, string value)
        {
            byte[] bytes = Utf8NoBom.GetBytes(value);
            WriteInt32LE(s, bytes.Length);
            s.Write(bytes, 0, bytes.Length);
            int pad = (4 - (bytes.Length & 3)) & 3;
            for (int i = 0; i < pad; i++)
            {
                s.WriteByte(0);
            }
        }

        public static void AlignStream(Stream s, int alignment)
        {
            int rem = (int)(s.Position % alignment);
            if (rem != 0)
            {
                int pad = alignment - rem;
                for (int i = 0; i < pad; i++)
                {
                    s.WriteByte(0);
                }
            }
        }
    }
}
