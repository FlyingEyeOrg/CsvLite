using System;
using System.Text;

namespace CsvLite.Internal
{
    /// <summary>
    /// 行编码时所用方言与值格式选项的不可变快照。
    /// 每个 appender 只取一次，这样行在热路径上永远不必回头查可变选项。
    /// </summary>
    internal sealed class Dialect
    {
        internal readonly Encoding Encoding;
        internal readonly byte Delimiter;
        internal readonly byte QuoteCharByte;
        internal readonly byte[] NewLine;
        internal readonly QuoteMode QuoteMode;
        internal readonly EnumFormat EnumFormat;
        internal readonly string? DateTimeFormat;
        internal readonly string? GuidFormat;
        internal readonly int MaxRowBytes;

        internal Dialect(CsvOptions options, byte quoteCharByte)
        {
            Encoding = options.Encoding;
            Delimiter = options.Delimiter;
            QuoteCharByte = quoteCharByte;
            NewLine = (byte[])options.NewLine.Clone();
            QuoteMode = options.QuoteMode;
            EnumFormat = options.EnumFormat;
            DateTimeFormat = options.DateTimeFormat;
            GuidFormat = options.GuidFormat;
            MaxRowBytes = options.MaxRowBytes;
        }
    }
}
