# CsvLite 设计文档 v10

> ⚠️ **已被 `design-v11.md` 取代**：v10 的"单写者"前提不成立（需求要求高并发），且缺少表头增删列。
> 本文保留作为历史决策记录；表头编辑契约、就地移位、转义规则等结论仍被 v11 继承。
>
> 状态：**设计收敛，待开工**
> 最后更新：2026-10-26
> 核心修正：本库的对象是**文件句柄的薄封装**，不是数据容器。`Open` 与 `Create` 成本相同。

---

## 1. 对象模型：它只是一个句柄

```
CsvAppender
├── FileStream        ← 唯一的"真实"东西，指向磁盘上的文件
├── byte[] header     ← 表头字节（几 KB）
├── byte[] buffer     ← 256 KB 写缓冲
└── CsvOptions        ← 分隔符、换行、格式
```

就这些。**没有行集合、没有索引、没有行数统计、没有列结构。**

对象存活期间占用的内存 = `256 KB + 表头长度`，与文件是 14 KB 还是 14 GB **完全无关**。

### 1.1 由此推出的结论

| 操作 | 本质 | 成本 |
|---|---|---|
| `Create` | 建文件 + 写表头 + 拿句柄 | **O(表头)** |
| `Open` | 拿句柄 + `Seek(末尾)` + 读表头行 | **O(表头)**，≈ 微秒 |
| `Append` | `Stream.Write` 进缓冲 | O(行) |
| `SetHeaders` 等长 | `Seek(0)` + `Write` | **O(表头)** |
| `SetHeaders` 变长 | 搬数据（物理约束，见 §5） | O(文件) |
| `Flush` | 缓冲刷盘 | O(缓冲) |

**没有一项需要把文件读进内存。** 唯一 O(文件) 的操作是"改表头且长度变了"——这是 CSV 格式的物理约束，不是实现选择。

### 1.2 `Open` 保留，且几乎零成本

既然对象只是句柄，`Open` 就是：

```csharp
var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
fs.Seek(0, SeekOrigin.End);          // 追加位置
header = ReadFirstLine(fs, MaxHeaderBytes);   // 只为知道表头是什么
```

- **不扫描全文件、不数行、不建索引、不嗅探方言**；
- 分隔符等方言由 `CsvOptions` 显式给（`Create` 时写进去，`Open` 时调用方给同样的）；
- 代价和 `Create` 一样，都是打开一个句柄。

> **技术细节**：不能用 `FileMode.Append`，因为追加模式禁止 `Seek`，而改表头必须能跳到文件头。所以用 `FileMode.OpenOrCreate` + 手动 `Seek(End)`。

---

## 2. API

```csharp
public sealed class CsvAppender : IDisposable
{
    public static CsvAppender Create(string path, string[]? headers = null, CsvOptions? o = null);
    public static CsvAppender Open(string path, CsvOptions? o = null);

    public CsvRow CreateRow();
    public void   Append(CsvRow row);

    public string[]? Headers { get; }
    public void      SetHeaders(string[]? headers,
                                IProgress<double>? progress = null,
                                CancellationToken ct = default);

    public long FileLength { get; }
    public long RowCount   { get; }     // 本次会话 Append 的次数，不统计历史
    public void Flush();
    public void Dispose();
}
```

```csharp
public sealed class CsvOptions
{
    public byte   Delimiter { get; set; } = (byte)',';
    public byte[] NewLine   { get; set; } = { (byte)'\n' };
    public char   Quote     { get; set; } = '"';
    public bool   WriteBom  { get; set; }
    public bool   HasHeader { get; set; } = true;
    public bool   Overwrite { get; set; }              // Create 遇已存在文件

    public CultureInfo Culture        { get; set; } = CultureInfo.InvariantCulture;
    public string?     DateTimeFormat { get; set; }    // null → ISO 8601 "O"
    public string?     GuidFormat     { get; set; }    // null → "D"
    public EnumFormat  EnumFormat     { get; set; } = EnumFormat.Name;

    public FlushPolicy Flush          { get; set; } = FlushPolicy.Batch;
    public int  BufferBytes     { get; set; } = 256 * 1024;
    public int  ShiftChunkBytes { get; set; } = 1024 * 1024;
    public int  MaxHeaderBytes  { get; set; } = 1024 * 1024;
}

public enum EnumFormat  { Name, Numeric }
public enum FlushPolicy { None, Batch, Sync }
```

