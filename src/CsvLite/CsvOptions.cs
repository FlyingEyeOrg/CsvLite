using System;
using System.Text;

namespace CsvLite
{
    /// <summary><see cref="Enum"/> 值的写出方式。</summary>
    public enum EnumFormat
    {
        /// <summary>写出枚举名，例如 <c>Ok</c>。</summary>
        Name = 0,

        /// <summary>写出底层的数值。</summary>
        Numeric = 1,
    }

    /// <summary>字段何时被引号包裹。</summary>
    public enum QuoteMode
    {
        /// <summary>仅当字段会破坏 CSV 时才加引号（含分隔符、引号、CR 或 LF）。</summary>
        Minimal = 0,

        /// <summary>每个字段都加引号，包括空字段。</summary>
        EveryField = 1,
    }

    /// <summary>行以何种方式抵达文件。</summary>
    public enum WriteMode
    {
        /// <summary>
        /// 生产者把行拷进各自私有的环形缓冲，由专职写线程追加到文件。
        /// 吞吐最好；写入错误会在下一次 <c>Append</c>/<c>Flush</c> 时暴露。
        /// </summary>
        Async = 0,

        /// <summary>
        /// 在调用线程上、持锁直接写入 <see cref="System.IO.FileStream"/>。
        /// 写入错误立即抛出，且全局顺序严格。面向单线程调用方。
        /// </summary>
        Direct = 1,
    }

    /// <summary>后台写线程出错时的处理方式。</summary>
    public enum WriteErrorPolicy
    {
        /// <summary>记录错误，并在下一次 <c>Append</c>/<c>Flush</c> 时重新抛出。</summary>
        Throw = 0,

        /// <summary>记录错误并停止写线程，但不重新抛出（适合日志型用法）。</summary>
        Stop = 1,
    }

    /// <summary>已写入文件的数据的持久化策略。</summary>
    public enum FlushPolicy
    {
        /// <summary>由操作系统决定何时落盘；只有 <c>Flush</c>/<c>Dispose</c> 会强制刷出。</summary>
        Batch = 0,

        /// <summary>每批写入之后调用 <c>FileStream.Flush(true)</c>（非常昂贵）。</summary>
        Sync = 1,
    }

    /// <summary>
    /// <see cref="CsvAppender"/> 的配置。一旦交给 <see cref="CsvAppender.Create"/> /
    /// <see cref="CsvAppender.Open"/>，就应当把这个实例视为不可变。
    /// </summary>
    public sealed class CsvOptions
    {
        // ── 方言 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 文本编码，默认 UTF-8（无 BOM）。中文 Excel 场景常用 <see cref="CsvEncodings.Gbk"/>。
        /// </summary>
        /// <remarks>
        /// 包一层 <see cref="Encoding"/> 而不是换成字符串，是因为编码决定了字段字节的长度，
        /// 而行缓冲、转义扫描、表头长度计算全部按字节进行，必须与之一致。
        /// </remarks>
        public Encoding Encoding { get; set; } = Utf8NoBom;

        /// <summary>默认的无 BOM UTF-8 编码。</summary>
        internal static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>字段分隔符。必须是单个 ASCII 字节，且不能是 CR、LF 或引号字符。</summary>
        public byte Delimiter { get; set; } = (byte)',';

        /// <summary>记录终止符。必须是 <c>"\n"</c>、<c>"\r"</c> 或 <c>"\r\n"</c>。</summary>
        public byte[] NewLine { get; set; } = { (byte)'\n' };

        /// <summary>引号字符。必须是 ASCII。</summary>
        public char QuoteChar { get; set; } = '"';

        /// <summary>每个字段都加引号，还是仅在必要时加。</summary>
        public QuoteMode QuoteMode { get; set; } = QuoteMode.Minimal;

