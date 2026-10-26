# CsvLite 设计文档 v11

> 状态：**方案待评审**（未开工）
> 取代：`design.md`（v10）。v10 的"单写者"前提已被推翻，其余结论（句柄薄封装、就地移位、转义规则）继续有效。
> 本次变更主线：**异步写线程 + 每线程段缓冲**（高并发/高性能追加）、**表头增删列**、**表头编辑与数据列解耦**。

---

## 0. 一页摘要

```
                    ┌────────────── 生产者（任意线程） ──────────────┐
 Append(CsvRow) ──► │ 取本线程 Segment（64 KB）→ 拷一行 → Length+=n │   ← 无锁（仅本核写）、每行 0 分配
                    └───────────────────────┬──────────────────────┘
                              段满 / Flush / Dispose
                                            ▼
                              ┌──── ConcurrentQueue<Segment> ────┐   ← 段粒度交接（约 48k 次/秒 量级）
                              └───────────────┬─────────────────┘
                                              ▼
                              ┌──────── 写线程（唯一碰文件）────────┐
                              │ 批量出队 → FileStream.Write        │
                              │ 段归还池 / 计数 / 错误暂存         │
                              └───────────────┬─────────────────┘
                                              ▼
                                          磁盘文件
```

| 你的需求 | v11 落点 |
|---|---|
| 编辑 / 删除 / 设置表头 | `SetHeaders` / `DeleteHeaders` / `SetHeaderColumn` |
| 添加表头列、删除表头列 | `AddHeaderColumn` / `RemoveHeaderColumn` |
| 编辑表头时不管数据列是否对应 | §6.1 契约：**只改第一行，数据行零改动、零校验** |
| 行对象追加（避开分隔符问题） | `CsvRow`：全类型 `Add` 重载，内部自动转义 |
| 高并发 | §4 每线程段缓冲 + 段粒度交接；写盘单线程 |
| 高性能追加 | 热路径 0 分配、原子操作落在本核独占缓存行，单线程 ≥ 1.5 GB/s |

**三个必须提前接受的性质：**

1. **`Append` 是"提交到队列"，不是"已落盘"。** 写盘异常异步暴露（下一次 `Append`/`Flush` 抛），需要落盘时显式 `Flush()`。
2. **跨线程的行顺序按段粒度交错**（默认 64 KB）。同线程内严格保序；全局顺序在并发写入下本就没有良定义。
3. **表头增删列会改变表头字节长度 → 触发 O(文件) 就地移位**（14 GB ≈ 8–12 s）。这是 CSV 格式的物理约束，不是实现选择。

---

## 1. 需求 → 设计缺口（相对 v10）

| 需求 | v10 | v11 |
|---|---|---|
| 设置/删除表头 | `SetHeaders` | 保留，补 `DeleteHeaders` |
| **增删表头列** | ✗ | 新增，走"重写第一行"统一原语 |
| **编辑表头不看数据列** | 表述含糊 | 写成 §6.1 明确契约（不补齐/不截断/不校验/不重排） |
| 行对象追加 | `CsvRow` ✓ | 保留，改为**池化 + Append 后自动回收** |
| **高并发** | ✗（v10 C7：单写者，文档明确） | 异步写线程 + 每线程段 |
| 高性能追加 | ≥ 2.5 GB/s（内存侧） | 热路径无锁；单线程 ≥ 1.5 GB/s，多线程受磁盘限制 |

---

## 2. 对象模型与线程拓扑

```
CsvAppender                     ← 一个文件句柄的管理者
├── FileStream                  仅写线程访问（除移位期间的管理员屏障）
├── WriteThread                 1 个，后台线程，懒启动
├── ConcurrentQueue<Segment>    段队列（MPSC）
├── SegmentPool                 ArrayPool<byte> 支撑，默认 64 KB/段
├── ProducerSlot[]              每线程一条，数量 = 并发写线程数
├── HeaderLine                  当前表头字节（含 BOM 偏移，几 KB）
└── CsvOptions                  方言 + 并发 + 刷盘参数
```

**内存占用** = `线程数 × 64 KB + 队列水位上限（默认 64 MB，只在写盘跟不上时才会涨）+ 表头`。与文件大小无关。

**线程数只有两种：**
- **生产者**：任意线程调用 `Append`，不阻塞在 IO 上（背压除外），不碰 `FileStream`。
- **写线程**：唯一碰文件句柄的线程；顺序消费段队列。
- 管理员屏障（表头移位）：**借用调用线程**，暂停写线程后独占文件，完成后归还。

### 2.1 与 v10 的关键差异

| | v10 | v11 |
|---|---|---|
| `Append` | 直接写 256 KB 共享缓冲（`lock`） | 写本线程段（无锁） |
| 文件访问 | 调用线程 | 专用写线程 |
| 表头移位 | 调用线程直接改文件 | 同上，但要先暂停写线程 |
| `Flush` | 刷缓冲 | **屏障**：排空所有生产者段 + 队列 + 写线程 |
| 异常 | 同步抛出 | 暂存，下次 `Append`/`Flush` 抛出 |
| 线程数 | 1 | 1 + 1（写线程） |

---

## 3. 追加路径总览

```
Append(row)
  1. 校验 row.Owner == this、row 未被回收
  2. seg = 本线程 slot.Segment            （线程本地，无同步读）
  3. if (row.ByteCount > seg.Free) → 发布 seg，换新段（可能被背压阻塞）
  4. InFlight = 1                          （release 写）
  5. if (seg.Stolen) → InFlight = 0，回到 2 （屏障抢走了段）
  6. memcpy(row.Bytes → seg.Buf + seg.Length)   ← 唯一的行级成本
  7. seg.Length += n；InFlight = 0
  8. row 归还池
```

第 6 步是热路径的全部内容：一次受段容量约束的 `memcpy`（≤ 64 KB，典型 100 B ≈ 10 ns）。

---

## 4. 生产者侧

### 4.1 `CsvRow`：行对象

```csharp
public sealed class CsvRow
{
    public CsvAppender Owner { get; }      // 方言绑定，跨 appender 复用 → 抛异常
    public int  Count { get; }             // 字段数
    public int  ByteCount { get; }         // 已序列化字节数（不含结尾换行）
    public bool IsRecycled { get; }

    public void Clear();

    public void Add(string? text);                 // null → 空字段
    public void Add(ReadOnlySpan<char> text);
    public void Add(ReadOnlySpan<byte> utf8);
    public void Add(char value);

    public void Add(bool value);
    public void Add(sbyte  value);  public void Add(short  value);
    public void Add(int    value);  public void Add(long   value);
    public void Add(byte   value);  public void Add(ushort value);
    public void Add(uint   value);  public void Add(ulong  value);
    public void Add(float  value);  public void Add(double value);
    public void Add(decimal value);

    public void Add(DateTime value);  public void Add(DateTimeOffset value);
    public void Add(TimeSpan value);  public void Add(Guid value);
    public void Add(Enum value);
    public void AddNull();             // 显式空字段（与 Add((string?)null) 等价）
}
```

