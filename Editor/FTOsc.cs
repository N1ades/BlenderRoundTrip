using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Nyades.BlenderRoundTrip
{
    // OSC 1.0, including nested bundles. No Unity or third-party runtime dependency.
    public static class FTOsc
    {
        public sealed class Message { public string address; public object[] arguments; }
        static void String(Stream stream, string value)
        {
            if (value.IndexOf('\0') >= 0) throw new FormatException("OSC strings cannot contain a null character.");
            var bytes = Encoding.UTF8.GetBytes(value); stream.Write(bytes, 0, bytes.Length); stream.WriteByte(0);
            while (stream.Position % 4 != 0) stream.WriteByte(0);
        }
        static void Number(Stream stream, byte[] bytes)
        { if (BitConverter.IsLittleEndian) Array.Reverse(bytes); stream.Write(bytes, 0, bytes.Length); }
        public static byte[] Encode(string address, object value)
        {
            using (var stream = new MemoryStream())
            {
                String(stream, address);
                if (value is string text) { String(stream, ",s"); String(stream, text); }
                else if (value is bool flag) String(stream, flag ? ",T" : ",F");
                else if (value is int integer) { String(stream, ",i"); Number(stream, BitConverter.GetBytes(integer)); }
                else { String(stream, ",f"); Number(stream, BitConverter.GetBytes(Convert.ToSingle(value))); }
                if (stream.Length > 65507) throw new FormatException("OSC packet is too large.");
                return stream.ToArray();
            }
        }
        static string ReadString(byte[] data, ref int offset, int end)
        {
            int start = offset;
            while (offset < end && data[offset] != 0) offset++;
            if (offset >= end) throw new FormatException("Truncated OSC string.");
            string value = Encoding.UTF8.GetString(data, start, offset - start);
            offset = (offset + 4) & ~3;
            if (offset > end) throw new FormatException("Truncated OSC padding.");
            return value;
        }
        static byte[] Bytes(byte[] data, ref int offset, int end, int count)
        {
            if (offset + count > end) throw new FormatException("Truncated OSC number.");
            var bytes = new byte[count]; Array.Copy(data, offset, bytes, 0, count); offset += count;
            if (BitConverter.IsLittleEndian) Array.Reverse(bytes); return bytes;
        }
        static void Parse(byte[] data, int start, int end, int depth, List<Message> messages)
        {
            if (depth > 8 || end - start > 65507 || start < 0 || end > data.Length) throw new FormatException("OSC packet exceeds limits.");
            int offset = start;
            string address = ReadString(data, ref offset, end);
            if (address == "#bundle")
            {
                if (offset + 8 > end) throw new FormatException("Truncated OSC bundle.");
                offset += 8;
                while (offset < end)
                {
                    int length = BitConverter.ToInt32(Bytes(data, ref offset, end, 4), 0);
                    if (length <= 0 || length > end - offset) throw new FormatException("Invalid OSC bundle element.");
                    Parse(data, offset, offset + length, depth + 1, messages); offset += length;
                }
                return;
            }
            string tags = ReadString(data, ref offset, end);
            if (!address.StartsWith("/") || !tags.StartsWith(",")) throw new FormatException("Invalid OSC message header.");
            var values = new List<object>();
            foreach (char tag in tags.Substring(1))
                switch (tag)
                {
                    case 'i': values.Add(BitConverter.ToInt32(Bytes(data, ref offset, end, 4), 0)); break;
                    case 'f': values.Add(BitConverter.ToSingle(Bytes(data, ref offset, end, 4), 0)); break;
                    case 'd': values.Add(BitConverter.ToDouble(Bytes(data, ref offset, end, 8), 0)); break;
                    case 's': values.Add(ReadString(data, ref offset, end)); break;
                    case 'T': values.Add(true); break;
                    case 'F': values.Add(false); break;
                    case 'N': values.Add(null); break;
                    default: throw new FormatException("Unsupported OSC type: " + tag);
                }
            if (offset != end) throw new FormatException("Trailing OSC data.");
            messages.Add(new Message { address = address, arguments = values.ToArray() });
        }
        public static List<Message> Decode(byte[] data)
        { var result = new List<Message>(); Parse(data, 0, data.Length, 0, result); return result; }
    }
}