---

## 3. `CsvRow`：全部基础类型

```csharp
public sealed class CsvRow
{
    public void Clear();
    public int  Count { get; }

    public void Add(string? text);              // null → 空字段
    public void Add(ReadOnlySpan<char> text);
    public void Add(ReadOnlySpan<byte> utf8);
    public void Add(char value);

    public void Add(bool value);
    public void Add(sbyte  value); public void Add(short  value);
    public void Add(int    value); public void Add(long   value);
    public void Add(byte   value); public void Add(ushort value);
    public void Add(uint   value); public void Add(ulong  value);
    public void Add(float  value); public void Add(double value);
    public void Add(decimal value);

    public void Add(DateTime value);  public void Add(DateTimeOffset value);
    public void Add(TimeSpan value);  public void Add(Guid value);
    public void Add(Enum value);

    public void AddNull();
}
```

| 类型 | 默认格式 | 配置 |
|---|---|---|
| `DateTime` / `DateTimeOffset` | ISO 8601 `"O"` | `DateTimeFormat` |
| `TimeSpan` | `"c"` | — |
| `Guid` | `"D"` | `GuidFormat` |
| `Enum` | 名字 `"Ok"` | `EnumFormat.Numeric` |
| `bool` | `"true"`/`"false"` | — |
| 数值 | 最短往返 | — |

- 一律 `InvariantCulture`（`Culture` 属性保留但**不支持换成逗号小数点**，会写坏 CSV）。
- `DateTimeFormat = null` 走 `Utf8Formatter` **0 分配**；自定义格式串慢约 3 倍。
- `Enum` 默认 `ToString()` 有一次分配；高频场景建议 `Add((int)status)`。

**所有 `Add` 自动转义**：先 `IndexOfAny(delim, quote, '\r', '\n')` SIMD 预扫描，干净直接 memcpy，脏才加引号并翻倍。**无绕过入口。**

---

## 4. 追加

```
Append(CsvRow) → Row 内的字节 → BlockCopy 进 256 KB 缓冲
```

| 刷盘 | 行为 |
|---|---|
| `None` | 只写托管缓冲 |
| `Batch`（默认） | 缓冲满时 `Flush()` |
| `Sync` | 每次 `Flush()` 后 `Flush(true)` |

---

## 5. 表头修改

### 5.1 三条路径

```
SetHeaders(headers)
├─ 序列化长度 == 当前表头长度  → Seek(0) + Write          O(表头)
├─ 文件没有数据行              → SetLength(0) + Write     O(表头)
└─ 长度变了且有数据            → 就地移位（§5.2）         O(文件)，内存 1 MB
```

快路径覆盖：**等长改名**、**重排/换序**（名字集合相同 → 长度必相同）、等长纠错。

### 5.2 就地移位（无 `.tmp`）

```
oldLen = H, newLen = H + delta, L = 文件长度

变长（delta > 0，从后往前搬）：
  1. Flush + Flush(true)
  2. 预检剩余空间 ≥ delta
  3. SetLength(L + delta)
  4. for (pos = L - chunk; pos >= H; pos -= chunk)     ← 必须倒序
        read  [pos, min(pos+chunk, L))  → buf
        write buf                     → pos + delta
  5. 写新表头 → [0, H + delta)
  6. Flush + Flush(true)

变短（delta < 0，从前往后搬）：先搬，再 SetLength，最后写表头
```

**第 4 步必须倒序**：正序会覆盖尚未读出的数据，静默产出错乱 CSV。

峰值内存 = `ShiftChunkBytes` = 1 MB，与文件大小无关。额外磁盘 = 仅 `delta` 字节。

### 5.3 已知代价

| 项 | 14 GB @ NVMe |
|---|---|
| 耗时 | ≈ 8~12 s（读写全文件） |
| 额外磁盘 | 仅 delta 字节 |
| 峰值内存 | 1 MB |

**两个已确认接受的后果：**

1. **移位期间崩溃 → 文件损坏且无法恢复**（数据搬到一半）；
2. **移位期间外部程序（Excel、`tail -f`）会读到错乱数据**，替换后需重开。

前提是"本进程独占写入"。若将来需要崩溃安全，加检查点日志约 80 行（§5.4）。

### 5.4 可选：崩溃恢复日志（不做）

每块记 16 B 检查点 → 14 GB 约 224 KB 日志，恢复时续搬完。约 80 行。**默认不做。**

