using System.Windows;
using System.Windows.Input;

namespace DeskFlow
{
    public partial class CalendarWidget : Window
    {
        public CalendarWidget()
        {
            InitializeComponent();
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