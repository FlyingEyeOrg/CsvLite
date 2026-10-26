using System;
using System.IO;
using System.Threading;

namespace CsvLite.Internal
{
    /// <summary>
    /// 单生产者 / 单消费者的环形字节缓冲，承载某一个生产者线程的行编码字节流。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 拥有该槽位的线程是 <c>_head</c> 的唯一写者；写线程是 <c>_tail</c> 与 <c>_writtenBytes</c> 的唯一写者。
    /// 拷完字节后用 release 写发布 <c>_head</c>，消费者再用 acquire 读把它读回来，从而看见这一行。
    /// x86/x64 上 release 存储是免费的，所以稳态下每行的成本就是一次普通存储加 <c>memcpy</c> 本身
    /// —— 没有 interlocked 操作，也没有共享的可写缓存行。
    /// </para>
    /// <para>
    /// 这个缓冲是真正的环形：记录可以跨越末尾，两侧都用 <c>position &amp; mask</c> 计算偏移，
    /// 因此任何一侧都不会给另一侧留下未初始化的空隙去读。
    /// </para>
    /// </remarks>
    internal sealed class ProducerSlot
    {
        private readonly object _sync = new object();
        private readonly int _minCapacity;
        private readonly int _maxCapacity;

        private byte[] _buf;
        private int _mask;
        private long _head;         // 生产者写，消费者读
        private long _tail;         // 消费者写，生产者读
        private long _writtenBytes; // 消费者写，任何人读
        private long _tailSeen;     // 生产者缓存的 _tail 快照

        internal ProducerSlot(int initialCapacity, int maxCapacity)
        {
            _minCapacity = initialCapacity;
            _maxCapacity = maxCapacity;
            _buf = new byte[initialCapacity];
            _mask = initialCapacity - 1;
        }

        /// <summary>生产者到目前为止已发布的字节数。</summary>
        internal long PublishedBytes => Volatile.Read(ref _head);

        /// <summary>已经写入文件的字节数。</summary>
        internal long WrittenBytes => Volatile.Read(ref _writtenBytes);

        internal long Tail => Volatile.Read(ref _tail);

        internal long PendingBytes => Volatile.Read(ref _head) - Volatile.Read(ref _writtenBytes);

        internal bool IsEmpty => Volatile.Read(ref _head) == Volatile.Read(ref _tail);

        internal int Capacity => _buf.Length;

        /// <summary>当前是否还能再装下 <paramref name="length"/> 字节（不扩容）。</summary>
        internal bool CanFit(int length)
        {
            long head = _head;
            if (head - _tailSeen + length > _buf.Length)
            {
                _tailSeen = Volatile.Read(ref _tail);
            }
            return head - _tailSeen + length <= _buf.Length;
        }

        /// <summary>要拷入的起始偏移。先调 <see cref="CopyIn"/> / <see cref="CopyInAt"/>，再调 <see cref="Advance"/>。</summary>
        internal int ReserveStart() => (int)(_head & _mask);

        /// <summary>把记录拷进环形缓冲的 <paramref name="start"/> 处，必要时回绕。</summary>
        internal void CopyIn(int start, ReadOnlySpan<byte> source)
        {
            int capacity = _buf.Length;
            int first = Math.Min(source.Length, capacity - start);
            source.Slice(0, first).CopyTo(new Span<byte>(_buf, start, first));
            if (first < source.Length)
            {
                source.Slice(first).CopyTo(new Span<byte>(_buf, 0, source.Length - first));
            }
        }

        /// <summary>在指定偏移处拷贝一小段，必要时回绕。</summary>
        internal void CopyInAt(int offset, ReadOnlySpan<byte> source)
        {
            int capacity = _buf.Length;
            int first = Math.Min(source.Length, capacity - offset);
            source.Slice(0, first).CopyTo(new Span<byte>(_buf, offset, first));
            if (first < source.Length)
            {
                source.Slice(first).CopyTo(new Span<byte>(_buf, 0, source.Length - first));
            }
        }

        /// <summary>提交 <paramref name="length"/> 字节，把它们发布给消费者。</summary>
        internal void Advance(int length) => Volatile.Write(ref _head, _head + length);

        /// <summary>
        /// 扩容环形缓冲，使单条 <paramref name="length"/> 字节的记录能被放下。
        /// 只有在整个环完全排空时才合法。
        /// </summary>
        internal bool TryGrow(int length, int maxBytes)
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _head) != Volatile.Read(ref _tail)) return false;

                int target = _minCapacity;
                while (target < length)
                {
                    if (target >= maxBytes) return false;
                    target <<= 1;
                }
                if (target > maxBytes || target == _buf.Length) return false;

                _buf = new byte[target];
                _mask = target - 1;
                _head = 0;
                _tail = 0;
                _writtenBytes = 0;
                _tailSeen = 0;
                return true;
            }
        }

        /// <summary>排空后缩回配置的最小容量。由消费者调用。</summary>
        internal void TryShrink()
        {
            lock (_sync)
            {
                if (_buf.Length == _minCapacity) return;
                if (Volatile.Read(ref _head) != 0) return;
                if (Volatile.Read(ref _head) != Volatile.Read(ref _tail)) return;

                _buf = new byte[_minCapacity];
                _mask = _minCapacity - 1;
                _tailSeen = 0;
            }
        }

        /// <summary>
        /// 把 <c>[tail, head)</c> 区间写入 <paramref name="stream"/>，但不释放这段空间。
        /// 调用方在自己的记账成功之后再调 <see cref="CommitTail"/> 提交。
        /// </summary>
        internal int WriteOut(Stream stream, long tail, long head)
        {
            long remaining = head - tail;
            if (remaining <= 0) return 0;

            byte[] buf = _buf;
            int capacity = buf.Length;
            int start = (int)(tail & _mask);
            int total = 0;

            while (remaining > 0)
            {
                int chunk = (int)Math.Min(remaining, capacity - start);
                stream.Write(buf, start, chunk);
                total += chunk;
                remaining -= chunk;
                start = 0;
            }

            return total;
        }

        /// <summary>释放已由 <see cref="WriteOut"/> 写出的字节。</summary>
        internal void CommitTail(long head)
        {
            Volatile.Write(ref _writtenBytes, head);
            Volatile.Write(ref _tail, head);
        }

        /// <summary>丢弃所有排队数据（写失败且不可恢复后使用）。</summary>
        internal void Discard() => Volatile.Write(ref _tail, Volatile.Read(ref _head));
    }
}