**设计要点：**

| 项 | 决定 | 理由 |
|---|---|---|
| 何时编码 | `Add` 时立刻编码进行自带的 scratch | 单遍扫描；`Append` 只需 `memcpy` |
| 行归属 | 创建时绑定 `CsvAppender`（分隔符/引号/换行/格式都取自它） | 编码需要方言；`Append` 时校验 owner 防错用 |
| 复用 | `CreateRow()` 从池取，`Append` 后**自动归还** | 每行 0 分配 |
| 归还后再用 | 抛 `InvalidOperationException` | 静默复用是最难查的 bug |
| 跨线程 | 行实例不得被两个线程同时使用；`Append` 可在别的线程调用 | 行内部无共享状态 |

**转义（沿用 v10，无绕过入口）**：先 `IndexOfAny(delim, quote, '\r', '\n')` SIMD 预扫描；干净 → 直接 `memcpy`；脏 → 加引号 + 引号翻倍。

**格式规则（沿用 v10 §3）：**

| 类型 | 默认 | 可配置 |
|---|---|---|
| `DateTime` / `DateTimeOffset` | `"O"` | `DateTimeFormat` |
| `TimeSpan` | `"c"` | — |
| `Guid` | `"D"` | `GuidFormat` |
| `Enum` | 名字 | `EnumFormat.Numeric` |
| `bool` | `true`/`false` | — |
| 数值 | 最短往返（`Utf8Formatter`） | — |
| `DateOnly` / `TimeOnly` | 仅在 `net8.0` 目标提供重载 | — |

`null` 与 `AddNull()` 都产出**空字段**（不是字面量 `NULL`，也不加引号）。三种"空"在 CSV 里不可区分，不引入额外约定。

### 4.2 `Segment`：段

```csharp
sealed class Segment
{
    public byte[] Buf;        // ArrayPool 租借，默认 64 KB
    public int    Capacity;   // = Buf.Length（大行段可能大于 SegmentBytes）
    public int    Length;     // 已提交字节数（生产者单写，Volatile）
    public int    InFlight;   // 生产者是否正在往里拷（Volatile，Dekker 握手用）
    public int    Stolen;     // 屏障请求回收（Volatile，Dekker 握手用）
    public int    Published;  // CAS 0→1，防重复入队
    public Segment? Next;     // 池空闲链
}
```

> 不变量：屏障只在观察到 `InFlight == 0` 之后读 `Length`，所以 `Length` 不需要额外的同步；`Published` 是唯一用 `Interlocked` 的字段（每段一次，不在热路径）。

**整行必须落在同一段内**：段容量不足时，行不去拆分（拆分会让别的线程的段插在中间，把行撕成两半），而是让该行独占一个新段（"大行段"）。因此 `有效段容量 = max(SegmentBytes, 最大行字节数)`。

> 大行段（> 64 KB 的行）走"私有块"路径：从池里按需租大块，**不参与屏障抢占**（屏障只抢每线程的常规段），从而把 `InFlight` 窗口限制在 ≤ 64 KB 的 `memcpy`（约 2–4 μs）。

### 4.3 `ProducerSlot`：每线程一条

```csharp
sealed class ProducerSlot
{
    public CsvAppender Owner;
    public int         ThreadId;
    public Segment?    Segment;   // volatile：本线程当前写段
    public readonly object Gate;  // 仅保护 Segment 指针交换（每段一次，不是每行）
}
```

查表用 `[ThreadStatic]` 缓存最近一条 slot，切换 appender 时再扫一遍注册表（表长 = 并发线程数，通常 < 16）。新线程首次 `Append` 时注册（一次性加锁，之后无锁）。

### 4.4 发布协议（唯一需要小心的并发代码）

生产者发布满段。**唯一出口**是"换新段"，无论本次是否由自己发布——这条是血泪教训，把槽位修复放在 CAS 判断里面会导致"槽位仍指向已入队的段，下次 Append 写进已发布的段"。

```csharp
// 前提：seg.InFlight == 0，且 seg 归属 slot
void Retire(Segment seg, ProducerSlot slot)
{
    if (Interlocked.Exchange(ref seg.Published, 1) == 0)   // 首次发布
    {
        EnterBackpressureIfNeeded();        // 只在首次发布时阻塞
        _queue.Enqueue(seg);
        Interlocked.Add(ref _publishedBytes, seg.Length);   // 每段一次，不在热路径
        WakeWriterIfParked();
    }
    // ── 无论谁发布，都必须走到这里 ──
    lock (slot.Gate)
        if (ReferenceEquals(slot.Segment, seg))
            slot.Segment = _pool.Rent();
}
```

管理员屏障抢占（`Flush` / `Dispose` 使用）：

```csharp
void StealAndPublish(ProducerSlot slot)
{
    Segment? old;
    lock (slot.Gate) old = slot.Segment;
    if (old is null || Volatile.Read(ref old.Length) == 0) return;

    Volatile.Write(ref old.Stolen, 1);         // release
    SpinWait spin = default;
    while (Volatile.Read(ref old.InFlight) != 0) spin.SpinOnce();   // 窗口 ≤ 一次 memcpy

    Retire(old, slot);                          // 与生产者共用同一出口
}
```

生产者侧对应的 Dekker 握手（每行执行）：

```csharp
retry:
    Segment seg = Volatile.Read(ref slot.Segment);
    if (row.ByteCount > seg.Capacity - seg.Length) { Retire(seg, slot); goto retry; }

    Volatile.Write(ref seg.InFlight, 1);       // release
    if (Volatile.Read(ref seg.Stolen) != 0)    // acquire
    {
        Volatile.Write(ref seg.InFlight, 0);
        goto retry;                            // 整行在新段重写
    }
    CopyInto(seg, row);                        // 唯一的行级成本
    Volatile.Write(ref seg.Length, seg.Length + row.ByteCount);
    Volatile.Write(ref seg.InFlight, 0);
```

