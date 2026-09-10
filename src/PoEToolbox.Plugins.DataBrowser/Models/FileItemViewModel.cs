using LibBundle3.Records;

namespace PoEToolbox.Plugins.DataBrowser.Models;

public class FileItemViewModel
{
    public string Name { get; set; } = "";
    public string SizeDisplay { get; set; } = "";
    public string FullPath { get; set; } = "";
    public FileRecord? FileRecord { get; set; }
}