        // ── 文件 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 创建文件时写入该编码的 BOM（若该编码有 BOM）。
        /// </summary>
        /// <remarks>
        /// 只有 UTF-8 / UTF-16 这类编码有 BOM。GBK 没有 BOM，中文 Excel 是按系统 ANSI 代码页
        /// 解释无 BOM 的 CSV 的，所以用 GBK 时保持 <c>false</c> 即可；
        /// 若写 UTF-8 且希望 Excel 不把中文认成 ANSI，则应把此项设为 <c>true</c>。
        /// <para>
        /// 注意默认的 <see cref="Encoding"/> 用的是「不输出 BOM」的 UTF-8 实例，
        /// 它自身的 <c>GetPreamble()</c> 是空的。因此本选项为 <c>true</c> 且编码是 UTF-8 时，
        /// 会改用标准的 UTF-8 BOM 字节，而不是依赖该实例的前导码。
        /// </para>
        /// </remarks>
        public bool WriteBom { get; set; }

        /// <summary>文件是否应带有一行表头记录。</summary>
        public bool HasHeader { get; set; } = true;

        /// <summary>
        /// 允许 <see cref="CsvAppender.Create"/> 截断已存在的文件。默认 <c>false</c>：
        /// 在已有文件上创建会抛异常，而不是悄悄把它毁掉。
        /// </summary>
        public bool Overwrite { get; set; }

        /// <summary>释放 appender 时刷出待写数据。</summary>
        public bool FlushOnDispose { get; set; } = true;

        /// <summary>释放 appender 时同时强制落盘（<c>fsync</c>）。</summary>
        public bool FsyncOnDispose { get; set; }

        // ── 值格式化 ───────────────────────────────────────────────────────────

        /// <summary><see cref="DateTime"/> 的自定义格式；<c>null</c> 表示使用往返格式 <c>"O"</c>。</summary>
        public string? DateTimeFormat { get; set; }

        /// <summary><see cref="Guid"/> 的自定义格式；<c>null</c> 表示使用 <c>"D"</c>。</summary>
        public string? GuidFormat { get; set; }

        /// <summary>枚举的渲染方式。</summary>
        public EnumFormat EnumFormat { get; set; } = EnumFormat.Name;

        // ── 并发 / 性能 ────────────────────────────────────────────────────────

        /// <summary>行的投递策略。</summary>
        public WriteMode WriteMode { get; set; } = WriteMode.Async;

        /// <summary>
        /// 每个生产者的缓冲大小。取值会向上取整到 2 的幂。单行不得超过这个大小，
        /// 除非缓冲扩容（而扩容只在缓冲完全排空时才会发生）。默认 64 KiB。
        /// </summary>
        public int SlotBufferBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// 生产者槽位的最大数量。超出此数的线程共用同一个带锁的溢出槽。默认 64。
        /// </summary>
        public int MaxProducerSlots { get; set; } = 64;

        /// <summary>
        /// 为容纳超大单行，槽位缓冲允许扩容到的上限。默认 16 MiB。
        /// </summary>
        public int MaxRowBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>持久化策略。</summary>
        public FlushPolicy Flush { get; set; } = FlushPolicy.Batch;

        /// <summary>写线程失败策略。</summary>
        public WriteErrorPolicy OnWriteError { get; set; } = WriteErrorPolicy.Throw;

        // ── 表头重写 ───────────────────────────────────────────────────────────

        /// <summary>为更长表头腾出空间而搬移文件内容时使用的块大小。</summary>
        public int ShiftChunkBytes { get; set; } = 1024 * 1024;

        /// <summary>表头记录允许占用的最大字节数。默认 1 MiB。</summary>
        public int MaxHeaderBytes { get; set; } = 1024 * 1024;

        /// <summary>就地扩展文件之前要求预留的可用空间余量。</summary>
        public int FreeSpaceMarginBytes { get; set; } = 16 * 1024 * 1024;

