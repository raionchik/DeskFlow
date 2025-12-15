using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DeskFlow
{
    public partial class NotesWidget : Window
    {
        public NotesWidget()
        {
            InitializeComponent();
        }

        private void NotesTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Здесь можно добавить логику сохранения или синхронизации с main, но поскольку main имеет автосохранение, можно оставить
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                this.DragMove();
            }
        }
    }
}