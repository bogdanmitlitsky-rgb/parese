using System.Threading;
using System.Windows;
using Hotline_Main_Parsing.common;

namespace Hotline_Main_Parsing
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;
        private static bool _ownsSingleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, "Hotline_Main_Parsing_Single_Instance", out bool createdNew);
            _ownsSingleInstanceMutex = createdNew;

            if (!createdNew)
            {
                MessageBox.Show(
                    "Программа уже запущена.\n\nЗакройте текущее окно или дождитесь завершения парсинга.",
                    "Hotline Parser",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ManagerHotline.KillTrackedBrowserProcesses();

            if (_ownsSingleInstanceMutex)
            {
                _singleInstanceMutex?.ReleaseMutex();
            }

            _singleInstanceMutex?.Dispose();
            base.OnExit(e);
        }

        public static void ReleaseSingleInstanceLockForRestart()
        {
            if (!_ownsSingleInstanceMutex)
            {
                return;
            }

            _singleInstanceMutex?.ReleaseMutex();
            _ownsSingleInstanceMutex = false;
        }
    }
}
