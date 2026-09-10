using System.Windows;
using PoEToolbox.Plugins.BagCleaner.ViewModels;

namespace PoEToolbox.Plugins.BagCleaner.Views;

/// <summary>
/// 校准向导窗口 (SPEC §5 Views/CalibrationWindow.xaml)。
///
/// 校准流程在 Calibrator 状态机里跑 (Core/CalibrationController.cs)，
/// 此窗口仅作为状态显示 + 模态阻塞入口。
/// </summary>
public partial class CalibrationWindow : Window
{
    public CalibrationWindow(int calibrateKey = 0x72) // 默认 F3
    {
        InitializeComponent();
        DataContext = new CalibrationViewModel(calibrateKey);
    }

    public void UpdateForState(Core.CalibrationState state)
    {
        if (DataContext is CalibrationViewModel vm)
            vm.UpdateForState(state);
    }
}
