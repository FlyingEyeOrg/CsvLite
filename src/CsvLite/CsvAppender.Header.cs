using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using CsvLite.Internal;

namespace CsvLite
{
    public sealed partial class CsvAppender
    {
        // ── 表头公开 API ───────────────────────────────────────────────────────

        /// <summary>
        /// 替换表头记录。<c>null</c> 等价于 <see cref="DeleteHeaders"/>。
        /// </summary>
        /// <remarks>
        /// 只改动文件的第一行。数据记录永远不会被重新对齐、补齐、截断，也不会拿新表头去校验它们
        /// —— 加一列不会给已有行加字段，删一列也不会删字段。两种结果都是合法的 CSV。
        /// </remarks>
        public void SetHeaders(IReadOnlyList<string>? headers,
                               IProgress<double>? progress = null,
                               CancellationToken cancellationToken = default)
        {
            if (headers == null)
            {
                DeleteHeaders(progress, cancellationToken);
                return;
            }

            byte[] line = HeaderCodec.Build(headers, _dialect);
            if (line.Length > _options.MaxHeaderBytes)
                throw new CsvHeaderTooLargeException(
                    $"The encoded header is {line.Length} bytes, above MaxHeaderBytes ({_options.MaxHeaderBytes}).");

            ApplyHeader(line, CopyOf(headers), progress, cancellationToken);
        }

        /// <summary>完全移除表头记录。</summary>
        public void DeleteHeaders(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            ApplyHeader(Array.Empty<byte>(), null, progress, cancellationToken);
        }

        /// <summary>
        /// 在 <paramref name="index"/> 处插入一个表头列。<c>index == HeaderCount</c>（或 -1）表示追加到末尾。
        /// 当前还没有表头时，行为等价于 <c>SetHeaders([name])</c>。
        /// </summary>
        public void AddHeaderColumn(string name, int index = -1,
                                    IProgress<double>? progress = null,
                                    CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> current = Headers ?? Array.Empty<string>();
            if (index < 0) index = current.Count;
            if (index > current.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"index must be between 0 and {current.Count}.");

            var next = new List<string>(current.Count + 1);
            for (int i = 0; i < current.Count; i++)
            {
                if (i == index) next.Add(name ?? string.Empty);
                next.Add(current[i]);
            }
            if (index == current.Count) next.Add(name ?? string.Empty);

            SetHeaders(next, progress, cancellationToken);
        }

        /// <summary>移除 <paramref name="index"/> 处的表头列。</summary>
        /// <remarks>移除最后一个剩余列时，表头记录本身也被移除。</remarks>
        public void RemoveHeaderColumn(int index,
                                       IProgress<double>? progress = null,
                                       CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> current = Headers ?? Array.Empty<string>();
            if (index < 0 || index >= current.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"index must be between 0 and {current.Count - 1}.");

            if (current.Count == 1)
            {
                DeleteHeaders(progress, cancellationToken);
                return;
            }

            var next = new List<string>(current.Count - 1);
            for (int i = 0; i < current.Count; i++)
            {
                if (i != index) next.Add(current[i]);
            }
            SetHeaders(next, progress, cancellationToken);
        }

        /// <summary>重命名单个表头列。</summary>
        public void SetHeaderColumn(int index, string name,
                                    IProgress<double>? progress = null,
                                    CancellationToken cancellationToken = default)
        {
            IReadOnlyList<string> current = Headers ?? Array.Empty<string>();
            if (index < 0 || index >= current.Count)
                throw new ArgumentOutOfRangeException(nameof(index),
                    $"index must be between 0 and {current.Count - 1}.");

            var next = new List<string>(current);
            next[index] = name ?? string.Empty;
            SetHeaders(next, progress, cancellationToken);
        }

        // ── 核心 ───────────────────────────────────────────────────────────────

