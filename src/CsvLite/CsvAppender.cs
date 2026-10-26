using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CsvLite.Internal;
namespace CsvLite
{
    /// <summary>
    /// 把 CSV 记录追加到文件。appender 只是文件流之上的一层薄句柄：它从不把文件读进内存，
    /// 所以打开 14 GB 文件和打开 14 KB 文件的代价一样。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Append 提交到缓冲，而不是提交到磁盘。</b> 在 <see cref="WriteMode.Async"/>（默认）下，
    /// 行被拷进每线程的环形缓冲，由后台线程写出；当字节必须真正抵达文件时，请调用 <see cref="Flush()"/>。
    /// 写入失败会在下一次 <c>Append</c>/<c>Flush</c> 调用时暴露，除非
    /// <see cref="CsvOptions.OnWriteError"/> 被设为 <see cref="WriteErrorPolicy.Stop"/>。
    /// </para>
    /// <para>
    /// <b>顺序。</b> 同一线程追加的行严格保持顺序。跨线程时只在记录边界处交错 —— 一条记录永远不会被
    /// 拆开 —— 但不同线程之间的相对顺序未做规定。
    /// </para>
    /// <para>
    /// 除 <see cref="Dispose()"/> 之外的所有成员都可以并发调用。
    /// </para>
    /// </remarks>
    public sealed partial class CsvAppender : IDisposable
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        , IAsyncDisposable
#endif
    {
        private readonly CsvOptions _options;
        private readonly Dialect _dialect;
        private readonly FileStream _stream;

        private readonly object _fileGate = new object();      // 所有对 FileStream 的改动都经此锁
        private readonly object _disposeSync = new object();
        private readonly object _slotSync = new object();
        private readonly SemaphoreSlim _adminGate = new SemaphoreSlim(1, 1);

        private readonly ProducerSlot?[] _slots;
        private readonly object _overflowLock = new object();
        private readonly ThreadLocal<SlotBinding?> _binding;
        private readonly ThreadLocal<Stack<CsvRow>?> _rowPool;
        private ProducerSlot? _overflowSlot;

        private readonly ManualResetEventSlim _writerProgress = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _writerWake = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _writerParkedEvent = new ManualResetEventSlim(false);
        private readonly ManualResetEventSlim _writerResumeEvent = new ManualResetEventSlim(false);

        private volatile WriteFailure? _failure;
        private volatile bool _disposed;
        private volatile bool _stopWriter;
        private int _pauseGeneration;
        private int _pausedAck;
        private int _nextSlot;
        private Thread? _writerThread;

        private byte[] _headerLine = Array.Empty<byte>();   // 已编码的表头记录，含终止符，不含 BOM
        private IReadOnlyList<string>? _headers;
        private long _bomLength;

        private byte[] _staging;                            // Direct 模式的缓冲
        private int _stagingLength;                         // 仅 Direct 模式使用

        // Async 模式把字节计数放在各自的 ProducerSlot 里（见 CsvAppender.Append.cs）；
        // 用单一共享计数器会让所有生产者的写入都落在同一条缓存行上。
        // Direct 模式只有一个控制线程，普通计数器在那里是正确的。
        private long _directAppended;
        private long _directWritten;
        private long _fileEnd;                              // 实际已在文件中的字节数
        private long _rowsAppended;

        private CsvAppender(string path, CsvOptions options, FileStream stream)
        {
            Path = path;
            _options = options;
            _dialect = new Dialect(options, options.QuoteCharByte);
            _stream = stream;
            _staging = new byte[Math.Max(4096, options.SlotBufferBytes)];
            _slots = new ProducerSlot?[options.MaxProducerSlots];
            _binding = new ThreadLocal<SlotBinding?>(() => null);
            _rowPool = new ThreadLocal<Stack<CsvRow>?>(() => null);
        }

        // ── 工厂方法 ───────────────────────────────────────────────────────────

        /// <summary>创建（或截断）文件，并可选地写入表头记录。</summary>
        public static CsvAppender Create(string path, IReadOnlyList<string>? headers = null, CsvOptions? options = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            options ??= new CsvOptions();
            options.Validate();

            if (File.Exists(path) && !options.Overwrite)
                throw new IOException($"File already exists: {path}. Pass CsvOptions.Overwrite = true to replace it.");

            // 先登记再打开：如果路径已被占用，在这里抛出就不能留下一个刚打开又没人管的句柄。
            OpenPathRegistry.Register(path);

            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite,
                                        bufferSize: 0, FileOptions.None);
            }
            catch
            {
                OpenPathRegistry.Unregister(path);
                throw;
            }

