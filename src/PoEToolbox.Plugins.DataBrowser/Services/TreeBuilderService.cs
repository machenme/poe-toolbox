using System.Collections.ObjectModel;
using System.Linq;
using LibBundle3.Nodes;

namespace PoEToolbox.Plugins.DataBrowser.Services;

public static class TreeBuilderService
{
    public static ObservableCollection<Models.TreeItemViewModel> BuildRoot(IDirectoryNode root)
    {
        var items = new ObservableCollection<Models.TreeItemViewModel>();
        // Directories first, then files; each group sorted alphabetically
        foreach (var child in root.Children.OrderBy(c => c is not IDirectoryNode).ThenBy(c => c.Name))
        {
            if (child is IDirectoryNode dir)
                items.Add(CreateDirectoryVM(dir));
            else if (child is IFileNode file)
                items.Add(CreateFileVM(file));
        }
        return items;
    }

    public static void LoadChildren(Models.TreeItemViewModel parent, IDirectoryNode dirNode)
    {
        // Already loaded or no dummy (empty dir)
        if (parent.IsLoaded) return;
        if (parent.Children == null || parent.Children.Count == 0) return;
        if (parent.Children[0] != Models.TreeItemViewModel.Dummy) return; // already loaded

        var children = new ObservableCollection<Models.TreeItemViewModel>();
        foreach (var child in dirNode.Children.OrderBy(c => c is not IDirectoryNode).ThenBy(c => c.Name))
        {
            if (child is IDirectoryNode subDir)
                children.Add(CreateDirectoryVM(subDir));
            else if (child is IFileNode file)
                children.Add(CreateFileVM(file));
        }
        parent.Children = children;
        parent.IsLoaded = true;
    }

    private static Models.TreeItemViewModel CreateDirectoryVM(IDirectoryNode dir)
    {
        bool hasChildren = dir.Children.Count > 0;
        return new Models.TreeItemViewModel
        {
            Name = dir.Name,
            FullPath = ITreeNode.GetPath(dir),
            IsDirectory = true,
            // Dummy placeholder so TreeView shows expand arrow; replaced on first expand
            Children = hasChildren
                ? new ObservableCollection<Models.TreeItemViewModel> { Models.TreeItemViewModel.Dummy }
                : new ObservableCollection<Models.TreeItemViewModel>(),
            IsLoaded = !hasChildren
        };
    }

    private static Models.TreeItemViewModel CreateFileVM(IFileNode file)
    {
        return new Models.TreeItemViewModel
        {
            Name = file.Name,
            FullPath = file.Record.Path ?? file.Name,
            IsDirectory = false,
            Size = file.Record.Size,
            FileRecord = file.Record,
            Children = null,
            IsLoaded = true
        };
    }
}