        /// <summary>
        /// 用 <paramref name="newLine"/> 替换表头记录（空表示「没有表头」）。
        /// </summary>
        private void ApplyHeader(byte[] newLine, IReadOnlyList<string>? newHeaders,
                                 IProgress<double>? progress, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            ThrowIfFaulted();

            _adminGate.Wait(cancellationToken);
            try
            {
                bool async = _options.WriteMode == WriteMode.Async;

                // 顺序很重要：先排空（此时写线程还在跑），再暂停它。
                // 反过来会死锁 —— FlushPipeline 会去等一个已经被暂停的写线程。
                if (async) FlushPipeline();
                else FlushStaging();

                if (async) PauseWriter();
                try
                {
                    lock (_fileGate)
                    {
                        ReplaceHeaderCore(newLine, newHeaders, progress, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    progress?.Report(0);
                    throw;
                }
                finally
                {
                    if (async) ResumeWriter();
                }
            }
            finally
            {
                _adminGate.Release();
            }
        }

        private void ReplaceHeaderCore(byte[] newLine, IReadOnlyList<string>? newHeaders,
                                       IProgress<double>? progress, CancellationToken cancellationToken)
        {
            long fileLength = _stream.Length;
            long headerStart = _bomLength;
            byte[] oldHeader = Volatile.Read(ref _headerLine);
            long dataStart = headerStart + oldHeader.Length;
            long dataLength = fileLength - dataStart;

            if (dataLength < 0) dataLength = 0;   // 防御：文件在我们背后被截断过
            long delta = newLine.Length - oldHeader.Length;

            if (delta == 0 && newLine.Length > 0)
            {
                // 快路径：编码长度相同，就地覆盖。
                WriteHeaderAt(headerStart, newLine);
            }
            else if (delta == 0)
            {
                // 本来就没有表头，而「删除表头」正好等于什么都不做。
            }
            else if (dataLength == 0)
            {
                // 没有数据记录：截到表头区域，再写入新记录。
                _stream.SetLength(headerStart);
                if (newLine.Length > 0) WriteHeaderAt(headerStart, newLine);
            }
            else
            {
                ShiftData(dataStart, dataLength, delta, newLine, oldHeader, headerStart, progress, cancellationToken);
            }

            _stream.Seek(0, SeekOrigin.End);
            _stream.Flush();
            _fileEnd = _stream.Length;
            Volatile.Write(ref _headerLine, newLine);
            Volatile.Write(ref _headers, newHeaders);
        }

        private void WriteHeaderAt(long offset, byte[] line)
        {
            _stream.Seek(offset, SeekOrigin.Begin);
            _stream.Write(line, 0, line.Length);
        }

        /// <summary>
        /// 把数据区就地搬移 <paramref name="delta"/> 字节，并写入新表头。
        /// </summary>
        /// <remarks>
        /// 变长时从后往前搬尾部（否则会覆盖尚未读出的字节）；变短时从前往后搬头部。
        /// 无论哪种，这个搬移都是 memmove：拷贝顺序由源与目标的相对位置决定。
        /// 取消时通过 <paramref name="oldHeader"/> 加一次反向搬移，把文件逐字节恢复原样。
        /// </remarks>
        private void ShiftData(long dataStart, long dataLength, long delta, byte[] newLine, byte[] oldHeader,
                               long headerStart, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            int chunk = (int)Math.Min(Math.Max(4096, _options.ShiftChunkBytes), Math.Max(1, dataLength));
            byte[] buffer = new byte[chunk];
            bool growing = delta > 0;

            if (growing) EnsureFreeSpace(delta);

            long moved = 0;
            long originalLength = dataStart + dataLength;

            try
            {
                if (growing)
                {
                    _stream.SetLength(_stream.Length + delta);

                    long end = dataStart + dataLength;
                    long pos = end;
                    while (pos > dataStart)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int take = (int)Math.Min(buffer.Length, pos - dataStart);
                        long src = pos - take;
                        ReadAt(src, buffer, take);
                        WriteAt(src + delta, buffer, take);

                        moved += take;
                        pos = src;
                        progress?.Report((double)moved / dataLength);
                    }
                }
                else
                {
                    long drop = -delta;
                    long pos = dataStart;
                    long end = dataStart + dataLength;
                    while (pos < end)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        int take = (int)Math.Min(buffer.Length, end - pos);
                        ReadAt(pos, buffer, take);
                        WriteAt(pos - drop, buffer, take);

                        moved += take;
                        pos += take;
                        progress?.Report((double)moved / dataLength);
                    }

                    _stream.SetLength(_stream.Length - drop);
                }

                // 提交点：从这里开始，新表头才是权威的。
                if (newLine.Length > 0) WriteHeaderAt(headerStart, newLine);
            }
            catch (OperationCanceledException)
            {
                Rollback(dataStart, dataLength, delta, moved, growing, headerStart, oldHeader, originalLength);
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    Rollback(dataStart, dataLength, delta, moved, growing, headerStart, oldHeader, originalLength);
                }
                catch (Exception rollbackFailure)
                {
                    _failure ??= new WriteFailure(
                        new CsvFileCorruptedException(
                            "A header rewrite failed and the file could not be restored.", rollbackFailure));
                    throw new CsvFileCorruptedException(
                        "A header rewrite failed and the file could not be restored.", ex);
                }
                throw;
            }
        }

        /// <summary>
        /// 撤销一次未完成的搬移：把已经搬走的字节搬回去、恢复原始长度，并重写旧表头记录。
        /// 从未被触碰过的字节保持不动。
        /// </summary>
        private void Rollback(long dataStart, long dataLength, long delta, long moved, bool growing,
                              long headerStart, byte[] oldHeader, long originalLength)
        {
            if (moved > 0)
            {
                if (growing)
                {
                    // 已搬走的尾部现在高了 delta 字节，把它搬回来。
                    long end = dataStart + dataLength;
                    CopyRange(end - moved + delta, moved, end - moved);
                }
                else
                {
                    // 已搬走的头部现在低了 |delta| 字节，把它搬回去。
                    CopyRange(dataStart - (-delta), moved, dataStart);
                }
            }

            _stream.SetLength(originalLength);

            // 旧表头可能已被搬移弄脏了一部分，按原样重写回去。
            if (oldHeader.Length > 0) WriteHeaderAt(headerStart, oldHeader);

            _stream.Seek(0, SeekOrigin.End);
            _stream.Flush();
        }

