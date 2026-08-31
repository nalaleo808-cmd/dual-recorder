using System;
using System.Windows;
using System.Windows.Threading;

namespace DualRecorder
{
    public partial class App : Application
    {
        public App()
        {
            // never die silently: show the problem, keep whatever was recorded
            DispatcherUnhandledException += OnDispatcherException;
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                try
                {
                    MessageBox.Show("Unexpected error: " + (e.ExceptionObject as Exception)?.Message,
                        "Dual Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };
        }

        private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Unexpected error: " + e.Exception.Message +
                            "\n\nAny recording in progress has been written to disk and is playable.",
                            "Dual Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
