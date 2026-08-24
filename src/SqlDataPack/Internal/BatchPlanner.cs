using SqlDataPack.Models;

namespace SqlDataPack.Internal;

internal static class BatchPlanner {
    public const long DefaultLargeTableThresholdBytes = 50L * 1024 * 1024;
    public const long DefaultLargeTableRowThreshold = 100_000;
    public const int DefaultLargeTableBatchSize = 250;
    public const long DefaultMaxBatchBytes = 4L * 1024 * 1024;

    // Throughput-leaning tuning used by the evolving `.Latest` option presets. These are
    // deliberately separate from the Default* constants so the stable defaults never move
    // when this tuning is revised; see ExportOptions.Latest / ImportOptions.Latest.
    public const int LatestBatchSize = 5_000;
    public const long LatestMaxBatchBytes = 8L * 1024 * 1024;

    public static void Validate(ExportOptions options) {
        ValidateTimeout(options.CommandTimeout, nameof(ExportOptions.CommandTimeout));
        Validate(options.BatchSize, options.AdaptiveBatchingEnabled, options.LargeTableThresholdBytes, options.LargeTableRowThreshold, options.LargeTableBatchSize, options.MaxBatchBytes);
    }

    public static void Validate(ImportOptions options) {
        ValidateTimeout(options.ValidationCommandTimeout, nameof(ImportOptions.ValidationCommandTimeout));
        ValidateTimeout(options.BulkCopyTimeout, nameof(ImportOptions.BulkCopyTimeout));
        Validate(options.BatchSize, options.AdaptiveBatchingEnabled, options.LargeTableThresholdBytes, options.LargeTableRowThreshold, options.LargeTableBatchSize, options.MaxBatchBytes);
    }

    public static int GetEffectiveBatchSize(ExportOptions options, long estimatedRows, long estimatedBytes) {
        return GetEffectiveBatchSize(options.BatchSize, options.AdaptiveBatchingEnabled, options.LargeTableThresholdBytes, options.LargeTableRowThreshold, options.LargeTableBatchSize, options.MaxBatchBytes, estimatedRows, estimatedBytes);
    }

    public static int GetEffectiveBatchSize(ImportOptions options, long estimatedRows, long estimatedBytes) {
        return GetEffectiveBatchSize(options.BatchSize, options.AdaptiveBatchingEnabled, options.LargeTableThresholdBytes, options.LargeTableRowThreshold, options.LargeTableBatchSize, options.MaxBatchBytes, estimatedRows, estimatedBytes);
    }

    private static void Validate(int batchSize, bool adaptiveBatchingEnabled, long largeTableThresholdBytes, long largeTableRowThreshold, int largeTableBatchSize, long maxBatchBytes) {
        if (batchSize <= 0) {
            throw new SqlDataPackException("BatchSize must be greater than zero.");
        }

        if (!adaptiveBatchingEnabled) {
            return;
        }

        if (largeTableThresholdBytes <= 0) {
            throw new SqlDataPackException("LargeTableThresholdBytes must be greater than zero.");
        }

        if (largeTableRowThreshold <= 0) {
            throw new SqlDataPackException("LargeTableRowThreshold must be greater than zero.");
        }

        if (largeTableBatchSize <= 0) {
            throw new SqlDataPackException("LargeTableBatchSize must be greater than zero.");
        }

        if (maxBatchBytes <= 0) {
            throw new SqlDataPackException("MaxBatchBytes must be greater than zero.");
        }
    }

    private static void ValidateTimeout(int? timeout, string name) {
        if (timeout <= 0) {
            throw new SqlDataPackException($"{name} must be greater than zero when set.");
        }
    }

    private static int GetEffectiveBatchSize(int batchSize, bool adaptiveBatchingEnabled, long largeTableThresholdBytes, long largeTableRowThreshold, int largeTableBatchSize, long maxBatchBytes, long estimatedRows, long estimatedBytes) {
        if (!adaptiveBatchingEnabled) {
            return batchSize;
        }

        var effective = batchSize;
        if (estimatedBytes >= largeTableThresholdBytes || estimatedRows >= largeTableRowThreshold) {
            effective = Math.Min(effective, largeTableBatchSize);
        }

        if (estimatedBytes > 0 && estimatedRows > 0) {
            var averageRowBytes = Math.Max(1, DivideRoundingUp(estimatedBytes, estimatedRows));
            var maxRowsByBytes = Math.Max(1, maxBatchBytes / averageRowBytes);
            effective = (int)Math.Min(effective, Math.Min(maxRowsByBytes, int.MaxValue));
        }

        return Math.Max(1, effective);
    }

    private static long DivideRoundingUp(long dividend, long divisor) {
        return 1 + ((dividend - 1) / divisor);
    }
}
