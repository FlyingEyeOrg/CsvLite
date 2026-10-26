using System;
using System.Collections.Generic;
using System.Text;

namespace CsvLite.Internal
{
    /// <summary>序列化与解析那唯一一行表头记录。刻意保持最小实现：不做方言嗅探。</summary>
    internal static class HeaderCodec
    {
        /// <summary>构造表头记录的字节（载荷 + 行终止符），不含 BOM。</summary>
        internal static byte[] Build(IReadOnlyList<string> headers, Dialect dialect)
        {
            if (headers.Count == 0) return dialect.NewLine.Length == 0 ? Array.Empty<byte>() : AppendNewLine(Array.Empty<byte>(), dialect);

            var scratch = new List<byte>(64);
            for (int i = 0; i < headers.Count; i++)
            {
                if (i > 0) scratch.Add(dialect.Delimiter);
                AppendField(scratch, headers[i] ?? string.Empty, dialect);
            }
            byte[] line = scratch.ToArray();
            return AppendNewLine(line, dialect);
        }

        private static void AppendField(List<byte> target, string value, Dialect dialect)
        {
            // 表头字段必须用与正文相同的编码，否则文件里会混入两种字节序列。
            byte[] encoded = Utf8Compat.GetBytes(dialect.Encoding, value);
            byte quote = dialect.QuoteCharByte;

            bool mustQuote = dialect.QuoteMode == QuoteMode.EveryField
                             || !FieldEscape.IsClean(encoded, dialect.Delimiter, quote);

            if (!mustQuote)
            {
                target.AddRange(encoded);
                return;
            }

            target.Add(quote);
            for (int i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == quote) target.Add(quote);
                target.Add(encoded[i]);
            }
            target.Add(quote);
        }

        private static byte[] AppendNewLine(byte[] line, Dialect dialect)
        {
            byte[] result = new byte[line.Length + dialect.NewLine.Length];
            Array.Copy(line, result, line.Length);
            Array.Copy(dialect.NewLine, 0, result, line.Length, dialect.NewLine.Length);
            return result;
        }

        /// <summary>
        /// 把表头记录拆成各个字段值。<paramref name="line"/> 可以带 BOM，行终止符可有可无；
        /// 两者都在这里剥掉。字段内容用 <paramref name="encoding"/> 解码。
        /// </summary>
        internal static string[] Parse(ReadOnlySpan<byte> line, Encoding encoding, byte delimiter, byte quote)
        {
            // 剥掉 BOM（按该编码自己的 BOM 判断，而不是硬编码 UTF-8 的三个字节）
            byte[] bom = encoding.GetPreamble();
            if (bom.Length > 0 && line.Length >= bom.Length && line.Slice(0, bom.Length).SequenceEqual(bom))
                line = line.Slice(bom.Length);

            // 剥掉一个行终止符
            if (line.Length >= 2 && line[line.Length - 2] == (byte)'\r' && line[line.Length - 1] == (byte)'\n')
                line = line.Slice(0, line.Length - 2);
            else if (line.Length >= 1 && (line[line.Length - 1] == (byte)'\n' || line[line.Length - 1] == (byte)'\r'))
                line = line.Slice(0, line.Length - 1);

            if (line.Length == 0) return Array.Empty<string>();

            var result = new List<string>(8);
            int i = 0;
            var field = new List<byte>(32);

            while (true)
            {
                field.Clear();
                bool quoted = i < line.Length && line[i] == quote;

                if (quoted)
                {
                    i++; // 起始引号
                    while (i < line.Length)
                    {
                        byte b = line[i];
                        if (b == quote)
                        {
                            if (i + 1 < line.Length && line[i + 1] == quote)
                            {
                                field.Add(quote);
                                i += 2;
                                continue;
                            }
                            i++; // 收尾引号
                            break;
                        }
                        field.Add(b);
                        i++;
                    }
                    // 容忍收尾引号之后、分隔符之前的残余字符
                    while (i < line.Length && line[i] != delimiter) i++;
                }
                else
                {
                    while (i < line.Length && line[i] != delimiter)
                    {
                        field.Add(line[i]);
                        i++;
                    }
                }

                result.Add(Utf8Compat.GetString(encoding, field.ToArray()));

                if (i >= line.Length) break;
                i++; // 分隔符
                if (i >= line.Length)
                {
                    result.Add(string.Empty); // 末尾的空字段
                    break;
                }
            }

            return result.ToArray();
        }
    }
}
