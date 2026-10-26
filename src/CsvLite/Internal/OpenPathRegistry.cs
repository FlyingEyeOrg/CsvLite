using System;
using System.Collections.Generic;
using System.IO;

namespace CsvLite.Internal
{
    /// <summary>
    /// 进程级的路径登记表，记录当前有哪些文件路径正被活的 <see cref="CsvAppender"/> 占用。
    /// </summary>
    /// <remarks>
    /// 文件流以 <see cref="FileShare.ReadWrite"/> 打开，这样外部读者（Excel、<c>tail -f</c>、监控 agent）
    /// 不会被挡在外面。代价是操作系统不会阻止同进程的第二个 appender 打开同一路径，所以独占性改在这里保证。
    /// 跨进程的单写者安全性由调用方负责。
    /// </remarks>
    internal static class OpenPathRegistry
    {
        private static readonly HashSet<string> Open = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Sync = new object();

        internal static void Register(string path)
        {
            string key = Normalize(path);
            lock (Sync)
            {
                if (!Open.Add(key))
                    throw new IOException(
                        $"The file '{path}' is already open by another {nameof(CsvAppender)} in this process.");
            }
        }

        internal static void Unregister(string path)
        {
            string key = Normalize(path);
            lock (Sync)
            {
                Open.Remove(key);
            }
        }

        private static string Normalize(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }
    }
}
