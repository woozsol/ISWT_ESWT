using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ISWT;

public partial class MainWindow : Window
{
    private readonly Stopwatch stopwatch = new();
    private readonly DispatcherTimer displayTimer = new();
    private readonly (double Speed, double Interval, int Shuttles)[] levels =
    {
        (1.80,20,3),(2.41,14.9,4),(3.02,11.9,5),(3.64,9.9,6),
        (4.25,8.5,7),(4.86,7.4,8),(5.47,6.6,9),(6.08,5.9,10),
        (6.70,5.4,11),(7.31,4.9,12),(7.92,4.5,13),(8.53,4.2,14)
    };
    private int levelIndex, totalShuttles, shuttlesThisLevel;
    private double nextShuttleAt = 20, nextLevelAt = 60;
    private bool hasStarted, isStopped, resultShown;
    private string completionReason = "";
    private string entryMmrc = "";
    private string entryOxygen = "";
    private string entryRespRate = "";
    private string entryBirthDate = "";

    public MainWindow()
    {
        InitializeComponent();
        // UI 타이머 간격을 줄여 셔틀 신호가 예정 시각에 더 가깝게 처리되도록 합니다.
        displayTimer.Interval = TimeSpan.FromMilliseconds(10);
        displayTimer.Tick += DisplayTimer_Tick;
        UpdateDisplay();
    }


    private void EntryPatientAge_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        string raw = EntryPatientAge.Text.Trim();
        string digits = Regex.Replace(raw, @"\D", "");
        if (digits.Length != 8 || !DateTime.TryParseExact(digits, "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime birth) || birth.Date > DateTime.Today)
        {
            MessageBox.Show("생년월일을 YYYY / MM / DD 형식으로 정확히 입력해 주세요.\n예: 1975 / 01 / 13", "생년월일 확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            EntryPatientAge.Focus(); EntryPatientAge.SelectAll(); return;
        }
        int age = DateTime.Today.Year - birth.Year;
        if (birth.Date > DateTime.Today.AddYears(-age)) age--;
        if (age < 1 || age > 119)
        {
            MessageBox.Show("계산된 나이가 1~119세 범위를 벗어납니다.");
            EntryPatientAge.Focus(); EntryPatientAge.SelectAll(); return;
        }
        entryBirthDate = birth.ToString("yyyy / MM / dd", CultureInfo.InvariantCulture);
        EntryPatientAge.Text = age.ToString(CultureInfo.InvariantCulture);
        UpdateEntryHeartRate(age);
    }

    private void UpdateEntryHeartRate(int age)
    {
        if (EntryPredictedHr == null || EntryTargetHr == null) return;
        int predicted = (int)Math.Round(210 - age * 0.65, MidpointRounding.AwayFromZero);
        int target = (int)Math.Round(predicted * 0.85, MidpointRounding.AwayFromZero);
        EntryPredictedHr.Text = $"{predicted} 회/분";
        EntryTargetHr.Text = $"{target} 회/분";
    }

    private bool PreparePatientData()
    {
        if (string.IsNullOrWhiteSpace(EntryPatientName.Text))
        { MessageBox.Show("환자 이름을 입력해 주세요."); EntryPatientName.Focus(); return false; }
        if (!Regex.IsMatch(EntryPatientRegistration.Text.Trim(), @"^\d{8}$"))
        { MessageBox.Show("등록번호는 숫자 8자리로 입력해 주세요."); EntryPatientRegistration.Focus(); return false; }
        if (!PositiveNumber(EntryPatientHeight.Text, out _))
        { MessageBox.Show("키를 올바르게 입력해 주세요."); EntryPatientHeight.Focus(); return false; }
        if (!PositiveNumber(EntryPatientWeight.Text, out _))
        { MessageBox.Show("몸무게를 올바르게 입력해 주세요."); EntryPatientWeight.Focus(); return false; }
        if (!int.TryParse(EntryPatientAge.Text, out int age) || age < 1 || age > 119 || string.IsNullOrWhiteSpace(entryBirthDate))
        { MessageBox.Show("생년월일을 YYYY / MM / DD 형식으로 입력한 뒤 Enter를 눌러 나이로 변환해 주세요."); EntryPatientAge.Focus(); return false; }

        PatientName.Text = EntryPatientName.Text.Trim();
        PatientRegistration.Text = EntryPatientRegistration.Text.Trim();
        PatientHeight.Text = EntryPatientHeight.Text.Trim();
        PatientWeight.Text = EntryPatientWeight.Text.Trim();
        PatientAge.Text = EntryPatientAge.Text.Trim();
        RestingSpo2Box.Text = EntryRestingSpo2.Text.Trim();
        RestingHrBox.Text = EntryRestingHr.Text.Trim();
        RestingSystolicBox.Text = EntryRestingSystolic.Text.Trim();
        RestingDiastolicBox.Text = EntryRestingDiastolic.Text.Trim();
        entryMmrc = EntryMmrc0.IsChecked == true ? "0" : EntryMmrc1.IsChecked == true ? "1" :
            EntryMmrc2.IsChecked == true ? "2" : EntryMmrc3.IsChecked == true ? "3" :
            EntryMmrc4.IsChecked == true ? "4" : "";
        entryOxygen = EntryOxygen.Text.Trim();
        entryRespRate = EntryRespRate.Text.Trim();
        return true;
    }

    private string BuildPatientInfo() =>
        $"이름: {PatientName.Text}\n등록번호: {PatientRegistration.Text}\n키: {PatientHeight.Text} cm\n몸무게: {PatientWeight.Text} kg\n나이: {PatientAge.Text}";

    private void StartEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!PreparePatientData()) return;
        EntryOverlay.Visibility = Visibility.Collapsed;
        TestRoot.IsEnabled = true;
        StatusText.Text = "ISWT 검사 준비";
    }

