using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace BeamPositionStability
{
    public partial class FrmMain : Form
    {
        private const int MaxPoints = 5000;

        private static readonly DateTime UnixEpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const long MicrosecPerSecond = 1_000_000;
        private const long TicksPerMicrosec = 10; // 1 tick = 100 ns

        private readonly List<PointD> _points = new List<PointD>(MaxPoints);

        private double[] _xpCache = Array.Empty<double>();
        private double[] _ypCache = Array.Empty<double>();

        private void EnsureCacheCapacity(int needed)
        {
            if (_xpCache.Length < needed)
            {
                _xpCache = new double[needed];
                _ypCache = new double[needed];
            }
        }

        public FrmMain()
        {
            InitializeComponent();
        }

        private void lvPoints_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.Text) || e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void lvPoints_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                var newPoints = new List<PointD>();

                if (e.Data.GetDataPresent(DataFormats.Text))
                {
                    string text = (string)e.Data.GetData(DataFormats.Text);
                    newPoints.AddRange(ParsePointsFromText(text));
                }
                else if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                    foreach (var file in files)
                    {
                        string text = File.ReadAllText(file);
                        newPoints.AddRange(ParsePointsFromText(text));
                    }
                }

                AddPoints(newPoints);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "DragDrop Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            FitColumns();
        }

        private void AddPoints(IEnumerable<PointD> points)
        {
            foreach (var p in points)
            {
                if (_points.Count >= MaxPoints) break;
                _points.Add(p);
            }

            UpdateListView();
            ToggleTimeCtrls();
            Recalculate();
        }

        private void UpdateListView()
        {
            lvPoints.BeginUpdate();
            lvPoints.Items.Clear();

            for (int i = 0; i < _points.Count; i++)
            {
                var p = _points[i];

                var item = new ListViewItem((i + 1).ToString());
                item.SubItems.Add(p.TimestampText ?? string.Empty);
                item.SubItems.Add(p.X.ToString("G17", CultureInfo.InvariantCulture));
                item.SubItems.Add(p.Y.ToString("G17", CultureInfo.InvariantCulture));
                lvPoints.Items.Add(item);
            }

            lvPoints.EndUpdate();
            UpdateUI();
        }

        private void UpdateUI()
        {
            Text = $"Beam Stability Demo - Points: {_points.Count}/{MaxPoints}";
        }

        private IEnumerable<PointD> ParsePointsFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);

            bool csvMode = false;
            char csvDelimiter = ',';
            int tsCol = -1;
            int xCol = -1;
            int yCol = -1;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                line = line.TrimStart('\uFEFF');

                if (!csvMode && DetectHeaderLine(line))
                {
                    csvDelimiter = DetectDelimiter(line);
                    var headerCols = SplitDelimitedLine(line, csvDelimiter);

                    tsCol = FindTimestampColumnIndex(headerCols);
                    xCol = FindXColumnIndex(headerCols);
                    yCol = FindYColumnIndex(headerCols);

                    csvMode = true;
                    continue;
                }

                if (csvMode)
                {
                    var cols = SplitDelimitedLine(line, csvDelimiter);
                    if (TryParsePointFromColumns(cols, tsCol, xCol, yCol, out PointD p))
                    {
                        yield return p;
                        continue;
                    }
                }
                else
                {
                    char candidateDelimiter = DetectDelimiter(line);
                    if (candidateDelimiter != '\0')
                    {
                        var cols = SplitDelimitedLine(line, candidateDelimiter);
                        if (TryParsePointFromColumns(cols, -1, -1, -1, out PointD p))
                        {
                            yield return p;
                            continue;
                        }
                    }
                }

                if (TryParseXY(line, out double x, out double y))
                    yield return new PointD(timestampMicroseconds: null, timestampText: null, x: x, y: y);
            }
        }

        private static bool DetectHeaderLine(string line)
        {
            if (line.IndexOf("timestamp", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (line.IndexOf("centroid", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (Regex.IsMatch(line, @"\b(x|x\s*\(.*\))\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(line, @"\b(y|y\s*\(.*\))\b", RegexOptions.IgnoreCase))
                return true;
            
            return false;
        }

        private static int FindTimestampColumnIndex(IReadOnlyList<string> cols)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                string c = (cols[i] ?? string.Empty).Trim();

                if (c.IndexOf("timestamp", StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;

                if (Regex.IsMatch(c, @"^\s*time(\s*\(.*\))?\s*$", RegexOptions.IgnoreCase))
                    return i;

                if (Regex.IsMatch(c, @"^\s*date(\s*\(.*\))?\s*$", RegexOptions.IgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int FindXColumnIndex(IReadOnlyList<string> cols)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                string c = (cols[i] ?? string.Empty).Trim();

                if (c.IndexOf("centroid", StringComparison.OrdinalIgnoreCase) >= 0
                    && Regex.IsMatch(c, @"\bx\b", RegexOptions.IgnoreCase))
                    return i;

                if (Regex.IsMatch(c, @"^\s*x(\s*\(.*\))?\s*$", RegexOptions.IgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int FindYColumnIndex(IReadOnlyList<string> cols)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                string c = (cols[i] ?? string.Empty).Trim();

                if (c.IndexOf("centroid", StringComparison.OrdinalIgnoreCase) >= 0
                    && Regex.IsMatch(c, @"\by\b", RegexOptions.IgnoreCase))
                    return i;

                if (Regex.IsMatch(c, @"^\s*y(\s*\(.*\))?\s*$", RegexOptions.IgnoreCase))
                    return i;
            }

            return -1;
        }

        private static char DetectDelimiter(string line)
        {
            int comma = CountCharOutsideQuotes(line, ',');
            int semicolon = CountCharOutsideQuotes(line, ';');
            int tab = CountCharOutsideQuotes(line, '\t');

            int max = Math.Max(comma, Math.Max(semicolon, tab));
            if (max <= 0)
                return '\0';

            if (max == tab) return '\t';
            if (max == semicolon) return ';';
            return ',';
        }

        private static int CountCharOutsideQuotes(string s, char ch)
        {
            bool inQuotes = false;
            int count = 0;

            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < s.Length && s[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && c == ch)
                    count++;
            }

            return count;
        }

        private static List<string> SplitDelimitedLine(string line, char delimiter)
        {
            var result = new List<string>();
            if (line == null)
                return result;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && c == delimiter)
                {
                    result.Add(sb.ToString().Trim());
                    sb.Clear();
                    continue;
                }

                sb.Append(c);
            }

            result.Add(sb.ToString().Trim());
            return result;
        }

        private bool TryGetSelectedWindowMicrosec(out long windowMicroseconds)
        {
            windowMicroseconds = 0;

            if (rbtnShortTerm.Checked)
            {
                windowMicroseconds = 1L * MicrosecPerSecond;
                return true;
            }

            if (rbtnMidTerm.Checked)
            {
                windowMicroseconds = 60L * MicrosecPerSecond;
                return true;
            }

            if (rbtnLongTerm.Checked)
            {
                windowMicroseconds = 3600L * MicrosecPerSecond;
                return true;
            }

            if (rbtnCustomTime.Checked)
            {
                if (!double.TryParse(txtCustomTime.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out double seconds)
                    && !double.TryParse(txtCustomTime.Text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out seconds))
                {
                    return false;
                }

                if (seconds <= 0)
                    return false;

                // Use double type only for custom parameters
                // Convert integers into microseconds
                windowMicroseconds = (long)Math.Round(seconds * MicrosecPerSecond, MidpointRounding.AwayFromZero);
                return windowMicroseconds > 0;
            }

            return false;
        }

        private static bool TryParsePointFromColumns(IReadOnlyList<string> cols, int tsCol, int xCol, int yCol, out PointD p)
        {
            p = default(PointD);

            if (cols == null || cols.Count < 2)
                return false;

            int xi;
            int yi;

            if (xCol >= 0 && yCol >= 0 && xCol < cols.Count && yCol < cols.Count)
            {
                xi = xCol;
                yi = yCol;
            }
            else
            {
                xi = cols.Count - 2;
                yi = cols.Count - 1;
            }

            if (!TryParseDouble(cols[xi], out double x)) return false;
            if (!TryParseDouble(cols[yi], out double y)) return false;

            int candidateTsCol = -1;
            if (tsCol >= 0 && tsCol < cols.Count)
                candidateTsCol = tsCol;
            else if (cols.Count >= 3)
                candidateTsCol = 0;

            long? tsMicros = null;
            string tsText = null;

            if (candidateTsCol >= 0)
            {
                string rawTs = (cols[candidateTsCol] ?? string.Empty).Trim();
                if (rawTs.Length > 0 && TryParseTimestampToMicrosec(rawTs, out long microseconds))
                {
                    tsMicros = microseconds;
                    tsText = rawTs;
                }
            }

            p = new PointD(timestampMicroseconds: tsMicros, timestampText: tsText, x: x, y: y);
            return true;
        }

        private static bool TryParseXY(string line, out double x, out double y)
        {
            x = y = 0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            var matches = Regex.Matches(line, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?");
            if (matches.Count < 2)
                return false;

            var s1 = matches[matches.Count - 2].Value;
            var s2 = matches[matches.Count - 1].Value;

            if (!TryParseDouble(s1, out x)) return false;
            if (!TryParseDouble(s2, out y)) return false;

            return true;
        }

        private static bool TryParseDouble(string s, out double v)
        {
            var style = NumberStyles.Float | NumberStyles.AllowThousands;

            return double.TryParse(s, style, CultureInfo.CurrentCulture, out v)
                || double.TryParse(s, style, CultureInfo.InvariantCulture, out v);
        }

        private static bool TryParseTimestampToMicrosec(string s, out long microseconds)
        {
            microseconds = 0;

            if (string.IsNullOrWhiteSpace(s))
                return false;

            s = s.Trim();

            if (TryParseTimeSpanToMicrosec(s, out microseconds))
                return true;

            if (TryParseDateTimeToMicrosec(s, out microseconds))
                return true;

            if (TryParseDateTime(s, out DateTime dt))
            {
                DateTime utc = dt.ToUniversalTime();
                long ticks = utc.Ticks - UnixEpochUtc.Ticks;
                microseconds = ticks / TicksPerMicrosec;
                return true;
            }

            return false;
        }

        private static bool TryParseTimeSpanToMicrosec(string s, out long microseconds)
        {
            microseconds = 0;

            if (!Regex.IsMatch(s, @"^\s*\d{1,3}:\d{2}(:\d{2})?([.,]\d+)?\s*$"))
                return false;

            s = s.Replace(',', '.');

            // hh:mm:ss (.f)
            if (s.Count(c => c == ':') == 2)
            {
                string[] parts = s.Split(':');
                if (parts.Length != 3) return false;

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h)) return false;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return false;

                if (!TryParseFracSecToMicrosec(parts[2], out long secMicros)) return false;

                microseconds = ((long)h * 3600L + (long)m * 60L) * MicrosecPerSecond + secMicros;
                return true;
            }

            // mm:ss (.f)
            if (s.Count(c => c == ':') == 1)
            {
                string[] parts = s.Split(':');
                if (parts.Length != 2) return false;

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m)) return false;

                if (!TryParseFracSecToMicrosec(parts[1], out long secMicros)) return false;

                microseconds = ((long)m * 60L) * MicrosecPerSecond + secMicros;
                return true;
            }

            return false;
        }

        // "9.8" -> 9s 800000us, "09.1234567" -> 9s 123456us (microsecond precision clipped)
        private static bool TryParseFracSecToMicrosec(string s, out long microseconds)
        {
            microseconds = 0;

            if (string.IsNullOrWhiteSpace(s))
                return false;

            s = s.Trim();

            int dot = s.IndexOf('.');
            if (dot < 0)
            {
                if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int secWhole))
                    return false;

                microseconds = (long)secWhole * MicrosecPerSecond;
                return true;
            }

            string wholePart = s.Substring(0, dot);
            string fracPart = s.Substring(dot + 1);

            if (!int.TryParse(wholePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
                return false;

            // Microseconds : take up to 6 digits, pad right
            if (fracPart.Length > 6)
                fracPart = fracPart.Substring(0, 6);
            fracPart = fracPart.PadRight(6, '0');

            if (!int.TryParse(fracPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fracMicros))
                return false;

            microseconds = (long)sec * MicrosecPerSecond + fracMicros;
            return true;
        }

        private static bool TryParseDateTimeToMicrosec(string s, out long microseconds)
        {
            microseconds = 0;

            // 2026-01-03 17:50:47.978000
            // 2026-01-03 17:50:47.9780000
            // 2026-01-03T17:50:47.978000
            var m = Regex.Match(
                s,
                @"^\s*(\d{4})-(\d{2})-(\d{2})[ T](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?\s*$",
                RegexOptions.CultureInvariant);

            if (!m.Success)
                return false;

            if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int year)) return false;
            if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int month)) return false;
            if (!int.TryParse(m.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int day)) return false;
            if (!int.TryParse(m.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour)) return false;
            if (!int.TryParse(m.Groups[5].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minute)) return false;
            if (!int.TryParse(m.Groups[6].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int second)) return false;

            // Fractional seconds : 1 ~ 7 digits (ticks precision). Convert to microseconds (6 digits)
            string frac = m.Groups[7].Success ? m.Groups[7].Value : string.Empty;

            // Convert fraction to microseconds (6 digits), rounding from 7 digits if present
            int micros = 0;
            if (frac.Length > 0)
            {
                // Normalize to 7 digits first (ticks), then to microseconds
                string frac7 = frac.Length > 7 ? frac.Substring(0, 7) : frac.PadRight(7, '0');

                if (!int.TryParse(frac7, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ticksFraction))
                    return false;

                // ticksFraction is 0 .. 9_999_999 ticks within second
                // Microseconds = ticks / 10 with rounding
                micros = (ticksFraction + 5) / 10;
                if (micros >= 1_000_000)
                {
                    // Carry to next second
                    micros = 0;
                    second++;
                }
            }

            try
            {
                // Construct UTC DateTime (treat input as local? Existing code uses ToUniversalTime() on parsed datetime.)
                // Preserve the previous behavior by creating Unspecified then ToUniversalTime().
                var dt = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Unspecified)
                    .AddSeconds(second)
                    .AddTicks(micros * TicksPerMicrosec);

                DateTime utc = dt.ToUniversalTime();
                long ticks = utc.Ticks - UnixEpochUtc.Ticks;
                microseconds = ticks / TicksPerMicrosec;
                return true;
            }
            catch
            {
                return false;
            }
        }
        private static bool TryParseDateTime(string s, out DateTime dt)
        {
            dt = default(DateTime);

            string[] formats =
            {
                "yyyy-MM-dd HH:mm:ss.FFFFFFF",
                "yyyy-MM-dd HH:mm:ss,FFFFFFF",
                "yyyy-MM-dd HH:mm:ss",
                "M/d/yyyy h:mm:ss tt",
                "M/d/yyyy hh:mm:ss tt",
                "M/d/yyyy H:mm:ss",
                "M/d/yyyy HH:mm:ss",
                "M/d/yyyy H:mm:ss.FFFFFFF",
                "M/d/yyyy HH:mm:ss.FFFFFFF",
                "yyyy/MM/dd HH:mm:ss.FFFFFFF",
                "yyyy/MM/dd HH:mm:ss",
            };

            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
                return true;

            CultureInfo[] cultures =
            {
                CultureInfo.CurrentCulture,
                CultureInfo.InvariantCulture,
                CultureInfo.GetCultureInfo("en-US"),
                CultureInfo.GetCultureInfo("en-GB"),
                CultureInfo.GetCultureInfo("ko-KR"),
            };

            for (int i = 0; i < cultures.Length; i++)
            {
                if (DateTime.TryParse(s, cultures[i], DateTimeStyles.AllowWhiteSpaces, out dt))
                    return true;
            }

            return false;
        }

        private void Recalculate()
        {
            int total = _points.Count;

            GetConsideredRange(total, out int start, out int considered);

            lblTotal.Text = total.ToString();
            lblConsidered.Text = considered.ToString();

            if (considered < 2)
            {
                SetResultColor(isOk: false);
                lblCentroidX.Text = "-";
                lblCentroidY.Text = "-";
                lblAzimuth.Text = "-";
                lblDeltaX.Text = "-";
                lblDeltaY.Text = "-";
                lblDelta.Text = "-";
                return;
            }

            ComputeCentroid(start, total, out double meanX, out double meanY);
            double azimuthRad = ComputeAzimuthFromCov(start, total, meanX, meanY);
            int n = FillRotatedCtrToCache(start, total, meanX, meanY, azimuthRad);

            double stdXp = StdDevSample(_xpCache, n, 0.0);
            double stdYp = StdDevSample(_ypCache, n, 0.0);

            double deltaX = 4.0 * stdXp;
            double deltaY = 4.0 * stdYp;

            double radialStd = Math.Sqrt(stdXp * stdXp + stdYp * stdYp);
            double delta = 2.0 * Math.Sqrt(2.0) * radialStd;

            string azText;
            if (rbtnDeg.Checked)
            {
                double azDeg = azimuthRad * 180.0 / Math.PI;
                azText = azDeg.ToString("F8", CultureInfo.InvariantCulture) + " deg";
            }
            else
            {
                azText = azimuthRad.ToString("F8", CultureInfo.InvariantCulture) + " rad";
            }

            lblCentroidX.Text = $"{meanX.ToString("F6", CultureInfo.InvariantCulture)} mm";
            lblCentroidY.Text = $"{meanY.ToString("F6", CultureInfo.InvariantCulture)} mm";
            lblAzimuth.Text = azText;

            lblDeltaX.Text = $"{deltaX.ToString("F6", CultureInfo.InvariantCulture)} mm";
            lblDeltaY.Text = $"{deltaY.ToString("F6", CultureInfo.InvariantCulture)} mm";
            lblDelta.Text = $"{delta.ToString("F6", CultureInfo.InvariantCulture)} mm";

            SetResultColor(isOk: true);
        }


        private void ComputeCentroid(int start, int total, out double meanX, out double meanY)
        {
            double sx = 0, sy = 0;
            int count = total - start;

            for (int i = start; i < total; i++)
            {
                sx += _points[i].X;
                sy += _points[i].Y;
            }

            meanX = sx / count;
            meanY = sy / count;
        }

        private double ComputeAzimuthFromCov(int start, int total, double meanX, double meanY)
        {
            double sxx = 0, syy = 0, sxy = 0;
            int n = total - start;

            for (int i = start; i < total; i++)
            {
                double dx = _points[i].X - meanX;
                double dy = _points[i].Y - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
            }

            double denom = (n - 1);
            sxx /= denom;
            syy /= denom;
            sxy /= denom;

            return 0.5 * Math.Atan2(2.0 * sxy, (sxx - syy));
        }

        private int FillRotatedCtrToCache(int start, int total, double meanX, double meanY, double azimuthRad)
        {
            int count = total - start;

            EnsureCacheCapacity(count);

            double c = Math.Cos(-azimuthRad);
            double s = Math.Sin(-azimuthRad);

            int k = 0;
            for (int i = start; i < total; i++)
            {
                double dx = _points[i].X - meanX;
                double dy = _points[i].Y - meanY;

                double xp = dx * c - dy * s;
                double yp = dx * s + dy * c;

                _xpCache[k] = xp;
                _ypCache[k] = yp;
                k++;
            }

            return count;
        }

        private static double StdDevSample(double[] values, int count, double mean)
        {
            double sumSq = 0;

            for (int i = 0; i < count; i++)
            {
                double d = values[i] - mean;
                sumSq += d * d;
            }

            if (count <= 1) return 0;
            return Math.Sqrt(sumSq / (count - 1));
        }

        //private static double StdDevPopulation(IEnumerable<double> values, double mean)
        //{
        //    double sumSq = 0;
        //    int n = 0;

        //    foreach (var v in values)
        //    {
        //        double d = v - mean;
        //        sumSq += d * d;
        //        n++;
        //    }

        //    if (n <= 0) return 0;
        //    return Math.Sqrt(sumSq / n);
        //}

        private void GetConsideredRange(int total, out int start, out int considered)
        {
            start = 0;
            considered = total;

            if (total <= 0)
                return;

            // Value-based
            if (rbtnAllValues.Checked)
            {
                start = 0;
                considered = total;
                return;
            }

            if (rbtn1000Values.Checked)
            {
                considered = Math.Min(total, 1000);
                start = total - considered;
                return;
            }

            if (rbtnCustomSeq.Checked)
            {
                if (TryParsePositiveInt(lblCustomSeqNumber.Text, out int n))
                {
                    considered = Math.Min(total, n);
                    start = total - considered;
                }
                return;
            }

            // Time-based
            if (!HasAnyTimestampMicrosec())
            {
                considered = Math.Min(total, 1000);
                start = total - considered;
                return;
            }

            if (!TryGetSelectedWindowMicrosec(out long windowMicros))
            {
                considered = Math.Min(total, 1000);
                start = total - considered;
                return;
            }

            long? lastTs = GetLastTimestampMicrosecOrNull();
            if (lastTs == null)
            {
                considered = Math.Min(total, 1000);
                start = total - considered;
                return;
            }

            long threshold = lastTs.Value - windowMicros;

            int idx = total - 1;
            while (idx >= 0)
            {
                long? ts = _points[idx].TimestampMicroseconds;
                if (ts == null || ts.Value < threshold) // Threshold boundary is inclusive
                    break;

                idx--;
            }

            start = Math.Max(0, idx + 1);
            considered = total - start;
        }

        private static bool TryParsePositiveInt(string text, out int value)
        {
            value = 0;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value) && value > 0)
                return true;

            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0)
                return true;

            value = 0;
            return false;
        }
        private bool HasAnyTimestampMicrosec()
        {
            for (int i = 0; i < _points.Count; i++)
            {
                if (_points[i].TimestampMicroseconds != null)
                    return true;
            }

            return false;
        }

        private long? GetLastTimestampMicrosecOrNull()
        {
            for (int i = _points.Count - 1; i >= 0; i--)
            {
                var ts = _points[i].TimestampMicroseconds;
                if (ts != null)
                    return ts;
            }

            return null;
        }

        private void ToggleTimeCtrls()
        {
            bool hasAnyTimestamp = HasAnyTimestampMicrosec();

            rbtnShortTerm.Enabled = hasAnyTimestamp;
            rbtnMidTerm.Enabled = hasAnyTimestamp;
            rbtnLongTerm.Enabled = hasAnyTimestamp;
            rbtnCustomTime.Enabled = hasAnyTimestamp;
            txtCustomTime.Enabled = hasAnyTimestamp;
            lblSeconds.Enabled = hasAnyTimestamp;

            if (!hasAnyTimestamp)
            {
                bool anyTimeChecked = rbtnShortTerm.Checked || rbtnMidTerm.Checked || rbtnLongTerm.Checked || rbtnCustomTime.Checked;
                if (anyTimeChecked)
                    rbtn1000Values.Checked = true;
            }
        }

        private void SetResultColor(bool isOk)
        {
            var c = isOk ? Color.Black : Color.Red;

            lblCentroidX.ForeColor = c;
            lblCentroidY.ForeColor = c;
            lblAzimuth.ForeColor = c;
            lblDeltaX.ForeColor = c;
            lblDeltaY.ForeColor = c;
            lblDelta.ForeColor = c;

            lblTotal.ForeColor = c;
            lblConsidered.ForeColor = c;
        }

        private void rbtnDeg_CheckedChanged(object sender, EventArgs e) 
        { 
            Recalculate(); 
        }

        private void rbtnRad_CheckedChanged(object sender, EventArgs e) 
        { 
            Recalculate(); 
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            AutoFitColumns();

            if (!rbtnShortTerm.Checked && !rbtnMidTerm.Checked && !rbtnLongTerm.Checked && !rbtnCustomTime.Checked
                && !rbtnAllValues.Checked && !rbtn1000Values.Checked && !rbtnCustomSeq.Checked)
            {
                rbtn1000Values.Checked = true;
            }

            rbtnRad.Checked = true;

            ToggleTimeCtrls();
            Recalculate();
        }

        private void SettingsChanged(object sender, EventArgs e)
        {
            ToggleTimeCtrls();
            Recalculate();
        }

        private void FitColumns()
        {
            lvPoints.BeginUpdate();
            try
            {
                lvPoints.AutoResizeColumns(ColumnHeaderAutoResizeStyle.ColumnContent);
            }
            finally
            {
                lvPoints.EndUpdate();
            }
        }

        private void AutoFitColumns()
        {
            foreach (ColumnHeader col in lvPoints.Columns)
                col.Width = -2;
        }

        private void lvPoints_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.V)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;

                try
                {
                    var newPoints = new List<PointD>();

                    if (Clipboard.ContainsText())
                    {
                        string text = Clipboard.GetText();
                        newPoints.AddRange(ParsePointsFromText(text));
                    }
                    else if (Clipboard.ContainsFileDropList())
                    {
                        var files = Clipboard.GetFileDropList();
                        foreach (string file in files)
                        {
                            string text = File.ReadAllText(file);
                            newPoints.AddRange(ParsePointsFromText(text));
                        }
                    }

                    if (newPoints.Count > 0)
                        AddPoints(newPoints);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Paste Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }

                FitColumns();
            }
        }

        private readonly struct PointD
        {
            public long? TimestampMicroseconds { get; }
            public string TimestampText { get; }
            public double X { get; }
            public double Y { get; }

            public PointD(long? timestampMicroseconds, string timestampText, double x, double y)
            {
                TimestampMicroseconds = timestampMicroseconds;
                TimestampText = timestampText;
                X = x;
                Y = y;
            }
        }

        #region Event Handlers for Settings
        private void rbtnShortTerm_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtnMidTerm_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtnLongTerm_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtnCustomTime_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void txtCustomTime_TextChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtnAllValues_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtn1000Values_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void rbtnCustomSeq_CheckedChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }

        private void lblCustomSeqNumber_TextChanged(object sender, EventArgs e)
        {
            SettingsChanged(sender, e);
        }
    }
    #endregion
}