---

## 6. `Open` 的边界情况

因为 `Open` 不扫描文件，只有两件事需要注意：

| # | 情况 | 处理 |
|---|---|---|
| **E1** | 上次写到一半被杀，末尾无换行，追加会把两行粘成一行 | `Open` 读末尾 **1 个字节**；非 `\n`/`\r` 则写一个 `\n` |
| **E2** | 文件不存在 | `OpenOrCreate` 自动创建；若 `HasHeader=true` 则需调用方随后 `SetHeaders` |

E1 是**必须做**的：它只读 1 个字节，完全符合"句柄映射"模型。没有它，崩溃后续写会产生难查的粘行数据。

`Open` 不做的事：嗅探分隔符、数行数、校验列数、扫描内容。

---

## 7. 其它正确性坑

| # | 坑 | 处理 |
|---|---|---|
| C1 | 移位正序搬数据 | 必须倒序（§5.2） |
| C2 | 无表头文件 | `HasHeader=false`，不读首行 |
| C3 | 文件只有表头无数据 | `FileLength == headerLength` → 走快路径 |
| C4 | BOM | 算表头长度时排除；`Create` 按选项写 |
| C5 | CRLF | `NewLine` 显式配置，不嗅探 |
| C6 | 表头超 `MaxHeaderBytes` | 抛明确异常 |
| C7 | 多线程写 | 单写者，文档明确 |
| C8 | `Create` 覆盖已有 14 GB 文件 | 默认不覆盖，需 `Overwrite=true` |
| C9 | 移位时磁盘不足（变长） | 预检剩余空间，立即抛 |

---

## 8. 多目标（netstandard2.0 / 2.1 / net8.0）

```xml
<TargetFrameworks>netstandard2.0;netstandard2.1;net8.0</TargetFrameworks>
```

| 需要 | ns2.0 | 替代 |
|---|---|---|
| `Span/ReadOnlySpan` | ✗（ns2.1 ✓） | `System.Memory` 包 |
| `Utf8Formatter` | 待实测 | 手写定点格式化（约 40 行） |
| `Span.IndexOfAny(b,b,b,b)` | ✓ | 直接用 |
| `Encoding.UTF8.GetBytes(string, Span<byte>)` | ✗ | `GetByteCount` + 池化 `byte[]` |
| `DateOnly`/`TimeOnly` | ✗（net6+） | 仅在 net6+ 目标提供 |
| `DriveInfo.AvailableFreeSpace` | ✓ | 空间预检 |

条件编译集中在 `Utf8Format.cs`、`IoCompat.cs`。

---

## 9. 性能目标

| 操作 | 目标 |
|---|---|
| `Create` / `Open` | ≤ 1 ms（只碰表头） |
| `Append(CsvRow)` | ≥ 2.5 GB/s（内存侧） |
| 单字段（无转义 / 需转义） | ≥ 300 M / 100 M 字段每秒 |
| `SetHeaders` 等长或重排 | ≤ 50 μs，与文件大小无关 |
| `SetHeaders` 变长 | 读+写全文件，内存 1 MB |
| 每行分配 | **0 B**（`Enum`/自定义格式除外） |

---

## 10. 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| **M0** | 三目标骨架 + 基准 + 实测 `Utf8Formatter` 在 ns2.0 | 基线 |
| **M1** | `Create`/`Open` + 句柄 + 表头读写 + E1/E2 | 14 GB 打开 ≤ 1 ms |
| **M2** | `CsvRow` 全类型 + 可配置格式 + 转义 | ≥ 300 M 字段/s，0 分配 |
| **M3** | `SetHeaders` 快路径（等长/重排/无数据） | ≤ 50 μs |
| **M4** | 就地移位 + 进度/取消 + 空间预检 | 14 GB 峰值内存 ≤ 2 MB |
| **M5** | 边界测试（空字段、CJK、CRLF、BOM、超大字段、14 GB 实测） | — |

核心预计 **550~700 行**。

---

## 11. 待确认（仅剩两个小的）

1. `DateOnly`/`TimeOnly` 在 netstandard2.0 不存在 —— 只在 net6+ 目标提供这两个重载，可以吗？
2. `Open` 时如果调用方给的 `Delimiter` 与文件实际的**不一致**，会写出错位的文件。需要一个"校验首行分隔符并抛异常"的开关吗？（只读首行，不违反内存约束）
