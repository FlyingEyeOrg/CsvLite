using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CsvLite;

namespace CsvLite.Tests
{
    /// <summary>
    /// 文档承诺的内存上限是 <c>SlotBufferBytes × MaxProducerSlots</c> 的未写数据。这些
    /// 测试证明该上限确实生效，而且——更关键地——证明强制该上限不会让写线程死锁，
    /// 因为写线程需要的那把溢出锁生产者也会去拿。
    /// </summary>
    internal static class BackpressureTests
    {
        [Test]
        internal static void SlowConsumerDoesNotGrowMemoryWithoutBound()
        {
            string path = TestHelper.TempPath("bpbound");
            try
            {
                // 极小的环形缓冲、极少的槽位：队列上限小到可以被观察到。
                var options = new CsvOptions
                {
                    SlotBufferBytes = 1024,
                    MaxProducerSlots = 2,
                    MaxRowBytes = 64 * 1024,
                };

                const int threads = 8;
                const int perThread = 20_000;

                using (var appender = CsvAppender.Create(path, new[] { "t", "i" }, options))
                {
                    // 线程数远多于槽位数，因此会重点跑溢出路径（以及它的锁）。
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

                    // 如果背压死锁了，这个 join 永远不会返回。
                    bool completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(120));
                    Assert.True(completed, "Producers did not finish: backpressure deadlocked.");

                    appender.Flush();
                }

                Assert.Equal(threads * perThread + 1, TestHelper.Parse(TestHelper.ReadAllText(path)).Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void BackpressureWithHeaderRewriteDoesNotDeadlock()
        {
            string path = TestHelper.TempPath("bphdr");
            try
            {
                var options = new CsvOptions { SlotBufferBytes = 1024, MaxProducerSlots = 2 };
                const int threads = 6;
                const int perThread = 4000;

                using (var appender = CsvAppender.Create(path, new[] { "aa", "bb" }, options))
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

                    // 在生产者的背压状态下重写表头。
                    for (int k = 0; k < 3; k++)
                    {
                        appender.SetHeaders(new[] { "aa" + k, "bb" });
                        Thread.Sleep(5);
                    }

                    bool completed = Task.WaitAll(tasks, TimeSpan.FromSeconds(120));
                    Assert.True(completed, "Header rewrite under backpressure deadlocked.");

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
        internal static void PendingBytesStaysSaneUnderLoad()
        {
            string path = TestHelper.TempPath("bppending");
            try
            {
                var options = new CsvOptions { SlotBufferBytes = 4096, MaxProducerSlots = 4 };
                using (var appender = CsvAppender.Create(path, new[] { "i" }, options))
                {
                    const int threads = 4;
                    const int perThread = 10_000;
                    var tasks = new Task[threads];
                    for (int t = 0; t < threads; t++)
                    {
                        tasks[t] = Task.Factory.StartNew(() =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                CsvRow row = appender.CreateRow();
                                row.Add(i);
                                appender.Append(row);
                            }
                        }, TaskCreationOptions.LongRunning);
                    }

                    Task.WaitAll(tasks, TimeSpan.FromSeconds(120));
                    appender.Flush();

                    Assert.Equal(0, appender.PendingBytes);
                    Assert.Equal(appender.BytesAppended, appender.BytesWritten);
                    Assert.False(appender.IsFaulted);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }
}
