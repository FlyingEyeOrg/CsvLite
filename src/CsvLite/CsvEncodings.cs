using System;
using System.Text;

namespace CsvLite
{
    /// <summary>
    /// 常用非 UTF-8 编码的获取入口。
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET Core 及之后的目标框架默认只带 UTF-8/UTF-16/ASCII 等少数编码。
    /// 要拿到 GBK（代码页 936）这类 ANSI 编码，必须先注册
    /// <see cref="CodePagesEncodingProvider"/>。本类的静态构造器会替你完成注册，
    /// 因此直接取 <see cref="Gbk"/> 即可。
    /// </para>
    /// <para>
    /// 注意 <see cref="Encoding.WebName"/> 对代码页 936 返回的是 <c>gb2312</c>，
    /// 这是 .NET 的历史命名，实际支持 GBK 字符集。
    /// </para>
    /// </remarks>
    public static class CsvEncodings
    {
        private static readonly object Sync = new object();
        private static bool _providerRegistered;

        /// <summary>
        /// 注册代码页编码提供程序。多次调用是安全的。只要通过本类取过一次编码就已自动注册，
        /// 一般不需要手动调用。
        /// </summary>
        public static void EnsureCodePagesRegistered()
        {
            if (_providerRegistered) return;
            lock (Sync)
            {
                if (_providerRegistered) return;
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                _providerRegistered = true;
            }
        }

        /// <summary>
        /// GBK（代码页 936）。中文 Windows 上 Excel 默认按 ANSI 代码页解释 CSV，
        /// 用它写出的文件在中文 Excel 里直接打开不会乱码。
        /// </summary>
        /// <remarks>
        /// GBK 向下兼容 ASCII：ASCII 字符仍是单字节，且多字节字符的尾字节最小为 0xA2，
        /// 因此逗号、引号、CR、LF 这些 ASCII 控制字节永远不会出现在多字节字符内部，
        /// 按字节做 CSV 转义是安全的。
        /// </remarks>
        public static Encoding Gbk => Get(936);

        /// <summary>GB18030（代码页 54936），GBK 的超集，覆盖更多生僻字。</summary>
        public static Encoding Gb18030 => Get(54936);

        /// <summary>Big5（代码页 950），繁体中文。</summary>
        public static Encoding Big5 => Get(950);

        /// <summary>Shift-JIS（代码页 932），日文。</summary>
        public static Encoding ShiftJis => Get(932);

        /// <summary>
        /// 按代码页取编码。自动完成代码页提供程序注册。
        /// </summary>
        /// <param name="codePage">例如 936（GBK）、950（Big5）、932（Shift-JIS）。</param>
        public static Encoding Get(int codePage)
        {
            EnsureCodePagesRegistered();
            return Encoding.GetEncoding(codePage);
        }

        /// <summary>
        /// 按名称取编码。自动完成代码页提供程序注册。
        /// </summary>
        /// <param name="name">例如 <c>gbk</c>、<c>gb2312</c>、<c>big5</c>、<c>utf-8</c>。</param>
        public static Encoding Get(string name)
        {
            EnsureCodePagesRegistered();
            return Encoding.GetEncoding(name);
        }

        /// <summary>
        /// 判断某个 ASCII 字节能否安全地用作分隔符或引号字符。
        /// </summary>
        /// <remarks>
        /// <para>
        /// 对 UTF-8 与 ASCII 永远安全。对双字节编码（GBK/Big5/Shift-JIS 等），
        /// 需要确认该字节不会出现在多字节字符的尾字节位置。否则按字节扫描时
        /// 会把一个汉字的半个字节误认成分隔符，产出错位的文件。
        /// </para>
        /// <para>
        /// 例如：GBK 的尾字节最小为 0xA2，所以 0x00–0x7F 全部安全；
        /// 而 Big5 的尾字节范围是 0x40–0x7E，落在该区间的分隔符（如 <c>|</c>、<c>\</c>）就不安全，
        /// 但逗号（0x2C）、引号（0x22）、CR、LF 都小于 0x40，仍然安全。
        /// </para>
        /// </remarks>
        internal static bool IsSyntaxByteSafe(Encoding encoding, byte value)
        {
            // 非 ASCII 字节不可能是分隔符/引号（那会把多字节字符拆坏），上游已校验过。
            if (value >= 0x80) return false;

            // UTF-8 与 ASCII：ASCII 字节只在单字节字符里出现，安全。
            if (IsUtf8(encoding) || encoding.CodePage == 20127 /* us-ascii */) return true;

            // 单字节编码（如 Latin-1）：任何字节都不会被"拆分"，安全。
            if (!MayBeDoubleByte(encoding)) return true;

            return !AppearsAsTrailByte(encoding, value);
        }