        /// <summary>
        /// 在文件内把 <paramref name="count"/> 字节从 <paramref name="srcStart"/> 拷到
        /// <paramref name="dstStart"/>，并根据两个区间的相对位置选择迭代方向，
        /// 使区间可以重叠（memmove 语义）。
        /// </summary>
        private void CopyRange(long srcStart, long count, long dstStart)
        {
            if (count <= 0 || srcStart == dstStart) return;

            int bufferLength = (int)Math.Min(Math.Max(4096, _options.ShiftChunkBytes), count);
            byte[] buffer = new byte[bufferLength];

            if (dstStart > srcStart)
            {
                // 目标在源之上：从末尾往前拷。
                long done = 0;
                while (done < count)
                {
                    int take = (int)Math.Min(buffer.Length, count - done);
                    long src = srcStart + count - done - take;
                    ReadAt(src, buffer, take);
                    WriteAt(dstStart + count - done - take, buffer, take);
                    done += take;
                }
            }
            else
            {
                // 目标在源之下：从开头往后拷。
                long done = 0;
                while (done < count)
                {
                    int take = (int)Math.Min(buffer.Length, count - done);
                    long src = srcStart + done;
                    ReadAt(src, buffer, take);
                    WriteAt(dstStart + done, buffer, take);
                    done += take;
                }
            }
        }

        private void ReadAt(long position, byte[] buffer, int count)
        {
            _stream.Seek(position, SeekOrigin.Begin);
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0) throw new IOException("Unexpected end of file while shifting data.");
                read += n;
            }
        }

        private void WriteAt(long position, byte[] buffer, int count)
        {
            _stream.Seek(position, SeekOrigin.Begin);
            _stream.Write(buffer, 0, count);
        }

        private void EnsureFreeSpace(long delta)
        {
            try
            {
                string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(Path));
                if (string.IsNullOrEmpty(root)) return;

                var drive = new DriveInfo(root);
                if (!drive.IsReady) return;

                long required = delta + _options.FreeSpaceMarginBytes;
                if (drive.AvailableFreeSpace < required)
                    throw new IOException(
                        $"Not enough free space to grow the header by {delta} bytes " +
                        $"(need {required}, have {drive.AvailableFreeSpace}).");
            }
            catch (IOException)
            {
                throw;
            }
            catch
            {
                // DriveInfo 只是参考值：未知/网络/CoW 文件系统可能报得不准。
            }
        }

        private static string[] CopyOf(IReadOnlyList<string> headers)
        {
            var copy = new string[headers.Count];
            for (int i = 0; i < headers.Count; i++) copy[i] = headers[i] ?? string.Empty;
            return copy;
        }

        // ── 暂停写线程（管理员屏障） ───────────────────────────────────────────

        /// <summary>
        /// 挂起写线程，并等到能确定它已经退出了文件。
        /// 生产者<b>不</b>会被停下：它们只往自己的环里写，写线程恢复之后，
        /// 它们的字节会落在移位后的数据之后。
        /// </summary>
        /// <remarks>
        /// 这里用世代号握手而不是布尔量。布尔量会丢一次「恢复/暂停」配对：
        /// 当写线程恰好在 <c>paused</c> 被清除的瞬间观察到它，之后的一次暂停就会直接返回，
        /// 而写线程其实并没有被挂起。
        /// </remarks>
        private void PauseWriter()
        {
            if (_options.WriteMode != WriteMode.Async) return;
            if (_writerThread == null) return;

            int generation = Interlocked.Increment(ref _pauseGeneration);
            _writerWake.Set();

            SpinWait spin = default;
            while (Volatile.Read(ref _pausedAck) != generation)
            {
                if (spin.NextSpinWillYield) _writerParkedEvent.Wait(1);
                else spin.SpinOnce();
            }
            _writerParkedEvent.Reset();
        }

        private void ResumeWriter()
        {
            if (_options.WriteMode != WriteMode.Async) return;
            if (_writerThread == null) return;

            Interlocked.Exchange(ref _pauseGeneration, 0);
            _writerResumeEvent.Set();
        }

        /// <summary>写线程发现暂停请求时调用。</summary>
        private void AcknowledgePauseAndWait(int generation)
        {
            Volatile.Write(ref _pausedAck, generation);
            _writerParkedEvent.Set();

            _writerResumeEvent.Wait();
            _writerResumeEvent.Reset();
        }
    }
}
