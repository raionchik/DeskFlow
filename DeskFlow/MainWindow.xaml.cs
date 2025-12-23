using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using WinForms = System.Windows.Forms;

namespace DeskFlow
{
    public class FileItem
    {
        public Guid Id { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public string? Size { get; set; }
        public string? Category { get; set; }
        public string? Icon { get; set; }
        [JsonIgnore]
        public SolidColorBrush? CategoryColor { get; set; }
    }

    public class Profile
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<FileItem>? Files { get; set; }
        public string FilesCount => $"Файлов: {Files?.Count ?? 0}";
    }

    public class TaskItem
    {
        public Guid Id { get; set; }
        public string? Text { get; set; }
        public bool Completed { get; set; }
    }

    public class AppData
    {
        public List<FileItem>? Files { get; set; }
        public List<Profile>? Profiles { get; set; }
        public List<TaskItem>? Tasks { get; set; }
    }

    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> files = new ObservableCollection<FileItem>();
        private ObservableCollection<Profile> profiles = new ObservableCollection<Profile>();
        private ObservableCollection<TaskItem> tasks = new ObservableCollection<TaskItem>();
        private List<List<FileItem>> sortingHistory = new List<List<FileItem>>();
        private string dataFilePath;
        private string desktopPath;
        private string sortDirectory;
        private FileSystemWatcher? desktopWatcher;
        private DispatcherTimer? clockTimer;
        private DispatcherTimer? notesTimer;
        private DispatcherTimer? notificationTimer;
        private DispatcherTimer? fileProcessingTimer; // НОВОЕ: для отложенной обработки файлов
        private Queue<FileSystemEventArgs> pendingFileEvents = new Queue<FileSystemEventArgs>(); // НОВОЕ

        // Виджеты
        private ClockWidget? clockWidget;
        private CalendarWidget? calendarWidget;
        private NotesWidget? notesWidget;
        private TasksWidget? tasksWidget;

        public MainWindow()
        {
            InitializeComponent();

            desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            sortDirectory = desktopPath;
            dataFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskFlow",
                "data.json"
            );
            var directoryName = Path.GetDirectoryName(dataFilePath);
            if (directoryName != null)
            {
                Directory.CreateDirectory(directoryName);
            }

            FilesListBox.ItemsSource = files;
            ProfilesListBox.ItemsSource = profiles;
            TasksListBox.ItemsSource = tasks;
            SortDirectoryTextBox.Text = sortDirectory;

            LoadData();
            LoadNotesFromFile();
            UpdateStats();