> 两侧共享 `InFlight` / `Stolen` 这两个内存位置，构成标准的进程内 Dekker 互斥：任一方的"我占了"写与"你占了吗"读之间，因为读写的是同一对位置，x86/x64 的 TSO 与 .NET 的 `Volatile` 语义足以保证至少一方看到对方，不会两边都以为对方没动。**这里是 `Volatile` 而非 `Interlocked`**——热路径上省掉 `lock` 前缀，代价是必须把 `MemoryBarrier` 写在正确位置（ARM64 由 `Volatile` 自身的 `dmb` 语义兜底）。

**不变量 2 的证明要点**：生产者只在 `seg.Length` 不足时才调 `Retire`，而 `Retire` 内部先做 CAS 再阻塞；此刻 `InFlight == 0`。因此"`InFlight == 1`"与"生产者阻塞"不会同时成立，屏障的等待窗口被限制在单次 `memcpy`（≤ 64 KB，2–4 μs）。

**四条不变量（实现和测试必须钉死）：**

1. **一个段在任一时刻只有一个所有者**：生产者或写线程，二者不重叠；所有权经 `Published` 的 CAS 单向传递。
2. **`InFlight == 1` 时生产者绝不阻塞**（背压在 `InFlight` 归零后做），所以屏障的等待有界。
3. **发布顺序 = 线程内行顺序**：先 `Enqueue` 再换段指针（`Retire` 的出口顺序固定）。
4. **`Stolen` 一旦置位，生产者本行作废、整行在新段重写**（不是"接着写"，半个行会撕裂）。

**不需要做的优化**：段字段**不必**做缓存行填充。稳态下每个段只被它所属的那一个生产者线程碰，零跨核共享；屏障只在 `Flush`/`Dispose` 时碰一次。为一年几次的握手付每段 128 B 的填充成本不划算。

> `_segments` 数组本身只在构造期发布（`Volatile.Write` 一次），之后只读，不需要无锁队列那套"引用必须在收集前暴露"的双重技巧。


### 4.5 背压

| 参数 | 默认 | 作用 |
|---|---|---|
| `SegmentBytes` | 64 KB | 每线程段大小；越小交错越细、交接越频繁 |
| `MaxQueuedBytes` | 64 MB | 队列水位上限 |

`BytesQueued > MaxQueuedBytes` → 生产者自旋 + 让出（`SpinWait` → `Thread.Sleep(0)` → `Sleep(1)`），直到写线程把水位降下来。

**为什么必须有**：写盘慢于生产时（机械盘、网络盘、冷缓存），无界队列会把内存吃光。水位 64 MB 对应"写线程落后 64 MB"，通常意味着磁盘已经跟不上，此时让生产者变慢是正确的反馈。

### 4.6 行顺序语义（写进 README）

| 场景 | 保证 |
|---|---|
| 同一线程依次 `Append` | 严格保序 |
| 不同线程之间 | **按段（默认 64 KB）粒度交错**，行本身永不撕裂 |
| 需要全局严格顺序 | 调用方自行串行化，或 `WriteMode = Direct`（锁内直写，全局有序） |

---

## 5. 写入侧（写线程）

### 5.1 主循环

```csharp
void WriteLoop()
{
    var batch = new List<Segment>(64);
    while (true)
    {
        if (!TryDrainBatch(batch)) { Park(); continue; }    // 空队列 → 睡到被唤醒
        foreach (var seg in batch)
        {
            _fs.Write(seg.Buf, 0, seg.Length);
            _writtenLocal += seg.Length;
            Volatile.Write(ref _writtenBytes, _writtenLocal);
            _pool.Return(seg);
        }
        batch.Clear();
        if (_options.Flush == FlushPolicy.Sync) _fs.Flush(true);
    }
}
```

- 线程 `IsBackground = true`：不会阻止进程退出。
- 段大小 64 KB 远大于 `FileStream` 内部缓冲，写放大会自动批量；不需要额外的 256 KB staging。
- 写线程**永远不碰表头**，也不做任何 Seek；只在管理员屏障期间被暂停。
- `_publishedBytes` / `_writtenBytes` 是**字节级单调计数**：段落入队时 `Interlocked.Add`，写完后由写线程累加本地值再 `Volatile.Write` 发布。**每段一次，不在行热路径上**。`PendingBytes = _publishedBytes - _writtenBytes`。

### 5.2 唤醒策略

`Park` 前置 `_writerParked = 1`（volatile），生产者只在看到该标志为 1 时才 `SemaphoreSlim.Release()`。正常写盘期间生产者的唤醒成本 = 一次 volatile 读（约 1 ns），避免每次交接都进内核。

### 5.3 错误传播（异步语义的代价）

写线程捕获所有异常 → 存 `_firstError` → 置 `_faulted = 1` → 退出循环（不再消费）。

| 触发点 | 行为 |
|---|---|
| `Append` | 开头检查 `_faulted`（volatile 读），置位则抛 `CsvWriteException(inner: firstError)` |
| `Flush` / `FlushAsync` | 同上 |
| `Dispose` | 同上，且保证文件句柄被关闭 |
| `LastError` | 属性，任何时候可读，不抛 |

`OnWriteError = Stop` 时不抛，只停写线程 + 记录错误，供"日志型"调用方使用。

### 5.4 `Flush()`：真正的屏障

核心是**先抢段、后记水位**这个顺序，而不是"记水位、抢段、再记一次"：

```csharp
void Flush(bool fsync)
{
    ThrowIfFaulted();
    foreach (var slot in Slots) StealAndPublish(slot);   // 先把所有半段提交进队列
    long target = Volatile.Read(ref _publishedBytes);     // 抢完之后才取水位
    WaitForWritten(target);                               // 等写线程追平到该水位
    if (fsync) _fs.Flush(true);
}
```

**正确性要点（一个真实的竞态）：**

`Flush` 抢完段后，某个生产者可能在**同一纳秒**发布了一个新段，`_publishedBytes` 因此比 `target` 大——这没关系：`WaitForWritten(target)` 只等"字节数追平到 target"，新段晚一点写不影响正确性（`Flush` 的语义是"此前的都已落盘"，不是"之后的不许存在"）。

真正会挂住的情形是反过来：**先取水位再抢段**，然后去等一个永远不存在的中间值。所以顺序必须是"抢段 → 取水位 → 等"。

另加一条保险：若 `target` 在等待期间因为 `_faulted` 被置位而永远到不了，`WaitForWritten` 也必须能被写线程的故障唤醒（用同一个 `ManualResetEventSlim` 同时做"有进展"和"已故障"的唤醒源），否则 `Flush` 会在 IO 失败后死等。

