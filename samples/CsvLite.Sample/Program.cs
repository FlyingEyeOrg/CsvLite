using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsvLite;

namespace CsvLite.Sample
{
    internal static class Program
    {
        private static int Main()
        {
            // 临时文件放在仓库下的 tmp/，不写到系统盘；
            // 文件名带 Guid，多个实例并行跑也不会互相踩。
            string directory = ResolveWorkspaceDirectory("sample");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"sample-{Guid.NewGuid():N}.csv");

            try
            {
                BasicWrite(path);
                ConcurrentWrite(path);
                HeaderEditing(path);
                CancellableShift(path);
                GbkForExcel(Path.Combine(directory, $"gbk-{Guid.NewGuid():N}.csv"));
            }
            finally
            {
                TryDelete(path);
            }

            Console.WriteLine();
            Console.WriteLine("示例运行完毕。");
            return 0;
        }

        /// <summary>
        /// 从程序目录向上找到包含 <c>CsvLite.sln</c> 的仓库根目录，在其下建 <c>tmp/&lt;name&gt;</c>。
        /// 找不到解决方案文件时退回程序目录，绝不写系统临时目录。
        /// </summary>
        private static string ResolveWorkspaceDirectory(string name)
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "CsvLite.sln")))
                    return Path.Combine(dir, "tmp", name);

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            return Path.Combine(AppContext.BaseDirectory, "tmp", name);
        }

        private static void BasicWrite(string path)
        {
            Console.WriteLine("── 基本写入 ────────────────────────────────────────────");

            using (var csv = CsvAppender.Create(
                       path,
                       new[] { "id", "customer", "total", "note" },
                       new CsvOptions { Overwrite = true }))
            {
                Append(csv, 1001, "Alice, Jr.", 19.95m, "said \"hi\"");
                Append(csv, 1002, "Bob", 4.50m, null);
                csv.Flush();
            }

            // 注意：appender 关闭之后才能用 File.ReadAllText 读。若要边写边读，
            // 必须以 FileShare.ReadWrite 打开，否则会因共享冲突失败。
            Console.WriteLine(File.ReadAllText(path));
        }

        private static void ConcurrentWrite(string path)
        {
            Console.WriteLine("── 并发写入 ────────────────────────────────────────────");

            var sw = Stopwatch.StartNew();
            using (var csv = CsvAppender.Create(
                       path,
                       new[] { "worker", "seq", "value" },
                       new CsvOptions { Overwrite = true }))
            {
                Parallel.For(0, 4, worker =>
                {
                    for (int i = 0; i < 10_000; i++)
                    {
                        CsvRow row = csv.CreateRow();
                        row.Add(worker);
                        row.Add(i);
                        row.Add(i * 2.5);
                        csv.Append(row);      // 不加锁，也没有共享计数器
                    }
                });
                csv.Flush();

                sw.Stop();
                Console.WriteLine($"  4 个线程共 40,000 行，用时 {sw.ElapsedMilliseconds} ms");
                Console.WriteLine($"  已提交 {csv.BytesAppended} 字节，待写 {csv.PendingBytes} 字节");
                Console.WriteLine($"  文件长度 {new FileInfo(path).Length} 字节");
            }
        }

        private static void HeaderEditing(string path)
        {
            Console.WriteLine();
            Console.WriteLine("── 编辑表头 ────────────────────────────────────────────");

            using (var csv = CsvAppender.Open(path))
            {
                Console.WriteLine($"  打开后读到 {csv.HeaderCount} 列：{string.Join(" | ", csv.Headers!)}");

                // 编码长度相同：就地覆写，不搬动数据。
                csv.SetHeaders(new[] { "worker", "seq", "value_renamed" });
                Console.WriteLine("  等长改名（快路径，代价 O(表头)）");

                // 长度不同：数据区会被就地移位。
                csv.AddHeaderColumn("source");
                Console.WriteLine($"  增加一列 → 现在 {csv.HeaderCount} 列");
            }

            // 契约的关键点：数据行完全没有被碰过。
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                string? header = reader.ReadLine();
                string? firstRow = reader.ReadLine();
                Console.WriteLine($"  表头：{header}");
                Console.WriteLine($"  首行：{firstRow}");
            }

            Console.WriteLine("  （数据行仍然只有 3 个字段——这正是文档里写明的契约，不是 bug）");
        }

        private static void CancellableShift(string path)
        {
            Console.WriteLine();
            Console.WriteLine("── 可取消的移位 ────────────────────────────────────────");

            // 用小分块，这样进度回调会多次触发，取消点才有机会命中。
            var options = new CsvOptions { ShiftChunkBytes = 16 * 1024 };
            using var csv = CsvAppender.Open(path, options);
            using var cts = new CancellationTokenSource();

            var progress = new SyncProgress(fraction =>
            {
                Console.WriteLine($"  移位进度 {fraction:P0}");
                if (fraction >= 0.3 && !cts.IsCancellationRequested)
                {
                    cts.Cancel();     // 演示：搬到三成就取消
                }
            });

            try
            {
                // 刻意用超大的表头，让移位耗时足够长，可以被打断。
                csv.SetHeaders(new[] { new string('X', 200_000) }, progress, cts.Token);
                Console.WriteLine("  移位完成（未取消）");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("  已取消；文件已逐字节恢复原样");
            }

            Console.WriteLine($"  是否处于故障状态：{csv.IsFaulted}");

            // 取消之后 appender 仍然可用。
            CsvRow row = csv.CreateRow();
            row.Add("取消后仍可继续追加");
            csv.Append(row);
            csv.Flush();
            Console.WriteLine("  取消后继续追加成功");
        }

        /// <summary>
        /// 演示 GBK 输出：中文 Windows 的 Excel 双击打开不会乱码。
        /// </summary>
        private static void GbkForExcel(string path)
        {
            Console.WriteLine();
            Console.WriteLine("── GBK 输出（Excel 直接打开不乱码）────────────────────");

            var options = new CsvOptions
            {
                Encoding = CsvEncodings.Gbk,   // 代码页 936
                WriteBom = false,              // GBK 没有 BOM，Excel 按系统 ANSI 代码页解释
            };

            try
            {
                using (var csv = CsvAppender.Create(path, new[] { "编号", "客户名称", "金额", "备注" }, options))
                {
                    CsvRow row = csv.CreateRow();
                    row.Add(1);
                    row.Add("张三");
                    row.Add(19.95m);
                    row.Add("含,逗号与\"引号\"的备注");   // 中文 + 需转义字符
                    csv.Append(row);

                    row = csv.CreateRow();
                    row.Add(2);
                    row.Add("李四");
                    row.Add(4.50m);
                    row.AddNull();
                    csv.Append(row);

                    csv.Flush();
                }

                byte[] bytes = File.ReadAllBytes(path);
                Console.WriteLine($"  文件共 {bytes.Length} 字节，前 4 字节：{BitConverter.ToString(bytes, 0, Math.Min(4, bytes.Length))}");
                Console.WriteLine($"  编码代码页：{options.Encoding.CodePage}，BOM 长度：{options.Encoding.GetPreamble().Length}");
                Console.WriteLine();

                // 按 GBK 读回来验证。
                string text = File.ReadAllText(path, CsvEncodings.Gbk);
                Console.WriteLine(text.TrimEnd('\r', '\n'));
            }
            finally
            {
                TryDelete(path);
            }

            Console.WriteLine();
            Console.WriteLine($"  GBK 文件已生成并核对；临时文件位于 {Path.GetDirectoryName(path)}");
        }

        private static void Append(CsvAppender csv, int id, string customer, decimal total, string? note)
        {
            CsvRow row = csv.CreateRow();
            row.Add(id);
            row.Add(customer);
            row.Add(total);
            row.Add(note);
            csv.Append(row);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 清理失败不影响示例结论
            }
        }

        /// <summary>
        /// 同步的 <see cref="IProgress{T}"/>：<see cref="Progress{T}"/> 会把回调派发到线程池，
        /// 那样取消时机就变成非确定性的，演示效果会飘。
        /// </summary>
        private sealed class SyncProgress : IProgress<double>
        {
            private readonly Action<double> _onReport;

            internal SyncProgress(Action<double> onReport) => _onReport = onReport;

            public void Report(double value) => _onReport(value);
        }
    }
}
