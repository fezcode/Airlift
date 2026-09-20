using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Airlift.Desktop;

/// <summary>CommonMark/GitHub text rendered as native controls, without an HTML or script engine.</summary>
public sealed class MarkdownNotes : StackPanel
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseAutoLinks().UseEmphasisExtras().DisableHtml().Build();
    private readonly Uri _baseUri;
    private readonly Func<string, Task> _open;
    public MarkdownNotes(string markdown, string releaseUrl, Func<string, Task> open)
    {
        _baseUri = new Uri(releaseUrl); _open = open; Spacing = 12;
        var limited = markdown.Length > 24000 ? markdown[..24000] + "\n\n… Full notes are available on GitHub." : markdown;
        AddBlocks(this, Markdown.Parse(limited, Pipeline));
    }
    private void AddBlocks(StackPanel target, ContainerBlock container)
    {
        foreach (var block in container)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var title = Paragraph(heading.Inline); title.FontSize = heading.Level switch { 1 => 23, 2 => 19, _ => 15 };
                    // A heading keeping the body's line box would have its ascenders and descenders cut off.
                    title.LineHeight = Math.Round(title.FontSize * 1.35);
                    title.FontWeight = FontWeight.SemiBold; title.Foreground = Themes.Brush(p => p.TextBright);
                    target.Children.Add(title); break;
                case ParagraphBlock paragraph: target.Children.Add(Paragraph(paragraph.Inline)); break;
                case CodeBlock code:
                    var codeText = new SelectableTextBlock { Text = code.Lines.ToString(), FontFamily = FontFamily.Parse("Cascadia Mono, Menlo, DejaVu Sans Mono, monospace"), FontSize = 11, Foreground = Themes.Brush(p => p.TextCode) };
                    var scroller = new ScrollViewer { Content = codeText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
                    target.Children.Add(Ui.Card(scroller, 13, p => p.Code)); break;
                case ListBlock list:
                    var items = Ui.Stack(7); var index = int.TryParse(list.OrderedStart, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        var itemBody = Ui.Stack(6); AddBlocks(itemBody, item);
                        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 9 };
                        var marker = Ui.MutedText(list.IsOrdered ? $"{index++}." : "•", 12); marker.VerticalAlignment = VerticalAlignment.Top; marker.MinWidth = 13;
                        row.Children.Add(marker); row.Children.Add(itemBody); Grid.SetColumn(itemBody, 1); items.Children.Add(row);
                    }
                    target.Children.Add(items); break;
                case QuoteBlock quote:
                    var quoted = Ui.Stack(8); AddBlocks(quoted, quote);
                    target.Children.Add(new Border { Child = quoted, BorderBrush = Ui.Lime, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(13, 3, 0, 3) }); break;
                case ThematicBreakBlock: target.Children.Add(Ui.Separator()); break;
                case Table table:
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("*", table.ColumnDefinitions.Count))) };
                    var rowIndex = 0;
                    foreach (var row in table.OfType<TableRow>())
                    {
                        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); var column = 0;
                        foreach (var cell in row.OfType<TableCell>())
                        {
                            var cellContent = Ui.Stack(5); AddBlocks(cellContent, cell);
                            if (row.IsHeader) foreach (var text in cellContent.Children.OfType<TextBlock>()) text.FontWeight = FontWeight.SemiBold;
                            var border = new Border { Child = cellContent, Padding = new Thickness(10), BorderThickness = new Thickness(0, 0, 0, 1), BorderBrush = Ui.Line, Background = row.IsHeader ? Themes.Brush(p => p.Raised) : Brushes.Transparent };
                            grid.Children.Add(border); Grid.SetColumn(border, column++); Grid.SetRow(border, rowIndex);
                        }
                        rowIndex++;
                    }
                    target.Children.Add(grid); break;
                case ContainerBlock nested: var panel = Ui.Stack(8); AddBlocks(panel, nested); target.Children.Add(panel); break;
                case LeafBlock leaf: target.Children.Add(Paragraph(leaf.Inline)); break;
            }
        }
    }
    private SelectableTextBlock Paragraph(ContainerInline? inline)
    {
        var text = new SelectableTextBlock { FontSize = 12, Foreground = Themes.Brush(p => p.Prose), TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        if (inline != null) AddInlines(text.Inlines!, inline);
        return text;
    }
    private void AddInlines(InlineCollection target, ContainerInline container)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case LineBreakInline line: target.Add(line.IsHard ? new LineBreak() : new Run(" ")); break;
                case CodeInline code:
                    target.Add(new Run(code.Content) { FontFamily = FontFamily.Parse("Cascadia Mono, Menlo, DejaVu Sans Mono, monospace"), Background = Themes.Brush(p => p.CodeInline), Foreground = Ui.Lime }); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount == 2) span.FontWeight = FontWeight.Bold;
                    else span.FontStyle = FontStyle.Italic;
                    AddInlines(span.Inlines, emphasis); target.Add(span); break;
                case LinkInline link:
                    AddLink(target, link.Url, Flatten(link), link.IsImage); break;
                case AutolinkInline link: AddLink(target, link.Url, link.Url, false); break;
                case TaskList task:
                    target.Add(new Run(task.Checked ? "☑ " : "☐ ") { Foreground = Ui.Lime }); break;
                case ContainerInline nested:
                    var group = new Span(); AddInlines(group.Inlines, nested); target.Add(group); break;
            }
        }
    }
    private void AddLink(InlineCollection target, string? destination, string label, bool image)
    {
        var display = image ? "Image: " + label : label;
        if (!TryResolveLink(_baseUri, destination, out var uri)) { target.Add(new Run(display)); return; }
        var button = Ui.AsyncButton(display, () => _open(uri!.AbsoluteUri), "link"); button.FontSize = 12;
        ToolTip.SetTip(button, uri!.AbsoluteUri); target.Add(new InlineUIContainer { Child = button });
    }
    public static bool TryResolveLink(Uri baseUri, string? destination, out Uri? uri)
    {
        uri = null;
        return !string.IsNullOrWhiteSpace(destination) && Uri.TryCreate(baseUri, destination, out uri) && uri.Scheme == "https" && uri.UserInfo == "" && uri.IsDefaultPort;
    }
    private static string Flatten(ContainerInline inline) => string.Concat(inline.Select(node => node switch
    {
        LiteralInline literal => literal.Content.ToString(), CodeInline code => code.Content,
        ContainerInline nested => Flatten(nested), LineBreakInline => " ", _ => ""
    }));
}
