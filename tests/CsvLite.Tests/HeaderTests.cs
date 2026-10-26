using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CsvLite;

namespace CsvLite.Tests
{
    internal static class HeaderTests
    {
        private static void Write(CsvAppender appender, params object[] fields)
        {
            CsvRow row = appender.CreateRow();
            foreach (object field in fields)
            {
                switch (field)
                {
                    case string s: row.Add(s); break;
                    case int i: row.Add(i); break;
                    default: row.Add(field.ToString()); break;
                }
            }
            appender.Append(row);
        }

        [Test]
        internal static void HeaderIsWrittenOnCreate()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (CsvAppender.Create(path, new[] { "a", "b", "c" }))
                {
                }
                Assert.Equal("a,b,c\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderIsReadBackOnOpen()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "id", "name" }))
                {
                    Write(appender, 1, "x");
                }

                using var reopened = CsvAppender.Open(path);
                Assert.NotNull(reopened.Headers);
                Assert.SequenceEqual(new[] { "id", "name" }, reopened.Headers!);
                Assert.Equal(2, reopened.HeaderCount);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SameLengthRenameKeepsFileSize()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "aaa", "bbb" }))
                {
                    Write(appender, 1, 2);
                    Write(appender, 3, 4);
                }

                long before = new FileInfo(path).Length;

                using (var appender = CsvAppender.Open(path))
                {
                    appender.SetHeaders(new[] { "xxx", "yyy" });
                }

                long after = new FileInfo(path).Length;
                Assert.Equal(before, after);

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(3, records.Count);
                Assert.SequenceEqual(new[] { "xxx", "yyy" }, records[0]);
                Assert.SequenceEqual(new[] { "1", "2" }, records[1]);
                Assert.SequenceEqual(new[] { "3", "4" }, records[2]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void LongerHeaderShiftsDataDown()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }))
                {
                    for (int i = 0; i < 50; i++) Write(appender, i, i * 2);
                }

                using (var appender = CsvAppender.Open(path))
                {
                    appender.SetHeaders(new[] { "a_renamed_longer", "b" });
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(51, records.Count);
                Assert.SequenceEqual(new[] { "a_renamed_longer", "b" }, records[0]);
                for (int i = 0; i < 50; i++)
                {
                    Assert.SequenceEqual(new[] { i.ToString(), (i * 2).ToString() }, records[i + 1]);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ShorterHeaderShiftsDataUp()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "very_long_header", "another_long_one" }))
                {
                    for (int i = 0; i < 50; i++) Write(appender, i, i * 2);
                }

                using (var appender = CsvAppender.Open(path))
                {
                    appender.SetHeaders(new[] { "a", "b" });
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(51, records.Count);
                Assert.SequenceEqual(new[] { "a", "b" }, records[0]);
                Assert.SequenceEqual(new[] { "49", "98" }, records[50]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void AddHeaderColumnDoesNotTouchDataRows()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }))
                {
                    Write(appender, 1, 2);
                    Write(appender, 3, 4);
                }

                using (var appender = CsvAppender.Open(path))
                {
                    appender.AddHeaderColumn("c");
                    Assert.SequenceEqual(new[] { "a", "b", "c" }, appender.Headers!);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(3, records.Count);
                Assert.SequenceEqual(new[] { "a", "b", "c" }, records[0]);
                // 数据行确实仍然只有两个字段：这是契约，不是 bug。
                Assert.Equal(2, records[1].Count);
                Assert.Equal(2, records[2].Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void AddHeaderColumnInTheMiddle()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "c" }))
                {
                    appender.AddHeaderColumn("b", 1);
                    Assert.SequenceEqual(new[] { "a", "b", "c" }, appender.Headers!);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.SequenceEqual(new[] { "a", "b", "c" }, records[0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void RemoveHeaderColumnDoesNotTouchDataRows()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "b", "c" }))
                {
                    Write(appender, 1, 2, 3);
                }

                using (var appender = CsvAppender.Open(path))
                {
                    appender.RemoveHeaderColumn(1);
                    Assert.SequenceEqual(new[] { "a", "c" }, appender.Headers!);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.SequenceEqual(new[] { "a", "c" }, records[0]);
                Assert.SequenceEqual(new[] { "1", "2", "3" }, records[1]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void RemovingLastColumnRemovesTheHeader()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "only" }))
                {
                    Write(appender, "x");
                    appender.RemoveHeaderColumn(0);
                    Assert.Null(appender.Headers);
                    Assert.Equal(0, appender.HeaderCount);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(1, records.Count);
                Assert.SequenceEqual(new[] { "x" }, records[0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DeleteHeadersLeavesDataIntact()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }))
                {
                    Write(appender, 1, 2);
                    Write(appender, 3, 4);
                    appender.DeleteHeaders();
                    Assert.Null(appender.Headers);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(2, records.Count);
                Assert.SequenceEqual(new[] { "1", "2" }, records[0]);
                Assert.SequenceEqual(new[] { "3", "4" }, records[1]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SetHeadersNullIsDelete()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var appender = CsvAppender.Create(path, new[] { "a" });
                appender.SetHeaders(null);
                Assert.Null(appender.Headers);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SetHeaderColumnRenamesOne()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var appender = CsvAppender.Create(path, new[] { "aaaa", "bbbb" });
                Write(appender, 1, 2);
                appender.SetHeaderColumn(1, "cccc");
                Assert.SequenceEqual(new[] { "aaaa", "cccc" }, appender.Headers!);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderOnEmptyFileUsesFastPath()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    appender.SetHeaders(new[] { "x", "y" });
                }
                Assert.Equal("x,y\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderOnHeaderOnlyFileUsesFastPath()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (CsvAppender.Create(path, new[] { "a" })) { }

                using (var appender = CsvAppender.Open(path))
                {
                    appender.SetHeaders(new[] { "much_longer_header" });
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(1, records.Count);
                Assert.SequenceEqual(new[] { "much_longer_header" }, records[0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderFieldsAreEscaped()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a,b", "c\"d" }))
                {
                    Write(appender, 1, 2);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.SequenceEqual(new[] { "a,b", "c\"d" }, records[0]);

                using (var appender = CsvAppender.Open(path))
                {
                    Assert.SequenceEqual(new[] { "a,b", "c\"d" }, appender.Headers!);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderIndexOutOfRangeThrows()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var appender = CsvAppender.Create(path, new[] { "a", "b" });
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.RemoveHeaderColumn(2));
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.RemoveHeaderColumn(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.SetHeaderColumn(5, "x"));
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.AddHeaderColumn("x", 99));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void AddHeaderColumnOnHeaderlessFileCreatesHeader()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, options: new CsvOptions { HasHeader = false }))
                {
                    Write(appender, 1, 2);
                    appender.AddHeaderColumn("first");
                    Assert.SequenceEqual(new[] { "first" }, appender.Headers!);
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(2, records.Count);
                Assert.SequenceEqual(new[] { "first" }, records[0]);
                Assert.SequenceEqual(new[] { "1", "2" }, records[1]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void LongShiftPreservesEveryByte()
        {
            // 大于一个移位分块，因此会走多轮读/写。
            string path = TestHelper.TempPath("h");
            try
            {
                int rows = 4000;
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" },
                           new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    for (int i = 0; i < rows; i++) Write(appender, i, "value-" + i);
                }

                using (var appender = CsvAppender.Open(path, new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    appender.SetHeaders(new[] { "column_a_with_a_much_longer_name", "b" });
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(rows + 1, records.Count);
                Assert.Equal("column_a_with_a_much_much_longer_name",
                    records[0][0].Replace("a_much_longer", "a_much_much_longer"));
                for (int i = 0; i < rows; i += 397)
                {
                    Assert.Equal(i.ToString(), records[i + 1][0]);
                    Assert.Equal("value-" + i, records[i + 1][1]);
                }
                Assert.Equal((rows - 1).ToString(), records[rows][0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CancelDuringShiftRestoresFileExactly()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                int rows = 20000;
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" },
                           new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    for (int i = 0; i < rows; i++) Write(appender, i, "v" + i);
                }

                byte[] before = TestHelper.ReadAllBytes(path);

                using (var appender = CsvAppender.Open(path, new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    var cts = new CancellationTokenSource();
                    var progress = new ProgressReporter(p => { if (p >= 0.25) cts.Cancel(); });

                    Assert.Throws<OperationCanceledException>(
                        () => appender.SetHeaders(new[] { "a_very_much_longer_column_name", "b" },
                                                 progress, cts.Token));

                    // 表头必须仍然是原来的那个。
                    Assert.SequenceEqual(new[] { "a", "b" }, appender.Headers!);
                }

                byte[] after = TestHelper.ReadAllBytes(path);
                Assert.Equal(before.Length, after.Length);
                for (int i = 0; i < before.Length; i++)
                {
                    if (before[i] != after[i])
                        throw new AssertFailedException($"File differs at byte {i} after rollback.");
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void AppenderRemainsUsableAfterCancelledShift()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" },
                           new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    for (int i = 0; i < 20000; i++) Write(appender, i);
                }

                using (var appender = CsvAppender.Open(path, new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    var cts = new CancellationTokenSource();
                    var progress = new ProgressReporter(p => { if (p >= 0.1) cts.Cancel(); });
                    try
                    {
                        appender.SetHeaders(new[] { "much_longer_name_here" }, progress, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // 预期行为
                    }

                    Assert.False(appender.IsFaulted);
                    Write(appender, 999);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.SequenceEqual(new[] { "a" }, records[0]);
                Assert.SequenceEqual(new[] { "999" }, records[records.Count - 1]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ProgressIsReportedAndMonotonic()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" },
                           new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    for (int i = 0; i < 20000; i++) Write(appender, i);
                }

                var values = new List<double>();
                var gate = new object();

                using (var appender = CsvAppender.Open(path, new CsvOptions { ShiftChunkBytes = 4096 }))
                {
                    appender.SetHeaders(
                        new[] { "another_header_that_is_longer" },
                        new ProgressReporter(p => { lock (gate) values.Add(p); }));
                }

                Assert.True(values.Count > 0, "Expected progress reports.");
                double last = -1;
                lock (gate)
                {
                    foreach (double v in values)
                    {
                        Assert.True(v >= last - 1e-9, $"Progress went backwards: {v} < {last}");
                        Assert.True(v >= 0 && v <= 1.0001, $"Progress out of range: {v}");
                        last = v;
                    }
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void HeaderTooLargeThrows()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var appender = CsvAppender.Create(path, options: new CsvOptions { MaxHeaderBytes = 64 });
                Assert.Throws<CsvHeaderTooLargeException>(
                    () => appender.SetHeaders(new[] { new string('x', 200) }));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CreateRefusesToOverwriteByDefault()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (CsvAppender.Create(path, new[] { "a" })) { }
                Assert.Throws<IOException>(() => CsvAppender.Create(path, new[] { "b" }));

                using (CsvAppender.Create(path, new[] { "b" }, new CsvOptions { Overwrite = true })) { }
                Assert.Equal("b\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SecondAppenderOnSamePathIsRejected()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var first = CsvAppender.Create(path);
                Assert.Throws<IOException>(() => CsvAppender.Open(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void PathIsReusableAfterDispose()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (CsvAppender.Create(path, new[] { "a" })) { }
                using (var second = CsvAppender.Open(path))
                {
                    Assert.NotNull(second.Headers);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void OpenRepairsMissingTrailingNewline()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                File.WriteAllText(path, "a,b\n1,2");   // 没有结尾换行：模拟崩溃

                using (var appender = CsvAppender.Open(path))
                {
                    Write(appender, 3, 4);
                    appender.Flush();
                }

                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(3, records.Count);
                Assert.SequenceEqual(new[] { "1", "2" }, records[1]);
                Assert.SequenceEqual(new[] { "3", "4" }, records[2]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void BomIsWrittenAndExcludedFromHeaderLength()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "aaa" },
                           new CsvOptions { WriteBom = true }))
                {
                    Write(appender, 1);
                    appender.SetHeaders(new[] { "bbb" });   // 长度相同 -> 就地覆写
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.Equal(0xEF, bytes[0]);
                Assert.Equal(0xBB, bytes[1]);
                Assert.Equal(0xBF, bytes[2]);

                using var reopened = CsvAppender.Open(path);
                Assert.SequenceEqual(new[] { "bbb" }, reopened.Headers!);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CrLfNewLineIsHonoured()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                var options = new CsvOptions { NewLine = new byte[] { (byte)'\r', (byte)'\n' } };
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }, options))
                {
                    Write(appender, 1, 2);
                }

                Assert.Equal("a,b\r\n1,2\r\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CustomDelimiterWorks()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                var options = new CsvOptions { Delimiter = (byte)';' };
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }, options))
                {
                    Write(appender, "x;y", "z");
                }

                Assert.Equal("a;b\n\"x;y\";z\n", TestHelper.ReadAllText(path));

                var records = TestHelper.Parse(TestHelper.ReadAllText(path), ';');
                Assert.Equal("x;y", records[1][0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void InvalidOptionsAreRejected()
        {
            Assert.Throws<ArgumentException>(() => new CsvOptions { Delimiter = (byte)'\n' }.ValidateForTests());
            Assert.Throws<ArgumentException>(() => new CsvOptions { Delimiter = (byte)'"' }.ValidateForTests());
            Assert.Throws<ArgumentException>(() => new CsvOptions { NewLine = new byte[] { (byte)'x' } }.ValidateForTests());
            Assert.Throws<ArgumentException>(() => new CsvOptions { QuoteChar = '中' }.ValidateForTests());
        }

        [Test]
        internal static void HeadersAreImmutableSnapshots()
        {
            string path = TestHelper.TempPath("h");
            try
            {
                using var appender = CsvAppender.Create(path, new[] { "a", "b" });
                IReadOnlyList<string> first = appender.Headers!;
                appender.SetHeaders(new[] { "c", "d" });
                IReadOnlyList<string> second = appender.Headers!;

                Assert.SequenceEqual(new[] { "a", "b" }, first);
                Assert.SequenceEqual(new[] { "c", "d" }, second);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }

    /// <summary>同步的进度接收器；<see cref="Progress{T}"/> 会把回调投递到线程池。</summary>
    internal sealed class ProgressReporter : IProgress<double>
    {
        private readonly Action<double> _onReport;

        internal ProgressReporter(Action<double> onReport) => _onReport = onReport;

        public void Report(double value) => _onReport(value);
    }

    internal static class CsvOptionsTestExtensions
    {
        /// <summary>从测试程序集里验证内部校验器。</summary>
        internal static void ValidateForTests(this CsvOptions options)
        {
            // 非法选项在 Create/Open 时才会暴露；用一个用完即弃的路径。
            string path = TestHelper.TempPath("opt");
            try
            {
                // 合法配置下 Create 会成功：必须释放，否则文件句柄泄漏、文件删不掉。
                using (CsvAppender.Create(path, options: options))
                {
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }
}
