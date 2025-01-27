using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reactive.Concurrency;
using System.Text.Json;
using System.Threading.Tasks;
using Humanizer;
using Humanizer.Bytes;
using Mono.Unix;
using ReactiveUI;
using Serilog;
using UnitystationLauncher.Exceptions;
using UnitystationLauncher.Infrastructure;
using UnitystationLauncher.Models;
using UnitystationLauncher.Models.Api;
using UnitystationLauncher.Models.ConfigFile;
using UnitystationLauncher.Models.Enums;
using UnitystationLauncher.Services.Interface;

namespace UnitystationLauncher.Services;

public class InstallationService : IInstallationService
{
    private readonly HttpClient _httpClient;
    private readonly IEnvironmentService _environmentService;
    private readonly IPreferencesService _preferencesService;
    private readonly IServerService _serverService;

    private readonly List<Download> _downloads;
    private List<Installation> _installations = new();
    private readonly string _installationsJsonFilePath;

    public InstallationService(HttpClient httpClient, IPreferencesService preferencesService, IEnvironmentService environmentService, IServerService serverService)
    {
        _httpClient = httpClient;
        _preferencesService = preferencesService;
        _environmentService = environmentService;
        _serverService = serverService;
        _downloads = new();
        _installationsJsonFilePath = Path.Combine(_environmentService.GetUserdataDirectory(), "installations.json");

        ReadInstallations();

        Preferences preferences = _preferencesService.GetPreferences();
        SetupInstallationPath(preferences.InstallationPath);

        CleanupOldVersions(true);
    }

    #region Public interface
    public List<Installation> GetInstallations()
    {
        return _installations;
    }

    public Installation? GetInstallation(string forkName, int buildVersion)
    {
        return _installations.FirstOrDefault(i => i.ForkName == forkName && i.BuildVersion == buildVersion);
    }

    public Download? GetInProgressDownload(string forkName, int buildVersion)
    {
        return _downloads.FirstOrDefault(d =>
            d.Active
            && d.ForkName == forkName
            && d.BuildVersion == buildVersion);
    }

    public (Download?, string) DownloadInstallation(Server server)
    {
        string? downloadUrl = server.GetDownloadUrl(_environmentService);
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            const string failureReason = "Empty or missing download url for server.";
            Log.Warning(failureReason + $" ServerName: {server.ServerName}");
            return (null, failureReason);
        }

        Download? download = GetInProgressDownload(server.ForkName, server.BuildVersion);

        if (download != null)
        {
            Log.Warning($"Download already in progress. ForkName={server.ForkName} BuildVersion={server.BuildVersion}");
            return (download, string.Empty);
        }

        string installationBasePath = _preferencesService.GetPreferences().InstallationPath;
        // should be something like {basePath}/{forkName}/{version}
        string installationPath = Path.Combine(Path.Combine(installationBasePath, server.ForkName), server.BuildVersion.ToString());

        download = new(downloadUrl, installationPath, server.ForkName, server.BuildVersion);

        (bool canStartDownload, string cantDownloadReason) = CanStartDownload(download);

        if (!canStartDownload)
        {
            return (null, cantDownloadReason);
        }

        Download? itemToRemove = _downloads.FirstOrDefault(d => d.ForkName == server.ForkName && d.BuildVersion == server.BuildVersion);
        if (itemToRemove != null)
        {
            _downloads.Remove(itemToRemove);
        }

        _downloads.Add(download);
        download.Active = true;

