using System;
using System.ComponentModel;
using System.Windows;

namespace WpfProxyServer
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private ProxyServer _proxyServer;
        private bool _canStart = true;
        private bool _canStop = false;

        public event PropertyChangedEventHandler PropertyChanged;

        public bool CanStart
        {
            get => _canStart;
            set
            {
                _canStart = value;
                OnPropertyChanged(nameof(CanStart));
            }
        }

        public bool CanStop
        {
            get => _canStop;
            set
            {
                _canStop = value;
                OnPropertyChanged(nameof(CanStop));
            }
        }



        private int port;
        private string prefix;
        private Uri? targetUri;
        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;

            if (!int.TryParse(PortTextBox.Text, out int port))
                return;
            if (!Uri.TryCreate(TargetUrlTextBox.Text, UriKind.Absolute, out targetUri))
                return;

            prefix = $"http://localhost:{port}/";
            
            _proxyServer = new ProxyServer(prefix,targetUri.ToString());
            _proxyServer.LogMessage += LogMessage; // Hook logging event
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            CanStart = false;
            CanStop = true;

            try
            {
                await _proxyServer.StartAsync();
                LogMessage($"Proxy server started on {prefix}, forwarding to {targetUri}");
            }
            catch (Exception ex)
            {
                LogMessage($"Error starting proxy server: {ex.Message}");
                CanStart = true;
                CanStop = false;
            }

        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _proxyServer.Stop();
                LogMessage("Proxy server stopped.");
            }
            catch (Exception ex)
            {
                LogMessage($"Error stopping proxy server: {ex.Message}");
            }
            finally
            {
                CanStart = true;
                CanStop = false;
            }
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogTextBox.AppendText($"{DateTime.Now:HH:mm:ss} - {message}\n");
                LogTextBox.ScrollToEnd();
            });
        }

        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
