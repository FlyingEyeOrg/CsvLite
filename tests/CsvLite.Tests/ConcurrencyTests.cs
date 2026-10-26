using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsvLite;

namespace CsvLite.Tests
{
    internal static class ConcurrencyTests
    {
        [Test]
        internal static void ManyThreadsProduceEveryRowExactlyOnce()
        {
            string path = TestHelper.TempPath("conc");
            const int threads = 8;
            const int rowsPerThread = 5000;

            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "thread", "seq", "payload" }))
                {
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < rowsPerThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                row.Add("v" + i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }

                    Task.WaitAll(tasks);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * rowsPerThread + 1, records.Count); // + 表头

                var seen = new HashSet<(int, int)>();
                for (int i = 1; i < records.Count; i++)
                {
                    List<string> record = records[i];
                    if (record.Count != 3)
                        throw new AssertFailedException($"Record {i} has {record.Count} fields: torn record.");

                    int threadId = int.Parse(record[0]);
                    int seq = int.Parse(record[1]);
                    if (record[2] != "v" + seq)
                        throw new AssertFailedException(
                            $"Record {i} payload mismatch: thread={threadId} seq={seq} payload={record[2]}");

                    if (!seen.Add((threadId, seq)))
                        throw new AssertFailedException($"Duplicate record thread={threadId} seq={seq}");
                }

                Assert.Equal(threads * rowsPerThread, seen.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void PerThreadOrderIsPreserved()
        {
            string path = TestHelper.TempPath("order");
            const int threads = 6;
            const int rowsPerThread = 3000;

            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "t", "seq" }))
                {
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < rowsPerThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }
                    Task.WaitAll(tasks);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                var last = new Dictionary<int, int>();
                for (int i = 1; i < records.Count; i++)
                {
                    int threadId = int.Parse(records[i][0]);
                    int seq = int.Parse(records[i][1]);
                    if (last.TryGetValue(threadId, out int previous))
                    {
                        if (seq <= previous)
                            throw new AssertFailedException(
                                $"Thread {threadId} rows out of order: {previous} then {seq}");
                    }
                    last[threadId] = seq;
                }

                Assert.Equal(threads, last.Count);
                foreach (int value in last.Values) Assert.Equal(rowsPerThread - 1, value);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void FlushMakesEverythingVisible()
        {
            string path = TestHelper.TempPath("flush");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" }))
                {
                    for (int i = 0; i < 100; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        appender.Append(row);
                    }

                    appender.Flush();

                    Assert.Equal(0, appender.PendingBytes);
                    Assert.Equal(appender.BytesAppended, appender.BytesWritten);
                }

                Assert.Equal(101, TestHelper.Lines(path).Length);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ConcurrentFlushAndAppendDoNotInterfere()
        {
            string path = TestHelper.TempPath("flushconc");
            const int threads = 4;
            const int perThread = 2000;

            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "t", "i" }))
                {
                    var writers = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        writers[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }

                    var flusher = Task.Factory.StartNew(() =>
                    {
                        while (!writers.All(w => w.IsCompleted))
                        {
                            appender.Flush();
                            Thread.Sleep(1);
                        }
                    }, TaskCreationOptions.LongRunning);

                    Task.WaitAll(writers);
                    flusher.Wait();
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * perThread + 1, records.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderRewriteDuringConcurrentAppendDoesNotCorruptRows()
        {
            string path = TestHelper.TempPath("hdrconc");
            const int threads = 4;
            const int perThread = 1500;

            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "aaaa", "bbbb" }))
                {
                    var writers = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        writers[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                appender.Append(row);
                                if (i % 200 == 0) Thread.Sleep(1);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }

                    // 在写入数据行的同时做等长改名（快路径）。
                    for (int k = 0; k < 5; k++)
                    {
                        appender.SetHeaders(new[] { "hdr" + k + "x", "bbbb" });
                        Thread.Sleep(2);
                    }

                    Task.WaitAll(writers);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * perThread + 1, records.Count);

                for (int i = 1; i < records.Count; i++)
                {
                    if (records[i].Count != 2)
                        throw new AssertFailedException($"Record {i} has {records[i].Count} fields.");
                    int threadId = int.Parse(records[i][0]);
                    int seq = int.Parse(records[i][1]);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void GrowingHeaderShiftDuringConcurrentAppendKeepsAllRows()
        {
            string path = TestHelper.TempPath("hdrgrow");
            const int threads = 3;
            const int perThread = 800;

            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }))
                {
                    var writers = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        writers[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add("payload-" + i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }

                    Thread.Sleep(5);
                    // 长度发生变化：在生产者的持续追加下强制执行就地移位。
                    appender.SetHeaders(new[] { "a_much_longer_column_name", "b" });

                    Task.WaitAll(writers);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * perThread + 1, records.Count);
                Assert.SequenceEqual(new[] { "a_much_longer_column_name", "b" }, records[0]);

                var seen = new HashSet<(int, int)>();
                for (int i = 1; i < records.Count; i++)
                {
                    if (records[i].Count != 2)
                        throw new AssertFailedException($"Record {i} has {records[i].Count} fields: torn.");
                    seen.Add((int.Parse(records[i][0]), int.Parse(records[i][1].Replace("payload-", ""))));
                }
                Assert.Equal(threads * perThread, seen.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void LargeRowsAreNotTorn()
        {
            string path = TestHelper.TempPath("large");
            try
            {
                // 远大于 4 KiB 槽位缓冲的行，用来跑扩容路径。
                var options = new CsvOptions { SlotBufferBytes = 4096, MaxRowBytes = 4 * 1024 * 1024 };
                string big = new string('z', 20000);

                using (var appender = CsvAppender.Create(path, new[] { "id", "payload" }, options))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add(big + i);
                        appender.Append(row);
                    }
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(21, records.Count);
                for (int i = 0; i < 20; i++)
                {
                    Assert.Equal(i.ToString(), records[i + 1][0]);
                    Assert.Equal(big + i, records[i + 1][1]);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DirectModeIsOrderedAndImmediate()
        {
            string path = TestHelper.TempPath("direct");
            try
            {
                var options = new CsvOptions { WriteMode = WriteMode.Direct };
                using (var appender = CsvAppender.Create(path, new[] { "i" }, options))
                {
                    for (int i = 0; i < 500; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        appender.Append(row);
                    }
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(501, records.Count);
                for (int i = 0; i < 500; i++) Assert.Equal(i.ToString(), records[i + 1][0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ManyThreadsBeyondSlotLimitStillWork()
        {
            string path = TestHelper.TempPath("overflow");
            const int threads = 12;
            const int perThread = 500;

            try
            {
                // 槽位数量刻意少于线程数：多出来的线程共享溢出槽。
                var options = new CsvOptions { MaxProducerSlots = 2, SlotBufferBytes = 1024 };
                using (var appender = CsvAppender.Create(path, new[] { "t", "i" }, options))
                {
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }
                    Task.WaitAll(tasks);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * perThread + 1, records.Count);

                var seen = new HashSet<(int, int)>();
                for (int i = 1; i < records.Count; i++)
                {
                    if (records[i].Count != 2)
                        throw new AssertFailedException($"Record {i} torn: {records[i].Count} fields.");
                    seen.Add((int.Parse(records[i][0]), int.Parse(records[i][1])));
                }
                Assert.Equal(threads * perThread, seen.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void BackpressureDoesNotLoseOrDuplicationRows()
        {
            string path = TestHelper.TempPath("bp");
            const int perThread = 20000;
            const int threads = 4;

            try
            {
                var options = new CsvOptions { SlotBufferBytes = 1024, MaxProducerSlots = 2 };
                using (var appender = CsvAppender.Create(path, new[] { "t", "i" }, options))
                {
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        int id = t;
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(id);
                                row.Add(i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }
                    Task.WaitAll(tasks);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(threads * perThread + 1, records.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DisposeFlushesPendingData()
        {
            string path = TestHelper.TempPath("disp");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" }))
                {
                    for (int i = 0; i < 50; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        appender.Append(row);
                    }
                }

                Assert.Equal(51, TestHelper.Lines(path).Length);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DisposeIsIdempotent()
        {
            string path = TestHelper.TempPath("dispidem");
            try
            {
                var appender = CsvAppender.Create(path);
                appender.Dispose();
                appender.Dispose();
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void UseAfterDisposeThrows()
        {
            string path = TestHelper.TempPath("disposed");
            try
            {
                var appender = CsvAppender.Create(path);
                appender.Dispose();
                Assert.Throws<ObjectDisposedException>(() => appender.CreateRow());
                Assert.Throws<ObjectDisposedException>(() => appender.Flush());
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }
}
