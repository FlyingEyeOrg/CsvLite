using System;
using System.Text;

namespace CsvLite.Internal
{
    /// <summary>
    /// 处理各目标框架之间差异的编码辅助方法。
    /// </summary>
    /// <remarks>
    /// <para>
    /// netstandard2.0 没有 <c>Encoding.GetBytes(ReadOnlySpan&lt;char&gt;, Span&lt;byte&gt;)</c> 与
    /// <c>Encoding.GetChars(ReadOnlySpan&lt;byte&gt;, Span&lt;char&gt;)</c> 这两个重载，
    /// 因此退回指针重载（需要 <c>/unsafe</c>，项目已启用）。
    /// </para>
    /// <para>
    /// 关键在于这里的每个方法都接受调用方传入的 <see cref="Encoding"/>，
    /// 而不是固定用 UTF-8 —— GBK 等 ANSI 编码正是靠这一点接入的。
    /// </para>
    /// </remarks>
    internal static class Utf8Compat
    {
        /// <summary>默认的无 BOM UTF-8 编码。</summary>
        internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>把 <paramref name="src"/> 用 <paramref name="encoding"/> 编进 <paramref name="dst"/>，返回写入的字节数。</summary>
        internal static int GetBytes(Encoding encoding, ReadOnlySpan<char> src, Span<byte> dst)
        {
            if (src.Length == 0) return 0;
#if NETSTANDARD2_0
            unsafe
            {
                fixed (char* pSrc = src)
                fixed (byte* pDst = dst)
                {
                    return encoding.GetBytes(pSrc, src.Length, pDst, dst.Length);
                }
            }
#else
            return encoding.GetBytes(src, dst);
#endif
        }

        /// <summary><paramref name="charCount"/> 个字符在该编码下的最坏字节长度。</summary>
        internal static int GetMaxByteCount(Encoding encoding, int charCount) => encoding.GetMaxByteCount(charCount);

        /// <summary><paramref name="byteCount"/> 个字节在该编码下的最坏字符长度。</summary>
        internal static int GetMaxCharCount(Encoding encoding, int byteCount) => encoding.GetMaxCharCount(byteCount);

        /// <summary>把 <paramref name="bytes"/> 用 <paramref name="encoding"/> 解码到字符跨度，返回写入的字符数。</summary>
        internal static int GetChars(Encoding encoding, ReadOnlySpan<byte> bytes, Span<char> dst)
        {
            if (bytes.Length == 0) return 0;
#if NETSTANDARD2_0
            unsafe
            {
                fixed (byte* pSrc = bytes)
                fixed (char* pDst = dst)
                {
                    return encoding.GetChars(pSrc, bytes.Length, pDst, dst.Length);
                }
            }
#else
            return encoding.GetChars(bytes, dst);
#endif
        }

        /// <summary>把字节跨度用 <paramref name="encoding"/> 解码为字符串。</summary>
        internal static string GetString(Encoding encoding, ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length == 0) return string.Empty;
#if NETSTANDARD2_0
            unsafe
            {
                fixed (byte* p = bytes)
                {
                    return encoding.GetString(p, bytes.Length);
                }
            }
#else
            return encoding.GetString(bytes);
#endif
        }

        /// <summary>把字节数组用 <paramref name="encoding"/> 解码为字符串。</summary>
        internal static string GetString(Encoding encoding, byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;
            return encoding.GetString(bytes, 0, bytes.Length);
        }

        /// <summary>用 <paramref name="encoding"/> 把字符串编码为字节数组。</summary>
        internal static byte[] GetBytes(Encoding encoding, string value)
        {
            if (value.Length == 0) return Array.Empty<byte>();
            return encoding.GetBytes(value);
        }
    }
}
