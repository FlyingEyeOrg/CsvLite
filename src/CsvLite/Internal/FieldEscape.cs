using System;

namespace CsvLite.Internal
{
    /// <summary>
    /// 字段转义。所有写入都落在调用方提供的缓冲里，不产生分配。
    /// 扫描通过 <c>MemoryExtensions.IndexOfAny</c> 走向量化实现。
    /// </summary>
    internal static class FieldEscape
    {
        /// <summary>
        /// 当 <paramref name="s"/> 中不含任何会破坏 CSV 编码的字节时返回 true。
        /// </summary>
        /// <remarks>
        /// 这里用两次向量化扫描而不是一次：netstandard2.0 的 <c>System.Memory</c> 只为
        /// <see cref="byte"/> 提供了三字符的 <c>IndexOfAny</c> 重载，而 CR 与 LF 是两个都必须排除的值。
        /// 对一个常驻 L1 的字段扫两遍，仍然远比标量循环便宜。
        /// </remarks>
        internal static bool IsClean(ReadOnlySpan<byte> s, byte delimiter, byte quote)
        {
            if (s.IndexOfAny(delimiter, quote, (byte)'\r') >= 0) return false;
            return s.IndexOf((byte)'\n') < 0;
        }

        /// <summary>统计 <paramref name="s"/> 中 <paramref name="quote"/> 出现的次数。</summary>
        internal static int CountQuotes(ReadOnlySpan<byte> s, byte quote)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == quote) n++;
            }
            return n;
        }

        /// <summary>
        /// 把 <paramref name="quoted"/> 用引号包起来输出，内部的引号翻倍。
        /// 目标缓冲必须能容纳 <c>quoted.Length + quoteCount + 2</c> 字节。
        /// 正向写入；即使 <paramref name="dst"/> 与 <paramref name="quoted"/> 重叠也安全，
        /// 因为 <paramref name="dst"/> 的写游标永远不会落后于读游标。
        /// </summary>
        internal static int WriteQuoted(Span<byte> dst, ReadOnlySpan<byte> quoted, byte quote)
        {
            int o = 0;
            dst[o++] = quote;
            for (int i = 0; i < quoted.Length; i++)
            {
                byte b = quoted[i];
                dst[o++] = b;
                if (b == quote) dst[o++] = quote;
            }
            dst[o++] = quote;
            return o;
        }

        /// <summary>给定载荷字节数 <paramref name="byteCount"/> 与引号数 <paramref name="quoteCount"/>，返回加引号后的编码长度。</summary>
        internal static int QuotedLength(int byteCount, int quoteCount) => byteCount + quoteCount + 2;

        /// <summary>
        /// 把已经位于 <c>buf[offset .. offset+length)</c> 的未加引号载荷，就地扩展成同一偏移处的带引号字段。
        /// 从后往前处理，因此重叠是安全的。
        /// </summary>
        internal static int QuoteInPlace(byte[] buf, int offset, int length, int quoteCount, byte quote)
        {
            int newLength = QuotedLength(length, quoteCount);
            int dst = offset + newLength - 1;
            buf[dst--] = quote;                       // 收尾引号
            for (int i = offset + length - 1; i >= offset; i--)
            {
                byte b = buf[i];
                buf[dst--] = b;
                if (b == quote) buf[dst--] = quote;
            }
            buf[dst] = quote;                         // 起始引号
            return newLength;
        }
    }
}
