using System.Windows;
using System.Windows.Input;
using Microsoft.VisualStudio.PlatformUI;

namespace VSIXExtention
{
    public partial class HandlerSelectionDialog : DialogWindow
    {
        public string SelectedHandler { get; private set; }

        public HandlerSelectionDialog(string message, string[] handlerNames)
        {
            InitializeComponent();
            InitializeDialog(message, handlerNames);
        }

        private void InitializeDialog(string message, string[] handlerNames)
        {
            // Set the message text
            MessageLabel.Text = message;
            
            // Populate the list box
            if (handlerNames?.Length > 0)
            {
                foreach (var handler in handlerNames)
                {
                    HandlerListBox.Items.Add(handler);
                }
                HandlerListBox.SelectedIndex = 0;
            }
            
            // Set focus to the list box after the window loads
            Loaded += (sender, e) =>
            {
                HandlerListBox?.Focus();
            };
        }

        // Event handlers for XAML controls
        private void HandlerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (HandlerListBox.SelectedItem != null)
            {
                AcceptSelection();
            }
        }

        private void HandlerListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && HandlerListBox.SelectedItem != null)
            {
                AcceptSelection();
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            AcceptSelection();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void AcceptSelection()
        {
            if (HandlerListBox?.SelectedItem != null)
            {
                SelectedHandler = HandlerListBox.SelectedItem.ToString();
                DialogResult = true;
            }
        }
    }
} 