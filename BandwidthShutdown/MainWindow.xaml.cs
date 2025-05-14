// =============================
// USING DIRECTIVES AND ALIASES
// =============================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Timers;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Timer = System.Timers.Timer;

namespace BandwidthShutdown
{
    public partial class MainWindow : Window
    {
        // =============================
        // TIMER AND DATA FIELDS
        // =============================

        private Timer monitorTimer; // Periodically checks current bandwidth
        private Timer displayTimer; // Updates the UI display every second
        private List<double> bandwidthSamples = new List<double>(); // Stores recent bandwidth samples for averaging
        private double thresholdKBps; // User-defined threshold to trigger shutdown (KB/s)
        private int intervalSeconds; // Interval in seconds between bandwidth samples
        private int delaySeconds; // Total time in seconds below threshold before shutdown
        private int samplesRequired; // Number of samples required to cover the delay window
        private bool testingMode; // Indicates whether shutdown should be simulated
        private double latestBandwidthKBps = 0; // Most recent sampled bandwidth in KB/s

        // For calculating bandwidth from total byte deltas
        private System.Net.NetworkInformation.IPv4InterfaceStatistics[]? previousStats = null;

        private DateTime? lastSampleTime = null;

        // Location of settings file in AppData
        private static readonly string SettingsFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BandwidthShutdown", "settings.json");

        // =============================
        // CONSTRUCTOR
        // =============================

        public MainWindow()
        {
            InitializeComponent();

            // Timer to update the bandwidth display every second
            displayTimer = new Timer(1000);
            displayTimer.Elapsed += DisplayTimer_Elapsed;
            displayTimer.Start();

            // Save settings on close
            this.Closing += (s, e) => SaveSettings();

            // Load saved settings into UI controls
            LoadSettings();
        }

        // =============================
        // UI DISPLAY TIMER
        // =============================

        private void DisplayTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Calculate average over most recent samplesRequired values
                double avg = 0;
                if (bandwidthSamples.Count >= samplesRequired)
                {
                    avg = bandwidthSamples.Skip(bandwidthSamples.Count - samplesRequired).DefaultIfEmpty(0).Average();
                }
                else if (bandwidthSamples.Count > 0)
                {
                    avg = bandwidthSamples.DefaultIfEmpty(0).Average();
                }

                // Update bandwidth display text
                BandwidthDisplay.Text = $"Current Bandwidth: {Math.Round(latestBandwidthKBps)} KB/s (Avg: {Math.Round(avg)} KB/s)";

