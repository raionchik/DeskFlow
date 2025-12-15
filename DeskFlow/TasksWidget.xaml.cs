using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls; // Важно для WPF Button
using System.Windows.Input;

namespace DeskFlow
{
    public partial class TasksWidget : Window
    {
        private ObservableCollection<TaskItem> _tasks;
        private Action _onDataChanged;

        public TasksWidget(ObservableCollection<TaskItem> tasks, Action onDataChanged)
        {
            InitializeComponent();
            _tasks = tasks;
            _onDataChanged = onDataChanged;
            WidgetTasksListBox.ItemsSource = _tasks;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }

        private void TaskCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            _onDataChanged?.Invoke();
        }

        private void BtnDeleteTask_Click(object sender, RoutedEventArgs e)
        {
            // Явно указываем System.Windows.Controls.Button, чтобы избежать конфликта с WinForms
            var btn = sender as System.Windows.Controls.Button;
            var task = btn?.Tag as TaskItem;

            if (task != null)
            {
                // Явно указываем System.Windows.MessageBox
                if (System.Windows.MessageBox.Show("Удалить задачу?", "Удаление", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    _tasks.Remove(task);
                    _onDataChanged?.Invoke();
                }
            }
        }
    }
}