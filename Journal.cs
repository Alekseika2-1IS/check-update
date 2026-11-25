using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace UpdateHub
{
    public sealed class JournalEntry
    {
        public string Timestamp { get; private set; }
        public string Message { get; private set; }
        public bool IsError { get; private set; }
        private static readonly Regex CommandResult = new Regex(@"^(Проверка|Обновление .+?): (-?\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        private static readonly Regex ExceptionName = new Regex(@"^(?:[\w]+\.)+[\w]*Exception\b", RegexOptions.CultureInvariant);

        public JournalEntry(string timestamp, string message)
        {
            Timestamp = timestamp;
            Message = message;
            int end = message.IndexOfAny(new[] { '\r', '\n' });
            string firstLine = end < 0 ? message : message.Substring(0, end);
            var result = CommandResult.Match(firstLine);
            int code;
            IsError = firstLine.StartsWith("[Ошибка]", StringComparison.OrdinalIgnoreCase) ||
                firstLine.StartsWith("Не удалось", StringComparison.OrdinalIgnoreCase) ||
                firstLine.StartsWith("Проверка Windows Update:", StringComparison.Ordinal) ||
                firstLine.StartsWith("Проверка NVIDIA:", StringComparison.Ordinal) ||
                firstLine.StartsWith("Проверка версии приложения:", StringComparison.Ordinal) ||
                firstLine.StartsWith("Скачивание приложения:", StringComparison.Ordinal) ||
                firstLine.StartsWith("Открытие ", StringComparison.Ordinal) ||
                firstLine.StartsWith("Повторная проверка:", StringComparison.Ordinal) ||
                firstLine.StartsWith("Автоматическая проверка:", StringComparison.Ordinal) ||
                ExceptionName.IsMatch(firstLine) ||
                (result.Success && Int32.TryParse(result.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out code) &&
                    (result.Groups[1].Value == "Проверка" ? code != 0 : !Winget.UpgradeNeedsVerification(code)));
        }

        public string Raw { get { return String.IsNullOrEmpty(Timestamp) ? Message : Timestamp + " " + Message; } }
    }

    public sealed class JournalSnapshot
    {
        private const int PreviewBytes = 128 * 1024;
        private static readonly Regex EntryStart = new Regex(@"^(?<time>[0-9]{4}-[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2}Z) ",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        public readonly List<JournalEntry> Entries = new List<JournalEntry>();
        public bool Truncated { get; private set; }

        public static JournalSnapshot Read(string path)
        {
            var snapshot = new JournalSnapshot();
            string text;
            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new BinaryReader(stream))
                {
                    long length = stream.Length;
                    snapshot.Truncated = length > PreviewBytes;
                    stream.Position = Math.Max(0, length - PreviewBytes);
                    var bytes = reader.ReadBytes((int)Math.Min(PreviewBytes, length));
                    int first = 0;
                    while (first < bytes.Length && (bytes[first] & 0xC0) == 0x80) first++;
                    text = Winget.StripTerminalFormatting(Encoding.UTF8.GetString(bytes, first, bytes.Length - first)).TrimStart('\uFEFF');
                }
            }
            catch (FileNotFoundException) { return snapshot; }
            catch (DirectoryNotFoundException) { return snapshot; }

            var matches = EntryStart.Matches(text);
            int prefixLength = matches.Count == 0 ? text.Length : matches[0].Index;
            if (!snapshot.Truncated || matches.Count == 0)
                snapshot.AddEntry(null, text.Substring(0, prefixLength));
            for (int i = 0; i < matches.Count; i++)
            {
                var match = matches[i];
                int start = match.Index + match.Length;
                int end = i + 1 == matches.Count ? text.Length : matches[i + 1].Index;
                snapshot.AddEntry(match.Groups["time"].Value, text.Substring(start, end - start));
            }
            return snapshot;
        }

        private void AddEntry(string timestamp, string message)
        {
            if (!String.IsNullOrWhiteSpace(message)) Entries.Add(new JournalEntry(timestamp, message.TrimEnd('\r', '\n')));
        }
    }

    public partial class MainWindow
    {
        private readonly DockPanel journalPage = new DockPanel { Margin = new Thickness(28, 26, 28, 22), Visibility = Visibility.Collapsed };
        private readonly TextBox journalSearch = new TextBox();
        private readonly CheckBox journalErrorsOnly = new CheckBox { Content = "Только ошибки", VerticalAlignment = VerticalAlignment.Center };
        private readonly Button journalRefresh = new Button { Content = "Обновить" };
        private readonly Button journalCopy = new Button { Content = "Копировать записи" };
        private readonly TextBlock journalStatus = Label("", 12, "#9CAABB");
        private readonly RichTextBox journalText = new RichTextBox();
        private List<JournalEntry> visibleJournal = new List<JournalEntry>();
        private JournalSnapshot journal = new JournalSnapshot();
        private bool readingJournal;
        private bool journalReadFailed;

        private void BuildJournalPage()
        {
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            var title = Label("Журнал", 27, "#F2F7FF");
            title.FontWeight = FontWeights.SemiBold;
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            journalPage.Children.Add(header);

            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            journalCopy.Margin = new Thickness(8, 0, 0, 0);
            journalRefresh.Margin = new Thickness(14, 0, 0, 0);
            journalErrorsOnly.Margin = new Thickness(14, 0, 0, 0);
            foreach (UIElement action in new UIElement[] { journalCopy, journalRefresh, journalErrorsOnly })
            {
                DockPanel.SetDock(action, Dock.Right);
                toolbar.Children.Add(action);
            }
            toolbar.Children.Add(BuildSearchField(journalSearch, "Поиск по журналу"));
            DockPanel.SetDock(toolbar, Dock.Top);
            journalPage.Children.Add(toolbar);

            journalStatus.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(journalStatus, Dock.Top);
            journalPage.Children.Add(journalStatus);
            journalText.IsReadOnly = true;
            journalText.IsUndoEnabled = false;
            journalText.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            journalText.Background = Paint("#171C23");
            journalText.Foreground = Paint("#C8D7E9");
            journalText.SelectionBrush = Paint("#2D627C");
            journalText.BorderThickness = new Thickness(0);
            journalText.FontFamily = new FontFamily("Consolas");
            journalText.FontSize = 12;
            journalText.Padding = new Thickness(14, 10, 9, 10);
            System.Windows.Automation.AutomationProperties.SetName(journalText, "Записи журнала");
            journalPage.Children.Add(RoundedContentCard(journalText));

            journalSearch.TextChanged += (s, e) => RenderJournal();
            journalErrorsOnly.Click += (s, e) => RenderJournal();
            journalRefresh.Click += async (s, e) => await RefreshJournalAsync();
            journalCopy.Click += (s, e) =>
            {
                if (visibleJournal.Count == 0) return;
                try
                {
                    Clipboard.SetText(String.Join(Environment.NewLine + Environment.NewLine, visibleJournal.Select(entry => entry.Raw)));
                    journalStatus.Text = "Показанные записи скопированы.";
                }
                catch (ExternalException) { journalStatus.Text = "Буфер обмена занят. Попробуйте ещё раз."; }
            };
            RenderJournal();
        }

        private async Task RefreshJournalAsync()
        {
            if (readingJournal) return;
            readingJournal = true;
            journalRefresh.IsEnabled = false;
            journalStatus.Text = "Читаем журнал…";
            try
            {
                journal = await Task.Run(() => JournalSnapshot.Read(logFile));
                journalReadFailed = false;
            }
            catch (IOException) { journalReadFailed = true; }
            catch (UnauthorizedAccessException) { journalReadFailed = true; }
            finally
            {
                readingJournal = false;
                journalRefresh.IsEnabled = true;
                RenderJournal();
            }
        }

        private void RenderJournal()
        {
            string query = journalSearch.Text.Trim();
            visibleJournal = journal.Entries.Where(entry =>
                (journalErrorsOnly.IsChecked != true || entry.IsError) &&
                (query.Length == 0 || entry.Raw.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)).ToList();
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            foreach (var entry in visibleJournal)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                if (entry.IsError || !String.IsNullOrEmpty(entry.Timestamp))
                {
                    string heading = (entry.IsError ? "Ошибка  " : "") + entry.Timestamp;
                    paragraph.Inlines.Add(new Run(heading.TrimEnd())
                    {
                        Foreground = Paint(entry.IsError ? "#FFBF77" : "#8EADC9"),
                        FontWeight = entry.IsError ? FontWeights.SemiBold : FontWeights.Normal
                    });
                    paragraph.Inlines.Add(new LineBreak());
                }
                string message = entry.Message.StartsWith("[Ошибка] ", StringComparison.Ordinal) ? entry.Message.Substring(9) : entry.Message;
                paragraph.Inlines.Add(new Run(message));
                document.Blocks.Add(paragraph);
            }
            journalText.Document = document;
            journalText.ScrollToEnd();
            journalCopy.IsEnabled = visibleJournal.Count > 0;
            if (readingJournal) journalStatus.Text = "Читаем журнал…";
            else if (journalReadFailed) journalStatus.Text = "Не удалось прочитать журнал. Повторите попытку.";
            else if (journal.Entries.Count == 0) journalStatus.Text = "Записей пока нет.";
            else
            {
                journalStatus.Text = visibleJournal.Count == 0 ? "По выбранным фильтрам ничего не найдено."
                    : "Показано записей: " + visibleJournal.Count + " из " + journal.Entries.Count;
                if (journal.Truncated) journalStatus.Text += " · Показана последняя часть файла.";
            }
        }
    }
}
