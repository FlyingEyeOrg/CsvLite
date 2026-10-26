using System;
using System.IO;
using System.Text;
using CsvLite;

namespace CsvLite.Tests
{
    /// <summary>
    /// GBK 等 ANSI 编码的测试。重点验证两件事：
    /// 一是字节确实是 GBK（而不是被悄悄当成 UTF-8），
    /// 二是双字节字符不会被分隔符扫描切坏。
    /// </summary>
    internal static class EncodingTests
    {
        private static CsvOptions GbkOptions() => new CsvOptions { Encoding = CsvEncodings.Gbk };

        private static string ReadGbk(string path) => File.ReadAllText(path, CsvEncodings.Gbk);

        [Test]
        internal static void GbkIsAvailableAndReports936()
        {
            Encoding gbk = CsvEncodings.Gbk;
            Assert.Equal(936, gbk.CodePage);
            Assert.Equal("A", GbkRoundTrip("A"));

            // GBK 向下兼容 ASCII：ASCII 字符仍是单字节。
            Assert.Equal(1, gbk.GetByteCount("A"));
        }

        [Test]
        internal static void GbkHasNoBom()
        {
            Assert.Equal(0, CsvEncodings.Gbk.GetPreamble().Length);
            Assert.Equal(3, CsvEncodings.Get(65001).GetPreamble().Length);
        }

