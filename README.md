# CsvLite

一个面向 .NET 的小巧、高速 CSV **写入**库。它通过一个薄句柄把记录追加到文件 —— 没有行集合、没有索引、
没有列模型 —— 所以打开 14 GB 文件和打开 14 KB 文件的代价一样。

```csharp
using CsvLite;

using var csv = CsvAppender.Create("orders.csv", new[] { "id", "customer", "total", "note" });

CsvRow row = csv.CreateRow();
row.Add(1001);                    // int
row.Add("Alice, Jr.");            // 含逗号，自动加引号
row.Add(19.95m);                  // decimal
row.Add("said \"hi\"");           // 含引号，自动加引号并翻倍
csv.Append(row);                  // 行在这里被回收
```

输出：

```csv
id,customer,total,note
1001,"Alice, Jr.",19.95,"said ""hi"""
```

---

## 三件必须知道的事

### 1. `Append` 提交到缓冲，不是提交到磁盘

在 `WriteMode.Async`（默认）下，由后台线程负责写盘。字节必须真正落到文件时调用 `Flush()`：

```csharp
csv.Append(row);
csv.Flush();             // 到此刻为止追加的字节都已进入文件
```

写入失败不会当场抛出，而是在下一次 `Append`/`Flush` 时以 `CsvWriteException` 的形式暴露。
如果你希望错误就在调用线程上同步抛出，请用 `WriteMode.Direct`。

### 2. 同一线程内顺序严格，跨线程不保证

同一线程追加的行永远按顺序落到文件。不同线程的行只在**记录边界**处交错 —— 一条记录永远不会被拆开
—— 但两个线程之间的相对顺序未做规定。需要全局顺序时，请单线程追加或使用 `WriteMode.Direct`。

### 3. 编辑表头不会碰数据行

这是契约，不是疏漏：

```csharp
csv.SetHeaders(new[] { "a", "b", "c" });   // 数据行可能仍只有 2 个字段——这是合法 CSV
csv.AddHeaderColumn("extra");              // 加的是「表头列」，不是数据列
csv.RemoveHeaderColumn(1);                 // 删的是「表头单元格」，不是数据列
csv.DeleteHeaders();                       // 整行表头移除
```

表头操作只重写第一行。已有记录永远不会被补齐、截断、重新对齐，也不会拿新表头去校验；
`Headers` 只是库自己对那一行的解析结果。

---

## 编码：GBK 与 Excel

中文 Windows 上的 Excel 打开无 BOM 的 UTF-8 CSV 会乱码，因为默认按系统 ANSI 代码页解释。
需要中文 Excel 直接打开时，用 GBK：

```csharp
var csv2 = CsvAppender.Create("客户.csv",
    new[] { "编号", "客户名称", "金额", "备注" },
    new CsvOptions
    {
        Encoding = CsvEncodings.Gbk,   // 代码页 936
        WriteBom = false,              // GBK 没有 BOM，Excel 按 ANSI 代码页识别
        Overwrite = true,
    });
```

也可以直接给别的编码：

```csharp
new CsvOptions { Encoding = CsvEncodings.Gb18030 };   // 54936，含更多生僻字
new CsvOptions { Encoding = CsvEncodings.Big5 };      // 950，繁体
new CsvOptions { Encoding = CsvEncodings.Get(932) };  // Shift-JIS
new CsvOptions { Encoding = new UTF8Encoding(true) }; // 带 BOM 的 UTF-8
```

`CsvEncodings` 会自动完成 `CodePagesEncodingProvider` 注册，不需要你手动调用。
`.NET Core` 及以后默认不带 ANSI 代码页，这一步是必需的。

### 两条自动安全校验

**分隔符不会被双字节字符切坏。** 双字节编码里，一个汉字由「引导字节 + 尾字节」组成。
如果尾字节范围涵盖分隔符字节，按字节扫描就会把汉字的半个字节误当成语法符号，产出错位文件。
库会**逐字符**实测该编码的尾字节范围并拒绝不安全的分隔符。

实测尾字节范围（逐字符编码 U+4E00–U+9FA5）：

| 编码 | 尾字节范围 | `,` `;` TAB | `\|` `^` `~` `` ` `` `\` `@` |
|---|---|---|---|
| UTF-8 | 0x80–0xBF | ✅ | ✅ |
| GBK (936) | **0x40–0xFE** | ✅ | ❌ 拒绝 |
| Big5 (950) | 0x40–0xFE | ✅ | ❌ 拒绝 |
| Shift-JIS (932) | 0x40–0xFC | ✅ | ❌ 拒绝 |

所以**中文场景下请用逗号、分号或制表符**，不要用 `|`、`^`、`~`、`\`、`@`：

```csharp
// 会抛 ArgumentException：GBK 下 0x7C 可能是汉字的尾字节
new CsvOptions { Encoding = CsvEncodings.Gbk, Delimiter = (byte)'|' };

