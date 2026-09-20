using System.Windows.Controls;
using PoEToolbox.Abstractions;

namespace PoEToolbox.Ui;

/// <summary>
/// 不变式 7（docs/ARCHITECTURE.md §4）：<c>IPlugin</c> 无界面、本接口带界面；<c>Abstractions</c> 因此不引用 WPF。
/// 带界面的插件：在 <see cref="IPlugin"/> 之上补上视图工厂。
/// 主窗的导航只列实现了本接口的插件；生命周期回调仍由 <see cref="IPlugin"/> 承担，
/// 所以无界面的插件（后台同步、快捷键服务之类）可以只实现 <see cref="IPlugin"/> 而不必引用 WPF。
/// </summary>
public interface IUiPlugin : IPlugin
{
    UserControl CreateView();
}
