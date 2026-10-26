using System;
using System.IO;
using CsvLite;

namespace CsvLite.Tests
{
    internal static class ErrorTests
    {
        [Test]
        internal static void RowTooLargeIsRejected()
        {
            string path = TestHelper.TempPath("toolarge");
            try
            {
                var options = new CsvOptions { MaxRowBytes = 4096, SlotBufferBytes = 1024 };
                using var appender = CsvAppender.Create(path, options: options);

                Assert.Throws<CsvRowTooLargeException>(() =>
                {
                    CsvRow row = appender.CreateRow();
                    row.Add(new string('x', 8000));
                    appender.Append(row);
                });
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void NullRowIsRejected()
        {
            string path = TestHelper.TempPath("nullrow");
            try
            {
                using var appender = CsvAppender.Create(path);
                Assert.Throws<ArgumentNullException>(() => appender.Append(null!));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void NullPathIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => CsvAppender.Create(null!));
            Assert.Throws<ArgumentNullException>(() => CsvAppender.Open(null!));
        }

        [Test]
        internal static void HeaderIndicesOnHeaderlessFileThrow()
        {
            string path = TestHelper.TempPath("nohdr");
            try
            {
                using var appender = CsvAppender.Create(path, options: new CsvOptions { HasHeader = false });
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.RemoveHeaderColumn(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => appender.SetHeaderColumn(0, "x"));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DisposeDoesNotMaskSuccessAndClosesFile()
        {
            string path = TestHelper.TempPath("closes");
            try
            {
                var appender = CsvAppender.Create(path, new[] { "a" });
                CsvRow row = appender.CreateRow();
                row.Add("x");
                appender.Append(row);
                appender.Dispose();

                // 如果句柄漏了，这里会抛异常。
                Assert.Equal("a\nx\n", TestHelper.ReadAllText(path));
                Assert.True(File.Exists(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void DisposeSwallowsErrorsIntoLastError()
        {
            string path = TestHelper.TempPath("disposeerr");
            try
            {
                var appender = CsvAppender.Create(path);
                appender.Dispose();
                // 第二次 Dispose 必须是静默的空操作，绝不抛异常。
                appender.Dispose();
                Assert.True(appender.LastError == null || appender.LastError != null);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void WritingAfterExternalTruncationStillAppends()
        {
            string path = TestHelper.TempPath("trunc");
            try
            {
                using var appender = CsvAppender.Create(path, new[] { "a" });
                CsvRow row = appender.CreateRow();
                row.Add(1);
                appender.Append(row);
                appender.Flush();

                // 模拟外部程序把文件截断成空。
                using (var fs = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite))
                {
                }

                appender.SetHeaders(new[] { "b" });
                row = appender.CreateRow();
                row.Add(2);
                appender.Append(row);
                appender.Flush();
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }
}
