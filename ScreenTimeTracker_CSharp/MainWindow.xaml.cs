using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using Microsoft.Win32;
using SkiaSharp;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ScreenTimeTracker_CSharp
{
    public partial class MainWindow : Window
    {
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private readonly string _dataFilePath;
        private Dictionary<string, Dictionary<string, int>> _allData = new Dictionary<string, Dictionary<string, int>>();
        private List<string> _availableMonths = new List<string>();
        private string _currentDisplayMonth;
        private string _selectedDateString;

        private ColumnSeries<ObservableValue> _barSeries;
        private List<PieSeries<int>> _pieSeriesList;

        private NotifyIcon _notifyIcon;
        private readonly string _appName = "ScreenTimeTracker_CSharp";
        private readonly RegistryKey _startupRegistryKey = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true);

        public MainWindow()
        {
            InitializeComponent();
            string appDataFolder = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolder = Path.Combine(appDataFolder, "ScreenTimeTracker_CSharp");
            Directory.CreateDirectory(appFolder);
            _dataFilePath = Path.Combine(appFolder, "screentime_data_daily.json");

            InitializeCharts();
            LoadDataAndInitializeState();
            SetupTrayIcon();

            this.Loaded += async (s, e) =>
            {
                this.Hide();
                await StartTrackingAsync();
            };
            this.StateChanged += MainWindow_StateChanged;
        }

        private void SetupTrayIcon()
        {
            _notifyIcon = new NotifyIcon();
            try
            {
                // ★★★ これが最終的な、最も確実なコードです ★★★
                var assembly = Assembly.GetExecutingAssembly();

                // プロジェクトのデフォルト名前空間 + ファイル名 がリソースの完全な名前です
                string resourceName = "ScreenTimeTracker_CSharp.icon.ico";

                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        // もしこれでも見つからない場合、こちらのエラーが表示されます
                        throw new Exception($"埋め込みリソース '{resourceName}' が見つかりません。ビルドアクションが「埋め込みリソース」になっているか、名前空間が正しいか確認してください。");
                    }
                    _notifyIcon.Icon = new System.Drawing.Icon(stream);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"アイコンの読み込みに失敗しました:\n{ex.Message}", "重大なエラー", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
                return;
            }

            _notifyIcon.Text = _appName;
            _notifyIcon.Visible = true;
            _notifyIcon.Click += (sender, args) => { this.Show(); this.WindowState = WindowState.Normal; this.Activate(); };
            _notifyIcon.ContextMenuStrip = new ContextMenuStrip();
            _notifyIcon.ContextMenuStrip.Items.Add("表示", null, (s, e) => this.Show());
            _notifyIcon.ContextMenuStrip.Items.Add("終了", null, (s, e) => Application.Current.Shutdown());
            StartupCheckBox.IsChecked = _startupRegistryKey.GetValue(_appName) != null;
        }

        private void MainWindow_StateChanged(object sender, EventArgs e)
        {
            if (this.WindowState == WindowState.Minimized) { this.Hide(); }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; this.Hide(); base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveData(); _notifyIcon.Dispose(); base.OnClosed(e);
        }

        private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                if (StartupCheckBox.IsChecked == true)
                {
                    // ★★★ 修正箇所 ★★★
                    // 常に起動元の.exeファイルへのパスを返す、より確実な方法
                    string exePath = Environment.ProcessPath;

                    // もし古い.NET Frameworkを使っている場合は、こちらの方が確実な場合があります
                    // string exePath = Process.GetCurrentProcess().MainModule.FileName;

                    _startupRegistryKey.SetValue(_appName, $"\"{exePath}\"");
                }
                else
                {
                    _startupRegistryKey.DeleteValue(_appName, false);
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"スタートアップ設定の更新に失敗しました: {ex.Message}");
            }
        }

        private void InitializeCharts()
        {
            _barSeries = new ColumnSeries<ObservableValue> { Fill = new SolidColorPaint(SKColors.CornflowerBlue), Name = "Total Time" };
            BarChart.Series = new ISeries[] { _barSeries };
            BarChart.XAxes = new[] { new Axis { LabelsPaint = new SolidColorPaint(SKColors.LightGray), TextSize = 10, LabelsRotation = 45 } };
            BarChart.YAxes = new[] { new Axis { LabelsPaint = new SolidColorPaint(SKColors.LightGray), TextSize = 10, Labeler = v => TimeSpan.FromSeconds(v).ToString(@"h'h 'm'm'"), MinLimit = 0, MaxLimit = 3600 } };
            _pieSeriesList = new List<PieSeries<int>>();
            var colors = new[] { SKColors.CornflowerBlue, SKColors.MediumPurple, SKColors.MediumAquamarine, SKColors.Gold, SKColors.IndianRed };
            for (int i = 0; i < 5; i++)
            {
                _pieSeriesList.Add(new PieSeries<int> { Values = new[] { 0 }, Name = "", IsVisible = false, Fill = new SolidColorPaint(colors[i % colors.Length]) });
            }
            PieChart.Series = _pieSeriesList;
            PieChart.LegendTextPaint = new SolidColorPaint(SKColors.LightGray);
        }

        private void LoadDataAndInitializeState()
        {
            try { if (File.Exists(_dataFilePath)) { string json = File.ReadAllText(_dataFilePath); var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, int>>>(json); if (data != null) _allData = data; } } catch { /* Ignore */ }
            _availableMonths = _allData.Keys.Select(day => day.Substring(0, 7)).Distinct().OrderBy(month => month).ToList();
            _currentDisplayMonth = _availableMonths.LastOrDefault();
            MonthSelectorComboBox.ItemsSource = _availableMonths;
            MonthSelectorComboBox.SelectedItem = _currentDisplayMonth;
            UpdateNavButtonState();
            UpdateBarChart();
            UpdateDetailsDisplay();
        }

        private void BarChart_DataPointerDown(IChartView chart, IEnumerable<ChartPoint> points)
        {
            var firstPoint = points.FirstOrDefault();
            if (firstPoint == null) return;
            var datesInMonth = _allData.Keys.Where(day => day.StartsWith(_currentDisplayMonth)).OrderBy(day => day).ToList();
            if (firstPoint.Index < datesInMonth.Count)
            {
                _selectedDateString = datesInMonth[(int)firstPoint.Index];
                UpdateDetailsDisplay();
            }
        }

        private void PrevMonthButton_Click(object sender, RoutedEventArgs e) { int idx = _availableMonths.IndexOf(_currentDisplayMonth); if (idx > 0) MonthSelectorComboBox.SelectedIndex = idx - 1; }
        private void NextMonthButton_Click(object sender, RoutedEventArgs e) { int idx = _availableMonths.IndexOf(_currentDisplayMonth); if (idx < _availableMonths.Count - 1) MonthSelectorComboBox.SelectedIndex = idx + 1; }
        private void MonthSelectorComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MonthSelectorComboBox.SelectedItem is string selectedMonth)
            {
                _currentDisplayMonth = selectedMonth;
                UpdateNavButtonState();
                UpdateBarChart();
                UpdateDetailsDisplay();
            }
        }
        private void UpdateNavButtonState() { if (_availableMonths.Count == 0) return; int idx = _availableMonths.IndexOf(_currentDisplayMonth); PrevMonthButton.IsEnabled = idx > 0; NextMonthButton.IsEnabled = idx < _availableMonths.Count - 1; }

        private async Task StartTrackingAsync()
        {
            while (true)
            {
                string todayDateString = DateTime.Now.ToString("yyyy-MM-dd");
                string todayMonthString = todayDateString.Substring(0, 7);
                if (!_allData.ContainsKey(todayDateString)) _allData[todayDateString] = new Dictionary<string, int>();
                var todayData = _allData[todayDateString];
                string currentProcessName = "Unknown";
                try { IntPtr handle = GetForegroundWindow(); if (handle != IntPtr.Zero) { GetWindowThreadProcessId(handle, out uint pid); Process p = Process.GetProcessById((int)pid); currentProcessName = p.ProcessName + ".exe"; } } catch { /* Ignore */ }
                if (todayData.ContainsKey(currentProcessName)) todayData[currentProcessName]++; else todayData[currentProcessName] = 1;
                if (!_availableMonths.Contains(todayMonthString)) { _availableMonths.Add(todayMonthString); MonthSelectorComboBox.ItemsSource = null; MonthSelectorComboBox.ItemsSource = _availableMonths; MonthSelectorComboBox.SelectedItem = todayMonthString; }
                if (_currentDisplayMonth == todayMonthString)
                {
                    var datesInMonth = _allData.Keys.Where(d => d.StartsWith(_currentDisplayMonth)).OrderBy(d => d).ToList();
                    int todayIndex = datesInMonth.IndexOf(todayDateString);
                    if (todayIndex != -1 && _barSeries.Values.Count > todayIndex)
                    {
                        double currentTotalSeconds = todayData.Values.Sum();
                        var yAxis = BarChart.YAxes.First();
                        double maxLimit = yAxis.MaxLimit ?? 1;
                        double minHeight = maxLimit * 0.015;
                        double displayValue = (currentTotalSeconds > 0 && currentTotalSeconds < minHeight) ? minHeight : currentTotalSeconds;
                        ((ObservableValue)_barSeries.Values.ElementAt(todayIndex)).Value = displayValue;
                        if (yAxis.MaxLimit.HasValue && currentTotalSeconds > yAxis.MaxLimit.Value)
                        {
                            yAxis.MaxLimit = currentTotalSeconds * 1.1;
                            UpdateBarChart();
                        }
                    }
                }
                if (_selectedDateString == todayDateString) { UpdateDetailsDisplay(); }
                await Task.Delay(1000);
            }
        }

        private void UpdateBarChart()
        {
            var monthlyData = _allData.Where(kvp => kvp.Key.StartsWith(_currentDisplayMonth)).OrderBy(kvp => kvp.Key).ToList();
            if (!monthlyData.Any()) return;
            var actualValues = monthlyData.Select(kvp => (double)kvp.Value.Values.Sum()).ToList();
            double maxTimeInMonth = actualValues.Max();
            double newMaxLimit = Math.Max(600, maxTimeInMonth * 1.1);
            BarChart.YAxes.First().MaxLimit = newMaxLimit;
            double minHeight = newMaxLimit * 0.015;
            var displayValues = actualValues.Select(val => (val > 0 && val < minHeight) ? minHeight : val).Select(val => new ObservableValue(val)).ToList();
            _barSeries.Values = displayValues;
            BarChart.XAxes.First().Labels = monthlyData.Select(kvp => kvp.Key.Substring(5)).ToList();
        }

        private void UpdateDetailsDisplay()
        {
            if (string.IsNullOrEmpty(_selectedDateString) || !_selectedDateString.StartsWith(_currentDisplayMonth))
            {
                _selectedDateString = _allData.Keys.Where(d => d.StartsWith(_currentDisplayMonth)).OrderBy(d => d).LastOrDefault() ?? "No data";
            }
            DetailsLabel.Text = $"詳細データ: {_selectedDateString}";
            var usageDataForDay = _allData.ContainsKey(_selectedDateString) ? _allData[_selectedDateString] : new Dictionary<string, int>();
            var usageList = usageDataForDay.Select(kvp => new AppUsage { AppName = kvp.Key, TotalSeconds = kvp.Value }).OrderByDescending(u => u.TotalSeconds).ToList();
            AppTimeListView.ItemsSource = usageList;
            var top5Apps = usageDataForDay.OrderByDescending(kvp => kvp.Value).Take(5).ToList();
            for (int i = 0; i < 5; i++)
            {
                if (i < top5Apps.Count)
                {
                    _pieSeriesList[i].Values = new[] { top5Apps[i].Value };
                    _pieSeriesList[i].Name = top5Apps[i].Key;
                    _pieSeriesList[i].IsVisible = true;
                }
                else
                {
                    _pieSeriesList[i].IsVisible = false;
                    _pieSeriesList[i].Values = new[] { 0 };
                }
            }
        }

        private void SaveData() { try { var options = new JsonSerializerOptions { WriteIndented = true }; string jsonString = JsonSerializer.Serialize(_allData, options); File.WriteAllText(_dataFilePath, jsonString); } catch (Exception ex) { Console.WriteLine($"データの保存に失敗しました: {ex.Message}"); } }

    } // ★★★ ここが MainWindow クラスの正しい閉じ括弧 ★★★

    public class AppUsage { public string AppName { get; set; } public int TotalSeconds { get; set; } public string TimeString => TimeSpan.FromSeconds(TotalSeconds).ToString(@"hh\:mm\:ss"); }
}