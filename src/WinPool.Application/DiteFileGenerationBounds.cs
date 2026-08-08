namespace WinPool.Application;

public static class DiteFileGenerationBounds
{
    public const int ManifestFileCount = 1;
    private const long ManifestBaseMaximumBytes = 4096;
    private const long ManifestMaximumBytesPerEntry = 32;

    public static long CalculateMaximumBytes(int totalMiB, int targetCount) =>
        checked(
            totalMiB * 1024L * 1024L
            + targetCount
            + ManifestBaseMaximumBytes
            + targetCount * ManifestMaximumBytesPerEntry);
}
