using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace UpdateHub
{
    public partial class MainWindow
    {
        private static readonly Tuple<string, string[,]>[] ButtonGuide =
        {
            Tuple.Create("Программы", new[,]
            {
                { "Обновления", "Показывает программы, для которых доступны обновления." },
                { "Все программы", "Показывает весь список, найденный WinGet, включая программы без обновлений и с пропущенными версиями." },
                { "Проверить обновления", "Загружает список программ и проверяет доступные обновления. Установка начинается после отдельного подтверждения." },
                { "С обновлениями", "Оставляет в списке программы с доступными обновлениями, кроме пропущенных версий." },
                { "Выбрать показанные", "Отмечает доступные обновления в текущем списке с учётом поиска." },
                { "Обновить выбранные", "Открывает список отмеченных программ для подтверждения установки." },
                { "Обновить все программы", "Предлагает обновить все доступные программы, включая скрытые поиском, кроме пропущенных версий." },
                { "Пропустить версию", "Скрывает это обновление для выбранной строки. Следующая версия снова появится в списке." },
                { "Вернуть обновление", "Отменяет пропуск выбранной версии. Найти её можно в разделе «Все программы»." },
                { "Очистить поиск", "Удаляет запрос из поля поиска и возвращает список с текущим фильтром." }
            }),
            Tuple.Create("История и журнал", new[,]
            {
                { "История", "Показывает даты, версии и результаты попыток обновления." },
                { "Повторить обновление", "Проверяет, доступно ли обновление для выбранной программы, и предлагает повторить установку." },
                { "Сверить версию", "Перепроверяет установленную версию и обновляет результат в истории." },
                { "Журнал", "Открывает технические записи Check Update и WinGet." },
                { "Только ошибки", "Показывает ошибки в журнале с учётом поискового запроса." },
                { "Обновить журнал", "Перечитывает журнал и показывает новые записи." },
                { "Копировать записи", "Копирует записи, которые видны с текущим поиском и фильтром." }
            }),
            Tuple.Create("Windows и NVIDIA", new[,]
            {
                { "Windows и NVIDIA", "Открывает отдельный раздел обновлений Windows и драйверов NVIDIA." },
                { "Проверить Windows и NVIDIA", "Перепроверяет доступные обновления Windows и драйверы NVIDIA." },
                { "Открыть Windows Update", "Открывает страницу обновлений в настройках Windows." },
                { "Дополнительные обновления Windows", "Открывает список дополнительных обновлений в настройках Windows." },
                { "Открыть NVIDIA App", "Запускает программу NVIDIA, где можно скачать и установить драйвер." },
                { "Драйверы на сайте NVIDIA", "Открывает официальный каталог драйверов NVIDIA в браузере." },
                { "Список исправлений NVIDIA", "Открывает примечания NVIDIA к выпуску драйвера." },
                { "Проверить запись журнала", "В течение 30 секунд наблюдает за файлом журнала NVIDIA. Результат показывает, менялся ли файл." }
            }),
            Tuple.Create("Перенос, расписание и версия", new[,]
            {
                { "Проверять обновления при запуске", "Включает автоматическую проверку наличия новых версий программ при открытии Check Update." },
                { "Сохранить список", "Сохраняет список программ, найденных WinGet, в файл JSON для переноса." },
                { "Перенести программы", "Открывает файл, показывает недостающие программы и предлагает установить их через WinGet." },
                { "Расписание обновлений", "Настраивает ежедневную установку обновлений программ, пока Check Update открыт." },
                { "Проверить версию", "Ищет новый выпуск самого Check Update на GitHub." },
                { "Скачать обновление", "Скачивает и проверяет новый выпуск Check Update, затем предлагает перезапуск." },
                { "Перезапустить", "Открывает подтверждение перезапуска для установки скачанной версии Check Update." },
                { "Справка", "Открывает описание кнопок. Эту справку можно открыть клавишей F1." }
            }),
            Tuple.Create("Окна подтверждения", new[,]
            {
                { "Обновить", "Запускает установку обновлений, перечисленных в окне." },
                { "Установить", "Начинает установку недостающих программ из списка переноса." },
                { "Сохранить", "Применяет выбранные настройки расписания." },
                { "Отмена", "Закрывает окно без подтверждения действия." },
                { "Закрыть", "Закрывает справку и возвращает к предыдущему разделу." }
            })
        };

        private static void ApplyButtonHelp(FrameworkElement control, string name)
        {
            foreach (var section in ButtonGuide)
            {
                var items = section.Item2;
                for (int i = 0; i < items.GetLength(0); i++)
                {
                    if (items[i, 0] != name) continue;
                    control.ToolTip = items[i, 1];
                    AutomationProperties.SetHelpText(control, items[i, 1]);
                    ToolTipService.SetShowOnDisabled(control, true);
                    return;
                }
            }
        }

        private Button CreateHelpButton()
        {
            var help = new Button { Content = "Справка", FontSize = 11, Padding = new Thickness(6, 3, 6, 3) };
            ApplyButtonHelp(help, "Справка");
            help.Click += (s, e) => ShowButtonHelp();
            var command = new RoutedCommand();
            InputBindings.Add(new KeyBinding(command, Key.F1, ModifierKeys.None));
            CommandBindings.Add(new CommandBinding(command, (s, e) => ShowButtonHelp()));
            return help;
        }

        private void ShowButtonHelp()
        {
            if (!workflowDialogOpen)
                ShowWorkflowConfirmation(BuildButtonHelp());
        }

        private Window BuildButtonHelp()
        {
            var entries = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            foreach (var section in ButtonGuide)
            {
                var heading = Label(section.Item1, 17, "#89E9FC");
                heading.Margin = new Thickness(0, entries.Children.Count == 0 ? 0 : 14, 0, 8);
                heading.FontWeight = FontWeights.SemiBold;
                AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
                entries.Children.Add(heading);
                var items = section.Item2;
                for (int i = 0; i < items.GetLength(0); i++)
                {
                    var title = Label(items[i, 0], 13, "#EDF4FC");
                    title.FontWeight = FontWeights.SemiBold;
                    var description = Label(items[i, 1], 12, "#B1C0D4");
                    description.Margin = new Thickness(0, 2, 0, 9);
                    entries.Children.Add(title);
                    entries.Children.Add(description);
                }
            }
            var scroll = new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = Math.Max(120, Math.Min(480, SystemParameters.WorkArea.Height - 235)) };
            return BuildConfirmation("Справка", "Что делают кнопки", "Действия сгруппированы по разделам.",
                scroll, null, null, "Закрыть", 660);
        }
    }
}
