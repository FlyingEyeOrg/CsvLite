using System;
using System.Collections.Generic;
using System.Threading;
using CsvLite.Internal;

namespace CsvLite
{
    public sealed partial class CsvAppender
    {
        // ── 异步生产者路径 ─────────────────────────────────────────────────────

        /// <summary>
        /// 把一条记录追加到本线程的环形缓冲。
        /// </summary>
        /// <remarks>
        /// 专属槽位只属于一个线程（槽位下标每线程只绑定一次），所以热路径不加锁，
        /// 也不对共享状态做任何 interlocked 操作：它只往环里写，并用 release 存储发布新的 head。
        /// 每个槽位的字节计数器同样是单写者，因此总量是惰性求和，而不是靠争用来维护。
        /// <para>
        /// 溢出槽被所有超出 <see cref="CsvOptions.MaxProducerSlots"/> 的线程共享，所以在那里
        /// 容量检查与写入必须落在同一个临界区内：在锁外检查容量会让两个线程都判定「还有空位」，
        /// 然后各自提交，把 <c>_head</c> 推过缓冲末端，覆盖写线程尚未消费的字节。
        /// </para>
        /// </remarks>
        private void AppendAsync(CsvRow row)
        {
            ReadOnlySpan<byte> record = row.Bytes;
            int total = record.Length + _dialect.NewLine.Length;

            SlotBinding binding = GetBinding();
            ProducerSlot slot = binding.Slot;

            if (!binding.IsOverflow)
            {
                if (slot.CanFit(total))
                {
                    WriteRecord(slot, record, total);
                    WaitForDrainIfNeeded();
                    return;
                }
                AppendSlowDedicated(slot, record, total);
                return;
            }

            bool written;
            lock (_overflowLock)
            {
                written = slot.CanFit(total);
                if (written) WriteRecord(slot, record, total);
            }
            if (written)
            {
                // 背压必须在 _overflowLock 之外施加：写线程排空溢出槽也要拿这把锁，
                // 持锁等它就会死锁。
                WaitForDrainIfNeeded();
                return;
            }
            AppendSlowShared(record, total);
        }

        private void WriteRecord(ProducerSlot slot, ReadOnlySpan<byte> record, int total)
        {
            int start = slot.ReserveStart();
            slot.CopyIn(start, record);
            slot.CopyInAt((start + record.Length) & (slot.Capacity - 1), _dialect.NewLine);
            slot.Advance(total);
        }

        /// <summary>专属槽位的慢路径：超大记录就扩容，否则等待。</summary>
        private void AppendSlowDedicated(ProducerSlot slot, ReadOnlySpan<byte> record, int total)
        {
            if (total > _options.MaxRowBytes)
                throw new CsvRowTooLargeException(
                    $"The encoded record is {total} bytes, above MaxRowBytes ({_options.MaxRowBytes}).");

            SpinWait spin = default;
            while (true)
            {
                ThrowIfFaulted();

                if (slot.CanFit(total))
                {
                    WriteRecord(slot, record, total);
                    WaitForDrainIfNeeded();
                    return;
                }

                // 已排空的环可以直接扩容；这是唯一会分配内存的路径。
                if (slot.IsEmpty && total > slot.Capacity && slot.TryGrow(total, _options.MaxRowBytes))
                    continue;

                BackpressureWait(slot, ref spin);
            }
        }

        /// <summary>共享溢出槽的慢路径：逻辑相同，但每一步都在槽位锁内完成。</summary>
        private void AppendSlowShared(ReadOnlySpan<byte> record, int total)
        {
            if (total > _options.MaxRowBytes)
                throw new CsvRowTooLargeException(
                    $"The encoded record is {total} bytes, above MaxRowBytes ({_options.MaxRowBytes}).");

            SpinWait spin = default;
            while (true)
            {
                ThrowIfFaulted();

                // 绝不要在持有 _overflowLock 时调用 WaitForDrainIfNeeded()：写线程排空本槽
                // 也需要这把锁，持锁阻塞会让双方互等。
                bool fits;
                lock (_overflowLock)
                {
                    ProducerSlot slot = _overflowSlot!;
                    fits = slot.CanFit(total);
                    if (fits)
                    {
                        WriteRecord(slot, record, total);
                    }
                    else if (slot.IsEmpty && total > slot.Capacity)
                    {
                        // 已排空：扩容是安全的，而且不用等待就能推进。
                        slot.TryGrow(total, _options.MaxRowBytes);
                    }
                }

                if (fits)
                {
                    WaitForDrainIfNeeded();
                    return;
                }

                BackpressureWait(_overflowSlot!, ref spin);
            }
        }