    private void StartEswtEntry_Click(object sender, RoutedEventArgs e)
    {
        if (!PreparePatientData()) return;

        // ESWT 단독 경로: ISWT 측정값은 미시행으로 두고 동일한 리포트/ESWT 화면을 엽니다.
        string testInfo = "ISWT 검사: 미시행\n종료 유형: ESWT 단독 진행";
        var resultWindow = new ResultWindow(BuildPatientInfo(), testInfo, PatientAge.Text,
            PredictedHrText.Text, TargetHrText.Text,
            RestingSpo2Box.Text, RestingHrBox.Text,
            RestingSystolicBox.Text, RestingDiastolicBox.Text,
            entryMmrc, entryOxygen, entryRespRate, eswtOnly: true, birthDate: entryBirthDate) { Owner = this };
        resultWindow.ShowDialog();
    }

    private void Registration_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");

    private void Registration_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string pasted = (string)e.DataObject.GetData(typeof(string))!;
            if (!Regex.IsMatch(pasted, @"^\d{0,8}$")) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private bool PositiveNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) && value > 0;

    private bool ValidatePatient()
    {
        if (string.IsNullOrWhiteSpace(PatientName.Text))
        { MessageBox.Show("환자 이름을 입력해 주세요."); PatientName.Focus(); return false; }
        if (!Regex.IsMatch(PatientRegistration.Text.Trim(), @"^\d{8}$"))
        { MessageBox.Show("등록번호는 숫자 8자리로 입력해 주세요."); PatientRegistration.Focus(); return false; }
        if (!PositiveNumber(PatientHeight.Text, out _))
        { MessageBox.Show("키를 올바르게 입력해 주세요."); PatientHeight.Focus(); return false; }
        if (!PositiveNumber(PatientWeight.Text, out _))
        { MessageBox.Show("몸무게를 올바르게 입력해 주세요."); PatientWeight.Focus(); return false; }
        return true;
    }

    private void PatientAge_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (PredictedHrText == null || TargetHrText == null) return;
        if (int.TryParse(PatientAge.Text, out int age) && age > 0 && age < 120)
        {
            int predicted = (int)Math.Round(210 - age * 0.65, MidpointRounding.AwayFromZero);
            int target = (int)Math.Round(predicted * 0.85, MidpointRounding.AwayFromZero);
            PredictedHrText.Text = $"{predicted} 회/분";
            TargetHrText.Text = $"{target} 회/분";
        }
        else
        {
            PredictedHrText.Text = "-- 회/분";
            TargetHrText.Text = "-- 회/분";
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (isStopped) return;
        if (!hasStarted)
        {
            if (!ValidatePatient()) return;
            hasStarted = true;
            nextShuttleAt = levels[0].Interval;
            nextLevelAt = 60;
            SystemSounds.Beep.Play();
        }
        stopwatch.Start(); displayTimer.Start();
        StatusText.Text = "검사 진행 중"; LevelText.Text = $"{levelIndex + 1}단계";
        StartButton.IsEnabled = false; PauseButton.IsEnabled = true; StopButton.IsEnabled = true;
        UpdateDisplay();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!hasStarted || isStopped) return;
        if (stopwatch.IsRunning)
        {
            stopwatch.Stop(); displayTimer.Stop();
            PauseButton.Content = "계속"; StatusText.Text = "일시 정지";
        }
        else
        {
            stopwatch.Start(); displayTimer.Start();
            PauseButton.Content = "일시 정지"; StatusText.Text = "검사 진행 중";
            LevelText.Text = $"{levelIndex + 1}단계";
        }
        UpdateDisplay();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (!hasStarted || isStopped) return;
        stopwatch.Stop(); displayTimer.Stop(); isStopped = true;
        completionReason = "중도 종료"; StatusText.Text = "검사 종료";
        StartButton.IsEnabled = false; PauseButton.IsEnabled = false; StopButton.IsEnabled = false;
        UpdateDisplay(); ShowResult();
    }

    private void DisplayTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        bool shuttleSignalDue = false;
        bool levelSignalDue = false;

        // 예정된 시각을 지나친 경우에도 놓친 이벤트를 시간순으로 처리합니다.
        while (!isStopped)
        {
            if (nextShuttleAt <= nextLevelAt && elapsed >= nextShuttleAt)
            {
                totalShuttles++;
                shuttlesThisLevel++;
                nextShuttleAt += levels[levelIndex].Interval;
                shuttleSignalDue = true;
                continue;
            }

            if (elapsed >= nextLevelAt)
            {
                if (levelIndex == levels.Length - 1)
                {
                    FinishTest();
                    return;
                }

                levelIndex++;
                shuttlesThisLevel = 0;
                nextShuttleAt = levelIndex * 60.0 + levels[levelIndex].Interval;
                nextLevelAt += 60.0;
                LevelText.Text = $"{levelIndex + 1}단계";
                levelSignalDue = true;
                continue;
            }

            break;
        }

        UpdateDisplay();

        // 신호 시각에는 먼저 00을 표시하고, 같은 UI 처리 안에서 소리를 재생합니다.
        if (shuttleSignalDue)
        {
            CountdownText.Text = "00";
            GaugeArc.Data = Geometry.Empty;
            SystemSounds.Beep.Play();
        }
        else if (levelSignalDue)
        {
            SystemSounds.Beep.Play();
        }
    }

    private void FinishTest()
    {
        stopwatch.Stop(); displayTimer.Stop(); isStopped = true;
        completionReason = "자동 완료"; StatusText.Text = "검사 완료"; LevelText.Text = "검사 완료";
        StartButton.IsEnabled = false; PauseButton.IsEnabled = false; StopButton.IsEnabled = false;
        UpdateDisplay(); ShowResult();
    }

    private void UpdateDisplay()
    {
        double elapsed = stopwatch.Elapsed.TotalSeconds;
        TimeSpan t = stopwatch.Elapsed;
        TimerText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        SpeedText.Text = $"권장 속도 {levels[levelIndex].Speed:F1} km/h";
        LevelShuttleText.Text = $"{shuttlesThisLevel}/{levels[levelIndex].Shuttles}";
        ShuttleText.Text = $"{totalShuttles}회"; DistanceText.Text = $"{totalShuttles * 10} m";
        double interval = levels[levelIndex].Interval;
        double remaining = Math.Max(0, Math.Min(interval, nextShuttleAt - elapsed));
        CountdownText.Text = Math.Ceiling(remaining).ToString("00");
        GaugeArc.Data = MakeArc(interval <= 0 ? 0 : remaining / interval);
        GaugeArc.Stroke = remaining <= 3 ? Brushes.Crimson : new SolidColorBrush(Color.FromRgb(59,130,246));
    }

    private Geometry MakeArc(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction < 0.001) return Geometry.Empty;
        const double c = 145, r = 125;
        double angle = -90 + 359.9 * fraction, rad = angle * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(c, c-r), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment(new Point(c+r*Math.Cos(rad), c+r*Math.Sin(rad)),
            new Size(r,r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry(); g.Figures.Add(figure); return g;
    }

    private void ShowResult()
    {
        if (resultShown) return;
        resultShown = true;
        TimeSpan t = stopwatch.Elapsed;
        string patient = BuildPatientInfo();
        string result = $"종료 유형: {completionReason}\n검사 시간: {(int)t.TotalMinutes:00}:{t.Seconds:00}\n최종 단계: {levelIndex+1}단계\n완료 단계: {levelIndex}\n현재 단계 셔틀: {shuttlesThisLevel}\n누적 셔틀: {totalShuttles}회\n총 거리: {totalShuttles*10} m";
        new ResultWindow(patient, result, PatientAge.Text,
            PredictedHrText.Text, TargetHrText.Text,
            RestingSpo2Box.Text, RestingHrBox.Text,
            RestingSystolicBox.Text, RestingDiastolicBox.Text,
            entryMmrc, entryOxygen, entryRespRate, birthDate: entryBirthDate) { Owner = this }.ShowDialog();
    }
}