// 安全
new CsvOptions { Encoding = CsvEncodings.Gbk, Delimiter = (byte)';' };
```

**BOM 由编码自身决定。** UTF-8/UTF-16 有 BOM，GBK 没有。写 GBK 时 `WriteBom = true` 也不会产生 BOM，
所以不用担心「GBK 文件带 UTF-8 BOM」这种灾难。

**UTF-16 / UTF-32 会被拒绝。** 分隔符与行终止符是按单字节写出的，UTF-16 下逗号会变成 `2C 00`，
按单字节写就会产出无法解析的文件。库在 `Validate()` 阶段直接拒绝（含 1200/1201/12000/12001）。

> 注意：`CsvEncodings.Gbk.WebName` 返回的是 `gb2312`，这是 .NET 的历史命名，实际支持 GBK 字符集。

---

## 表头编辑的代价

| 改动 | 代价 |
|---|---|
| 编码后长度不变（改名、换序、纠错） | `O(表头)`，几十微秒 |
| 文件尚无数据记录 | `O(表头)` |
| 长度改变且已有数据 | **`O(文件)`** —— 数据区就地移位 |

增删列几乎必然改变编码后长度，因此会走慢路径。14 GB 文件大约需要 8–12 秒读写。
移位**不需要额外磁盘空间**（只需 `delta` 字节），峰值内存为 `ShiftChunkBytes`（默认 1 MB）。

实用建议：要么在开始写入前把表头定好，要么用一次 `SetHeaders` 批量改，
而不是调 N 次 `AddHeaderColumn`（N 次调用 = N 次全文件搬迁）。

移位是就地进行的，并且可以取消；被取消的移位会把文件**逐字节**恢复原状：

```csharp
csv.SetHeaders(headers, progress: new Progress<double>(p => Console.WriteLine($"{p:P0}")), ct: token);
```

代价是：如果进程在移位**过程中**被杀，文件会损坏。介意的话请先复制文件。

---

## 并发

除 `Dispose()` 之外的所有成员都可以并发调用。

```csharp
Parallel.For(0, 8, t =>
{
    for (int i = 0; i < 100_000; i++)
    {
        CsvRow row = csv.CreateRow();   // 每次迭代一个行，不共享
        row.Add(t);
        row.Add(i);
        csv.Append(row);
    }
});
csv.Flush();
```

规则：

- **一个线程同时只持有一个 `CsvRow`。** 行是临时工作区，不要把同一个实例交给多个线程。
- **`Dispose()` 时不能有并发 `Append`。** 等所有写入线程结束后再释放。
- **每个进程每个文件只能有一个写者。** 同进程内重复打开同一路径会抛异常；文件以
  `FileShare.ReadWrite` 打开，所以外部读者（Excel、`tail -f`）不会被锁在外面。
  跨进程的单写者安全性由调用方负责。
- 行由 `Append` 池化回收。之后再去碰它会抛异常，而不是悄悄写出垃圾数据。

---

## 并发是怎么实现的

每个生产者线程拥有一个私有的环形缓冲（`SlotBufferBytes`，默认 64 KiB）。`Append` 把编码好的记录拷进
自己的环，然后用 release 存储发布新的 head —— 在 x86/x64 上就是一条普通存储，无锁、无共享计数器。
由单个写线程把各个环排空进文件。

- 超过环容量的单行会在原地扩容，但只在环完全排空时才会这样做。
- 超过 `MaxProducerSlots` 的线程共用一个带锁的溢出槽；容量检查与写入必须在同一临界区内完成。
- 写线程跟不上时，待写字节超过 `SlotBufferBytes × MaxProducerSlots` 就会让生产者阻塞，从而在慢盘上
  把内存锁住。`IsBackpressured` 可以告诉你当前是否正在发生。
- 表头移位只暂停写线程。生产者继续填充自己的环，它们的字节会落在移位后的数据之后 ——
  不需要让所有生产者停下来。

`Flush()` 是一个屏障：它等待本次调用之前发布的每一个字节都被写出。
它**不便宜**（会打断每个生产者的批量攒批），所以要按批刷新，绝不逐行刷新。

---

## API

```csharp
public sealed class CsvAppender : IDisposable, IAsyncDisposable
{
    static CsvAppender Create(string path, IReadOnlyList<string>? headers = null, CsvOptions? options = null);
    static CsvAppender Open(string path, CsvOptions? options = null);

    CsvRow CreateRow();
    void   Append(CsvRow row);

    IReadOnlyList<string>? Headers { get; }
    int  HeaderCount { get; }
    void SetHeaders(IReadOnlyList<string>? headers, IProgress<double>? progress = null, CancellationToken ct = default);
    void DeleteHeaders(IProgress<double>? progress = null, CancellationToken ct = default);
    void AddHeaderColumn(string name, int index = -1, IProgress<double>? progress = null, CancellationToken ct = default);
    void RemoveHeaderColumn(int index, IProgress<double>? progress = null, CancellationToken ct = default);
    void SetHeaderColumn(int index, string name, IProgress<double>? progress = null, CancellationToken ct = default);

