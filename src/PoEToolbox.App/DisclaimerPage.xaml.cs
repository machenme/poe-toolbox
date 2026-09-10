using System.IO;
using System.Windows;
using System.Windows.Controls;
using PoEToolbox.Shared;

namespace PoEToolbox.App;

public partial class DisclaimerPage : UserControl
{
    /// <summary>Fires when user clicks Accept.</summary>
    public event Action? Accepted;

    /// <summary>Fires when user clicks Decline.</summary>
    public event Action? Declined;

    public DisclaimerPage()
    {
        InitializeComponent();
        ApplyLocalization();
    }

    private void ApplyLocalization()
    {
        var isZh = UILabels.Current != UILabels.Lang.English;
        DisclaimerText.Text = isZh
            ? "⚠ 免责声明\n\n• 使用者需自行承担所有风险。开发者对于账号封锁、数据遗失或其他损害概不负责。\n• 物价标注工具会修改 GGPK 文件 — 请勿在直播或录像时使用，可能违反游戏服务条款。\n• 背包清理工具使用输入模拟 — 请仅在城市/仓库场景中使用。\n• 请务必备份原始 Content.ggpk 文件。\n\n点击「同意」即表示您已了解上述风险。"
            : "⚠ Disclaimer\n\n• Use at your own risk. The authors are not responsible for any account bans, data loss, or other damages.\n• Price Tagger modifies GGPK files — do NOT use while streaming or recording, as it may violate game ToS.\n• Bag Cleaner uses input simulation — only use in town/stash scenarios.\n• Always keep a backup of your original Content.ggpk.\n\nBy clicking \"Agree\", you acknowledge these risks.";

        HowToText.Text = isZh
            ? "📋 使用须知\n\n• 本工具不注入、不修改游戏内存，仅模拟输入操作和修改 GGPK 文件。\n• 修改 GGPK 文件存在账号安全风险，请务必在操作前备份原始文件。\n• 每天会主动检测一次版本更新；发现新版本时，右上角版本号旁会显示红点。\n• 开发者对因使用本工具导致的任何后果不承担任何责任。\n\n各功能详细使用说明请进入对应功能页面查看。"
            : "📋 Usage Notes\n\n• This tool does NOT inject or modify game memory. It only simulates input and modifies GGPK files.\n• Modifying GGPK files carries account security risks. Always backup original files first.\n• It checks for version updates once every 24 hours. A red dot appears beside the version when an update is available.\n• The authors assume no responsibility for any consequences.\n\nSee individual tool pages for usage instructions.";

        AcceptBtn.Content = isZh ? "同意并不再提示" : "Agree & Don't Show Again";
        RejectBtn.Content = isZh ? "拒绝并关闭" : "Decline & Close";
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        SavePreference();
        Accepted?.Invoke();
    }

    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        Declined?.Invoke();
    }

    private static void SavePreference()
    {
        ConfigService.SetValue("disclaimer_accepted", "true");
    }

    /// <summary>Check if user has already accepted the disclaimer.</summary>
    public static bool IsAccepted()
    {
        return string.Equals(ConfigService.GetValue("disclaimer_accepted"), "true", StringComparison.OrdinalIgnoreCase);
    }
}