        private void BackpressureWait(ProducerSlot slot, ref SpinWait spin)
        {
            // 只有环确实紧张时才去唤醒写线程，而不是每次重试都唤。
            if (spin.Count >= 64) _writerWake.Set();

            // 环满载一段时间后就让出 CPU；写线程是唯一的出路。
            if (spin.Count >= 256) Thread.Sleep(0);
            else if (spin.Count >= 1024) Thread.Sleep(1);

            spin.SpinOnce();
        }

        /// <summary>
        /// 当尚未写出的数据总量超过配置的排队上限时阻塞。
        /// </summary>
        /// <remarks>
        /// 每个槽位的环本身就能约束内存，但上限只是 <c>SlotBufferBytes × 活跃槽数</c>；
        /// 磁盘慢加上生产者多，总量仍会慢慢涨上去。这个方法让
        /// <see cref="CsvOptions.SlotBufferBytes"/> × <see cref="CsvOptions.MaxProducerSlots"/>
        /// 这个文档里承诺的界限真正成立，而不只是一句口号。
        /// <para>
        /// 以记录为粒度调用，而非逐字节，并且当写线程跟得上时会立刻返回
        /// （只有在接近上限时才会去查 <c>SumPublishedBytes</c>）。
        /// </para>
        /// </remarks>
        private void WaitForDrainIfNeeded()
        {
            long limit = MaxQueuedBytes;
            if (limit <= 0) return;

            SpinWait spin = default;
            while (true)
            {
                ThrowIfFaulted();

                long pending = SumPublishedBytes() - SumWrittenBytes();
                if (pending <= limit) return;

                _writerWake.Set();
                if (spin.NextSpinWillYield) Thread.Sleep(1);
                else spin.SpinOnce();
            }
        }

        private SlotBinding GetBinding()
        {
            SlotBinding? binding = _binding.Value;
            if (binding != null) return binding;

            int index = Interlocked.Increment(ref _nextSlot) - 1;
            bool overflow = index >= _slots.Length;
            ProducerSlot slot = overflow ? GetOrCreateOverflowSlot() : GetOrCreateSlot(index);

            binding = new SlotBinding(slot, overflow);
            _binding.Value = binding;
            return binding;
        }

        private ProducerSlot GetOrCreateSlot(int index)
        {
            ProducerSlot? existing = Volatile.Read(ref _slots[index]);
            if (existing != null) return existing;

            lock (_slotSync)
            {
                existing = _slots[index];
                if (existing != null) return existing;

                int capacity = CsvOptions.RoundUpPowerOfTwo(_options.SlotBufferBytes);
                var created = new ProducerSlot(capacity, _options.MaxRowBytes);
                Volatile.Write(ref _slots[index], created);
                return created;
            }
        }

        private ProducerSlot GetOrCreateOverflowSlot()
        {
            ProducerSlot? existing = _overflowSlot;
            if (existing != null) return existing;

            lock (_overflowLock)
            {
                existing = _overflowSlot;
                if (existing != null) return existing;
                int capacity = CsvOptions.RoundUpPowerOfTwo(_options.SlotBufferBytes);
                var created = new ProducerSlot(capacity, _options.MaxRowBytes);
                _overflowSlot = created;
                return created;
            }
        }

        // ── 写线程 ─────────────────────────────────────────────────────────────

        private void StartWriterIfNeeded()
        {
            if (_options.WriteMode == WriteMode.Async) EnsureWriterRunning();
        }

        private void EnsureWriterRunning()
        {
            if (_writerThread != null) return;
            lock (_slotSync)
            {
                if (_writerThread != null || _stopWriter) return;
                var thread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "CsvLite writer",
                };
                _writerThread = thread;
                thread.Start();
            }
        }

