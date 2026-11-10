using System.Buffers;
using System.Text.Json;

namespace CodexUsageWidgetProvider;

internal static class TokenActivityReader
{
    private static ReadOnlySpan<byte> UsageMarker => "\"token_usage_record\""u8;

    public sealed record ActivityResult(List<(long ts, long tokens)> Buckets, long MaxTokens, long TotalTokens, long WeekTokens);

    public static ActivityResult ReadActivity(string sessionDir, DateTimeOffset now)
    {
        long end = now.ToUnixTimeSeconds();
        long dayStart = end - 24 * 3600;
        long weekStart = end - 7 * 86400;
        long weekTokens = 0;
        var hours = new long[24];
        var minModified = now.UtcDateTime.AddDays(-9);
        try
        {
            foreach (var file in new DirectoryInfo(sessionDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
            {
                var modified = file.LastWriteTimeUtc;
                if (modified < minModified) continue;
                bool includeDaily = modified >= now.UtcDateTime.AddHours(-36);
                ReadRecords(file.FullName, (timestamp, tokens) =>
                {
                    if (!DateTimeOffset.TryParse(timestamp, out var time)) return;
                    long seconds = time.ToUnixTimeSeconds();
                    if (seconds < weekStart || seconds > end) return;
                    weekTokens += tokens;
                    if (includeDaily && seconds >= dayStart && seconds < end)
                        hours[(int)((seconds - dayStart) / 3600)] += tokens;
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Local logs may move or disappear while Codex is running.
        }

        var buckets = new List<(long ts, long tokens)>(24);
        for (int i = 0; i < hours.Length; i++)
            buckets.Add((dayStart + i * 3600, hours[i]));
        return new ActivityResult(buckets, hours.Max(), hours.Sum(), weekTokens);
    }

    public static long ReadThreadTokens(string file)
    {
        long total = 0;
        ReadRecords(file, (_, tokens) => total += tokens);
        return total;
    }

    private static void ReadRecords(string file, Action<string?, long> consume)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 0, Options = FileOptions.SequentialScan,
            });
            long endOfFile = stream.Length;
            long lineStart = 0;
            bool isCandidate = false;
            var marker = UsageMarker;
            int overlap = marker.Length - 1;
            Span<byte> boundary = stackalloc byte[overlap * 2];
            int tailLength = 0;
            for (long offset = 0; offset < endOfFile;)
            {
                int requested = (int)Math.Min(buffer.Length, endOfFile - offset);
                int count = stream.ReadAtLeast(buffer.AsSpan(0, requested), requested, false);
                if (count == 0) break;
                int start = 0;
                while (start < count)
                {
                    int newline = buffer.AsSpan(start, count - start).IndexOf((byte)'\n');
                    int length = newline < 0 ? count - start : newline;
                    var part = buffer.AsSpan(start, length);
                    if (!isCandidate)
                    {
                        isCandidate = part.IndexOf(marker) >= 0;
                        if (!isCandidate && tailLength > 0)
                        {
                            int headLength = Math.Min(overlap, part.Length);
                            part[..headLength].CopyTo(boundary[tailLength..]);
                            isCandidate = boundary[..(tailLength + headLength)].IndexOf(marker) >= 0;
                        }
                    }

                    long lineEnd = offset + start + length;
                    if (newline >= 0 || lineEnd == endOfFile || count < requested)
                    {
                        if (isCandidate)
                        {
                            if (lineStart >= offset)
                                ParseRecord(buffer.AsMemory((int)(lineStart - offset), (int)(lineEnd - lineStart)), consume);
                            else
                                ReadSplitRecord(stream, lineStart, lineEnd, offset + count, consume);
                        }
                        lineStart = lineEnd + 1;
                        tailLength = 0;
                        isCandidate = false;
                    }
                    else
                    {
                        // Only the last segment of a full block can continue in the next block.
                        tailLength = Math.Min(overlap, length);
                        part[^tailLength..].CopyTo(boundary);
                    }
                    start += length + 1;
                }
                offset += count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missing or inaccessible log must not block the remaining files.
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void ReadSplitRecord(FileStream stream, long start, long end, long nextBlock, Action<string?, long> consume)
    {
        // Only matching records that cross a block boundary need a contiguous copy.
        if (end - start > Array.MaxLength) return;
        var record = GC.AllocateUninitializedArray<byte>((int)(end - start));
        stream.Position = start;
        stream.ReadExactly(record);
        stream.Position = nextBlock;
        ParseRecord(record, consume);
    }

    private static void ParseRecord(ReadOnlyMemory<byte> record, Action<string?, long> consume)
    {
        if (record.Span.StartsWith("\uFEFF"u8)) record = record[3..];
        try
        {
            using var doc = JsonDocument.Parse(record);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object ||
                !usage.TryGetProperty("total_tokens", out var total) || total.ValueKind != JsonValueKind.Number ||
                !total.TryGetInt64(out var tokens)) return;
            string? timestamp = root.TryGetProperty("timestamp", out var time) && time.ValueKind == JsonValueKind.String
                ? time.GetString() : null;
            consume(timestamp, tokens);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Ignore partially written or malformed records.
        }
    }
}
