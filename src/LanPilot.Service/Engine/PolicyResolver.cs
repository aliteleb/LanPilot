using LanPilot.Contracts;

namespace LanPilot.Service.Engine;

public sealed class PolicyResolver
{
    public DevicePolicy Resolve(
        DevicePolicy policy,
        GroupPolicy? group,
        IEnumerable<ScheduleRule> schedules,
        DateTimeOffset now)
    {
        long? down = policy.DownloadLimitBitsPerSecond ?? group?.DownloadLimitBitsPerSecond;
        long? up = policy.UploadLimitBitsPerSecond ?? group?.UploadLimitBitsPerSecond;
        bool blocked = policy.BlockInternet || group?.BlockInternet == true;
        DevicePriority priority = policy.Priority != DevicePriority.Normal
            ? policy.Priority
            : group?.Priority ?? DevicePriority.Normal;

        foreach (ScheduleRule schedule in schedules.Where(item =>
                     item.Enabled &&
                     (item.DeviceId == policy.DeviceId || (item.DeviceId is null && item.GroupId == policy.GroupId)) &&
                     IsActive(item, now)))
        {
            blocked |= schedule.BlockInternet;
            down = MinimumLimit(down, schedule.DownloadLimitBitsPerSecond);
            up = MinimumLimit(up, schedule.UploadLimitBitsPerSecond);
        }

        return policy with
        {
            BlockInternet = blocked,
            DownloadLimitBitsPerSecond = down,
            UploadLimitBitsPerSecond = up,
            Priority = priority
        };
    }

    public static bool IsActive(ScheduleRule rule, DateTimeOffset now)
    {
        TimeOnly time = TimeOnly.FromDateTime(now.LocalDateTime);
        bool wraps = rule.End < rule.Start;
        DayOfWeek day = now.LocalDateTime.DayOfWeek;

        if (!wraps)
        {
            return rule.Days.Contains(day) && time >= rule.Start && time < rule.End;
        }

        if (time >= rule.Start)
        {
            return rule.Days.Contains(day);
        }

        DayOfWeek previous = (DayOfWeek)(((int)day + 6) % 7);
        return time < rule.End && rule.Days.Contains(previous);
    }

    private static long? MinimumLimit(long? left, long? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return Math.Min(left.Value, right.Value);
    }
}