- `Flush()` **不是**廉价操作：它要打断所有生产者的段批处理（每线程一次 `SpinOnce` 级别等待 + 一次入队）。高频调用会把并发优势抹掉。文档要明确：**按批 Flush，不要逐行 Flush。**
- `FlushAsync` 用 `TaskCompletionSource` 挂在"`_writtenBytes ≥ target` 或 `_faulted`"两种完成条件上，避免占用调用线程。

---

## 6. 表头子系统

### 6.1 契约：**"编辑表头时，不管数据列是否对应"**

这一条是你需求里最容易被实现成"好心办坏事"的地方。v11 把它写成不可协商的契约：

| # | 库做 | 库**不做** |
|---|---|---|
| 1 | 只替换文件第一行的字节（BOM 之后） | 不读、不扫、不改任何数据行 |
| 2 | 允许表头列数 ≠ 数据行字段数 | 不校验列数一致性，不报警告 |
| 3 | 允许表头变长/变短（走移位） | 不补齐、不截断、不按列名重排数据 |
| 4 | `Headers` 返回库自己解析出的快照 | 不尝试推断"哪个列对应哪列" |
| 5 | 加列 → 数据行仍然少一列；删列 → 数据行仍然多一列 | 这两种输出都是**合法 CSV**，不视为错误 |

**推论**：`AddHeaderColumn` / `RemoveHeaderColumn` 操作的是**表头行里的单元格**，不是"数据列"。删了表头第 2 列，数据行第 2 个字段原封不动。

> 如果将来需要"按列名重排数据"，那是另一个操作（`ReconcileColumns`），成本 O(文件) 且必须解析每一行，不在本库轻量定位内。**不做，也不预留。**

### 6.2 API

底层只有一个原语：`ReplaceHeaderLine(ReadOnlySpan<byte> line, ...)`。以下全部是它的语法糖。

```csharp
public IReadOnlyList<string>? Headers { get; }   // 只读快照，无表头 → null
public int  HeaderCount { get; }

public void SetHeaders(IReadOnlyList<string>? headers,
                       IProgress<double>? progress = null,
                       CancellationToken ct = default);

public void DeleteHeaders(IProgress<double>? progress = null, CancellationToken ct = default);

public void AddHeaderColumn(string name, int index = -1,      // -1 = 追加到末尾
                            IProgress<double>? progress = null, CancellationToken ct = default);

public void RemoveHeaderColumn(int index,
                               IProgress<double>? progress = null, CancellationToken ct = default);

public void SetHeaderColumn(int index, string name,           // 单列改名 = "编辑表头"
                            IProgress<double>? progress = null, CancellationToken ct = default);
```

异步变体：`Task SetHeadersAsync(...)` 等（移位 10 s 级别时不要占着调用线程）。

**索引规则**：`AddHeaderColumn` 的 `index ∈ [0, HeaderCount]`（`HeaderCount` 表示追加）；`Remove/Set` 的 `index ∈ [0, HeaderCount)`。越界 → `ArgumentOutOfRangeException`。

**语义细节：**

| 情况 | 行为 |
|---|---|
| `SetHeaders(null)` | 等价 `DeleteHeaders()` |
| `DeleteHeaders()` 在有数据时 | 移走整行表头，O(文件) |
| `DeleteHeaders()` 只剩一行 | 清空文件，O(表头) |
| `RemoveHeaderColumn` 删掉最后一列 | 等价 `DeleteHeaders()`（不留空行） |
| `AddHeaderColumn` 时无表头 | 等价 `SetHeaders([name])` |
| `HasHeader = false` 的文件上 `SetHeaders/AddHeaderColumn` | 在文件头**插入**一行表头（O(文件) 移位），本次会话按有表头处理 |
| 新列名含分隔符/引号/换行 | 自动按同一套转义规则加引号 |

### 6.3 三条路径（成本分级）

```
ReplaceHeaderLine(newLine)
├─ newLen == curLen                  → Seek(BomLen) + Write        O(表头)，≤ 50 μs
├─ 文件无数据行（L == 表头长度）      → SetLength(0) + Write        O(表头)
└─ 长度变了且有数据                  → 就地移位（§6.4）             O(文件)
```

快路径覆盖：**等长改名**、**重排换序**（名字集合相同 → 长度必相同）、等长纠错、任何不改变字节数的编辑。

> 增删列几乎必然改变表头长度 → **必走慢路径**。所以推荐调用方：要么在写数据前把表头定稿，要么批量改一次 `SetHeaders`，而不是逐个 `AddHeaderColumn`（N 次加列 = N 次全文件搬迁）。

### 6.4 就地移位（含**回滚**和进度）

记 `H` = 旧表头字节长度，`L` = 文件长度，`delta` = 新长度 − 旧长度，`D = |delta|`。

**提交点是"写完新表头"，不是"搬完数据"**——在此之前，原文件内容在语义上仍然完整，因此**取消可以精确回滚**（v10 没做这件事，v11 加上）。

#### 变长（delta > 0，倒序搬，否则覆盖未读数据）

```
1. 管理员屏障：暂停写线程，排空队列，fs.Flush(true)，Position = L
2. 预检：目标盘可用空间 ≥ delta（不足立即抛，不做任何修改）
3. fs.SetLength(L + delta)
4. moved = 0
   for (pos = L; pos > H; ) {
       take = min(ShiftChunkBytes, pos - H)
       src  = pos - take
       ReadAt(src, buf, take);  WriteAt(src + delta, buf, take)
       moved += take; pos = src
       Report(moved / (L - H));  ct.ThrowIfCancellationRequested();   ← 唯一的取消点
   }
5. WriteAt(BomLen, newHeader)         ← 提交点
6. fs.Flush(true)，恢复写线程
```

**回滚**（第 4 步被取消时）：已搬的 `moved` 字节现在位于文件尾部 `[L-moved+delta, L+delta)`，把它们**倒序**搬回 `[L-moved, L)`，再 `SetLength(L)`。倒序同样是为了不覆盖尚未复制的数据；由于每段目的区在搬回过程中都落在已读区之后，回滚是**逐字节精确**的。

#### 变短（delta < 0，正序搬）

```
1~3 同上（不扩容）
4. for (pos = H; pos < L; ) {
       take = min(chunk, L - pos)
       ReadAt(pos); WriteAt(pos + delta, take)      // 目的区始终落在已读区之前，安全
       moved += take; pos += take
       Report / ct 检查
   }
5. SetLength(L + delta)
6. WriteAt(BomLen, newHeader)         ← 提交点
```

**回滚**（第 5 步之前取消）：把 `[H+delta, H+delta+moved)` **倒序**搬回 `[H, H+moved)`。倒序是必须的（目的区在源区之后）。

#### 成本与峰值

