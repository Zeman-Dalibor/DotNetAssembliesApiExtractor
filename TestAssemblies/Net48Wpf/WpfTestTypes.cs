using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TestAssemblies
{
    public class SampleWindow : Window
    {
        private readonly Button _button;

        public SampleWindow()
        {
            Title = "Sample WPF Window";
            Width = 400;
            Height = 300;

            _button = new Button
            {
                Content = "Click Me",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brushes.LightBlue
            };
            _button.Click += OnButtonClick;

            Content = _button;
        }

        private void OnButtonClick(object sender, RoutedEventArgs e)
        {
            _button.Content = "Clicked!";
        }
    }

    public class SampleUserControl : UserControl
    {
        public string DisplayText { get; set; }

        public SampleUserControl()
        {
            var textBlock = new TextBlock
            {
                Text = "Hello from UserControl",
                FontSize = 16,
                Foreground = Brushes.DarkBlue
            };
            Content = textBlock;
        }

        public void UpdateText(string text)
        {
            DisplayText = text;
            if (Content is TextBlock tb)
            {
                tb.Text = text;
            }
        }
    }

    public interface IWpfService
    {
        void Initialize(Window owner);
        void ShowDialog(string message);
    }

    public class WpfService : IWpfService
    {
        private Window _owner;

        public void Initialize(Window owner)
        {
            _owner = owner;
        }

        public void ShowDialog(string message)
        {
            MessageBox.Show(_owner, message, "Info", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public struct WpfLayoutInfo
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public HorizontalAlignment HorizontalAlignment { get; set; }
        public VerticalAlignment VerticalAlignment { get; set; }
    }

    public enum SampleTheme
    {
        Light,
        Dark,
        HighContrast
    }
}
