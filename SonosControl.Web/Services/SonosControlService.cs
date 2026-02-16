using System;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SonosControl.DAL.Interfaces;
using SonosControl.DAL.Models;

namespace SonosControl.Web.Services
{
    public class SonosControlService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly TimeProvider _timeProvider;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly ILogger<SonosControlService> _logger;

        public SonosControlService(IServiceScopeFactory scopeFactory, ILogger<SonosControlService> logger, TimeProvider? timeProvider = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _timeProvider = timeProvider ?? TimeProvider.System;
            _delay = delay ?? TaskDelay;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Create a new scope for each execution cycle
                using (var scope = _scopeFactory.CreateScope())
                {
                    var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                    var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();

                    // Continuously evaluate settings until start time is reached
                    var (settings, schedule, startTime) = await WaitUntilStartTime(uow, stoppingToken);

                    if (settings == null || settings.Speakers == null || !settings.Speakers.Any())
                    {
                        _logger.LogDebug("No speakers configured. Waiting...");
                        await _delay(TimeSpan.FromSeconds(30), stoppingToken);
                        continue;
                    }

                    var stopTimeOnly = schedule?.StopTime ?? settings.StopTime;
                    var speakers = settings.Speakers.ToList();

                    var startTimeOnly = TimeOnly.FromDateTime(startTime.LocalDateTime);
                    TimeSpan duration = stopTimeOnly - startTimeOnly;
                    if (duration < TimeSpan.Zero)
                    {
                        duration = duration.Add(TimeSpan.FromDays(1));
                    }
                    var stopDateTime = startTime.Add(duration);

                    try
                    {
                        await StartSpeaker(uow, speakers, settings, schedule, stoppingToken);
                        await notificationService.SendNotificationAsync($"Automation started playback on {speakers.Count} speakers.");
                    }
                    catch (Exception ex)
                    {
                        await notificationService.SendNotificationAsync($"Automation failed to start playback: {ex.Message}");
                    }

                    await StopSpeaker(uow, speakers, stopDateTime, schedule, stoppingToken);
                    await notificationService.SendNotificationAsync($"Automation stopped playback.");
                }
            }
        }

        private async Task StartSpeaker(IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, SonosSettings settings, DaySchedule? schedule, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetLocalNow();
            DayOfWeek today = now.DayOfWeek;

            if (schedule != null && ShouldSkipPlayback(schedule))
            {
                if (schedule is HolidaySchedule holiday)
                    _logger.LogInformation("Holiday override for {Date} skips playback", holiday.Date);
                return;
            }

            if (schedule == null && (settings == null || !settings.ActiveDays.Contains(today)))
            {
                _logger.LogDebug("Today ({Day}) is not an active day", today);
                return;
            }

            string masterIp = speakers.First().IpAddress;
            bool isSynced = schedule?.IsSyncedPlayback ?? true;

            var targetSpeakers = new List<string>();

            // Ungroup all speakers first to ensure a clean slate
            await Task.WhenAll(speakers.Select(async speaker =>
            {
                await uow.SonosConnectorRepo.UngroupSpeaker(speaker.IpAddress, cancellationToken);
                // Set volume for each speaker
                int volume = speaker.StartupVolume ?? settings.Volume;
                await uow.SonosConnectorRepo.SetSpeakerVolume(speaker.IpAddress, volume, cancellationToken);
            }));

            if (isSynced)
            {
                // In synced mode, we group everyone to the master, and only command the master
                targetSpeakers.Add(masterIp);

                var slaveIps = speakers.Skip(1).Select(s => s.IpAddress);
                if (slaveIps.Any())
                {
                    bool groupSuccess = await uow.SonosConnectorRepo.CreateGroup(masterIp, slaveIps, cancellationToken);
                    if (!groupSuccess)
                    {
                        _logger.LogWarning("Grouping failed or partial. Falling back to individual playback for all speakers");
                        foreach (var slave in slaveIps)
                        {
                            if (!targetSpeakers.Contains(slave))
                            {
                                targetSpeakers.Add(slave);
                            }
                        }
                    }
                }
            }
            else
            {
                // In independent mode, we target all speakers individually
                targetSpeakers.AddRange(speakers.Select(s => s.IpAddress));
            }

            var playAction = GetPlayAction(uow, schedule, settings);
            await Task.WhenAll(targetSpeakers.Select(ip => playAction(ip)));
            _logger.LogInformation("Started playback for {Count} speaker(s)", targetSpeakers.Count);
        }

        private static Func<string, Task> GetPlayAction(IUnitOfWork uow, DaySchedule? schedule, SonosSettings settings)
        {
            if (schedule != null)
            {
                if (schedule.PlayRandomSpotify)
                {
                    var url = GetRandomSpotifyUrl(settings);
                    return url != null ? (ip => uow.SonosConnectorRepo.PlaySpotifyTrackAsync(ip, url)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
                }
                if (schedule.PlayRandomYouTubeMusic)
                {
                    var url = GetRandomYouTubeMusicUrl(settings);
                    return url != null ? (ip => uow.SonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
                }
                if (schedule.PlayRandomStation)
                {
                    var url = GetRandomStationUrl(settings);
                    return url != null ? (ip => uow.SonosConnectorRepo.SetTuneInStationAsync(ip, url)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
                }
                if (!string.IsNullOrEmpty(schedule.SpotifyUrl))
                    return (ip => uow.SonosConnectorRepo.PlaySpotifyTrackAsync(ip, schedule.SpotifyUrl!));
                if (!string.IsNullOrEmpty(schedule.YouTubeMusicUrl))
                    return (ip => uow.SonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, schedule.YouTubeMusicUrl!, settings.AutoPlayStationUrl));
                if (!string.IsNullOrEmpty(schedule.StationUrl))
                    return (ip => uow.SonosConnectorRepo.SetTuneInStationAsync(ip, schedule.StationUrl));
                return ip => uow.SonosConnectorRepo.StartPlaying(ip);
            }

            if (settings.AutoPlayRandomSpotify)
            {
                var url = GetRandomSpotifyUrl(settings);
                return url != null ? (ip => uow.SonosConnectorRepo.PlaySpotifyTrackAsync(ip, url)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
            }
            if (settings.AutoPlayRandomYouTubeMusic)
            {
                var url = GetRandomYouTubeMusicUrl(settings);
                return url != null ? (ip => uow.SonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, url, settings.AutoPlayStationUrl)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
            }
            if (settings.AutoPlayRandomStation)
            {
                var url = GetRandomStationUrl(settings);
                return url != null ? (ip => uow.SonosConnectorRepo.SetTuneInStationAsync(ip, url)) : (ip => uow.SonosConnectorRepo.StartPlaying(ip));
            }
            if (!string.IsNullOrEmpty(settings.AutoPlaySpotifyUrl))
                return (ip => uow.SonosConnectorRepo.PlaySpotifyTrackAsync(ip, settings.AutoPlaySpotifyUrl!));
            if (!string.IsNullOrEmpty(settings.AutoPlayYouTubeMusicUrl))
                return (ip => uow.SonosConnectorRepo.PlayYouTubeMusicTrackAsync(ip, settings.AutoPlayYouTubeMusicUrl!, settings.AutoPlayStationUrl));
            if (!string.IsNullOrEmpty(settings.AutoPlayStationUrl))
                return (ip => uow.SonosConnectorRepo.SetTuneInStationAsync(ip, settings.AutoPlayStationUrl!));
            return ip => uow.SonosConnectorRepo.StartPlaying(ip);
        }


        private static string? GetRandomStationUrl(SonosSettings settings)
        {
            if (settings.Stations == null || settings.Stations.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.Stations.Count);
            return settings.Stations[index].Url;
        }

        private static string? GetRandomSpotifyUrl(SonosSettings settings)
        {
            if (settings.SpotifyTracks == null || settings.SpotifyTracks.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.SpotifyTracks.Count);
            return settings.SpotifyTracks[index].Url;
        }

        private static string? GetRandomYouTubeMusicUrl(SonosSettings settings)
        {
            if (settings.YouTubeMusicCollections == null || settings.YouTubeMusicCollections.Count == 0)
                return null;

            var index = Random.Shared.Next(settings.YouTubeMusicCollections.Count);
            return settings.YouTubeMusicCollections[index].Url;
        }

        private async Task<(SonosSettings settings, DaySchedule? schedule, DateTimeOffset startTime)> WaitUntilStartTime(IUnitOfWork uow, CancellationToken token)
        {
            TimeOnly? previousStart = null;
            DayOfWeek? previousDay = null;
            DateTimeOffset? previousTarget = null;

            while (!token.IsCancellationRequested)
            {
                var settings = await uow.SettingsRepo.GetSettings();
                if (settings is null)
                {
                    await _delay(TimeSpan.FromSeconds(1), token);
                    continue;
                }
                var now = _timeProvider.GetLocalNow();

                var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);
                var todayDate = DateOnly.FromDateTime(now.LocalDateTime);
                var todaySchedule = GetScheduleForDate(settings, todayDate);
                var todayStart = todaySchedule?.StartTime ?? settings.StartTime;

                if (todaySchedule != null && ShouldSkipPlayback(todaySchedule))
                {
                    todaySchedule = null;
                }

                if (todaySchedule != null
                    && previousTarget is DateTimeOffset scheduledTarget
                    && scheduledTarget.Date == now.Date
                    && now >= scheduledTarget)
                {
                    var todayStartTime = new DateTimeOffset(todayDate.ToDateTime(todayStart), now.Offset);
                    return (settings, todaySchedule, todayStartTime);
                }

                var (target, schedule, start, startDay) = DetermineNextStart(settings, now);

                if (target <= now)
                    return (settings, schedule, target);

                var remaining = target - now;

                if (previousStart != start || previousDay != startDay || previousTarget != target)
                {
                    string delayInMs;
                    if (remaining.Days > 0)
                    {
                        delayInMs = string.Format("{0}d:{1:D2}h:{2:D2}m:{3:D2}s:{4:D3}ms",
                            remaining.Days,
                            remaining.Hours,
                            remaining.Minutes,
                            remaining.Seconds,
                            remaining.Milliseconds);
                    }
                    else
                    {
                        delayInMs = string.Format("{0:D2}h:{1:D2}m:{2:D2}s:{3:D3}ms",
                            remaining.Hours,
                            remaining.Minutes,
                            remaining.Seconds,
                            remaining.Milliseconds);
                    }

                    _logger.LogDebug("Starting in {Delay}", delayInMs);
                    previousStart = start;
                    previousDay = startDay;
                    previousTarget = target;
                }

                // Fix: Avoid rounding issues that cause delays larger than remaining time for small durations
                // Poll settings at most once per minute to pick up schedule changes
                var maxDelay = TimeSpan.FromMinutes(1);
                var delay = remaining > maxDelay ? maxDelay : remaining;

                // Ensure we don't pass a zero or negative delay if something drifted slightly,
                // but ManualTimeProvider handles zero correctly.
                // However, we want to respect the cancellation token and yield.
                if (delay <= TimeSpan.Zero)
                    delay = TimeSpan.FromTicks(1);

                await _delay(delay, token);
            }

            token.ThrowIfCancellationRequested();
            return default;
        }

        private static DaySchedule? GetScheduleForDate(SonosSettings settings, DateOnly date)
        {
            var day = date.DayOfWeek;

            // Master switch: if day is not active, no playback (even if holiday).
            // Default to true (Active) if ActiveDays is null for backward compatibility.
            if (settings.ActiveDays != null && !settings.ActiveDays.Contains(day))
                return null;

            if (settings.HolidaySchedules != null)
            {
                var holiday = settings.HolidaySchedules.FirstOrDefault(h => h.Date == date);
                if (holiday != null)
                    return holiday;
            }

            if (settings.DailySchedules != null && settings.DailySchedules.TryGetValue(day, out var schedule))
                return schedule;

            return null;
        }

        private (DateTimeOffset target, DaySchedule? schedule, TimeOnly start, DayOfWeek day) DetermineNextStart(SonosSettings settings, DateTimeOffset now)
        {
            var todayDate = DateOnly.FromDateTime(now.LocalDateTime);
            var currentTime = TimeOnly.FromDateTime(now.LocalDateTime);

            for (int offset = 0; offset <= 14; offset++)
            {
                var candidateDate = todayDate.AddDays(offset);
                var schedule = GetScheduleForDate(settings, candidateDate);

                if (schedule != null && ShouldSkipPlayback(schedule))
                    continue;

                // If no schedule found (and not holiday override), check if day is active.
                // If not active, skip it entirely instead of falling back to default settings.
                // Also respect backward compatibility (null ActiveDays = all active).
                if (schedule == null && settings.ActiveDays != null && !settings.ActiveDays.Contains(candidateDate.DayOfWeek))
                    continue;

                var candidateStart = schedule?.StartTime ?? settings.StartTime;
                var candidateDateTime = new DateTimeOffset(candidateDate.ToDateTime(candidateStart), now.Offset);

                if (offset == 0 && candidateStart < currentTime)
                    continue;

                return (candidateDateTime, schedule, candidateStart, candidateDate.DayOfWeek);
            }

            var fallbackDate = todayDate.AddDays(1);
            var fallbackStart = settings.StartTime;
            var fallbackDateTime = new DateTimeOffset(fallbackDate.ToDateTime(fallbackStart), now.Offset);
            return (fallbackDateTime, null, fallbackStart, fallbackDate.DayOfWeek);
        }

        private static Task TaskDelay(TimeSpan delay, CancellationToken token)
        {
            if (delay <= TimeSpan.Zero)
                return Task.CompletedTask;

            return Task.Delay(delay, token);
        }

        private static bool HasPlaybackTarget(DaySchedule schedule)
        {
            return schedule.PlayRandomStation
                   || schedule.PlayRandomSpotify
                   || schedule.PlayRandomYouTubeMusic
                   || !string.IsNullOrWhiteSpace(schedule.StationUrl)
                   || !string.IsNullOrWhiteSpace(schedule.SpotifyUrl)
                   || !string.IsNullOrWhiteSpace(schedule.YouTubeMusicUrl);
        }

        private static bool ShouldSkipPlayback(DaySchedule schedule)
        {
            if (schedule is HolidaySchedule holiday)
            {
                if (holiday.SkipPlayback)
                    return true;

                return !HasPlaybackTarget(holiday);
            }

            return false;
        }

        private async Task StopSpeaker(IUnitOfWork uow, IEnumerable<SonosSpeaker> speakers, DateTimeOffset stopDateTime, DaySchedule? schedule, CancellationToken cancellationToken)
        {
            var now = _timeProvider.GetLocalNow();
            var timeDifference = stopDateTime - now;
            bool isSynced = schedule?.IsSyncedPlayback ?? true;

            var targetSpeakers = speakers.Select(s => s.IpAddress).ToList();

            if (stopDateTime <= now)
            {
                await Task.WhenAll(targetSpeakers.Select(ip => uow.SonosConnectorRepo.StopPlaying(ip)));
                _logger.LogInformation("Stopped playback");
            }
            else
            {
                var delaySpan = timeDifference;
                if (delaySpan < TimeSpan.Zero) delaySpan = TimeSpan.Zero;
                _logger.LogDebug("Pausing in {Delay}", delaySpan);
                await _delay(delaySpan, cancellationToken);

                await Task.WhenAll(targetSpeakers.Select(ip => uow.SonosConnectorRepo.StopPlaying(ip)));
                _logger.LogInformation("Stopped playback");
            }

            if (isSynced)
            {
                await Task.WhenAll(speakers.Select(speaker => uow.SonosConnectorRepo.UngroupSpeaker(speaker.IpAddress, cancellationToken)));
            }
        }
    }
}
