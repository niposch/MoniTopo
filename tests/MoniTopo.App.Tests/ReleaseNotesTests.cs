using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MoniTopo.App.Dialogs;
using MoniTopo.App.Popup;
using MoniTopo.Core.Updates;

namespace MoniTopo.App.Tests;

[Collection(WpfUiTestCollectionDefinition.Name)]
public sealed class ReleaseNotesTests
{
    [Theory]
    [InlineData(UpdateStatus.Available)]
    [InlineData(UpdateStatus.Downloading)]
    [InlineData(UpdateStatus.ReadyToInstall)]
    public void TrayNotesActionIsSeparateFromDownloadAndRestart(UpdateStatus status)
    {
        StaDispatcherTest.Run(() =>
        {
            var update = new AvailableUpdate("2026.1022.0", "2026.10.22", "# Fixes\n- Corrected capture.");
            var popup = new PopupWindow { ShowActivated = false, ShowInTaskbar = false };
            popup.ViewModel.ApplyUpdateState(new UpdateState(status, update, 50, "Update"));
            var updates = 0;
            AvailableUpdate? requestedNotes = null;
            popup.UpdateActionRequested += (_, _) => updates++;
            popup.ReleaseNotesRequested += (_, value) => requestedNotes = value;
            try
            {
                popup.Show();
                popup.UpdateLayout();
                var notesButton = Descendants<Button>(popup).Single(button => AutomationProperties.GetName(button) == "View release notes");
                var downloadButton = Descendants<Button>(popup).Single(button => AutomationProperties.GetName(button) == "Update MoniTopo");
                var actions = (Grid)popup.FindName("UpdateActions");
                Assert.Equal(12, actions.Margin.Left);
                Assert.Equal(12, actions.Margin.Right);
                Assert.True(notesButton.TranslatePoint(new Point(), actions).X < downloadButton.TranslatePoint(new Point(), actions).X);
                Assert.Equal(0, Grid.GetColumn(notesButton));
                Assert.Equal(1, Grid.GetColumn(downloadButton));
                Assert.True(notesButton.IsVisible);
                Assert.True(notesButton.IsEnabled);
                notesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Same(update, requestedNotes);
                Assert.Equal(0, updates);
                Assert.False(popup.IsVisible);
                popup.ViewModel.SetActivationBusy(true);
                Assert.False(notesButton.IsEnabled);
                popup.ViewModel.SetActivationBusy(false);
                Assert.True(notesButton.IsEnabled);
            }
            finally { popup.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void MissingNotesHaveAnExplicitFallback(string? notes)
    {
        StaDispatcherTest.Run(() =>
        {
            var dialog = new ReleaseNotesDialog(new AvailableUpdate("2026.1022.0", "2026.10.22", notes));
            var text = new TextRange(dialog.NotesViewer.Document.ContentStart, dialog.NotesViewer.Document.ContentEnd).Text;
            Assert.Contains("Release notes are not available", text, StringComparison.Ordinal);
            dialog.Close();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void NotesAreScrollableSelectableLiteralTextNotExecutableMarkup()
    {
        StaDispatcherTest.Run(() =>
        {
            var dialog = new ReleaseNotesDialog(new AvailableUpdate("2026.1022.0", "2026.10.22",
                "# Fixes\n- HDR capture\n<script>untrusted()</script>\n[link](file:///example)\n" + string.Join('\n', Enumerable.Repeat("More notes", 100))))
            { ShowActivated = false, ShowInTaskbar = false };
            try
            {
                dialog.Show();
                dialog.UpdateLayout();
                Assert.Equal(ScrollBarVisibility.Auto, dialog.NotesViewer.VerticalScrollBarVisibility);
                var document = dialog.NotesViewer.Document;
                var heading = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
                Assert.Equal(FontWeights.SemiBold, heading.FontWeight);
                var text = new TextRange(document.ContentStart, document.ContentEnd).Text;
                Assert.Contains("• HDR capture", text, StringComparison.Ordinal);
                Assert.Contains("<script>untrusted()</script>", text, StringComparison.Ordinal);
                Assert.Contains("[link](file:///example)", text, StringComparison.Ordinal);
                Assert.Equal(104, document.Blocks.Count);
                Assert.All(document.Blocks.Cast<Paragraph>(), paragraph => Assert.All(paragraph.Inlines.Cast<Inline>(), inline => Assert.IsType<Run>(inline)));
            }
            finally { dialog.Close(); }
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typed) yield return typed;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