| 项 | 14 GB @ NVMe | 说明 |
|---|---|---|
| 耗时 | 8–12 s | 读写全文件 |
| 额外磁盘 | 仅 `delta` 字节 | 不产生 `.tmp` |
| 峰值内存 | `ShiftChunkBytes`（默认 1 MB） | 与文件大小无关 |
| 可取消 | 是，且**精确回滚** | 回滚本身失败（磁盘错）才置 Faulted |

### 6.5 屏障与并发的交互（**最容易写错的地方**）

这里有两个不同的屏障，不要混：

| 屏障 | 涉及谁 | 谁等待 | 为什么 |
|---|---|---|---|
| **Flush 屏障** | 生产者 + 写线程 | 调用线程等 `_writtenBytes` 追平 | 要把半段提交并落盘 |
| **管理员屏障**（表头移位） | **只需写线程** | 调用线程自己干活 | 生产者**不需要**被拦 |

**关键洞察：表头移位不需要停生产者。** 生产者只往段里写字节，既不碰文件也不碰表头；它们的段在移位结束后照常追加到新的文件末尾，位置正确。所以：

```
SetHeaders(变长):
  1. _paused = 1                // 写线程停止消费
  2. 等写线程确认已停（它自己的 staging 已 flush，Position = L）
  3. 移位
  4. 更新 _headerLine / 计数，Position = 新末尾
  5. _paused = 0，唤醒写线程
```

好处：移位期间生产者仍在跑（只是段会积压），不必实现"全体生产者暂停"，少一大类死锁。

代价：移位期间生产者会撞上背压（队列水位 64 MB → 写线程停着不排，生产者很快被挡住）。这是**正确**反馈，但要写进文档：

> 表头变长移位期间，若积压超过 `MaxQueuedBytes`，`Append` 会阻塞到移位结束。预估 10 s 级操作时，请先把生产者停下来，或调大水位。

**死锁检查表：**

| 场景 | 是否死锁 | 原因 |
|---|---|---|
| 移位时生产者撞背压 | 否 | 移位不等生产者 |
| Flush 屏障等 `InFlight` | 否 | `InFlight=1` 期间生产者不阻塞（§4.4 不变量 2） |
| `Dispose` 时有线程在 `Append` | 文档禁止 | `Dispose` 要求无并发 `Append`（见 §10） |
| 两个线程同时改表头 | 否 | `_adminGate`（`SemaphoreSlim(1,1)`）串行化 |

### 6.6 边界情况

| # | 情况 | 处理 |
|---|---|---|
| H1 | 移位正序搬 | 禁止；变长必须倒序（同 v10 C1） |
| H2 | 无表头文件 | `HasHeader=false` 不读首行；显式设表头则插到文件头 |
| H3 | 只有表头无数据 | `L == 表头长度` → 快路径 |
| H4 | BOM | 表头从 BOM 之后开始；长度计算排除 BOM；`Create` 按选项写 |
| H5 | 表头超 `MaxHeaderBytes` | 抛 `CsvHeaderTooLargeException` |
| H6 | 新表头导致长度未变但语义变了 | 快路径，正确 |
| H7 | 移位中磁盘满 | 预检 + 移位中 `IOException` → 尝试回滚 → 仍失败则 Faulted |
| H8 | 取消后继续用 | `Append`/`Flush` 正常（已回滚）；若回滚失败 → `IsFaulted`，一切操作抛 |
| H9 | 表头行本身是超长行（> `MaxHeaderBytes`） | 打开时就抛，不进入半可用状态 |

---

## 7. 完整 API

```csharp
public sealed class CsvAppender : IDisposable
#if NETSTANDARD2_1_OR_GREATER || NET8_0_OR_GREATER
    , IAsyncDisposable
#endif
{
    // ── 生命周期 ─────────────────────────────────────────────
    public static CsvAppender Create(string path, IReadOnlyList<string>? headers = null, CsvOptions? options = null);
    public static CsvAppender Open(string path, CsvOptions? options = null);

    // ── 行 ──────────────────────────────────────────────────
    public CsvRow CreateRow();
    public void   Append(CsvRow row);

    // ── 表头 ────────────────────────────────────────────────
    public IReadOnlyList<string>? Headers { get; }
    public int  HeaderCount { get; }
    public void SetHeaders(IReadOnlyList<string>? headers, IProgress<double>? progress = null, CancellationToken ct = default);
    public void DeleteHeaders(IProgress<double>? progress = null, CancellationToken ct = default);
    public void AddHeaderColumn(string name, int index = -1, IProgress<double>? progress = null, CancellationToken ct = default);
    public void RemoveHeaderColumn(int index, IProgress<double>? progress = null, CancellationToken ct = default);
    public void SetHeaderColumn(int index, string name, IProgress<double>? progress = null, CancellationToken ct = default);

    public Task SetHeadersAsync(IReadOnlyList<string>? headers, IProgress<double>? progress = null, CancellationToken ct = default);
    // DeleteHeadersAsync / AddHeaderColumnAsync / RemoveHeaderColumnAsync / SetHeaderColumnAsync 同构

    // ── 状态 ────────────────────────────────────────────────
    public string Path { get; }
    public long RowCount { get; }        // 本会话 Append 成功的次数
    public long BytesAppended { get; }   // 已提交进队列的字节
    public long BytesWritten { get; }    // 已写入 FileStream 的字节
    public long PendingBytes { get; }    // = BytesAppended - BytesWritten
    public long FileLength { get; }      // 磁盘长度 + PendingBytes（"Flush 后会有多长"）
    public long DurableLength { get; }   // 真正落盘（fsync 过）的字节，用于对账
    public bool IsFaulted { get; }
    public bool IsBackpressured { get; }   // 生产者此刻是否会被背压挡住（= PendingBytes > MaxQueuedBytes）
    public Exception? LastError { get; }

    // ── 刷盘 ────────────────────────────────────────────────
    public void Flush();
    public void Flush(bool fsync);
    public Task FlushAsync(CancellationToken ct = default);

    // ── 释放 ────────────────────────────────────────────────
    public void Dispose();                 // 隐式 Flush + 停写线程 + 关文件
    public ValueTask DisposeAsync();       // 同上，不阻塞调用线程
}
```

`FileLength` 与 `DurableLength` 必须分开：前者是"逻辑上文件已经有这么长"，后者是"真正刷到盘上了"。混用会让调用方在崩溃后对不上账。

---

## 8. `CsvOptions`