                // Change display color if average is below threshold
                BandwidthDisplay.Foreground = (avg < thresholdKBps && bandwidthSamples.Count >= samplesRequired)
                    ? Brushes.Red : Brushes.Black;
            });
        }

        // =============================
        // START MONITORING BUTTON HANDLER
        // =============================

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Clean up any previous monitoring timer
            if (monitorTimer != null)
            {
                monitorTimer.Elapsed -= MonitorTimer_Elapsed;
                monitorTimer.Dispose();
            }

            // Read values from UI
            if (!double.TryParse(ThresholdInput.Text, out thresholdKBps)) thresholdKBps = 200;
            if (!int.TryParse(IntervalInput.Text, out intervalSeconds)) intervalSeconds = 2;
            if (!int.TryParse(DelayInput.Text, out delaySeconds)) delaySeconds = 60;
            testingMode = TestingModeCheckBox.IsChecked == true;
            samplesRequired = (int)Math.Ceiling((double)delaySeconds / intervalSeconds);

            // Save to file
            SaveSettings();

            // Start monitoring
            bandwidthSamples.Clear();
            monitorTimer = new Timer(intervalSeconds * 1000);
            monitorTimer.Elapsed += MonitorTimer_Elapsed;
            monitorTimer.Start();

            Log($"Monitoring started. Threshold: {thresholdKBps} KB/s, Delay: {delaySeconds}s.", Brushes.DarkCyan);
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
        }

        // =============================
        // STOP MONITORING BUTTON HANDLER
        // =============================

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            StopMonitoring();
         }

        // =============================
        // MONITORING LOGIC
        // =============================

        private void MonitorTimer_Elapsed(object? sender, ElapsedEventArgs e)
        {
            Dispatcher.Invoke(() =>
            {
                // Get bandwidth since last sample
                double bandwidthKBps = GetTotalBandwidthKBps();
                latestBandwidthKBps = bandwidthKBps;
                bandwidthSamples.Add(bandwidthKBps);

                // Limit to sample window
                if (bandwidthSamples.Count > samplesRequired)
                    bandwidthSamples.RemoveAt(0);

                // Calculate average
                double avg = bandwidthSamples.Count >= samplesRequired
                    ? bandwidthSamples.Skip(bandwidthSamples.Count - samplesRequired).Average()
                    : bandwidthSamples.Average();

                // Trigger shutdown if below threshold long enough
                if (bandwidthSamples.Count >= samplesRequired && avg < thresholdKBps)
                {
                    Log($"Bandwidth Average: {Math.Round(avg)} KB/s Below Threshold. Initiating Shutdown.", Brushes.Red);
                    monitorTimer.Stop();

                    if (testingMode)
                    {
                        StopMonitoring();
                        MessageBox.Show("[TEST MODE] Shutdown Triggered.", "Test Mode", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        Process.Start(new ProcessStartInfo("shutdown", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                    }
                }
                else
                {
                    Log($"Bandwidth: {Math.Round(bandwidthKBps)} KB/s, Average: {Math.Round(avg)}", Brushes.Black);
                }
            });
        }

        void StopMonitoring()
        {
            bandwidthSamples.Clear();
            monitorTimer?.Stop();
            monitorTimer?.Dispose();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            Log("Monitoring stopped.", Brushes.Gray);
        }

        // =============================
        // BANDWIDTH SAMPLING FUNCTION
        // =============================

        private double GetTotalBandwidthKBps()
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic => nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .ToArray();

            var currentStats = interfaces.Select(nic => nic.GetIPv4Statistics()).ToArray();
            var now = DateTime.UtcNow;

            if (previousStats == null || lastSampleTime == null)
            {
                previousStats = currentStats;
                lastSampleTime = now;
                return 0;
            }

            double totalBytesReceived = 0;
            double totalElapsedSeconds = (now - lastSampleTime.Value).TotalSeconds;

            for (int i = 0; i < interfaces.Length; i++)
            {
                long previousBytes = previousStats[i].BytesReceived;
                long currentBytes = currentStats[i].BytesReceived;
                totalBytesReceived += (currentBytes - previousBytes);
            }

            previousStats = currentStats;
            lastSampleTime = now;

            return (totalBytesReceived / totalElapsedSeconds) / 1024; // Convert to KB/s
        }

        // =============================
        // LOGGING FUNCTION
        // =============================

        private void Log(string message, Brush color)
        {
            Dispatcher.Invoke(() =>
            {
                var timestamp = DateTime.Now.ToString("HH:mm:ss");
                var line = new Run($"[{timestamp}] {message}\n") { Foreground = color };
                LogParagraph.Inlines.Add(line);
                LogOutput.ScrollToEnd();
            });
        }

        // =============================
        // SAVE SETTINGS TO FILE
        // =============================

        private void SaveSettings()
        {
            try
            {
                // Refresh current settings from UI
                _ = double.TryParse(ThresholdInput.Text, out thresholdKBps);
                _ = int.TryParse(IntervalInput.Text, out intervalSeconds);
                _ = int.TryParse(DelayInput.Text, out delaySeconds);
                testingMode = TestingModeCheckBox.IsChecked == true;

                var settings = new UserSettings
                {
                    Threshold = thresholdKBps,
                    Interval = intervalSeconds,
                    Delay = delaySeconds,
                    Testing = testingMode
                };

                string? dir = Path.GetDirectoryName(SettingsFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings));
            }
            catch (Exception ex)
            {
                Log($"Failed to save settings: {ex.Message}", Brushes.Orange);
            }
        }

        // =============================
        // LOAD SETTINGS FROM FILE
        // =============================

        private void LoadSettings()
        {
            if (!File.Exists(SettingsFile)) return;
            try
            {
                var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsFile));
                if (settings == null) return;

                ThresholdInput.Text = settings.Threshold.ToString();
                IntervalInput.Text = settings.Interval.ToString();
                DelayInput.Text = settings.Delay.ToString();
                TestingModeCheckBox.IsChecked = settings.Testing;
            }
            catch (Exception ex)
            {
                Log($"Failed to load settings: {ex.Message}", Brushes.Orange);
            }
        }

        // =============================
        // SETTINGS CLASS
        // =============================

        private class UserSettings
        {
            public double Threshold { get; set; }
            public int Interval { get; set; }
            public int Delay { get; set; }
            public bool Testing { get; set; }
        }
    }
}
