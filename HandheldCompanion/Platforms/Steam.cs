﻿using HandheldCompanion.Shared;
using HandheldCompanion.Utils;
using HandheldCompanion.Watchers;
using Microsoft.Win32;
using Nefarius.Utilities.DeviceManagement.Drivers;
using Nefarius.Utilities.DeviceManagement.PnP;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Timers;

namespace HandheldCompanion.Platforms;

public class Steam : IPlatform
{
    private readonly RegistryWatcher SteamActiveUserWatcher = new(WatchedRegistry.CurrentUser, @"SOFTWARE\\Valve\\Steam\\ActiveProcess\\", "ActiveUser");
    private FileSystemWatcher SteamActiveUserFileWatcher = new();
    private Timer debounceTimer;

    private readonly int SteamAppsId = 413080;
    private SteamWatcher steamWatcher = new SteamWatcher();

    public Steam()
    {
        PlatformType = PlatformType.Steam;

        Name = "Steam";
        ExecutableName = "steam.exe";
        RunningName = "steamwebhelper.exe";

        // store specific modules
        Modules =
        [
            "steam.exe",
            "steamwebhelper.exe",
            "gameoverlayrenderer.dll",
            "gameoverlayrenderer64.dll",
            "steamclient.dll",
            "steamclient64.dll"
        ];

        // check if platform is installed
        InstallPath = RegistryUtils.GetString(@"SOFTWARE\Wow6432Node\Valve\Steam", "InstallPath");
        if (Path.Exists(InstallPath))
        {
            // update paths
            ExecutablePath = Path.Combine(InstallPath, ExecutableName);

            // check executable
            IsInstalled = File.Exists(ExecutablePath);
        }

        if (!IsInstalled)
        {
            LogManager.LogWarning("Steam is not available. You can get it from: {0}",
                "https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe");
            return;
        }

        // Initialize debounce timer
        debounceTimer = new Timer(1000); // 1 second delay
        debounceTimer.AutoReset = false; // Trigger only once per activation
        debounceTimer.Elapsed += DebounceTimer_Elapsed;
    }

    public override bool Start()
    {
        SteamActiveUserWatcher.RegistryChanged += ActiveUserWatcher_RegistryChanged;
        SteamActiveUserWatcher.StartWatching();
        steamWatcher.Start();

        return base.Start();
    }

    public override bool Stop(bool kill = false)
    {
        SteamActiveUserWatcher.StopWatching();
        steamWatcher.Stop();

        SteamActiveUserFileWatcher.Changed -= FileWatcher_Changed;
        SteamActiveUserFileWatcher.Dispose();

        debounceTimer.Stop();
        if (kill)
        {
            debounceTimer.Dispose();
            debounceTimer = null;
        }

        return base.Stop();
    }

    private void ActiveUserWatcher_RegistryChanged(object? sender, RegistryChangedEventArgs e)
    {
        // pull user id
        int userId = GetActiveProcessValue();
        if (userId == 0)
            return;

        // update paths
        SettingsPath = Path.Combine(InstallPath, "userdata", userId.ToString(), "config", "localconfig.vdf");
        if (!File.Exists(SettingsPath))
            return;

        // tear down the old watcher
        if (SteamActiveUserFileWatcher != null)
        {
            SteamActiveUserFileWatcher.Changed -= FileWatcher_Changed;
            SteamActiveUserFileWatcher.Dispose();
        }

        // update file watcher
        SteamActiveUserFileWatcher = new()
        {
            Path = Path.GetDirectoryName(SettingsPath),
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
        };
        SteamActiveUserFileWatcher.Changed += FileWatcher_Changed;

        ActiveFileWatch_Changed();
    }

    private void FileWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        if (debounceTimer is null)
            return;

        try
        {
            // Restart the debounce timer whenever a change is detected
            debounceTimer.Stop();
            debounceTimer.Start();
        }
        catch { }
    }

    private void DebounceTimer_Elapsed(object? sender, ElapsedEventArgs e)
    {
        ActiveFileWatch_Changed();
    }

    private void ActiveFileWatch_Changed()
    {
        int SteamInput = GetUseSteamControllerConfigValue();
        base.SettingsValueChaned("UseSteamControllerConfig", SteamInput);
    }

    private int GetActiveProcessValue()
    {
        try
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
            {
                if (key != null)
                {
                    object value = key.GetValue("ActiveUser");
                    if (value != null)
                    {
                        return int.Parse(value.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading registry: {ex.Message}");
        }

        return 0;
    }

    public int GetUseSteamControllerConfigValue()
    {
        try
        {
            // get settings content
            string content = File.ReadAllText(SettingsPath);

            string pattern = @"""apps""\s*\{.*?""413080""\s*\{.*?""UseSteamControllerConfig""\s*""(\d+)""";
            Match match = Regex.Match(content, pattern, RegexOptions.Singleline);

            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value);
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading file {SettingsPath}: {ex.Message}");
            return 0;
        }
    }

    public bool SetUseSteamControllerConfigValue(int newValue)
    {
        try
        {
            // get settings content
            string content = File.ReadAllText(SettingsPath);

            // Pattern to match the UseSteamControllerConfig value for app ID 413080
            string pattern = @"""apps""\s*\{.*?""413080""\s*\{.*?""UseSteamControllerConfig""\s*""(\d+)""";

            // Replace the matched value with the new value
            string updatedContent = Regex.Replace(content, pattern, m =>
            {
                return m.Value.Replace(m.Groups[1].Value, newValue.ToString());
            }, RegexOptions.Singleline);

            // Write the updated content back to the file
            File.WriteAllText(SettingsPath, updatedContent);

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating file {SettingsPath}: {ex.Message}");
            return false;
        }
    }

    public bool HasXboxDriversInstalled()
    {
        bool driversCheck = FilterDrivers.GetDeviceClassUpperFilters(DeviceClassIds.XnaComposite).Any(f => f.Equals("steamxbox"));
        bool registryCheck = RegistryUtils.SearchForKeyValue(@"SYSTEM\CurrentControlSet\Enum\ROOT\SYSTEM", "Service", "steamxbox");
        return driversCheck || registryCheck;
    }

    public override bool StartProcess()
    {
        if (!IsInstalled)
            return false;

        if (IsRunning)
            return false;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = ExecutablePath
        });

        return process is not null;
    }

    public override bool StopProcess()
    {
        if (!IsInstalled)
            return false;

        if (!IsRunning)
            return false;

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = ExecutablePath,
            ArgumentList = { "-shutdown" },
            WindowStyle = ProcessWindowStyle.Hidden,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        return process is not null;
    }
}