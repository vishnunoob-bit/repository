using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Enzo
{
    public class EnzoMem
    {
        #region DLL Imports
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(ProcessAccessFlags dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, uint dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, uint dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenThread(ThreadAccess dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

        [DllImport("kernel32.dll")]
        public static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        public static extern int CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll")]
        public static extern IntPtr CreateRemoteThread(IntPtr hProcess, IntPtr lpThreadAttributes, uint dwStackSize, IntPtr lpStartAddress, IntPtr lpParameter, uint dwCreationFlags, IntPtr lpThreadId);
        #endregion

        #region Structures
        public struct PatternData
        {
            public byte[] pattern { get; set; }
            public byte[] mask { get; set; }
        }

        public struct MemoryPage
        {
            public IntPtr Start;
            public int Size;

            public MemoryPage(IntPtr start, int size)
            {
                Start = start;
                Size = size;
            }
        }

        public struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public UIntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        public class PatchInfo
        {
            public long Address { get; set; }
            public byte[] OriginalBytes { get; set; }
            public byte[] PatchedBytes { get; set; }
        }
        #endregion

        #region Enums
        [Flags]
        public enum ProcessAccessFlags
        {
            AllAccess = 0x001F0FFF,
            CreateProcess = 0x0080,
            CreateThread = 0x0002,
            DupHandle = 0x0040,
            QueryInformation = 0x0400,
            QueryLimitedInformation = 0x1000,
            SetInformation = 0x0200,
            SetQuota = 0x0100,
            SuspendResume = 0x0800,
            Terminate = 0x0001,
            VmOperation = 0x0008,
            VmRead = 0x0010,
            VmWrite = 0x0020,
            Synchronize = 0x00100000
        }

        [Flags]
        public enum ThreadAccess : int
        {
            TERMINATE = 0x0001,
            SUSPEND_RESUME = 0x0002,
            GET_CONTEXT = 0x0008,
            SET_CONTEXT = 0x0010,
            SET_INFORMATION = 0x0020,
            QUERY_INFORMATION = 0x0040,
            SET_THREAD_TOKEN = 0x0080,
            IMPERSONATE = 0x0100,
            DIRECT_IMPERSONATION = 0x0200
        }
        #endregion

        #region Fields
        public bool isPrivate;
        public int processId;
        public IntPtr _processHandle;
        private bool _enableCheck = true;
        public const uint MEM_COMMIT = 4096u;
        public const uint MEM_PRIVATE = 131072u;
        public const uint PAGE_READWRITE = 4u;

        // Fast offset patching storage
        private Dictionary<string, List<PatchInfo>> activePatchesStorage = new Dictionary<string, List<PatchInfo>>();

        // Load & Toggle storage - stores addresses for later patching
        private Dictionary<string, List<long>> loadedAddressesStorage = new Dictionary<string, List<long>>();

        // Scan optimization settings
        private int _maxThreads = Environment.ProcessorCount; // Use all CPU cores
        private int _chunkSize = 2 * 1024 * 1024; // 2MB chunks for smooth scanning
        private long _maxRegionSize = 10 * 1024 * 1024; // 10MB max region size

        // Scan cache to avoid re-scanning same patterns - PERMANENT until exe restart OR process change
        private Dictionary<string, List<long>> _scanCache = new Dictionary<string, List<long>>();
        private bool _useScanCache = true;

        // Track process to detect restarts
        private int _cachedProcessId = -1;
        #endregion

        #region Scan Configuration Methods
        /// <summary>
        /// Configure scan optimization settings
        /// </summary>
        public void ConfigureScanSettings(int? maxThreads = null, int? chunkSizeMB = null, bool? useCache = null)
        {
            if (maxThreads.HasValue && maxThreads.Value > 0)
                _maxThreads = maxThreads.Value;

            if (chunkSizeMB.HasValue && chunkSizeMB.Value > 0)
                _chunkSize = chunkSizeMB.Value * 1024 * 1024;

            if (useCache.HasValue)
                _useScanCache = useCache.Value;
        }

        /// <summary>
        /// Clear scan cache manually (optional - cache persists until exe restart)
        /// </summary>
        public void ClearScanCache()
        {
            _scanCache.Clear();
        }

        /// <summary>
        /// Get cached scan result if available
        /// </summary>
        private List<long> GetCachedScan(string pattern)
        {
            if (!_useScanCache)
                return null;

            if (_scanCache.ContainsKey(pattern))
            {
                return _scanCache[pattern];
            }
            return null;
        }

        /// <summary>
        /// Store scan result in cache - PERMANENT until exe restart
        /// </summary>
        private void CacheScanResult(string pattern, List<long> addresses)
        {
            if (!_useScanCache)
                return;

            _scanCache[pattern] = addresses;
        }

        /// <summary>
        /// Get count of cached patterns
        /// </summary>
        public int GetCachedPatternCount()
        {
            return _scanCache.Count;
        }

        /// <summary>
        /// Check if a pattern is cached
        /// </summary>
        public bool IsPatternCached(string pattern)
        {
            return _scanCache.ContainsKey(pattern);
        }
        #endregion

        #region Process Management
        public bool SetProcess(string[] processNames)
        {
            processId = 0;
            Process[] processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                string processName = process.ProcessName;
                if (Array.Exists(processNames, (string name) => name.Equals(processName, StringComparison.CurrentCultureIgnoreCase)))
                {
                    processId = process.Id;
                    break;
                }
            }
            if (processId <= 0)
            {
                return false;
            }

            // Check if process has changed (emulator restarted)
            if (_cachedProcessId != -1 && _cachedProcessId != processId)
            {
                // Process changed - clear all caches and active patches
                ClearAllOnProcessChange();
            }

            // Update cached process ID
            _cachedProcessId = processId;

            _processHandle = OpenProcess(ProcessAccessFlags.AllAccess, bInheritHandle: false, processId);
            if (_processHandle == IntPtr.Zero)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Clear all caches and active patches when process changes (emulator restart)
        /// </summary>
        private void ClearAllOnProcessChange()
        {
            try
            {
                // Clear scan cache - old addresses are invalid
                _scanCache.Clear();

                // Clear loaded addresses - old addresses are invalid
                loadedAddressesStorage.Clear();

                // Clear active patches - old patches are invalid
                activePatchesStorage.Clear();
            }
            catch
            {
                // Silent fail - ensure we don't crash on cleanup
            }
        }

        /// <summary>
        /// Check if process has changed since last operation
        /// </summary>
        public bool HasProcessChanged()
        {
            return _cachedProcessId != -1 && _cachedProcessId != processId;
        }

        /// <summary>
        /// Get current cached process ID
        /// </summary>
        public int GetCachedProcessId()
        {
            return _cachedProcessId;
        }

        public void CheckProcess()
        {
            if (!_enableCheck)
            {
                return;
            }
            foreach (ProcessThread thread in Process.GetProcessById(processId).Threads)
            {
                IntPtr intPtr = OpenThread(ThreadAccess.SUSPEND_RESUME, bInheritHandle: false, (uint)thread.Id);
                if (intPtr != IntPtr.Zero)
                {
                    int num = 0;
                    do
                    {
                        num = ResumeThread(intPtr);
                    }
                    while (num > 0);
                    CloseHandle(intPtr);
                }
            }
        }
        #endregion

        #region Fast AOB Scanning (Optimized for Smooth Performance)
        public async Task<IEnumerable<long>> AoBScan(string bytePattern)
        {
            return await AobScan(bytePattern);
        }

        private async Task<IEnumerable<long>> AobScan(string pattern)
        {
            // Check cache first
            var cached = GetCachedScan(pattern);
            if (cached != null)
            {
                return cached;
            }

            PatternData patternData = GetPatternDataFromPattern(pattern);
            List<long> addressRet = new List<long>();

            await Task.Run(delegate
            {
                List<MemoryPage> pages = new List<MemoryPage>();
                IntPtr ptr = IntPtr.Zero;
                MEMORY_BASIC_INFORMATION mbi;

                // Collect all readable pages with size optimization
                while (VirtualQueryEx(_processHandle, ptr, out mbi, (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == (uint)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)))
                {
                    if (CanReadPage(mbi))
                    {
                        long regionSize = (long)mbi.RegionSize.ToUInt64();

                        // Split large regions into chunks to prevent memory issues and lag
                        if (regionSize > _maxRegionSize)
                        {
                            long offset = 0;
                            while (offset < regionSize)
                            {
                                int chunkSize = (int)Math.Min(_chunkSize, regionSize - offset);
                                pages.Add(new MemoryPage((IntPtr)((long)ptr + offset), chunkSize));
                                offset += chunkSize;
                            }
                        }
                        else
                        {
                            pages.Add(new MemoryPage(ptr, (int)regionSize));
                        }
                    }
                    ptr = (IntPtr)((long)mbi.BaseAddress + (long)(ulong)mbi.RegionSize);
                }

                int patternLength = patternData.pattern.Length;

                // Use parallel processing with controlled thread count for smooth performance
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = _maxThreads
                };

                Parallel.ForEach(pages, options, page =>
                {
                    try
                    {
                        byte[] buffer = new byte[page.Size];

                        if (ReadProcessMemory(_processHandle, page.Start, buffer, (IntPtr)page.Size, out var bytesRead))
                        {
                            int actualSize = (int)bytesRead;

                            // Process the buffer in smaller chunks to reduce CPU spikes
                            int searchOffset = 0;
                            int searchChunkSize = Math.Min(actualSize, 512 * 1024); // 512KB search chunks

                            while (searchOffset < actualSize - patternLength)
                            {
                                int searchEnd = Math.Min(searchOffset + searchChunkSize, actualSize);
                                int index = searchOffset - patternLength;

                                do
                                {
                                    index = FindPattern(buffer, patternData.pattern, patternData.mask, index + patternLength, searchEnd);
                                    if (index >= 0 && index < searchEnd)
                                    {
                                        lock (addressRet)
                                        {
                                            addressRet.Add((long)page.Start + index);
                                        }
                                    }
                                }
                                while (index >= 0 && index < searchEnd - patternLength);

                                searchOffset = searchEnd - patternLength;

                                // Small delay to prevent CPU hogging
                                if (searchOffset < actualSize - patternLength)
                                {
                                    System.Threading.Thread.Sleep(0); // Yield to other threads
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Continue with other pages on error
                    }
                });
            });

            var result = addressRet.OrderBy((long c) => c).ToList();

            // Cache the result for future use
            CacheScanResult(pattern, result);

            return result;
        }

        public bool CanReadPage(MEMORY_BASIC_INFORMATION page)
        {
            if (page.State == 4096 && page.Type == 131072)
            {
                return page.Protect == 4;
            }
            return false;
        }

        private PatternData GetPatternDataFromPattern(string pattern)
        {
            string[] patternParts = pattern.Split(' ');
            PatternData patternData = new PatternData
            {
                pattern = patternParts.Select(s => s.Contains("??") ? (byte)0x00 : byte.Parse(s, NumberStyles.HexNumber)).ToArray(),
                mask = patternParts.Select(s => s.Contains("??") ? (byte)0x00 : (byte)0xFF).ToArray()
            };
            return patternData;
        }

        private int FindPattern(byte[] body, byte[] pattern, byte[] masks, int start = 0, int end = -1)
        {
            int result = -1;

            if (end == -1)
                end = body.Length;

            if (body.Length == 0 || pattern.Length == 0 || start > end - pattern.Length || pattern.Length > body.Length)
            {
                return result;
            }

            // Optimized pattern matching with early exit
            for (int i = start; i <= end - pattern.Length; i++)
            {
                // Quick first byte check
                if ((body[i] & masks[0]) != (pattern[0] & masks[0]))
                {
                    continue;
                }

                // Check remaining bytes
                bool match = true;
                for (int j = 1; j < pattern.Length; j++)
                {
                    if ((body[i + j] & masks[j]) != (pattern[j] & masks[j]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    result = i;
                    break;
                }
            }
            return result;
        }
        #endregion

        #region Memory Read/Write Operations
        public bool AobReplace(long address, string bytePattern)
        {
            try
            {
                byte[] array = StringToByteArray(bytePattern);
                return WriteProcessMemory(_processHandle, (IntPtr)address, array, (IntPtr)array.Length, IntPtr.Zero);
            }
            catch (Exception)
            {
            }
            return false;
        }

        public bool AobReplace(long address, int bytePattern)
        {
            byte[] bytes = BitConverter.GetBytes(bytePattern);
            return WriteProcessMemory(_processHandle, (IntPtr)address, bytes, (IntPtr)bytes.Length, IntPtr.Zero);
        }

        public async Task<int> ReadIntAsync(long addressToRead)
        {
            return await Task.Run(() => ReadInt(addressToRead));
        }

        public int ReadInt(long addressToRead)
        {
            byte[] array = new byte[4];
            if (ReadProcessMemory(_processHandle, (IntPtr)addressToRead, array, (IntPtr)array.Length, out var _))
            {
                return BitConverter.ToInt32(array, 0);
            }
            return 0;
        }

        public float ReadFloat(long addressToRead)
        {
            byte[] array = new byte[4];
            if (ReadProcessMemory(_processHandle, (IntPtr)addressToRead, array, (IntPtr)array.Length, out var _))
            {
                return BitConverter.ToSingle(array, 0);
            }
            return 0f;
        }

        public byte ReadHexByte(long addressToRead)
        {
            byte[] array = new byte[1];
            if (ReadProcessMemory(_processHandle, (IntPtr)addressToRead, array, (IntPtr)array.Length, out var _))
            {
                return array[0];
            }
            return 0;
        }

        public short ReadInt16(long addressToRead)
        {
            byte[] array = new byte[2];
            if (ReadProcessMemory(_processHandle, (IntPtr)addressToRead, array, (IntPtr)array.Length, out var _))
            {
                return BitConverter.ToInt16(array, 0);
            }
            return 0;
        }

        public string ReadString(long addressToRead, int size)
        {
            byte[] buffer = new byte[size];
            IntPtr bytesRead;
            bool readSuccess = ReadProcessMemory(_processHandle, (IntPtr)addressToRead, buffer, (IntPtr)size, out bytesRead);
            if (readSuccess && bytesRead.ToInt64() == size)
            {
                return BitConverter.ToString(buffer).Replace("-", " ");
            }
            return "";
        }

        public byte[] ReadMemory(long address, int size)
        {
            byte[] buffer = new byte[size];
            IntPtr bytesRead;
            bool success = ReadProcessMemory(_processHandle, (IntPtr)address, buffer, (IntPtr)size, out bytesRead);
            if (!success || bytesRead.ToInt32() != size)
            {
                return null;
            }
            return buffer;
        }

        public bool WriteBytes(long address, byte[] bytes)
        {
            return WriteProcessMemory(_processHandle, (IntPtr)address, bytes, (IntPtr)bytes.Length, IntPtr.Zero);
        }

        public bool WriteInt(long address, int value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            return WriteProcessMemory(_processHandle, (IntPtr)address, bytes, (IntPtr)bytes.Length, IntPtr.Zero);
        }

        public bool WriteFloat(long address, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            return WriteProcessMemory(_processHandle, (IntPtr)address, bytes, (IntPtr)bytes.Length, IntPtr.Zero);
        }

        private byte[] StringToByteArray(string hexString)
        {
            return (from hex in hexString.Split(' ')
                    select byte.Parse(hex, NumberStyles.HexNumber)).ToArray();
        }
        #endregion

        #region METHOD 1: Fast Offset Patching (Search Once + Patch Offsets Immediately)
        /// <summary>
        /// Fast offset patching - Search once and immediately apply offset patches
        /// Best for features that need quick toggle on/off
        /// </summary>
        public async Task<bool> FastOffsetPatch(string featureName, string basePattern, Dictionary<long, byte[]> offsets, bool enable)
        {
            try
            {
                // If disabling, restore original values (super fast - no scanning)
                if (!enable)
                {
                    if (!activePatchesStorage.ContainsKey(featureName))
                    {
                        return false;
                    }

                    foreach (var patch in activePatchesStorage[featureName])
                    {
                        if (patch.OriginalBytes != null && patch.OriginalBytes.Length > 0)
                        {
                            WriteBytes(patch.Address, patch.OriginalBytes);
                        }
                    }

                    activePatchesStorage.Remove(featureName);
                    return true;
                }

                // Enabling - do fast scan and apply offsets immediately
                IEnumerable<long> addresses = await AoBScan(basePattern);

                if (addresses == null || !addresses.Any())
                {
                    return false;
                }

                List<PatchInfo> patches = new List<PatchInfo>();

                // Apply patches to all found addresses with offsets
                foreach (long baseAddr in addresses)
                {
                    foreach (var offset in offsets)
                    {
                        long targetAddr = baseAddr + offset.Key;
                        byte[] newBytes = offset.Value;

                        // Safety check - skip if newBytes is null or empty
                        if (newBytes == null || newBytes.Length == 0)
                        {
                            continue;
                        }

                        byte[] originalBytes = ReadMemory(targetAddr, newBytes.Length);

                        if (originalBytes == null || originalBytes.Length != newBytes.Length)
                        {
                            continue;
                        }

                        patches.Add(new PatchInfo
                        {
                            Address = targetAddr,
                            OriginalBytes = originalBytes,
                            PatchedBytes = newBytes
                        });

                        WriteBytes(targetAddr, newBytes);
                    }
                }

                // Only store if we successfully created patches
                if (patches.Count > 0)
                {
                    activePatchesStorage[featureName] = patches;
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }
        #endregion

        #region METHOD 2: Load & Toggle System (Search Once, Store, Toggle Later)
        /// <summary>
        /// STEP 1: Load addresses by scanning pattern and store them
        /// Use this for the "Load" button
        /// </summary>
        public async Task<int> LoadAddresses(string featureName, string basePattern)
        {
            try
            {
                IEnumerable<long> addresses = await AoBScan(basePattern);

                if (addresses == null || !addresses.Any())
                {
                    return 0;
                }

                loadedAddressesStorage[featureName] = addresses.ToList();
                return addresses.Count();
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// STEP 2: Toggle patches on/off using pre-loaded addresses
        /// Use this for the "On/Off" toggle
        /// </summary>
        public bool ToggleLoadedFeature(string featureName, Dictionary<long, byte[]> offsets, bool enable)
        {
            try
            {
                // Check if addresses are loaded
                if (!loadedAddressesStorage.ContainsKey(featureName))
                {
                    return false;
                }

                List<long> addresses = loadedAddressesStorage[featureName];

                if (addresses == null || addresses.Count == 0)
                {
                    return false;
                }

                // If disabling, restore original values
                if (!enable)
                {
                    if (!activePatchesStorage.ContainsKey(featureName))
                    {
                        return false;
                    }

                    foreach (var patch in activePatchesStorage[featureName])
                    {
                        if (patch.OriginalBytes != null && patch.OriginalBytes.Length > 0)
                        {
                            WriteBytes(patch.Address, patch.OriginalBytes);
                        }
                    }

                    activePatchesStorage.Remove(featureName);
                    return true;
                }

                // Enabling - apply patches to stored addresses
                List<PatchInfo> patches = new List<PatchInfo>();

                foreach (long baseAddr in addresses)
                {
                    foreach (var offset in offsets)
                    {
                        long targetAddr = baseAddr + offset.Key;
                        byte[] newBytes = offset.Value;

                        // Safety check - skip if newBytes is null or empty
                        if (newBytes == null || newBytes.Length == 0)
                        {
                            continue;
                        }

                        byte[] originalBytes = ReadMemory(targetAddr, newBytes.Length);

                        if (originalBytes == null || originalBytes.Length != newBytes.Length)
                        {
                            continue;
                        }

                        patches.Add(new PatchInfo
                        {
                            Address = targetAddr,
                            OriginalBytes = originalBytes,
                            PatchedBytes = newBytes
                        });

                        WriteBytes(targetAddr, newBytes);
                    }
                }

                // Only store if we successfully created patches
                if (patches.Count > 0)
                {
                    activePatchesStorage[featureName] = patches;
                    return true;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Check if addresses are loaded for a feature
        /// </summary>
        public bool IsAddressesLoaded(string featureName)
        {
            return loadedAddressesStorage.ContainsKey(featureName) && loadedAddressesStorage[featureName].Count > 0;
        }

        /// <summary>
        /// Get count of loaded addresses for a feature
        /// </summary>
        public int GetLoadedAddressCount(string featureName)
        {
            if (!loadedAddressesStorage.ContainsKey(featureName))
                return 0;
            return loadedAddressesStorage[featureName].Count;
        }
        #endregion

        #region Utility Methods
        /// <summary>
        /// Check if a feature is currently active
        /// </summary>
        public bool IsFeatureActive(string featureName)
        {
            return activePatchesStorage.ContainsKey(featureName);
        }

        /// <summary>
        /// Get count of active patches for a feature
        /// </summary>
        public int GetPatchCount(string featureName)
        {
            if (!activePatchesStorage.ContainsKey(featureName))
                return 0;
            return activePatchesStorage[featureName].Count;
        }

        /// <summary>
        /// Clear all active patches
        /// </summary>
        public int ClearAllPatches()
        {
            int count = 0;
            try
            {
                foreach (var featureName in activePatchesStorage.Keys.ToList())
                {
                    foreach (var patch in activePatchesStorage[featureName])
                    {
                        if (patch.OriginalBytes != null && patch.OriginalBytes.Length > 0)
                        {
                            WriteBytes(patch.Address, patch.OriginalBytes);
                            count++;
                        }
                    }
                }
                activePatchesStorage.Clear();
            }
            catch (Exception)
            {
                // Silent fail - return count of patches cleared so far
            }
            return count;
        }

        /// <summary>
        /// Get list of all active features
        /// </summary>
        public List<string> GetActiveFeatures()
        {
            return activePatchesStorage.Keys.ToList();
        }

        /// <summary>
        /// Clear loaded addresses for a specific feature
        /// </summary>
        public void ClearLoadedAddresses(string featureName)
        {
            if (loadedAddressesStorage.ContainsKey(featureName))
            {
                loadedAddressesStorage.Remove(featureName);
            }
        }

        /// <summary>
        /// Clear all loaded addresses
        /// </summary>
        public void ClearAllLoadedAddresses()
        {
            loadedAddressesStorage.Clear();
        }
        #endregion

        #region Initialization and Configuration
        /// <summary>
        /// Initialize EnzoMem with optimal settings
        /// Call this when your application starts (Form_Load or constructor)
        /// </summary>
        public void Initialize()
        {
            // Configure for optimal performance
            ConfigureScanSettings(
                maxThreads: Environment.ProcessorCount,  // Use all CPU cores
                chunkSizeMB: 2,                          // 2MB chunks for smooth scanning
                useCache: true                           // Enable smart caching
            );
        }

        /// <summary>
        /// Initialize with custom settings
        /// </summary>
        public void Initialize(int maxThreads, int chunkSizeMB, bool useCache)
        {
            ConfigureScanSettings(maxThreads, chunkSizeMB, useCache);
        }
        #endregion

        #region METHOD 3: Multi-Pattern Patching (Patch Multiple Patterns with Single Button)
        /// <summary>
        /// Patch multiple patterns with different offsets in parallel - Super fast!
        /// Perfect for activating multiple features with one button
        /// </summary>
        public async Task<MultiPatchResult> FastMultiPatch(string featureName, List<PatternPatch> patterns, bool enable)
        {
            MultiPatchResult result = new MultiPatchResult();

            try
            {
                // If disabling, restore all original values
                if (!enable)
                {
                    if (!activePatchesStorage.ContainsKey(featureName))
                    {
                        result.Success = false;
                        result.Message = "Feature not active";
                        return result;
                    }

                    foreach (var patch in activePatchesStorage[featureName])
                    {
                        if (patch.OriginalBytes != null && patch.OriginalBytes.Length > 0)
                        {
                            WriteBytes(patch.Address, patch.OriginalBytes);
                        }
                    }

                    activePatchesStorage.Remove(featureName);
                    result.Success = true;
                    result.Message = "All patches deactivated";
                    return result;
                }

                // Enabling - scan all patterns in parallel
                Stopwatch totalTimer = new Stopwatch();
                totalTimer.Start();

                List<PatchInfo> allPatches = new List<PatchInfo>();
                object lockObj = new object();

                // Process all patterns in parallel for maximum speed
                await Task.Run(() =>
                {
                    Parallel.ForEach(patterns, pattern =>
                    {
                        try
                        {
                            // Scan for this pattern
                            var addresses = AobScan(pattern.Pattern).Result;

                            if (addresses != null && addresses.Any())
                            {
                                foreach (long baseAddr in addresses)
                                {
                                    foreach (var offset in pattern.Offsets)
                                    {
                                        long targetAddr = baseAddr + offset.Key;
                                        byte[] newBytes = offset.Value;

                                        if (newBytes == null || newBytes.Length == 0)
                                            continue;

                                        byte[] originalBytes = ReadMemory(targetAddr, newBytes.Length);

                                        if (originalBytes == null || originalBytes.Length != newBytes.Length)
                                            continue;

                                        lock (lockObj)
                                        {
                                            allPatches.Add(new PatchInfo
                                            {
                                                Address = targetAddr,
                                                OriginalBytes = originalBytes,
                                                PatchedBytes = newBytes
                                            });
                                        }

                                        WriteBytes(targetAddr, newBytes);
                                    }
                                }

                                lock (lockObj)
                                {
                                    result.PatternsFound++;
                                }
                            }
                        }
                        catch
                        {
                            // Continue with other patterns even if one fails
                        }
                    });
                });

                totalTimer.Stop();

                if (allPatches.Count > 0)
                {
                    activePatchesStorage[featureName] = allPatches;
                    result.Success = true;
                    result.PatchesApplied = allPatches.Count;
                    result.ElapsedSeconds = totalTimer.Elapsed.TotalSeconds;
                    result.Message = $"Applied {allPatches.Count} patches from {result.PatternsFound}/{patterns.Count} patterns";
                }
                else
                {
                    result.Success = false;
                    result.Message = "No patches applied - patterns not found";
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Error: {ex.Message}";
                return result;
            }
        }

        /// <summary>
        /// Load multiple patterns for later toggling
        /// </summary>
        public async Task<MultiPatchResult> LoadMultiplePatterns(string featureName, List<string> patterns)
        {
            MultiPatchResult result = new MultiPatchResult();

            try
            {
                Stopwatch totalTimer = new Stopwatch();
                totalTimer.Start();

                List<long> allAddresses = new List<long>();
                object lockObj = new object();

                await Task.Run(() =>
                {
                    Parallel.ForEach(patterns, pattern =>
                    {
                        try
                        {
                            var addresses = AobScan(pattern).Result;

                            if (addresses != null && addresses.Any())
                            {
                                lock (lockObj)
                                {
                                    allAddresses.AddRange(addresses);
                                    result.PatternsFound++;
                                }
                            }
                        }
                        catch
                        {
                            // Continue with other patterns
                        }
                    });
                });

                totalTimer.Stop();

                if (allAddresses.Count > 0)
                {
                    loadedAddressesStorage[featureName] = allAddresses;
                    result.Success = true;
                    result.PatchesApplied = allAddresses.Count;
                    result.ElapsedSeconds = totalTimer.Elapsed.TotalSeconds;
                    result.Message = $"Loaded {allAddresses.Count} addresses from {result.PatternsFound}/{patterns.Count} patterns";
                }
                else
                {
                    result.Success = false;
                    result.Message = "No addresses found";
                }

                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"Error: {ex.Message}";
                return result;
            }
        }
        #endregion

        #region Helper Classes for Multi-Pattern Patching
        /// <summary>
        /// Represents a pattern with its offsets for multi-patching
        /// </summary>
        public class PatternPatch
        {
            public string Pattern { get; set; }
            public Dictionary<long, byte[]> Offsets { get; set; }

            public PatternPatch(string pattern, Dictionary<long, byte[]> offsets)
            {
                Pattern = pattern;
                Offsets = offsets;
            }
        }

        /// <summary>
        /// Result of multi-pattern patching operation
        /// </summary>
        public class MultiPatchResult
        {
            public bool Success { get; set; }
            public int PatternsFound { get; set; }
            public int PatchesApplied { get; set; }
            public double ElapsedSeconds { get; set; }
            public string Message { get; set; }

            public MultiPatchResult()
            {
                Success = false;
                PatternsFound = 0;
                PatchesApplied = 0;
                ElapsedSeconds = 0;
                Message = string.Empty;
            }
        }
        #endregion
    }
}