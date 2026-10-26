using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CsvLite;

namespace CsvLite.Bench
{
    /// <summary>
    /// 手写的基准运行器。刻意不使用 BenchmarkDotNet：本项目必须能在离线的
    /// 构建代理上跑，而真正关心的指标（追加吞吐、每行分配、打开与表头耗时）
    /// 简单到可以直接测。
    /// </summary>
    internal static class Program
    {
        private const int WarmupRounds = 2;
        private const int MeasureRounds = 5;

        private static int Main(string[] args)
        {
            string filter = args.Length > 0 ? args[0] : string.Empty;
            Console.WriteLine($"CsvLite benchmark — {Environment.ProcessorCount} logical cores, .NET {Environment.Version}");
            Console.WriteLine();

            if (Wants("single")) BenchSingleThreadAppend();
            if (Wants("wide")) BenchWideRows();
            if (Wants("escape")) BenchEscapedFields();
            if (Wants("multi")) BenchMultiThreadAppend();
            if (Wants("open")) BenchOpenLargeFile();
            if (Wants("header")) BenchHeaderRename();
            if (Wants("alloc")) BenchAllocationsPerRow();

            Console.WriteLine("done.");
            return 0;

            bool Wants(string name) => filter.Length == 0 || filter.Contains(name, StringComparison.OrdinalIgnoreCase);
        }

        // ── 行的构成 ─────────────────────────────────────────────────────────

        private static void FillTypical(CsvRow row, int i)
        {
            // 这些取值是刻意挑的：要测的是*库本身*，而不是基准里的字符串拼接。
            row.Add(i);                              // int
            row.Add(1_700_000_000_000L);             // long
            row.Add(Names[i & 0xFF]);                // 短文本，无需转义
            row.Add(3.14159d);                       // double
            row.Add(i % 2 == 0);                     // bool
            row.Add(Timestamp);                      // 时间戳文本
            row.Add(Decimals[i & 0xFF]);             // decimal
            row.Add("tail");                         // 短文本
        }

        private static readonly string[] Names = BuildNames();
        private static readonly decimal[] Decimals = BuildDecimals();
        private const string Timestamp = "2026-10-26T10:00:00Z";

        private static string[] BuildNames()
        {
            var names = new string[256];
            for (int i = 0; i < names.Length; i++) names[i] = "name-" + i;
            return names;
        }

        private static decimal[] BuildDecimals()
        {
            var values = new decimal[256];
            for (int i = 0; i < values.Length; i++) values[i] = i * 0.25m;
            return values;
        }

        // ── 基准项 ───────────────────────────────────────────────────────────

        private static void BenchSingleThreadAppend()
        {
            const int rows = 1_000_000;
            var options = new CsvOptions { WriteMode = WriteMode.Direct, SlotBufferBytes = 256 * 1024 };

            Measure("Append, 1 thread, 8 typed fields", rows, () =>
            {
                string path = Temp();
                try
                {
                    using var appender = CsvAppender.Create(path, options: options);
                    for (int i = 0; i < rows; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        FillTypical(row, i);
                        appender.Append(row);
                    }
                    appender.Flush();
                    return new FileInfo(path).Length;
                }
                finally
                {
                    Delete(path);
                }
            });
        }

        /// <summary>
        /// 宽行：此时 "GB/s" 才是有意义的指标，因为每行的固定开销被大载荷摊薄了。
        /// 窄行测的是相反的情况，因此改用 行/秒 来报告。
        /// </summary>
        private static void BenchWideRows()
        {
            const int rows = 200_000;
            var payload = new string('x', 4000);
            var options = new CsvOptions { WriteMode = WriteMode.Direct };

            Measure("Append, 1 thread, 4 KB payload/row", rows, () =>
            {
                string path = Temp();
                try
                {
                    using var appender = CsvAppender.Create(path, options: options);
                    for (int i = 0; i < rows; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add(payload);
                        appender.Append(row);
                    }
                    appender.Flush();
                    return new FileInfo(path).Length;
                }
                finally
                {
                    Delete(path);
                }
            });
        }

        private static void BenchEscapedFields()
        {
            const int rows = 300_000;
            var options = new CsvOptions { WriteMode = WriteMode.Direct };

            Measure("Append, fields needing escaping", rows, () =>
            {
                string path = Temp();
                try
                {
                    using var appender = CsvAppender.Create(path, options: options);
                    for (int i = 0; i < rows; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add("plain");
                        row.Add("has,comma and \"quotes\"");
                        row.Add("multi\nline");
                        row.Add("plain2");
                        appender.Append(row);
                    }
                    appender.Flush();
                    return new FileInfo(path).Length;
                }
                finally
                {
                    Delete(path);
                }
            });
        }

        private static void BenchMultiThreadAppend()
        {
            foreach (int threads in new[] { 2, 4, 8 })
            {
                int threadCount = threads;
                const int rowsPerThread = 400_000;
                long totalRows = (long)threadCount * rowsPerThread;

                Measure($"Append, {threadCount} threads, async writer", totalRows, () =>
                {
                    string path = Temp();
                    try
                    {
                        using var appender = CsvAppender.Create(path);
                        var tasks = new Task[threadCount];
                        for (int t = 0; t < threadCount; t++)
                        {
                            tasks[t] = Task.Factory.StartNew(() =>
                            {
                                for (int i = 0; i < rowsPerThread; i++)
                                {
                                    CsvRow row = appender.CreateRow();
                                    FillTypical(row, i);
                                    appender.Append(row);
                                }
                            }, TaskCreationOptions.LongRunning);
                        }
                        Task.WaitAll(tasks);
                        appender.Flush();
                        return new FileInfo(path).Length;
                    }
                    finally
                    {
                        Delete(path);
                    }
                });
            }
        }

