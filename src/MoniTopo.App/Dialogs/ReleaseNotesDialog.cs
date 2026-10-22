using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using MoniTopo.App.Tray;
using MoniTopo.Core.Updates;
using Button = System.Windows.Controls.Button;

namespace MoniTopo.App.Dialogs;

public sealed class ReleaseNotesDialog : Window
{
    public ReleaseNotesDialog(AvailableUpdate update)
    {
        Title = "MoniTopo release notes";
        Icon = ApplicationIcon.WindowIcon;
        Width = 600;
        Height = 480;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        var panel = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock
        {
            Text = $"Version {update.DisplayVersion}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12),
        };
        DockPanel.SetDock(heading, Dock.Top);
        panel.Children.Add(heading);
        var close = new Button
        {
            Content = "Close",
            IsCancel = true,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            MinWidth = 80,
            Margin = new Thickness(0, 12, 0, 0),
        };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        NotesViewer = new FlowDocumentScrollViewer
        {
            Document = CreateDocument(update.ReleaseNotes),
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        NotesViewer.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        NotesViewer.SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        panel.Children.Add(NotesViewer);
        Content = panel;
    }

    internal FlowDocumentScrollViewer NotesViewer { get; }

    private static FlowDocument CreateDocument(string? notes)
    {
        var document = new FlowDocument
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13,
            PagePadding = new Thickness(0),
        };
        var text = string.IsNullOrWhiteSpace(notes)
            ? "Release notes are not available for this update."
            : notes;
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var trimmed = line.TrimStart();
            var headingLength = trimmed.TakeWhile(character => character == '#').Count();
            var isHeading = headingLength is > 0 and <= 6 &&
                            trimmed.Length > headingLength && trimmed[headingLength] == ' ';
            var content = isHeading ? trimmed[(headingLength + 1)..] : line;
            if (content.StartsWith("- ", StringComparison.Ordinal) || content.StartsWith("* ", StringComparison.Ordinal))
            {
                content = "• " + content[2..];
            }
            // Feed text is untrusted. Render literal text only, never HTML, XAML, images, or executable links.
            document.Blocks.Add(new Paragraph(new Run(content))
            {
                FontSize = isHeading ? 17 : 13,
                FontWeight = isHeading ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 0, string.IsNullOrWhiteSpace(content) ? 3 : 6),
            });
        }
        return document;
    }
}