    long RowCount { get; }        // 本实例上成功的 Append 次数
    long BytesAppended { get; }   // 已交给写入路径
    long BytesWritten { get; }    // 已进入文件
    long PendingBytes { get; }
    long FileLength { get; }      // Flush 之后文件会有多长
    bool IsFaulted { get; }
    bool IsBackpressured { get; }
    Exception? LastError { get; }

    void Flush();
    void Flush(bool fsync);
    void Dispose();
    ValueTask DisposeAsync();
}
```

### 行类型

每个 `Add` 重载都会自动转义。不存在绕过转义的入口。

| 类型 | 默认渲染 |
|---|---|
| `string?`、`ReadOnlySpan<char>`、`ReadOnlySpan<byte>`、`char` | 原样，必要时加引号 |
| `bool` | `true` / `false` |
| `sbyte`、`short`、`int`、`long`、`byte`、`ushort`、`uint`、`ulong` | 最短往返表示 |
| `float`、`double`、`decimal` | 最短往返表示 |
| `DateTime`、`DateTimeOffset` | ISO 8601 `"O"`（可用 `DateTimeFormat` 覆盖） |
| `TimeSpan` | `"c"` |
| `Guid` | 带连字符 `"D"`（可用 `GuidFormat` 覆盖） |
| `Enum` | 枚举名（可用 `EnumFormat.Numeric` 覆盖） |
| `DateOnly`、`TimeOnly` | `"O"` —— 仅 `net8.0` 目标 |
| `AddNull()` / `null` 字符串 | 空字段 |

区域性始终是不变文化（Invariant）：逗号做小数点会把 CSV 写坏。

### 配置项

```csharp
new CsvOptions
{
    Encoding  = CsvEncodings.Gbk,     // 默认无 BOM 的 UTF-8
    Delimiter = (byte)',',            // 不能是 CR、LF 或引号字符
    NewLine   = new[] { (byte)'\n' }, // "\n"、"\r" 或 "\r\n"
    QuoteChar = '"',
    QuoteMode = QuoteMode.Minimal,    // 或 EveryField

    WriteBom  = false,
    HasHeader = true,
    Overwrite = false,                // Create 默认拒绝截断已存在的文件
    FlushOnDispose = true,
    FsyncOnDispose = false,

    DateTimeFormat = null,            // null => "O"
    GuidFormat     = null,            // null => "D"
    EnumFormat     = EnumFormat.Name,

    WriteMode        = WriteMode.Async,   // 或 Direct
    SlotBufferBytes  = 64 * 1024,
    MaxProducerSlots = 64,
    MaxRowBytes      = 16 * 1024 * 1024,
    Flush            = FlushPolicy.Batch, // 或 Sync（每批写完 fsync）
    OnWriteError     = WriteErrorPolicy.Throw,

    ShiftChunkBytes = 1024 * 1024,
    MaxHeaderBytes  = 1024 * 1024,
    FreeSpaceMarginBytes = 16 * 1024 * 1024,
}
```

`Open` 会读取第一行、补上缺失的行尾换行（否则被强杀的写入进程会把两条记录粘在一起）、
定位到文件末尾然后启动。它不扫描文件、不统计行数、不建索引、不嗅探方言 ——
请传入与创建时相同的 `CsvOptions`。

---

## 目标框架

`netstandard2.0`、`netstandard2.1`、`net8.0`。

`netstandard2.0` 上依赖 `System.Memory` 以支持 span。
所有目标都依赖 `System.Text.Encoding.CodePages` 以支持 GBK 等 ANSI 代码页。

`Utf8Formatter` 在三个目标上都可用，所以数字与日期的格式化在所有框架上都不产生分配。

---

## 构建与测试

仓库只有一个解决方案 `CsvLite.sln`，按用途分了解决方案文件夹：

```
src/       CsvLite            库（netstandard2.0、netstandard2.1、net8.0）
tests/     CsvLite.Tests      自带运行器的测试
bench/     CsvLite.Bench      吞吐与分配测量
samples/   CsvLite.Sample     可运行的演示
```

```bash
dotnet build CsvLite.sln
dotnet run --project tests/CsvLite.Tests
dotnet run --project bench/CsvLite.Bench -c Release
dotnet run --project samples/CsvLite.Sample
```

测试项目是自带运行器的（不依赖测试框架），因此可以在离线构建机上跑：
它反射扫描 `[Test]` 方法并打印通过/失败汇总。可以传一个子串做过滤：

```bash
dotnet run --project tests/CsvLite.Tests -- Conc
```

测试与示例的所有临时文件都写在仓库下的 `tmp/`，不会写系统临时目录。

---

## 这个库刻意不做的事

- **不读取、不解析 CSV。** 它只写。`Headers` 只解析表头那一行。
- **不做列对齐。** 增加表头列不会给已有行增加字段。需要这个功能就得逐条重写文件 —— 那是另一个工具。
- **不建行索引**，不持有任何与文件大小成正比的运行时状态。
- **不支持多个进程同时追加同一个文件。**
- **不能从表头移位过程中的崩溃里恢复。** 取消可以回滚，强杀不行。
