using Shouldly;
using SqlDataPack.Internal;
using SqlDataPack.Models;
using Xunit;

namespace SqlDataPack.Tests;

/// <summary>
/// Covers the effective batch size calculation: the large-table cap and the bytes-per-row cap that keep a wide or
/// large table from pulling an unbounded batch into memory. Thresholds come from the <see cref="BatchPlanner"/>
/// constants rather than literals, so this file pins the branching while <c>PublicApiContractTests</c> pins the values
/// those constants are allowed to have.
/// </summary>
public sealed class BatchPlannerTests {
    private const long SixtyFourKiB = 64L * 1024;

    [Fact]
    public void GetEffectiveBatchSize_SmallTable_KeepsConfiguredBatchSize() {
        // 100 rows / 100 KB is under every threshold, so nothing should touch the configured size.
        var options = new ExportOptions { BatchSize = 1_000 };

        var batchSize = BatchPlanner.GetEffectiveBatchSize(options, estimatedRows: 100, estimatedBytes: 100_000);

        batchSize.ShouldBe(1_000);
    }

    [Fact]
    public void GetEffectiveBatchSize_LargeTableByBytes_UsesLargeTableBatchSize() {
        var options = new ExportOptions { BatchSize = 1_000 };

        // Bytes exactly at the threshold, rows an order of magnitude under the row threshold, so only the byte arm
        // of the OR can explain the result.
        var batchSize = BatchPlanner.GetEffectiveBatchSize(
            options,
            estimatedRows: BatchPlanner.DefaultLargeTableRowThreshold / 10,
            estimatedBytes: BatchPlanner.DefaultLargeTableThresholdBytes);

        batchSize.ShouldBe(BatchPlanner.DefaultLargeTableBatchSize);
    }

    [Fact]
    public void GetEffectiveBatchSize_LargeTableByRows_UsesLargeTableBatchSizeWhenBytesAreUnknown() {
        // ImportOptions overload: the second call site into the shared implementation.
        var options = new ImportOptions { BatchSize = 1_000 };

        var batchSize = BatchPlanner.GetEffectiveBatchSize(
            options,
            estimatedRows: BatchPlanner.DefaultLargeTableRowThreshold,
            estimatedBytes: 0);

        batchSize.ShouldBe(BatchPlanner.DefaultLargeTableBatchSize);
    }

    [Fact]
    public void GetEffectiveBatchSize_WideRows_ReducesByMaxBatchBytes() {
        var options = new ExportOptions {
            BatchSize = BatchPlanner.LatestBatchSize,
            MaxBatchBytes = BatchPlanner.LatestMaxBatchBytes,
            // Park the large-table arms out of reach so only the bytes-per-row cap can move the result.
            LargeTableThresholdBytes = long.MaxValue,
            LargeTableRowThreshold = long.MaxValue
        };

        // 1,000 rows averaging 64 KiB each: 8 MiB / 64 KiB = 128 rows per batch.
        var batchSize = BatchPlanner.GetEffectiveBatchSize(options, estimatedRows: 1_000, estimatedBytes: 1_000 * SixtyFourKiB);

        batchSize.ShouldBe(128);
    }

    [Fact]
    public void GetEffectiveBatchSize_FloorsAtOne() {
        var options = new ExportOptions {
            BatchSize = BatchPlanner.LatestBatchSize,
            MaxBatchBytes = BatchPlanner.DefaultMaxBatchBytes,
            LargeTableThresholdBytes = long.MaxValue,
            LargeTableRowThreshold = long.MaxValue
        };

        // Average row is twice MaxBatchBytes, so the integer division underflows to 0 without the floor, and a
        // 0 batch size divides by zero downstream.
        var batchSize = BatchPlanner.GetEffectiveBatchSize(
            options,
            estimatedRows: 4,
            estimatedBytes: 4 * (BatchPlanner.DefaultMaxBatchBytes * 2));

        batchSize.ShouldBe(1);
    }

    [Theory]
    [InlineData(50, 1_000L, 1_000L)]
    [InlineData(50, 1_000_000L, 0L)]
    [InlineData(50, 10L, 10L)]
    [InlineData(BatchPlanner.LatestBatchSize, 200_000L, BatchPlanner.DefaultLargeTableThresholdBytes * 2)]
    [InlineData(BatchPlanner.LatestBatchSize, 1L, 1L)]
    [InlineData(1, 5_000_000L, 5_000_000_000L)]
    public void GetEffectiveBatchSize_NeverIncreasesConfiguredBatchSize(int configuredBatchSize, long estimatedRows, long estimatedBytes) {
        var options = new ExportOptions { BatchSize = configuredBatchSize };

        var batchSize = BatchPlanner.GetEffectiveBatchSize(options, estimatedRows, estimatedBytes);

        // Min, not Max: LargeTableBatchSize is a cap, never a promotion for a caller who asked for smaller batches.
        batchSize.ShouldBeLessThanOrEqualTo(configuredBatchSize);
        batchSize.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void GetEffectiveBatchSize_DisabledAdaptiveBatching_KeepsConfiguredBatchSize() {
        var options = new ExportOptions {
            BatchSize = BatchPlanner.LatestBatchSize,
            AdaptiveBatchingEnabled = false,
            // Every remaining branch would fire on these inputs, so anything but the configured size means the
            // opt-out did not short-circuit first.
            LargeTableThresholdBytes = 1,
            LargeTableRowThreshold = 1,
            LargeTableBatchSize = 1,
            MaxBatchBytes = 1
        };

        var batchSize = BatchPlanner.GetEffectiveBatchSize(options, estimatedRows: 1_000_000, estimatedBytes: 1_000_000_000);

        batchSize.ShouldBe(BatchPlanner.LatestBatchSize);
    }
}
