using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PowerMonitor.Core;

public sealed record SampleRow(
    DateTimeOffset Time, double IntervalSec, bool OnAc, bool Charging, int RateMw, int CapacityMwh, int FullMwh,
    int? SystemMw, int PackageMw, int CoresMw, int IGpuMw, int DGpuDState, int? DGpuMw, int? RestMw, int RefreshHz)
{
    public double Percent => FullMwh > 0 ? 100.0 * CapacityMwh / FullMwh : 0;
}

public sealed record DischargeSession(
    DateTimeOffset Start, DateTimeOffset End, double StartPercent, double EndPercent,
    double UsedWh, double ActiveHours, double AverageW, double FullWh)
{
    /// <summary>Runtime of a full charge at this session's average draw.</summary>
    public double ProjectedFullHours => AverageW > 0 ? FullWh / AverageW : 0;
    public bool Ongoing { get; init; }
}

public sealed record AppEnergy(string Name, double CpuWh, double IGpuWh, double DGpuWh, double CpuHours, double GpuHours)
{
    public double TotalWh => CpuWh + IGpuWh + DGpuWh;
}

public sealed record HealthPoint(DateOnly Day, int FullMwh, int DesignMwh, int CycleCount);

/// <summary>SQLite store shared by the collector (writer) and the app (reader), WAL mode.</summary>
public sealed class Store : IDisposable
{
    /// <summary>%LOCALAPPDATA%\PowerMonitor\power.db, or POWERMONITOR_DB when set (testing).</summary>
    public static string DefaultPath => Environment.GetEnvironmentVariable("POWERMONITOR_DB") is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerMonitor", "power.db");

    readonly SqliteConnection _db;