```csharp
public sealed class CsvOptions
{
    // ── 方言 ────────────────────────────────────────────────
    public byte   Delimiter      { get; set; } = (byte)',';
    public byte[] NewLine        { get; set; } = { (byte)'\n' };
    public char   QuoteChar      { get; set; } = '"';
    public QuoteMode QuoteMode   { get; set; } = QuoteMode.Minimal;   // Minimal | EveryField

    // ── 文件 ────────────────────────────────────────────────
    public bool WriteBom { get; set; }
    public bool HasHeader { get; set; } = true;
    public bool Overwrite { get; set; }                    // Create 遇已存在文件
    public bool FlushOnDispose { get; set; } = true;
    public bool FsyncOnDispose { get; set; }

    // ── 格式 ────────────────────────────────────────────────
    public string?     DateTimeFormat { get; set; }        // null → "O"
    public string?     GuidFormat     { get; set; }        // null → "D"
    public EnumFormat  EnumFormat     { get; set; } = EnumFormat.Name;

    // ── 并发 / 性能 ──────────────────────────────────────────
    public WriteMode WriteMode        { get; set; } = WriteMode.Async;   // Async | Direct
    public int       SegmentBytes     { get; set; } = 64 * 1024;
    public long      MaxQueuedBytes   { get; set; } = 64L * 1024 * 1024;
    public FlushPolicy Flush          { get; set; } = FlushPolicy.Batch;
    public WriteErrorPolicy OnWriteError { get; set; } = WriteErrorPolicy.Throw;

    // ── 表头移位 ────────────────────────────────────────────
    public int ShiftChunkBytes { get; set; } = 1024 * 1024;
    public int MaxHeaderBytes  { get; set; } = 1024 * 1024;
    public int FreeSpaceMarginBytes { get; set; } = 16 * 1024 * 1024;  // 预检余量
}

public enum EnumFormat       { Name, Numeric }
public enum QuoteMode        { Minimal, EveryField }
public enum WriteMode        { Async, Direct }
public enum FlushPolicy      { Batch, Sync }     // Batch: 段满即写；Sync: 每次段写后 fsync
public enum WriteErrorPolicy { Throw, Stop }
```

**`WriteMode.Direct` 为什么保留**：单线程调用（脚本、单元测试、调试）时，它把 `Append` 变成"锁内写共享缓冲"——异常当场抛、全局有序、无写线程。约 50 行，和 `Async` 共享行序列化与移位代码。也正好做 A/B 基准的对照组。

---

## 9. 生命周期与状态机

```
Create ─┐
        ├─► Open(可写) ─► [读表头 / FileShare.Read / Seek(End) / 末尾补换行] ─► Ready
Open ───┘

Ready ──Append──────► Ready            （懒启动写线程）
Ready ──SetHeaders──► Shifting ──► Ready
Ready ──Faulted─────► Faulted          （写盘异常 / 回滚失败）
Ready ──Dispose─────► Disposed
```

| 规则 | 说明 |
|---|---|
| 懒启动写线程 | 首次 `Append`（或首次 `Flush`）才起线程，纯元数据操作不付线程成本 |
| `Dispose` 幂等 | 二次调用无操作 |
| `Dispose` 后调用 | 除 `Dispose`/`LastError` 外全部抛 `ObjectDisposedException` |
| `Dispose` 有并发 `Append` | **不支持**（文档明确）；`Direct` 模式下 `Dispose` 会取同一把锁，所以安全 |
| `Faulted` | 只能 `Dispose`；其余操作抛 `CsvWriteException` |
| `Create` 覆盖已有文件 | 默认拒绝；`Overwrite=true` 才截断（避免误毁 14 GB 文件） |
| 两实例开同一路径 | 同进程用路径字典（大小写不敏感）直接拒绝并给出友好报错；跨进程靠 `FileShare.Read`（第二个写者拿不到写权限）。**注意：`FileShare.Read` 只挡住第二个 *写* 句柄，`FileShare.ReadWrite` 的外部读者仍可打开，这属于 v10 已接受的后果** |

---

## 10. 线程安全矩阵

| 成员 | 多线程并发 | 备注 |
|---|---|---|
| `CreateRow()` | ✓ | 每线程各自的池，无争用 |
| `Append(row)` | ✓ | 行实例本身不得被两线程同时持有 |
| `Flush()` / `FlushAsync()` | ✓ | 屏障，会短暂打断所有生产者 |
| `Headers` / `HeaderCount` | ✓ | 不可变快照（`Volatile.Read` 一次拿到引用） |
| `SetHeaders` 等 | ✓ | `_adminGate` 串行化 + 管理员屏障 |
| 统计属性 | ✓ | `Volatile` / `Interlocked.Add` |
| `Dispose` | ✗（须无并发 `Append`） | 文档明确；`Direct` 模式例外 |

---

## 11. 错误模型

| 异常 | 何时 |
|---|---|
| `CsvWriteException` | 写盘失败（异步暴露）；`InnerException` 为原始 `IOException` |
| `CsvFileCorruptedException` | 移位取消后回滚也失败 |
| `CsvHeaderTooLargeException` | 表头超 `MaxHeaderBytes` |
| `InvalidOperationException` | 行已回收、行属于别的 appender、Faulted、已 Dispose |
| `ArgumentException` / `ArgumentOutOfRangeException` | 表头索引越界、非法分隔符（`\r`/`\n`/引号） |
| `IOException` | 文件被占用、空间不足、移位中 IO 失败 |

**不做**的：不静默吞任何错误（除 `OnWriteError = Stop`），不在 `Dispose` 里抛（改为吞掉并写 `LastError`，避免 `using` 块里覆盖真正的业务异常）——这一条要写清楚，因为它是 `IDisposable` 的常见坑。

---

## 12. TFM 与条件编译

```xml
<TargetFrameworks>netstandard2.0;netstandard2.1;net8.0</TargetFrameworks>
```

| 能力 | ns2.0 | 处理 |
|---|---|---|
| `Span` / `ReadOnlySpan` | ✗ | 引 `System.Memory` |
| `Utf8Formatter` | 需实测（`System.Memory` 4.5.x 在 ns2.0 上有 `System.Buffers.Text`） | 实测可用则用；否则 `Utf8Format.cs` 手写兜底（约 40 行） |
| `Encoding.UTF8.GetBytes(span, span)` | ✗ | `GetByteCount` + 池化缓冲 |
| `FileStream.Flush(bool)` | 需核实 | 不可用则退化为 `Flush()`（**丢 fsync 语义**，必须文档标注） |
| `IAsyncDisposable` / `ValueTask` | ✗ | 条件编译；ns2.0 只给 `Task` 版 |
| `DateOnly` / `TimeOnly` | ✗ | 仅 `net8.0` 提供重载 |
| `SpinWait` / `Volatile` / `SemaphoreSlim` / `ConcurrentQueue` | ✓ | 直接用 |
| `DriveInfo.AvailableFreeSpace` | ✓ | 空间预检（**仅 advisory**，网络盘/CoW 可能不准） |

