using System;
using System.IO;
using System.Text;
using CsvLite;

namespace CsvLite.Tests
{
    /// <summary>
    /// 文件级别的往返校验：磁盘上的字节、BOM 处理以及编码。这些检查能抓到
    /// 字符串级别测试看不见的问题。
    /// </summary>
    internal static class FileFormatTests
    {
        [Test]
        internal static void OutputIsValidUtf8ForWideCharacterText()
        {
            string path = TestHelper.TempPath("utf8");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("日本語テキスト");
                    row.Add("한국어");
                    row.Add("العربية");
                    row.Add("🎉");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                string decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                Assert.Equal("日本語テキスト,한국어,العربية,🎉\n", decoded);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SurrogatePairIsEncodedAsFourBytes()
        {
            string path = TestHelper.TempPath("surrogate");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("😀");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                // 代理对占 4 个字节，换行符占 1 个字节。
                Assert.Equal(5, bytes.Length);
                Assert.Equal(0xF0, bytes[0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void BomMakesFileRecognisableToConsumers()
        {
            string path = TestHelper.TempPath("bom");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" }, new CsvOptions { WriteBom = true }))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("x");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.Equal(0xEF, bytes[0]);
                Assert.Equal(0xBB, bytes[1]);
                Assert.Equal(0xBF, bytes[2]);
                Assert.Equal((byte)'a', bytes[3]);

                // 表头解析器绝不能把 BOM 误当成第一个列名的一部分。
                using var reopened = CsvAppender.Open(path);
                Assert.SequenceEqual(new[] { "a" }, reopened.Headers!);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void NoBomByDefault()
        {
            string path = TestHelper.TempPath("nobom");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "a" }))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("x");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.Equal((byte)'a', bytes[0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void FileEndsWithExactlyOneNewLine()
        {
            string path = TestHelper.TempPath("eol");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    for (int i = 0; i < 10; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        appender.Append(row);
                    }
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.Equal((byte)'\n', bytes[bytes.Length - 1]);
                Assert.NotEqual((byte)'\n', bytes[bytes.Length - 2]);

                // 重新打开并追加时，不得再加一个终止符。
                using (var appender = CsvAppender.Open(path))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add(10);
                    appender.Append(row);
                    appender.Flush();
                }

                Assert.Equal(11, TestHelper.Lines(path).Length);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CrLfFilesRoundTrip()
        {
            string path = TestHelper.TempPath("crlf");
            try
            {
                var options = new CsvOptions { NewLine = new byte[] { (byte)'\r', (byte)'\n' } };
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }, options))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("x");
                    row.Add("y");
                    appender.Append(row);
                }

                string text = TestHelper.ReadAllText(path);
                Assert.Equal("a,b\r\nx,y\r\n", text);

                var records = TestHelper.Parse(text);
                Assert.Equal(2, records.Count);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void TabDelimitedWorks()
        {
            string path = TestHelper.TempPath("tab");
            try
            {
                var options = new CsvOptions { Delimiter = (byte)'\t' };
                using (var appender = CsvAppender.Create(path, new[] { "a", "b" }, options))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("x,y");     // 这里的逗号不具特殊含义
                    row.Add("z");
                    appender.Append(row);
                }

                Assert.Equal("a\tb\nx,y\tz\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void OriginalFileBytesAreUntouchedByHeaderFastPath()
        {
            string path = TestHelper.TempPath("untouched");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "aa" }))
                {
                    for (int i = 0; i < 100; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        appender.Append(row);
                    }
                }

                byte[] before = TestHelper.ReadAllBytes(path);

                using (var appender = CsvAppender.Open(path))
                {
                    appender.SetHeaders(new[] { "bb" });   // 长度相同
                }

                byte[] after = TestHelper.ReadAllBytes(path);
                Assert.Equal(before.Length, after.Length);

                // 只有表头那几个字节不同。
                int differing = 0;
                for (int i = 0; i < before.Length; i++)
                {
                    if (before[i] != after[i]) differing++;
                }
                Assert.Equal(2, differing);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }
    }
}