            var appender = new CsvAppender(path, options, stream);
            try
            {
                // BOM 由编码自身决定：UTF-8/UTF-16 有，GBK 等 ANSI 编码没有。
                if (options.WriteBom)
                {
                    byte[] bom = options.Bom;
                    if (bom.Length > 0)
                    {
                        stream.Write(bom, 0, bom.Length);
                        appender._bomLength = bom.Length;
                    }
                }

                if (options.HasHeader && headers != null)
                {
                    appender._headerLine = HeaderCodec.Build(headers, appender._dialect);
                    appender._headers = CopyOf(headers);
                    stream.Write(appender._headerLine, 0, appender._headerLine.Length);
                }

                appender._fileEnd = stream.Length;
                appender.StartWriterIfNeeded();
                return appender;
            }
            catch
            {
                stream.Dispose();
                OpenPathRegistry.Unregister(path);
                throw;
            }
        }

        /// <summary>打开已有文件（不存在则创建空文件）以追加数据。</summary>
        public static CsvAppender Open(string path, CsvOptions? options = null)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            options ??= new CsvOptions();
            options.Validate();

            // 先登记再打开：如果路径已被占用，在这里抛出就不能留下一个刚打开又没人管的句柄。
            OpenPathRegistry.Register(path);

            // OpenOrCreate 加显式 Seek：FileMode.Append 会禁止 Seek，而重写表头必须能跳到文件头。
            // 用 FileShare.ReadWrite（而不是 Read），这样 Excel、`tail -f` 这类外部读者仍能打开文件；
            // 单写者安全性由进程内的 OpenPathRegistry 保证。
            FileStream stream;
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite,
                                        bufferSize: 0, FileOptions.None);
            }
            catch
            {
                OpenPathRegistry.Unregister(path);
                throw;
            }

            var appender = new CsvAppender(path, options, stream);
            try
            {
                long length = stream.Length;

                // 用配置的编码自己的 BOM 探测前缀；GBK 没有 BOM，这里自然是 0。
                byte[] bom = options.Bom;
                if (bom.Length > 0 && length >= bom.Length)
                {
                    var probe = new byte[bom.Length];
                    stream.Seek(0, SeekOrigin.Begin);
                    int read = stream.Read(probe, 0, probe.Length);
                    if (read == bom.Length && probe.AsSpan(0, read).SequenceEqual(bom))
                        appender._bomLength = bom.Length;
                }

                if (options.HasHeader && length > appender._bomLength)
                {
                    byte[] line = ReadHeaderLine(stream, appender._bomLength, options.MaxHeaderBytes);
                    appender._headerLine = line;
                    appender._headers = HeaderCodec.Parse(line, options.Encoding, options.Delimiter, options.QuoteCharByte);
                }

                // E1：上一个写入进程在记录中途被杀。没有这一步，下一次追加会把两行粘成一行。
                stream.Seek(0, SeekOrigin.End);
                if (stream.Length > appender._bomLength)
                {
                    long last = stream.Length - 1;
                    stream.Seek(last, SeekOrigin.Begin);
                    int b = stream.ReadByte();
                    if (b != '\n' && b != '\r')
                    {
                        stream.Seek(0, SeekOrigin.End);
                        stream.Write(appender._dialect.NewLine, 0, appender._dialect.NewLine.Length);
                    }
                }

                // E2：以 HasHeader 打开的空文件（或只有 BOM 的文件）还没有表头记录。这是合法的：
                // 调用方之后可以调 SetHeaders，它会在 BOM 偏移处插入一行记录。
                stream.Seek(0, SeekOrigin.End);
                appender._fileEnd = stream.Length;
                appender.StartWriterIfNeeded();
                return appender;
            }
            catch
            {
                stream.Dispose();
                OpenPathRegistry.Unregister(path);
                throw;
            }
        }

        private static byte[] ReadHeaderLine(FileStream stream, long offset, int maxBytes)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new List<byte>(256);
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) break;
                buffer.Add((byte)b);
                if (b == '\n') break;
                if (buffer.Count > maxBytes)
                    throw new CsvHeaderTooLargeException(
                        $"The header record exceeds MaxHeaderBytes ({maxBytes} bytes).");
            }
            return buffer.ToArray();
        }

        // ── 公开状态 ───────────────────────────────────────────────────────────

        /// <summary>正在追加的文件的完整路径。</summary>
        public string Path { get; }

        /// <summary>当前的表头字段；没有表头记录时为 <c>null</c>。</summary>
        public IReadOnlyList<string>? Headers => Volatile.Read(ref _headers);

        /// <summary>表头列数；没有表头记录时为 0。</summary>
        public int HeaderCount => Headers?.Count ?? 0;

        /// <summary>本实例已追加的记录数。</summary>
        public long RowCount => Interlocked.Read(ref _rowsAppended);

        /// <summary>已交给写入路径的字节数。</summary>
        public long BytesAppended => _options.WriteMode == WriteMode.Direct
            ? Interlocked.Read(ref _directAppended)
            : SumPublishedBytes();

        /// <summary>已写入文件流的字节数。</summary>
        public long BytesWritten => _options.WriteMode == WriteMode.Direct
            ? Interlocked.Read(ref _directWritten)
            : SumWrittenBytes();

        /// <summary>已缓冲但尚未写入的字节数。</summary>
        public long PendingBytes => Math.Max(0, BytesAppended - BytesWritten);

        /// <summary>把缓冲全部刷出之后的文件长度。</summary>
        public long FileLength => Interlocked.Read(ref _fileEnd) + PendingBytes;

        /// <summary>后台写入一旦失败，此值为 true。</summary>
        public bool IsFaulted => _failure != null;

        /// <summary>第一个后台写入失败异常（如果有）。</summary>
        public Exception? LastError => _failure?.Exception;

        /// <summary>生产者当前是否会因等待空间而阻塞。</summary>
        public bool IsBackpressured
        {
            get
            {
                if (_options.WriteMode == WriteMode.Direct) return false;
                return PendingBytes > MaxQueuedBytes;
            }
        }

        private long MaxQueuedBytes => (long)_options.SlotBufferBytes * _options.MaxProducerSlots;

        // ── 行 ─────────────────────────────────────────────────────────────────

        /// <summary>取得一个绑定到本 appender 方言的池化行。</summary>
        public CsvRow CreateRow()
        {
            ThrowIfDisposed();
            return RentRow();
        }

        /// <summary>
        /// 追加一行。在 async 模式下，记录被拷进调用线程的环形缓冲，行随即被回收；
        /// 之后不要再触碰它。
        /// </summary>
        public void Append(CsvRow row)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));
            ThrowIfDisposed();
            ThrowIfFaulted();

            if (!ReferenceEquals(row.Owner, this))
                throw new InvalidOperationException("The row was created by a different CsvAppender.");
            if (row.IsRecycled)
                throw new InvalidOperationException("The row has already been appended; call CreateRow() for a new one.");

            if (_options.WriteMode == WriteMode.Direct) AppendDirect(row);
            else AppendAsync(row);

            Interlocked.Increment(ref _rowsAppended);
            row.ReturnToPool();
        }

        /// <summary>
        /// 把行交还给本 appender 的空闲链表。由 <see cref="CsvRow.ReturnToPool"/> 调用。
        /// </summary>
        /// <remarks>
        /// 行是每线程的临时工作区，所以每线程一个池就够了，还能让交接避开任何共享缓存行。
        /// 为容纳超大行而扩容过的缓冲会被直接丢弃而不是缓存下来，
        /// 免得一条超大记录在 appender 的整个生命周期里都占着内存。
        /// </remarks>
        internal void ReturnRow(CsvRow row)
        {
            if (_disposed)
            {
                row.Release();
                return;
            }

            if (row.BufferLength > MaxCachedRowBytes)
            {
                row.Release();
                return;
            }
            _rowPool.Value!.Push(row);
        }

        private CsvRow RentRow()
        {
            Stack<CsvRow>? pool = _rowPool.Value;
            if (pool == null)
            {
                pool = new Stack<CsvRow>(4);
                _rowPool.Value = pool;
            }

            if (pool.Count > 0)
            {
                CsvRow pooled = pool.Pop();
                pooled.Reset(recycled: false);
                return pooled;
            }
            return new CsvRow(this, _dialect);
        }

        /// <summary>回收的行缓冲在多大以上就不再缓存、直接丢弃。</summary>
        private const int MaxCachedRowBytes = 64 * 1024;

        // ── 刷盘 / 释放 ────────────────────────────────────────────────────────

        /// <summary>把缓冲中的所有内容写入文件。</summary>
        public void Flush() => Flush(fsync: false);

        /// <summary>把缓冲中的所有内容写入文件，可选地强制落盘。</summary>
        public void Flush(bool fsync)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();

            if (_options.WriteMode == WriteMode.Direct)
            {
                FlushStaging();
                if (fsync)
                {
                    lock (_fileGate) _stream.Flush(true);
                }
                return;
            }

            FlushPipeline();
            if (fsync)
            {
                lock (_fileGate) _stream.Flush(true);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_disposeSync)
            {
                if (_disposed) return;
            }

            try
            {
                if (_options.FlushOnDispose && _failure == null)
                {
                    try
                    {
                        if (_options.WriteMode == WriteMode.Direct) FlushStaging();
                        else FlushPipeline();
                    }
                    catch (Exception ex)
                    {
                        _failure ??= new WriteFailure(ex);
                    }
                }

                _disposed = true;
                StopWriter();
            }
            finally
            {
                try
                {
                    lock (_fileGate)
                    {
                        if (_options.FlushOnDispose)
                        {
                            if (_options.FsyncOnDispose) _stream.Flush(true);
                            else _stream.Flush();
                        }
                    }
                }
                catch (Exception ex)
                {
                    _failure ??= new WriteFailure(ex);
                }

                try { _stream.Dispose(); } catch { /* Dispose 不允许抛异常 */ }
                OpenPathRegistry.Unregister(Path);

                _binding.Dispose();
                _rowPool.Dispose();
                _writerProgress.Dispose();
                _writerWake.Dispose();
                _adminGate.Dispose();
            }
        }

#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
#endif

        // ── 内部实现 ───────────────────────────────────────────────────────────

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CsvAppender));
        }

        private void ThrowIfFaulted()
        {
            WriteFailure? failure = _failure;
            if (failure == null) return;
            if (_options.OnWriteError == WriteErrorPolicy.Stop) return;
            throw new CsvWriteException("A background write to the CSV file failed.", failure.Exception);
        }

        private void Fail(Exception ex)
        {
            if (_failure == null) _failure = new WriteFailure(ex);
            _writerProgress.Set();
        }

        private sealed class WriteFailure
        {
            internal WriteFailure(Exception exception) => Exception = exception;
            internal Exception Exception { get; }
        }

        private sealed class SlotBinding
        {
            internal SlotBinding(ProducerSlot slot, bool overflow)
            {
                Slot = slot;
                IsOverflow = overflow;
            }

            internal ProducerSlot Slot { get; }
            internal bool IsOverflow { get; }
        }
    }
}
