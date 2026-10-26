using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;

namespace CsvLite.Internal
{
    /// <summary>
    /// 基础类型格式化。全部走 <see cref="Utf8Formatter"/>，因此常见场景既无分配又是区域无关的。
    /// </summary>
    internal static class ValueFormatter
    {
        /// <summary>定长数值类型经 Utf8Formatter 输出的最大长度（decimal 最宽）。</summary>
        internal const int MaxNumericBytes = 40;

        internal static bool TryFormatSByte(sbyte v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatInt16(short v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatInt32(int v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatInt64(long v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatByte(byte v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatUInt16(ushort v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatUInt32(uint v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatUInt64(ulong v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatSingle(float v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatDouble(double v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        internal static bool TryFormatDecimal(decimal v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        /// <summary>
        /// 输出 <c>true</c>/<c>false</c>（小写，JSON 风格）。
        /// </summary>
        /// <remarks>
        /// <c>Utf8Formatter</c> 会把布尔值渲染成 <c>True</c>/<c>False</c>；而 CSV 消费方绝大多数
        /// 期待小写形式，所以这里手写。
        /// </remarks>
        internal static bool TryFormatBoolean(bool v, Span<byte> dst, out int written)
        {
            if (v)
            {
                dst[0] = (byte)'t';
                dst[1] = (byte)'r';
                dst[2] = (byte)'u';
                dst[3] = (byte)'e';
                written = 4;
            }
            else
            {
                dst[0] = (byte)'f';
                dst[1] = (byte)'a';
                dst[2] = (byte)'l';
                dst[3] = (byte)'s';
                dst[4] = (byte)'e';
                written = 5;
            }
            return true;
        }

        /// <summary>ISO 8601 往返格式（<c>"O"</c>）。</summary>
        internal static bool TryFormatDateTime(DateTime v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written, new StandardFormat('O'));

        /// <summary>ISO 8601 往返格式（<c>"O"</c>）。</summary>
        internal static bool TryFormatDateTimeOffset(DateTimeOffset v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written, new StandardFormat('O'));

        /// <summary>常量/不变格式（<c>"c"</c>）。</summary>
        internal static bool TryFormatTimeSpan(TimeSpan v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written);

        /// <summary>带连字符的 <c>"D"</c> 格式。</summary>
        internal static bool TryFormatGuid(Guid v, Span<byte> dst, out int written)
            => Utf8Formatter.TryFormat(v, dst, out written, new StandardFormat('D'));

        /// <summary>按 <see cref="CsvOptions.DateTimeFormat"/> 渲染 <see cref="DateTime"/>。</summary>
        internal static string FormatDateTimeCustom(DateTime v, string format)
            => v.ToString(format, CultureInfo.InvariantCulture);

        /// <summary>按 <see cref="CsvOptions.DateTimeFormat"/> 渲染 <see cref="DateTimeOffset"/>。</summary>
        internal static string FormatDateTimeOffsetCustom(DateTimeOffset v, string format)
            => v.ToString(format, CultureInfo.InvariantCulture);

        /// <summary>按 <see cref="CsvOptions.GuidFormat"/> 渲染 <see cref="Guid"/>。</summary>
        internal static string FormatGuidCustom(Guid v, string format)
            => v.ToString(format);

        /// <summary>渲染枚举的数值形式，避免经 <see cref="Enum.ToString()"/> 装箱。</summary>
        internal static bool TryFormatEnumNumeric(Enum value, Span<byte> dst, out int written)
        {
            Type underlying = Enum.GetUnderlyingType(value.GetType());
            if (underlying == typeof(ulong))
                return Utf8Formatter.TryFormat(Convert.ToUInt64(value, CultureInfo.InvariantCulture), dst, out written);
            return Utf8Formatter.TryFormat(Convert.ToInt64(value, CultureInfo.InvariantCulture), dst, out written);
        }
    }
}