**M0 必须先实测三件事**（写代码前）：`Utf8Formatter` 在 ns2.0 是否可用、`FileStream.Flush(bool)` 在 ns2.0 是否存在、`ArrayPool<byte>` 在 ns2.0 的分配行为。

---

## 13. 性能目标（**已按实测校准**）

> ⚠️ v11 初稿写的「`Append` ≥ 1.5 GB/s」是**伪指标**。实测表明：吞吐几乎完全由**每行固定开销**决定，
> 而不是字节数。同样 1.5 GB/s 的目标，对 4 KB 的行只要 39 万行/秒，对 68 字节的行却要 **2200 万行/秒**
> —— 后者已超过单核 `memcpy` 上限（实测 12.6 GB/s，复制 68 字节载荷约合 24 M 次/秒，且不含 8 次
> `Add` 的格式化）。所以指标必须拆成「宽行 GB/s」和「窄行 行/秒」两条。

**实测（24 核 / .NET 8.0.23 / Release / 本机 NVMe，Direct 模式同步落缓冲）**

| 场景 | 实测 | 说明 |
|---|---|---|
| 宽行（4 KB 载荷） | **2.15 GB/s** | 字节受限，超过原目标 |
| 窄行（8 个 int，68 B/行） | **5.43 M 行/秒** | 每行固定开销受限，约 0.35 GB/s |
| 8 字段混合类型 | **约 2.9 M 行/秒** | 含 double/decimal/string/时间戳格式化 |
| 需转义字段 | 约 5.1 M 行/秒 | 引号翻倍 + 预扫描 |
| 8 线程聚合（Async） | 约 9.0 M 行/秒 | 受写线程落盘限制 |
| `Open()` 192 MB 文件 | **0.24 ms**（p95 0.30 ms） | 只读表头行，与文件大小无关 |
| `SetHeaders` 等长 | **7.7 µs**（p95 8.5 µs） | 与文件大小无关 |
| 每次 `Append` 分配 | **0.3 B/行** | 行池 + 段池，稳态无分配 |

**结论与取舍：**

- **宽行场景**（日志、导出、批量数据）：字节吞吐是有效指标，2.15 GB/s 达标。
- **窄行场景**（大量小记录）：正确指标是**行/秒**，约 3–5.4 M 行/秒。要再快只能减少每行 `Add` 次数或
  直接写预格式化字节（`Add(ReadOnlySpan<byte>)`），那是调用方的选择，不是库能单方面优化的。
- **不要在文档里承诺单一 GB/s 数字**，它会让窄行使用者产生错误预期。

调优旋钮：`SlotBufferBytes`（吞吐 vs 交错粒度）、`MaxProducerSlots`（并发上限）、`WriteMode.Direct`
（单线程最省一次跨线程交接）。

---

## 14. 测试计划

| 组 | 内容 |
|---|---|
| **行序列化** | 全类型重载、`null`/`AddNull`、CJK、emoji、超长字段（16 MB）、单字段行、内嵌 `\r\n`、字段以引号开头/结尾 |
| **转义** | 分隔符/引号/CR/LF 四种脏字符逐一 + 组合；`QuoteMode.EveryField` |
| **表头** | 等长改名、重排、增删列（首/中/尾）、删最后一列、无表头文件上设表头、索引越界、含分隔符的列名 |
| **移位** | delta = ±1；文件长 < chunk / = chunk / = chunk±1；进度单调；**取消后文件字节 == 原文件字节**（逐字节比对） |
| **并发** | 8/16 线程 × 10 万行：行数、每行字段数、行内容集合（排序后）校验；行永不撕裂 |
| **屏障交织** | N 个 `Append` 线程 + 一个反复 `SetHeaders(等长/变长)` + 反复 `Flush`：最终文件可完整解析，行数一致 |
| **错误** | 写盘失败注入（只读盘、磁盘满、句柄被抢）；`Faulted` 后行为 |
| **崩溃恢复** | 写到一半 `Kill` 进程 → `Open` → E1 补换行 → 追加不粘行 |
| **跨 TFM** | 三目标各跑同一套测试 |
| **基准** | BenchmarkDotNet：单/8 线程 `Append`、`SegmentBytes` 扫描、`Direct` vs `Async` |

并发测试必须在 `Debug` + `Release` 各跑一遍，并在 ARM64 上验证 Dekker 握手（如果 CI 能提供）。

---

## 15. 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| **M0** | 三目标骨架 + 三项 TFM 实测 + 空基准 | 基线，消除 §12 的不确定性 |
| **M1** | `CsvRow` 全类型 + 转义 + `WriteMode.Direct` | 正确性全绿，≥ 1.5 GB/s，0 分配 |
| **M2** | 写线程 + 段 + 队列 + 背压 + `Flush` 屏障 | 8 线程无撕裂，磁盘限流 |
| **M3** | 表头全套（快路径）+ 解析缓存 | 等长编辑 ≤ 50 μs |
| **M4** | 移位 + 进度 + 取消 + **回滚** + 空间预检 | 14 GB 峰值内存 ≤ 2 MB，取消后字节级还原 |
| **M5** | 并发/边界/崩溃测试 | 全部绿，含三 TFM |
| **M6** | 基准与调优 + README/示例 | 公布数字 |

规模预估：核心 **≈ 900–1100 行**（v10 是 550–700，增量主要是段/队列/屏障/回滚），测试 **≈ 800 行**。

---

## 16. 风险

| # | 风险 | 缓解 |
|---|---|---|
| R1 | 段/屏障协议写错 → 极难复现的丢行/错行 | 协议只有 §4.4 一处；配 8/16 线程压力测试 + 断言语义不变量；必要时降级为"每行 `lock` 直写" |
| R2 | 异步错误暴露让调用方误以为已落盘 | README 首段明确；`PendingBytes` 可观测；`Sync` 策略可选 |
| R3 | 移位期间外部读者（Excel/tail）读到错乱 | v10 已接受的后果；文档标注"移位后需重开文件" |
| R4 | 移位期间崩溃 → 文件损坏 | v10 已接受；v11 多给了"取消可回滚"，但进程崩溃仍不可恢复 |
| R5 | `Append` 阻塞在背压上被误判为死锁 | `PendingBytes`/`IsBackpressured` 可观测；文档给出水位估算公式 |
| R6 | 三 TFM 条件编译拖慢迭代 | M0 先钉死能力矩阵，条件编译集中在 2 个文件 |

