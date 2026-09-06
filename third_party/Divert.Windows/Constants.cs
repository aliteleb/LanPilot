namespace Divert.Windows;

internal static class Constants
{
    public const short WINDIVERT_PRIORITY_HIGHEST = 3000;
    public const short WINDIVERT_PRIORITY_LOWEST = -WINDIVERT_PRIORITY_HIGHEST;
    public const int WINDIVERT_PARAM_QUEUE_LENGTH_DEFAULT = 4096;
    public const int WINDIVERT_PARAM_QUEUE_LENGTH_MIN = 32;
    public const int WINDIVERT_PARAM_QUEUE_LENGTH_MAX = 16384;
    public const int WINDIVERT_PARAM_QUEUE_TIME_DEFAULT = 2000;
    public const int WINDIVERT_PARAM_QUEUE_TIME_MIN = 100;
    public const int WINDIVERT_PARAM_QUEUE_TIME_MAX = 16000;
    public const int WINDIVERT_PARAM_QUEUE_SIZE_DEFAULT = 4 * 1024 * 1024;
    public const int WINDIVERT_PARAM_QUEUE_SIZE_MIN = 64 * 1024;
    public const int WINDIVERT_PARAM_QUEUE_SIZE_MAX = 32 * 1024 * 1024;
    public const int WINDIVERT_BATCH_MAX = byte.MaxValue;
}
