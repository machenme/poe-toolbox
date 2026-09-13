# 理解 PoE2 的 Bundles2 存储模型

> 性质：机制说明 + 由此推出的硬约束
> 适用：改 fx-patch 引擎、写补丁的人。看懂它，就知道为什么有些事"做不到"而不是"没做"。

---

## 0. 一句话

**游戏文件不是磁盘上的文件，它是某个 bundle 里的一段字节；索引里的记录只是一个「四元组」，告诉客户端去哪个 bundle 的哪个偏移读多长。**

```
┌─ _.index.bin（索引本体，几十~几百 MB）──────────────┐
│  BundleRecord 数组：每条 = 一个 bundle 的路径 + 未压缩大小 │
│  FileRecord  数组： 每条 = (路径哈希, bundle 序号, 偏移, 长度) │
│  DirectoryTable： 路径字符串表（给 FileRecord 反解路径用）  │
└──────────────────────────────────────────────────┘
        │ 每条 FileRecord 指向
        ▼
┌─ bundle 文件（几百 MB 的压缩包，装成百上千个文件内容）──┐
│   0       1200              98000                    │
│   ├── 成员A ──┤── 成员B ──┤── 成员C ──┤ ...          │
└─────────────────────────────────────────────────────┘
```

`FileRecord` 只有四个字段（`Records/FileRecord.cs:154-163`）：

```csharp
stream.Write(PathHash);
stream.Write(BundleRecord.BundleIndex);
stream.Write(Offset);   // 在所属 bundle 里的偏移
stream.Write(Size);
```

---

## 1. 必须分清的两个"偏移"

这是最容易搞混的地方，本文所有结论都建立在这上面。

| 名字 | 相对谁 | 敏感程度 |
|---|---|---|
| **`FileRecord.Offset`** | 相对它**所属的那个 bundle** 解压后的内容流 | 极敏感：往这个 bundle 里插东西，后面所有成员的 offset 都要重排 |
| **记录自己在 `_.index.bin` 里的位置** | 相对索引文件 | **不敏感**：索引每次都是全量重建（见 §4） |

`FileRecord.Read()` 就是证明（`Records/FileRecord.cs:51-63`）：它先 `BundleRecord.TryGetBundle()` 打开那个 bundle，再 `bundle.Read(Offset, Size)` —— **offset 从来不是相对 `_.index.bin` 的**。

所以"给游戏加内容会不会挤动原来记录的位置"要分两问：

- **会不会挤动索引里的记录？** 不会。见 §4。
- **会不会挤动某个 bundle 里的成员？** 会！所以绝不能往已有 bundle 里塞东西。见 §3。

---

## 2. 为什么不能就地改一个游戏文件

原生 bundle 是一个**共享压缩包**：几百 MB、里面排着成百上千个成员，**而且是压缩过的**。

要"改其中一个成员"，实际要做的是：

1. 解压整个 bundle
2. 替换/插入那段字节
3. **重排后面所有成员的 offset**（因为它们整体位移了）
4. 更新索引里成百上千条 FileRecord 的 Offset 字段
5. 重新压缩整个几百 MB 的包写回磁盘

每改一个小文件都要做一遍——这既不现实，也会让"系统里同时装着多个 mod"变成不可能（每个人都在重压同一个包）。

**所以 PoE 生态里所有工具（含本项目的 LibBundle3）都用同一个办法：**

> 新内容写进**另一个 bundle**，然后把那一条 FileRecord 挂过去。

这就是 `FileRecord.Redirect`（`Records/FileRecord.cs:139-149`）—— 把 `BundleRecord` / `Offset` / `Size` 三个字段改指到新位置。它是 `public virtual`，**支持指回任意 bundle**（这一点在讨论"还原能否拨回去"时很关键）。

**推论：我们从不写原生 bundle 一个字节。** 原版内容永远完好地躺在原来的 bundle 里，只是指向它的那条记录离开了。

---

## 3. "新增内容必须放到末尾" —— 引擎正是这么做的

### 写入时的做法

`Index.EnsureWriteBundle`（`Index.cs:1011-1025`）在第一次打开待写 bundle 时，会**先把该 bundle 已有的全部内容读进内存流，再往后面追加**：

```csharp
ms.Write(b.ReadWithoutCache(0, originalSize)); // 读回原有内容
// 之后 FileRecord.Write 的 ms.Write(newContent) 都是追加在末尾
```

`FileRecord.Write`（`Records/FileRecord.cs:97-107`）拿到的是 `(int)ms.Length` 作为新成员的 Offset —— 也就是**当前流的末尾**。已有成员的 offset 一个都没变。

### 往哪个 bundle 写

`Index.GetBundleToWrite`（`Index.cs:925-983`）只会有两个结果：

| 情况 | 落点 |
|---|---|
| 钉扎了写入目标（补丁引擎会设 `PinnedWriteBundlePath`） | `PATCHED/<bundleName>_v<version>.bundle.bin` |
| 没钉扎（通用写入） | 新建 `LibGGPK3/N.bundle.bin` |

**两者都不是原生 bundle。** 配合上面的"追加到末尾"，三条约束同时成立：

1. 原生 bundle 一个字节不变
2. 已有成员的 offset 全部不变
3. 索引里其他记录不需要动

---

## 4. `_.index.bin` 本身：全量重建，不做增量插入

`Index.Save()`（`Index.cs:564-581`）每次都是**整体重写**：

