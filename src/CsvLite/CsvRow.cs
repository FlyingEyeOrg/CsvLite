using System;
using System.Buffers;
using CsvLite.Internal;

namespace CsvLite
{
    /// <summary>
    /// 一条正在构造中的 CSV 记录。字段在加入时就完成编码与转义，
    /// 因此调用方永远不必亲自处理分隔符、引号或换行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 行是池化的：用 <see cref="CsvAppender.CreateRow"/> 取一个，通过 <see cref="CsvAppender.Append"/>
    /// 交还，它会自动回收。在交出之后再使用该行会抛 <see cref="InvalidOperationException"/>，
    /// 而不是悄悄写出垃圾数据。
    /// </para>
    /// <para>
    /// 行携带创建它的 appender 的方言，因此来自某个 appender 的行不能追加到另一个 appender。
    /// </para>
    /// <para>
    /// 同一个行实例不得由两个线程同时使用。
    /// </para>
    /// </remarks>
    public sealed class CsvRow
    {
        private const int InitialCapacity = 256;

        private byte[] _buf;
        private int _length;
        private int _fieldCount;
        private bool _recycled;

        internal CsvRow(CsvAppender owner, Dialect dialect)
        {
            Owner = owner;
            Dialect = dialect;
            _buf = ArrayPool<byte>.Shared.Rent(InitialCapacity);
        }

        internal CsvAppender Owner { get; }

        internal Dialect Dialect { get; }

        /// <summary>到目前为止已加入的字段数。</summary>
        public int FieldCount => _fieldCount;

        /// <summary>字段占用的 UTF-8 字节数，不含记录终止符。</summary>
        public int ByteCount => _length;

        /// <summary>一旦该行被交给 <see cref="CsvAppender.Append"/> 并回收，此值为 true。</summary>
        public bool IsRecycled => _recycled;

        /// <summary>底层缓冲的当前容量（供拥有者的池化策略判断）。</summary>
        internal int BufferLength => _buf.Length;

        /// <summary>已编码的字段，不含记录终止符。</summary>
        internal ReadOnlySpan<byte> Bytes => new ReadOnlySpan<byte>(_buf, 0, _length);

        /// <summary>清空所有字段，以便复用该行。</summary>
        public void Clear()
        {
            ThrowIfRecycled();
            _length = 0;
            _fieldCount = 0;
        }

        // ── 文本 ───────────────────────────────────────────────────────────────

        /// <summary>加入一个文本字段。<c>null</c> 产生一个空字段。</summary>
        public void Add(string? text)
        {
            ThrowIfRecycled();
            if (text == null)
            {
                AddEmpty();
                return;
            }
            BeginField();
            int start = _length;
            int max = Utf8Compat.GetMaxByteCount(Dialect.Encoding, text.Length);
            int written = Utf8Compat.GetBytes(Dialect.Encoding, text.AsSpan(), Slice(start, max));
            _length = start + written;
            FinishTextField(start, written);
        }

        /// <summary>用字符跨度加入一个文本字段。</summary>
        public void Add(ReadOnlySpan<char> text)
        {
            ThrowIfRecycled();
            BeginField();
            int start = _length;
            int max = Utf8Compat.GetMaxByteCount(Dialect.Encoding, text.Length);
            int written = Utf8Compat.GetBytes(Dialect.Encoding, text, Slice(start, max));
            _length = start + written;
            FinishTextField(start, written);
        }

        /// <summary>加入一个已经是 UTF-8 的字段。字节内容不做校验。</summary>
        public void Add(ReadOnlySpan<byte> utf8)
        {
            ThrowIfRecycled();
            BeginField();
            int start = _length;
            if (utf8.Length > 0)
            {
                utf8.CopyTo(Slice(start, utf8.Length));
                _length = start + utf8.Length;
            }
            FinishTextField(start, utf8.Length);
        }

        /// <summary>加入单个字符作为一个字段。</summary>
        public void Add(char value)
        {
            ThrowIfRecycled();
            BeginField();
            int start = _length;
            Span<char> one = stackalloc char[1];
            one[0] = value;
            int written = Utf8Compat.GetBytes(Dialect.Encoding, one, Slice(start, Utf8Compat.GetMaxByteCount(Dialect.Encoding, 1)));
            _length = start + written;
            FinishTextField(start, written);
        }

        // ── 基础类型 ───────────────────────────────────────────────────────────

