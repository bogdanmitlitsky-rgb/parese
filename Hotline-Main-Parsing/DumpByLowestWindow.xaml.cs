using Hotline_Main_Parsing.common;
using Microsoft.Extensions.Configuration;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Navigation;
using DefaultSheets = Hotline_Main_Parsing.@default;

namespace Hotline_Main_Parsing
{
    public partial class DumpByLowestWindow : Window
    {
        private readonly ObservableCollection<DumpByLowestProductRecord> _products = new();
        private ICollectionView? _productsView;
        private bool _isLoadingProducts;
        private bool _hasUnsavedChanges;

        public DumpByLowestWindow()
        {
            InitializeComponent();
            ProductsGrid.ItemsSource = _products;
            _productsView = CollectionViewSource.GetDefaultView(_products);
            _productsView.Filter = FilterProduct;
            LoadLocalBase();
        }

        private void LoadLocalBase()
        {
            ReplaceProducts(DumpByLowestProductStore.Load());
            RefreshSummary();
            SetSaveState(false, "Сохранено", SaveStateKind.Saved);
        }

        private async void RefreshBase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SetBusy(true, "Обновляю базу из Google Sheets...");
                var freshProducts = await Task.Run(ReadProductsFromGoogleSheets);
                var merged = DumpByLowestProductStore.MergeWithExisting(freshProducts);
                DumpByLowestProductStore.Save(merged);