        /// <summary>
        /// 该编码是否为库目前支持的"单字节兼容"编码，即 ASCII 字符编码后仍占一个字节。
        /// </summary>
        /// <remarks>
        /// 库把分隔符、引号、行终止符按**单字节**直接写进输出流。因此只有 ASCII 向下兼容的编码
        /// （UTF-8、GBK、Big5、Shift-JIS、Latin-1 等）才能正确工作。
        /// UTF-16/UTF-32 会把每个 ASCII 字符编成多字节（如逗号变成 <c>2C 00</c>），
        /// 按单字节写入会直接产出无法解析的文件，因此必须显式拒绝而不是悄悄写坏。
        /// </remarks>
        internal static bool IsSingleByteCompatible(Encoding encoding)
        {
            switch (encoding.CodePage)
            {
                case 1200:  // UTF-16 LE
                case 1201:  // UTF-16 BE
                case 12000: // UTF-32 LE
                case 12001: // UTF-32 BE
                case 65000: // UTF-7（同样是多字节变换编码）
                    return false;
                default:
                    return true;
            }
        }

        /// <summary>该编码是否为 UTF-8（不含 UTF-16/UTF-32；后两者由 <see cref="IsSingleByteCompatible"/> 把关）。</summary>
        internal static bool IsUtf8(Encoding encoding) => encoding.CodePage == 65001 /* utf-8 */;

        /// <summary>
        /// 采样编码一段 CJK 文本，判断 <paramref name="value"/> 是否可能落在尾字节位置。
        /// </summary>
        /// <summary>
        /// 判断 <paramref name="value"/> 是否可能出现在多字节字符的**尾字节**位置。
        /// </summary>
        /// <remarks>
        /// 必须**逐字符**编码后再看每个字符自己的尾字节。曾经的做法是把整段 CJK 文本一次性编码，
        /// 再用"引导字节 >= 0x81 就吞掉下一个字节"的状态机推断边界 —— 那是错的：
        /// GBK/Big5 的尾字节可以低到 0x40，本身就可能 >= 0x81，状态机会错位，
        /// 从而把安全的分隔符（如 GBK 下的 <c>|</c> 0x7C）误判为不安全。
        /// </remarks>
        private static bool AppearsAsTrailByte(Encoding encoding, byte value)
        {
            // CJK 统一表意文字主体区间，覆盖中日韩绝大多数常用字。
            const int Start = 0x4E00;
            const int End = 0x9FA5;

            char[] one = new char[1];
            for (int code = Start; code <= End; code++)
            {
                one[0] = (char)code;
                byte[] bytes = encoding.GetBytes(one);

                // 单字节字符不存在尾字节；多字节字符看第一段之后的位置。
                // 对 GB18030 的四字节序列，中间与末尾字节都可能与 ASCII 冲突，一并检查。
                for (int i = 1; i < bytes.Length; i++)
                {
                    if (bytes[i] == value) return true;
                }
            }
            return false;
        }

        /// <summary>粗略判断编码是否为双字节字符集。</summary>
        private static bool MayBeDoubleByte(Encoding encoding)
        {
            switch (encoding.CodePage)
            {
                case 936:   // GBK / GB2312
                case 54936: // GB18030
                case 950:   // Big5
                case 932:   // Shift-JIS
                case 949:   // 韩文
                case 51949: // EUC-KR
                case 20932: // EUC-JP
                case 50220: // ISO-2022-JP
                case 50221:
                case 50222:
                case 51932: // EUC-JP
                case 51936: // EUC-CN
                    return true;
                default:
                    return false;
            }
        }
    }
}
