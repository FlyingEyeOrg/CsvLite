using System;
using System.Globalization;

namespace CsvLite.Tests
{
    internal static class RowSerializationTests
    {
        private static string WriteOne(Action<CsvRow> fill, CsvOptions? options = null)
        {
            string path = TestHelper.TempPath("row");
            try
            {
                using (var appender = CsvAppender.Create(path, options: options))
                {
                    CsvRow row = appender.CreateRow();
                    fill(row);
                    appender.Append(row);
                }
                return TestHelper.ReadAllText(path);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void EmptyFileHasNoTrailingGarbage()
        {
            string path = TestHelper.TempPath("empty");
            try
            {
                using (CsvAppender.Create(path)) { }
                Assert.Equal(string.Empty, TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void TextFieldsAreSeparated()
        {
            string text = WriteOne(r =>
            {
                r.Add("a");
                r.Add("b");
                r.Add("c");
            });
            Assert.Equal("a,b,c\n", text);
        }

        [Test]
        internal static void NullAndEmptyProduceEmptyField()
        {
            string text = WriteOne(r =>
            {
                r.Add((string?)null);
                r.Add(string.Empty);
                r.AddNull();
            });
            Assert.Equal(",,\n", text);
        }

        [Test]
        internal static void DirtyFieldsAreQuotedAndEscaped()
        {
            string[] values =
            {
                "a,b",
                "say \"hi\"",
                "line1\nline2",
                "line1\r\nline2",
                "carriage\rreturn",
            };

            foreach (string value in values)
            {
                string text = WriteOne(r =>
                {
                    r.Add(value);
                    r.Add("tail");
                });

                var records = TestHelper.Parse(text);
                Assert.Equal(1, records.Count);
                Assert.Equal(2, records[0].Count);
                Assert.Equal(value, records[0][0]);
                Assert.Equal("tail", records[0][1]);
            }
        }

        [Test]
        internal static void CleanFieldsAreNeverQuoted()
        {
            string text = WriteOne(r => r.Add("plain text"));
            Assert.Equal("plain text\n", text);
        }

        [Test]
        internal static void EveryFieldModeQuotesEverything()
        {
            var options = new CsvOptions { QuoteMode = QuoteMode.EveryField };
            string text = WriteOne(r =>
            {
                r.Add("a");
                r.Add((string?)null);
            }, options);
            Assert.Equal("\"a\",\"\"\n", text);
        }

        [Test]
        internal static void AllNumericTypesFormatCorrectly()
        {
            string text = WriteOne(r =>
            {
                r.Add((sbyte)-128);
                r.Add((short)-32768);
                r.Add(int.MinValue);
                r.Add(long.MinValue);
                r.Add((byte)255);
                r.Add(ushort.MaxValue);
                r.Add(uint.MaxValue);
                r.Add(ulong.MaxValue);
                r.Add(1.5d);
                r.Add(2.5f);
                r.Add(3.14159265358979323846m);
            });

            string[] fields = text.TrimEnd('\n').Split(',');
            Assert.Equal("-128", fields[0]);
            Assert.Equal("-32768", fields[1]);
            Assert.Equal("-2147483648", fields[2]);
            Assert.Equal("-9223372036854775808", fields[3]);
            Assert.Equal("255", fields[4]);
            Assert.Equal("65535", fields[5]);
            Assert.Equal("4294967295", fields[6]);
            Assert.Equal("18446744073709551615", fields[7]);
            Assert.Equal("1.5", fields[8]);
            Assert.Equal("2.5", fields[9]);
            Assert.Equal("3.14159265358979323846", fields[10]);
        }

        [Test]
        internal static void DoubleUsesShortestRoundTrip()
        {
            string text = WriteOne(r => r.Add(0.1d + 0.2d));
            Assert.Equal("0.30000000000000004", text.TrimEnd('\n'));
        }

        [Test]
        internal static void BooleanFormatting()
        {
            string text = WriteOne(r =>
            {
                r.Add(true);
                r.Add(false);
            });
            Assert.Equal("true,false\n", text);
        }

        [Test]
        internal static void DateTimeUsesRoundTripFormatByDefault()
        {
            var value = new DateTime(2026, 10, 26, 13, 45, 30, DateTimeKind.Utc);
            string text = WriteOne(r => r.Add(value));
            Assert.Equal(value.ToString("O", CultureInfo.InvariantCulture), text.TrimEnd('\n'));
        }

        [Test]
        internal static void DateTimeHonoursCustomFormat()
        {
            var options = new CsvOptions { DateTimeFormat = "yyyy-MM-dd" };
            string text = WriteOne(r => r.Add(new DateTime(2026, 10, 26)), options);
            Assert.Equal("2026-10-26\n", text);
        }

        [Test]
        internal static void GuidUsesHyphenatedByDefault()
        {
            var value = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            string text = WriteOne(r => r.Add(value));
            Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e\n", text);
        }

        [Test]
        internal static void GuidHonoursCustomFormat()
        {
            var options = new CsvOptions { GuidFormat = "N" };
            var value = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
            string text = WriteOne(r => r.Add(value), options);
            Assert.Equal("0f8fad5bd9cb469fa16570867728950e\n", text);
        }

        [Test]
        internal static void EnumUsesNameByDefault()
        {
            string text = WriteOne(r => r.Add(DayOfWeek.Friday));
            Assert.Equal("Friday\n", text);
        }

        [Test]
        internal static void EnumCanUseNumericValue()
        {
            var options = new CsvOptions { EnumFormat = EnumFormat.Numeric };
            string text = WriteOne(r => r.Add(DayOfWeek.Friday), options);
            Assert.Equal("5\n", text);
        }

        [Test]
        internal static void TimeSpanUsesConstantFormat()
        {
            string text = WriteOne(r => r.Add(new TimeSpan(1, 2, 3, 4)));
            Assert.Equal("1.02:03:04", text.TrimEnd('\n'));
        }

        [Test]
        internal static void CharFieldWorks()
        {
            string text = WriteOne(r =>
            {
                r.Add('x');
                r.Add(',');
            });
            var records = TestHelper.Parse(text);
            Assert.SequenceEqual(new[] { "x", "," }, records[0]);
        }

        [Test]
        internal static void CjkAndEmojiRoundTrip()
        {
            string text = WriteOne(r =>
            {
                r.Add("中文测试");
                r.Add("😀🚀");
            });
            var records = TestHelper.Parse(text);
            Assert.Equal("中文测试", records[0][0]);
            Assert.Equal("😀🚀", records[0][1]);
        }

        [Test]
        internal static void RowExposesFieldCountAndByteCount()
        {
            string path = TestHelper.TempPath("counts");
            try
            {
                using var appender = CsvAppender.Create(path);
                CsvRow row = appender.CreateRow();
                row.Add("ab");
                row.Add("cd");
                Assert.Equal(2, row.FieldCount);
                Assert.Equal(5, row.ByteCount); // "ab,cd" 即字段本身
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void AppendingTwiceThrows()
        {
            string path = TestHelper.TempPath("twice");
            try
            {
                using var appender = CsvAppender.Create(path);
                CsvRow row = appender.CreateRow();
                row.Add("x");
                appender.Append(row);
                Assert.True(row.IsRecycled);
                Assert.Throws<InvalidOperationException>(() => appender.Append(row));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void UsingRecycledRowThrows()
        {
            string path = TestHelper.TempPath("recycled");
            try
            {
                using var appender = CsvAppender.Create(path);
                CsvRow row = appender.CreateRow();
                appender.Append(row);
                Assert.Throws<InvalidOperationException>(() => row.Add("x"));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void RowFromAnotherAppenderIsRejected()
        {
            string a = TestHelper.TempPath("a");
            string b = TestHelper.TempPath("b");
            try
            {
                using var first = CsvAppender.Create(a);
                using var second = CsvAppender.Create(b);
                CsvRow row = first.CreateRow();
                row.Add("x");
                Assert.Throws<InvalidOperationException>(() => second.Append(row));
            }
            finally
            {
                TestHelper.Delete(a);
                TestHelper.Delete(b);
            }
        }

        [Test]
        internal static void ClearAllowsReuse()
        {
            string path = TestHelper.TempPath("clear");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add("first");
                    row.Clear();
                    Assert.Equal(0, row.FieldCount);
                    row.Add("second");
                    appender.Append(row);
                }

                Assert.Equal("second\n", TestHelper.ReadAllText(path));
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void VeryLongFieldWorks()
        {
            string big = new string('x', 100_000);
            string path = TestHelper.TempPath("big");
            try
            {
                using (var appender = CsvAppender.Create(path))
                {
                    CsvRow row = appender.CreateRow();
                    row.Add(big);
                    appender.Append(row);
                }
                var records = TestHelper.Parse(TestHelper.ReadAllText(path));
                Assert.Equal(big, records[0][0]);
            }
            finally
            {
                TestHelper.Delete(path);
            }
        }

        [Test]
        internal static void SingleFieldRowHasNoTrailingDelimiter()
        {
            string text = WriteOne(r => r.Add("only"));
            Assert.Equal("only\n", text);
        }

        [Test]
        internal static void RowWithManyFieldsIsWellFormed()
        {
            string text = WriteOne(r =>
            {
                for (int i = 0; i < 500; i++) r.Add(i);
            });
            var records = TestHelper.Parse(text);
            Assert.Equal(1, records.Count);
            Assert.Equal(500, records[0].Count);
            Assert.Equal("499", records[0][499]);
        }
    }
}
