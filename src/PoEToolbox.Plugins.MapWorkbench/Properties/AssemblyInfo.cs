using System.Runtime.CompilerServices;

// 解析层的分块与取数辅助方法保持 internal（它们不该成为插件的对外 API），
// 但测试要直接覆盖这些边界行为（未闭合花括号、负数、无百分号）。
// 沿用本仓库既有做法：只在测试程序集面前打开。
[assembly: InternalsVisibleTo("PoEToolbox.Tests")]