```csharp
ms.Write(_Bundles.Length);
foreach (var b in _Bundles) b.Serialize(ms);   // 所有 bundle 记录
ms.Write(_Files.Count);
foreach (var f in _Files.Values) f.Serialize(ms);  // 所有文件记录
ms.Write(directoryBundleData, ...);
baseBundle.Save(ms, compressor, compressionLevel); // 压缩后写回索引文件
```

所以"在索引中间插一条记录会不会挤动后面的记录"这个问题**不存在** —— 每次都是重新排队写一遍。

代价写在别处了（见 MEMORY.md）：每次 Save 要建整份未压缩索引的 MemoryStream 再压缩，**瞬时占用约 2~3× 索引大小**。

### Save 顺带做的一件事

`Index.cs:549-584`：**Save 开头会把所有「成员数为 0 的自定义 bundle」摘掉**，索引写完之后调 `bundleFactory.DeleteBundle` 删掉它们的物理文件（`DriveBundleFactory.cs:35-41` 就是 `File.Delete`）。

⇒ 只要某个 `PATCHED/…` bundle 里的成员全部被移走，它会在下一次 Save 时**自动消失**。不需要单独的"清理"动作。

---

## 5. 补丁事务为什么要踩住 MaxBundleSize

`FlushWriteBundle`（`Index.cs:1031-1039`）会在内容超过 `MaxBundleSize` 时把 bundle 写盘、清空内存流。

补丁引擎在写入期间把它设成 `int.MaxValue`（`FxPatchEngine.cs:753`）：

```csharp
// 补丁事务不能在中途触发 Bundle flush；所有 FileRecord/AddFile 操作
// 都只改内存中的索引和单个待写 Bundle，最后统一 Save 一次。
gd.Index.MaxBundleSize = int.MaxValue;
```

原因不是"偏移会坏"（flush 后再写会先把磁盘内容读回来，offset 仍然连续），而是**事务原子性**：拍到磁盘上的 bundle 已经脱离内存回滚的能力范围了。事务失败时要靠 `RollbackMutation` + 回写原索引字节来撤销，中途落盘的 bundle 会成为需要额外处理的残留。

---

## 6. 由这个模型推出的几条硬约束

改引擎、写补丁之前请对照这几条——它们不是设计选择，是模型逼出来的。

**① 修改任何游戏文件 = 一次 Redirect，没有别的办法。**（§2）

**② 逻辑地址稳定，物理位置会漂。**
同一个路径在补丁叠加后可以依次住在 `原生 bundle → PATCHED/A → PATCHED/B`。
⇒ **任何判据都必须落在逻辑内容上，不能落在 bundle/offset 上。**

引擎现在正是这么做的（`FxPatchEngine.ComputeState` 全部基于内容）：

| op | 判据 | 位置 |
|---|---|---|
| edittext | 解码后的文本里有没有 old / new | `FxPatchEngine.cs:935-950` |
| patchptr-byid | 读表内容，看 Id 那行指针当前指向哪个串 | `:924-933` |
| addfile-* | SHA-256 比对 | `:877-922` |

这带来一个红利：**revert 天然支持栈式叠加**。`X→Y` 与 `Y→Z` 两个补丁叠加后，按 `Z→Y→X` 逆序还原逐步成立——每一步都用"当前内容里有没有我要的那段"自证，不关心是谁改的、改到了哪个 bundle。

**③ 「记下旧的 bundle/offset、还原时 Redirect 拨回去」不做。**
单补丁场景下它确实可行且更省（不重写内容、PATCHED 能清空），但它把判据从内容换成了位置：
- 逆序还原仍正确，但**乱序还原会静默抹掉上层补丁的改动**，且没有内容校验能发现；
- 客户端更新后 bundle 被换、或被别的工具重压过，记下的位置会失效——而那时 `Size` 字段还是旧值，读出来是垃圾且索引不报错，**失败是静默的**（内容模型的失败是安全的：落到 `Incompatible`）。

曾评估过一个带 SHA 守门员 + fallback 的折中版，可以覆盖全部边界。
**2026-09-13 用户拍板「彻底放弃我刚才的想法」——该方向不再考虑，维持内容重写的 revert。**

**④ PATCHED 目录只增不减，是模型的固有代价，不是 bug。**
revert 不是"删掉改动"，而是**写出一份还原版内容并 Redirect 过去**（`FileRecord.Redirect` 会把该记录从原 bundle 的成员表里摘走）。于是revert 之后，`PATCHED/` 里那份装着原版内容的 bundle 成了这条记录的**唯一载体**——删它等于把文件从客户端里挖走。

**⑤ 想要彻底干净，出口是整体回滚，不是改 revert 语义。**
原生 bundle 从未被写过，`backup/_.index.bin` 又是完整的原版索引 ⇒ `restore` 能一步回到出厂状态。这是正确答案。

---

## 7. 速查表

| 想做的事 | 能不能 | 怎么做 |
|---|---|---|
| 改一个已有游戏文件的内容 | 能 | `FileRecord.Write` → 内容进 PATCHED bundle → Redirect |
| 新增一个游戏文件 | 能 | `Index.AddFile` → 内容进新 bundle + 追加目录表项 |
| 就地改写原生 bundle | **不能** | 要解包/重排/重压整个几百 MB 的包 |
| 让 PATCHED 目录自动变干净 | 部分 | 成员全被移走的 bundle 会在下次 Save 自动删 |
| 彻底回到没打补丁的状态 | 能 | `restore` 回滚 `backup/_.index.bin` |
| 用 bundle/offset 判断补丁是否已应用 | **不能** | 逻辑位置会漂，只能用内容（SHA / 文本 / 指针值） |