        private static void BenchOpenLargeFile()
        {
            string path = Temp();
            try
            {
                Console.Write("  preparing ~200 MB file ... ");
                using (var appender = CsvAppender.Create(path, new[] { "a", "b", "c", "d" }))
                {
                    var payload = new string('x', 180);
                    const int rows = 1_000_000;
                    for (int i = 0; i < rows; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add(payload);
                        row.Add(i);
                        row.Add(i);
                        appender.Append(row);
                    }
                    appender.Flush();
                }
                long size = new FileInfo(path).Length;
                Console.WriteLine($"{size / (1024.0 * 1024.0):F1} MB");

                var samples = new List<double>();
                for (int i = 0; i < 50; i++)
                {
                    var sw = Stopwatch.StartNew();
                    using (CsvAppender.Open(path))
                    {
                        sw.Stop();
                    }
                    samples.Add(sw.Elapsed.TotalMilliseconds);
                }
                samples.Sort();
                Console.WriteLine($"  Open()  median {samples[samples.Count / 2]:F3} ms   p95 {samples[(int)(samples.Count * 0.95)]:F3} ms");
                Console.WriteLine();
            }
            finally
            {
                Delete(path);
            }
        }

        private static void BenchHeaderRename()
        {
            string path = Temp();
            try
            {
                using (var appender = CsvAppender.Create(path,
                           new[] { "column_one", "column_two", "column_three" }))
                {
                    for (int i = 0; i < 100_000; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add(i);
                        row.Add(i);
                        appender.Append(row);
                    }
                    appender.Flush();
                }

                var samples = new List<double>();
                using (var appender = CsvAppender.Open(path))
                {
                    for (int i = 0; i < 300; i++)
                    {
                        var sw = Stopwatch.StartNew();
                        appender.SetHeaders(new[] { "column_ONE", "column_TWO", "column_THREE" });
                        sw.Stop();
                        samples.Add(sw.Elapsed.TotalMilliseconds * 1000.0);
                    }
                }
                samples.Sort();
                Console.WriteLine(
                    $"SetHeaders, same length (fast path)  median {samples[samples.Count / 2]:F1} µs   p95 {samples[(int)(samples.Count * 0.95)]:F1} µs");
                Console.WriteLine();
            }
            finally
            {
                Delete(path);
            }
        }

        private static void BenchAllocationsPerRow()
        {
            string path = Temp();
            try
            {
                var options = new CsvOptions { WriteMode = WriteMode.Direct, SlotBufferBytes = 1024 * 1024 };
                using var appender = CsvAppender.Create(path, options: options);

                for (int i = 0; i < 2000; i++)
                {
                    CsvRow row = appender.CreateRow();
                    FillTypical(row, i);
                    appender.Append(row);
                }
                appender.Flush();

                const int rows = 200_000;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < rows; i++)
                {
                    CsvRow row = appender.CreateRow();
                    FillTypical(row, i);
                    appender.Append(row);
                }
                long after = GC.GetAllocatedBytesForCurrentThread();

                Console.WriteLine(
                    $"Allocations per Append (typed fields)  {(after - before) / (double)rows:F1} B/row   " +
                    $"total {(after - before) / 1024.0:F0} KB");
                Console.WriteLine();
            }
            finally
            {
                Delete(path);
            }
        }

        // ── 基础设施 ─────────────────────────────────────────────────────────

        private delegate long Round();

        /// <summary>先跑预热轮次，再正式测量。每个测量轮次返回它产出的字节数。</summary>
        private static void Measure(string title, long rows, Round round)
        {
            for (int i = 0; i < WarmupRounds; i++) round();

            var samples = new List<(double Gbps, double RowsPerSec, double Seconds)>();
            for (int i = 0; i < MeasureRounds; i++)
            {
                var sw = Stopwatch.StartNew();
                long bytes = round();
                sw.Stop();

                double seconds = sw.Elapsed.TotalSeconds;
                samples.Add((
                    bytes / 1024.0 / 1024.0 / 1024.0 / seconds,
                    rows / seconds,
                    seconds));
            }

            samples.Sort((a, b) => a.Gbps.CompareTo(b.Gbps));
            var median = samples[samples.Count / 2];
            Console.WriteLine(
                $"{title,-40} median {median.Gbps,7:F2} GB/s   {median.RowsPerSec / 1e6,8:F2} M rows/s   ({median.Seconds * 1000:F0} ms)");
            Console.WriteLine();
        }

        /// <summary>
        /// 基准临时文件写在仓库下的 <c>tmp/bench</c>，不占系统盘。
        /// 从程序目录向上找 <c>CsvLite.sln</c> 定位仓库根。
        /// </summary>
        private static string Temp()
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "CsvLite.sln")))
                {
                    string target = Path.Combine(dir, "tmp", "bench");
                    Directory.CreateDirectory(target);
                    return Path.Combine(target, "bench-" + Guid.NewGuid().ToString("N") + ".csv");
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            string fallback = Path.Combine(AppContext.BaseDirectory, "tmp", "bench");
            Directory.CreateDirectory(fallback);
            return Path.Combine(fallback, "bench-" + Guid.NewGuid().ToString("N") + ".csv");
        }

        private static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 尽力而为
            }
        }
    }
}
