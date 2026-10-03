using DevTools.App.Services;
using DevTools.Core.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DevTools.App.Controls;

/// <summary>
/// A JSON document as an expandable tree (FR-J11).
/// </summary>
/// <remarks>
/// <para>
/// The text view answers "what does it say"; this one answers "what shape is it" — which of a
/// hundred keys are objects, how many items an array really holds, where a path leads. A
/// formatted document can only be read top to bottom, and folding is the difference between
/// scanning a payload and scrolling one.
/// </para>
/// <para>
/// Children are reported as unrealised and filled on expand, so opening a large document costs
/// a parse and nothing more. The projection behind it caches each level the first time it is
/// asked for, which is what keeps collapse-and-expand free.
/// </para>
/// </remarks>
public sealed partial class JsonTreeView : UserControl
{
    private readonly IClipboardService _clipboard = App.GetService<IClipboardService>();

    public JsonTreeView() => InitializeComponent();

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(JsonTreeView), new PropertyMetadata("Structure"));

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>The root to show, or <see langword="null"/> for an empty tree.</summary>
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(
        nameof(Root), typeof(JsonOutlineNode), typeof(JsonTreeView),
        new PropertyMetadata(null, static (d, _) => ((JsonTreeView)d).Reload()));

    public JsonOutlineNode? Root
    {
        get => (JsonOutlineNode?)GetValue(RootProperty);
        set => SetValue(RootProperty, value);
    }

    public static readonly DependencyProperty SelectedPathProperty = DependencyProperty.Register(
        nameof(SelectedPath), typeof(string), typeof(JsonTreeView), new PropertyMetadata(string.Empty));

    public string SelectedPath
    {
        get => (string)GetValue(SelectedPathProperty);
        set => SetValue(SelectedPathProperty, value);
    }

    /// <summary>Raised when a row is selected, carrying the line it starts on.</summary>
    public event EventHandler<JsonOutlineNode>? NodeSelected;

    private void Reload()
    {
        Tree.RootNodes.Clear();
        SelectedPath = string.Empty;

        if (Root is not { } root)
        {
            return;
        }

        var node = CreateNode(root);
        Tree.RootNodes.Add(node);

        // The root is opened for you — children and all. A tree that starts closed makes the
        // first click a formality on every document, and the fill has to be explicit because
        // Expanding is only raised for expansions the control itself drives: setting IsExpanded
        // on its own would open a row with nothing under it.
        Realize(node);
        node.IsExpanded = true;
    }

    private static TreeViewNode CreateNode(JsonOutlineNode outline) => new()
    {
        Content = outline,
        HasUnrealizedChildren = outline.IsBranch,
    };

    /// <summary>
    /// Fills a branch's children, once.
    /// </summary>
    /// <remarks>
    /// <c>HasUnrealizedChildren</c> is cleared afterwards so the children survive a collapse:
    /// rebuilding them on every expand would throw away the expansion state of everything
    /// underneath, which turns exploring a document into losing your place in it.
    /// </remarks>
    /// <returns>How many rows were added.</returns>
    private static int Realize(TreeViewNode node)
    {
        if (!node.HasUnrealizedChildren || node.Content is not JsonOutlineNode outline)
        {
            return 0;
        }

        var added = 0;

        foreach (var child in outline.Children)
        {
            node.Children.Add(CreateNode(child));
            added++;
        }

        node.HasUnrealizedChildren = false;
        return added;
    }

    private void OnNodeExpanding(TreeView sender, TreeViewExpandingEventArgs args) =>
        Realize(args.Node);

    private void OnSelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) =>
        Select(args.AddedItems.Count > 0 ? args.AddedItems[0] : Tree.SelectedItem);

    /// <summary>
    /// Both events, because a single-selection <c>TreeView</c> does not reliably raise
    /// <c>SelectionChanged</c> for a click, and a path you cannot read is a path you cannot copy.
    /// </summary>
    private void OnItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args) =>
        Select(args.InvokedItem);

    /// <summary>
    /// Publishes a row's path.
    /// </summary>
    /// <remarks>
    /// With nodes added by hand the events carry the <c>TreeViewNode</c> rather than the row it
    /// holds, so the content has to be unwrapped. Both shapes are accepted because the two modes
    /// differ here and nothing outside this method should have to know which one is in use.
    /// </remarks>
    private void Select(object? item)
    {
        var outline = item switch
        {
            TreeViewNode node => node.Content as JsonOutlineNode,
            JsonOutlineNode row => row,
            _ => null,
        };

        if (outline is null)
        {
            return;
        }

        SelectedPath = outline.Path;
        NodeSelected?.Invoke(this, outline);
    }

    /// <summary>
    /// Opens everything, breadth first, filling branches as it goes.
    /// </summary>
    /// <remarks>
    /// Capped, because "expand all" on a document with a hundred thousand nodes is a request to
    /// build a hundred thousand rows and the honest answer is to open as much as stays usable.
    /// The rest is one click away where it matters.
    /// </remarks>
    private void OnExpandAllClick(object sender, RoutedEventArgs e)
    {
        const int limit = 5_000;

        var realized = 0;
        var queue = new Queue<TreeViewNode>(Tree.RootNodes);

        while (queue.Count > 0 && realized < limit)
        {
            var node = queue.Dequeue();

            if (node.Content is not JsonOutlineNode outline || !outline.IsBranch)
            {
                continue;
            }

            realized += Realize(node);
            node.IsExpanded = true;

            foreach (var child in node.Children)
            {
                queue.Enqueue(child);
            }
        }
    }

    private void OnCollapseAllClick(object sender, RoutedEventArgs e)
    {
        foreach (var node in Tree.RootNodes)
        {
            Collapse(node);
        }

        static void Collapse(TreeViewNode node)
        {
            foreach (var child in node.Children)
            {
                Collapse(child);
            }

            node.IsExpanded = false;
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(SelectedPath))
        {
            _clipboard.SetText(SelectedPath);
        }
    }
}
