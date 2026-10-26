using System;
using System.IO;

namespace CsvLite
{
    /// <summary>后台写入文件失败。</summary>
    public sealed class CsvWriteException : IOException
    {
        /// <summary>创建实例。</summary>
        public CsvWriteException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>表头记录超出 <see cref="CsvOptions.MaxHeaderBytes"/>。</summary>
    public sealed class CsvHeaderTooLargeException : InvalidOperationException
    {
        /// <summary>创建实例。</summary>
        public CsvHeaderTooLargeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>单行超出生产者缓冲容量，无法容纳。</summary>
    public sealed class CsvRowTooLargeException : InvalidOperationException
    {
        /// <summary>创建实例。</summary>
        public CsvRowTooLargeException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// 表头重写被取消或失败，且文件无法恢复到先前状态。
    /// 此时必须认为文件已损坏。
    /// </summary>
    public sealed class CsvFileCorruptedException : Exception
    {
        /// <summary>创建实例。</summary>
        public CsvFileCorruptedException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
