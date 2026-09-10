using System.Collections.ObjectModel;
using System.ComponentModel;
using LibBundle3.Records;

namespace PoEToolbox.Plugins.DataBrowser.Models;

public class TreeItemViewModel : INotifyPropertyChanged
{
    /// <summary>Dummy item used as placeholder in unexpanded directory nodes so the expand triangle shows.</summary>
    public static readonly TreeItemViewModel Dummy = new() { Name = "..." };

    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long? Size { get; set; }
    public FileRecord? FileRecord { get; set; }

    private ObservableCollection<TreeItemViewModel>? _children;
    public ObservableCollection<TreeItemViewModel>? Children
    {
        get => _children;
        set { _children = value; PropertyChanged?.Invoke(this, new(nameof(Children))); }
    }

    private bool _isLoaded;
    public bool IsLoaded
    {
        get => _isLoaded;
        set { _isLoaded = value; PropertyChanged?.Invoke(this, new(nameof(IsLoaded))); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new(nameof(IsExpanded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
