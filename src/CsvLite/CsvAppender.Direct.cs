using System;
using System.Threading;
using CsvLite.Internal;

namespace CsvLite
{
    public sealed partial class CsvAppender
    {
        // ── Direct（同步）模式 ─────────────────────────────────────────────────

        /// <summary>
        /// 在锁内直接写入文件流的暂存缓冲。写入错误就在此处、就在调用线程上抛出，
        /// 且全局记录顺序严格。
        /// </summary>
        private void AppendDirect(CsvRow row)
        {
            ReadOnlySpan<byte> record = row.Bytes;
            int total = record.Length + _dialect.NewLine.Length;

            if (total > _options.MaxRowBytes)
                throw new CsvRowTooLargeException(
                    $"The encoded record is {total} bytes, above MaxRowBytes ({_options.MaxRowBytes}).");

            lock (_fileGate)
            {
                if (_stagingLength > 0 && _stagingLength + total > _staging.Length) FlushStagingCore();
                EnsureStagingCapacity(_stagingLength + total);

                record.CopyTo(new Span<byte>(_staging, _stagingLength, record.Length));
                _stagingLength += record.Length;
                _dialect.NewLine.CopyTo(new Span<byte>(_staging, _stagingLength, _dialect.NewLine.Length));
                _stagingLength += _dialect.NewLine.Length;

                Interlocked.Add(ref _directAppended, total);

                if (_stagingLength >= _staging.Length) FlushStagingCore();
            }
        }

        /// <summary>把暂存的字节写出。可并发调用。</summary>
        private void FlushStaging()
        {
            lock (_fileGate) FlushStagingCore();
        }

        private void FlushStagingCore()
        {
            if (_stagingLength == 0) return;

            _stream.Write(_staging, 0, _stagingLength);
            _fileEnd += _stagingLength;
            Interlocked.Add(ref _directWritten, _stagingLength);
            _stagingLength = 0;

            if (_options.Flush == FlushPolicy.Sync) _stream.Flush(true);
        }

        /// <summary>确保暂存缓冲至少能容纳 <paramref name="needed"/> 字节。</summary>
        private void EnsureStagingCapacity(int needed)
        {
            if (_staging.Length >= needed) return;

            int size = _staging.Length;
            while (size < needed)
            {
                if (size >= _options.MaxRowBytes) throw new CsvRowTooLargeException(
                    $"The encoded record needs {needed} bytes, above MaxRowBytes ({_options.MaxRowBytes}).");
                size <<= 1;
            }

            byte[] next = new byte[size];
            Array.Copy(_staging, next, _stagingLength);
            _staging = next;
        }
    }
}
