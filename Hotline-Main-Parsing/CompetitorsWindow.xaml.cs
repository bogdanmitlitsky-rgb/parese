using Hotline_Main_Parsing.common;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Navigation;

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

        private async void SendExcelToTelegram_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                var token = config["Bot_Token"];
                var ids = config.GetSection("Ids")
                    .AsEnumerable()
                    .Select(p => p.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!)
                    .ToArray();

                if (string.IsNullOrWhiteSpace(token) || ids.Length == 0)
                {
                    MessageBox.Show("Укажите Telegram-бота и группу в настройках.", "Telegram", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string reportPath = CompetitorHistoryStore.ExportLatestToExcel();
                await SendTelegramDocumentAsync(token, ids, reportPath);
                MessageBox.Show("Excel-отчет отправлен в Telegram.", "Telegram", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось отправить Excel-отчет: {ex.Message}", "Telegram", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static async Task SendTelegramDocumentAsync(string token, IEnumerable<string> ids, string filePath)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Hotline Parser");

            foreach (var id in ids)
            {
                await using var fileStream = File.OpenRead(filePath);
                using var form = new MultipartFormDataContent();
                using var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

                form.Add(new StringContent(id), "chat_id");
                form.Add(new StringContent($"Отчет конкурентов Hotline {DateTime.Now:dd.MM.yyyy HH:mm}"), "caption");
                form.Add(fileContent, "document", Path.GetFileName(filePath));

                var response = await httpClient.PostAsync($"https://api.telegram.org/bot{token}/sendDocument", form);
                response.EnsureSuccessStatusCode();
            }
        }

        private void HotlineLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(e.Uri?.AbsoluteUri))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                });
            }

            e.Handled = true;
        }

        private void LoadData()
        {
            var data = CompetitorHistoryStore.ReadLatest();
            CompetitorsGrid.ItemsSource = data;

            int dumping = data.Count(item => item.IsDumping);
            int ownHigher = data.Count(item => item.OwnIsHigherThanMarket);
            int canRaise = data.Count(item => item.CanRaisePrice);
            int softDrops = data.Count(item => item.SoftPriceDropApplied);
            SummaryText.Text = $"Товаров: {data.Count} | Авто-снижений: {softDrops} | Демпинг: {dumping} | Ты выше рынка: {ownHigher} | Можно поднять цену: {canRaise}";
        }
    }
}