    public Store(string? path = null, bool readOnly = false)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _db.Open();
        // A small page cache: the collector appends and the app reads short ranges.
        Exec("PRAGMA busy_timeout = 5000; PRAGMA cache_size = -512;");
        if (!readOnly) Migrate();
    }

    void Migrate()
    {
        Exec("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS samples (
                t INTEGER PRIMARY KEY,          -- unix ms, UTC
                interval REAL NOT NULL,
                on_ac INTEGER NOT NULL, charging INTEGER NOT NULL,
                rate_mw INTEGER NOT NULL, capacity_mwh INTEGER NOT NULL, full_mwh INTEGER NOT NULL,
                design_mwh INTEGER NOT NULL, cycles INTEGER NOT NULL,
                system_mw INTEGER, pkg_mw INTEGER NOT NULL, cores_mw INTEGER NOT NULL, igpu_mw INTEGER NOT NULL,
                dgpu_dstate INTEGER NOT NULL, dgpu_mw INTEGER, rest_mw INTEGER, refresh_hz INTEGER NOT NULL,
                source INTEGER NOT NULL DEFAULT 0  -- 0 collector, 1 imported battery-watch CSV
            );
            CREATE TABLE IF NOT EXISTS proc_minute (
                minute INTEGER NOT NULL,        -- unix s, multiple of 60
                on_battery INTEGER NOT NULL,
                name TEXT NOT NULL,
                cpu_j REAL NOT NULL, igpu_j REAL NOT NULL, dgpu_j REAL NOT NULL,
                cpu_s REAL NOT NULL, gpu_s REAL NOT NULL,
                PRIMARY KEY (minute, on_battery, name)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public void Write(Sample s)
    {
        using var tx = _db.BeginTransaction();
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR REPLACE INTO samples VALUES ($t, $iv, $ac, $ch, $rate, $cap, $full, $design, $cyc,
                    $sys, $pkg, $cores, $igpu, $ds, $dgpu, $rest, $hz, 0)
                """;
            cmd.Parameters.AddWithValue("$t", s.Time.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$iv", s.IntervalSec);
            cmd.Parameters.AddWithValue("$ac", s.OnAc);
            cmd.Parameters.AddWithValue("$ch", s.Charging);
            cmd.Parameters.AddWithValue("$rate", s.BatteryRateMw);
            cmd.Parameters.AddWithValue("$cap", s.CapacityMwh);
            cmd.Parameters.AddWithValue("$full", s.FullMwh);
            cmd.Parameters.AddWithValue("$design", s.DesignMwh);
            cmd.Parameters.AddWithValue("$cyc", s.CycleCount);
            cmd.Parameters.AddWithValue("$sys", (object?)s.SystemMw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pkg", s.PackageMw);
            cmd.Parameters.AddWithValue("$cores", s.CoresMw);
            cmd.Parameters.AddWithValue("$igpu", s.IGpuMw);
            cmd.Parameters.AddWithValue("$ds", s.DGpuDState);
            cmd.Parameters.AddWithValue("$dgpu", (object?)s.DGpuMw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$rest", (object?)s.RestMw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hz", s.RefreshHz);
            cmd.ExecuteNonQuery();
        }
        using (var cmd = _db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO proc_minute VALUES ($m, $b, $n, $c, $ig, $dg, $cs, $gs)
                ON CONFLICT(minute, on_battery, name) DO UPDATE SET
                    cpu_j = cpu_j + $c, igpu_j = igpu_j + $ig, dgpu_j = dgpu_j + $dg, cpu_s = cpu_s + $cs, gpu_s = gpu_s + $gs
                """;
            var m = cmd.Parameters.Add("$m", SqliteType.Integer);
            var b = cmd.Parameters.Add("$b", SqliteType.Integer);
            var n = cmd.Parameters.Add("$n", SqliteType.Text);
            var c = cmd.Parameters.Add("$c", SqliteType.Real);
            var ig = cmd.Parameters.Add("$ig", SqliteType.Real);
            var dg = cmd.Parameters.Add("$dg", SqliteType.Real);
            var cs = cmd.Parameters.Add("$cs", SqliteType.Real);
            var gs = cmd.Parameters.Add("$gs", SqliteType.Real);
            m.Value = s.Time.ToUnixTimeSeconds() / 60 * 60;
            b.Value = !s.OnAc;
            foreach (var p in s.Processes)
            {
                n.Value = p.Name; c.Value = p.CpuJ; ig.Value = p.IGpuJ; dg.Value = p.DGpuJ; cs.Value = p.CpuSec; gs.Value = p.GpuSec;
                cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
    }

    /// <summary>Drops 10-second samples after a year and per-minute process rows after 120 days.</summary>
    public void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM samples WHERE t < $s; DELETE FROM proc_minute WHERE minute < $p;";
        cmd.Parameters.AddWithValue("$s", now.AddDays(-366).ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$p", now.AddDays(-120).ToUnixTimeSeconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time import of the PowerShell battery watcher's per-minute CSVs, so the
    /// sessions view starts with history. Only rows older than the first own sample.
    /// </summary>
    public int ImportBatteryWatchCsv(string directory)
    {
        if (GetMeta("imported_battery_watch") is not null || !Directory.Exists(directory)) return 0;
        long firstOwn;
        using (var q = _db.CreateCommand())
        {
            q.CommandText = "SELECT COALESCE(MIN(t), 9223372036854775807) FROM samples WHERE source = 0";
            firstOwn = (long)q.ExecuteScalar()!;
        }
        int count = 0;
        using var tx = _db.BeginTransaction();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO samples VALUES ($t, 60, 0, 0, $rate, $cap, $full, 0, 0,
                $sys, 0, 0, 0, $ds, NULL, NULL, $hz, 1)
            """;
        foreach (var file in Directory.GetFiles(directory, "battery-*.csv").Order())
        {
            var lines = File.ReadAllLines(file);
            if (lines.Length < 2) continue;
            var header = Split(lines[0]);
            int I(string col) => Array.IndexOf(header, col);
            int iTime = I("Time"), iBat = I("OnBattery"), iW = I("Watts"), iRem = I("RemainingWh"), iFull = I("FullWh"),
                iDs = I("DGpuState"), iHz = I("RefreshHz");
            foreach (var line in lines.Skip(1))
            {
                var f = Split(line);
                if (f.Length < header.Length || f[iBat] != "True") continue;
                if (!DateTime.TryParse(f[iTime], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)) continue;
                long t = new DateTimeOffset(local).ToUnixTimeMilliseconds();
                if (t >= firstOwn) continue;
                double w = Num(f[iW]);
                if (w <= 0) continue;
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$t", t);
                cmd.Parameters.AddWithValue("$rate", -(int)(w * 1000));
                cmd.Parameters.AddWithValue("$cap", (int)(Num(f[iRem]) * 1000));
                cmd.Parameters.AddWithValue("$full", (int)(Num(f[iFull]) * 1000));
                cmd.Parameters.AddWithValue("$sys", (int)(w * 1000));
                cmd.Parameters.AddWithValue("$ds", (int)Num(f[iDs]));
                cmd.Parameters.AddWithValue("$hz", (int)Num(f[iHz]));
                count += cmd.ExecuteNonQuery();
            }
        }
        tx.Commit();
        SetMeta("imported_battery_watch", count.ToString(CultureInfo.InvariantCulture));
        return count;

        static string[] Split(string line) => line.TrimStart('﻿').Split(',').Select(x => x.Trim('"')).ToArray();
        static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ---- queries for the app ----

    public List<SampleRow> Samples(DateTimeOffset from, DateTimeOffset to)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT t, interval, on_ac, charging, rate_mw, capacity_mwh, full_mwh, system_mw, pkg_mw, cores_mw, igpu_mw,
                   dgpu_dstate, dgpu_mw, rest_mw, refresh_hz
            FROM samples WHERE t >= $a AND t <= $b ORDER BY t
            """;
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var list = new List<SampleRow>();
        int? N(int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
        while (r.Read())
            list.Add(new SampleRow(DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)), r.GetDouble(1),
                r.GetBoolean(2), r.GetBoolean(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), N(7),
                r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), N(12), N(13), r.GetInt32(14)));
        return list;
    }

    public SampleRow? Latest()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT MAX(t) FROM samples";
        if (cmd.ExecuteScalar() is not long t) return null;
        var at = DateTimeOffset.FromUnixTimeMilliseconds(t);
        return Samples(at, at).FirstOrDefault();
    }

    /// <summary>
    /// Discharge sessions: runs of on-battery samples, newest first. A run ends at the
    /// first AC sample (or a gap across which it charged); other gaps (sleep, collector off) stay in the run but do not count as
    /// active time, so the average draw is the draw while the laptop was in use.
    /// </summary>
    public List<DischargeSession> Sessions(DateTimeOffset from)
    {
        var rows = Samples(from, DateTimeOffset.UtcNow);
        var sessions = new List<DischargeSession>();
        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].OnAc) { i++; continue; }
            int j = i;
            double activeSec = 0, energyJ = 0;
            while (j < rows.Count && !rows[j].OnAc)
            {
                var r = rows[j];
                // The imported watcher log has no AC rows: a gap after which the battery
                // holds more charge means it was plugged in, so a new session starts.
                if (j > i && (r.Time - rows[j - 1].Time).TotalMinutes > 10 && r.CapacityMwh > rows[j - 1].CapacityMwh + 500) break;
                if (r.SystemMw is int mw && r.IntervalSec <= 120) { activeSec += r.IntervalSec; energyJ += mw / 1000.0 * r.IntervalSec; }
                j++;
            }
            var first = rows[i];
            var last = rows[j - 1];
            // Some imported rows lack the full-charge capacity; use the run's largest.
            int full = 0;
            for (int k = i; k < j; k++) full = Math.Max(full, rows[k].FullMwh);
            double Pct(SampleRow r) => full > 0 ? 100.0 * r.CapacityMwh / full : 0;
            if (activeSec >= 120)
                sessions.Add(new DischargeSession(first.Time.AddSeconds(-first.IntervalSec), last.Time,
                    Pct(first), Pct(last), Math.Max(0, first.CapacityMwh - last.CapacityMwh) / 1000.0,
                    activeSec / 3600, energyJ / activeSec, full / 1000.0)
                { Ongoing = j == rows.Count && DateTimeOffset.UtcNow - last.Time < TimeSpan.FromMinutes(5) });
            i = j;
        }
        sessions.Reverse();
        return sessions;
    }

    public List<AppEnergy> Apps(DateTimeOffset from, DateTimeOffset to, bool batteryOnly, int limit = 50)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = $"""
            SELECT name, SUM(cpu_j), SUM(igpu_j), SUM(dgpu_j), SUM(cpu_s), SUM(gpu_s) FROM proc_minute
            WHERE minute >= $a AND minute <= $b {(batteryOnly ? "AND on_battery = 1" : "")}
            GROUP BY name ORDER BY SUM(cpu_j) + SUM(igpu_j) + SUM(dgpu_j) DESC LIMIT $n
            """;
        cmd.Parameters.AddWithValue("$a", from.ToUnixTimeSeconds() / 60 * 60);
        cmd.Parameters.AddWithValue("$b", to.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$n", limit);
        using var r = cmd.ExecuteReader();
        var list = new List<AppEnergy>();
        while (r.Read())
            list.Add(new AppEnergy(r.GetString(0), r.GetDouble(1) / 3600, r.GetDouble(2) / 3600, r.GetDouble(3) / 3600,
                r.GetDouble(4) / 3600, r.GetDouble(5) / 3600));
        return list;
    }

    public List<HealthPoint> Health()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            -- The day's last reading (SQLite takes bare columns from the MAX(t) row).
            SELECT date(t / 1000, 'unixepoch', 'localtime') AS d, full_mwh, design_mwh, cycles, MAX(t)
            FROM samples WHERE full_mwh > 0 GROUP BY d ORDER BY d
            """;
        using var r = cmd.ExecuteReader();
        var list = new List<HealthPoint>();
        while (r.Read())
            list.Add(new HealthPoint(DateOnly.Parse(r.GetString(0), CultureInfo.InvariantCulture), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
        return list;
    }

    public void Dispose() => _db.Dispose();
}