            InitializeDesktopWatcher();
            InitializeClock();
            InitializeNotesAutoSave();
            InitializeNotificationTimer();
            InitializeFileProcessingTimer(); // НОВОЕ
            ClockFormat.SelectionChanged += (s, e) => UpdateClock();
        }

        // НОВОЕ: Таймер для отложенной обработки файлов
        private void InitializeFileProcessingTimer()
        {
            fileProcessingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            fileProcessingTimer.Tick += (s, e) =>
            {
                fileProcessingTimer.Stop();
                ProcessPendingFileEvents();
            };
        }

        // НОВОЕ: Обработка накопленных событий файловой системы
        private void ProcessPendingFileEvents()
        {
            try
            {
                while (pendingFileEvents.Count > 0)
                {
                    var evt = pendingFileEvents.Dequeue();
                    ProcessFileEvent(evt);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обработки событий: {ex.Message}");
            }
        }

        // НОВОЕ: Безопасная обработка события файловой системы
        private void ProcessFileEvent(FileSystemEventArgs e)
        {
            try
            {
                if (!File.Exists(e.FullPath))
                {
                    // Файл был удален
                    var fileToRemove = files.FirstOrDefault(f => f.Path == e.FullPath);
                    if (fileToRemove != null)
                    {
                        files.Remove(fileToRemove);
                        SaveData();
                        UpdateStats();
                    }
                    return;
                }

                // Ждем, пока файл станет доступен
                System.Threading.Thread.Sleep(100);

                var fileInfo = new FileInfo(e.FullPath);

                // Проверяем, что файл существует и доступен
                if (!fileInfo.Exists) return;

                // Пропускаем системные и скрытые файлы
                if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                    (fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                {
                    return;
                }

                // Проверяем, нет ли уже такого файла
                var existingFile = files.FirstOrDefault(f => f.Path == e.FullPath);
                if (existingFile != null)
                {
                    // Обновляем существующий файл
                    existingFile.Name = fileInfo.Name;
                    existingFile.Size = FormatFileSize(fileInfo.Length);
                }
                else
                {
                    // Добавляем новый файл
                    var fileItem = CreateFileItem(fileInfo);
                    files.Add(fileItem);
                    ShowNotification($"➕ Новый файл: {fileInfo.Name}");

                    if (ChkAutoSort?.IsChecked == true)
                    {
                        AutoSort();
                    }
                }

                SaveData();
                UpdateStats();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка обработки файла {e.FullPath}: {ex.Message}");
            }
        }

        private void InitializeNotificationTimer()
        {
            notificationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            notificationTimer.Tick += (s, e) =>
            {
                NotificationToast.Visibility = Visibility.Collapsed;
                notificationTimer.Stop();
            };
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ScanDesktop();
            ShowNotification($"✓ Загружено {files.Count} файлов с рабочего стола");
        }

        // === Часы ===
        private void InitializeClock()
        {
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (s, e) => UpdateClock();
            clockTimer.Start();
            UpdateClock();
        }

        private void UpdateClock()
        {
            var now = DateTime.Now;
            bool is24Hour = ClockFormat?.SelectedIndex == 0;

            ClockTime.Text = is24Hour
                ? now.ToString("HH:mm:ss")
                : now.ToString("hh:mm:ss tt");

            var culture = new CultureInfo("ru-RU");
            ClockDate.Text = now.ToString("dddd, d MMMM yyyy", culture);

            if (clockWidget != null && clockWidget.IsLoaded)
            {
                clockWidget.ClockFormat.SelectedIndex = ClockFormat.SelectedIndex;
            }
        }

        // === Автосохранение заметок ===
        private void InitializeNotesAutoSave()
        {
            notesTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            notesTimer.Tick += (s, e) =>
            {
                SaveNotesToFile();
                notesTimer.Stop();
            };
        }

        private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            notesTimer?.Stop();
            notesTimer?.Start();
            NotesStatus.Text = "Сохранение...";
            NotesStatus.Foreground = new SolidColorBrush(Color.FromRgb(251, 191, 36));

            if (notesWidget != null && notesWidget.IsLoaded)
            {
                if (notesWidget.NotesTextBox.Text != NotesTextBox.Text)
                {
                    notesWidget.NotesTextBox.Text = NotesTextBox.Text;
                }
            }
        }

        private void SaveNotesToFile()
        {
            try
            {
                var notesPath = Path.Combine(Path.GetDirectoryName(dataFilePath) ?? string.Empty, "notes.txt");
                File.WriteAllText(notesPath, NotesTextBox.Text);
                NotesStatus.Text = "✓ Сохранено автоматически";
                NotesStatus.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            }
            catch { }
        }

        private void LoadNotesFromFile()
        {
            try
            {
                var notesPath = Path.Combine(Path.GetDirectoryName(dataFilePath) ?? string.Empty, "notes.txt");
                if (File.Exists(notesPath))
                {
                    NotesTextBox.Text = File.ReadAllText(notesPath);
                }
            }
            catch { }
        }

        // === Задачи ===
        private void NewTaskTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == Key.Enter && !string.IsNullOrWhiteSpace(NewTaskTextBox.Text))
            {
                AddTask();
            }
        }

        private void BtnAddTask_Click(object sender, RoutedEventArgs e)
        {
            AddTask();
        }

        private void AddTask()
        {
            if (string.IsNullOrWhiteSpace(NewTaskTextBox.Text)) return;
            var task = new TaskItem
            {
                Id = Guid.NewGuid(),
                Text = NewTaskTextBox.Text,
                Completed = false
            };
            tasks.Add(task);
            NewTaskTextBox.Text = string.Empty;
            SaveData();
            UpdateTasksStats();
        }

        private void TaskCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            SaveData();
            UpdateTasksStats();
        }

        private void BtnDeleteTask_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var task = button?.Tag as TaskItem;
            if (task != null)
            {
                tasks.Remove(task);
                SaveData();
                UpdateTasksStats();
            }
        }

        private void UpdateTasksStats()
        {
            var completed = tasks.Count(t => t.Completed);
            TasksStats.Text = $"Выполнено: {completed} из {tasks.Count}";
        }

        // === Мониторинг рабочего стола (ИСПРАВЛЕНО) ===
        private void InitializeDesktopWatcher()
        {
            try
            {
                desktopWatcher = new FileSystemWatcher
                {
                    Path = desktopPath,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    EnableRaisingEvents = false, // Запускаем только когда включен мониторинг
                    IncludeSubdirectories = false
                };

                desktopWatcher.Created += OnDesktopFileCreated;
                desktopWatcher.Deleted += OnDesktopFileDeleted;
                desktopWatcher.Renamed += OnDesktopFileRenamed;
                desktopWatcher.Error += OnWatcherError; // НОВОЕ: обработка ошибок
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка инициализации мониторинга: {ex.Message}", true);
            }
        }

        // НОВОЕ: Обработка ошибок FileSystemWatcher
        private void OnWatcherError(object sender, ErrorEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                ShowNotification("Ошибка мониторинга. Перезапуск...", true);

                // Перезапускаем watcher
                if (desktopWatcher != null)
                {
                    desktopWatcher.EnableRaisingEvents = false;
                    System.Threading.Thread.Sleep(500);
                    desktopWatcher.EnableRaisingEvents = ChkMonitoring?.IsChecked == true;
                }
            });
        }

        private void OnDesktopFileCreated(object sender, FileSystemEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            try
            {
                // Добавляем событие в очередь вместо немедленной обработки
                Dispatcher.Invoke(() =>
                {
                    pendingFileEvents.Enqueue(e);
                    fileProcessingTimer?.Stop();
                    fileProcessingTimer?.Start();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в OnDesktopFileCreated: {ex.Message}");
            }
        }

        private void OnDesktopFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var fileToRemove = files.FirstOrDefault(f => f.Path == e.FullPath);
                        if (fileToRemove != null)
                        {
                            files.Remove(fileToRemove);
                            SaveData();
                            UpdateStats();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка удаления файла: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в OnDesktopFileDeleted: {ex.Message}");
            }
        }

        private void OnDesktopFileRenamed(object sender, RenamedEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            try
            {
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        var fileToUpdate = files.FirstOrDefault(f => f.Path == e.OldFullPath);
                        if (fileToUpdate != null)
                        {
                            fileToUpdate.Path = e.FullPath;
                            fileToUpdate.Name = Path.GetFileName(e.FullPath);
                            SaveData();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка переименования файла: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка в OnDesktopFileRenamed: {ex.Message}");
            }
        }

        private void ChkMonitoring_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (desktopWatcher != null)
                {
                    desktopWatcher.EnableRaisingEvents = ChkMonitoring.IsChecked == true;
                    ShowNotification(ChkMonitoring.IsChecked == true
                        ? "✓ Мониторинг включен"
                        : "Мониторинг отключен");
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка мониторинга: {ex.Message}", true);
            }
        }

        private void ChkAutoSort_Changed(object sender, RoutedEventArgs e)
        {
            ShowNotification(ChkAutoSort.IsChecked == true
                ? "✓ Автосортировка включена"
                : "Автосортировка отключена");
        }

        // === Сканирование и перемещение файлов ===
        private void BtnScanDesktop_Click(object sender, RoutedEventArgs e)
        {
            ScanDesktop();
            ShowNotification($"✓ Найдено файлов: {files.Count}");
        }

        private void ScanDesktop()
        {
            try
            {
                files.Clear();
                var desktopFiles = Directory.GetFiles(desktopPath);

                foreach (var filePath in desktopFiles)
                {
                    try
                    {
                        var fileInfo = new FileInfo(filePath);
                        if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                            (fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                        {
                            continue;
                        }
                        var fileItem = CreateFileItem(fileInfo);
                        files.Add(fileItem);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка обработки файла {filePath}: {ex.Message}");
                    }
                }
                SaveData();
                UpdateStats();
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка сканирования: {ex.Message}", true);
            }
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Multiselect = true };
            if (dlg.ShowDialog() == true)
            {
                foreach (var file in dlg.FileNames)
                {
                    var dest = Path.Combine(desktopPath, Path.GetFileName(file));
                    if (!File.Exists(dest))
                    {
                        try
                        {
                            File.Copy(file, dest);
                        }
                        catch (Exception ex)
                        {
                            ShowNotification($"Ошибка добавления: {ex.Message}", true);
                        }
                    }
                }
            }
        }

        private void BtnAutoSort_Click(object sender, RoutedEventArgs e)
        {
            AutoSort();
        }

        private void AutoSort()
        {
            var sorted = files.OrderBy(f => f.Category).ThenBy(f => f.Name).ToList();
            sortingHistory.Add(files.ToList());
            files.Clear();
            foreach (var f in sorted) files.Add(f);
            ShowNotification("✓ Файлы отсортированы виртуально");
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (sortingHistory.Count > 0)
            {
                var prev = sortingHistory.Last();
                sortingHistory.RemoveAt(sortingHistory.Count - 1);
                files.Clear();
                foreach (var f in prev) files.Add(f);
                ShowNotification("✓ Отмена сортировки");
            }
            else
            {
                ShowNotification("Нет действий для отмены", true);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (files.Count == 0) return;
            if (MessageBox.Show("Удалить все файлы с рабочего стола?", "Очистка", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                foreach (var f in files.ToList())
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(f.Path) && f.Path.StartsWith(desktopPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(f.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Ошибка удаления {f.Name}: {ex.Message}");
                    }
                }
                files.Clear();
                SaveData();
                UpdateStats();
                ShowNotification("✓ Рабочий стол очищен");
            }
        }

        private void BtnDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var file = btn?.Tag as FileItem;
            if (file != null && MessageBox.Show($"Удалить {file.Name ?? string.Empty}?", "Удаление", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(file.Path ?? string.Empty);
                    files.Remove(file);
                    SaveData();
                    UpdateStats();
                    ShowNotification($"✓ Удален: {file.Name ?? string.Empty}");
                }
                catch (Exception ex)
                {
                    ShowNotification($"Ошибка: {ex.Message}", true);
                }
            }
        }

        private void BtnSelectDirectory_Click(object sender, RoutedEventArgs e)
        {
            using (var dialog = new WinForms.FolderBrowserDialog())
            {
                dialog.SelectedPath = sortDirectory;
                dialog.Description = "Выберите папку для создания категорий";
                if (dialog.ShowDialog() == WinForms.DialogResult.OK)
                {
                    sortDirectory = dialog.SelectedPath;
                    SortDirectoryTextBox.Text = sortDirectory;
                    ShowNotification($"✓ Выбрана папка: {Path.GetFileName(sortDirectory)}");
                }
            }
        }

        private void BtnMoveToFolders_Click(object sender, RoutedEventArgs e)
        {
            if (files.Count == 0)
            {
                ShowNotification("Нет файлов для перемещения", true);
                return;
            }
            var result = MessageBox.Show(
                $"Переместить {files.Count} файлов в папки-категории?\n\nПапки будут созданы в: {sortDirectory}",
                "Подтверждение перемещения",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );
            if (result == MessageBoxResult.Yes)
            {
                MoveFilesToCategoryFolders();
            }
        }

        private void MoveFilesToCategoryFolders()
        {
            try
            {
                int movedCount = 0;
                var categories = files.Where(f => !string.IsNullOrEmpty(f.Category)).GroupBy(f => f.Category);

                foreach (var category in categories)
                {
                    var categoryName = category.Key ?? "Без категории";
                    var categoryFolder = Path.Combine(sortDirectory, categoryName);
                    Directory.CreateDirectory(categoryFolder);

                    foreach (var file in category.ToList())
                    {
                        try
                        {
                            if (File.Exists(file.Path))
                            {
                                var newPath = Path.Combine(categoryFolder, file.Name ?? string.Empty);

                                if (File.Exists(newPath))
                                {
                                    var nameWithoutExt = Path.GetFileNameWithoutExtension(file.Name ?? string.Empty);
                                    var extension = Path.GetExtension(file.Name ?? string.Empty);
                                    int counter = 1;

                                    while (File.Exists(newPath))
                                    {
                                        newPath = Path.Combine(categoryFolder, $"{nameWithoutExt} ({counter}){extension}");
                                        counter++;
                                    }
                                }

                                File.Move(file.Path ?? string.Empty, newPath);

                                Dispatcher.Invoke(() =>
                                {
                                    file.Path = newPath;
                                    file.Category = categoryName;
                                });

                                movedCount++;
                            }
                            else
                            {
                                Dispatcher.Invoke(() => files.Remove(file));
                            }
                        }
                        catch (Exception ex)
                        {
                            ShowNotification($"Ошибка перемещения {file.Name ?? string.Empty}: {ex.Message}", true);
                        }
                    }
                }
                SaveData();
                ShowNotification($"✓ Перемещено файлов: {movedCount}");

                var openResult = MessageBox.Show(
                    "Открыть папку с отсортированными файлами?",
                    "Перемещение завершено",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information
                );
                if (openResult == MessageBoxResult.Yes)
                {
                    System.Diagnostics.Process.Start("explorer.exe", sortDirectory);
                }
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка перемещения: {ex.Message}", true);
            }
        }

        private FileItem CreateFileItem(FileInfo fileInfo)
        {
            var extension = fileInfo.Extension.ToLower();
            var category = GetFileCategory(extension);
            return new FileItem
            {
                Id = Guid.NewGuid(),
                Name = fileInfo.Name,
                Path = fileInfo.FullName,
                Size = FormatFileSize(fileInfo.Length),
                Category = category,
                Icon = GetFileIcon(extension),
                CategoryColor = GetCategoryColor(category)
            };
        }

        private string GetFileCategory(string ext)
        {
            if (new[] { ".doc", ".docx", ".pdf", ".txt", ".xlsx", ".pptx" }.Contains(ext)) return "Документы";
            if (new[] { ".jpg", ".png", ".gif", ".bmp", ".svg" }.Contains(ext)) return "Изображения";
            if (new[] { ".mp4", ".avi", ".mkv", ".mov" }.Contains(ext)) return "Видео";
            if (new[] { ".mp3", ".wav", ".flac", ".aac" }.Contains(ext)) return "Аудио";
            if (new[] { ".zip", ".rar", ".7z", ".tar" }.Contains(ext)) return "Архивы";
            if (new[] { ".exe", ".msi" }.Contains(ext)) return "Приложения";
            if (new[] { ".lnk", ".url" }.Contains(ext)) return "Ярлыки";
            return "Другие";
        }

        private string GetFileIcon(string ext)
        {
            var category = GetFileCategory(ext);
            switch (category)
            {
                case "Документы": return "📄";
                case "Изображения": return "🖼️";
                case "Видео": return "🎥";
                case "Аудио": return "🎵";
                case "Архивы": return "📦";
                case "Приложения": return "⚙️";
                case "Ярлыки": return "🔗";
                default: return "📁";
            }
        }

        private SolidColorBrush GetCategoryColor(string category)
        {
            switch (category)
            {
                case "Документы": return new SolidColorBrush(Color.FromRgb(59, 130, 246));
                case "Изображения": return new SolidColorBrush(Color.FromRgb(16, 185, 129));
                case "Видео": return new SolidColorBrush(Color.FromRgb(239, 68, 68));
                case "Аудио": return new SolidColorBrush(Color.FromRgb(245, 158, 11));
                case "Архивы": return new SolidColorBrush(Color.FromRgb(139, 92, 246));
                case "Приложения": return new SolidColorBrush(Color.FromRgb(34, 197, 94));
                case "Ярлыки": return new SolidColorBrush(Color.FromRgb(251, 191, 36));
                default: return new SolidColorBrush(Color.FromRgb(100, 116, 139));
            }
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024:F2} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024 * 1024):F2} MB";
            return $"{bytes / (1024 * 1024 * 1024):F2} GB";
        }

        // === Профили ===
        private void BtnCreateProfile_Click(object sender, RoutedEventArgs e)
        {
            var input = Microsoft.VisualBasic.Interaction.InputBox("Имя профиля:", "Создать профиль", "Профиль 1");
            if (!string.IsNullOrEmpty(input))
            {
                var profile = new Profile
                {
                    Name = input,
                    Description = "Описание...",
                    Files = files.ToList()
                };
                profiles.Add(profile);
                SaveData();
                UpdateStats();
                ShowNotification($"✓ Создан профиль: {input}");
            }
        }

        private void BtnApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var profile = btn?.Tag as Profile;
            if (profile != null)
            {
                files.Clear();
                foreach (var f in profile.Files ?? new List<FileItem>())
                {
                    f.Icon = GetFileIcon(Path.GetExtension(f.Path ?? string.Empty));
                    f.CategoryColor = GetCategoryColor(f.Category ?? string.Empty);
                    files.Add(f);
                }
                SaveData();
                UpdateStats();
                ShowNotification($"✓ Применен профиль: {profile.Name ?? string.Empty}");
            }
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var btn = sender as Button;
            var profile = btn?.Tag as Profile;
            if (profile != null)
            {
                profiles.Remove(profile);
                SaveData();
                UpdateStats();
            }
        }

        // === Резервное копирование ===
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "JSON files (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var data = new AppData
                    {
                        Files = files.ToList(),
                        Profiles = profiles.ToList(),
                        Tasks = tasks.ToList()
                    };
                    File.WriteAllText(dlg.FileName, JsonConvert.SerializeObject(data, Formatting.Indented));
                    ShowNotification("✓ Данные экспортированы");
                }
                catch (Exception ex)
                {
                    ShowNotification($"Ошибка экспорта: {ex.Message}", true);
                }
            }
        }

        private void BtnImport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Filter = "JSON files (*.json)|*.json" };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dlg.FileName);
                    var data = JsonConvert.DeserializeObject<AppData>(json);
                    files.Clear();
                    foreach (var f in data?.Files ?? new List<FileItem>())
                    {
                        f.Icon = GetFileIcon(Path.GetExtension(f.Path ?? string.Empty));
                        f.CategoryColor = GetCategoryColor(f.Category ?? string.Empty);
                        files.Add(f);
                    }
                    profiles.Clear();
                    foreach (var p in data?.Profiles ?? new List<Profile>()) profiles.Add(p);
                    tasks.Clear();
                    foreach (var t in data?.Tasks ?? new List<TaskItem>()) tasks.Add(t);
                    SaveData();
                    UpdateStats();
                    ShowNotification("✓ Данные импортированы");
                }
                catch (Exception ex)
                {
                    ShowNotification($"Ошибка импорта: {ex.Message}", true);
                }
            }
        }

        // === Работа с данными ===
        private void LoadData()
        {
            try
            {
                if (File.Exists(dataFilePath))
                {
                    var json = File.ReadAllText(dataFilePath);
                    var data = JsonConvert.DeserializeObject<AppData>(json);
                    if (data != null)
                    {
                        var validFiles = data.Files?.Where(f => File.Exists(f.Path ?? string.Empty)).ToList() ?? new List<FileItem>();
                        foreach (var f in validFiles)
                        {
                            f.Icon = GetFileIcon(Path.GetExtension(f.Path ?? string.Empty));
                            f.CategoryColor = GetCategoryColor(f.Category ?? string.Empty);
                        }
                        files = new ObservableCollection<FileItem>(validFiles);
                        profiles = new ObservableCollection<Profile>(data.Profiles ?? new List<Profile>());
                        tasks = new ObservableCollection<TaskItem>(data.Tasks ?? new List<TaskItem>());

                        FilesListBox.ItemsSource = files;
                        ProfilesListBox.ItemsSource = profiles;
                        TasksListBox.ItemsSource = tasks;
                    }
                }
                UpdateStats();
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка загрузки: {ex.Message}", true);
            }
        }

        private void SaveData()
        {
            try
            {
                var data = new AppData
                {
                    Files = files.ToList(),
                    Profiles = profiles.ToList(),
                    Tasks = tasks.ToList()
                };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(dataFilePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка сохранения: {ex.Message}");
            }
        }

        private void UpdateStats()
        {
            FileCountText.Text = $"Файлов: {files.Count}";
            ProfileCountText.Text = $"Профилей: {profiles.Count}";
            FilesStatsText.Text = $"Файлов в системе: {files.Count}";
            ProfilesStatsText.Text = $"Профилей: {profiles.Count}";
            TasksStatsText.Text = $"Задач: {tasks.Count}";
            UpdateTasksStats();
        }

        private void ShowNotification(string text, bool error = false)
        {
            NotificationText.Text = text;
            NotificationToast.BorderBrush = error ? new SolidColorBrush(Color.FromRgb(239, 68, 68)) : new SolidColorBrush(Color.FromRgb(16, 185, 129));
            NotificationToast.Visibility = Visibility.Visible;
            notificationTimer?.Stop();
            notificationTimer?.Start();
        }

        // === Навигация ===
        private void BtnDesktop_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(DesktopPanel);
        }

        private void BtnWidgets_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(WidgetsPanel);
        }

        private void BtnNotesTasks_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(NotesTasksPanel);
        }

        private void BtnProfiles_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(ProfilesPanel);
        }

        private void BtnSort_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(SortPanel);
        }

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(SettingsPanel);
        }

        private void ShowPanel(Grid panel)
        {
            DesktopPanel.Visibility = Visibility.Collapsed;
            WidgetsPanel.Visibility = Visibility.Collapsed;
            NotesTasksPanel.Visibility = Visibility.Collapsed;
            ProfilesPanel.Visibility = Visibility.Collapsed;
            SortPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;

            panel.Visibility = Visibility.Visible;
        }

        // Показ виджетов на рабочем столе
        private void ShowClockOnDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (clockWidget == null || clockWidget.IsLoaded == false)
            {
                clockWidget = new ClockWidget();
                clockWidget.ClockFormat.SelectedIndex = ClockFormat.SelectedIndex;
                clockWidget.Show();
            }
            else
            {
                clockWidget.Close();
                clockWidget = null;
            }
        }

        private void ShowCalendarOnDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (calendarWidget == null || calendarWidget.IsLoaded == false)
            {
                calendarWidget = new CalendarWidget();
                calendarWidget.Show();
            }
            else
            {
                calendarWidget.Close();
                calendarWidget = null;
            }
        }

        private void ShowNotesOnDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (notesWidget == null || notesWidget.IsLoaded == false)
            {
                notesWidget = new NotesWidget();
                notesWidget.NotesTextBox.Text = NotesTextBox.Text;
                notesWidget.NotesTextBox.TextChanged += (s, args) => NotesTextBox.Text = notesWidget.NotesTextBox.Text;
                notesWidget.Show();
            }
            else
            {
                notesWidget.Close();
                notesWidget = null;
            }
        }

        private void ShowTasksOnDesktop_Click(object sender, RoutedEventArgs e)
        {
            if (tasksWidget == null || tasksWidget.IsLoaded == false)
            {
                tasksWidget = new TasksWidget(tasks, () =>
                {
                    SaveData();
                    UpdateStats();
                });
                tasksWidget.Show();
            }
            else
            {
                tasksWidget.Close();
                tasksWidget = null;
            }
        }

        // НОВОЕ: Корректное закрытие приложения
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            base.OnClosing(e);

            try
            {
                // Останавливаем все таймеры
                clockTimer?.Stop();
                notesTimer?.Stop();
                notificationTimer?.Stop();
                fileProcessingTimer?.Stop();

                // Останавливаем FileSystemWatcher
                if (desktopWatcher != null)
                {
                    desktopWatcher.EnableRaisingEvents = false;
                    desktopWatcher.Dispose();
                }

                // Закрываем виджеты
                clockWidget?.Close();
                calendarWidget?.Close();
                notesWidget?.Close();
                tasksWidget?.Close();

                // Сохраняем данные
                SaveData();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка при закрытии: {ex.Message}");
            }
        }
    }
}