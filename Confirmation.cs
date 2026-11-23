using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace UpdateHub
{
    public partial class MainWindow
    {
        private Window BuildProgramConfirmation(List<Package> chosen)
        {
            var list = new DataGrid
            {
                ItemsSource = chosen,
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserReorderColumns = false,
                CanUserResizeColumns = false,
                CanUserResizeRows = false,
                CanUserSortColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeaderWidth = 0,
                MinRowHeight = 36,
                ColumnHeaderHeight = 36,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Paint("#273449"),
                Background = Paint("#151E2E"),
                BorderThickness = new Thickness(0),
                MaxHeight = Math.Max(140, Math.Min(330, SystemParameters.WorkArea.Height - 300))
            };
            ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(list, "Программы для обновления");
            list.Columns.Add(ConfirmationColumn("ПРОГРАММА", "Name", 2.4, "#EDF4FC"));
            list.Columns.Add(ConfirmationColumn("УСТАНОВЛЕНО", "Installed", 1, "#A0B0C6"));
            list.Columns.Add(ConfirmationColumn("НОВАЯ ВЕРСИЯ", "Available", 1, "#89E9FC"));
            return BuildConfirmation("Подтверждение обновлений", "Обновить программы?", "Выбрано программ: " + chosen.Count,
                RoundedContentCard(list), "Сохраните работу в выбранных программах. Установщики могут запросить права администратора. Нажимая «Обновить», вы принимаете лицензионные условия обновлений.",
                "Обновить", "Отмена", 660);
        }

        private static DataGridTextColumn ConfirmationColumn(string title, string property, double weight, string color)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.ForegroundProperty, Paint(color)));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.MarginProperty, new Thickness(0, 6, 0, 6)));
            style.Setters.Add(new Setter(ToolTipProperty, new Binding(property)));
            if (property == "Name") style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
            return new DataGridTextColumn
            {
                Header = title,
                Binding = new Binding(property),
                Width = new DataGridLength(weight, DataGridLengthUnitType.Star),
                ElementStyle = style
            };
        }

        private Window BuildRestartConfirmation(Version nextVersion)
        {
            var versions = new Grid();
            versions.ColumnDefinitions.Add(new ColumnDefinition());
            versions.ColumnDefinitions.Add(new ColumnDefinition());
            var current = ConfirmationVersion("Текущая версия", SelfUpdate.CurrentVersion, "#EDF4FC");
            var next = ConfirmationVersion("Новая версия", nextVersion, "#89E9FC");
            next.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(next, 1);
            versions.Children.Add(current);
            versions.Children.Add(next);
            return BuildConfirmation("Обновление Check Update", "Перезапустить Check Update?", "Обновление скачано и проверено.",
                Card(versions, "#142638", new Thickness(14)),
                "При перезапуске Check Update применит новую версию и откроется снова.",
                "Перезапустить", "Позже", 490);
        }

        private static StackPanel ConfirmationVersion(string title, Version version, string color)
        {
            var panel = new StackPanel();
            panel.Children.Add(Label(title, 12, "#A0B0C6"));
            var number = Label(version.ToString(3), 23, color);
            number.Margin = new Thickness(0, 4, 0, 0);
            number.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(number);
            return panel;
        }

        private Window BuildConfirmation(string title, string heading, string summary, UIElement detail, string note, string action, string dismiss, double width)
        {
            var dialog = new Window
            {
                Owner = this,
                Icon = Icon,
                Title = title,
                Width = Math.Min(width, SystemParameters.WorkArea.Width - 24),
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
                Background = Background,
                Foreground = Foreground,
                FontFamily = FontFamily,
                FontSize = FontSize,
                UseLayoutRounding = true
            };
            dialog.Resources.MergedDictionaries.Add(Resources.MergedDictionaries[0]);
            dialog.SourceInitialized += (s, e) => UseDarkTitleBar(dialog);

            var body = new StackPanel { Margin = new Thickness(24, 21, 24, 22) };
            var headline = Label(heading, 24, "#F2F7FF");
            headline.FontWeight = FontWeights.SemiBold;
            body.Children.Add(headline);
            var subtitle = Label(summary, 13, "#A0B0C6");
            subtitle.Margin = new Thickness(0, 6, 0, 16);
            body.Children.Add(subtitle);
            body.Children.Add(detail);
            var explanation = Label(note, 12, "#B1C0D4");
            explanation.Margin = new Thickness(0, 14, 0, 18);
            body.Children.Add(explanation);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = dismiss, IsCancel = true, Margin = new Thickness(0, 0, 8, 0), MinWidth = 88 };
            cancel.Click += (s, e) => dialog.DialogResult = false;
            var confirm = new Button
            {
                Content = action, IsDefault = true, MinWidth = 116,
                Background = Paint("#79DFF4"), BorderBrush = Paint("#79DFF4"), Foreground = Paint("#102332")
            };
            confirm.SetResourceReference(StyleProperty, "AnimatedButton");
            confirm.Click += (s, e) => dialog.DialogResult = true;
            actions.Children.Add(cancel);
            actions.Children.Add(confirm);
            body.Children.Add(actions);
            var surface = new Border { Background = Background, Child = body };
            dialog.Content = surface;
            return dialog;
        }
    }
}
