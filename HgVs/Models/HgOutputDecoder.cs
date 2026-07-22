using System;
using System.Text;

namespace HgVs.Models
{
    internal static class HgOutputDecoder
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        public static string Decode(byte[] bytes)
        {
            if (bytes.Length == 0) return "";

            var result = new StringBuilder(bytes.Length);
            int lineStart = 0;

            for (int i = 0; i <= bytes.Length; i++)
            {
                if (i == bytes.Length || bytes[i] == '\n')
                {
                    int lineEnd = i;
                    if (lineEnd > lineStart && bytes[lineEnd - 1] == '\r')
                        lineEnd--;

                    int lineLen = lineEnd - lineStart;
                    if (lineLen > 0)
                    {
                        var lineBytes = new byte[lineLen];
                        Array.Copy(bytes, lineStart, lineBytes, 0, lineLen);
                        try
                        {
                            result.Append(StrictUtf8.GetString(lineBytes));
                        }
                        catch (DecoderFallbackException)
                        {
                            result.Append(Encoding.Default.GetString(lineBytes));
                        }
                    }

                    if (i < bytes.Length)
                        result.Append('\n');

                    lineStart = i + 1;
                }
            }

            return result.ToString();
        }
    }
}
