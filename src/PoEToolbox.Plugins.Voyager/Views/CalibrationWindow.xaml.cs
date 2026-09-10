using System.Windows;

namespace PoEToolbox.Plugins.Voyager.Views;

/// <summary>
/// 地图仓校准引导窗口。
/// 进入校准模式后显示，按校准热键逐步捕获坐标。
/// </summary>
public partial class CalibrationWindow : Window
{
    public CalibrationWindow(int calibrateKey = 0x75)
    {
        InitializeComponent();
        UpdateForStep(1, calibrateKey);
    }

    public void UpdateForStep(int step, int calibrateKey)
    {
        var keyName = KeyName(calibrateKey);
        switch (step)
        {
            case 1:
                TitleText.Text = "地图仓校准";
                StepText.Text = "步骤 1/2";
                InstructionText.Text = $"把鼠标移到【第一张地图】中心\n然后按 {keyName} 标记\n(关闭窗口取消校准)";
                break;
            case 2:
                StepText.Text = "步骤 2/2";
                InstructionText.Text = $"第一点已标记 ✓\n\n把鼠标移到【最后一张地图】中心\n然后按 {keyName} 完成校准\n(关闭窗口取消校准)";
                break;
        }
    }

    private static string KeyName(int vk)
    {
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
        return $"VK 0x{vk:X2}";
    }
}
