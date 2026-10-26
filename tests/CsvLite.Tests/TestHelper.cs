using System;
using System.Collections.Generic;
using System.IO;

namespace CsvLite.Tests
{
    /// <summary>共用辅助方法：临时文件，以及一个用于校验输出的独立 CSV 记录解析器。</summary>
    internal static class TestHelper
    {
        /// <summary>
        /// 测试临时目录固定在仓库下的 <c>tmp/tests</c>，不写系统临时盘。
        /// 从程序目录向上找 <c>CsvLite.sln</c> 定位仓库根；找不到时退回程序目录。
        /// </summary>
        private static readonly string Root = ResolveRoot();

        private static string ResolveRoot()
        {
            string? dir = AppContext.BaseDirectory;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "CsvLite.sln")))
                    return Path.Combine(dir, "tmp", "tests");

                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return Path.Combine(AppContext.BaseDirectory, "tmp", "tests");
        }

        internal static string TempPath(string tag = "csv")
        {
            Directory.CreateDirectory(Root);
            return Path.Combine(Root, $"{tag}-{Guid.NewGuid():N}.csv");
        }

        internal static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // 测试清理时尽力而为
            }
        }

        internal static string ReadAllText(string path) => File.ReadAllText(path);

        internal static byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

        internal static string[] Lines(string path)
        {
            string text = File.ReadAllText(path);
            if (text.Length == 0) return Array.Empty<string>();
            return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>一个刻意写得简单、独立的 RFC 4180 解析器。</summary>
        internal static List<List<string>> Parse(string text, char delimiter = ',')
        {
            var records = new List<List<string>>();
            var record = new List<string>();
            var field = new System.Text.StringBuilder();
            int i = 0;
            bool quoted = false;
            bool any = false;

            while (i < text.Length)
            {
                char c = text[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            field.Append('"');
                            i += 2;
                            continue;
                        }
                        quoted = false;
                        i++;
                        continue;
                    }
                    field.Append(c);
                    i++;
                    continue;
                }

                if (c == '"' && field.Length == 0)
                {
                    quoted = true;
                    any = true;
                    i++;
                    continue;
                }

                if (c == delimiter)
                {
                    record.Add(field.ToString());
                    field.Clear();
                    any = true;
                    i++;
                    continue;
                }

                if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    i++;
                    record.Add(field.ToString());
                    field.Clear();
                    records.Add(record);
                    record = new List<string>();
                    any = false;
                    continue;
                }

                field.Append(c);
                any = true;
                i++;
            }

            if (any || field.Length > 0 || record.Count > 0)
            {
                record.Add(field.ToString());
                records.Add(record);
            }

            return records;
        }
    }
}
