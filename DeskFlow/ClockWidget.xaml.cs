using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DeskFlow
{
    public partial class ClockWidget : Window
    {
        private DispatcherTimer? clockTimer;

        public ClockWidget()
        {
            InitializeComponent();
            InitializeClock();
        }

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
            bool is24Hour = ClockFormat.SelectedIndex == 0;

            ClockTime.Text = is24Hour
                ? now.ToString("HH:mm:ss")
                : now.ToString("hh:mm:ss tt");

            var culture = new CultureInfo("ru-RU");
            ClockDate.Text = now.ToString("dddd, d MMMM yyyy", culture);
        }

        private void ClockFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateClock();
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