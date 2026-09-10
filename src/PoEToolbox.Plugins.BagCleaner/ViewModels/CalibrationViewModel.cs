using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PoEToolbox.Plugins.BagCleaner.Core;
using PoEToolbox.Plugins.BagCleaner.Models;

namespace PoEToolbox.Plugins.BagCleaner.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "坐标校准";
    [ObservableProperty] private string _step = "1/2";
    [ObservableProperty] private string _instruction = "";
    [ObservableProperty] private string _stepColor = "#0078D4";

    private readonly int _calibrateKey;

    public CalibrationViewModel(int calibrateKey = 0x72) // 默认 F3
    {
        _calibrateKey = calibrateKey;
        UpdateForState(CalibrationState.Idle);
    }

    public void UpdateForState(CalibrationState state)
    {
        var keyName = KeyName(_calibrateKey);
        switch (state)
        {
            case CalibrationState.Idle:
                Title = "坐标校准";
                Step = "—";
                Instruction = "按 Esc 关闭此窗口";
                StepColor = "#A0A0A0";
                break;
            case CalibrationState.WaitingFirst:
                Step = "1/2";
                StepColor = "#0078D4";
                Instruction = $"把鼠标移到背包【左上第 1 格】中心\n然后按 {keyName} 标记\n(按 Esc 取消校准)";
                break;
            case CalibrationState.WaitingSecond:
                Step = "2/2";
                StepColor = "#0078D4";
                Instruction = $"第 1 格已标记 ✓\n\n把鼠标移到背包【右下最后 1 格】中心\n然后按 {keyName} 完成校准\n(按 Esc 取消校准)";
                break;
        }
    }

    private static string KeyName(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return ((char)('A' + vk - 0x41)).ToString();
        if (vk >= 0x30 && vk <= 0x39) return ((char)('0' + vk - 0x30)).ToString();
        if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x6F);
        return vk switch
        {
            0x1B => "Esc", 0x09 => "Tab", 0x20 => "Space", 0x08 => "Backspace",
            0x0D => "Enter", 0x2E => "Delete", 0x2D => "Insert",
            0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
            0x26 => "↑", 0x28 => "↓", 0x25 => "←", 0x27 => "→",
            _ => $"VK 0x{vk:X2}"
        };
    }
}