**不做的事（明确划界）：** 不改存储格式（保持纯 CSV）、不做列对齐/数据重排、不做 CSV 解析（只写不读）、不做多进程并发追加、不做行索引。

---

## 16.5 文本编码：GBK 与双字节安全性（**v11 追加**）

> 背景：中文 Windows 上的 Excel 打开无 BOM 的 UTF-8 CSV 会按系统 ANSI 代码页解释，中文全部乱码。
> 实际业务里大量 CSV 就是 GBK 交付的，所以编码必须可配，不能硬编码 UTF-8。

### 16.5.1 设计

```csharp
public Encoding Encoding { get; set; } = Utf8NoBom;   // CsvOptions
```

- **用 `Encoding` 对象而不是字符串名**：编码决定字段字节长度，而行缓冲、转义扫描、表头长度计算
  全部按字节进行，必须与之完全一致。
- `CsvEncodings` 静态类提供 `Gbk`(936) / `Gb18030`(54936) / `Big5`(950) / `ShiftJis`(932) / `Get(cp)`，
  并**自动完成 `CodePagesEncodingProvider` 注册** —— .NET Core 起 ANSI 代码页不再内置，这一步是必需的，
  不能指望调用方记得。
- `Dialect` 快照携带编码，`CsvRow` / `HeaderCodec` 全部改为接受编码参数；`Utf8Compat` 的方法签名
  加 `Encoding` 参数（ns2.0 走指针重载，ns2.1+ 走 Span 重载）。

### 16.5.2 双字节字符集的字节安全性（**最重要的约束**）

双字节编码（GBK/Big5/Shift-JIS）中一个汉字是「引导字节 + 尾字节」。若尾字节范围涵盖
逗号 `0x2C`、引号 `0x22`、CR `0x0D`、LF `0x0A`，则按字节扫描会把汉字的一半误认为语法符号，
产出**错位的文件且不会报错**。

**实测数据（逐字符编码 U+4E00–U+9FA5，只统计多字节字符自己的尾字节）：**

| 编码 | 尾字节范围 | 逗号 `,` 分号 `;` TAB | `\|` `^` `~` `` ` `` `\` `@`（0x40–0x7E） |
|---|---|---|---|
| UTF-8 | 0x80–0xBF（续字节） | ✅ | ✅（ASCII 不会出现在续字节） |
| GBK (936) | **0x40–0xFE** | ✅ | ❌ 不安全 |
| Big5 (950) | 0x40–0xFE | ✅ | ❌ 不安全 |
| Shift-JIS (932) | 0x40–0xFC | ✅ | ❌ 不安全 |

> ⚠️ **勘误**：v11 初版写的「GBK 尾字节最小 0xA2」是**错的**。那个数字来自一次有缺陷的探测 ——
> 把整段 CJK 文本一次性编码，再用「引导字节 >= 0x81 就吞掉下一字节」的状态机推断边界。
> 但 GBK 的尾字节本身可以 >= 0x81，状态机会错位。逐字符统计后真实范围是 **0x40–0xFE**。
> 对用逗号/分号/TAB 的调用方没有影响（都 < 0x40），但 `|` 在 GBK 下确实不安全。

所以不能「因为 GBK 支持逗号就假定所有 ANSI 编码都支持所有分隔符」。实现上：

```csharp
// CsvOptions.Validate()
if (!CsvEncodings.IsSyntaxByteSafe(Encoding, Delimiter)) throw new ArgumentException(...);
if (!CsvEncodings.IsSyntaxByteSafe(Encoding, QuoteCharByte)) throw new ArgumentException(...);
```

`IsSyntaxByteSafe` 的做法：对 CJK 统一表意文字区（U+4E00–U+9FA5）**逐字符**编码，
检查该字符自己的第 2..n 个字节是否等于目标字节。**逐字符是必须的** —— 见上面的勘误；
整体编码 + 状态机推断会误判。

### 16.5.2.1 只支持「单字节兼容」编码

分隔符、引号、行终止符都是**按单字节**直接写入输出流的。因此：

- UTF-16/UTF-32 下逗号会被编码成 `2C 00`，而库只写 `2C`，字段内容却是双字节 → 文件彻底错乱。
- 实测确认：UTF-16 输出 `4B-6D-D5-8B-2C-3C-50-0A`（8 字节），正确结果应是
  `4B-6D-D5-8B-2C-00-3C-50-0A-00`（10 字节）。**这是会静默产出坏文件的真实缺陷**，
  已在 `Validate()` 中显式拒绝（`IsSingleByteCompatible`），覆盖 1200/1201/12000/12001/65000。

### 16.5.3 BOM

- BOM 必须由**编码自身**决定：UTF-8/UTF-16 有，GBK/Big5 没有。
- 默认 `Encoding` 是 `UTF8Encoding(false)`，其 `GetPreamble()` 为空；
  但用户显式设 `WriteBom = true` 时意图明确就是标准 UTF-8 BOM，因此 `CsvOptions.Bom` 在此情形下
  回退到 `EF BB BF`。**这是实现过程中被测试抓到的真实回归**（初版直接取 `GetPreamble()`，
  导致 `WriteBom` 对默认编码静默失效）。
- `Open` 的 BOM 探测同样改为按 `options.Bom` 比较，而不是硬编码三个字节。

### 16.5.4 测试

`tests/CsvLite.Tests/EncodingTests.cs`（14 个）：GBK 字节级断言（"中文测试" 必须是 8 字节且以
`D6 D0` 开头，误用 UTF-8 会是 12 字节）、表头往返、含分隔符/引号/换行的中文转义、
**遍历 20902 个 CJK 字符确认尾字节永不撞逗号/引号/CR/LF**、GBK 下变长表头移位、
GB18030 四字节生僻字、Big5 + `|` 被拒绝、BOM 回归。

---

## 17. 待确认（仅剩 3 个小的）

1. **`DateOnly`/`TimeOnly` 只在 `net8.0` 目标提供重载** —— 可以吗？（ns2.0 没有这两个类型，无法模拟）
2. **`Open` 时方言校验**：若调用方给的 `Delimiter` 与文件实际不符，会写出错位文件。要不要一个 `DialectCheck = None | Warn | Throw` 开关，默认 `None`（沿用 v10 的"不嗅探"立场）？只读首行，不违反内存约束。
3. **`WriteMode.Direct` 是否保留** —— 我倾向保留（约 50 行，换调试/单线程友好 + 基准对照组）。若不保留，代码和测试都少一截。
