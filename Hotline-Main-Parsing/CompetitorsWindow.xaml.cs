using Hotline_Main_Parsing.common;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Hotline_Main_Parsing
{
    public partial class CompetitorsWindow : Window
    {
        public CompetitorsWindow()
        {
            InitializeComponent();
            LoadData();
        }

        private void Reload_Click(object sender, RoutedEventArgs e)
        {
            LoadData();
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(CompetitorHistoryStore.DirectoryPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = CompetitorHistoryStore.DirectoryPath,
                UseShellExecute = true
            });
        }

        private void LoadData()
        {
            var data = CompetitorHistoryStore.ReadLatest();
            CompetitorsGrid.ItemsSource = data;

            int dumping = data.Count(item => item.IsDumping);
            int ownHigher = data.Count(item => item.OwnIsHigherThanMarket);
            int canRaise = data.Count(item => item.CanRaisePrice);
            SummaryText.Text = $"Товаров: {data.Count} | Демпинг: {dumping} | Ты выше рынка: {ownHigher} | Можно поднять цену: {canRaise}";
        }
    }
}