        private static string GbkRoundTrip(string value)
        {
            string path = TestHelper.TempPath("gbk");
            try
            {
                using (var appender = CsvAppender.Create(path, options: GbkOptions()))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add(value);
                    appender.Append(row);
                }
                return ReadGbk(path).TrimEnd('\r', '\n');
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ChineseTextIsWrittenAsGbkBytes()
        {
            string path = TestHelper.TempPath("gbk");
            try
            {
                using (var appender = CsvAppender.Create(path, options: GbkOptions()))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("中文测试");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);

                // "中文测试" 在 GBK 下是 8 字节；若误用 UTF-8 会是 12 字节。
                Assert.Equal(8 + 1, bytes.Length); // + 换行

                // 前两个字节应当是 GBK 的 "中" = 0xD6 0xD0。
                Assert.Equal(0xD6, bytes[0]);
                Assert.Equal(0xD0, bytes[1]);

                Assert.Equal("中文测试", ReadGbk(path).TrimEnd('\r', '\n'));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void GbkHeaderIsEncodedAndParsedBack()
        {
            string path = TestHelper.TempPath("gbkhdr");
            var headers = new[] { "编号", "客户名称", "金额", "备注" };
            try
            {
                using (CsvAppender.Create(path, headers, GbkOptions()))
                {
                }

                string text = ReadGbk(path);
                Assert.Equal("编号,客户名称,金额,备注\n", text);

                // 重新打开时能正确解析回中文，且 BOM 处理不会误吞首字节。
                using var reopened = CsvAppender.Open(path, GbkOptions());
                Assert.SequenceEqual(headers, reopened.Headers!);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void ChineseFieldsWithDelimitersAreEscapedCorrectly()
        {
            string path = TestHelper.TempPath("gbkesc");
            try
            {
                using (var appender = CsvAppender.Create(path, options: GbkOptions()))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("含,逗号的中文");
                    row.Add("含\"引号\"的中文");
                    row.Add("含\n换行的中文");
                    appender.Append(row);
                }

                string text = ReadGbk(path);
                var records = TestHelper.Parse(text);
                Assert.Equal(1, records.Count);
                Assert.Equal("含,逗号的中文", records[0][0]);
                Assert.Equal("含\"引号\"的中文", records[0][1]);
                Assert.Equal("含\n换行的中文", records[0][2]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void GbkTrailBytesNeverCollideWithSyntax()
        {
            // 遍历 CJK 常用字区，确认这些字符的 GBK 字节里不会出现逗号/引号/CR/LF。
            // 这是「按字节转义在 GBK 下安全」的根本前提。
            Encoding gbk = CsvEncodings.Gbk;
            byte[] dangerous = { (byte)',', (byte)'"', (byte)'\r', (byte)'\n' };

            for (int code = 0x4E00; code <= 0x9FA5; code++)
            {
                byte[] encoded = gbk.GetBytes(new[] { (char)code });
                Assert.Equal(2, encoded.Length);
                foreach (byte b in dangerous)
                {
                    Assert.NotEqual(b, encoded[0]);
                    Assert.NotEqual(b, encoded[1]);
                }
            }
        }

        [Test]
        internal static void GbkRoundTripsThroughManyRows()
        {
            string path = TestHelper.TempPath("gbkmany");
            try
            {
                using (var appender = CsvAppender.Create(
                           path, new[] { "序号", "名称", "说明" }, GbkOptions()))
                {
                    for (int i = 0; i < 500; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add("产品-" + i);
                        row.Add(i % 3 == 0 ? "含,逗号" : "普通描述");
                        appender.Append(row);
                    }
                    appender.Flush();
                }

                var records = TestHelper.Parse(ReadGbk(path));
                Assert.Equal(501, records.Count);
                Assert.SequenceEqual(new[] { "序号", "名称", "说明" }, records[0]);
                Assert.Equal("产品-499", records[500][1]);
                Assert.Equal("普通描述", records[500][2]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void GbkHeaderEditingWorks()
        {
            string path = TestHelper.TempPath("gbkhdr2");
            try
            {
                using (var appender = CsvAppender.Create(path, new[] { "旧名", "二" }, GbkOptions()))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(i);
                        row.Add("值" + i);
                        appender.Append(row);
                    }
                }

                // 变长改名：走就地移位路径，字节长度按 GBK 计算。
                using (var appender = CsvAppender.Open(path, GbkOptions()))
                {
                    appender.SetHeaders(new[] { "新的更长的名字", "二" });
                }

                var records = TestHelper.Parse(ReadGbk(path));
                Assert.Equal(21, records.Count);
                Assert.SequenceEqual(new[] { "新的更长的名字", "二" }, records[0]);
                Assert.SequenceEqual(new[] { "19", "值19" }, records[20]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void CjkNumeralCharactersSurviveRoundTrip()
        {
            // 这些字符的 GBK 尾字节可能接近 ASCII 区间，逐一确认不被切坏。
            string[] samples =
            {
                "中文", "测试", "数据", "表头", "编号", "金额", "备注", "名称",
                "中文，全角逗号", "（全角括号）", "“中文引号”", "【方括号】",
            };

            string path = TestHelper.TempPath("gbkcjk");
            try
            {
                using (var appender = CsvAppender.Create(path, options: GbkOptions()))
                {
                    foreach (string sample in samples)
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add(sample);
                        row.Add("next");
                        appender.Append(row);
                    }
                    appender.Flush();
                }

                var records = TestHelper.Parse(ReadGbk(path));
                Assert.Equal(samples.Length, records.Count);
                for (int i = 0; i < samples.Length; i++)
                {
                    Assert.Equal(samples[i], records[i][0]);
                    Assert.Equal("next", records[i][1]);
                }
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void Utf8WithBomIsStillTheDefaultShape()
        {
            string path = TestHelper.TempPath("utf8bom");
            try
            {
                var options = new CsvOptions { WriteBom = true };
                using (var appender = CsvAppender.Create(path, new[] { "名称" }, options))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("中文");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.Equal(0xEF, bytes[0]);
                Assert.Equal(0xBB, bytes[1]);
                Assert.Equal(0xBF, bytes[2]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void GbkWithoutBomMatchesWhatExcelExpects()
        {
            string path = TestHelper.TempPath("excel");
            try
            {
                // 中文 Windows 上 Excel 打开无 BOM 的 CSV 时按系统 ANSI 代码页（简体中文为 936）解释。
                // 因此 GBK + 无 BOM 正是「双击就能正确打开」的组合。
                var options = new CsvOptions { Encoding = CsvEncodings.Gbk, WriteBom = false };
                using (var appender = CsvAppender.Create(path, new[] { "姓名", "城市" }, options))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("张三");
                    row.Add("北京");
                    appender.Append(row);
                }

                byte[] bytes = TestHelper.ReadAllBytes(path);
                Assert.NotEqual(0xEF, bytes[0]);   // 确认没有 BOM

                // 用系统 ANSI 代码页（中文环境即 936）读回来应当正常。
                string viaAnsi = File.ReadAllText(path, CsvEncodings.Gbk);
                Assert.True(viaAnsi.StartsWith("姓名,城市", StringComparison.Ordinal));
                Assert.True(viaAnsi.Contains("张三,北京"), viaAnsi);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void Gb18030HandlesRareCharacters()
        {
            string path = TestHelper.TempPath("gb18030");
            try
            {
                var options = new CsvOptions { Encoding = CsvEncodings.Gb18030 };
                using (var appender = CsvAppender.Create(path, options: options))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("𠀀");     // 需要 GB18030 四字节序列的生僻字
                    row.Add("普通");
                    appender.Append(row);
                }

                string text = File.ReadAllText(path, CsvEncodings.Gb18030);
                var records = TestHelper.Parse(text);
                Assert.Equal("𠀀", records[0][0]);
                Assert.Equal("普通", records[0][1]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void UnsafeDelimiterForDoubleByteEncodingIsRejected()
        {
            // GBK / Big5 / Shift-JIS 的尾字节范围都是 0x40 起，
            // 因此落在该区间的分隔符会切坏汉字，必须拒绝。
            foreach (Encoding encoding in new[] { CsvEncodings.Gbk, CsvEncodings.Big5, CsvEncodings.ShiftJis })
            {
                foreach (byte delimiter in new byte[] { (byte)'|', (byte)'^', (byte)'~', (byte)'`', (byte)'\\', (byte)'@' })
                {
                    var bad = new CsvOptions { Encoding = encoding, Delimiter = delimiter };
                    Assert.Throws<ArgumentException>(() => bad.ValidateForTests());
                }
            }

            // 小于 0x40 的分隔符在所有双字节编码下都安全。
            foreach (Encoding encoding in new[] { CsvEncodings.Gbk, CsvEncodings.Big5, CsvEncodings.ShiftJis })
            {
                foreach (byte delimiter in new byte[] { (byte)',', (byte)';', (byte)'\t', (byte)':', (byte)'!', (byte)'/' })
                {
                    var ok = new CsvOptions { Encoding = encoding, Delimiter = delimiter };
                    ok.ValidateForTests();
                }
            }
        }

        [Test]
        internal static void GbkAllowsAsciiDelimitersBelow0x40()
        {
            // 逗号、分号、制表符在 GBK 下都能用，且切分正确。
            foreach (byte delimiter in new byte[] { (byte)',', (byte)';', (byte)'\t' })
            {
                string path = TestHelper.TempPath("gbkdelim");
                try
                {
                    var options = new CsvOptions { Encoding = CsvEncodings.Gbk, Delimiter = delimiter };
                    using (var appender = CsvAppender.Create(path, new[] { "甲", "乙" }, options))
                    {
                        CsvRow row = appender.CreateRow();
                        row.Add("中文" + (char)delimiter + "值");
                        row.Add("完");
                        appender.Append(row);
                    }

                    var records = TestHelper.Parse(ReadGbk(path), (char)delimiter);
                    Assert.Equal(2, records.Count);
                    Assert.Equal("中文" + (char)delimiter + "值", records[1][0]);
                    Assert.Equal("完", records[1][1]);
                }
                finally
                {
                    TestHelper.Delete(path);
                }
            }
        }

        [Test]
        internal static void Utf16AndUtf32AreRejected()
        {
            // 分隔符与行终止符是按单字节写出的，UTF-16/UTF-32 下逗号会变成 2C 00，
            // 产出无法解析的文件，因此必须在 Validate 阶段就拒绝。
            foreach (int codePage in new[] { 1200, 1201, 12000, 12001 })
            {
                var options = new CsvOptions { Encoding = Encoding.GetEncoding(codePage) };
                Assert.Throws<ArgumentException>(() => options.ValidateForTests());
            }
        }

        [Test]
        internal static void NullEncodingIsRejected()
        {
            var options = new CsvOptions { Encoding = null! };
            Assert.Throws<ArgumentNullException>(() => options.ValidateForTests());
        }
    }
}
