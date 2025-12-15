using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;

namespace DeskFlow
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<FileItem> files = new ObservableCollection<FileItem>();
        private ObservableCollection<Profile> profiles = new ObservableCollection<Profile>();
        private List<List<FileItem>> sortingHistory = new List<List<FileItem>>();
        private string dataFilePath;
        private string desktopPath;
        private FileSystemWatcher desktopWatcher;

        public MainWindow()
        {
            InitializeComponent();

            desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            dataFilePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskFlow",
                "data.json"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(dataFilePath));

            FilesListBox.ItemsSource = files;
            ProfilesListBox.ItemsSource = profiles;

            LoadData();
            UpdateStats();

            // Инициализация мониторинга рабочего стола
            InitializeDesktopWatcher();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Автоматическое сканирование рабочего стола при запуске
            ScanDesktop();
            ShowNotification($"Автоматически загружено {files.Count} файлов с рабочего стола");
        }

        // === Мониторинг рабочего стола ===
        private void InitializeDesktopWatcher()
        {
            desktopWatcher = new FileSystemWatcher
            {
                Path = desktopPath,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                Filter = "*.*",
                EnableRaisingEvents = true
            };

            desktopWatcher.Created += OnDesktopFileCreated;
            desktopWatcher.Deleted += OnDesktopFileDeleted;
            desktopWatcher.Renamed += OnDesktopFileRenamed;
        }

        private void OnDesktopFileCreated(object sender, FileSystemEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            Dispatcher.Invoke(() =>
            {
                try
                {
                    var fileInfo = new FileInfo(e.FullPath);
                    if (fileInfo.Exists && !files.Any(f => f.Path == e.FullPath))
                    {
                        var fileItem = CreateFileItem(fileInfo);
                        files.Add(fileItem);
                        SaveData();
                        UpdateStats();
                        ShowNotification($"➕ Обнаружен новый файл: {fileInfo.Name}");

                        if (ChkAutoSort?.IsChecked == true)
                        {
                            AutoSort();
                        }
                    }
                }
                catch { }
            });
        }

        private void OnDesktopFileDeleted(object sender, FileSystemEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            Dispatcher.Invoke(() =>
            {
                var fileToRemove = files.FirstOrDefault(f => f.Path == e.FullPath);
                if (fileToRemove != null)
                {
                    files.Remove(fileToRemove);
                    SaveData();
                    UpdateStats();
                    ShowNotification($"➖ Файл удалён с рабочего стола: {fileToRemove.Name}");
                }
            });
        }

        private void OnDesktopFileRenamed(object sender, RenamedEventArgs e)
        {
            if (ChkMonitoring?.IsChecked != true) return;

            Dispatcher.Invoke(() =>
            {
                var fileToUpdate = files.FirstOrDefault(f => f.Path == e.OldFullPath);
                if (fileToUpdate != null)
                {
                    fileToUpdate.Path = e.FullPath;
                    fileToUpdate.Name = Path.GetFileName(e.FullPath);
                    SaveData();
                    ShowNotification($"📝 Файл переименован: {e.Name}");
                }
            });
        }

        private void ChkMonitoring_Changed(object sender, RoutedEventArgs e)
        {
            if (desktopWatcher != null)
            {
                desktopWatcher.EnableRaisingEvents = ChkMonitoring.IsChecked == true;
                ShowNotification(ChkMonitoring.IsChecked == true
                    ? "✓ Мониторинг рабочего стола включен"
                    : "Мониторинг рабочего стола отключен");
            }
        }

        private void ChkAutoSort_Changed(object sender, RoutedEventArgs e)
        {
            ShowNotification(ChkAutoSort.IsChecked == true
                ? "✓ Автосортировка включена"
                : "Автосортировка отключена");
        }

        // === Сканирование рабочего стола ===
        private void BtnScanDesktop_Click(object sender, RoutedEventArgs e)
        {
            ScanDesktop();
            ShowNotification($"✓ Рабочий стол просканирован. Найдено файлов: {files.Count}");
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
                        // Пропускаем скрытые и системные файлы
                        if ((fileInfo.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden ||
                            (fileInfo.Attributes & FileAttributes.System) == FileAttributes.System)
                        {
                            continue;
                        }

                        var fileItem = CreateFileItem(fileInfo);
                        files.Add(fileItem);
                    }
                    catch { } // Игнорируем файлы, к которым нет доступа
                }

                SaveData();
                UpdateStats();
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка сканирования: {ex.Message}", true);
            }
        }

        private FileItem CreateFileItem(FileInfo fileInfo)
        {
            var category = GetFileCategory(fileInfo.Extension);
            return new FileItem
            {
                Id = Guid.NewGuid(),
                Name = fileInfo.Name,
                Path = fileInfo.FullName,
                Size = FormatFileSize(fileInfo.Length),
                Category = category,
                Icon = GetFileIcon(fileInfo.Extension),
                CategoryColor = GetCategoryColor(category)
            };
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
                        // Проверяем, существуют ли файлы
                        var validFiles = data.Files?.Where(f => File.Exists(f.Path)).ToList() ?? new List<FileItem>();
                        files = new ObservableCollection<FileItem>(validFiles);
                        profiles = new ObservableCollection<Profile>(data.Profiles ?? new List<Profile>());
                        FilesListBox.ItemsSource = files;
                        ProfilesListBox.ItemsSource = profiles;
                    }
                }
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
                    Profiles = profiles.ToList()
                };
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(dataFilePath, json);
            }
            catch (Exception ex)
            {
                ShowNotification($"Ошибка сохранения: {ex.Message}", true);
            }
        }

        // === Навигация по табам ===
        private void BtnDesktop_Click(object sender, RoutedEventArgs e)
        {
            ShowPanel(DesktopPanel);
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
            DesktopPathText.Text = $"Путь к рабочему столу: {desktopPath}";
        }

        private void ShowPanel(Grid panel)
        {
            DesktopPanel.Visibility = Visibility.Collapsed;
            ProfilesPanel.Visibility = Visibility.Collapsed;
            SortPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            panel.Visibility = Visibility.Visible;
        }

        // === Работа с файлами ===
        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Multiselect = true,
                Title = "Выберите файлы"
            };

            if (dialog.ShowDialog() == true)
            {
                foreach (var filePath in dialog.FileNames)
                {
                    // Проверяем, не добавлен ли уже этот файл
                    if (files.Any(f => f.Path == filePath))
                        continue;

                    var fileInfo = new FileInfo(filePath);
                    var fileItem = CreateFileItem(fileInfo);
                    files.Add(fileItem);
                }

                SaveData();
                UpdateStats();
                ShowNotification($"Добавлено файлов: {dialog.FileNames.Length}");

                if (ChkAutoSort.IsChecked == true)
                {
                    AutoSort();
                }
            }
        }

        private void BtnDeleteFile_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var fileItem = button?.Tag as FileItem;
            if (fileItem != null)
            {
                files.Remove(fileItem);
                SaveData();
                UpdateStats();
                ShowNotification("Файл удалён из списка");
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Очистить весь список файлов?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question
            );

            if (result == MessageBoxResult.Yes)
            {
                files.Clear();
                SaveData();
                UpdateStats();
                ShowNotification("✓ Список очищен");
            }
        }

        private void BtnAutoSort_Click(object sender, RoutedEventArgs e)
        {
            AutoSort();
        }

        private void AutoSort()
        {
            if (files.Count == 0)
            {
                ShowNotification("Нет файлов для сортировки", true);
                return;
            }

            // Сохраняем текущее состояние в историю
            sortingHistory.Add(files.ToList());
            if (sortingHistory.Count > 50)
                sortingHistory.RemoveAt(0);

            // Сортируем файлы: сначала по категории, потом по имени
            var sortedFiles = files.OrderBy(f => GetCategorySortOrder(f.Category))
                                   .ThenBy(f => f.Name)
                                   .ToList();

            // Очищаем коллекцию и добавляем отсортированные файлы
            files.Clear();
            foreach (var file in sortedFiles)
            {
                files.Add(file);
            }

            SaveData();
            ShowNotification("✓ Файлы отсортированы по категориям");
        }

        private int GetCategorySortOrder(string category)
        {
            return category switch
            {
                "Документы" => 1,
                "Изображения" => 2,
                "Видео" => 3,
                "Аудио" => 4,
                "Архивы" => 5,
                "Исполняемые" => 6,
                _ => 7
            };
        }

        private void BtnUndo_Click(object sender, RoutedEventArgs e)
        {
            if (sortingHistory.Count > 0)
            {
                var previous = sortingHistory.Last();
                sortingHistory.RemoveAt(sortingHistory.Count - 1);

                files.Clear();
                foreach (var item in previous)
                {
                    files.Add(item);
                }

                SaveData();
                ShowNotification("↺ Сортировка отменена");
            }
            else
            {
                ShowNotification("Нечего отменять", true);
            }
        }

        // === Работа с профилями ===
        private void BtnCreateProfile_Click(object sender, RoutedEventArgs e)
        {
            var profile = new Profile
            {
                Id = Guid.NewGuid(),
                Name = $"Профиль {profiles.Count + 1}",
                Description = "Новый профиль",
                FilesSnapshot = files.ToList(),
                CreatedAt = DateTime.Now,
                FilesCount = $"Файлов: {files.Count}"
            };

            profiles.Add(profile);
            SaveData();
            UpdateStats();
            ShowNotification("✓ Профиль создан");
        }

        private void BtnApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var profile = button?.Tag as Profile;
            if (profile != null)
            {
                files.Clear();
                foreach (var item in profile.FilesSnapshot)
                {
                    // Проверяем, существует ли файл
                    if (File.Exists(item.Path))
                    {
                        files.Add(item);
                    }
                }
                SaveData();
                UpdateStats();
                ShowNotification($"✓ Профиль \"{profile.Name}\" применён");
            }
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var profile = button?.Tag as Profile;
            if (profile != null)
            {
                var result = MessageBox.Show(
                    $"Удалить профиль \"{profile.Name}\"?",
                    "Подтверждение",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question
                );

                if (result == MessageBoxResult.Yes)
                {
                    profiles.Remove(profile);
                    SaveData();
                    UpdateStats();
                    ShowNotification("✓ Профиль удалён");
                }
            }
        }

        // === Экспорт/Импорт ===
        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "JSON файлы (*.json)|*.json",
                FileName = $"DeskFlow_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var data = new AppData
                    {
                        Files = files.ToList(),
                        Profiles = profiles.ToList(),
                        ExportedAt = DateTime.Now
                    };
                    var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                    File.WriteAllText(dialog.FileName, json);
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
            var dialog = new OpenFileDialog
            {
                Filter = "JSON файлы (*.json)|*.json"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var json = File.ReadAllText(dialog.FileName);
                    var data = JsonConvert.DeserializeObject<AppData>(json);

                    if (data != null)
                    {
                        files = new ObservableCollection<FileItem>(data.Files ?? new List<FileItem>());
                        profiles = new ObservableCollection<Profile>(data.Profiles ?? new List<Profile>());
                        FilesListBox.ItemsSource = files;
                        ProfilesListBox.ItemsSource = profiles;
                        SaveData();
                        UpdateStats();
                        ShowNotification("✓ Данные импортированы");
                    }
                }
                catch (Exception ex)
                {
                    ShowNotification($"Ошибка импорта: {ex.Message}", true);
                }
            }
        }

        // === Вспомогательные методы ===
        private string GetFileCategory(string extension)
        {
            extension = extension.ToLower();
            if (new[] { ".doc", ".docx", ".pdf", ".txt", ".xlsx", ".xls", ".pptx", ".ppt", ".odt", ".rtf" }.Contains(extension))
                return "Документы";
            if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".svg", ".webp", ".ico" }.Contains(extension))
                return "Изображения";
            if (new[] { ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm" }.Contains(extension))
                return "Видео";
            if (new[] { ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma", ".m4a" }.Contains(extension))
                return "Аудио";
            if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2" }.Contains(extension))
                return "Архивы";
            if (new[] { ".exe", ".msi", ".bat", ".cmd", ".sh" }.Contains(extension))
                return "Исполняемые";
            return "Прочие";
        }

        private string GetFileIcon(string extension)
        {
            var category = GetFileCategory(extension);
            return category switch
            {
                "Документы" => "📄",
                "Изображения" => "🖼️",
                "Видео" => "🎥",
                "Аудио" => "🎵",
                "Архивы" => "📦",
                "Исполняемые" => "⚙️",
                _ => "📁"
            };
        }

        private string GetCategoryColor(string category)
        {
            return category switch
            {
                "Документы" => "#3B82F6",
                "Изображения" => "#10B981",
                "Видео" => "#EF4444",
                "Аудио" => "#F59E0B",
                "Архивы" => "#8B5CF6",
                "Исполняемые" => "#EC4899",
                _ => "#6B7280"
            };
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private void UpdateStats()
        {
            FileCountText.Text = $"Файлов: {files.Count}";
            ProfileCountText.Text = $"Профилей: {profiles.Count}";
            FilesStatsText.Text = $"Файлов в системе: {files.Count}";
            ProfilesStatsText.Text = $"Профилей создано: {profiles.Count}";
        }

        private void ShowNotification(string message, bool isError = false)
        {
            if (ChkNotifications?.IsChecked != true && !isError) return;

            NotificationText.Text = message;
            NotificationToast.BorderBrush = new SolidColorBrush(
                isError ? Color.FromRgb(239, 68, 68) : Color.FromRgb(16, 185, 129)
            );
            NotificationToast.Visibility = Visibility.Visible;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (s, args) =>
            {
                NotificationToast.Visibility = Visibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }

        protected override void OnClosed(EventArgs e)
        {
            desktopWatcher?.Dispose();
            base.OnClosed(e);
        }
    }

    // === Модели данных ===
    public class FileItem
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public string Size { get; set; }
        public string Category { get; set; }
        public string Icon { get; set; }
        public string CategoryColor { get; set; }
    }

    public class Profile
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<FileItem> FilesSnapshot { get; set; }
        public DateTime CreatedAt { get; set; }
        public string FilesCount { get; set; }
    }

    public class AppData
    {
        public List<FileItem> Files { get; set; }
        public List<Profile> Profiles { get; set; }
        public DateTime ExportedAt { get; set; }
    }
}