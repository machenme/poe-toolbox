# PoE Toolbox

Windows 下的 Path of Exile 工具箱，提供 PoE1 / PoE2 物价标注、游戏数据浏览、特效补丁、地图标签修改、POE2 字体配置与 PoB 国服交易补丁。物价数据来自 [poe.ninja](https://poe.ninja)，物品名称写入使用本地游戏客户端数据。

## 功能

| 功能 | 支持范围 | 说明 |
|------|----------|------|
| **物价标注** | PoE1 / PoE2 | 点击“检测当前联盟”后识别客户端版本并从 poe.ninja 获取行情，在繁中基础物品名后追加价格标签 |
| **联盟选择** | PoE1 / PoE2 | 加载两代联盟列表，支持自定义联盟名；写入前校验联盟与客户端版本一致 |
| **数据浏览** | PoE1 / PoE2 | 浏览、搜索、编辑和提取 `Content.ggpk` 或 Bundles2 `_.index.bin` 内的数据；支持文本文件查找、批量替换、批量保存、路径复制，以及"复制为新路径"生成隔离副本 |
| **特效补丁** | PoE2 | 指令式补丁引擎：一个补丁 = 一份几 KB 的 JSON 描述文件；内置“黏油榴弹特效+地面燃烧特效”补丁，支持自定义补丁与索引 diff 自动生成；幂等可还原，与官方更新及其他补丁叠加共存 |
| **术语翻译** | PoE1 / PoE2 | 使用内置术语资源和游戏数据进行术语翻译，支持后台执行与取消 |
| **地图标签** | PoE1 / PoE2 | 独立通用工具；PoE1 修改 `mapnumbers1..16.dds`，PoE2 修改 `endgamemap1..15.dds`，支持字体、字号和偏移 |
| **POE2 字体配置** | PoE2 | 修改 `metadata/ui/uisettings.xml` 与 `uisettings.traditional chinese.xml`；支持系统字体、字号倍率和实时预览，可恢复原始配置 |
| **PoB 国服补丁** | PoE1 | 修改 PoB 交易地址和赛季编号，并支持还原国际服配置 |
| **背包清理** | PoE1 | 通过校准坐标和热键批量存入背包物品 |
| **繁体中文补丁** | PoE1 | 将 French 语言槽替换为繁体中文，可恢复 |

## 下载运行

从 [GitHub Releases](https://github.com/machenme/poe-toolbox/releases) 下载最新版本的 ZIP，解压后直接运行：

```text
PoEToolbox.exe
```

当前发布采用单文件、framework-dependent 方式。运行电脑需要安装 .NET 10 Windows Desktop Runtime。首次使用需要的 `oo2core.dll` 会自动释放到 `%LocalAppData%\PoEToolbox\native\`，不会写入程序目录。

发布 ZIP 和 Actions Artifact 均只包含 `PoEToolbox.exe`。程序运行产生的配置、缓存、日志和 native 运行库会写入用户目录，不会混入发布目录。

## 物价标注

1. 选择游戏客户端数据文件：PoE1 为 `Content.ggpk`，PoE2 为 `Bundles2\_.index.bin`。
2. 点击“检测当前联盟”识别客户端版本，并加载对应的 PoE1 或 PoE2 分类；打开页面时不会自动读取游戏数据。
3. 从下拉列表选择联盟，或输入自定义联盟名。工具会自动判断该联盟属于 PoE1 还是 PoE2。
4. 选择分类后应用价格标签。首次使用或勾选强制更新时会请求 poe.ninja。
5. 建议先使用预览模式；正式写入前可勾选备份。多分类操作只打开客户端、备份和写入一次。

PoE1 和 PoE2 的基础物品中英名称词典已内置在发布文件中。客户端更新后，工具仍会根据当前客户端英文表动态定位写入行号。

## POE2 字体配置

1. 在 POE2 字体配置页面选择 `Content.ggpk` 或 `Bundles2\\_.index.bin`。
2. 从系统已安装字体列表中选择字体，也可以直接输入字体族名称。
3. 调整字号倍率，右侧预览会即时显示中文、英文、数字和不同字号的示例效果。
4. 点击应用后，工具会修改 `metadata/ui/uisettings.xml` 和 `metadata/ui/uisettings.traditional chinese.xml`。

字体配置以首次应用时保存的原始 XML 为基准，每次调整都会重新生成，不会重复放大或缩小字号。原始文件保存在游戏数据同目录的 `backup\\poe2-fonts\\` 下，可使用“恢复首次备份的原始字体”还原。

## GGPK 文件编辑

1. 在“数据浏览”中选择文本类文件，例如 `.ao`、`.txt`、`.json`、`.xml`、`.ini` 或 Shader 文件。
2. 点击“编辑”，在编辑器中修改内容；支持查找下一个、区分大小写和全部替换。
3. 点击“暂存修改”后，修改仍保留在内存中；顶部“保存全部修改”会一次性写入所有暂存文件。
4. 保存前会创建原始索引备份并写入操作日志；“查看改动”可查看本次文件变化，“撤销全部”可放弃尚未保存的修改。
5. 编辑器的 `A−` / `A+` 按钮可以调整字体大小，设置会自动记住。
6. 需要修改被多处复用的资源时（例如某个被多个技能引用的特效文件），右键选择“复制为新路径（隔离副本）”，先复制出专用副本，再编辑副本并改写引用，原始文件保持不变。

保存修改会写入新的 Bundle，不直接覆盖原始 Bundle。所有改动统一写入单一自定义 Bundle `LibGGPK3/0.bundle.bin`（超过 200 MB 也继续写入同一个文件，不会分裂出 `LibGGPK3/1`、`LibGGPK3/2` …）。备份和日志位置见下方“本地数据”。

## 特效补丁系统

特效补丁是一套“指令式”的游戏数据修改系统：**一个补丁就是一份几 KB 的 `.patch.json` 描述文件**，引擎按描述就地修改游戏索引，而不是分发整个几十 MB 的索引文件。内置补丁“黏油榴弹特效+地面燃烧特效”（延长黏油榴弹地面燃烧的淡出、去掉落地油花与点燃火波）是第一个实例；可以在“特效补丁”页面操作，也可以走命令行。

### 补丁如何执行

以内置补丁为例，一份描述文件长这样：

```json
{
  "patchId": "oil-grenade-fx-lite",
  "bundleName": "OilGrenade",
  "operations": [
    { "op": "addfile-derived", "src": ".../grd_burning01.ao", "dst": ".../grd_burning01_oil.ao",
      "replace": [ { "old": "2 0 0 Linear 0.25 0 Linear", "new": "2 0 0 Linear 0.25 1 Linear", "count": 2 } ] },
    { "op": "patchptr-byid", "table": "data/balance/miscanimated.datc64",
      "id": "BaseOilGroundBurningEffect", "originalPath": ".../grd_Burning01.ao", "newPath": ".../grd_Burning01_oil.ao" },
    { "op": "edittext", "path": ".../oilground.ot", "old": "preload .../grd_Burning01.ao", "new": "preload .../grd_Burning01_oil.ao" }
  ]
}
```

执行 `apply` 时引擎做四件事：

1. **备份**：首次修改前把当前索引逐字节保存为 baseline，之后每次成功修改追加一条 journal 记录。
2. **逐条执行操作**（见下表）。每条操作先校验现状再动手：目标不存在或内容与预期不符 = 冲突，**整个补丁中止**，不做半套修改。
3. **独立落盘**：所有写入进入游戏索引同目录 `PATCHED\<bundleName>_<时间戳>.bundle.bin`，与原版 Bundle 以及其他工具的写入位置物理隔离。
4. **校验**：重新打开索引确认全部操作生效，否则报告失败。

### 四种操作类型

| op | 作用 |
|----|------|
| `addfile-derived` | 读取索引中的原文件，按 `replace` 规则替换内容后生成一个新路径的独立副本（原件与其他引用者不受影响） |
| `addfile-asset` | 把补丁包内自带的成品文件内容写入指定路径；可附带原版内容供还原 |
| `patchptr-byid` | 在 DAT 数据表内按 `Id` 定位行（不硬编码行号，客户端更新后仍能定位），把行内指向某文件路径的指针重定向到新路径 |
| `edittext` | 在指定文本文件内做精确字符串替换（UTF-16），可指定替换次数 |

### 幂等与还原

每条操作有三态判定：**未应用 / 已应用 / 冲突**。

- `apply` 幂等：已应用的操作自动跳过，重复执行安全。
- `revert` 自动取反：指针指回原路径、文本反向替换；`addfile` 生成的副本保留在索引中但不再被引用，无副作用。
- `status` 随时查看每个操作的状态。
- 彻底还原：CLI `restore` 一键恢复最初的 baseline 索引；客户端更新本身也会还原全部修改。

### 补丁生成器（diff）

不想手写 JSON？在“特效补丁”页面选择**原版索引**和**修改后索引**，引擎自动 diff 两份索引（秒级，300 万文件不全量计算），生成 `patch.json` + `assets/` 到 `%LocalAppData%\PoEToolbox\patches\<补丁ID>\`。也可以把别人发布的整包索引 mod 一条命令转成本工具的补丁格式。

### 分发为 zip 补丁包

把生成目录整体压缩成一个 zip（`patch.json` 与 `assets/` 保持相对位置即可，无论是否多包一层文件夹都能识别），引擎可以直接读取：

```powershell
dotnet run --project src\PoEToolbox.Cli -- fx-patch <game-data> <补丁包.zip> status|apply|revert
```

UI 的“自定义补丁描述文件”同样支持选择 zip。引擎会把补丁包解压到临时目录再执行，24 小时后自动清理，不影响原 zip 文件。

### 命令行

```powershell
# 内置油弹补丁：查看状态 / 应用 / 还原
dotnet run --project src\PoEToolbox.Cli -- fx-oilmod <game-data> status
dotnet run --project src\PoEToolbox.Cli -- fx-oilmod <game-data> apply
dotnet run --project src\PoEToolbox.Cli -- fx-oilmod <game-data> revert

# 执行任意补丁描述
dotnet run --project src\PoEToolbox.Cli -- fx-patch <game-data> <patch.json> status|apply|revert

# 索引 diff 自动生成补丁
dotnet run --project src\PoEToolbox.Cli -- fx-patch diff <原版索引> <修改后索引> -o <输出目录>

# 恢复最初的 baseline 索引
dotnet run --project src\PoEToolbox.Cli -- restore <game-data>
```

## 术语翻译

术语翻译功能使用内置的基础术语资源，并结合当前游戏数据进行翻译处理。翻译任务在后台执行，可以随时取消；翻译资源构建脚本位于 `tools/BuildBaseItemTranslationDictionary.ps1`。

## 从源码构建

要求：Windows、.NET 10 SDK 和网络访问（用于 NuGet restore）。

```powershell
dotnet restore src\PoEToolbox.App\PoEToolbox.App.csproj
dotnet publish src\PoEToolbox.App\PoEToolbox.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

产物为 `publish\PoEToolbox.exe`。项目已将 Bundles2 解压所需的 `oo2core.dll` 运行库及所有嵌入资源打包进单文件；运行时会将 `oo2core.dll` 释放到 `%LocalAppData%\PoEToolbox\native\`。

也可以直接运行仓库根目录的 `build.bat`。脚本只生成或更新 `publish\PoEToolbox.exe`，不会清空或删除 `publish` 目录中的已有配置和用户数据。

提交前可运行回归测试：

```powershell
dotnet test tests\PoEToolbox.Tests\PoEToolbox.Tests.csproj -c Release
```

### GitHub Actions 发布

仓库包含 `.github/workflows/build.yml`：

* Pull Request 和 `main` 分支提交会在 Windows runner 上执行测试并构建 `PoEToolbox.exe`，构建结果可在 Actions 的 Artifacts 下载。
* 推送 `v` 开头且与 `version.json` 版本一致的 tag，会自动创建 GitHub Release，并上传只含 `PoEToolbox.exe` 的 `PoEToolbox-win-x64.zip`；Release 正文自动取自 `version.json` 的 `notes` 字段。
* 也可以在 GitHub 的 **Actions → Build PoEToolbox → Run workflow** 手动构建并下载 exe artifact；手动运行不会创建 Release。

发布前先更新 `version.json`，然后提交并推送，再创建同版本 tag：

```powershell
git add version.json
git commit -m "chore: release v0.1.7"
git push origin main
git tag v0.1.7
git push origin v0.1.7
```

tag 必须与 `version.json` 中的版本一致。发布包采用当前配置的 `win-x64`、single-file、framework-dependent 方式，运行目标电脑仍需安装 .NET 10 Windows Desktop Runtime。

### 兼容矩阵

| 项目 | 支持范围 |
|------|----------|
| 操作系统 | Windows |
| 运行时 | .NET 10 Windows Desktop Runtime（framework-dependent 发布） |
| PoE1 数据 | `Content.ggpk` |
| PoE2 数据 | `Bundles2\_.index.bin` |
| 架构 | 以发布包所带的运行库和目标运行时为准，当前发布命令为 `win-x64` |

## CLI

```powershell
# 下载 poe.ninja 行情；可加 --league "Forbidden Rites"
dotnet run --project src\PoEToolbox.Cli -- fetch poe1
dotnet run --project src\PoEToolbox.Cli -- fetch poe2 --league "Forbidden Rites"

# 对单一分类执行标价；先使用 --dry-run
dotnet run --project src\PoEToolbox.Cli -- pricetag <game-data> <category> --dry-run

# 常用数据操作
dotnet run --project src\PoEToolbox.Cli -- list <game-data> [filter]
dotnet run --project src\PoEToolbox.Cli -- read <game-data> <path>
dotnet run --project src\PoEToolbox.Cli -- extract <game-data> [table] [language]
dotnet run --project src\PoEToolbox.Cli -- build-name-dictionary <game-data> <output.json>
dotnet run --project src\PoEToolbox.Cli -- extract-all <source> <output-dir> [poe1|poe2]

# 把索引中的文件复制为一份独立副本（写入新 Bundle，原文件与其他引用者不变）
dotnet run --project src\PoEToolbox.Cli -- copy-file <game-data> <源路径> <目标路径>

# 特效补丁引擎（详见上方“特效补丁系统”）
dotnet run --project src\PoEToolbox.Cli -- fx-oilmod <game-data> <status|apply|revert>
dotnet run --project src\PoEToolbox.Cli -- fx-patch <game-data> <patch.json> <status|apply|revert>
dotnet run --project src\PoEToolbox.Cli -- restore <game-data>
```

运行 `dotnet run --project src\PoEToolbox.Cli -- help` 查看完整命令列表。

## 项目结构

```text
src/
|- PoEToolbox.App/                 WPF 主程序与插件导航
|- PoEToolbox.Cli/                 命令行工具
|- PoEToolbox.Core/                物价标注、DATC64 与名称词典
|- PoEToolbox.Shared/              配置、客户端检测、统一数据访问与特效补丁引擎
|- PoEToolbox.Plugins.PriceTagger/ 物价标注界面
|- PoEToolbox.Plugins.DataBrowser/ 数据浏览与地图标签插件
|- PoEToolbox.Plugins.FxPatch/     特效补丁管理界面
|- PoEToolbox.Plugins.Poe2Font/   POE2 字体配置与实时预览
|- PoEToolbox.Plugins.PoeCnPatch/  PoB 国服补丁
|- PoEToolbox.Plugins.BagCleaner/  背包清理
|- LibGGPK3/                       GGPK 容器读写
|- LibBundle3/                     Bundles2 与 Oodle 解压
|- LibBundledGGPK3/                GGPK / Bundles2 统一访问
`- LibDat2/                        DAT 数据解析
```

## 本地数据

运行过程中会在以下目录写入本地数据，这些文件不进入版本控制：

| 路径 | 用途 |
|------|------|
| `work/poe_ninja/<game>/<league>/` | poe.ninja 行情缓存，按游戏和联盟隔离 |
| `<游戏数据同目录>/backup/` | 每个客户端各自保存的 `baseline.index.bin` 与 `journal.jsonl` |
| `<游戏数据同目录>/Bundles2/LibGGPK3/0.bundle.bin` | 工具箱写入的所有改动所在的自定义 Bundle（不会覆盖原始 Bundle） |
| `<游戏数据同目录>/Bundles2/PATCHED/` | 特效补丁写入的独立 Bundle，每个补丁一个文件，删除即近似卸载 |
| `%LocalAppData%\PoEToolbox\backups\game-data\` | 特效补丁修改前的原始索引 baseline 与操作 journal |
| `%LocalAppData%\PoEToolbox\patches\` | 补丁生成器输出的 `patch.json` 与 `assets/` |
| `<游戏数据同目录>/backup/poe2-fonts/` | POE2 字体配置首次应用前的两个原始 XML |
| `%LocalAppData%\PoEToolbox\databrowser-cache\` | 数据浏览器树缓存 |
| `%LocalAppData%\PoEToolbox\schema\` | DAT schema 缓存 |
| `%LocalAppData%\PoEToolbox\config.json` | 用户配置 |
| `%LocalAppData%\PoEToolbox\logs\` | 运行日志（按天一个文件，保留 7 天；主界面“输出日志”面板与其同源） |
| `%LocalAppData%\PoEToolbox\native\oo2core.dll` | Bundles2/Oodle native 运行库 |

## 鸣谢与许可证

- [LibGGPK3](https://github.com/aianlinb/LibGGPK3) - GGPK 容器读写库。
- [VisualGGPK2](https://github.com/aianlinb/VisualGGPK2) - 部分设计参考。
- `oo2core.dll` - LibBundle3 使用的 Oodle 原生运行库；第三方组件和二进制文件受各自许可证约束。

本项目自有代码以 GNU GPL v3.0 发布，完整条款见 [LICENSE](LICENSE)。
