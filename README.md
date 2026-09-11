# PoE Toolbox

Windows 下的 Path of Exile 工具箱，提供 PoE1 / PoE2 物价标注、游戏数据浏览、地图标签修改、POE2 字体配置与 PoB 国服交易补丁。物价数据来自 [poe.ninja](https://poe.ninja)，物品名称写入使用本地游戏客户端数据。

## 功能

| 功能 | 支持范围 | 说明 |
|------|----------|------|
| **物价标注** | PoE1 / PoE2 | 自动识别客户端版本；从 poe.ninja 获取行情，在繁中基础物品名后追加价格标签 |
| **联盟选择** | PoE1 / PoE2 | 加载两代联盟列表，支持自定义联盟名；写入前校验联盟与客户端版本一致 |
| **数据浏览** | PoE1 / PoE2 | 浏览、搜索、编辑和提取 `Content.ggpk` 或 Bundles2 `_.index.bin` 内的数据；支持文本文件查找、批量替换、批量保存和路径复制 |
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
2. 工具自动识别客户端版本，并加载对应的 PoE1 或 PoE2 分类。
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

保存修改会写入新的 Bundle，不直接覆盖原始 Bundle；备份和日志位置见下方“本地数据”。

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
* 推送 `v` 开头且与 `version.json` 版本一致的 tag，会自动创建 GitHub Release，并上传只含 `PoEToolbox.exe` 的 `PoEToolbox-win-x64.zip`。
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
```

运行 `dotnet run --project src\PoEToolbox.Cli -- help` 查看完整命令列表。

## 项目结构

```text
src/
|- PoEToolbox.App/                 WPF 主程序与插件导航
|- PoEToolbox.Cli/                 命令行工具
|- PoEToolbox.Core/                物价标注、DATC64 与名称词典
|- PoEToolbox.Shared/              配置、客户端检测和统一数据访问
|- PoEToolbox.Plugins.PriceTagger/ 物价标注界面
|- PoEToolbox.Plugins.DataBrowser/ 数据浏览与地图标签插件
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
| `<游戏数据同目录>/backup/poe2-fonts/` | POE2 字体配置首次应用前的两个原始 XML |
| `%LocalAppData%\PoEToolbox\databrowser-cache\` | 数据浏览器树缓存 |
| `%LocalAppData%\PoEToolbox\schema\` | DAT schema 缓存 |
| `%LocalAppData%\PoEToolbox\config.json` | 用户配置 |
| `%LocalAppData%\PoEToolbox\logs\` | 运行日志 |
| `%LocalAppData%\PoEToolbox\native\oo2core.dll` | Bundles2/Oodle native 运行库 |

## 鸣谢与许可证

- [LibGGPK3](https://github.com/aianlinb/LibGGPK3) - GGPK 容器读写库。
- [VisualGGPK2](https://github.com/aianlinb/VisualGGPK2) - 部分设计参考。
- `oo2core.dll` - LibBundle3 使用的 Oodle 原生运行库；第三方组件和二进制文件受各自许可证约束。

本项目自有代码以 GNU GPL v3.0 发布，完整条款见 [LICENSE](LICENSE)。