        /// <summary>校验本实例，遇到不合理的配置抛 <see cref="ArgumentException"/>。</summary>
        internal void Validate()
        {
            if (Encoding == null)
                throw new ArgumentNullException(nameof(Encoding));

            // 分隔符、引号、行终止符都是按单字节写进输出流的。UTF-16/UTF-32 会把 ASCII 字符
            // 编成多字节，写出的文件无法解析，所以必须在这里就拒绝，而不是等到生成垃圾文件。
            if (!CsvEncodings.IsSingleByteCompatible(Encoding))
                throw new ArgumentException(
                    $"Encoding {Encoding.WebName} (codepage {Encoding.CodePage}) is not single-byte compatible. " +
                    "CSV delimiters and line terminators are written as single bytes, so UTF-16/UTF-32 cannot be supported. " +
                    "Use UTF-8, GBK, Big5, Shift-JIS or another ASCII-compatible encoding.",
                    nameof(Encoding));

            byte q = QuoteCharByte;
            if (Delimiter == (byte)'\r' || Delimiter == (byte)'\n')
                throw new ArgumentException("Delimiter must not be CR or LF.", nameof(Delimiter));
            if (Delimiter == q)
                throw new ArgumentException("Delimiter must differ from QuoteChar.", nameof(Delimiter));

            // 双字节编码下，分隔符/引号必须不会出现在多字节字符的尾字节位置，
            // 否则按字节扫描会把汉字的半个字节当成分隔符，产出错位文件。
            if (!CsvEncodings.IsSyntaxByteSafe(Encoding, Delimiter))
                throw new ArgumentException(
                    $"Delimiter byte 0x{Delimiter:X2} can appear inside a multi-byte character " +
                    $"in encoding {Encoding.WebName}, which would corrupt records. Choose another delimiter.",
                    nameof(Delimiter));
            if (!CsvEncodings.IsSyntaxByteSafe(Encoding, q))
                throw new ArgumentException(
                    $"QuoteChar byte 0x{q:X2} can appear inside a multi-byte character " +
                    $"in encoding {Encoding.WebName}, which would corrupt records. Choose another quote character.",
                    nameof(QuoteChar));

            if (NewLine == null || NewLine.Length == 0 || NewLine.Length > 2)
                throw new ArgumentException("NewLine must be \"\\n\", \"\\r\" or \"\\r\\n\".", nameof(NewLine));
            bool nlOk = (NewLine.Length == 1 && (NewLine[0] == (byte)'\n' || NewLine[0] == (byte)'\r'))
                        || (NewLine.Length == 2 && NewLine[0] == (byte)'\r' && NewLine[1] == (byte)'\n');
            if (!nlOk)
                throw new ArgumentException("NewLine must be \"\\n\", \"\\r\" or \"\\r\\n\".", nameof(NewLine));

            if (SlotBufferBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(SlotBufferBytes));
            if (MaxProducerSlots <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxProducerSlots));
            if (MaxRowBytes < SlotBufferBytes)
                throw new ArgumentOutOfRangeException(nameof(MaxRowBytes), "MaxRowBytes must be >= SlotBufferBytes.");
            if (ShiftChunkBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(ShiftChunkBytes));
            if (MaxHeaderBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(MaxHeaderBytes));
        }

        /// <summary>该编码的 BOM 字节；没有 BOM 时为空。</summary>
        internal byte[] Bom
        {
            get
            {
                if (_bom != null) return _bom;

                byte[] preamble = Encoding.GetPreamble();
                if (preamble.Length > 0)
                {
                    _bom = preamble;
                }
                else if (CsvEncodings.IsUtf8(Encoding) && Encoding.CodePage == 65001)
                {
                    // 默认实例是 UTF8Encoding(false)，它的前导码是空的；但用户显式要求写 BOM 时，
                    // 意思就是要标准的 UTF-8 BOM。
                    _bom = new byte[] { 0xEF, 0xBB, 0xBF };
                }
                else
                {
                    _bom = Array.Empty<byte>();
                }

                return _bom;
            }
        }

        private byte[]? _bom;

        /// <summary>引号字符对应的字节（已校验为 ASCII）。</summary>
        internal byte QuoteCharByte
        {
            get
            {
                if (QuoteChar > 0x7F)
                    throw new ArgumentException("QuoteChar must be an ASCII character.", nameof(QuoteChar));
                return (byte)QuoteChar;
            }
        }

        /// <summary>把 <paramref name="bytes"/> 向上取整到 2 的幂，最小 256。</summary>
        internal static int RoundUpPowerOfTwo(int bytes)
        {
            int v = 256;
            while (v < bytes)
            {
                if (v > int.MaxValue / 2) return int.MaxValue / 2 + 1;
                v <<= 1;
            }
            return v;
        }
    }
}