        private void WriterLoop()
        {
            try
            {
                while (!_stopWriter)
                {
                    int generation = Volatile.Read(ref _pauseGeneration);
                    if (generation != 0)
                    {
                        AcknowledgePauseAndWait(generation);
                        continue;
                    }

                    int drained = DrainOnce();
                    if (drained > 0)
                    {
                        _writerProgress.Set();
                        continue;
                    }

                    _writerWake.Reset();
                    // 重置之后再复查一遍，这样与重置竞争的发布动作不会被漏掉。
                    if (AnyPending()) continue;
                    if (Volatile.Read(ref _pauseGeneration) != 0) continue;
                    _writerWake.Wait(_stopWriter ? 0 : 4);
                }
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private bool AnyPending()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                ProducerSlot? slot = Volatile.Read(ref _slots[i]);
                if (slot != null && !slot.IsEmpty) return true;
            }
            ProducerSlot? overflow = _overflowSlot;
            return overflow != null && !overflow.IsEmpty;
        }

        /// <summary>写出所有槽位当前已发布的数据，返回写出的字节数。</summary>
        private int DrainOnce()
        {
            int total = 0;
            bool sync = _options.Flush == FlushPolicy.Sync;

            for (int i = 0; i < _slots.Length; i++)
            {
                ProducerSlot? slot = Volatile.Read(ref _slots[i]);
                if (slot != null) total += DrainSlot(slot, sync);
            }

            ProducerSlot? overflowSlot = _overflowSlot;
            if (overflowSlot != null)
            {
                lock (_overflowLock) total += DrainSlot(overflowSlot, sync);
            }

            return total;
        }

        private int DrainSlot(ProducerSlot slot, bool sync)
        {
            long tail = slot.Tail;
            long head = slot.PublishedBytes;
            if (head == tail) return 0;

            int written;
            lock (_fileGate)
            {
                written = slot.WriteOut(_stream, tail, head);
                if (sync) _stream.Flush(true);
            }

            slot.CommitTail(head);
            Interlocked.Add(ref _fileEnd, written);
            return written;
        }

        // ── 字节记账 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 所有生产者已发布的字节总数，由各槽位计数器求和得到。
        /// 刻意做成非规范化的：共享一个计数器会让所有生产者的写入都压在同一条缓存行上。
        /// </summary>
        private long SumPublishedBytes()
        {
            long total = _overflowSlot?.PublishedBytes ?? 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                ProducerSlot? slot = Volatile.Read(ref _slots[i]);
                if (slot != null) total += slot.PublishedBytes;
            }
            return total;
        }

        private long SumWrittenBytes()
        {
            long total = _overflowSlot?.WrittenBytes ?? 0;
            for (int i = 0; i < _slots.Length; i++)
            {
                ProducerSlot? slot = Volatile.Read(ref _slots[i]);
                if (slot != null) total += slot.WrittenBytes;
            }
            return total;
        }

        // ── 刷盘屏障 ───────────────────────────────────────────────────────────

        /// <summary>
        /// 等到本次调用之前发布的每一个字节都已写入文件。调用之后才发布的字节无关紧要：
        /// 它们本来就属于屏障点之后。
        /// </summary>
        /// <remarks>
        /// 目标值在等待<b>之前</b>采样，而已写入计数器只增不减，因此条件是单调的，
        /// 不会因为某个生产者不停发布而死转。
        /// </remarks>
        private void FlushPipeline()
        {
            long target = SumPublishedBytes();
            EnsureWriterRunning();
            _writerWake.Set();

            SpinWait spin = default;
            while (true)
            {
                if (SumWrittenBytes() >= target) return;
                if (_failure != null) ThrowIfFaulted();

                if (spin.NextSpinWillYield)
                {
                    _writerProgress.Wait(1);
                }
                else
                {
                    spin.SpinOnce();
                }
            }
        }

        private void StopWriter()
        {
            _stopWriter = true;
            _writerWake.Set();
            Thread? thread = _writerThread;
            if (thread != null) thread.Join(TimeSpan.FromSeconds(30));
        }
    }
}
