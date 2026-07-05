using Enzo;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AIMHEAD_ON_OFF
{
    public partial class Form1 : Form
    {
        public Form1()
        {
            InitializeComponent();
        }

        private void guna2Button2_Click(object sender, EventArgs e)
        {
            if (!AimbotToggle)
            {
                AimbotOFF();
                AimbotToggle = true;
            }
            else
            {
                AimbotON();
                AimbotToggle = false;
            }
        }


        private void UpdateLabel(string text, Color? color = null)
        {
            if (label5.InvokeRequired)
            {
                label5.Invoke(new Action(() => UpdateLabel(text, color)));
            }
            else
            {
                label5.Text = text;
                if (color.HasValue)
                {
                    label5.ForeColor = color.Value;
                }
            }
        }

        private void UpdatePerformanceStats(string statsText)
        {
            if (label5.InvokeRequired)
            {
                label5.Invoke(new Action(() => UpdatePerformanceStats(statsText)));
            }
            else
            {
                // Display in a secondary label or status bar if available
                System.Diagnostics.Debug.WriteLine(statsText);
            }
        }

        private string[] TaskName = new string[] { "HD-Player" };
        private EnzoMem Memory = new EnzoMem();
        
        // ✅ FIXED: Complete your actual pattern here (replace with your real pattern from Cheat Engine)
        // Example format: "12 34 56 78 9A BC DE F0 11 22 33 44 55 66 77 88"
        // Use "??" for wildcard bytes
        private string AimbotPattern = ""; // ← PUT YOUR COMPLETE PATTERN HERE!
        
        private long ReadOffset = 0xAF;   // Offset 124 in decimal
        private long WriteOffset = 0xAB;

        // ✅ NEW: Load & Toggle Pattern Storage
        private List<long> LoadedAddresses = new List<long>();
        private bool IsAddressesLoaded = false;

        private readonly Dictionary<long, byte[]> OriginalValue1 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> OriginalValue2 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> ReplacedValue1 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> ReplacedValue2 = new Dictionary<long, byte[]>();

        // ✅ NEW: Performance Monitoring
        private PerformanceMetrics PerfMetrics = new PerformanceMetrics();

        public void AimbotOFF()
        {
            if (OriginalValue1.Count == 0 && OriginalValue2.Count == 0)
            {
                UpdateLabel("Aimbot not activated yet", Color.Orange);
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            RestoreValues(OriginalValue1);
            RestoreValues(OriginalValue2);
            sw.Stop();

            PerfMetrics.LastToggleTime = sw.ElapsedMilliseconds;
            UpdateLabel($"Aimbot Disabled ({sw.ElapsedMilliseconds}ms)", Color.Red);
        }

        public void AimbotON()
        {
            if (ReplacedValue1.Count == 0 && ReplacedValue2.Count == 0)
            {
                UpdateLabel("Aimbot not activated yet", Color.Orange);
                return;
            }

            Stopwatch sw = Stopwatch.StartNew();
            RestoreValues(ReplacedValue1);
            RestoreValues(ReplacedValue2);
            sw.Stop();

            PerfMetrics.LastToggleTime = sw.ElapsedMilliseconds;
            UpdateLabel($"Aimbot Enabled ({sw.ElapsedMilliseconds}ms)", Color.Green);
        }

        private void RestoreValues(Dictionary<long, byte[]> dictionary)
        {
            foreach (var entry in dictionary)
            {
                int value = BitConverter.ToInt32(entry.Value, 0);
                Memory.WriteInt(entry.Key, value);
            }
        }

        private bool AimbotToggle = false;

        /// <summary>
        /// ✅ Validation: Check if pattern is valid before scanning
        /// </summary>
        private bool ValidatePattern()
        {
            if (string.IsNullOrWhiteSpace(AimbotPattern))
            {
                MessageBox.Show("❌ ERROR: AimbotPattern is empty!\n\nPlease set your pattern from Cheat Engine.", "Pattern Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            try
            {
                string[] parts = AimbotPattern.Split(' ');
                foreach (var part in parts)
                {
                    if (part != "??")
                    {
                        byte.Parse(part, System.Globalization.NumberStyles.HexNumber);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"❌ ERROR: Invalid pattern format!\n\n{ex.Message}\n\nMake sure your pattern is like:\n00 11 22 33 ?? 55 66 77", "Pattern Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        /// <summary>
        /// ✅ NEW METHOD: Load Addresses (First Step)
        /// Call this once to scan and cache addresses
        /// This is the slow operation (30-40s) but happens only ONCE
        /// </summary>
        private async void LoadAddressesClick(object sender, EventArgs e)
        {
            // ✅ Validate pattern first
            if (!ValidatePattern())
                return;

            if (!Memory.SetProcess(TaskName))
            {
                MessageBox.Show("Process not found", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                Process targetProcess = Process.GetProcessesByName("HD-Player").FirstOrDefault();
                if (targetProcess == null)
                {
                    MessageBox.Show("HD-Player process not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UpdateLabel("Loading addresses... (this may take 30-40 seconds)", Color.Orange);

                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                // ✅ Initialize memory with optimal settings
                Memory.Initialize(
                    maxThreads: Math.Max(2, Environment.ProcessorCount / 2),
                    chunkSizeMB: 1,
                    useCache: true
                );

                // ✅ Scan for addresses (only happens once)
                IEnumerable<long> addresses = await Memory.AoBScan(AimbotPattern);

                if (addresses == null || !addresses.Any())
                {
                    UpdateLabel("Error: Pattern not found!", Color.Red);
                    MessageBox.Show("Pattern not found in game memory. Make sure:\n1. The pattern is correct\n2. Game is running\n3. HD-Player process is active", "Pattern Not Found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                LoadedAddresses = addresses.ToList();
                IsAddressesLoaded = true;
                stopwatch.Stop();

                PerfMetrics.ScanTime = stopwatch.ElapsedMilliseconds;
                PerfMetrics.AddressCount = LoadedAddresses.Count;

                UpdateLabel($"✓ Loaded {LoadedAddresses.Count} addresses in {stopwatch.Elapsed.TotalSeconds:F2}s", Color.Blue);
                MessageBox.Show($"Successfully loaded {LoadedAddresses.Count} addresses!\n\nNow use the ON/OFF button for instant toggling.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                UpdateLabel($"Error: {ex.Message}", Color.Red);
                MessageBox.Show($"Error during load: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// ✅ NEW METHOD: Toggle Aimbot (Second Step)
        /// Call this to instantly apply/remove patches using pre-loaded addresses
        /// This is SUPER FAST (milliseconds)
        /// </summary>
        private void ToggleAimbotClick(object sender, EventArgs e)
        {
            if (!IsAddressesLoaded)
            {
                MessageBox.Show("Please click 'Load Addresses' first!", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Stopwatch stopwatch = new Stopwatch();
            stopwatch.Start();

            if (!AimbotToggle)
            {
                AimbotON_Fast();
                AimbotToggle = true;
            }
            else
            {
                AimbotOFF_Fast();
                AimbotToggle = false;
            }

            stopwatch.Stop();
            PerfMetrics.LastToggleTime = stopwatch.ElapsedMilliseconds;
            UpdateLabel($"Toggle completed in {stopwatch.ElapsedMilliseconds}ms", AimbotToggle ? Color.Green : Color.Red);
        }

        private void AimbotON_Fast()
        {
            OriginalValue1.Clear();
            OriginalValue2.Clear();
            ReplacedValue1.Clear();
            ReplacedValue2.Clear();

            foreach (long addr in LoadedAddresses)
            {
                long readAddr = addr + ReadOffset;
                long writeAddr = addr + WriteOffset;

                byte[] readBytes = Memory.ReadMemory(readAddr, 4);
                byte[] writeBytes = Memory.ReadMemory(writeAddr, 4);

                if (readBytes == null || writeBytes == null)
                    continue;

                int readValue = BitConverter.ToInt32(readBytes, 0);
                int writeValue = BitConverter.ToInt32(writeBytes, 0);

                // Store original values for restoration
                OriginalValue1[writeAddr] = writeBytes;
                OriginalValue2[readAddr] = readBytes;

                // Apply patches
                Memory.WriteInt(writeAddr, readValue);
                Memory.WriteInt(readAddr, writeValue);

                // Store replaced values
                ReplacedValue1[writeAddr] = BitConverter.GetBytes(readValue);
                ReplacedValue2[readAddr] = BitConverter.GetBytes(writeValue);
            }

            UpdateLabel("Aimbot Enabled", Color.Green);
        }

        private void AimbotOFF_Fast()
        {
            RestoreValues(OriginalValue1);
            RestoreValues(OriginalValue2);
            UpdateLabel("Aimbot Disabled", Color.Red);
        }

        /// <summary>
        /// ✅ ORIGINAL METHOD: One-Click Activation (Legacy)
        /// This scans and activates in one go - use if you don't want two buttons
        /// </summary>
        private async void guna2Button1_Click(object sender, EventArgs e)
        {
            // ✅ Validate pattern first
            if (!ValidatePattern())
                return;

            if (!Memory.SetProcess(TaskName))
            {
                MessageBox.Show("Process not found", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                Process targetProcess = Process.GetProcessesByName("HD-Player").FirstOrDefault();
                if (targetProcess == null)
                {
                    MessageBox.Show("HD-Player process not found.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                UpdateLabel("Activating...", Color.Orange);

                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();

                OriginalValue1.Clear();
                OriginalValue2.Clear();
                ReplacedValue1.Clear();
                ReplacedValue2.Clear();

                // Initialize memory with optimal settings
                Memory.Initialize(
                    maxThreads: Math.Max(2, Environment.ProcessorCount / 2),
                    chunkSizeMB: 1,
                    useCache: true
                );

                IEnumerable<long> addresses = await Memory.AoBScan(AimbotPattern);

                if (addresses == null || !addresses.Any())
                {
                    UpdateLabel("Error Contact Tempest For Help!", Color.Red);
                    return;
                }

                foreach (long addr in addresses)
                {
                    long readAddr = addr + ReadOffset;
                    long writeAddr = addr + WriteOffset;

                    byte[] readBytes = Memory.ReadMemory(readAddr, 4);
                    byte[] writeBytes = Memory.ReadMemory(writeAddr, 4);

                    if (readBytes == null || writeBytes == null)
                    {
                        MessageBox.Show("Failed to read memory.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }

                    int readValue = BitConverter.ToInt32(readBytes, 0);
                    int writeValue = BitConverter.ToInt32(writeBytes, 0);

                    OriginalValue1[writeAddr] = writeBytes;
                    OriginalValue2[readAddr] = readBytes;

                    Memory.WriteInt(writeAddr, readValue);
                    Memory.WriteInt(readAddr, writeValue);

                    ReplacedValue1[writeAddr] = BitConverter.GetBytes(readValue);
                    ReplacedValue2[readAddr] = BitConverter.GetBytes(writeValue);
                }

                stopwatch.Stop();
                PerfMetrics.ScanTime = stopwatch.ElapsedMilliseconds;
                PerfMetrics.AddressCount = addresses.Count();

                // ✅ Memory cleanup after scan
                GC.Collect(GC.MaxGeneration);

                UpdateLabel($"Aimbot Head Activated   {stopwatch.Elapsed.TotalSeconds:F3}s", Color.Green);
            }
            catch (Exception ex)
            {
                UpdateLabel($"Error: {ex.Message}", Color.Red);
                MessageBox.Show($"Injection Error: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// ✅ NEW METHOD: Show Performance Statistics
        /// Display memory usage, scan times, etc.
        /// </summary>
        public void ShowPerformanceStats()
        {
            string stats = PerfMetrics.GetFormattedStats();
            MessageBox.Show(stats, "Performance Statistics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>
    /// ✅ NEW CLASS: Performance Metrics Tracking
    /// Monitors memory usage, scan times, toggle times, etc.
    /// </summary>
    public class PerformanceMetrics
    {
        public long ScanTime { get; set; } // milliseconds
        public long LastToggleTime { get; set; } // milliseconds
        public int AddressCount { get; set; }
        public DateTime LastScanTime { get; set; }

        private Process _currentProcess;

        public PerformanceMetrics()
        {
            _currentProcess = Process.GetCurrentProcess();
        }

        /// <summary>
        /// Get current memory usage in MB
        /// </summary>
        public double GetMemoryUsageMB()
        {
            return _currentProcess.WorkingSet64 / (1024.0 * 1024.0);
        }

        /// <summary>
        /// Get formatted statistics string
        /// </summary>
        public string GetFormattedStats()
        {
            return $@"
═══════════════════════════════════
        PERFORMANCE METRICS
═══════════════════════════════════

📊 Memory Usage: {GetMemoryUsageMB():F2} MB

⏱️  Last Scan Time: {ScanTime} ms
⏱️  Last Toggle Time: {LastToggleTime} ms

🎯 Addresses Found: {AddressCount}
⏰ Last Scan: {LastScanTime:yyyy-MM-dd HH:mm:ss}

═══════════════════════════════════

✅ TIP: Use Load & Toggle method for
   fastest performance!
   
Load once (30-40s) → Toggle instantly (1-5ms)
═══════════════════════════════════
            ";
        }

        /// <summary>
        /// Log statistics to debug console
        /// </summary>
        public void LogStats()
        {
            System.Diagnostics.Debug.WriteLine("═══════════════════════════════════");
            System.Diagnostics.Debug.WriteLine("PERFORMANCE METRICS");
            System.Diagnostics.Debug.WriteLine($"Memory: {GetMemoryUsageMB():F2} MB");
            System.Diagnostics.Debug.WriteLine($"Scan Time: {ScanTime} ms");
            System.Diagnostics.Debug.WriteLine($"Toggle Time: {LastToggleTime} ms");
            System.Diagnostics.Debug.WriteLine($"Addresses: {AddressCount}");
            System.Diagnostics.Debug.WriteLine("═══════════════════════════════════");
        }
    }
}