        /// <summary>加入布尔值，输出 <c>true</c> 或 <c>false</c>。</summary>
        public void Add(bool value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatBoolean(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入有符号字节。</summary>
        public void Add(sbyte value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatSByte(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 16 位有符号整数。</summary>
        public void Add(short value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatInt16(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 32 位有符号整数。</summary>
        public void Add(int value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatInt32(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 64 位有符号整数。</summary>
        public void Add(long value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatInt64(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入无符号字节。</summary>
        public void Add(byte value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatByte(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 16 位无符号整数。</summary>
        public void Add(ushort value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatUInt16(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 32 位无符号整数。</summary>
        public void Add(uint value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatUInt32(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 64 位无符号整数。</summary>
        public void Add(ulong value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatUInt64(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入最短往返表示的 <see cref="float"/>。</summary>
        public void Add(float value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatSingle(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入最短往返表示的 <see cref="double"/>。</summary>
        public void Add(double value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatDouble(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 <see cref="decimal"/>。</summary>
        public void Add(decimal value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatDecimal(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        // ── 时间 / 标识 ────────────────────────────────────────────────────────

        /// <summary>加入 <see cref="DateTime"/>。默认格式为 ISO 8601 往返格式。</summary>
        public void Add(DateTime value)
        {
            ThrowIfRecycled();
            string? fmt = Dialect.DateTimeFormat;
            if (fmt != null)
            {
                Add(ValueFormatter.FormatDateTimeCustom(value, fmt));
                return;
            }
            BeginField();
            ValueFormatter.TryFormatDateTime(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 <see cref="DateTimeOffset"/>。默认格式为 ISO 8601 往返格式。</summary>
        public void Add(DateTimeOffset value)
        {
            ThrowIfRecycled();
            string? fmt = Dialect.DateTimeFormat;
            if (fmt != null)
            {
                Add(ValueFormatter.FormatDateTimeOffsetCustom(value, fmt));
                return;
            }
            BeginField();
            ValueFormatter.TryFormatDateTimeOffset(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>以常量格式加入 <see cref="TimeSpan"/>。</summary>
        public void Add(TimeSpan value)
        {
            ThrowIfRecycled();
            BeginField();
            ValueFormatter.TryFormatTimeSpan(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入 <see cref="Guid"/>。默认格式为带连字符形式。</summary>
        public void Add(Guid value)
        {
            ThrowIfRecycled();
            string? fmt = Dialect.GuidFormat;
            if (fmt != null)
            {
                Add(ValueFormatter.FormatGuidCustom(value, fmt));
                return;
            }
            BeginField();
            ValueFormatter.TryFormatGuid(value, NumberBuffer(out int start), out int w);
            _length = start + w;
            _fieldCount++;
        }

        /// <summary>加入枚举值，遵循 <see cref="CsvOptions.EnumFormat"/>。</summary>
        public void Add(Enum value)
        {
            ThrowIfRecycled();
            if (value == null)
            {
                AddEmpty();
                return;
            }
            if (Dialect.EnumFormat == EnumFormat.Numeric)
            {
                BeginField();
                ValueFormatter.TryFormatEnumNumeric(value, NumberBuffer(out int start), out int w);
                _length = start + w;
                _fieldCount++;
                return;
            }
            Add(value.ToString());
        }

#if NET8_0_OR_GREATER
        /// <summary>加入 <see cref="DateOnly"/>（仅 net8.0+）。</summary>
        public void Add(DateOnly value)
        {
            ThrowIfRecycled();
            Add(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        /// <summary>加入 <see cref="TimeOnly"/>（仅 net8.0+）。</summary>
        public void Add(TimeOnly value)
        {
            ThrowIfRecycled();
            Add(value.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }
#endif

        // ── 空字段 ─────────────────────────────────────────────────────────────

        /// <summary>加入一个空字段（等价于 <c>Add((string?)null)</c>）。</summary>
        public void AddNull()
        {
            ThrowIfRecycled();
            AddEmpty();
        }

        // ── 内部实现 ───────────────────────────────────────────────────────────

        private void AddEmpty()
        {
            BeginField();
            if (Dialect.QuoteMode == QuoteMode.EveryField)
            {
                // 在 EveryField 模式下，空字段也要变成 ""。
                EnsureCapacity(_length + 2);
                byte quote = Dialect.QuoteCharByte;
                _buf[_length++] = quote;
                _buf[_length++] = quote;
            }
            _fieldCount++;
        }

        private void BeginField()
        {
            if (_fieldCount > 0)
            {
                EnsureCapacity(_length + 1);
                _buf[_length++] = Dialect.Delimiter;
            }
        }

        /// <summary>
        /// 在确保容量之后，返回 <paramref name="offset"/> 处 <paramref name="count"/> 字节的跨度。
        /// 在调用方推进 <see cref="_length"/> 之前，这些字节在逻辑上尚未提交。
        /// </summary>
        private Span<byte> Slice(int offset, int count)
        {
            EnsureCapacity(offset + count);
            return new Span<byte>(_buf, offset, count);
        }

        private Span<byte> NumberBuffer(out int start)
        {
            EnsureCapacity(_length + ValueFormatter.MaxNumericBytes);
            start = _length;
            return new Span<byte>(_buf, _length, ValueFormatter.MaxNumericBytes);
        }

        /// <summary>
        /// 对已编码好、位于 <c>[start, start+length)</c> 的字段施加引号处理，并把字段计数加一。
        /// </summary>
        private void FinishTextField(int start, int length)
        {
            byte quote = Dialect.QuoteCharByte;
            bool mustQuote;

            if (Dialect.QuoteMode == QuoteMode.EveryField)
            {
                mustQuote = true;
            }
            else if (length == 0)
            {
                mustQuote = false;
            }
            else
            {
                ReadOnlySpan<byte> field = new ReadOnlySpan<byte>(_buf, start, length);
                mustQuote = !FieldEscape.IsClean(field, Dialect.Delimiter, quote);
            }

            if (mustQuote)
            {
                ReadOnlySpan<byte> field = new ReadOnlySpan<byte>(_buf, start, length);
                int quotes = FieldEscape.CountQuotes(field, quote);
                int quotedLength = FieldEscape.QuotedLength(length, quotes);
                EnsureCapacity(start + quotedLength);
                _length = start + length;                 // 让载荷对就地加引号逻辑可见
                FieldEscape.QuoteInPlace(_buf, start, length, quotes, quote);
                _length = start + quotedLength;
            }

            _fieldCount++;
        }

        /// <summary>
        /// 在当前末尾预留 <paramref name="count"/> 个可写字节并返回它们。
        /// 这些字节归调用方所有，<see cref="_length"/> 必须由调用方自己推进。
        /// </summary>
        /// <remarks>
        /// 把 <c>EnsureCapacity</c> 与长度更新拆开，调用方才能区分「逻辑上已提交」和「物理上已预留」，
        /// 这正是「先编码、再视情况加引号」这套流程不需要二次拷贝的原因。
        /// </remarks>
        private Span<byte> Reserve(int count)
        {
            EnsureCapacity(_length + count);
            return new Span<byte>(_buf, _length, count);
        }

        /// <summary>编码文本前先物理预留空间，返回可供编码的跨度。</summary>
        private Span<byte> ReserveWorstCase(int charCount)
        {
            int max = Utf8Compat.GetMaxByteCount(Dialect.Encoding, charCount);
            EnsureCapacity(_length + max);
            return new Span<byte>(_buf, _length, max);
        }

        private void EnsureCapacity(int required)
        {
            if (_buf.Length >= required) return;

            int size = _buf.Length;
            while (size < required)
            {
                if (size > Dialect.MaxRowBytes)
                    throw new CsvRowTooLargeException(
                        $"Row exceeds MaxRowBytes ({Dialect.MaxRowBytes} bytes).");
                size <<= 1;
            }

            byte[] next = ArrayPool<byte>.Shared.Rent(size);
            Array.Copy(_buf, 0, next, 0, _length);
            ArrayPool<byte>.Shared.Return(_buf);
            _buf = next;
        }

        private void ThrowIfRecycled()
        {
            if (_recycled)
                throw new InvalidOperationException(
                    "This CsvRow has already been appended and returned to the pool. Call CreateRow() for a new one.");
        }

        /// <summary>当行被交还以便复用时，由 appender 调用。</summary>
        internal void Reset(bool recycled)
        {
            _length = 0;
            _fieldCount = 0;
            _recycled = recycled;
        }

        /// <summary>释放租来的缓冲（appender 被释放时使用）。</summary>
        internal void Release()
        {
            byte[] buf = _buf;
            _buf = Array.Empty<byte>();
            _recycled = true;
            if (buf.Length > 0) ArrayPool<byte>.Shared.Return(buf);
        }

        /// <summary>把行交还给拥有者的池。由 <see cref="CsvAppender.Append"/> 调用。</summary>
        internal void ReturnToPool()
        {
            _length = 0;
            _fieldCount = 0;
            _recycled = true;
            Owner.ReturnRow(this);
        }
    }
}
