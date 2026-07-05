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



        private string[] TaskName = new string[] { "HD-Player" };
        private EnzoMem Memory = new EnzoMem();
        private string AimbotPattern = "00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 [...]";
        private long ReadOffset = 0xAF;   // Offset 124 in decimal
        private long WriteOffset = 0xAB;


        private readonly Dictionary<long, byte[]> OriginalValue1 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> OriginalValue2 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> ReplacedValue1 = new Dictionary<long, byte[]>();
        private readonly Dictionary<long, byte[]> ReplacedValue2 = new Dictionary<long, byte[]>();

        public void AimbotOFF()
        {
            if (OriginalValue1.Count == 0 && OriginalValue2.Count == 0)
            {
                UpdateLabel("Aimbot not activated yet", Color.Orange);
                return;
            }

            RestoreValues(OriginalValue1);
            RestoreValues(OriginalValue2);
            UpdateLabel("Aimbot Disabled", Color.Red);
        }
        public void AimbotON()
        {
            if (ReplacedValue1.Count == 0 && ReplacedValue2.Count == 0)
            {
                UpdateLabel("Aimbot not activated yet", Color.Orange);
                return;
            }

            RestoreValues(ReplacedValue1);
            RestoreValues(ReplacedValue2);
            UpdateLabel("Aimbot Enabled", Color.Green);
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

        private async void guna2Button1_Click(object sender, EventArgs e)
        {
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

                // ✅ FIXED: Initialize memory with optimal settings on first load
                Memory.Initialize(
                    maxThreads: Math.Max(2, Environment.ProcessorCount / 2), // Use 50% of cores, min 2
                    chunkSizeMB: 1,  // Reduce to 1MB for smoother scanning
                    useCache: true   // Keep cache enabled - DON'T clear it
                );

                // ✅ REMOVED: Memory.ClearScanCache(); - This was causing the 30-40 second delay!
                
                IEnumerable<long> addresses = await Memory.AoBScan(AimbotPattern);

                if (addresses == null || !addresses.Any())
                {
                    UpdateLabel("Error Contact Tempest For Help!", Color.Red);
                    return;
                }

                foreach (long addr in addresses)
                {
                    long readAddr = addr + ReadOffset;   // addr + 0x7C
                    long writeAddr = addr + WriteOffset; // addr + 0x80

                    byte[] readBytes = Memory.ReadMemory(readAddr, 4);
                    byte[] writeBytes = Memory.ReadMemory(writeAddr, 4);

                    if (readBytes == null || writeBytes == null)
                    {
                        MessageBox.Show("Failed to read memory.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        continue;
                    }

                    int readValue = BitConverter.ToInt32(readBytes, 0);
                    int writeValue = BitConverter.ToInt32(writeBytes, 0);

                    // Store original values
                    OriginalValue1[writeAddr] = writeBytes;
                    OriginalValue2[readAddr] = readBytes;

                    // Swap values - ORIGINAL AIMBOT LOGIC
                    Memory.WriteInt(writeAddr, readValue);
                    Memory.WriteInt(readAddr, writeValue);

                    // Store replaced values
                    ReplacedValue1[writeAddr] = BitConverter.GetBytes(readValue);
                    ReplacedValue2[readAddr] = BitConverter.GetBytes(writeValue);
                }

                stopwatch.Stop();
                UpdateLabel($"Aimbot Head Activated   {stopwatch.Elapsed.TotalSeconds:F3}s", Color.Green);
            }
            catch (Exception ex)
            {
                UpdateLabel($"Error: {ex.Message}", Color.Red);
            }
        }
    }
}
