using Hotline_Main_Parsing.common;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Google.Apis.Sheets.v4.Data;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json.Linq;
using PuppeteerSharp.Input;
using System.Globalization;
using Hotline_Main_Parsing.validmodel;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text.RegularExpressions;
using PuppeteerSharp;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Management;
using DefaultSheets = Hotline_Main_Parsing.@default;

namespace Hotline_Main_Parsing
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        BackgroundWorker _worker;
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        private Thread _thread;
        private CancellationTokenSource? _tokenSource;
        private Task? _parsingTask;
        private TimeValidateModel _validateModel;
        private static readonly object _fileLogLock = new object();
        private readonly object _runLogLock = new object();
        private string? _runLogPath;
        private Stopwatch? _runStopwatch;
        private int _processedProducts;
        private int _changedPrices;
        private int _errorCount;
        private bool _isNormalizingOrientir;
        private bool _wasTopmostBeforeSettings;
        private int _immediateStopRequested;
        private const int MaxVisibleLogItems = 700;
        private readonly ObservableCollection<VisibleLogEntry> _visibleLogEntries = new();
        private readonly DispatcherTimer _resourceMonitorTimer = new();
        private readonly Dictionary<int, TimeSpan> _lastCpuByProcess = new();
        private DateTime _lastResourceSampleUtc = DateTime.UtcNow;
        private DateTime _lastTemperatureSampleUtc = DateTime.MinValue;
        private double? _lastTemperatureCelsius;
        private string _lastTemperatureStatus = "ожидание замера";
        private readonly object _resourceSnapshotLock = new object();
        private readonly ResourceSnapshot _resourceSnapshot = new();
        private readonly object _progressSnapshotLock = new object();
        private readonly ProgressSnapshot _progressSnapshot = new();
        private readonly object _telegramPauseLock = new object();
        private DateTime? _telegramPauseUntilUtc;
        private DateTime? _lastAnnouncedPauseUntilUtc;
        private CancellationTokenSource? _telegramStatusCts;
        private Task? _telegramStatusTask;
        private int _telegramStatusOffset;
        private bool _telegramStatusInitialized;
        private string? _telegramStatusBotToken;
        private static readonly TimeSpan TelegramStatusPollInterval = TimeSpan.FromSeconds(3);

        private sealed class VisibleLogEntry
        {
            public string TimeText { get; init; } = string.Empty;
            public string ScopeText { get; init; } = string.Empty;
            public string MessageText { get; init; } = string.Empty;
            public string StatusText { get; init; } = string.Empty;
            public string OldPriceText { get; init; } = string.Empty;
            public string NewPriceText { get; init; } = string.Empty;
            public string ElapsedText { get; init; } = string.Empty;
            public Brush AccentBrush { get; init; } = Brushes.Transparent;
            public Brush PriceBrush { get; init; } = Brushes.Transparent;
            public Brush RowBackground { get; init; } = Brushes.Transparent;
        }

        private sealed class ParsingSectionStats
        {
            public int Processed { get; set; }
            public int ChangedPrices { get; set; }
            public int Errors { get; set; }
            public TimeSpan Elapsed { get; set; }
        }

        private sealed class ResourceSnapshot
        {
            public bool Available { get; set; }
            public double CpuPercent { get; set; }
            public long AppRamBytes { get; set; }
            public long ChromeRamBytes { get; set; }
            public int ChromeCount { get; set; }
            public double? TemperatureCelsius { get; set; }
            public string TemperatureStatus { get; set; } = string.Empty;
            public DateTime SampleUtc { get; set; }
        }

        private sealed class TemperatureReadResult
        {
            public double? Celsius { get; init; }
            public string Status { get; init; } = string.Empty;
        }

        private sealed class ProgressSnapshot
        {
            public string Status { get; set; } = "Ожидает запуска";
            public string Stage { get; set; } = "ожидание запуска";
            public string Section { get; set; } = string.Empty;
            public string ProductName { get; set; } = string.Empty;
            public string Note { get; set; } = string.Empty;
            public int Processed { get; set; }
            public int Total { get; set; }
            public double Progress { get; set; }
            public TimeSpan Elapsed { get; set; }
            public string Eta { get; set; } = "н/д";
        }

        private enum TelegramCommandKind
        {
            Unknown,
            Start,
            Status,
            Stop,
            Pause,
            Report,
            Load
        }

        private sealed class TelegramCommand
        {
            public TelegramCommandKind Kind { get; init; }
            public int? PauseMinutes { get; init; }
        }

        public bool IsParsingActive => _parsingTask != null && !_parsingTask.IsCompleted;

        public MainWindow()
        {
            InitializeComponent();
            Topmost = true;
            LogList.ItemsSource = _visibleLogEntries;

            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            _validateModel = new TimeValidateModel();
            this.DataContext = _validateModel;
            CheckBoxAks.IsChecked = Convert.ToBoolean(config["aksWorking"]);
            CheckBoxDefault.IsChecked = Convert.ToBoolean(config["mainWorking"]);
            bStop.IsEnabled = false;
            SetStatus("Ожидает запуска", "#FFEDD5", "#9A3412");
            SetStage("ожидание запуска");

            _worker = new BackgroundWorker();
            // Метод, который будет выполнятся в отдельном потоке. Событие DoWork срабатывает при вызове RunWorkerAsync
            _worker.DoWork += new DoWorkEventHandler(worker_DoWork);
            // Метод, который сработает в момент завершения BackgroundWorker
            _worker.RunWorkerCompleted += new RunWorkerCompletedEventHandler(worker_RunWorkerCompleted);
            // Событие для отслеживание процесса выполнения задачи BackgroundWorker. Событие возникает при вызове метода _worker.ReportProgress(i);
            _worker.ProgressChanged += new ProgressChangedEventHandler(worker_ProgressChanged);
            // Для отслеживания выполнения хода работ свойство WorkerReportsProgress устанавливаем true
            _worker.WorkerReportsProgress = true;
            // Поддержка отмены выполнения фоновой операции с помощью метода CancelAsync()
            _worker.WorkerSupportsCancellation = true;

            _resourceMonitorTimer.Interval = TimeSpan.FromSeconds(1);
            _resourceMonitorTimer.Tick += ResourceMonitorTimer_Tick;
            _resourceMonitorTimer.Start();
            UpdateResourceUsage();
            StartTelegramStatusListener();
        }

        protected override void OnClosed(EventArgs e)
        {
            _telegramStatusCts?.Cancel();
            _resourceMonitorTimer.Stop();
            base.OnClosed(e);
        }

        private void ConsoleOutputTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }
        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            TryStartParsing("Парсинг запущен.");
        }

        private bool TryStartParsing(string startLogMessage)
        {
            if (_parsingTask != null && !_parsingTask.IsCompleted)
            {
                AppendLog("Парсинг уже запущен.");
                return false;
            }

            if (_tokenSource != null)
            {
                _tokenSource.Dispose();
            }

            _tokenSource = new CancellationTokenSource();
            Interlocked.Exchange(ref _immediateStopRequested, 0);
            BeginRunLog();
            _parsingTask = Task.Run(() => MainMethodParsingHotlineAsync(_tokenSource.Token));
            bStart.IsEnabled = false;
            bStop.IsEnabled = true;
            SetStatus("Работает", "#DCFCE7", "#166534");
            SetStage("запуск парсинга");
            AppendLog(startLogMessage);
            return true;
        }

        void worker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            pbStatus.Value = e.ProgressPercentage;
        }

        // Метод работает из потока Dispetcher. Он может получать доступ к переменным окна.
        void worker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            this.Title = "Completed";
            MessageBox.Show("Completed");
            if (e.Cancelled)
                this.Title = "Cancelled";
        }

        // Данный метод работает в отдельном потоке.
        void worker_DoWork(object sender, DoWorkEventArgs e)
        {
            for (int i = 0; i <= 100; ++i)
            {
                // Эмулируем трудоемкую задачу.
                Thread.Sleep(50);

                // Отмена выполнения фоновой задачи, сработает при вызове CancelAsync
                if (_worker.CancellationPending)
                {
                    e.Cancel = true; // значение нужно установить для того что бы при событии RunWorkerCompleted определить почему оно было вызвано, из-за того что закончилась операция или из-за отмены.
                    return; // Отмена выполнения фоновой операции.
                }
                // Отчитываемся о проценте выполнения задачи.
                _worker.ReportProgress(i);
            }
        }

        private async void bStop_Click(object sender, RoutedEventArgs e)
        {
            await RequestImmediateStopAsync("Остановка кнопкой");
        }

        private async Task RequestImmediateStopAsync(string source)
        {
            if (_tokenSource == null || !IsParsingActive)
            {
                return;
            }

            _worker.ReportProgress(0);
            _tokenSource.Cancel();
            ClearTelegramPause();

            await Dispatcher.InvokeAsync((Action)delegate
            {
                bStop.IsEnabled = false;
                SetStatus("Останавливается", "#FEF3C7", "#92400E");
                SetStage("останавливаю: закрываю браузеры");
            });

            if (Interlocked.Exchange(ref _immediateStopRequested, 1) != 0)
            {
                return;
            }

            AppendLog($"{source}: остановка запрошена. Закрываю браузеры сразу.");
            await CloseAllBrowser();
            AppendLog("Браузеры закрыты. Парсер завершит остановку без записи неполного результата.");
        }

        private async Task MainMethodParsingHotlineAsync(CancellationToken cancellationToken)
        {
            //await CloseAllChromium();
            try
            {
                SetStage("читаю настройки");
                var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

                //var spreadSheetManager = new Hotline_Main_Parsing.@default.SpreadSheetManager(config["HotlineSpreadSheetId_default"]!, config["BitSpreadSheetId_default"]!, config["KYMPromSpreadSheetId_default"]!, config["HotlineSpreadSheetId_default_editor"]!);
                ////spreadSheetManager.UploadDataToKymProm(new List<@default.ProductInSheet>());
                //var ss = spreadSheetManager.GetHotlineEditorIdsOrder();
                while (!cancellationToken.IsCancellationRequested)
                {
                    GC.Collect();
                    Dispatcher.InvokeAsync((Action)delegate { ClearVisibleLog(); });
                    Dispatcher.InvokeAsync((Action)delegate { pbStatus.Value = 0.0; });
                    await WaitForTelegramPauseAsync("парсер", cancellationToken);

                    try
                    {
                        config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                        bool mainWorking = Convert.ToBoolean(config["mainWorking"]);
                        if (mainWorking && !cancellationToken.IsCancellationRequested)
                        {
                            await WaitForTelegramPauseAsync("смартфоны", cancellationToken);
                            SetStage("смартфоны: подготовка");
                            AppendLog("Основной парсинг начался");

                            await SendTelegramTemplateAsync("TelegramDefaultStartMessage", "Основной парсинг начался");
                            var defaultSw = Stopwatch.StartNew();
                            int defaultErrorsBefore = _errorCount;
                            ParsingSectionStats defaultStats;
                            try
                            {
                                SetStage("смартфоны: закрываю старый браузер");
                                await CloseAllChromium();
                                defaultStats = await Default(cancellationToken);
                            }
                            catch (Exception e) when (cancellationToken.IsCancellationRequested || IsCancellationException(e))
                            {
                                AppendLog("Основной парсинг остановлен пользователем.");
                                await SendTelegramStopAsync("TelegramDefaultStopMessage", "Основной парсинг остановлен пользователем.");
                                break;
                            }
                            catch (Exception e)
                            {
                                RegisterError(e.Message);
                                File.AppendAllText("logs.txt", $"\n{e.Message} - {e.StackTrace}");
                                throw;
                            }
                            if (cancellationToken.IsCancellationRequested)
                            {
                                AppendLog("Основной парсинг остановлен пользователем.");
                                await SendTelegramStopAsync("TelegramDefaultStopMessage", "Основной парсинг остановлен пользователем.");
                                break;
                            }
                            defaultSw.Stop();
                            defaultStats.Errors = Math.Max(0, _errorCount - defaultErrorsBefore);
                            if (defaultStats.Elapsed == TimeSpan.Zero)
                            {
                                defaultStats.Elapsed = defaultSw.Elapsed;
                            }
                            AppendLog("Основной парсинг закончился");
                            await SendTelegramTemplateAsync("TelegramDefaultFinishMessage", "Основной парсинг закончился\nОбработано: {processed}\nЦен сменилось: {changed}\nОшибок: {errors}\nВремя: {elapsed}", defaultStats);
                        }
                        config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                        bool aksWorking = Convert.ToBoolean(config["aksWorking"]);
                        Dispatcher.InvokeAsync((Action)delegate { pbStatus.Value = 0.0; });

                        if (aksWorking && !cancellationToken.IsCancellationRequested)
                        {
                            await WaitForTelegramPauseAsync("аксессуары", cancellationToken);
                            Dispatcher.InvokeAsync((Action)delegate { ClearVisibleLog(); });
                            SetStage("аксессуары: подготовка");
                            AppendLog("Парсинг аксессуаров начался");

                            await SendTelegramTemplateAsync("TelegramAksStartMessage", "Парсинг аксессуаров начался");
                            var aksSw = Stopwatch.StartNew();
                            int aksErrorsBefore = _errorCount;
                            ParsingSectionStats aksStats;
                            try
                            {
                                SetStage("аксессуары: закрываю старый браузер");
                                await CloseAllChromium();
                                aksStats = await Aks(cancellationToken);
                            }
                            catch (Exception e) when (cancellationToken.IsCancellationRequested || IsCancellationException(e))
                            {
                                AppendLog("Парсинг аксессуаров остановлен пользователем.");
                                await SendTelegramStopAsync("TelegramAksStopMessage", "Парсинг аксессуаров остановлен пользователем.");
                                break;
                            }
                            catch (Exception e)
                            {
                                RegisterError(e.Message);
                                File.AppendAllText("logs.txt", $"\n{e.Message} - {e.StackTrace}");
                                throw;
                            }
                            if (cancellationToken.IsCancellationRequested)
                            {
                                AppendLog("Парсинг аксессуаров остановлен пользователем.");
                                await SendTelegramStopAsync("TelegramAksStopMessage", "Парсинг аксессуаров остановлен пользователем.");
                                break;
                            }
                            aksSw.Stop();
                            aksStats.Errors = Math.Max(0, _errorCount - aksErrorsBefore);
                            if (aksStats.Elapsed == TimeSpan.Zero)
                            {
                                aksStats.Elapsed = aksSw.Elapsed;
                            }
                            AppendLog("Парсинг аксессуаров закончился");
                            await SendTelegramTemplateAsync("TelegramAksFinishMessage", "Парсинг аксессуаров закончился\nОбработано: {processed}\nЦен сменилось: {changed}\nОшибок: {errors}\nВремя: {elapsed}", aksStats);
                        }
                        config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await SendMorningReportIfNeededAsync();
                        }
                    }
                    catch (Exception e) when (cancellationToken.IsCancellationRequested || IsCancellationException(e))
                    {
                        AppendLog("Парсинг остановлен пользователем.");
                    }
                    catch (Exception e)
                    {
                        if (e.Message.Contains("443"))
                        {
                            RegisterError($"Парсинг остановлен из-за ошибки {e.Message}");
                            _tokenSource?.Cancel();
                        }
                        File.AppendAllText("logs.txt", $"\n{e.Message} - {e.StackTrace}");
                    }
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested || IsCancellationException(ex))
            {
                AppendLog("Парсинг остановлен пользователем.");
            }
            catch (Exception ex)
            {
                AppendLog($"Глобальная ошибка: {ex.Message}");
                File.AppendAllText("logs.txt", $"\n[GLOBAL] {ex.Message} - {ex.StackTrace}");
            }
            SetStage("закрываю браузер");
            await CloseAllBrowser();
            FinishRunLog(cancellationToken.IsCancellationRequested);
            Dispatcher.InvokeAsync((Action)delegate
            {
                bStart.IsEnabled = true;
                bStop.IsEnabled = false;
                SetStatus(cancellationToken.IsCancellationRequested ? "Остановлено" : "Ожидает запуска", "#FFEDD5", "#9A3412");
                SetStage(cancellationToken.IsCancellationRequested ? "остановлено" : "ожидание запуска");
            });
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e)
        {
            ClearVisibleLog();
            pbStatus.Value = 0;
            EtaText.Text = "н/д";
        }

        private void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            string logsDirectory = GetLogsDirectory();
            Directory.CreateDirectory(logsDirectory);

            Process.Start(new ProcessStartInfo
            {
                FileName = logsDirectory,
                UseShellExecute = true
            });
        }

        private void OpenCompetitors_Click(object sender, RoutedEventArgs e)
        {
            var window = new CompetitorsWindow
            {
                Owner = this
            };
            window.Show();
        }

        private async void NormalizeOrientir_Click(object sender, RoutedEventArgs e)
        {
            await StartNormalizeOrientirAsync();
        }

        public async Task StartNormalizeOrientirAsync()
        {
            if (IsParsingActive)
            {
                MessageBox.Show("Сначала остановите парсинг. Обновление D лучше запускать отдельно.", "Парсинг запущен", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_isNormalizingOrientir)
            {
                MessageBox.Show("Обновление D уже выполняется.", "Обновление D", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var confirm = MessageBox.Show(
                "Обновить колонку D в главной таблице по ОПТ из колонки O + 2%?\n\nОдинаковые модели с разными цветами будут выровнены в одну цену, если у них один ОПТ.",
                "Обновление D по ОПТ",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            _isNormalizingOrientir = true;
            bNormalizeOrientir.IsEnabled = false;
            bStart.IsEnabled = false;
            SetStatus("Обновляю D", "#DBEAFE", "#1D4ED8");
            SetStage("обновляю D по ОПТ");
            AppendLog("Начал обновление D по ОПТ.");

            try
            {
                var updates = await Task.Run(() =>
                {
                    var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                    var spreadSheetManager = new DefaultSheets.SpreadSheetManager(
                        config["HotlineSpreadSheetId_default"]!,
                        config["BitSpreadSheetId_default"]!,
                        config["KYMPromSpreadSheetId_default"]!,
                        config["HotlineSpreadSheetId_default_editor"]!,
                        config["1UaPromSpreadSheetId_default"]!,
                        config["StokPromSpreadSheetId_default"]!,
                        config["SmilePromSpreadSheetId_default"]!);

                    return spreadSheetManager.NormalizeOrientirPricesFromOpt(2m).ToList();
                });

                WriteOrientirUpdateLog(updates);
                AppendLog($"Обновление D завершено. Изменено строк: {updates.Count}.");
                MessageBox.Show(
                    updates.Count == 0 ? "Готово. Изменений нет: D уже совпадает с ОПТ + 2%." : $"Готово. Обновлено строк: {updates.Count}.",
                    "Обновление D",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                RegisterError($"Обновление D по ОПТ: {ex.Message}");
                MessageBox.Show($"Не удалось обновить D: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _isNormalizingOrientir = false;
                bNormalizeOrientir.IsEnabled = true;
                bStart.IsEnabled = !IsParsingActive;
                SetStatus("Ожидает запуска", "#FFEDD5", "#9A3412");
                SetStage("ожидание запуска");
            }
        }

        private void WriteOrientirUpdateLog(IReadOnlyList<DefaultSheets.OrientirPriceUpdate> updates)
        {
            Directory.CreateDirectory(GetLogsDirectory());
            string logPath = Path.Combine(GetLogsDirectory(), $"orientir_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
            var builder = new StringBuilder();
            builder.AppendLine($"Обновление D по ОПТ: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine("Правило: D = округление вверх(O * 1.02)");
            builder.AppendLine($"Изменено строк: {updates.Count}");
            builder.AppendLine();

            foreach (var update in updates)
            {
                builder.AppendLine(
                    $"Строка {update.RowNumber}; ID {update.Id}; O={update.OptPrice:0.##}; D: {update.OldPrice} -> {update.NewPrice:0}; {update.Name}");
            }

            File.WriteAllText(logPath, builder.ToString(), Encoding.UTF8);
            AppendLog($"Лог обновления D: {logPath}");
        }

        private static bool ShouldUseOrientirAsResult(IList<object> row)
        {
            return !IsCheckedCell(row, 9) && !IsCheckedCell(row, 10) && !IsCheckedCell(row, 11);
        }

        private static bool IsCancellationException(Exception exception)
        {
            return exception is OperationCanceledException
                || exception is TaskCanceledException
                || exception.Message.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("Операция была отменена", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCheckedCell(IList<object> row, int index)
        {
            return GetSheetCell(row, index).Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetSheetCell(IList<object> row, int index)
        {
            return row.Count > index ? row[index]?.ToString()?.Trim() ?? "" : "";
        }

        private static string GetHotlineUrl(IList<object> row)
        {
            string preferred = NormalizeHotlineUrl(GetSheetCell(row, 8));
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                return preferred;
            }

            foreach (var cell in row)
            {
                string url = NormalizeHotlineUrl(cell?.ToString());
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private static string NormalizeHotlineUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            string text = value.Trim();
            var match = Regex.Match(text, @"https?://[^\s""']+", RegexOptions.IgnoreCase);
            if (match.Success && match.Value.Contains("hotline.ua", StringComparison.OrdinalIgnoreCase))
            {
                return match.Value;
            }

            if (text.StartsWith("hotline.ua/", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("www.hotline.ua/", StringComparison.OrdinalIgnoreCase))
            {
                return "https://" + text;
            }

            if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return text.Contains("hotline.ua", StringComparison.OrdinalIgnoreCase) ? text : string.Empty;
            }

            return string.Empty;
        }

        private static bool TryParseSheetPrice(string? value, out decimal price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            string normalized = value
                .Replace("\u00A0", " ")
                .Replace("грн.", "", StringComparison.OrdinalIgnoreCase)
                .Replace("грн", "", StringComparison.OrdinalIgnoreCase)
                .Replace(" ", "")
                .Trim()
                .Replace(",", ".");

            return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out price);
        }

        private static void ApplyOrientirAsResultIfNeeded(DefaultSheets.ProductInSheet productInSheet, bool shouldUseOrientirAsResult)
        {
            if (!shouldUseOrientirAsResult || productInSheet.Price <= 0)
            {
                return;
            }

            productInSheet.BitPrice = productInSheet.Price;
            productInSheet.ReadyPrice = productInSheet.Price;
        }

        private static void ApplyOrientirAsResultIfNeeded(Hotline_Main_Parsing.aks.ProductInSheet productInSheet, bool shouldUseOrientirAsResult)
        {
            if (!shouldUseOrientirAsResult || productInSheet.Price <= 0)
            {
                return;
            }

            productInSheet.BitPrice = productInSheet.Price;
            productInSheet.ReadyPrice = productInSheet.Price;
        }

        private static SoftPriceAdjustmentResult ApplySoftCompetitorPriceDrop(DefaultSheets.ProductInSheet productInSheet, IReadOnlyList<Shop> shops, decimal rangePercent)
        {
            var result = TryCalculateSoftCompetitorPriceDrop(
                productInSheet.Price,
                productInSheet.ReadyPrice,
                productInSheet.BuyPriceInGRN,
                productInSheet.ParseMarkOld,
                productInSheet.ParseMarkNew,
                shops,
                rangePercent);

            if (!result.Applied)
            {
                return result;
            }

            productInSheet.BitPrice = result.NewPrice;
            productInSheet.ReadyPrice = result.NewPrice;
            return result;
        }

        private static SoftPriceAdjustmentResult ApplySoftCompetitorPriceDrop(Hotline_Main_Parsing.aks.ProductInSheet productInSheet, IReadOnlyList<Shop> shops, decimal rangePercent)
        {
            var result = TryCalculateSoftCompetitorPriceDrop(
                productInSheet.Price,
                productInSheet.ReadyPrice,
                productInSheet.BuyPriceInGRN,
                productInSheet.ParseMarkOld,
                productInSheet.ParseMarkNew,
                shops,
                rangePercent);

            if (!result.Applied)
            {
                return result;
            }

            productInSheet.BitPrice = result.NewPrice;
            productInSheet.ReadyPrice = result.NewPrice;
            return result;
        }

        private static SoftPriceAdjustmentResult TryCalculateSoftCompetitorPriceDrop(
            decimal orientirPrice,
            decimal currentReadyPrice,
            decimal? buyPrice,
            bool parseMarkOld,
            bool parseMarkNew,
            IReadOnlyList<Shop> shops,
            decimal rangePercent)
        {
            if ((!parseMarkOld && !parseMarkNew) || orientirPrice <= 0 || currentReadyPrice <= 0)
            {
                return SoftPriceAdjustmentResult.None("товар не участвует в расчете цены");
            }

            var lowerCompetitors = shops
                .Where(shop => shop.Price > 0 &&
                               shop.Price < currentReadyPrice &&
                               !AntiDumpingService.IsOwnShop(shop.Name))
                .OrderByDescending(shop => shop.Price)
                .ToList();

            var nearestLowerCompetitor = lowerCompetitors.FirstOrDefault();

            if (nearestLowerCompetitor == null)
            {
                return SoftPriceAdjustmentResult.None("конкурент рядом ниже не найден");
            }

            decimal gapPercent = Math.Round((currentReadyPrice - nearestLowerCompetitor.Price) / currentReadyPrice * 100m, 2);
            if (gapPercent < 1m || gapPercent > 3m)
            {
                return SoftPriceAdjustmentResult.None("разница не входит в коридор 1-3%");
            }

            decimal newPrice = Math.Round(nearestLowerCompetitor.Price - 1m, 0, MidpointRounding.AwayFromZero);
            decimal minAllowedPrice = CalculateMinimumAllowedPrice(orientirPrice, buyPrice, rangePercent);
            if (newPrice <= 0 || newPrice >= currentReadyPrice)
            {
                return SoftPriceAdjustmentResult.None("новая цена не ниже текущей");
            }

            if (newPrice < minAllowedPrice)
            {
                return SoftPriceAdjustmentResult.None("ниже минимального порога");
            }

            var nextLowerCompetitor = lowerCompetitors.Skip(1).FirstOrDefault();
            if (nextLowerCompetitor != null && newPrice > nextLowerCompetitor.Price)
            {
                decimal nextGapAfterDrop = Math.Round((newPrice - nextLowerCompetitor.Price) / newPrice * 100m, 2);
                if (nextGapAfterDrop >= 1m && nextGapAfterDrop <= 3m)
                {
                    return SoftPriceAdjustmentResult.None("ниже есть еще один близкий конкурент");
                }
            }

            return new SoftPriceAdjustmentResult
            {
                Applied = true,
                ShopName = nearestLowerCompetitor.Name,
                CompetitorPrice = nearestLowerCompetitor.Price,
                OldPrice = currentReadyPrice,
                NewPrice = newPrice,
                GapPercent = gapPercent,
                Reason = "конкурент ниже на 1-3%"
            };
        }

        private static decimal CalculateMinimumAllowedPrice(decimal orientirPrice, decimal? buyPrice, decimal rangePercent)
        {
            decimal minByPercent = Math.Round(orientirPrice * (100 - rangePercent) / 100);
            if (buyPrice.HasValue && buyPrice.Value > minByPercent)
            {
                return buyPrice.Value;
            }

            return minByPercent;
        }

        private static async Task ApplyRrcBitPriceIfNeeded(DefaultSheets.ProductInSheet productInSheet, IList<object> row, IReadOnlyDictionary<string, int> symbols, string rrcPriceColumn)
        {
            if (!await ShouldUseRrcBitPrice(row))
            {
                return;
            }

            if (TryGetRrcPrice(row, symbols, rrcPriceColumn, out decimal bitPrice))
            {
                productInSheet.BitPrice = bitPrice;
            }
        }

        private static async Task ApplyRrcBitPriceIfNeeded(Hotline_Main_Parsing.aks.ProductInSheet productInSheet, IList<object> row, IReadOnlyDictionary<string, int> symbols, string rrcPriceColumn)
        {
            if (!await ShouldUseRrcBitPrice(row))
            {
                return;
            }

            if (TryGetRrcPrice(row, symbols, rrcPriceColumn, out decimal bitPrice))
            {
                productInSheet.BitPrice = bitPrice;
            }
        }

        private static async Task<bool> ShouldUseRrcBitPrice(IList<object> row)
        {
            return IsCheckedCell(row, 11) && await TimeOut();
        }

        private static bool TryGetRrcPrice(IList<object> row, IReadOnlyDictionary<string, int> symbols, string rrcPriceColumn, out decimal price)
        {
            price = 0;
            if (string.IsNullOrWhiteSpace(rrcPriceColumn) || !symbols.TryGetValue(rrcPriceColumn, out int rrcPriceIndex))
            {
                return false;
            }

            return TryParseSheetPrice(GetSheetCell(row, rrcPriceIndex), out price) && price > 0;
        }

        private static bool SwitchParseMarkOldToNewIfNeeded(DefaultSheets.ProductInSheet productInSheet)
        {
            if (!ShouldSwitchParseMarkOldToNew(productInSheet.Price, productInSheet.ReadyPrice, productInSheet.PriceRange, productInSheet.ParseMarkOld, productInSheet.ParseMarkNew))
            {
                return false;
            }

            productInSheet.ParseMarkOld = false;
            productInSheet.ParseMarkNew = true;
            productInSheet.SwitchParseMarkOldToNew = true;
            return true;
        }

        private static bool SwitchParseMarkOldToNewIfNeeded(Hotline_Main_Parsing.aks.ProductInSheet productInSheet)
        {
            if (!ShouldSwitchParseMarkOldToNew(productInSheet.Price, productInSheet.ReadyPrice, productInSheet.PriceRange, productInSheet.ParseMarkOld, productInSheet.ParseMarkNew))
            {
                return false;
            }

            productInSheet.ParseMarkOld = false;
            productInSheet.ParseMarkNew = true;
            productInSheet.SwitchParseMarkOldToNew = true;
            return true;
        }

        private static bool ShouldSwitchParseMarkOldToNew(decimal orientirPrice, decimal readyPrice, decimal[] hotlinePrices, bool parseMarkOld, bool parseMarkNew)
        {
            if (!parseMarkOld || parseMarkNew || orientirPrice <= 0 || readyPrice <= 0 || hotlinePrices.Length == 0)
            {
                return false;
            }

            decimal roundedOrientir = Math.Round(orientirPrice, 0, MidpointRounding.AwayFromZero);
            decimal roundedReady = Math.Round(readyPrice, 0, MidpointRounding.AwayFromZero);
            if (roundedOrientir != roundedReady)
            {
                return false;
            }

            return hotlinePrices.Any(price => price > 0 && Math.Round(price, 0, MidpointRounding.AwayFromZero) < roundedReady);
        }

        private void BeginRunLog()
        {
            Directory.CreateDirectory(GetLogsDirectory());
            _runStopwatch = Stopwatch.StartNew();
            _processedProducts = 0;
            _changedPrices = 0;
            _errorCount = 0;
            lock (_progressSnapshotLock)
            {
                _progressSnapshot.Processed = 0;
                _progressSnapshot.Total = 0;
                _progressSnapshot.Progress = 0;
                _progressSnapshot.Elapsed = TimeSpan.Zero;
                _progressSnapshot.Eta = "считаю";
                _progressSnapshot.ProductName = string.Empty;
                _progressSnapshot.Note = string.Empty;
            }
            _runLogPath = Path.Combine(GetLogsDirectory(), $"run_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");

            File.WriteAllText(_runLogPath,
                $"Запуск: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n" +
                $"Режимы: смартфоны={CheckBoxDefault.IsChecked}, аксессуары={CheckBoxAks.IsChecked}\r\n\r\n",
                Encoding.UTF8);
        }

        private void FinishRunLog(bool wasCancelled)
        {
            _runStopwatch?.Stop();
            string status = wasCancelled ? "Остановлено пользователем" : "Завершено";
            string elapsed = _runStopwatch?.Elapsed.ToString(@"hh\:mm\:ss") ?? "00:00:00";

            AppendHistoryLog("");
            AppendHistoryLog($"Итог: {status}");
            AppendHistoryLog($"Обработано товаров: {_processedProducts}");
            AppendHistoryLog($"Цен сменилось: {_changedPrices}");
            AppendHistoryLog($"Ошибок: {_errorCount}");
            AppendHistoryLog($"Время работы: {elapsed}");
            _runLogPath = null;
            _runStopwatch = null;
        }

        private static string GetLogsDirectory()
        {
            return Path.Combine(AppContext.BaseDirectory, "logs");
        }

        private void AppendLog(string message)
        {
            Dispatcher.InvokeAsync((Action)delegate
            {
                AppendVisibleLog(message);
            });

            AppendHistoryLog(message);
        }

        private void AppendVisibleLog(string message)
        {
            string scope = GetGenericLogScope(message);
            string displayMessage = GetGenericLogMessage(message, scope);

            var entry = CreateLogEntry(
                scope: scope,
                message: displayMessage,
                status: GetGenericLogStatus(message),
                elapsed: string.Empty);

            AppendVisibleLogEntry(entry);
        }

        private void AppendProgressLog(string section, int processed, int total, string? productName, string? note, TimeSpan elapsed)
        {
            AppendProgressLog(section, processed, total, productName, note, elapsed, null, null);
        }

        private void AppendProgressLog(
            string section,
            int processed,
            int total,
            string? productName,
            string? note,
            TimeSpan elapsed,
            decimal? oldPrice,
            decimal? newPrice)
        {
            string status = string.IsNullOrWhiteSpace(note) ? "обработано" : note;
            string message = string.IsNullOrWhiteSpace(productName) ? "-" : productName;

            var entry = CreateLogEntry(
                scope: $"{section} {processed}/{total}",
                message: message,
                status: status,
                elapsed: elapsed.ToString(@"hh\:mm\:ss"),
                oldPrice: oldPrice,
                newPrice: newPrice);

            AppendVisibleLogEntry(entry);
        }

        private VisibleLogEntry CreateLogEntry(string scope, string message, string status, string elapsed)
        {
            return CreateLogEntry(scope, message, status, elapsed, null, null);
        }

        private VisibleLogEntry CreateLogEntry(
            string scope,
            string message,
            string status,
            string elapsed,
            decimal? oldPrice,
            decimal? newPrice)
        {
            Brush accent = GetLogAccentBrush(status, message);
            Brush background = GetLogBackgroundBrush(status, message);
            string oldPriceText = FormatLogPrice(oldPrice);
            string newPriceText = FormatLogPrice(newPrice);
            string trendText = GetPriceTrendText(oldPrice, newPrice);

            return new VisibleLogEntry
            {
                TimeText = DateTime.Now.ToString("HH:mm:ss"),
                ScopeText = scope,
                MessageText = message,
                StatusText = status,
                OldPriceText = oldPriceText,
                NewPriceText = string.IsNullOrWhiteSpace(newPriceText) ? string.Empty : $"{trendText}{newPriceText}",
                ElapsedText = elapsed,
                AccentBrush = accent,
                PriceBrush = GetPriceBrush(oldPrice, newPrice),
                RowBackground = background
            };
        }

        private static string FormatLogPrice(decimal? price)
        {
            if (!price.HasValue || price.Value <= 0)
            {
                return string.Empty;
            }

            return Math.Round(price.Value, 0, MidpointRounding.AwayFromZero)
                .ToString("#,0", CultureInfo.InvariantCulture)
                .Replace(",", " ");
        }

        private static string GetPriceTrendText(decimal? oldPrice, decimal? newPrice)
        {
            if (!oldPrice.HasValue || !newPrice.HasValue || oldPrice.Value <= 0 || newPrice.Value <= 0)
            {
                return string.Empty;
            }

            if (newPrice.Value > oldPrice.Value)
            {
                return "↑ ";
            }

            if (newPrice.Value < oldPrice.Value)
            {
                return "↓ ";
            }

            return string.Empty;
        }

        private static Brush GetPriceBrush(decimal? oldPrice, decimal? newPrice)
        {
            if (!oldPrice.HasValue || !newPrice.HasValue || oldPrice.Value <= 0 || newPrice.Value <= 0)
            {
                return CreateBrush("#44546A");
            }

            if (newPrice.Value > oldPrice.Value)
            {
                return CreateBrush("#DC2626");
            }

            if (newPrice.Value < oldPrice.Value)
            {
                return CreateBrush("#059669");
            }

            return CreateBrush("#44546A");
        }

        private void AppendVisibleLogEntry(VisibleLogEntry entry)
        {
            _visibleLogEntries.Add(entry);
            TrimVisibleLogIfNeeded();
            LogList.ScrollIntoView(entry);
        }

        private void TrimVisibleLogIfNeeded()
        {
            while (_visibleLogEntries.Count > MaxVisibleLogItems)
            {
                _visibleLogEntries.RemoveAt(0);
            }
        }

        private void ClearVisibleLog()
        {
            _visibleLogEntries.Clear();
        }

        private static string GetGenericLogScope(string message)
        {
            int separatorIndex = message.IndexOf(':');
            if (separatorIndex > 0 && separatorIndex <= 18)
            {
                return message.Substring(0, separatorIndex).Trim();
            }

            return "Система";
        }

        private static string GetGenericLogMessage(string message, string scope)
        {
            int separatorIndex = message.IndexOf(':');
            if (scope != "Система" && separatorIndex >= 0 && separatorIndex + 1 < message.Length)
            {
                return message.Substring(separatorIndex + 1).Trim();
            }

            return message;
        }

        private static string GetGenericLogStatus(string message)
        {
            if (message.Contains("Ошибка", StringComparison.OrdinalIgnoreCase))
            {
                return "ошибка";
            }

            if (message.Contains("останов", StringComparison.OrdinalIgnoreCase))
            {
                return "остановка";
            }

            if (message.Contains("закон", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("готов", StringComparison.OrdinalIgnoreCase))
            {
                return "готово";
            }

            if (message.Contains("нач", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("запущ", StringComparison.OrdinalIgnoreCase))
            {
                return "старт";
            }

            return "инфо";
        }

        private static Brush GetLogAccentBrush(string status, string message)
        {
            string value = $"{status} {message}";
            if (value.Contains("ошибка", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("таймаут", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBrush("#DC2626");
            }

            if (value.Contains("нету в наличии", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("нет предложений", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("пропуск", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("останов", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBrush("#D97706");
            }

            if (value.Contains("готов", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("обработано", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBrush("#16A34A");
            }

            return CreateBrush("#2563EB");
        }

        private static Brush GetLogBackgroundBrush(string status, string message)
        {
            string value = $"{status} {message}";
            if (value.Contains("ошибка", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("таймаут", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBrush("#FFF1F2");
            }

            if (value.Contains("нету в наличии", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("нет предложений", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("пропуск", StringComparison.OrdinalIgnoreCase))
            {
                return CreateBrush("#FFFBEB");
            }

            return Brushes.Transparent;
        }

        private void ResourceMonitorTimer_Tick(object? sender, EventArgs e)
        {
            UpdateResourceUsage();
        }

        private void UpdateResourceUsage()
        {
            try
            {
                var now = DateTime.UtcNow;
                double elapsedSeconds = Math.Max((now - _lastResourceSampleUtc).TotalSeconds, 0.1);
                var currentCpu = new Dictionary<int, TimeSpan>();

                long appRam = 0;
                long chromeRam = 0;
                int chromeCount = 0;
                double totalCpu = 0;

                using (var currentProcess = Process.GetCurrentProcess())
                {
                    appRam = currentProcess.WorkingSet64;
                    totalCpu += CalculateCpuUsage(currentProcess, currentCpu, elapsedSeconds);
                }

                foreach (var chromeProcess in Process.GetProcessesByName("chrome"))
                {
                    using (chromeProcess)
                    {
                        try
                        {
                            if (chromeProcess.HasExited)
                            {
                                continue;
                            }

                            chromeCount++;
                            chromeRam += chromeProcess.WorkingSet64;
                            totalCpu += CalculateCpuUsage(chromeProcess, currentCpu, elapsedSeconds);
                        }
                        catch
                        {
                            // Chrome can close between enumeration and reading metrics.
                        }
                    }
                }

                _lastCpuByProcess.Clear();
                foreach (var item in currentCpu)
                {
                    _lastCpuByProcess[item.Key] = item.Value;
                }

                _lastResourceSampleUtc = now;

                CpuUsageText.Text = $"{Math.Min(999, totalCpu):0}%";
                RamUsageText.Text = FormatMemory(appRam);
                ChromeUsageText.Text = $"{chromeCount} / {FormatMemory(chromeRam)}";
                double? temperature = UpdateTemperatureUsage(now);

                lock (_resourceSnapshotLock)
                {
                    _resourceSnapshot.Available = true;
                    _resourceSnapshot.CpuPercent = Math.Min(999, totalCpu);
                    _resourceSnapshot.AppRamBytes = appRam;
                    _resourceSnapshot.ChromeRamBytes = chromeRam;
                    _resourceSnapshot.ChromeCount = chromeCount;
                    _resourceSnapshot.TemperatureCelsius = temperature;
                    _resourceSnapshot.TemperatureStatus = _lastTemperatureStatus;
                    _resourceSnapshot.SampleUtc = now;
                }

                CpuUsageText.Foreground = CreateBrush(totalCpu >= 80 ? "#DC2626" : totalCpu >= 50 ? "#D97706" : "#166534");
                RamUsageText.Foreground = CreateBrush(appRam >= 800L * 1024 * 1024 ? "#DC2626" : appRam >= 300L * 1024 * 1024 ? "#D97706" : "#166534");
                ChromeUsageText.Foreground = CreateBrush(chromeRam >= 3L * 1024 * 1024 * 1024 ? "#DC2626" : chromeRam >= 1500L * 1024 * 1024 ? "#D97706" : "#166534");
            }
            catch
            {
                CpuUsageText.Text = "н/д";
                RamUsageText.Text = "н/д";
                ChromeUsageText.Text = "н/д";
                TemperatureText.Text = "н/д";
                lock (_resourceSnapshotLock)
                {
                    _resourceSnapshot.Available = false;
                    _resourceSnapshot.TemperatureStatus = "ошибка чтения нагрузки";
                    _resourceSnapshot.SampleUtc = DateTime.UtcNow;
                }
            }
        }

        private double? UpdateTemperatureUsage(DateTime nowUtc)
        {
            if ((nowUtc - _lastTemperatureSampleUtc).TotalSeconds >= 10)
            {
                TemperatureReadResult temperatureResult = TryReadTemperatureCelsius();
                _lastTemperatureCelsius = temperatureResult.Celsius;
                _lastTemperatureStatus = temperatureResult.Status;
                _lastTemperatureSampleUtc = nowUtc;
            }

            if (_lastTemperatureCelsius.HasValue)
            {
                double temperature = _lastTemperatureCelsius.Value;
                TemperatureText.Text = $"{temperature:0}°C";
                TemperatureText.Foreground = CreateBrush(temperature >= 85 ? "#DC2626" : temperature >= 75 ? "#D97706" : "#166534");
                TemperatureText.ToolTip = _lastTemperatureStatus;
            }
            else
            {
                TemperatureText.Text = IsRunningAsAdministrator() ? "н/д" : "админ?";
                TemperatureText.Foreground = CreateBrush("#8EA0BA");
                TemperatureText.ToolTip = _lastTemperatureStatus;
            }

            return _lastTemperatureCelsius;
        }

        private static TemperatureReadResult TryReadTemperatureCelsius()
        {
            var diagnostics = new List<string>();

            double? embeddedHardwareTemperature = TryReadEmbeddedHardwareTemperatureCelsius(out string embeddedHardwareStatus);
            diagnostics.Add(embeddedHardwareStatus);
            if (embeddedHardwareTemperature.HasValue)
            {
                return new TemperatureReadResult
                {
                    Celsius = embeddedHardwareTemperature,
                    Status = "температура CPU прочитана из встроенных датчиков"
                };
            }

            double? hardwareMonitorTemperature = TryReadHardwareMonitorTemperatureCelsius(out string hardwareMonitorStatus);
            diagnostics.Add(hardwareMonitorStatus);
            if (hardwareMonitorTemperature.HasValue)
            {
                return new TemperatureReadResult
                {
                    Celsius = hardwareMonitorTemperature,
                    Status = "температура CPU прочитана через Hardware Monitor"
                };
            }

            double? acpiTemperature = TryReadAcpiTemperatureCelsius(out string acpiStatus);
            diagnostics.Add(acpiStatus);
            if (acpiTemperature.HasValue)
            {
                return new TemperatureReadResult
                {
                    Celsius = acpiTemperature,
                    Status = "температура CPU прочитана через Windows ACPI"
                };
            }

            return new TemperatureReadResult
            {
                Celsius = null,
                Status = IsRunningAsAdministrator()
                    ? $"температура недоступна: {string.Join("; ", diagnostics.Where(item => !string.IsNullOrWhiteSpace(item)))}"
                    : $"температура недоступна: программа не запущена от администратора; {string.Join("; ", diagnostics.Where(item => !string.IsNullOrWhiteSpace(item)))}"
            };
        }

        private static double? TryReadEmbeddedHardwareTemperatureCelsius(out string status)
        {
            Computer? computer = null;
            try
            {
                var temperatures = new List<double>();
                computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = false,
                    IsMotherboardEnabled = false,
                    IsMemoryEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false
                };

                computer.Open();
                foreach (IHardware hardware in computer.Hardware)
                {
                    if (hardware.HardwareType == HardwareType.Cpu)
                    {
                        ReadHardwareTemperatures(hardware, temperatures);
                    }
                }

                if (temperatures.Count == 0)
                {
                    status = $"встроенные CPU-датчики: 0 значений, устройств: {computer.Hardware.Count}";
                    return null;
                }

                status = $"встроенные CPU-датчики: найдено {temperatures.Count}";
                return temperatures.Max();
            }
            catch (Exception ex)
            {
                status = $"встроенные CPU-датчики: ошибка {GetShortExceptionMessage(ex)}";
                return null;
            }
            finally
            {
                try
                {
                    computer?.Close();
                }
                catch
                {
                    // Hardware monitor cleanup is best-effort.
                }
            }
        }

        private static void ReadHardwareTemperatures(IHardware hardware, List<double> temperatures)
        {
            hardware.Update();
            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Value.HasValue)
                {
                    double celsius = sensor.Value.Value;
                    if (celsius > 0 && celsius < 130)
                    {
                        temperatures.Add(celsius);
                    }
                }
            }

            foreach (IHardware subHardware in hardware.SubHardware)
            {
                ReadHardwareTemperatures(subHardware, temperatures);
            }
        }

        private static double? TryReadNvidiaSmiTemperatureCelsius(out string status)
        {
            var candidates = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramW6432%\NVIDIA Corporation\NVSMI\nvidia-smi.exe"),
                "nvidia-smi.exe"
            };

            bool executableFound = false;
            string? lastError = null;
            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    bool isPath = candidate.Contains('\\') || candidate.Contains('/');
                    if (isPath && !File.Exists(candidate))
                    {
                        continue;
                    }

                    executableFound = true;
                    using var process = new Process();
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = candidate,
                        Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };

                    process.Start();
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(1500))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                            // Process may exit between timeout and kill.
                        }

                        lastError = "таймаут";
                        continue;
                    }

                    var temperatures = new List<double>();
                    foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string value = line.Trim();
                        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double celsius) ||
                            double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out celsius))
                        {
                            if (celsius > 0 && celsius < 130)
                            {
                                temperatures.Add(celsius);
                            }
                        }
                    }

                    if (temperatures.Count > 0)
                    {
                        status = "NVIDIA: температура найдена";
                        return temperatures.Max();
                    }

                    string error = process.StandardError.ReadToEnd().Trim();
                    lastError = string.IsNullOrWhiteSpace(error) ? "температура не найдена" : error;
                }
                catch (Exception ex)
                {
                    lastError = GetShortExceptionMessage(ex);
                    // NVIDIA utility is optional and may not exist on this PC.
                }
            }

            status = executableFound
                ? $"NVIDIA: {lastError ?? "температура не найдена"}"
                : "NVIDIA: nvidia-smi не найден";
            return null;
        }

        private static double? TryReadHardwareMonitorTemperatureCelsius(out string status)
        {
            var diagnostics = new List<string>();
            foreach (string scope in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        scope,
                        "SELECT Name, Identifier, SensorType, Value FROM Sensor WHERE SensorType = 'Temperature'");

                    var temperatures = new List<double>();
                    foreach (ManagementObject item in searcher.Get())
                    {
                        using (item)
                        {
                            object? rawValue = item["Value"];
                            if (rawValue == null)
                            {
                                continue;
                            }

                            string name = item["Name"]?.ToString() ?? string.Empty;
                            string identifier = item["Identifier"]?.ToString() ?? string.Empty;
                            if (!IsCpuTemperatureSensor(name, identifier))
                            {
                                continue;
                            }

                            double celsius = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                            if (celsius > 0 && celsius < 130)
                            {
                                temperatures.Add(celsius);
                            }
                        }
                    }

                    if (temperatures.Count > 0)
                    {
                        status = $"{scope}: найдено CPU-значений {temperatures.Count}";
                        return temperatures.Max();
                    }

                    diagnostics.Add($"{scope}: 0 CPU-значений");
                }
                catch (Exception ex)
                {
                    diagnostics.Add($"{scope}: {GetShortExceptionMessage(ex)}");
                    // Hardware monitor providers are optional.
                }
            }

            status = string.Join(", ", diagnostics);
            return null;
        }

        private static bool IsCpuTemperatureSensor(string name, string identifier)
        {
            string value = $"{identifier} {name}".ToLowerInvariant();
            if (value.Contains("gpu") ||
                value.Contains("nvidia") ||
                value.Contains("radeon") ||
                value.Contains("storage") ||
                value.Contains("ssd") ||
                value.Contains("hdd") ||
                value.Contains("nvme"))
            {
                return false;
            }

            return value.Contains("cpu") ||
                   value.Contains("intelcpu") ||
                   value.Contains("amdcpu") ||
                   value.Contains("processor") ||
                   value.Contains("package") ||
                   value.Contains("core") ||
                   value.Contains("tctl") ||
                   value.Contains("tdie") ||
                   value.Contains("ccd");
        }

        private static double? TryReadAcpiTemperatureCelsius(out string status)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");

                var temperatures = new List<double>();
                foreach (ManagementObject item in searcher.Get())
                {
                    using (item)
                    {
                        object? rawValue = item["CurrentTemperature"];
                        if (rawValue == null)
                        {
                            continue;
                        }

                        double kelvinTenth = Convert.ToDouble(rawValue, CultureInfo.InvariantCulture);
                        double celsius = kelvinTenth / 10.0 - 273.15;
                        if (celsius > 0 && celsius < 130)
                        {
                            temperatures.Add(celsius);
                        }
                    }
                }

                if (temperatures.Count == 0)
                {
                    status = "Windows ACPI: 0 значений";
                    return null;
                }

                status = $"Windows ACPI: найдено {temperatures.Count}";
                return temperatures.Max();
            }
            catch (Exception ex)
            {
                status = $"Windows ACPI: {GetShortExceptionMessage(ex)}";
                return null;
            }
        }

        private static string GetShortExceptionMessage(Exception ex)
        {
            string message = ex.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                message = ex.GetType().Name;
            }

            message = message.Replace("\r", " ").Replace("\n", " ").Trim();
            return message.Length <= 90 ? message : message[..90] + "...";
        }

        private static bool IsRunningAsAdministrator()
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private double CalculateCpuUsage(Process process, Dictionary<int, TimeSpan> currentCpu, double elapsedSeconds)
        {
            TimeSpan cpu = process.TotalProcessorTime;
            currentCpu[process.Id] = cpu;

            if (!_lastCpuByProcess.TryGetValue(process.Id, out var previousCpu))
            {
                return 0;
            }

            double cpuDelta = Math.Max(0, (cpu - previousCpu).TotalSeconds);
            return cpuDelta / elapsedSeconds / Math.Max(1, Environment.ProcessorCount) * 100.0;
        }

        private static string FormatMemory(long bytes)
        {
            const double megabyte = 1024d * 1024d;
            const double gigabyte = 1024d * 1024d * 1024d;

            if (bytes >= gigabyte)
            {
                return $"{bytes / gigabyte:0.0} ГБ";
            }

            return $"{bytes / megabyte:0} МБ";
        }

        private static string FormatEstimatedRemaining(int processed, int total, TimeSpan elapsed)
        {
            if (total <= 0 || processed <= 0 || elapsed.TotalSeconds < 1)
            {
                return "считаю";
            }

            int remainingItems = Math.Max(0, total - processed);
            if (remainingItems == 0)
            {
                return "готово";
            }

            double secondsPerItem = elapsed.TotalSeconds / processed;
            var remaining = TimeSpan.FromSeconds(secondsPerItem * remainingItems);

            if (remaining.TotalDays >= 1)
            {
                return $"{(int)remaining.TotalDays}д {remaining:hh\\:mm}";
            }

            if (remaining.TotalHours >= 1)
            {
                return remaining.ToString(@"h\:mm\:ss");
            }

            if (remaining.TotalMinutes >= 1)
            {
                return remaining.ToString(@"m\:ss");
            }

            return "<1 мин";
        }

        private void AppendHistoryLog(string message)
        {
            if (string.IsNullOrWhiteSpace(_runLogPath))
            {
                return;
            }

            lock (_runLogLock)
            {
                File.AppendAllText(_runLogPath, $"{DateTime.Now:HH:mm:ss} | {message}\r\n", Encoding.UTF8);
            }
        }

        private static void SafeAppendText(string path, string text)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    lock (_fileLogLock)
                    {
                        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(stream, Encoding.UTF8);
                        writer.Write(text);
                    }
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(150 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(150 * (attempt + 1));
                }
            }

            try
            {
                Directory.CreateDirectory("logs");
                string fallbackPath = Path.Combine("logs", $"file-log-{DateTime.Now:yyyy-MM-dd}.txt");
                using var stream = new FileStream(fallbackPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(text);
            }
            catch
            {
                // Лог не должен останавливать парсинг.
            }
        }

        private void RegisterError(string message)
        {
            Interlocked.Increment(ref _errorCount);
            AppendLog("Ошибка: " + message);
        }

        private void AddProcessedProducts(int count)
        {
            if (count > 0)
            {
                Interlocked.Add(ref _processedProducts, count);
            }
        }

        private void AddChangedPrices(int count)
        {
            if (count > 0)
            {
                Interlocked.Add(ref _changedPrices, count);
            }
        }

        private static bool IsReadyPriceChanged(string productId, decimal readyPrice, IReadOnlyDictionary<string, decimal> oldReadyPrices)
        {
            if (string.IsNullOrWhiteSpace(productId) || readyPrice <= 0)
            {
                return false;
            }

            if (!oldReadyPrices.TryGetValue(productId, out decimal oldReadyPrice) || oldReadyPrice <= 0)
            {
                return true;
            }

            return oldReadyPrice != readyPrice;
        }

        private static decimal? GetOldReadyPriceForLog(IReadOnlyDictionary<string, decimal> oldReadyPrices, string productId)
        {
            if (!string.IsNullOrWhiteSpace(productId) &&
                oldReadyPrices.TryGetValue(productId, out decimal oldReadyPrice) &&
                oldReadyPrice > 0)
            {
                return oldReadyPrice;
            }

            return null;
        }

        private static CompetitorInsight BuildCompetitorInsight(
            string section,
            string productId,
            string productName,
            string hotlineUrl,
            decimal orientirPrice,
            decimal ownPrice,
            IReadOnlyList<Shop> shops,
            AntiDumpingResult antiDumping,
            SoftPriceAdjustmentResult? softPriceDrop = null)
        {
            var competitors = shops
                .Where(shop => shop.Price > 0 && !AntiDumpingService.IsOwnShop(shop.Name))
                .OrderBy(shop => shop.Price)
                .ToList();

            var lowest = competitors.FirstOrDefault();
            var market = antiDumping.MarketShop ?? lowest;
            var nearestLower = competitors
                .Where(shop => ownPrice > 0 && shop.Price < ownPrice)
                .OrderByDescending(shop => shop.Price)
                .FirstOrDefault();
            var nearestUpper = competitors
                .Where(shop => ownPrice > 0 && shop.Price > ownPrice)
                .OrderBy(shop => shop.Price)
                .FirstOrDefault();

            decimal? differenceAmount = null;
            decimal? differencePercent = null;
            decimal? nearestLowerPercent = null;
            decimal? nearestUpperPercent = null;
            bool ownIsHigherThanMarket = false;
            bool canRaisePrice = false;

            if (market != null && market.Price > 0 && ownPrice > 0)
            {
                differenceAmount = ownPrice - market.Price;
                differencePercent = Math.Round(differenceAmount.Value / market.Price * 100m, 2);
                ownIsHigherThanMarket = ownPrice > market.Price;
                canRaisePrice = ownPrice < market.Price - 1;
            }

            if (nearestLower != null && ownPrice > 0)
            {
                nearestLowerPercent = Math.Round((ownPrice - nearestLower.Price) / ownPrice * 100m, 2);
            }

            if (nearestUpper != null && ownPrice > 0)
            {
                nearestUpperPercent = Math.Round((nearestUpper.Price - ownPrice) / ownPrice * 100m, 2);
            }

            int competitorsBelowOwn = ownPrice > 0 ? competitors.Count(shop => shop.Price < ownPrice) : 0;
            int competitorsAboveOwn = ownPrice > 0 ? competitors.Count(shop => shop.Price > ownPrice) : 0;
            int ownRank = ownPrice > 0 ? competitorsBelowOwn + 1 : 0;
            string competitorsBelowOwnText = BuildCompetitorListText(competitors
                .Where(shop => ownPrice > 0 && shop.Price < ownPrice)
                .OrderByDescending(shop => shop.Price));
            string competitorMap = BuildCompetitorListText(competitors);

            string productStatus = "Норма";
            string statusDetails = "цена рядом с рынком";
            if (competitors.Count == 0)
            {
                productStatus = "Нет предложений";
                statusDetails = "на Hotline не найдено конкурентов";
            }
            else if (softPriceDrop?.Applied == true)
            {
                productStatus = "Авто-снижение";
                statusDetails = $"{softPriceDrop.ShopName} ниже на {softPriceDrop.GapPercent:0.##}%, было {softPriceDrop.OldPrice:0}, стало {softPriceDrop.NewPrice:0}";
            }
            else if (antiDumping.IsDumping)
            {
                productStatus = "Демпинг";
                statusDetails = $"{antiDumping.DumpingShop?.Name} ниже рынка на {antiDumping.DumpingPercent:0.##}%";
            }
            else if (ownIsHigherThanMarket)
            {
                productStatus = "Ты выше рынка";
                statusDetails = $"выше ближайшего рынка на {differencePercent:0.##}%";
            }
            else if (canRaisePrice)
            {
                productStatus = "Можно поднять";
                statusDetails = $"рынок выше твоей цены на {Math.Abs(differencePercent ?? 0):0.##}%";
            }

            return new CompetitorInsight
            {
                CheckedAt = DateTime.Now,
                Section = section,
                ProductId = productId,
                ProductName = productName,
                HotlineUrl = hotlineUrl,
                ProductStatus = productStatus,
                StatusDetails = statusDetails,
                OrientirPrice = orientirPrice,
                OwnPrice = ownPrice,
                OffersCount = competitors.Count,
                OwnRank = ownRank,
                CompetitorsBelowOwnCount = competitorsBelowOwn,
                CompetitorsAboveOwnCount = competitorsAboveOwn,
                LowestShop = lowest?.Name ?? string.Empty,
                LowestPrice = lowest?.Price,
                NearestLowerShop = nearestLower?.Name ?? string.Empty,
                NearestLowerPrice = nearestLower?.Price,
                NearestLowerPercent = nearestLowerPercent,
                NearestUpperShop = nearestUpper?.Name ?? string.Empty,
                NearestUpperPrice = nearestUpper?.Price,
                NearestUpperPercent = nearestUpperPercent,
                MarketShop = market?.Name ?? string.Empty,
                MarketPrice = market?.Price,
                DumpingShop = antiDumping.DumpingShop?.Name ?? string.Empty,
                DumpingPrice = antiDumping.DumpingShop?.Price,
                DumpingPercent = antiDumping.IsDumping ? antiDumping.DumpingPercent : null,
                IsDumping = antiDumping.IsDumping,
                SoftPriceDropApplied = softPriceDrop?.Applied == true,
                SoftPriceDropShop = softPriceDrop?.ShopName ?? string.Empty,
                SoftPriceDropFromPrice = softPriceDrop?.Applied == true ? softPriceDrop.OldPrice : null,
                SoftPriceDropToPrice = softPriceDrop?.Applied == true ? softPriceDrop.NewPrice : null,
                SoftPriceDropPercent = softPriceDrop?.Applied == true ? softPriceDrop.GapPercent : null,
                CompetitorsBelowOwn = competitorsBelowOwnText,
                CompetitorMap = competitorMap,
                DifferenceAmount = differenceAmount,
                DifferencePercent = differencePercent,
                OwnIsHigherThanMarket = ownIsHigherThanMarket,
                CanRaisePrice = canRaisePrice
            };
        }

        private static string BuildCompetitorListText(IEnumerable<Shop> shops)
        {
            return string.Join("; ", shops.Select(shop => $"{shop.Name} {shop.Price:0}"));
        }

        private void SetStage(string stage)
        {
            lock (_progressSnapshotLock)
            {
                _progressSnapshot.Stage = stage;
                _progressSnapshot.Section = string.Empty;
                _progressSnapshot.ProductName = string.Empty;
                _progressSnapshot.Note = string.Empty;
                _progressSnapshot.Processed = 0;
                _progressSnapshot.Total = 0;
                _progressSnapshot.Progress = 0;
                _progressSnapshot.Eta = "н/д";
            }

            Dispatcher.InvokeAsync((Action)delegate
            {
                StageText.Text = $"Текущий этап: {stage}";
                EtaText.Text = "н/д";
            });

            AppendHistoryLog("Этап: " + stage);
        }

        private void UpdateProgressStage(
            string section,
            int processed,
            int total,
            string? productName,
            TimeSpan elapsed,
            string? note = null,
            decimal? oldPrice = null,
            decimal? newPrice = null)
        {
            double progress = total > 0 ? processed / (double)total * 100.0 : 0;
            string productText = string.IsNullOrWhiteSpace(productName) ? "" : $" | {productName}";
            string noteText = string.IsNullOrWhiteSpace(note) ? "" : $" | {note}";
            string etaText = FormatEstimatedRemaining(processed, total, elapsed);

            lock (_progressSnapshotLock)
            {
                _progressSnapshot.Stage = $"{section}: {processed}/{total}{productText}";
                _progressSnapshot.Section = section;
                _progressSnapshot.ProductName = productName ?? string.Empty;
                _progressSnapshot.Note = note ?? string.Empty;
                _progressSnapshot.Processed = processed;
                _progressSnapshot.Total = total;
                _progressSnapshot.Progress = progress;
                _progressSnapshot.Elapsed = elapsed;
                _progressSnapshot.Eta = etaText;
            }

            Dispatcher.InvokeAsync((Action)delegate
            {
                StageText.Text = $"Текущий этап: {section}: {processed}/{total}{productText}";
                EtaText.Text = etaText;
                AppendProgressLog(section, processed, total, productName, note, elapsed, oldPrice, newPrice);
                pbStatus.Value = progress;
            });

            AppendHistoryLog($"{section}: {processed}/{total}{productText}{noteText} | {elapsed:hh\\:mm\\:ss}");
        }

        private void SetStatus(string text, string background, string foreground)
        {
            lock (_progressSnapshotLock)
            {
                _progressSnapshot.Status = text;
            }

            StatusText.Text = text;
            StatusBadge.Background = CreateBrush(background);
            StatusText.Foreground = CreateBrush(foreground);
        }

        private static SolidColorBrush CreateBrush(string color)
        {
            return new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(color));
        }

        private static int GetBrowserCount(IConfiguration config)
        {
            if (int.TryParse(config["BrowserCount"], out int browserCount) && browserCount > 0)
            {
                return browserCount;
            }

            if (int.TryParse(config["ThreadCount"], out int legacyThreadCount) && legacyThreadCount > 0)
            {
                return legacyThreadCount;
            }

            return 1;
        }

        private static bool GetBrowserEconomyMode(IConfiguration config)
        {
            return bool.TryParse(config["BrowserEconomyMode"], out bool enabled) && enabled;
        }

        private ManagerHotline? TryCreateBrowserManager(string section, string proxy, string userAgent, string context, bool browserEconomyMode)
        {
            try
            {
                return new ManagerHotline(proxy, userAgent, browserEconomyMode);
            }
            catch (Exception ex)
            {
                RegisterError($"{section}: {context}: {ex.Message}");
                SafeAppendText("logs.txt", $"\n[{section}] {context}. Proxy: {proxy}. Error: {ex.Message}\n{ex.StackTrace}\n");
                return null;
            }
        }

        private static async Task CloseAllBrowser()
        {
            try
            {
                await CloseAllChromium();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
            }
        }

        private void StartTelegramStatusListener()
        {
            if (_telegramStatusTask != null)
            {
                return;
            }

            _telegramStatusCts = new CancellationTokenSource();
            _telegramStatusTask = Task.Run(() => TelegramStatusListenerLoopAsync(_telegramStatusCts.Token));
        }

        private async Task TelegramStatusListenerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", optional: true).Build();
                    bool enabled = !bool.TryParse(config["TelegramStatusEnabled"], out bool statusEnabled) || statusEnabled;
                    string? botToken = config["Bot_Token"];
                    string[] ids = GetTelegramIds(config);

                    if (enabled && !string.IsNullOrWhiteSpace(botToken) && ids.Length > 0 && !botToken.StartsWith("PUT_", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.Equals(_telegramStatusBotToken, botToken, StringComparison.Ordinal))
                        {
                            _telegramStatusBotToken = botToken;
                            _telegramStatusOffset = 0;
                            _telegramStatusInitialized = false;
                        }

                        await PollTelegramStatusCommandsAsync(botToken, ids, cancellationToken);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    SafeAppendText("logs.txt", $"\n[Telegram status] {ex.Message} - {ex.StackTrace}\n");
                }

                try
                {
                    await Task.Delay(TelegramStatusPollInterval, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task PollTelegramStatusCommandsAsync(string botToken, string[] allowedIds, CancellationToken cancellationToken)
        {
            string url = $"https://api.telegram.org/bot{botToken}/getUpdates?offset={_telegramStatusOffset}&timeout=0&limit=20";
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Hotline Parser Telegram Status");

            string responseText = await httpClient.GetStringAsync(url, cancellationToken);
            var response = JObject.Parse(responseText);
            if (response["ok"]?.Value<bool>() != true || response["result"] is not JArray updates)
            {
                return;
            }

            if (!_telegramStatusInitialized)
            {
                if (updates.Count > 0)
                {
                    int lastUpdateId = updates
                        .Select(update => update["update_id"]?.Value<int>() ?? 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    _telegramStatusOffset = lastUpdateId + 1;
                }

                _telegramStatusInitialized = true;
                return;
            }

            foreach (JToken update in updates)
            {
                int updateId = update["update_id"]?.Value<int>() ?? 0;
                if (updateId > 0)
                {
                    _telegramStatusOffset = Math.Max(_telegramStatusOffset, updateId + 1);
                }

                JToken? message = update["message"] ?? update["edited_message"];
                string? text = message?["text"]?.ToString();
                JToken? chat = message?["chat"];
                string? chatId = chat?["id"]?.ToString();

                if (string.IsNullOrWhiteSpace(chatId) || !IsAllowedTelegramChat(chat, allowedIds))
                {
                    continue;
                }

                TelegramCommand command = ParseTelegramCommand(text);
                if (command.Kind == TelegramCommandKind.Unknown)
                {
                    continue;
                }

                if (command.Kind == TelegramCommandKind.Report)
                {
                    string report = CompetitorHistoryStore.BuildMorningReportHtml(DateTime.Now);
                    await SendMessageToChatAsync(report, botToken, chatId, cancellationToken, parseMode: "HTML", disableLinkPreview: true);
                    continue;
                }

                string responseMessage = await ExecuteTelegramCommandAsync(command);
                await SendMessageToChatAsync(responseMessage, botToken, chatId, cancellationToken);
            }
        }

        private static bool IsAllowedTelegramChat(JToken? chat, string[] allowedIds)
        {
            if (chat == null)
            {
                return false;
            }

            var allowed = new HashSet<string>(
                allowedIds.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()),
                StringComparer.OrdinalIgnoreCase);

            string? chatId = chat["id"]?.ToString();
            if (!string.IsNullOrWhiteSpace(chatId) && allowed.Contains(chatId))
            {
                return true;
            }

            string? username = chat["username"]?.ToString();
            if (string.IsNullOrWhiteSpace(username))
            {
                return false;
            }

            return allowed.Contains(username) || allowed.Contains("@" + username);
        }

        private static TelegramCommand ParseTelegramCommand(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Unknown };
            }

            string[] parts = text.Trim().Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string command = parts.FirstOrDefault() ?? string.Empty;
            int botNameIndex = command.IndexOf('@');
            if (botNameIndex >= 0)
            {
                command = command.Substring(0, botNameIndex);
            }

            command = command.Trim().ToLowerInvariant();
            if (command is "/start" or "start" or "/старт" or "старт" or "/запуск" or "запуск")
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Start };
            }

            if (command is "статус" or "стасус" or "/статус" or "/status" or "status")
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Status };
            }

            if (command is "/stop" or "stop" or "/стоп" or "стоп")
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Stop };
            }

            if (command is "/load" or "load" or "/нагрузка" or "нагрузка")
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Load };
            }

            if (command is "/report" or "report" or "/отчет" or "отчет" or "/отчёт" or "отчёт")
            {
                return new TelegramCommand { Kind = TelegramCommandKind.Report };
            }

            if (command is "/pause" or "pause" or "/пауза" or "пауза")
            {
                int pauseMinutes = 10;
                if (parts.Length > 1)
                {
                    string value = parts[1].Trim().ToLowerInvariant();
                    if (value is "off" or "stop" or "стоп" or "выкл")
                    {
                        pauseMinutes = 0;
                    }
                    else if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pauseMinutes))
                    {
                        pauseMinutes = 10;
                    }
                }

                return new TelegramCommand
                {
                    Kind = TelegramCommandKind.Pause,
                    PauseMinutes = Math.Clamp(pauseMinutes, 0, 180)
                };
            }

            return new TelegramCommand { Kind = TelegramCommandKind.Unknown };
        }

        private async Task<string> ExecuteTelegramCommandAsync(TelegramCommand command)
        {
            switch (command.Kind)
            {
                case TelegramCommandKind.Start:
                    return await RequestStartFromTelegramAsync();

                case TelegramCommandKind.Status:
                    return BuildTelegramStatusMessage();

                case TelegramCommandKind.Stop:
                    return await RequestStopFromTelegramAsync();

                case TelegramCommandKind.Pause:
                    return RequestPauseFromTelegram(command.PauseMinutes ?? 10);

                case TelegramCommandKind.Load:
                    return BuildTelegramLoadMessage();

                default:
                    return "Команда не распознана.";
            }
        }

        private async Task<string> RequestStartFromTelegramAsync()
        {
            bool started = await Dispatcher.InvokeAsync(() =>
                TryStartParsing("Telegram: получена команда /start. Парсинг запущен."));

            return started
                ? "Парсинг запущен."
                : "Парсер уже запущен.";
        }

        private async Task<string> RequestStopFromTelegramAsync()
        {
            if (_tokenSource == null || _tokenSource.IsCancellationRequested || !IsParsingActive)
            {
                return "Парсер сейчас не запущен.";
            }

            await RequestImmediateStopAsync("Telegram: получена команда /stop");
            return "Остановка запрошена. Браузеры закрываются сразу.";
        }

        private string RequestPauseFromTelegram(int minutes)
        {
            if (minutes <= 0)
            {
                ClearTelegramPause();
                AppendLog("Telegram: пауза снята.");
                return "Пауза снята.";
            }

            DateTime pauseUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
            lock (_telegramPauseLock)
            {
                _telegramPauseUntilUtc = pauseUntilUtc;
                _lastAnnouncedPauseUntilUtc = null;
            }

            AppendLog($"Telegram: пауза на {minutes} мин., до {pauseUntilUtc.ToLocalTime():HH:mm:ss}.");
            return $"Пауза включена на {minutes} мин.\nДо: {pauseUntilUtc.ToLocalTime():HH:mm:ss}\nНовые товары подождут, текущий товар спокойно завершится.";
        }

        private string BuildTelegramStatusMessage()
        {
            ProgressSnapshot snapshot = GetProgressSnapshot();
            string stageText = snapshot.Total > 0 && !string.IsNullOrWhiteSpace(snapshot.Section)
                ? $"{snapshot.Section}: {snapshot.Processed}/{snapshot.Total}"
                : snapshot.Stage;
            string progressText = snapshot.Total > 0
                ? $"{snapshot.Progress:0}% ({snapshot.Processed}/{snapshot.Total})"
                : "н/д";

            var builder = new StringBuilder();
            builder.AppendLine("Статус Hotline Parser");
            builder.AppendLine($"Этап: {stageText}");
            builder.AppendLine($"Прогресс: {progressText}");
            builder.AppendLine($"Осталось: {(snapshot.Total > 0 ? snapshot.Eta : "н/д")}");

            return builder.ToString().Trim();
        }

        private string BuildTelegramLoadMessage()
        {
            ResourceSnapshot snapshot = GetResourceSnapshot();
            if (!snapshot.Available)
            {
                return "Нагрузка: данные пока недоступны.";
            }

            string temperature = snapshot.TemperatureCelsius.HasValue
                ? $"{snapshot.TemperatureCelsius.Value:0}°C"
                : $"н/д ({snapshot.TemperatureStatus})";

            var builder = new StringBuilder();
            builder.AppendLine("Нагрузка Hotline Parser");
            builder.AppendLine($"ЦП: {snapshot.CpuPercent:0}%");
            builder.AppendLine($"RAM программы: {FormatMemory(snapshot.AppRamBytes)}");
            builder.AppendLine($"Chrome: {snapshot.ChromeCount} / {FormatMemory(snapshot.ChromeRamBytes)}");
            builder.AppendLine($"Температура: {temperature}");
            builder.AppendLine($"Обновлено: {snapshot.SampleUtc.ToLocalTime():HH:mm:ss}");
            return builder.ToString().Trim();
        }

        private ProgressSnapshot GetProgressSnapshot()
        {
            lock (_progressSnapshotLock)
            {
                return new ProgressSnapshot
                {
                    Status = _progressSnapshot.Status,
                    Stage = _progressSnapshot.Stage,
                    Section = _progressSnapshot.Section,
                    ProductName = _progressSnapshot.ProductName,
                    Note = _progressSnapshot.Note,
                    Processed = _progressSnapshot.Processed,
                    Total = _progressSnapshot.Total,
                    Progress = _progressSnapshot.Progress,
                    Elapsed = _progressSnapshot.Elapsed,
                    Eta = _progressSnapshot.Eta
                };
            }
        }

        private ResourceSnapshot GetResourceSnapshot()
        {
            lock (_resourceSnapshotLock)
            {
                return new ResourceSnapshot
                {
                    Available = _resourceSnapshot.Available,
                    CpuPercent = _resourceSnapshot.CpuPercent,
                    AppRamBytes = _resourceSnapshot.AppRamBytes,
                    ChromeRamBytes = _resourceSnapshot.ChromeRamBytes,
                    ChromeCount = _resourceSnapshot.ChromeCount,
                    TemperatureCelsius = _resourceSnapshot.TemperatureCelsius,
                    TemperatureStatus = _resourceSnapshot.TemperatureStatus,
                    SampleUtc = _resourceSnapshot.SampleUtc
                };
            }
        }

        private string GetTelegramPauseText()
        {
            DateTime? pauseUntilUtc = GetTelegramPauseUntilUtc();
            if (!pauseUntilUtc.HasValue)
            {
                return string.Empty;
            }

            TimeSpan remaining = pauseUntilUtc.Value - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                ClearTelegramPause();
                return string.Empty;
            }

            return $"до {pauseUntilUtc.Value.ToLocalTime():HH:mm:ss}, осталось {FormatPauseRemaining(remaining)}";
        }

        private async Task WaitForTelegramPauseAsync(string section, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                DateTime? pauseUntilUtc = GetTelegramPauseUntilUtc();
                if (!pauseUntilUtc.HasValue)
                {
                    return;
                }

                TimeSpan remaining = pauseUntilUtc.Value - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    ClearTelegramPause();
                    if (IsParsingActive)
                    {
                        await Dispatcher.InvokeAsync((Action)delegate
                        {
                            SetStatus("Работает", "#DCFCE7", "#166534");
                        });
                    }
                    return;
                }

                if (ShouldAnnouncePauseStage(pauseUntilUtc.Value))
                {
                    await Dispatcher.InvokeAsync((Action)delegate
                    {
                        SetStatus("Пауза", "#DBEAFE", "#1D4ED8");
                    });
                    SetStage($"{section}: пауза до {pauseUntilUtc.Value.ToLocalTime():HH:mm:ss}");
                    AppendLog($"{section}: пауза до {pauseUntilUtc.Value.ToLocalTime():HH:mm:ss}");
                }

                TimeSpan delay = remaining < TimeSpan.FromSeconds(1)
                    ? remaining
                    : TimeSpan.FromSeconds(1);
                await Task.Delay(delay, cancellationToken);
            }
        }

        private DateTime? GetTelegramPauseUntilUtc()
        {
            lock (_telegramPauseLock)
            {
                return _telegramPauseUntilUtc;
            }
        }

        private bool ShouldAnnouncePauseStage(DateTime pauseUntilUtc)
        {
            lock (_telegramPauseLock)
            {
                if (_lastAnnouncedPauseUntilUtc == pauseUntilUtc)
                {
                    return false;
                }

                _lastAnnouncedPauseUntilUtc = pauseUntilUtc;
                return true;
            }
        }

        private void ClearTelegramPause()
        {
            lock (_telegramPauseLock)
            {
                _telegramPauseUntilUtc = null;
                _lastAnnouncedPauseUntilUtc = null;
            }
        }

        private static string FormatPauseRemaining(TimeSpan remaining)
        {
            if (remaining.TotalHours >= 1)
            {
                return remaining.ToString(@"h\:mm\:ss");
            }

            if (remaining.TotalMinutes >= 1)
            {
                return remaining.ToString(@"m\:ss");
            }

            return "<1 мин";
        }

        private static string TruncateForTelegram(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static string[] GetTelegramIds(IConfiguration config)
        {
            return config.GetSection("Ids")?
                .AsEnumerable()
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .Select(p => p.Value!.Trim())
                .ToArray() ?? Array.Empty<string>();
        }

        private async Task SendTelegramTemplateAsync(string templateKey, string fallbackMessage, ParsingSectionStats? stats = null)
        {
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var token = config["Bot_Token"];
            var ids = GetTelegramIds(config);

            if (string.IsNullOrWhiteSpace(token) || ids.Length == 0)
            {
                return;
            }

            string template = config[templateKey] ?? fallbackMessage;
            string message = BuildTelegramMessage(template, stats);

            await SendMessages(message, token, ids);
        }

        private async Task SendTelegramStopAsync(string templateKey, string fallbackMessage)
        {
            try
            {
                await SendTelegramTemplateAsync(templateKey, fallbackMessage);
            }
            catch (Exception ex)
            {
                AppendLog($"Telegram: не удалось отправить сообщение остановки: {ex.Message}");
                SafeAppendText("logs.txt", $"\n[Telegram stop] {ex.Message} - {ex.StackTrace}\n");
            }
        }

        private static string BuildTelegramMessage(string template, ParsingSectionStats? stats)
        {
            if (stats == null)
            {
                return template;
            }

            return template
                .Replace("{processed}", stats.Processed.ToString(CultureInfo.InvariantCulture))
                .Replace("{changed}", stats.ChangedPrices.ToString(CultureInfo.InvariantCulture))
                .Replace("{errors}", stats.Errors.ToString(CultureInfo.InvariantCulture))
                .Replace("{elapsed}", stats.Elapsed.ToString(@"hh\:mm\:ss"));
        }

        private static async Task SendMessages(string message, string token, string[] ids, string? parseMode = null, bool disableLinkPreview = false)
        {
            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_10_3) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/63.0.3239.132 Safari/537.36");
            foreach (var id in ids)
            {
                var messageUrl = $"https://api.telegram.org/bot{token}/sendMessage";
                var fields = new Dictionary<string, string>
                {
                    ["chat_id"] = id,
                    ["text"] = message
                };

                if (!string.IsNullOrWhiteSpace(parseMode))
                {
                    fields["parse_mode"] = parseMode;
                }

                if (disableLinkPreview)
                {
                    fields["link_preview_options"] = "{\"is_disabled\":true}";
                }

                var postData = new FormUrlEncodedContent(fields);

                var resp = await httpClient.PostAsync(messageUrl, postData);
                resp.EnsureSuccessStatusCode();
            }
        }

        private static async Task SendMessageToChatAsync(
            string message,
            string token,
            string chatId,
            CancellationToken cancellationToken,
            string? parseMode = null,
            bool disableLinkPreview = false)
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Hotline Parser Telegram Status");
            var messageUrl = $"https://api.telegram.org/bot{token}/sendMessage";
            var fields = new Dictionary<string, string>
            {
                ["chat_id"] = chatId,
                ["text"] = message
            };

            if (!string.IsNullOrWhiteSpace(parseMode))
            {
                fields["parse_mode"] = parseMode;
            }

            if (disableLinkPreview)
            {
                fields["link_preview_options"] = "{\"is_disabled\":true}";
            }

            var postData = new FormUrlEncodedContent(fields);

            var resp = await httpClient.PostAsync(messageUrl, postData, cancellationToken);
            resp.EnsureSuccessStatusCode();
        }

        private async Task SendMorningReportIfNeededAsync()
        {
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();

            if (!bool.TryParse(config["MorningReportEnabled"], out bool enabled) || !enabled)
            {
                return;
            }

            if (!TimeSpan.TryParse(config["MorningReportTime"], out TimeSpan reportTime))
            {
                reportTime = TimeSpan.FromHours(9);
            }

            if (DateTime.Now.TimeOfDay < reportTime)
            {
                return;
            }

            string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.Equals(config["MorningReportLastSentDate"], today, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var token = config["Bot_Token"];
            var ids = GetTelegramIds(config);
            if (string.IsNullOrWhiteSpace(token) || ids.Length == 0)
            {
                return;
            }

            string report = CompetitorHistoryStore.BuildMorningReportHtml(DateTime.Now);
            await SendMessages(report, token, ids, parseMode: "HTML", disableLinkPreview: true);
            SaveAppSetting("MorningReportLastSentDate", today);
            AppendLog("Утренний Telegram-отчет отправлен.");
        }

        private static void SaveAppSetting(string key, string value)
        {
            string path = "appsettings.json";
            var jsonObj = JObject.Parse(File.ReadAllText(path));
            jsonObj[key] = value;
            File.WriteAllText(path, JsonConvert.SerializeObject(jsonObj, Formatting.Indented), Encoding.UTF8);
        }

        private async Task<ParsingSectionStats> Default(CancellationToken cancellationToken)
        {
            var stats = new ParsingSectionStats();
            SetStage("смартфоны: очищаю старые временные файлы");
            // Удаляем старые файлы логов при запуске
            File.Delete("brokenLinks.txt");
            File.Delete("brokenProxy.txt");

            SetStage("смартфоны: закрываю браузер");
            await CloseAllBrowser(); // Ваш метод для закрытия браузеров

            try
            {
                SetStage("смартфоны: читаю настройки и Google таблицу");
                var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                var antiDumpingSettings = AntiDumpingSettings.FromConfig(config);

                var spreadSheetManager = new Hotline_Main_Parsing.@default.SpreadSheetManager(config["HotlineSpreadSheetId_default"]!,
                                                                                             config["BitSpreadSheetId_default"]!,
                                                                                             config["KYMPromSpreadSheetId_default"]!,
                                                                                             config["HotlineSpreadSheetId_default_editor"]!,
                                                                                             config["1UaPromSpreadSheetId_default"]!,
                                                                                             config["StokPromSpreadSheetId_default"]!,
                                                                                             config["SmilePromSpreadSheetId_default"]!);

                var dicSymbols = spreadSheetManager.GetSymbols();
                var priceOrientir = config["PriceOrientir"].ToString();
                var RRCPrice = config["RRCPrice"].ToString();

                object log_lock = new object();
                object _brokenLinksLock = new object();

                var allData = spreadSheetManager.GetData();
                // Проверка, что в таблице есть данные для парсинга
                if (allData.Values == null || allData.Values.Count < 3)
                {
                    AppendLog("Смартфоны: нет данных для обработки в таблице.");
                    return stats;
                }
                int percent = int.Parse(allData.Values[0][11].ToString());

                var sw = new Stopwatch();
                sw.Start();

                var managers = new ConcurrentQueue<ManagerHotline>();
                int browserCount = GetBrowserCount(config);
                bool browserEconomyMode = GetBrowserEconomyMode(config);
                var proxys = File.ReadAllLines("proxy_default.txt").ToList();
                var agent = File.ReadAllLines("agent.txt").ToList();
                AppendLog($"Смартфоны: будет открыто браузеров: {browserCount}");
                AppendLog($"Смартфоны: эконом-режим браузера: {(browserEconomyMode ? "включен" : "выключен")}");
                if (proxys.Count == 0 || agent.Count == 0)
                {
                    AppendLog("Смартфоны: нет прокси или User-Agent для запуска браузера.");
                    return stats;
                }

                // Инициализация пула менеджеров (браузеров)
                for (int i = 0; i < browserCount; ++i)
                {
                    AppendLog($"Смартфоны: открываю браузер {i + 1}/{browserCount}");
                    var manager = TryCreateBrowserManager("Смартфоны", proxys[i % proxys.Count], agent[i % agent.Count], $"браузер {i + 1}/{browserCount} не открылся", browserEconomyMode);
                    if (manager != null)
                    {
                        managers.Enqueue(manager);
                        AppendLog($"Смартфоны: браузер {i + 1}/{browserCount} открыт");
                    }
                }
                AppendLog($"Смартфоны: открыто браузеров: {managers.Count}");
                if (managers.IsEmpty)
                {
                    AppendLog("Смартфоны: ни один браузер не открылся, раздел остановлен.");
                    return stats;
                }

                var productsInSheet = new ConcurrentBag<Hotline_Main_Parsing.@default.ProductInSheet>();
                var competitorInsights = new ConcurrentBag<CompetitorInsight>();
                int switchedParseMarks = 0;

                // Читаем старые цены из таблиц для сохранения при пропуске
                var oldReadyPrices = new Dictionary<string, decimal>();
                for (int i = 2; i < allData.Values.Count; i++)
                {
                    var row = allData.Values[i];
                    var id = row.ElementAtOrDefault(1)?.ToString();
                    var rpStr = row.ElementAtOrDefault(5)?.ToString()?.Replace(" ", "").Replace(" грн.", "");
                    if (!string.IsNullOrEmpty(id) && decimal.TryParse(rpStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal rp) && rp > 0)
                        oldReadyPrices[id] = rp;
                }
                Dictionary<string, decimal> oldBitPrices;
                try { oldBitPrices = spreadSheetManager.GetCurrentBitPrices(); }
                catch { oldBitPrices = new Dictionary<string, decimal>(); }

                var rnd = new Random();

                // Создаем список индексов для обработки. Начинаем с 2, как в вашем коде.
                var indexes = Enumerable.Range(2, allData.Values.Count - 2).ToList();

                // Основной цикл обработки в параллельном режиме
                SetStage($"смартфоны: парсю товары 0/{indexes.Count}");
                await Parallel.ForEachAsync(indexes, new ParallelOptions() { MaxDegreeOfParallelism = managers.Count, CancellationToken = cancellationToken }, async (productIndex, token) =>
                {
                    ManagerHotline managerHotline = null;
                    try
                    {
                        await WaitForTelegramPauseAsync("смартфоны", token);
                        if (!managers.TryDequeue(out managerHotline))
                        {
                            Console.WriteLine("Warning: Manager queue was temporarily empty!");
                            await Task.Delay(100, token);
                            return;
                        }

                        var data = allData.Values[productIndex];
                        var productId = GetSheetCell(data, 1);
                        var productName = GetSheetCell(data, 2);
                        if (string.IsNullOrEmpty(productId) || productsInSheet.Any(p => p.Id == productId))
                        {
                            return;
                        }
                        decimal? oldReadyPriceForLog = GetOldReadyPriceForLog(oldReadyPrices, productId);

                        bool useOrientirAsResult = ShouldUseOrientirAsResult(data);

                        // Проверка наличия: с 05:00 до 22:00 парсим только товары с H = "+"
                        string availability = (string?)data.ElementAtOrDefault(7) ?? "";
                        if (!IsNightTime() && availability != "+")
                        {
                            var skipped = new Hotline_Main_Parsing.@default.ProductInSheet();
                            skipped.Id = productId;
                            skipped.PriceAvailableness = availability;
                            skipped.Url = GetHotlineUrl(data);
                            if (TryParseSheetPrice((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]), out decimal sp))
                                skipped.Price = sp;
                            skipped.BitPrice = oldBitPrices.TryGetValue(productId, out decimal sob) && sob > 0 ? sob : skipped.Price;
                            skipped.ReadyPrice = oldReadyPrices.TryGetValue(productId, out decimal sor) && sor > 0 ? sor : skipped.Price;
                            ApplyOrientirAsResultIfNeeded(skipped, useOrientirAsResult);
                            await ApplyRrcBitPriceIfNeeded(skipped, data, dicSymbols, RRCPrice);
                            productsInSheet.Add(skipped);
                            UpdateProgressStage("Смартфоны", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, "нету в наличии", oldReadyPriceForLog, skipped.ReadyPrice);
                            return;
                        }

                        // Цикл повторных попыток с ограничением по времени (10-20 минут)
                        var retryTimeout = TimeSpan.FromMinutes(rnd.Next(10, 21));
                        var retrySw = Stopwatch.StartNew();
                        int currentProxyIndex = Math.Max(0, proxys.IndexOf(managerHotline.proxy));
                        bool success = false;
                        bool noOffers = false;

                        while (!success && retrySw.Elapsed < retryTimeout && !token.IsCancellationRequested)
                        {
                            if (token.IsCancellationRequested) return;

                            try
                            {
                                var productInSheet = new Hotline_Main_Parsing.@default.ProductInSheet();
                                productInSheet.Id = productId;
                                productInSheet.PriceAvailableness = (string)data.ElementAtOrDefault(dicSymbols[RRCPrice]);
                                productInSheet.Price = decimal.Parse(((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]) ?? "-").Replace(" грн.", "").Replace(" ", ""));
                                // Используем старые цены как начальное значение (сохраняются если парсинг не удался)
                                productInSheet.BitPrice = oldBitPrices.TryGetValue(productId, out decimal ob) && ob > 0 ? ob : productInSheet.Price;
                                productInSheet.ReadyPrice = oldReadyPrices.TryGetValue(productId, out decimal oldRp) && oldRp > 0 ? oldRp : productInSheet.Price;
                                productInSheet.Url = GetHotlineUrl(data);
                                productInSheet.ParseMarkOld = (string)data[9] == "TRUE";
                                productInSheet.ParseMarkNew = (string)data[10] == "TRUE";
                                productInSheet.PriceAvailableness = (string?)data[7];
                                ApplyOrientirAsResultIfNeeded(productInSheet, useOrientirAsResult);

                                if (string.IsNullOrEmpty(productInSheet.Url))
                                {
                                    await ApplyRrcBitPriceIfNeeded(productInSheet, data, dicSymbols, RRCPrice);
                                    productsInSheet.Add(productInSheet);
                                    UpdateProgressStage("Смартфоны", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, "пропуск: нет ссылки", oldReadyPriceForLog, productInSheet.ReadyPrice);
                                    return;
                                }

                                var buyPriceGRNString = ((string?)data.ElementAtOrDefault(12) ?? "-").Replace(" грн.", "").Replace(" ", "");
                                if (decimal.TryParse(buyPriceGRNString, out decimal buyPrice))
                                {
                                    productInSheet.BuyPriceInGRN = buyPrice;
                                }

                                Product product = await ParseProduct(managerHotline, productInSheet.Url, "proxy_default.txt");

                                if (product.Shops.Count == 0)
                                {
                                    competitorInsights.Add(BuildCompetitorInsight(
                                        "Смартфоны",
                                        productId,
                                        productName,
                                        productInSheet.Url,
                                        productInSheet.Price,
                                        productInSheet.ReadyPrice,
                                        product.Shops,
                                        new AntiDumpingResult()));
                                    // Товара нет на Hotline — выходим сразу, смена прокси не поможет
                                    noOffers = true;
                                    break;
                                }

                                productInSheet.OffersCount = product.Shops.Count;
                                Shop technoBitShop = product.Shops.Find(s => s.Name == "TEHNO-BIT.COM.UA");
                                productInSheet.TehnoBit = technoBitShop != null ? '+' : '-';

                                Shop UA1 = product.Shops.Find(s => s.Name == "1UA.IN");
                                productInSheet.Ua_1 = UA1 != null ? '+' : '-';

                                var shops = product.Shops.DistinctBy(s => s.Price).ToList();
                                if (technoBitShop != null && !shops.Any(s => s.Name == "TEHNO-BIT.COM.UA"))
                                {
                                    shops.Add(technoBitShop);
                                }
                                shops = shops.OrderBy(s => s.Price).ToList();

                                var antiDumping = AntiDumpingService.Analyze(shops, antiDumpingSettings);
                                if (antiDumping.IsDumping)
                                {
                                    AppendLog($"Антидемпинг: {productId} - игнорирую {antiDumping.DumpingShop?.Name} {antiDumping.DumpingShop?.Price:0} грн, ниже рынка на {antiDumping.DumpingPercent:0.##}%.");
                                }

                                Hotline_Main_Parsing.@default.PriceCalculator.CalculatePrices(productInSheet, antiDumping.ShopsForPrice, percent);
                                var softPriceDrop = ApplySoftCompetitorPriceDrop(productInSheet, shops, percent);
                                if (softPriceDrop.Applied)
                                {
                                    AppendLog($"Автоцена: {productId} - {softPriceDrop.ShopName} ниже нас на {softPriceDrop.GapPercent:0.##}%, ставлю {softPriceDrop.NewPrice:0} грн вместо {softPriceDrop.OldPrice:0}.");
                                }

                                if (SwitchParseMarkOldToNewIfNeeded(productInSheet))
                                {
                                    Interlocked.Increment(ref switchedParseMarks);
                                }

                                competitorInsights.Add(BuildCompetitorInsight(
                                    "Смартфоны",
                                    productId,
                                    productName,
                                    productInSheet.Url,
                                    productInSheet.Price,
                                    productInSheet.ReadyPrice,
                                    shops,
                                    antiDumping,
                                    softPriceDrop));

                                await ApplyRrcBitPriceIfNeeded(productInSheet, data, dicSymbols, RRCPrice);

                                productsInSheet.Add(productInSheet);
                                success = true;

                                double fVal = (double)productsInSheet.Count;
                                double sVal = (double)indexes.Count;
                                double proc = sVal > 0 ? ((fVal / sVal) * 100.0) : 0;

                                UpdateProgressStage("Смартфоны", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, null, oldReadyPriceForLog, productInSheet.ReadyPrice);
                            }
                            catch (Exception ex)
                            {
                                if (token.IsCancellationRequested || IsCancellationException(ex))
                                {
                                    return;
                                }

                                RegisterError($"Смартфоны: товар {productIndex}: {ex.Message}");
                                lock (log_lock)
                                {
                                    File.AppendAllText("logs.txt", $"\n[Retry {retrySw.Elapsed:mm\\:ss}/{retryTimeout.TotalMinutes:0}min] ProductIndex: {productIndex}, Proxy: {managerHotline?.proxy}, Error: {ex.Message}\n");
                                }

                                if (ex.InnerException?.Message.Contains("ERR_PROXY_CONNECTION_FAILED") ?? false)
                                {
                                    if (!CheckForInternetConnection())
                                    {
                                        _tokenSource.Cancel();
                                        return;
                                    }
                                }

                                string currentUserAgent = managerHotline?.userAgent ?? agent[currentProxyIndex % agent.Count];
                                await (managerHotline?.CloseBrowser() ?? Task.CompletedTask);
                                managerHotline = null;

                                // Переключаемся на следующий прокси по кругу, но не зависаем на открытии Chrome.
                                bool replacementOpened = false;
                                for (int proxyAttempt = 0; proxyAttempt < proxys.Count && !token.IsCancellationRequested; proxyAttempt++)
                                {
                                    currentProxyIndex = (currentProxyIndex + 1) % proxys.Count;
                                    managerHotline = TryCreateBrowserManager("Смартфоны", proxys[currentProxyIndex], currentUserAgent, $"браузер после смены прокси {currentProxyIndex + 1}/{proxys.Count} не открылся", browserEconomyMode);
                                    if (managerHotline != null)
                                    {
                                        replacementOpened = true;
                                        break;
                                    }
                                }

                                if (!replacementOpened)
                                {
                                    AppendLog("Смартфоны: не удалось открыть браузер после смены прокси, оставляю старую цену.");
                                    break;
                                }

                                // Пауза 30 сек перед следующей попыткой (IP обновляется раз в 1 мин)
                                try { await Task.Delay(30_000, token); } catch (OperationCanceledException) { return; }
                            }
                        } // Конец while

                        // Таймаут истёк — добавляем товар со старыми ценами
                        if (!success && !token.IsCancellationRequested)
                        {
                            var fallback = new Hotline_Main_Parsing.@default.ProductInSheet();
                            fallback.Id = productId;
                            fallback.PriceAvailableness = (string?)data.ElementAtOrDefault(7);
                            fallback.Url = GetHotlineUrl(data);
                            if (TryParseSheetPrice((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]), out decimal fp))
                                fallback.Price = fp;
                            fallback.BitPrice = oldBitPrices.TryGetValue(productId, out decimal fob) && fob > 0 ? fob : fallback.Price;
                            fallback.ReadyPrice = oldReadyPrices.TryGetValue(productId, out decimal for2) && for2 > 0 ? for2 : fallback.Price;
                            ApplyOrientirAsResultIfNeeded(fallback, useOrientirAsResult);
                            await ApplyRrcBitPriceIfNeeded(fallback, data, dicSymbols, RRCPrice);
                            if (!noOffers) // Пишем в brokenLinks только при таймауте прокси, не при отсутствии товара
                            {
                                lock (_brokenLinksLock)
                                {
                                    File.AppendAllText("brokenLinks.txt", fallback.Id + " - " + fallback.Url + "\n");
                                }
                            }
                            productsInSheet.Add(fallback);
                            UpdateProgressStage("Смартфоны", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, noOffers ? "нет предложений на Hotline" : "таймаут/ошибка, оставил старую цену", oldReadyPriceForLog, fallback.ReadyPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested || IsCancellationException(ex))
                        {
                            return;
                        }

                        RegisterError($"Смартфоны: товар {productIndex}: {ex.Message}");
                        // Глобальный обработчик ошибок для всей задачи (если что-то пошло не так вне цикла попыток)
                        lock (log_lock)
                        {
                            File.AppendAllText("logs.txt", $"\n[FATAL] ProductIndex = {productIndex} Error: {ex.Message}\n{ex.StackTrace}\n");
                        }
                    }
                    finally
                    {
                        // !!! КЛЮЧЕВОЕ ИЗМЕНЕНИЕ !!!
                        // Этот блок выполнится ВСЕГДА. Гарантируем, что менеджер вернется в очередь.
                        if (managerHotline != null)
                        {
                            managers.Enqueue(managerHotline);
                        }
                    }
                });

                if (cancellationToken.IsCancellationRequested)
                {
                    AppendLog("Смартфоны: остановлено пользователем, неполный результат не записываю.");
                    return stats;
                }

                // Этот код теперь должен выполниться без зависания
                File.WriteAllText("mock.json", JsonConvert.SerializeObject(productsInSheet.ToList()));
                AppendLog("Смартфоны: не обработано - " + (indexes.Count - productsInSheet.Count));
                int changedPrices = productsInSheet.Count(product => IsReadyPriceChanged(product.Id, product.ReadyPrice, oldReadyPrices));
                AddProcessedProducts(productsInSheet.Count);
                AddChangedPrices(changedPrices);
                stats.Processed = productsInSheet.Count;
                stats.ChangedPrices = changedPrices;
                stats.Elapsed = sw.Elapsed;
                AppendLog($"Смартфоны: цен сменилось {changedPrices}");
                if (switchedParseMarks > 0)
                {
                    AppendLog($"Смартфоны: J -> K переключено: {switchedParseMarks}");
                }

                // Очищаем оставшиеся в очереди браузеры
                SetStage("смартфоны: закрываю браузеры");
                foreach (var manager in managers)
                {
                    await (manager?.CloseBrowser() ?? Task.CompletedTask);
                }

                SetStage("смартфоны: записываю результат в Google Sheets");
                spreadSheetManager.UploadDataToTables(productsInSheet);
                CompetitorHistoryStore.SaveInsights(competitorInsights);
                AppendLog($"Смартфоны: история конкурентов обновлена, записей: {competitorInsights.Count}");
                SetStage($"смартфоны: готово, обработано {productsInSheet.Count}");
                sw.Stop();
                stats.Elapsed = sw.Elapsed;
                AppendLog($"Смартфоны: время обработки {sw.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested || IsCancellationException(e))
                {
                    AppendLog("Смартфоны: остановлено пользователем.");
                    return stats;
                }

                RegisterError($"Смартфоны: {e.Message}");
                // Глобальный обработчик ошибок всего метода Default
                Console.WriteLine($"\n[GLOBAL ERROR] {e.Message}\n{e.StackTrace}");
                File.AppendAllText("logs.txt", $"\n[GLOBAL ERROR] {e.Message} - {e.StackTrace}");
            }
            finally
            {
                // Финальная очистка всех процессов хрома
                await CloseAllChromium();
            }

            return stats;
        }

        private static bool CheckForInternetConnection(int timeoutMs = 10000, string url = "https://www.google.com/")
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.KeepAlive = false;
                request.Timeout = timeoutMs;
                using (var response = (HttpWebResponse)request.GetResponse())
                    return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNightTime()
        {
            TimeSpan current = DateTime.Now.TimeOfDay;
            // Ночное время: с 22:00 до 05:00
            return current >= TimeSpan.FromHours(22) || current < TimeSpan.FromHours(5);
        }

        private static async Task<bool> TimeOut()
        {
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            // Указанное время для сравнения (без даты)
            TimeSpan timeStart = TimeSpan.Parse(config["timeStart"]);
            TimeSpan timeStop = TimeSpan.Parse(config["timeStop"]);

            // Получение текущего времени (без даты)
            TimeSpan currentTime = DateTime.Now.TimeOfDay;
            // Получение текущего дня недели
            DayOfWeek currentDayOfWeek = DateTime.Now.DayOfWeek;
            bool parse = false;

            if (currentDayOfWeek != DayOfWeek.Sunday && currentDayOfWeek != DayOfWeek.Saturday)
            {
                if (currentTime >= timeStart && currentTime <= timeStop)
                {
                    parse = true;
                }
            }

            return parse;
        }

        private async Task<ParsingSectionStats> Aks(CancellationToken cancellationToken)
        {
            var stats = new ParsingSectionStats();
            SetStage("аксессуары: очищаю старые временные файлы");
            Dictionary<string, string> lastPluses = null;
            File.Delete("brokenLinks.txt");
            File.Delete("brokenProxy.txt");

            try
            {
                SetStage("аксессуары: закрываю браузер");
                await CloseAllChromium();
            }
            catch (Exception e)
            {
                RegisterError($"Аксессуары: закрытие браузера: {e.Message}");
                Console.WriteLine(e.Message);
            }

            try
            {
                SetStage("аксессуары: читаю настройки и Google таблицу");
                var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                var antiDumpingSettings = AntiDumpingSettings.FromConfig(config);

                var spreadSheetManager = new Hotline_Main_Parsing.aks.SpreadSheetManager(config["HotlineSpreadSheetId_aks"]!, config["BitSpreadSheetId_aks"]!);

                var dicSymbols = spreadSheetManager.GetSymbols();
                var priceOrientir = config["PriceOrientir"].ToString();
                var RRCPrice = config["RRCPrice"].ToString();

                object log_lock = new object();
                object _brokenLinksLock = new object();

                var allData = spreadSheetManager.GetData();
                if (allData.Values == null || allData.Values.Count < 3)
                {
                    AppendLog("Аксессуары: нет данных для обработки в таблице.");
                    return stats;
                }
                int percent = int.Parse(allData.Values[0][11].ToString());

                var sw = new Stopwatch();
                sw.Start();

                // ИСПОЛЬЗУЕМ ПОТОКОБЕЗОПАСНУЮ ОЧЕРЕДЬ
                var managers = new ConcurrentQueue<ManagerHotline>();
                int browserCount = GetBrowserCount(config);
                bool browserEconomyMode = GetBrowserEconomyMode(config);
                var proxys = File.ReadAllLines("proxy_aks.txt").ToList();
                var agent = File.ReadAllLines("agent.txt").ToList();
                AppendLog($"Аксессуары: будет открыто браузеров: {browserCount}");
                AppendLog($"Аксессуары: эконом-режим браузера: {(browserEconomyMode ? "включен" : "выключен")}");
                if (proxys.Count == 0 || agent.Count == 0)
                {
                    AppendLog("Аксессуары: нет прокси или User-Agent для запуска браузера.");
                    return stats;
                }

                for (int i = 0; i < browserCount; ++i)
                {
                    AppendLog($"Аксессуары: открываю браузер {i + 1}/{browserCount}");
                    var manager = TryCreateBrowserManager("Аксессуары", proxys[i % proxys.Count], agent[i % agent.Count], $"браузер {i + 1}/{browserCount} не открылся", browserEconomyMode);
                    if (manager != null)
                    {
                        managers.Enqueue(manager);
                        AppendLog($"Аксессуары: браузер {i + 1}/{browserCount} открыт");
                    }
                }
                AppendLog($"Аксессуары: открыто браузеров: {managers.Count}");
                if (managers.IsEmpty)
                {
                    AppendLog("Аксессуары: ни один браузер не открылся, раздел остановлен.");
                    return stats;
                }

                // ИСПОЛЬЗУЕМ ПОТОКОБЕЗОПАСНУЮ КОЛЛЕКЦИЮ ДЛЯ РЕЗУЛЬТАТОВ
                var productsInSheet = new ConcurrentBag<Hotline_Main_Parsing.aks.ProductInSheet>();
                var competitorInsights = new ConcurrentBag<CompetitorInsight>();
                int switchedParseMarks = 0;

                // Читаем старые цены из таблиц для сохранения при пропуске
                var oldReadyPricesAks = new Dictionary<string, decimal>();
                for (int i = 2; i < allData.Values.Count; i++)
                {
                    var row = allData.Values[i];
                    var id = row.ElementAtOrDefault(1)?.ToString();
                    var rpStr = row.ElementAtOrDefault(5)?.ToString()?.Replace(" ", "").Replace(" грн.", "");
                    if (!string.IsNullOrEmpty(id) && decimal.TryParse(rpStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal rp) && rp > 0)
                        oldReadyPricesAks[id] = rp;
                }
                Dictionary<string, decimal> oldBitPricesAks;
                try { oldBitPricesAks = spreadSheetManager.GetCurrentBitPrices(); }
                catch { oldBitPricesAks = new Dictionary<string, decimal>(); }

                var rndAks = new Random();

                var indexes = Enumerable.Range(2, allData.Values.Count - 2).ToList();

                SetStage($"аксессуары: парсю товары 0/{indexes.Count}");
                await Parallel.ForEachAsync(indexes, new ParallelOptions() { MaxDegreeOfParallelism = managers.Count, CancellationToken = cancellationToken }, async (productIndex, token) =>
                {
                    ManagerHotline managerHotline = null;
                    try
                    {
                        await WaitForTelegramPauseAsync("аксессуары", token);
                        if (!managers.TryDequeue(out managerHotline))
                        {
                            Console.WriteLine("Warning: Manager queue was temporarily empty!");
                            await Task.Delay(100, token);
                            return;
                        }

                        var data = allData.Values[productIndex];
                        var productId = GetSheetCell(data, 1);
                        var productName = GetSheetCell(data, 2);
                        if (string.IsNullOrEmpty(productId) || productsInSheet.Any(p => p.Id == productId))
                        {
                            return;
                        }
                        decimal? oldReadyPriceForLog = GetOldReadyPriceForLog(oldReadyPricesAks, productId);

                        bool useOrientirAsResult = ShouldUseOrientirAsResult(data);

                        // Проверка наличия: с 05:00 до 22:00 парсим только товары с H = "+"
                        string availabilityAks = (string?)data.ElementAtOrDefault(7) ?? "";
                        if (!IsNightTime() && availabilityAks != "+")
                        {
                            var skipped = new Hotline_Main_Parsing.aks.ProductInSheet();
                            skipped.Id = productId;
                            skipped.PriceAvailableness = availabilityAks;
                            skipped.Url = GetHotlineUrl(data);
                            if (TryParseSheetPrice((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]), out decimal sp))
                                skipped.Price = sp;
                            skipped.BitPrice = oldBitPricesAks.TryGetValue(productId, out decimal sob) && sob > 0 ? sob : skipped.Price;
                            skipped.ReadyPrice = oldReadyPricesAks.TryGetValue(productId, out decimal sor) && sor > 0 ? sor : skipped.Price;
                            ApplyOrientirAsResultIfNeeded(skipped, useOrientirAsResult);
                            await ApplyRrcBitPriceIfNeeded(skipped, data, dicSymbols, RRCPrice);
                            productsInSheet.Add(skipped);
                            UpdateProgressStage("Аксессуары", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, "нету в наличии", oldReadyPriceForLog, skipped.ReadyPrice);
                            return;
                        }

                        // Цикл повторных попыток с ограничением по времени (10-20 минут)
                        var retryTimeout = TimeSpan.FromMinutes(rndAks.Next(10, 21));
                        var retrySw = Stopwatch.StartNew();
                        int currentProxyIndex = Math.Max(0, proxys.IndexOf(managerHotline.proxy));
                        bool success = false;
                        bool noOffers = false;

                        while (!success && retrySw.Elapsed < retryTimeout && !token.IsCancellationRequested)
                        {
                            if (token.IsCancellationRequested) return;

                            try
                            {
                                var productInSheet = new Hotline_Main_Parsing.aks.ProductInSheet();
                                productInSheet.Id = productId;

                                productInSheet.PriceAvailableness = (string)data.ElementAtOrDefault(dicSymbols[RRCPrice]);
                                if (string.IsNullOrEmpty((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir])))
                                {
                                    return;
                                }
                                productInSheet.Price = decimal.Parse(((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]) ?? "-").Replace(" грн.", "").Replace(" ", ""));
                                // Используем старые цены как начальное значение (сохраняются если парсинг не удался)
                                productInSheet.BitPrice = oldBitPricesAks.TryGetValue(productId, out decimal ob) && ob > 0 ? ob : productInSheet.Price;
                                productInSheet.ReadyPrice = oldReadyPricesAks.TryGetValue(productId, out decimal oldRp) && oldRp > 0 ? oldRp : productInSheet.Price;
                                productInSheet.Url = GetHotlineUrl(data);
                                productInSheet.ParseMarkOld = (string)data[9] == "TRUE";
                                productInSheet.ParseMarkNew = (string)data[10] == "TRUE";
                                productInSheet.ParseMarkRRC = IsCheckedCell(data, 11);
                                productInSheet.PriceAvailableness = (string?)data[7];
                                ApplyOrientirAsResultIfNeeded(productInSheet, useOrientirAsResult);

                                if (string.IsNullOrEmpty(productInSheet.Url))
                                {
                                    await ApplyRrcBitPriceIfNeeded(productInSheet, data, dicSymbols, RRCPrice);
                                    productsInSheet.Add(productInSheet);
                                    UpdateProgressStage("Аксессуары", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, "пропуск: нет ссылки", oldReadyPriceForLog, productInSheet.ReadyPrice);
                                    return;
                                }

                                var buyPriceGRNString = ((string?)data.ElementAtOrDefault(12) ?? "-").Replace(" грн.", "").Replace(" ", "");
                                if (decimal.TryParse(buyPriceGRNString, out decimal buyPrice))
                                {
                                    productInSheet.BuyPriceInGRN = buyPrice;
                                }

                                Hotline_Main_Parsing.common.Product product = await ParseProduct(managerHotline, productInSheet.Url, "proxy_aks.txt");

                                if (product.Shops.Count == 0)
                                {
                                    competitorInsights.Add(BuildCompetitorInsight(
                                        "Аксессуары",
                                        productId,
                                        productName,
                                        productInSheet.Url,
                                        productInSheet.Price,
                                        productInSheet.ReadyPrice,
                                        product.Shops,
                                        new AntiDumpingResult()));
                                    // Товара нет на Hotline — выходим сразу, смена прокси не поможет
                                    noOffers = true;
                                    break;
                                }

                                productInSheet.OffersCount = product.Shops.Count;
                                Shop technoBitShop = product.Shops.Find(s => s.Name == "TEHNO-BIT.COM.UA");
                                productInSheet.TehnoBit = technoBitShop != null ? '+' : '-';
                                Shop UA1 = product.Shops.Find(s => s.Name == "1UA.IN");
                                productInSheet.Ua_1 = UA1 != null ? '+' : '-';
                                var shops = product.Shops.DistinctBy(s => s.Price).ToList();
                                if (technoBitShop != null && !shops.Any(s => s.Name == "TEHNO-BIT.COM.UA"))
                                {
                                    shops.Add(technoBitShop);
                                }
                                shops = shops.OrderBy(s => s.Price).ToList();

                                var antiDumping = AntiDumpingService.Analyze(shops, antiDumpingSettings);
                                if (antiDumping.IsDumping)
                                {
                                    AppendLog($"Антидемпинг АКС: {productId} - игнорирую {antiDumping.DumpingShop?.Name} {antiDumping.DumpingShop?.Price:0} грн, ниже рынка на {antiDumping.DumpingPercent:0.##}%.");
                                }

                                Hotline_Main_Parsing.aks.PriceCalculator.CalculatePrices(productInSheet, antiDumping.ShopsForPrice, percent);
                                var softPriceDrop = ApplySoftCompetitorPriceDrop(productInSheet, shops, percent);
                                if (softPriceDrop.Applied)
                                {
                                    AppendLog($"Автоцена АКС: {productId} - {softPriceDrop.ShopName} ниже нас на {softPriceDrop.GapPercent:0.##}%, ставлю {softPriceDrop.NewPrice:0} грн вместо {softPriceDrop.OldPrice:0}.");
                                }

                                if (SwitchParseMarkOldToNewIfNeeded(productInSheet))
                                {
                                    Interlocked.Increment(ref switchedParseMarks);
                                }

                                competitorInsights.Add(BuildCompetitorInsight(
                                    "Аксессуары",
                                    productId,
                                    productName,
                                    productInSheet.Url,
                                    productInSheet.Price,
                                    productInSheet.ReadyPrice,
                                    shops,
                                    antiDumping,
                                    softPriceDrop));

                                await ApplyRrcBitPriceIfNeeded(productInSheet, data, dicSymbols, RRCPrice);

                                productsInSheet.Add(productInSheet);
                                success = true;

                                UpdateProgressStage("Аксессуары", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, null, oldReadyPriceForLog, productInSheet.ReadyPrice);
                            }
                            catch (Exception ex)
                            {
                                if (token.IsCancellationRequested || IsCancellationException(ex))
                                {
                                    return;
                                }

                                RegisterError($"Аксессуары: товар {productIndex}: {ex.Message}");
                                lock (log_lock)
                                {
                                    File.AppendAllText("logs.txt", $"\n[Retry {retrySw.Elapsed:mm\\:ss}/{retryTimeout.TotalMinutes:0}min] AKS - ProductIndex: {productIndex}, Proxy: {managerHotline?.proxy}, Error: {ex.Message}\n");
                                }
                                string currentUserAgent = managerHotline?.userAgent ?? agent[currentProxyIndex % agent.Count];
                                await (managerHotline?.CloseBrowser() ?? Task.CompletedTask);
                                managerHotline = null;

                                // Переключаемся на следующий прокси по кругу, но не зависаем на открытии Chrome.
                                bool replacementOpened = false;
                                for (int proxyAttempt = 0; proxyAttempt < proxys.Count && !token.IsCancellationRequested; proxyAttempt++)
                                {
                                    currentProxyIndex = (currentProxyIndex + 1) % proxys.Count;
                                    managerHotline = TryCreateBrowserManager("Аксессуары", proxys[currentProxyIndex], currentUserAgent, $"браузер после смены прокси {currentProxyIndex + 1}/{proxys.Count} не открылся", browserEconomyMode);
                                    if (managerHotline != null)
                                    {
                                        replacementOpened = true;
                                        break;
                                    }
                                }

                                if (!replacementOpened)
                                {
                                    AppendLog("Аксессуары: не удалось открыть браузер после смены прокси, оставляю старую цену.");
                                    break;
                                }

                                // Пауза 30 сек перед следующей попыткой
                                try { await Task.Delay(30_000, token); } catch (OperationCanceledException) { return; }
                            }
                        } // Конец while

                        // Таймаут истёк — добавляем товар со старыми ценами
                        if (!success && !token.IsCancellationRequested)
                        {
                            var fallback = new Hotline_Main_Parsing.aks.ProductInSheet();
                            fallback.Id = productId;
                            fallback.PriceAvailableness = (string?)data.ElementAtOrDefault(7);
                            fallback.Url = GetHotlineUrl(data);
                            if (TryParseSheetPrice((string?)data.ElementAtOrDefault(dicSymbols[priceOrientir]), out decimal fp))
                                fallback.Price = fp;
                            fallback.BitPrice = oldBitPricesAks.TryGetValue(productId, out decimal fob) && fob > 0 ? fob : fallback.Price;
                            fallback.ReadyPrice = oldReadyPricesAks.TryGetValue(productId, out decimal for2) && for2 > 0 ? for2 : fallback.Price;
                            ApplyOrientirAsResultIfNeeded(fallback, useOrientirAsResult);
                            await ApplyRrcBitPriceIfNeeded(fallback, data, dicSymbols, RRCPrice);
                            if (!noOffers)
                            {
                                lock (_brokenLinksLock)
                                {
                                    File.AppendAllText("brokenLinks.txt", fallback.Id + " - " + fallback.Url + "\n");
                                }
                            }
                            productsInSheet.Add(fallback);
                            UpdateProgressStage("Аксессуары", productsInSheet.Count, indexes.Count, productName, sw.Elapsed, noOffers ? "нет предложений на Hotline" : "таймаут/ошибка, оставил старую цену", oldReadyPriceForLog, fallback.ReadyPrice);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (token.IsCancellationRequested || IsCancellationException(ex))
                        {
                            return;
                        }

                        RegisterError($"Аксессуары: товар {productIndex}: {ex.Message}");
                        lock (log_lock)
                        {
                            File.AppendAllText("logs.txt", $"\n[FATAL] AKS - ProductIndex = {productIndex} Error: {ex.Message}\n{ex.StackTrace}\n");
                        }
                    }
                    finally
                    {
                        // Гарантированно возвращаем менеджера в очередь
                        if (managerHotline != null)
                        {
                            managers.Enqueue(managerHotline);
                        }
                    }
                });

                if (cancellationToken.IsCancellationRequested)
                {
                    AppendLog("Аксессуары: остановлено пользователем, неполный результат не записываю.");
                    return stats;
                }

                // Ваша логика пост-обработки
                if (lastPluses != null)
                {
                    foreach (var product in productsInSheet)
                    {
                        if (lastPluses.ContainsKey(product.Id))
                        {
                            if (lastPluses[product.Id] == "-" && product.PriceAvailableness == "+")
                            {
                                product.PriceColor = "red";
                            }
                        }
                    }
                }

                File.WriteAllText("mock.json", JsonConvert.SerializeObject(productsInSheet.ToList()));
                AppendLog("Аксессуары: не обработано - " + (indexes.Count - productsInSheet.Count));
                int changedPrices = productsInSheet.Count(product => IsReadyPriceChanged(product.Id, product.ReadyPrice, oldReadyPricesAks));
                AddProcessedProducts(productsInSheet.Count);
                AddChangedPrices(changedPrices);
                stats.Processed = productsInSheet.Count;
                stats.ChangedPrices = changedPrices;
                stats.Elapsed = sw.Elapsed;
                AppendLog($"Аксессуары: цен сменилось {changedPrices}");
                if (switchedParseMarks > 0)
                {
                    AppendLog($"Аксессуары: J -> K переключено: {switchedParseMarks}");
                }

                SetStage("аксессуары: закрываю браузеры");
                foreach (var manager in managers)
                {
                    await (manager?.CloseBrowser() ?? Task.CompletedTask);
                }

                SetStage("аксессуары: записываю результат в Google Sheets");
                spreadSheetManager.UploadDataToTables(productsInSheet.ToList()); // .ToList() нужен, если метод требует List, иначе можно передавать напрямую
                CompetitorHistoryStore.SaveInsights(competitorInsights);
                AppendLog($"Аксессуары: история конкурентов обновлена, записей: {competitorInsights.Count}");
                SetStage($"аксессуары: готово, обработано {productsInSheet.Count}");
                sw.Stop();
                stats.Elapsed = sw.Elapsed;
                AppendLog($"Аксессуары: время обработки {sw.Elapsed:hh\\:mm\\:ss}");
            }
            catch (Exception e)
            {
                if (cancellationToken.IsCancellationRequested || IsCancellationException(e))
                {
                    AppendLog("Аксессуары: остановлено пользователем.");
                    return stats;
                }

                RegisterError($"Аксессуары: {e.Message}");
                Console.WriteLine($"\n[GLOBAL ERROR AKS] {e.Message}\n{e.StackTrace}");
                File.AppendAllText("logs.txt", $"\n[GLOBAL ERROR AKS] {e.Message} - {e.StackTrace}");
            }
            finally
            {
                await CloseAllChromium();
            }

            return stats;
        }
        static async Task<Product> ParseProduct(ManagerHotline managerHotline, string url, string proxyPath, int count = 0, int errorCount = 0)
        {
            Product product = new Product() { Url = url };
            try
            {
                var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
                int browserCount = GetBrowserCount(config);
                var html = await managerHotline.LoadHtmlPage(url);
                if (html.Contains("turnstile") || html.Contains("Зафіксована підозріла активність"))
                {
                    Random rnd = new Random();

                    var proxys = File.ReadAllLines(proxyPath).ToList();
                    if (proxys.Count > 0)
                    {
                        int startIndex = Math.Min(browserCount, proxys.Count - 1);
                        int index = rnd.Next(startIndex, proxys.Count);
                        var proxy = proxys[index];
                        managerHotline.SetNewProxy(proxy);
                        html = await managerHotline.LoadHtmlPage(url);
                    }
                }

                // Проверяем локацию — если не Киев, выставляем Киев и перезагружаем страницу
                if (!html.Contains("title=\"Київ\""))
                {
                    await SetLocationKyiv(managerHotline);
                    html = await managerHotline.LoadHtmlPage(url);
                }

                // Ждём загрузки списка предложений
               // await managerHotline.GetActivePage().Result.WaitForSelectorAsync("div.list__item.flex.content");
                await Task.Delay(5000);

                // Извлекаем объект `window.__NUXT__`
                var nuxtData = await managerHotline.GetActivePage().Result.EvaluateExpressionAsync<string>("JSON.stringify(window.__NUXT__)");

                if (string.IsNullOrEmpty(nuxtData) || nuxtData == "null")
                    throw new InvalidOperationException("window.__NUXT__ is null — page not loaded");

                // Парсим JSON
                JObject json = JObject.Parse(nuxtData);

                // Ищем список магазинов и цен. Если структура не найдена, это ошибка загрузки,
                // а не "нет предложений".
                var offerEdges = json.SelectToken("$.state.product.offers.edges") as JArray;
                if (offerEdges == null)
                {
                    throw new InvalidOperationException("offers.edges is null — offers not loaded");
                }

                product.OffersLoaded = true;
                var offers = offerEdges
                    .Select(edge => edge["node"])
                    .Where(node => node != null)
                    .Select(node => new
                    {
                        Магазин = node!["firmTitle"]?.ToString() ?? "Неизвестный магазин",
                        Цена = node["price"]?.ToString() ?? "Нет цены"
                    })
                    .ToList();

                // Выводим результат
                Console.WriteLine($"🔍 Найдено магазинов: {offers.Count()}");
                foreach (var offer in offers)
                {
                    Console.WriteLine($"🏬 {offer.Магазин} | 💰 {offer.Цена} UAH");
                    Shop shop = new Shop();
                    shop.Name = offer.Магазин;
                    shop.Price = (int)Math.Floor(decimal.Parse(offer.Цена));

                    product.Shops.Add(shop);
                }



                //HtmlDocument htmlDoc = new HtmlDocument();
                //htmlDoc.LoadHtml(html);
                //var MainPrice = htmlDoc.DocumentNode.SelectNodes("//div[@class='list__item row flex']");
                //if (MainPrice == null)
                //    MainPrice = htmlDoc.DocumentNode.SelectNodes("//div[@class='list__item flex content']");
                //var priceBlock = htmlDoc.DocumentNode.SelectNodes("//div[@class='price-block']");
                //if (MainPrice != null)
                //{
                //    foreach (var item in MainPrice)
                //    {
                //        Shop shop = new Shop();
                //        var htmlDoc2 = new HtmlDocument();
                //        htmlDoc2.LoadHtml(item.InnerHtml);
                //        var shopName = htmlDoc2.DocumentNode.SelectSingleNode("//a[@class='shop__title']").InnerText.Trim();
                //        shop.Name = shopName;
                //        var priceValue = htmlDoc2.DocumentNode.SelectSingleNode("//span[@class='price__value']").InnerText;

                //        shop.Price = decimal.Parse(priceValue.Replace(" ", ""));
                //        product.Shops.Add(shop);

                //    }

                //}
                Random random = new Random();
                int del = random.Next(5000, 10000);
                await Task.Delay(4_000);

                await ChangeIP(managerHotline.changeLink);
            }
            catch (Exception ex)
            {
                if (errorCount < 10 && (ex.Message.Contains("ERR_CONNECTION_CLOSED") || ex.Message.Contains("ERR_EMPTY_RESPONSE") ||
                    ex.Message.Contains("ERR_TUNNEL_CONNECTION_FAILED") || ex.Message.Contains("Unable to get response body")
                    || ex.Message.Contains("ERR_SOCKET_NOT_CONNECTED") || ex.Message.Contains("ERR_TIMED_OUT") || ex.Message.Contains("Navigating frame was detached")
                    || ex.Message.Contains("Protocol error") || ex.Message.Contains("Object reference not set to") || ex.Message.Contains("ERR_ABORTED")
                    || ex.Message.Contains("window.__NUXT__ is null") || ex.Message.Contains("offers.edges is null")
                    || ex.Message.Contains("Hotline не вернул ответ") || ex.Message.Contains("Navigation timeout")))
                {
                    await ChangeIP(managerHotline.changeLink);
                    return await ParseProduct(managerHotline, url, proxyPath, count, errorCount + 1);
                }
                SafeAppendText("log.txt", DateTime.Now.ToString() + "     " + ex.Message +  " - " + ex.StackTrace + "\r\n");
                Console.WriteLine(ex.Message);
                throw;
            }
            if(product.Shops.Count == 0 && count < 15)
            {
                count++;
                await ChangeIP(managerHotline.changeLink);
                return await ParseProduct(managerHotline, url, proxyPath, count, errorCount);
            }
            return product;
        }
        static void ProcessJsonItem(JToken item)
        {
            if (item["@type"]?.ToString() == "BreadcrumbList")
            {
                Console.WriteLine("✅ Найдены хлебные крошки (Breadcrumbs):");
                var itemList = item["itemListElement"] as JArray;
                if (itemList != null)
                {
                    foreach (var listItem in itemList)
                    {
                        string name = listItem["item"]["name"]?.ToString() ?? "Без названия";
                        string link = listItem["item"]["@id"]?.ToString() ?? "Нет ссылки";
                        Console.WriteLine($"📌 {name} → {link}");
                    }
                }
            }

            // Парсим цену, если есть Product
            if (item["@type"]?.ToString() == "Product")
            {
                string productName = item["name"]?.ToString() ?? "Без названия";
                string price = item["offers"]?["price"]?.ToString() ?? "Цена не указана";
                string currency = item["offers"]?["priceCurrency"]?.ToString() ?? "Валюта не указана";

                Console.WriteLine("\n✅ Информация о товаре:");
                Console.WriteLine($"📌 Название: {productName}");
                Console.WriteLine($"💰 Цена: {price} {currency}");
            }
        }
        static async Task SetLocationKyiv(ManagerHotline managerHotline)
        {
            try
            {
                var page = await managerHotline.GetActivePage();

                // Кликаем на блок с локацией
                await page.ClickAsync("div.location");
                await Task.Delay(1500);

                // Ищем все ссылки городов и кликаем "Київ"
                var links = await page.QuerySelectorAllAsync("span.cities-list__link");
                foreach (var link in links)
                {
                    var text = await page.EvaluateFunctionAsync<string>("el => el.textContent", link);
                    if (text?.Trim() == "Київ")
                    {
                        // Запускаем ожидание навигации ДО клика, иначе можно пропустить событие
                        var navigationTask = page.WaitForNavigationAsync(new NavigationOptions { Timeout = 10_000 });
                        await link.ClickAsync();
                        try { await navigationTask; } catch { /* навигация могла не произойти — ок */ }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SetLocationKyiv error: {ex.Message}");
            }
        }

        static async Task ChangeIP(string targetUrl)
        {
            if (string.IsNullOrWhiteSpace(targetUrl))
            {
                return;
            }

            try
            {
                using var client = new HttpClient();
                var response = await client.GetStringAsync(targetUrl);

                Console.WriteLine("Ответ сервера:");
                Console.WriteLine(response);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка запроса: {ex.Message}");
            }
        }
        static Task CloseAllChromium()
        {
            return Task.Run(() =>
            {
                ManagerHotline.KillTrackedBrowserProcesses();
            });
        }


        private void CheckBoxAks_Checked(object sender, RoutedEventArgs e)
        {
            
        }

        private void CheckBoxAks_Click(object sender, RoutedEventArgs e)
        {
            bool flag = (bool)CheckBoxAks.IsChecked;
            SaveChangesValueInAppSettings("aksWorking", flag);
        }

        private void SaveChangesValueInAppSettings(string key, Object value)
        {
            string json = File.ReadAllText("appsettings.json");
            dynamic jsonObj = JsonConvert.DeserializeObject(json);
            jsonObj[key] = (bool)value;
            string output = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
            File.WriteAllText("appsettings.json", output);
        }

        private void CheckBoxDefault_Click(object sender, RoutedEventArgs e)
        {
            bool flag = (bool)CheckBoxDefault.IsChecked;
            SaveChangesValueInAppSettings("mainWorking", flag);
        }

        private void Button_ClickSetting(object sender, RoutedEventArgs e)
        {
            if (SettingsOverlay.Visibility == Visibility.Visible)
            {
                HideSettingsPanel();
                return;
            }

            var settingsPanel = new SettingsWindow
            {
                MainWindowOwner = this
            };
            settingsPanel.CloseRequested += SettingsPanel_CloseRequested;

            SettingsContent.Content = settingsPanel;
            SettingsOverlay.Visibility = Visibility.Visible;
            bSettings.Content = "Закрыть настройки";
            _wasTopmostBeforeSettings = Topmost;
            Topmost = true;
            Activate();
        }

        private void SettingsPanel_CloseRequested(object? sender, EventArgs e)
        {
            HideSettingsPanel();
        }

        private void HideSettingsPanel()
        {
            if (SettingsContent.Content is SettingsWindow settingsPanel)
            {
                settingsPanel.CloseRequested -= SettingsPanel_CloseRequested;
            }

            SettingsContent.Content = null;
            SettingsOverlay.Visibility = Visibility.Collapsed;
            bSettings.Content = "Настройки";
            Topmost = _wasTopmostBeforeSettings;
        }
    }

}
