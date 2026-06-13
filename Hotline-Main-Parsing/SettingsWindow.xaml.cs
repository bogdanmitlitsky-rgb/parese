using Hotline_Main_Parsing.validmodel;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace Hotline_Main_Parsing
{
    /// <summary>
    /// Логика взаимодействия для SettingsWindow.xaml
    /// </summary>
    public partial class SettingsWindow : UserControl
    {
        private const string DefaultTelegramDefaultStartMessage = "Основной парсинг начался";
        private const string DefaultTelegramDefaultStopMessage = "Основной парсинг остановлен пользователем.";
        private const string DefaultTelegramDefaultFinishMessage = "Основной парсинг закончился\nОбработано: {processed}\nЦен сменилось: {changed}\nОшибок: {errors}\nВремя: {elapsed}";
        private const string DefaultTelegramAksStartMessage = "Парсинг аксессуаров начался";
        private const string DefaultTelegramAksStopMessage = "Парсинг аксессуаров остановлен пользователем.";
        private const string DefaultTelegramAksFinishMessage = "Парсинг аксессуаров закончился\nОбработано: {processed}\nЦен сменилось: {changed}\nОшибок: {errors}\nВремя: {elapsed}";

        private readonly TimeValidateModel _validateModel;
        private IConfiguration config;

        public event EventHandler? CloseRequested;
        public MainWindow? MainWindowOwner { get; set; }

        public SettingsWindow()
        {
            InitializeComponent();
            _validateModel = new TimeValidateModel();
            DataContext = _validateModel;
            config = LoadConfig();
        }

        private void btnCloseSettings_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private static IConfiguration LoadConfig()
        {
            return new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
        }

        private void tbStartTime_Loaded(object sender, RoutedEventArgs e)
        {
            tbStartTime.Text = GetConfigValue("timeStart", "08:00:00");
        }

        private void tbFinishTime_Loaded(object sender, RoutedEventArgs e)
        {
            tbFinishTime.Text = GetConfigValue("timeStop", "21:00:00");
        }

        private void tbBrowserCount_Loaded(object sender, RoutedEventArgs e)
        {
            tbBrowserCount.Text = GetConfigValue("BrowserCount", GetConfigValue("ThreadCount", "1"));
        }

        private void cbBrowserEconomyMode_Loaded(object sender, RoutedEventArgs e)
        {
            cbBrowserEconomyMode.IsChecked = bool.TryParse(GetConfigValue("BrowserEconomyMode", "true"), out bool enabled) ? enabled : true;
        }

        private void cbAntiDumpingEnabled_Loaded(object sender, RoutedEventArgs e)
        {
            cbAntiDumpingEnabled.IsChecked = bool.TryParse(GetConfigValue("AntiDumpingEnabled", "true"), out bool enabled) ? enabled : true;
        }

        private void tbAntiDumpingPercent_Loaded(object sender, RoutedEventArgs e)
        {
            tbAntiDumpingPercent.Text = GetConfigValue("AntiDumpingPercent", "10");
        }

        private void tbAntiDumpingMinOffers_Loaded(object sender, RoutedEventArgs e)
        {
            tbAntiDumpingMinOffers.Text = GetConfigValue("AntiDumpingMinOffers", "3");
        }

        private void tbTelegramBotToken_Loaded(object sender, RoutedEventArgs e)
        {
            tbTelegramBotToken.Text = GetConfigValue("Bot_Token", "");
        }

        private void tbTelegramIds_Loaded(object sender, RoutedEventArgs e)
        {
            var ids = config.GetSection("Ids")
                .GetChildren()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();

            new TextRange(tbTelegramIds.Document.ContentStart, tbTelegramIds.Document.ContentEnd).Text =
                string.Join(Environment.NewLine, ids);
        }

        private void tbTelegramDefaultStartMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramDefaultStartMessage, GetConfigValue("TelegramDefaultStartMessage", DefaultTelegramDefaultStartMessage));
        }

        private void tbTelegramDefaultFinishMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramDefaultFinishMessage, GetConfigValue("TelegramDefaultFinishMessage", DefaultTelegramDefaultFinishMessage));
        }

        private void tbTelegramDefaultStopMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramDefaultStopMessage, GetConfigValue("TelegramDefaultStopMessage", DefaultTelegramDefaultStopMessage));
        }

        private void tbTelegramAksStartMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramAksStartMessage, GetConfigValue("TelegramAksStartMessage", DefaultTelegramAksStartMessage));
        }

        private void tbTelegramAksFinishMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramAksFinishMessage, GetConfigValue("TelegramAksFinishMessage", DefaultTelegramAksFinishMessage));
        }

        private void tbTelegramAksStopMessage_Loaded(object sender, RoutedEventArgs e)
        {
            SetRichTextBoxText(tbTelegramAksStopMessage, GetConfigValue("TelegramAksStopMessage", DefaultTelegramAksStopMessage));
        }

        private void cbMorningReportEnabled_Loaded(object sender, RoutedEventArgs e)
        {
            cbMorningReportEnabled.IsChecked = bool.TryParse(GetConfigValue("MorningReportEnabled", "true"), out bool enabled) ? enabled : true;
        }

        private void tbMorningReportTime_Loaded(object sender, RoutedEventArgs e)
        {
            tbMorningReportTime.Text = GetConfigValue("MorningReportTime", "09:00:00");
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(tbBrowserCount.Text, out int browserCount) || browserCount < 1)
            {
                MessageBox.Show("Кол-во браузеров должно быть положительным числом.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!TimeSpan.TryParse(tbStartTime.Text, out _) || !TimeSpan.TryParse(tbFinishTime.Text, out _))
            {
                MessageBox.Show("Время нужно указать в формате 08:00:00.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!decimal.TryParse(tbAntiDumpingPercent.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal antiDumpingPercent) || antiDumpingPercent <= 0)
            {
                MessageBox.Show("Порог демпинга должен быть положительным числом.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(tbAntiDumpingMinOffers.Text, out int antiDumpingMinOffers) || antiDumpingMinOffers < 2)
            {
                MessageBox.Show("Минимум конкурентов должен быть числом от 2.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SaveChangesValueInAppSettings("BrowserCount", browserCount, "int");
            SaveChangesValueInAppSettings("ThreadCount", browserCount, "int");
            SaveChangesValueInAppSettings("timeStop", tbFinishTime.Text.Trim(), "string");
            SaveChangesValueInAppSettings("timeStart", tbStartTime.Text.Trim(), "string");
            SaveChangesValueInAppSettings("BrowserEconomyMode", cbBrowserEconomyMode.IsChecked == true, "bool");
            SaveChangesValueInAppSettings("AntiDumpingEnabled", cbAntiDumpingEnabled.IsChecked == true, "bool");
            SaveChangesValueInAppSettings("AntiDumpingPercent", antiDumpingPercent, "decimal");
            SaveChangesValueInAppSettings("AntiDumpingMinOffers", antiDumpingMinOffers, "int");

            PromptRestartAfterSave();
        }

        private void SaveChangesValueInAppSettings(string key, object value, string type)
        {
            string json = File.ReadAllText("appsettings.json");
            var jsonObj = JObject.Parse(json);

            switch (type)
            {
                case "int":
                    jsonObj[key] = Convert.ToInt32(value.ToString());
                    break;
                case "string":
                    jsonObj[key] = value.ToString();
                    break;
                case "bool":
                    jsonObj[key] = (bool)value;
                    break;
                case "decimal":
                    jsonObj[key] = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    break;
            }

            if (key == "GoogleTableTo")
            {
                jsonObj.Remove("GoolgeTableTo");
            }

            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            File.WriteAllText("appsettings.json", output, Encoding.UTF8);
            config = LoadConfig();
        }

        private void btnSaveProxy_Click(object sender, RoutedEventArgs e)
        {
            TextRange range = new TextRange(tbProxy.Document.ContentStart, tbProxy.Document.ContentEnd);

            if (!IsIPAddress(range.Text))
            {
                MessageBox.Show("Вы указали некорректный IP-адрес.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(tbPort.Text, out _))
            {
                MessageBox.Show("Заполните поле порт.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(tbLogin.Text) ||
                string.IsNullOrWhiteSpace(tbPassword.Text) ||
                string.IsNullOrWhiteSpace(tbPort.Text) ||
                string.IsNullOrWhiteSpace(range.Text))
            {
                MessageBox.Show("Заполните все поля.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrWhiteSpace(tbChangeIpUrl.Text) &&
                !Uri.TryCreate(tbChangeIpUrl.Text.Trim(), UriKind.Absolute, out _))
            {
                MessageBox.Show("Ссылка смены IP должна быть корректным URL.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            List<string> proxy = CreateProxyList();
            if (rbAks.IsChecked == true)
            {
                File.WriteAllLines("proxy_aks.txt", proxy.ToArray(), Encoding.UTF8);
            }
            else if (rbPhone.IsChecked == true)
            {
                File.WriteAllLines("proxy_default.txt", proxy.ToArray(), Encoding.UTF8);
            }

            PromptRestartAfterSave();
        }

        private List<string> CreateProxyList()
        {
            TextRange range = new TextRange(tbProxy.Document.ContentStart, tbProxy.Document.ContentEnd);
            var list = SplitLines(range.Text);
            List<string> resProxy = new();
            string changeIpUrl = tbChangeIpUrl.Text.Trim();

            foreach (string proxy in list)
            {
                string proxyLine = $"{tbLogin.Text.Trim()}:{tbPassword.Text.Trim()}@{proxy.Trim()}:{tbPort.Text.Trim()}";
                if (!string.IsNullOrWhiteSpace(changeIpUrl))
                {
                    proxyLine += $"@{changeIpUrl}";
                }

                resProxy.Add(proxyLine);
            }

            return resProxy;
        }

        private static bool IsIPAddress(string ipAddress)
        {
            var list = SplitLines(ipAddress);
            if (list.Length == 0)
            {
                return false;
            }

            foreach (string proxy in list)
            {
                if (!IPAddress.TryParse(proxy.Trim(), out IPAddress? address) ||
                    address.AddressFamily != AddressFamily.InterNetwork)
                {
                    return false;
                }
            }

            return true;
        }

        private void btnSaveProxy_Click2(object sender, RoutedEventArgs e)
        {
            SaveChangesValueInAppSettings("GoogleTableFrom", tbOtDiapazon.Text.Trim(), "string");
            SaveChangesValueInAppSettings("GoogleTableTo", tbDoDiapazon.Text.Trim(), "string");
            SaveChangesValueInAppSettings("PriceOrientir", tbPriceOrientir.Text.Trim(), "string");
            SaveChangesValueInAppSettings("ResultParsing", tbPriceResult.Text.Trim(), "string");
            SaveChangesValueInAppSettings("RRCPrice", tbPriceRRC.Text.Trim(), "string");
            SaveChangesValueInAppSettings("CountPredloginiy", tbColPredlogeniy.Text.Trim(), "string");

            PromptRestartAfterSave();
        }

        private async void btnNormalizeOrientir_Click(object sender, RoutedEventArgs e)
        {
            var mainWindow = MainWindowOwner ?? Window.GetWindow(this) as MainWindow;
            if (mainWindow != null)
            {
                await mainWindow.StartNormalizeOrientirAsync();
            }
            else
            {
                MessageBox.Show("Не удалось найти главное окно программы.", "Обновление D", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void btnSaveTelegram_Click(object sender, RoutedEventArgs e)
        {
            string token = tbTelegramBotToken.Text.Trim();
            var idsRange = new TextRange(tbTelegramIds.Document.ContentStart, tbTelegramIds.Document.ContentEnd);
            string[] ids = SplitTelegramIds(idsRange.Text);

            if (string.IsNullOrWhiteSpace(token))
            {
                MessageBox.Show("Укажите токен Telegram-бота.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ids.Length == 0)
            {
                MessageBox.Show("Укажите хотя бы один ID группы или чата.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (string id in ids)
            {
                if (!IsTelegramChatId(id))
                {
                    MessageBox.Show($"Некорректный ID Telegram: {id}", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (!TimeSpan.TryParse(tbMorningReportTime.Text, out _))
            {
                MessageBox.Show("Время утреннего отчета нужно указать в формате 09:00:00.", "Ошибка заполнения", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string json = File.ReadAllText("appsettings.json");
            var jsonObj = JObject.Parse(json);
            jsonObj["Bot_Token"] = token;
            jsonObj["Ids"] = new JArray(ids);
            jsonObj["TelegramDefaultStartMessage"] = GetRichTextBoxText(tbTelegramDefaultStartMessage);
            jsonObj["TelegramDefaultStopMessage"] = GetRichTextBoxText(tbTelegramDefaultStopMessage);
            jsonObj["TelegramDefaultFinishMessage"] = GetRichTextBoxText(tbTelegramDefaultFinishMessage);
            jsonObj["TelegramAksStartMessage"] = GetRichTextBoxText(tbTelegramAksStartMessage);
            jsonObj["TelegramAksStopMessage"] = GetRichTextBoxText(tbTelegramAksStopMessage);
            jsonObj["TelegramAksFinishMessage"] = GetRichTextBoxText(tbTelegramAksFinishMessage);
            jsonObj["MorningReportEnabled"] = cbMorningReportEnabled.IsChecked == true;
            jsonObj["MorningReportTime"] = tbMorningReportTime.Text.Trim();

            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            File.WriteAllText("appsettings.json", output, Encoding.UTF8);
            config = LoadConfig();

            PromptRestartAfterSave();
        }

        private void PromptRestartAfterSave()
        {
            var mainWindow = MainWindowOwner ?? Window.GetWindow(this) as MainWindow;
            if (mainWindow != null && mainWindow.IsParsingActive)
            {
                MessageBox.Show(
                    "Настройки сохранены.\n\nСейчас идет парсинг, поэтому перезапуск не выполнен. Сначала остановите парсинг, дождитесь завершения остановки и затем перезапустите программу.",
                    "Настройки сохранены",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                "Настройки сохранены.\n\nПерезапустить программу сейчас?",
                "Перезапуск программы",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
        }

        private static void RestartApplication()
        {
            string? exePath = Environment.ProcessPath;

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                exePath = Process.GetCurrentProcess().MainModule?.FileName;
            }

            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                MessageBox.Show("Не удалось найти файл программы для перезапуска.", "Ошибка перезапуска", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            App.ReleaseSingleInstanceLockForRestart();

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            });

            Application.Current.Shutdown();
        }

        private void tbOtDiapazon_Loaded(object sender, RoutedEventArgs e)
        {
            tbOtDiapazon.Text = GetConfigValue("GoogleTableFrom", "A");
        }

        private void tbDoDiapazon_Loaded(object sender, RoutedEventArgs e)
        {
            tbDoDiapazon.Text = GetConfigValue("GoogleTableTo", "AC");
        }

        private void tbPriceOrientir_Loaded(object sender, RoutedEventArgs e)
        {
            tbPriceOrientir.Text = GetConfigValue("PriceOrientir", "D");
        }

        private void tbPriceResult_Loaded(object sender, RoutedEventArgs e)
        {
            tbPriceResult.Text = GetConfigValue("ResultParsing", "F");
        }

        private void tbPriceRRC_Loaded(object sender, RoutedEventArgs e)
        {
            tbPriceRRC.Text = GetConfigValue("RRCPrice", "G");
        }

        private void tbColPredlogeniy_Loaded(object sender, RoutedEventArgs e)
        {
            tbColPredlogeniy.Text = GetConfigValue("CountPredloginiy", "S");
        }

        private string GetConfigValue(string key, string fallback)
        {
            if (key == "GoogleTableTo")
            {
                return config["GoogleTableTo"] ?? config["GoolgeTableTo"] ?? fallback;
            }

            return config[key] ?? fallback;
        }

        private static void SetRichTextBoxText(RichTextBox richTextBox, string value)
        {
            new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd).Text = value ?? string.Empty;
        }

        private static string GetRichTextBoxText(RichTextBox richTextBox)
        {
            return new TextRange(richTextBox.Document.ContentStart, richTextBox.Document.ContentEnd)
                .Text
                .TrimEnd('\r', '\n');
        }

        private static string[] SplitLines(string value)
        {
            return value.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static string[] SplitTelegramIds(string value)
        {
            return value
                .Split(new[] { "\r\n", "\n", ",", ";", " " }, StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct()
                .ToArray();
        }

        private static bool IsTelegramChatId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.StartsWith("@") && value.Length > 1)
            {
                return true;
            }

            return long.TryParse(value, out _);
        }
    }
}
