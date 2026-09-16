// myFSM Unity Runtime — tiny codecs shared by the Core layer.
// Little-endian integer reads (platform-independent) + stable hashes.

using System;
using System.Text;

namespace MyFSM.Core
{
    public static class AstCodec
    {
        public static ushort ReadU16LE(byte[] data, int at)
        {
            return (ushort)(data[at] | (data[at + 1] << 8));
        }

        public static uint ReadU32LE(byte[] data, int at)
        {
            return (uint)data[at] | ((uint)data[at + 1] << 8) |
                   ((uint)data[at + 2] << 16) | ((uint)data[at + 3] << 24);
        }

        public static int ReadI32LE(byte[] data, int at)
        {
            unchecked
            {
                return (int)ReadU32LE(data, at);
            }
        }

        public static long ReadI64LE(byte[] data, int at)
        {
            unchecked
            {
                uint lo = ReadU32LE(data, at);
                uint hi = ReadU32LE(data, at + 4);
                return (long)(((ulong)hi << 32) | lo);
            }
        }

        public static float ReadF32LE(byte[] data, int at)
        {
            // Round-trip through BitConverter so the conversion is correct on
            // any platform endianness (BitConverter exists in Unity and in the
            // sandbox; Int32BitsToSingle does not exist on netstandard2.0).
            int bits = ReadI32LE(data, at);
            return BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        }

        public static double ReadF64LE(byte[] data, int at)
        {
            long bits = ReadI64LE(data, at);
            return BitConverter.ToDouble(BitConverter.GetBytes(bits), 0);
        }

        public static string ReadUtf8(byte[] data, int at, int length)
        {
            return Encoding.UTF8.GetString(data, at, length);
        }

        /// <summary>
        /// 32-bit FNV-1a hash. Used wherever the DSL needs a stable int for a
        /// string (notably Unity tag names for getTag/getNearestOfTag), so the
        /// mapping is deterministic across runs and machines.
        /// </summary>
        public static int Fnv1a32(string text)
        {
            unchecked
            {
                const uint offset = 2166136261u;
                const uint prime = 16777619u;
                uint h = offset;
                if (text != null)
                {
                    for (int i = 0; i < text.Length; i++)
                    {
                        h ^= text[i];
                        h *= prime;
                    }
                }
                return (int)h;
            }
        }
    }
}
