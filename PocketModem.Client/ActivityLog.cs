using System.Collections.Concurrent;
using System.Text;

namespace PocketModem.Client;

/// <summary>
/// A rolling record of what the app did, kept on disk.
///
/// Until now every diagnostic line went to Console.WriteLine, which the GUI has
/// no console for - so the reasons a connection failed were written to nowhere.
/// CrashLog covers the process dying; this covers everything before that, which
/// is where the answers usually are. Debugging a failed join meant reproducing
/// it under a CLI, which changes the conditions being investigated.
///
/// Bounded by time rather than size: an hour is long enough to cover the
/// session someone is asking about and short enough that the file stays
/// readable. Trimming happens on write, so a long-running session does not
/// accumulate a day of noise.
///
/// Writes are asynchronous and failures are swallowed. Logging must never be
/// the reason a tunnel drops, and a locked or full disk is not worth
/// propagating into the network path.
/// </summary>
public static class ActivityLog
{
    /// <summary>How much history to keep. Older lines are dropped on trim.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>
    /// Trim no more than once a minute.
    ///
    /// Rewriting the file on every line would turn a busy second into hundreds
    /// of full-file rewrites, which is exactly the kind of overhead that ends
    /// up in a packet path.
    /// </summary>
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(1);

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 4096);
    private static readonly object FileLock = new();

    private static Thread? _writer;
    private static DateTime _lastTrim = DateTime.UtcNow;
    private static bool _started;

    public static string Location => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PocketModem", "activity.log");

    /// <summary>
    /// Begin recording. Safe to call more than once; only the first starts a
    /// writer.
    /// </summary>
    public static void Start()
    {
        lock (FileLock)
        {
            if (_started) return;
            _started = true;
        }

        // A background thread rather than writing inline: a disk stall would
        // otherwise pause whichever thread happened to be logging, and some of
        // those are moving packets.
        _writer = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "activity-log",
        };
        _writer.Start();

        Write("--- session started ---");
    }

    /// <summary>
    /// Record a line. Never throws, and never blocks on a full queue.
    /// </summary>
    public static void Write(string message)
    {
        if (!_started) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";

        // TryAdd, not Add: if the queue is full the app is producing lines
        // faster than the disk takes them, and dropping a line beats blocking
        // the caller.
        Queue.TryAdd(line);
    }

    /// <summary>Record a line and echo it, for paths that also print.</summary>
    public static void WriteAndPrint(string message)
    {
        Console.WriteLine(message);
        Write(message);
    }

    /// <summary>The recent history, newest last. Empty if there is none.</summary>
    public static string Read()
    {
        try
        {
            lock (FileLock)
            {
                return File.Exists(Location) ? File.ReadAllText(Location) : "";
            }
        }
        catch
        {
            return "";
        }
    }

    private static void WriterLoop()
    {
        var batch = new List<string>(64);

        foreach (var first in Queue.GetConsumingEnumerable())
        {
            batch.Clear();
            batch.Add(first);

            // Drain whatever else is waiting, so a burst costs one write
            // rather than one per line.
            while (batch.Count < 64 && Queue.TryTake(out var more)) batch.Add(more);

            try
            {
                lock (FileLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Location)!);
                    File.AppendAllLines(Location, batch);

                    if (DateTime.UtcNow - _lastTrim > TrimInterval)
                    {
                        _lastTrim = DateTime.UtcNow;
                        TrimLocked();
                    }
                }
            }
            catch
            {
                // A log that cannot be written must not take the tunnel with
                // it. The lines are lost; the connection is not.
            }
        }
    }

    /// <summary>Drop lines older than the window. Caller holds FileLock.</summary>
    private static void TrimLocked()
    {
        try
        {
            if (!File.Exists(Location)) return;

            var cutoff = DateTime.Now - Window;
            var kept = new List<string>();
            bool keepingRest = false;

            foreach (var line in File.ReadLines(Location))
            {
                // Once a line is inside the window every later line is too, so
                // parsing can stop - the file is chronological.
                if (!keepingRest)
                {
                    if (line.Length >= 23 &&
                        DateTime.TryParse(line[..23], out var when) &&
                        when < cutoff)
                    {
                        continue;
                    }
                    keepingRest = true;
                }

                kept.Add(line);
            }

            var tmp = Location + ".tmp";
            File.WriteAllLines(tmp, kept, Encoding.UTF8);
            File.Move(tmp, Location, overwrite: true);
        }
        catch
        {
            // Leaving an oversized log is better than losing it.
        }
    }
}