        RxApp.MainThreadScheduler.ScheduleAsync((_, _) => StartDownloadAsync(download));
        return (download, string.Empty);
    }

    public (bool, string) StartInstallation(Guid installationId, string? server = null, short? port = null)
    {
        Installation? installation = GetInstallationById(installationId);
        if (installation == null)
        {
            const string failureReason = "Installation not found.";
            Log.Warning(failureReason + $" ID: {installationId}");
            return (false, failureReason);
        }

        string? executable = FindExecutable(installation.InstallationPath);
        if (string.IsNullOrWhiteSpace(executable))
        {
            const string failureReason = "Couldn't find executable to start.";
            Log.Warning(failureReason + $" Installation Path: {installation.InstallationPath ?? "null"}");
            return (false, failureReason);
        }

        EnsureExecutableFlagOnUnixSystems(executable);

        string arguments = GetArguments(server, port);
        ProcessStartInfo? startInfo = _environmentService.GetGameProcessStartInfo(executable, arguments);

        if (startInfo == null)
        {
            const string failureReason = "Unhandled platform.";
            Log.Warning(failureReason + $" Platform: {Enum.GetName(_environmentService.GetCurrentEnvironment())}");
            return (false, failureReason);
        }

        startInfo.UseShellExecute = false;
        Process process = new()
        {
            StartInfo = startInfo
        };

        process.Start();
        UpdateLastPlayedTime(installation);

        return (true, string.Empty);
    }

    public (bool, string) DeleteInstallation(Guid installationId)
    {
        Installation? installation = GetInstallationById(installationId);
        if (installation == null)
        {
            const string failureReason = "Installation not found.";
            Log.Warning(failureReason + $" ID: {installationId}");
            return (false, failureReason);
        }

        if (string.IsNullOrWhiteSpace(installation.InstallationPath))
        {
            const string failureReason = "Installation path is null.";
            Log.Warning(failureReason + $" Path=: {installation.InstallationPath ?? "null"}");
            return (false, failureReason);
        }

        if (Directory.Exists(installation.InstallationPath))
        {
            Log.Information($"Deleting installation. Fork={installation.ForkName} Version={installation.BuildVersion} Path={installation.InstallationPath}");
            if (_environmentService.GetCurrentEnvironment() is CurrentEnvironment.WindowsStandalone)
            {
                DeleteWindowsFolder(installation.InstallationPath);
            }
            else
            {
                Process process = new()
                {
                    StartInfo = new("/bin/bash", $"-c \" rm -r '{installation.InstallationPath}'; \"")
                    {
                        UseShellExecute = false
                    }
                };

                process.Start();
            }
        }

        _installations.Remove(installation);
        WriteInstallations();

        return (true, string.Empty);
    }

    public (bool, string) CleanupOldVersions(bool isAutoRemoveAction)
    {
        if (isAutoRemoveAction && !_preferencesService.GetPreferences().AutoRemove)
        {
            const string failureReason = "AutoRemove is disabled for installations.";
            Log.Information(failureReason);
            return (false, failureReason);
        }

        foreach (Installation installation in _installations)
        {
            if (_serverService.IsInstallationInUse(installation) || installation.RecentlyUsed)
            {
                continue;
            }

            DeleteInstallation(installation.InstallationId);
        }

        return (true, string.Empty);
    }

    public bool MoveInstallations(string newBasePath)
    {
        SetupInstallationPath(newBasePath);

        string oldBasePath = _preferencesService.GetPreferences().InstallationPath;

        foreach (Installation installation in _installations.Where(i => i.InstallationPath?.StartsWith(oldBasePath) ?? false))
        {
            string? oldPath = installation.InstallationPath;
            if (string.IsNullOrWhiteSpace(oldPath))
            {
                continue;
            }

            string newPath = oldPath.Replace(oldBasePath, newBasePath);

            try
            {
                if (!Directory.Exists(oldPath))
                {
                    Log.Error($"Directory does not exist: OldPath={oldPath}");
                    continue;
                }

                CreateParentDirectory(newPath);

                if (Directory.Exists(newPath))
                {
                    Log.Warning($"New path for server is already in use! NewPath={newPath}");
                    return false;
                }

                Directory.Move(
                    oldPath,
                    newPath
                );

                installation.InstallationPath = newPath;
            }
            catch (Exception e)
            {
                Log.Error($"Error while moving installation: OldInstallationPath='{oldPath}' NewInstallationPath='{newPath}' Error='{e.Message}'");
                return false;
            }
        }

        WriteInstallations();
        return true;
    }

    public (bool, string) IsValidInstallationBasePath(string path)
    {
        bool isValid = true;
        string invalidReason = string.Empty;


        if (!path.All(char.IsAscii))
        {
            isValid = false;
            invalidReason += "Path contains non-ASCII characters.\n";
        }

        try
        {
            if (Directory.Exists(path))
            {
                string testFilePath;
                do
                {
                    testFilePath = Path.Combine(path, $"StationHubTestFile-{Guid.NewGuid()}");
                } while (File.Exists(testFilePath));

                File.WriteAllText(testFilePath, "This is just to test if StationHub has write access here. \n"
                                                + "When you change your installation directory this is written and should be deleted after. \n"
                                                + "For some reason that didn't work, so I'll just let you know that this file is safe to delete.");
                File.Delete(testFilePath);
            }
            else
            {
                Directory.CreateDirectory(path);
            }
        }
        catch (Exception)
        {
            return (false, "No write access to the selected directory.");
        }

        return (isValid, invalidReason);
    }
    #endregion

    #region Private Helpers
    private void ReadInstallations()
    {
        List<Installation>? installations = null;

        if (File.Exists(_installationsJsonFilePath))
        {
            string json = File.ReadAllText(_installationsJsonFilePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                installations = JsonSerializer.Deserialize<List<Installation>>(json);
            }
            Log.Debug("Installations JSON read");
        }

        _installations = installations ?? new();
    }

    private void UpdateLastPlayedTime(Installation installation)
    {
        installation.LastPlayedDate = DateTime.Now;
        WriteInstallations();
    }

    private void WriteInstallations()
    {
        string json = JsonSerializer.Serialize(_installations);
        File.WriteAllText(_installationsJsonFilePath, json);
        Log.Debug("Installations JSON written");
    }

    private void CreateParentDirectory(string path)
    {
        DirectoryInfo directoryInfo = new(path);
        string? parentDir = directoryInfo.Parent?.FullName;

        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }
    }

    private Installation? GetInstallationById(Guid installationId)
    {
        return _installations.FirstOrDefault(i => i.InstallationId == installationId);
    }

    private (bool, string) CanStartDownload(Download download)
    {
        if (Directory.Exists(download.InstallPath))
        {
            const string failureReason = "Installation path already occupied";
            Log.Warning(failureReason);
            return (false, failureReason);
        }

        if (string.IsNullOrWhiteSpace(download.DownloadUrl))
        {
            const string failureReason = "Null or empty download url";
            Log.Warning(failureReason);
            return (false, failureReason);
        }

        return (true, string.Empty);
    }

    private string GetArguments(string? server, long? port)
    {
        string arguments = string.Empty;

        if (!string.IsNullOrWhiteSpace(server))
        {
            arguments += $"--server {server}";

            if (port.HasValue)
            {
                arguments += $" --port {port}";
            }
        }

        return arguments;
    }

    private async Task StartDownloadAsync(Download download)
    {
        Log.Information("Download requested, Installation Path '{Path}', Url '{Url}'", download.InstallPath, download.DownloadUrl);
        try
        {
            Directory.CreateDirectory(download.InstallPath);
            Log.Information("Download started...");
            HttpResponseMessage request = await _httpClient.GetAsync(download.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            await using Stream responseStream = await request.Content.ReadAsStreamAsync();
            Log.Information("Download connection established");
            await using ProgressStream progressStream = new(responseStream);
            download.Size = request.Content.Headers.ContentLength ??
                            throw new ContentLengthNullException(download.DownloadUrl);

            using IDisposable logProgressDisposable = LogProgress(progressStream, download);

            using IDisposable progressDisposable = progressStream.Progress
                .Subscribe(p => { download.Downloaded = p; });

            await Task.Run(() =>
            {
                Log.Information("Extracting...");
                try
                {
                    ZipArchive archive = new(progressStream);

                    // TODO: Enable extraction cancelling
                    archive.ExtractToDirectory(download.InstallPath, true);

                    Log.Information("Download completed");

                    _installations.Add(new()
                    {
                        BuildVersion = download.BuildVersion,
                        ForkName = download.ForkName,
                        InstallationId = Guid.NewGuid(),
                        InstallationPath = download.InstallPath,
                        LastPlayedDate = DateTime.Now
                    });

                    WriteInstallations();
                    EnsureExecutableFlagOnUnixSystems(download.InstallPath);
                }
                catch
                {
                    Log.Information("Extracting stopped");
                }
            });
        }
        catch (Exception e)
        {
            Log.Error(e, "Failed to download Url '{Url}'", download.DownloadUrl);
        }
        finally
        {
            download.Active = false;
            WriteInstallations();
        }
    }

    private IDisposable LogProgress(ProgressStream progressStream, Download download)
    {
        long lastPosition = 0L;
        DateTime lastTime = DateTime.Now;

        return progressStream.Progress
            .Subscribe(pos =>
            {
                DateTime time = DateTime.Now;
                long deltaPos = pos - lastPosition;
                TimeSpan deltaTime = time - lastTime;
                long deltaPercentage = deltaPos * 100 / download.Size;
                long percentage = pos * 100 / download.Size;

                if (pos != download.Size && deltaPercentage < 25 && deltaTime.TotalSeconds < .25)
                {
                    return;
                }

                ByteRate speed = deltaPos.Bytes().Per(deltaTime);
                Log.Information("Progress: {ProgressPercent}%, Speed = {DownloadSpeed}",
                    percentage, speed.Humanize("#.##"));

                lastPosition = pos;
                lastTime = time;
            });
    }

    private string? FindExecutable(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
        {
            return null;
        }

        return _environmentService.GetCurrentEnvironment() switch
        {
            CurrentEnvironment.WindowsStandalone
                => Path.Combine(installPath, "Unitystation.exe"),
            CurrentEnvironment.MacOsStandalone
                => Path.Combine(installPath, "Unitystation.app", "Contents", "MacOS", "unitystation"),
            CurrentEnvironment.LinuxStandalone or CurrentEnvironment.LinuxFlatpak
                => Path.Combine(installPath, "Unitystation"),
            _ => null
        };
    }

    private void DeleteWindowsFolder(string targetDir)
    {
        File.SetAttributes(targetDir, FileAttributes.Normal);

        string[] files = Directory.GetFiles(targetDir);
        string[] dirs = Directory.GetDirectories(targetDir);

        foreach (string file in files)
        {
            File.SetAttributes(file, FileAttributes.Normal);
            File.Delete(file);
        }

        foreach (string dir in dirs)
        {
            DeleteWindowsFolder(dir);
        }

        Directory.Delete(targetDir, false);
    }

    private void SetupInstallationPath(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        if (_environmentService.GetCurrentEnvironment() == CurrentEnvironment.WindowsStandalone)
        {
            return;
        }

        try
        {
            UnixFileInfo fileInfo = new(path);
            fileInfo.FileAccessPermissions |= FileAccessPermissions.UserReadWriteExecute;
        }
        catch (Exception e)
        {
            Log.Error("Error: {Error}", $"There was an issue setting up permissions for the installation directory: {e.Message}");
        }
    }

    private void EnsureExecutableFlagOnUnixSystems(string executablePath)
    {
        if (_environmentService.GetCurrentEnvironment() != CurrentEnvironment.WindowsStandalone)
        {
            UnixFileInfo fileInfo = new(executablePath);
            fileInfo.FileAccessPermissions |= FileAccessPermissions.UserReadWriteExecute;
        }
    }
    #endregion
}