                ReplaceProducts(merged);
                _productsView?.Refresh();
                RefreshSummary();
                SetSaveState(false, $"Сохранено {DateTime.Now:HH:mm:ss}", SaveStateKind.Saved);
                StatusText.Text = $"База обновлена и сохранена: {freshProducts.Count} товаров прочитано из Google Sheets.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось обновить базу: {ex.Message}", "Товары по низу", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "Ошибка обновления базы.";
                SetSaveState(_hasUnsavedChanges, "Ошибка обновления", SaveStateKind.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static List<DumpByLowestProductRecord> ReadProductsFromGoogleSheets()
        {
            var config = new ConfigurationBuilder().AddJsonFile("appsettings.json").Build();
            var result = new List<DumpByLowestProductRecord>();

            var defaultManager = new DefaultSheets.SpreadSheetManager(
                config["HotlineSpreadSheetId_default"]!,
                config["BitSpreadSheetId_default"]!,
                config["KYMPromSpreadSheetId_default"]!,
                config["HotlineSpreadSheetId_default_editor"]!,
                config["1UaPromSpreadSheetId_default"]!,
                config["StokPromSpreadSheetId_default"]!,
                config["SmilePromSpreadSheetId_default"]!);

            result.AddRange(defaultManager.GetProductsForDumpByLowestBase());

            var aksManager = new Hotline_Main_Parsing.aks.SpreadSheetManager(
                config["HotlineSpreadSheetId_aks"]!,
                config["BitSpreadSheetId_aks"]!);

            result.AddRange(aksManager.GetProductsForDumpByLowestBase());
            return result;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                DumpByLowestProductStore.Save(_products);
                RefreshSummary();
                SetSaveState(false, $"Сохранено {DateTime.Now:HH:mm:ss}", SaveStateKind.Saved);
                StatusText.Text = "Сохранено. Парсер применит выбор при следующем запуске.";
            }
            catch (Exception ex)
            {
                SetSaveState(true, "Не сохранено", SaveStateKind.Error);
                StatusText.Text = $"Не удалось сохранить выбор: {ex.Message}";
                MessageBox.Show($"Не удалось сохранить выбор: {ex.Message}", "Товары по низу", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SelectVisible_Click(object sender, RoutedEventArgs e)
        {
            if (_productsView == null)
            {
                return;
            }

            _isLoadingProducts = true;
            foreach (var product in _productsView.Cast<DumpByLowestProductRecord>())
            {
                product.Selected = true;
            }
            _isLoadingProducts = false;
            MarkUnsavedChanges();
            RefreshSummary();
        }

        private void UnselectVisible_Click(object sender, RoutedEventArgs e)
        {
            if (_productsView == null)
            {
                return;
            }

            _isLoadingProducts = true;
            foreach (var product in _productsView.Cast<DumpByLowestProductRecord>())
            {
                product.Selected = false;
            }
            _isLoadingProducts = false;
            MarkUnsavedChanges();
            RefreshSummary();
        }

        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            _productsView?.Refresh();
            RefreshSummary();
        }

        private bool FilterProduct(object obj)
        {
            if (obj is not DumpByLowestProductRecord product)
            {
                return false;
            }

            string section = (SectionFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все разделы";
            if (!section.Equals("Все разделы", StringComparison.OrdinalIgnoreCase) &&
                !product.Section.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string query = SearchBox.Text?.Trim() ?? string.Empty;
            if (query.Length == 0)
            {
                return true;
            }

            return product.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                   product.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
        }

        private void RefreshSummary()
        {
            int total = _products.Count;
            int selected = _products.Count(product => product.Selected);
            int visible = _productsView?.Cast<object>().Count() ?? total;
            SummaryText.Text = $"Товаров в базе: {total} | Выбрано по низу: {selected} | Показано: {visible}";
        }

        private void ReplaceProducts(IEnumerable<DumpByLowestProductRecord> products)
        {
            try
            {
                _isLoadingProducts = true;
                foreach (var product in _products)
                {
                    product.PropertyChanged -= Product_PropertyChanged;
                }

                _products.Clear();
                foreach (var product in products)
                {
                    product.PropertyChanged += Product_PropertyChanged;
                    _products.Add(product);
                }
            }
            finally
            {
                _isLoadingProducts = false;
            }
        }

        private void Product_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isLoadingProducts || e.PropertyName != nameof(DumpByLowestProductRecord.Selected))
            {
                return;
            }

            RefreshSummary();
            MarkUnsavedChanges();
        }

        private void MarkUnsavedChanges()
        {
            SetSaveState(true, "Есть изменения", SaveStateKind.Dirty);
            StatusText.Text = "Есть несохраненные изменения. Нажмите «Сохранить».";
        }

        private void SetSaveState(bool hasUnsavedChanges, string message, SaveStateKind state)
        {
            _hasUnsavedChanges = hasUnsavedChanges;
            SaveStateText.Text = message;

            switch (state)
            {
                case SaveStateKind.Saved:
                    SaveStateBadge.Background = new SolidColorBrush(Color.FromRgb(233, 248, 239));
                    SaveStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(187, 247, 208));
                    SaveStateText.Foreground = (Brush)FindResource("SuccessTextBrush");
                    break;
                case SaveStateKind.Dirty:
                    SaveStateBadge.Background = new SolidColorBrush(Color.FromRgb(255, 247, 237));
                    SaveStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(253, 186, 116));
                    SaveStateText.Foreground = (Brush)FindResource("WarningTextBrush");
                    break;
                case SaveStateKind.Error:
                    SaveStateBadge.Background = new SolidColorBrush(Color.FromRgb(254, 242, 242));
                    SaveStateBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(254, 202, 202));
                    SaveStateText.Foreground = (Brush)FindResource("ErrorTextBrush");
                    break;
            }

            UpdateSaveButtonState();
        }

        private void UpdateSaveButtonState()
        {
            SaveButton.IsEnabled = _hasUnsavedChanges && ProductsGrid.IsEnabled;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshBase_Click_ButtonState(busy);
            ProductsGrid.IsEnabled = !busy;
            UpdateSaveButtonState();
            if (!string.IsNullOrWhiteSpace(status))
            {
                StatusText.Text = status;
            }
        }

        private void RefreshBase_Click_ButtonState(bool busy)
        {
            foreach (var button in FindVisualChildren<Button>(this))
            {
                button.IsEnabled = !busy;
            }

            UpdateSaveButtonState();
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            {
                DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T typedChild)
                {
                    yield return typedChild;
                }

                foreach (T nestedChild in FindVisualChildren<T>(child))
                {
                    yield return nestedChild;
                }
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

        private enum SaveStateKind
        {
            Saved,
            Dirty,
            Error
        }
    }
}
