using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using HgVs.Models;
using HgVs.ViewModels;

public static class HgFileTreeTests
{
    public static void Run()
    {
        var files = new ObservableCollection<HgFileItem>
        {
            File("ProjectStudio/CAE/Project/Module/Code.cs", "R"),
            File("ProjectStudio/CAE/Project/Module/Other.cs", "M"),
            File("ProjectStudio/CAE/Tests/Module/CodeTests.cs", "A")
        };
        var view = new ListCollectionView(files);
        HgFileItem selected = null;
        var repository = @"D:\repo";
        var tree = new HgFileTree(view, () => repository, file => selected = file);
        var list = new ListBox { ItemsSource = tree.Rows };
        list.SetBinding(ListBox.SelectedItemProperty,
            new Binding("SelectedRow") { Source = tree, Mode = BindingMode.TwoWay });
        tree.SetActive(true);
        Assert(tree.Rows.Count == 6, "folder/file count");
        Assert(tree.Rows[0].Name == @"ProjectStudio\CAE", "compact common chain");
        Assert(tree.Rows[1].Name == @"Project\Module", "compact code chain");
        Assert(tree.Rows[4].Name == @"Tests\Module", "compact tests chain");
        Assert(tree.Rows[2].Name == "Code.cs" && tree.Rows[2].File.Status == "R", "filename and rename status");

        var codeFolder = tree.Rows[1];
        tree.SelectFile(files[0]);
        Assert(selected == files[0] && list.SelectedItem == tree.SelectedRow, "selection binding");
        tree.Toggle(codeFolder);
        Assert(tree.Rows.Count == 4 && tree.SelectedRow == codeFolder && selected == null, "collapse selected descendant");
        tree.SelectFile(files[0]);
        Assert(tree.Rows.Count == 6 && codeFolder.IsExpanded, "Locate opens ancestors");
        Assert(tree.SelectedRow.File == files[0], "Locate selects original file");
        files[0].IsReviewed = true;
        Assert(tree.SelectedRow.File.IsReviewed, "shared reviewed state");

        tree.SelectedRow = codeFolder;
        tree.Toggle(codeFolder);
        view.Refresh();
        Drain();
        Assert(tree.Rows.Count == 4 && !tree.Rows[1].IsExpanded, "collapse survives Refresh");
        Assert(tree.SelectedRow == tree.Rows[1], "folder selection survives Refresh");

        view.Filter = item => ((HgFileItem)item).Path.Contains("Tests");
        Drain();
        Assert(tree.Rows.Count == 2 && tree.Rows[0].Name == @"ProjectStudio\CAE\Tests\Module", "filtered compact tree");
        Assert(tree.Rows[1].File == files[2], "filter does not copy file models");
        view.Filter = null;
        Drain();
        Assert(tree.Rows.Count == 4 && !tree.Rows[1].IsExpanded, "filter preserves folder state");

        tree.SelectFile(files[0]);
        var replacement = File(files[0].Path, "R");
        files[0] = replacement;
        Drain();
        Assert(tree.SelectedRow.File == replacement && selected == replacement, "refresh restores selection by path");

        view.Filter = item => ((HgFileItem)item).Path.Contains("Tests");
        Drain();
        Assert(tree.SelectedRow == null && selected == null, "filter clears hidden file selection");
        view.Filter = null;
        Drain();

        // No work while TODO/list is active; returning to the tree catches up.
        tree.SetActive(false);
        var before = tree.Rows.Count;
        files.Add(File("Root.cs", "M"));
        Drain();
        Assert(tree.Rows.Count == before, "inactive tree stays idle");
        tree.SetActive(true);
        Assert(tree.Rows.Any(row => row.File != null && row.File.Path == "Root.cs"), "returning to tree catches up");

        files.Add(File("Fresh/Folder/New.cs", "M"));
        Assert(tree.SelectFile(files.Last()) != null, "Locate before queued rebuild");
        Drain();

        var resets = 0;
        tree.Rows.CollectionChanged += (sender, args) => { if (args.Action == NotifyCollectionChangedAction.Reset) resets++; };
        for (var i = 0; i < 2000; i++) files.Add(File("Bulk/Files/File" + i + ".cs", "M"));
        Drain();
        Assert(resets == 1, "batch refresh rebuilds once");
        tree.SelectFile(files.Last());
        Assert(tree.Rows.Count(row => row.File != null) == files.Count, "more than 1000 files");

        repository = @"D:\other-repo";
        tree.SetActive(false);
        files.Clear();
        files.Add(File(@"D:\other-repo\Folder\Nested\Absolute.cs", "M"));
        tree.SetActive(true);
        Assert(tree.Rows.Count == 2 && tree.Rows[0].Name == @"Folder\Nested", "absolute path relative to repository");
        Assert(tree.Rows[0].IsExpanded, "repository switch clears folder collapse state");
        Console.WriteLine("PASS: compact folders, selection, Refresh, filters, Locate, review/R, mode switching, 2000-file batch, absolute paths.");
    }

    private static HgFileItem File(string path, string status)
    {
        return new HgFileItem { Path = path, Status = status, Identity = "file|" + path };
    }

    private static void Drain()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }
}
