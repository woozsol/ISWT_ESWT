using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Media;
using System.Media;
using System.Printing;

namespace ISWT;

public partial class ResultWindow : Window
{
    private readonly string patientInfo;
    private readonly string testInfo;
    private readonly string ageText;
    private readonly string predictedHrText;
    private readonly string targetHrText;
    private readonly string restingSpo2;
    private readonly string restingHr;
    private readonly string restingSystolic;
    private readonly string restingDiastolic;
    private readonly string entryMmrc;
    private readonly string entryOxygen;
    private readonly string entryRespRate;
    private readonly Stopwatch eswtStopwatch = new();
    private readonly DispatcherTimer eswtTimer = new();
    private bool eswtHasStarted;
    private double eswtLastInterval;
    private int lastWarmupCue;
    private const double EswtWarmupSeconds = 120.0;

    public ResultWindow(string patientInfo, string testInfo, string ageText,
        string predictedHrText, string targetHrText, string restingSpo2,
        string restingHr, string restingSystolic, string restingDiastolic,
        string entryMmrc = "", string entryOxygen = "", string entryRespRate = "",
        bool eswtOnly = false, string birthDate = "")
    {
        InitializeComponent();
        this.patientInfo = patientInfo;
        this.testInfo = testInfo;
        this.ageText = ageText;
        this.predictedHrText = predictedHrText;
        this.targetHrText = targetHrText;
        this.restingSpo2 = restingSpo2;
        this.restingHr = restingHr;
        this.restingSystolic = restingSystolic;
        this.restingDiastolic = restingDiastolic;
        this.entryMmrc = entryMmrc;
        this.entryOxygen = entryOxygen;
        this.entryRespRate = entryRespRate;

        // 환자 기본정보를 2행 3열 표에 배치합니다.
        var info = patientInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimEnd('\r'))
            .Select(line => line.Split(new[] { ':' }, 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim());
        PatientNameValue.Text = info.GetValueOrDefault("이름", "");
        PatientRegValue.Text = info.GetValueOrDefault("등록번호", "");
        PatientHeightValue.Text = info.GetValueOrDefault("키", "").Replace(" cm", "") + " cm";
        PatientWeightValue.Text = info.GetValueOrDefault("몸무게", "").Replace(" kg", "") + " kg";
        PatientAgeValue.Text = info.GetValueOrDefault("나이", "") + " 세";
        BirthDateBox.Text = birthDate;
        RestingSpo2ResultText.Text = string.IsNullOrWhiteSpace(restingSpo2) ? "—" : restingSpo2 + " %";
        RestingHrResultText.Text = string.IsNullOrWhiteSpace(restingHr) ? "—" : restingHr + " 회/분";
        RestingBpResultText.Text = string.IsNullOrWhiteSpace(restingSystolic) && string.IsNullOrWhiteSpace(restingDiastolic)
            ? "—" : $"{restingSystolic} / {restingDiastolic} mmHg";

        // Resting 활력징후를 결과표의 '검사 전' 칸에 자동 입력합니다.
        if (!string.IsNullOrWhiteSpace(restingSpo2))
            Spo2Before.Text = restingSpo2.Trim();
        if (!string.IsNullOrWhiteSpace(restingHr))
            HrBefore.Text = restingHr.Trim();
        if (!string.IsNullOrWhiteSpace(restingSystolic) ||
            !string.IsNullOrWhiteSpace(restingDiastolic))
            BpBefore.Text = $"{restingSystolic.Trim()} / {restingDiastolic.Trim()}".Trim();

        PredictedHrBox.Text = Digits(predictedHrText);
        TargetHrBox.Text = Digits(targetHrText);

        CompletedLevelsBox.Text = ExtractLabelValue(testInfo, "완료 단계:");
        CompletedShuttlesBox.Text = ExtractLabelValue(testInfo, "현재 단계 셔틀:");
        TotalShuttlesBox.Text = ExtractNumber(ExtractLabelValue(testInfo, "누적 셔틀:"));
        DistanceBox.Text = ExtractNumber(ExtractLabelValue(testInfo, "총 거리:"));

        if (double.TryParse(DistanceBox.Text, out double distance))
        {
            double peak = 4.19 + 0.025 * distance;
            // 계산식 자체가 아니라 거리로 계산한 결과값을 표시합니다.
            PeakVo2Box.Text = peak.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            TargetVo2Box.Text = (peak * 0.85).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }

        if (entryMmrc == "0") Mmrc0.IsChecked = true;
        else if (entryMmrc == "1") Mmrc1.IsChecked = true;
        else if (entryMmrc == "2") Mmrc2.IsChecked = true;
        else if (entryMmrc == "3") Mmrc3.IsChecked = true;
        else if (entryMmrc == "4") Mmrc4.IsChecked = true;
        OxygenBox.Text = entryOxygen;
        RespRateBox.Text = entryRespRate;

        eswtTimer.Interval = TimeSpan.FromMilliseconds(100);
        eswtTimer.Tick += EswtTimer_Tick;
        if (eswtOnly)
        {
            // ESWT 단독 경로에서는 ISWT를 시행하지 않았으므로 ISWT 결과를 표시하거나 사용할 수 없습니다.
            PeakVo2Box.Clear();
            TargetVo2Box.Clear();
            PeakVo2Box.IsEnabled = false;
            TargetVo2Box.IsEnabled = false;

            // ISWT 측정값을 이용하는 ESWT 계산 방식도 비활성화하고 담당의 처방만 사용합니다.
            EswtFromIswt.IsChecked = false;
            EswtFromIswt.IsEnabled = true;
            EswtVo2Box.Clear();
            EswtSpeedBox.Clear();
            EswtIntervalBox.Clear();
            EswtLevelBox.Clear();
            EswtPrescription.IsChecked = true;
        }
        UpdateEswtMode();
        UpdateEswtTimeAndDistance();
        DrawEswtCountdownArc(1.0);
    }

    private sealed record EswtStage(int Level, double Speed, double ShuttleSeconds);

    private static readonly EswtStage[] EswtStages =
    {
        new(1, 1.78, 20.3), new(2, 2.09, 17.3), new(3, 2.44, 14.8), new(4, 2.72, 13.3),
        new(5, 3.00, 12.0), new(6, 3.27, 11.0), new(7, 3.60, 10.0), new(8, 3.79, 9.5),
        new(9, 4.11, 8.8), new(10, 4.36, 8.3), new(11, 4.65, 7.8), new(12, 4.97, 7.3),
        new(13, 5.14, 7.0), new(14, 5.54, 6.5), new(15, 5.76, 6.3), new(16, 6.00, 6.0)
    };

    private void EswtMode_Checked(object sender, RoutedEventArgs e)
    {
        // ESWT는 사용자가 방식을 선택한 뒤에만 값을 채웁니다.
        // 왼쪽 선택: ISWT 결과의 85% peak VO2를 가져오고 처방값은 초기화합니다.
        if (EswtFromIswt?.IsChecked == true)
        {
            EswtRxLevelBox?.Clear();
            EswtRxSpeedBox?.Clear();
            EswtRxIntervalBox?.Clear();

            if (TargetVo2Box != null && EswtVo2Box != null)
                EswtVo2Box.Text = TargetVo2Box.Text;

            UpdateEswtMode();
            CalculateEswtFromVo2();
            UpdateEswtTimeAndDistance();
        }
        // 오른쪽 선택: ISWT 방식의 값은 지우고 담당의 처방 입력을 사용합니다.
        else if (EswtPrescription?.IsChecked == true)
        {
            EswtVo2Box?.Clear();
            EswtSpeedBox?.Clear();
            EswtIntervalBox?.Clear();
            EswtLevelBox?.Clear();

            UpdateEswtMode();
            UpdatePrescriptionStage();
            UpdateEswtTimeAndDistance();
        }
    }

    private void UpdateEswtMode()
    {
        if (EswtFromIswt == null || EswtPrescription == null) return;

        bool isIswtMode = EswtFromIswt.IsChecked == true;
        bool isPrescriptionMode = EswtPrescription.IsChecked == true;
        bool hasSelection = isIswtMode || isPrescriptionMode;

        // 시작 시에는 양쪽 모두 선택되지 않으며, 선택 전에는 입력칸을 비활성화합니다.
        EswtVo2Box.IsEnabled = isIswtMode;
        EswtSpeedBox.IsEnabled = isIswtMode;
        EswtIntervalBox.IsEnabled = isIswtMode;
        EswtLevelBox.IsEnabled = isIswtMode;

        EswtRxLevelBox.IsEnabled = isPrescriptionMode;
        EswtRxSpeedBox.IsEnabled = isPrescriptionMode;
        EswtRxIntervalBox.IsEnabled = isPrescriptionMode;

        if (!hasSelection)
        {
            EswtVo2Box.Clear();
            EswtSpeedBox.Clear();
            EswtIntervalBox.Clear();
            EswtLevelBox.Clear();
            EswtRxLevelBox.Clear();
            EswtRxSpeedBox.Clear();
            EswtRxIntervalBox.Clear();
            return;
        }

        if (isIswtMode)
            CalculateEswtFromVo2();
        else if (isPrescriptionMode)
            UpdatePrescriptionStage();
    }

    private void CalculateEswtFromVo2()
    {
        if (EswtVo2Box == null || EswtSpeedBox == null ||
            EswtIntervalBox == null || EswtLevelBox == null)
            return;

        if (!double.TryParse(EswtVo2Box.Text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double vo2) &&
            !double.TryParse(EswtVo2Box.Text, out vo2))
        {
            EswtSpeedBox.Clear();
            EswtIntervalBox.Clear();
            EswtLevelBox.Clear();
            return;
        }

        if (vo2 <= 0)
        {
            EswtSpeedBox.Clear();
            EswtIntervalBox.Clear();
            EswtLevelBox.Clear();
            return;
        }

        // 첨부된 표의 VO2 구간 경계와 해당 구간의 속도 기준값입니다.
        // 각 구간에서 선형 보간으로 계산한 속도를 구한 뒤,
        // 가장 가까운 기준 속도에 해당하는 단계(Level)를 선택합니다.
        double[] vo2Points =
            { 0.0, 4.94, 5.94, 7.19, 8.69, 10.44, 12.44, 14.69, 17.19, 19.94 };
        double[] speedPoints =
            { 0.0, 1.80, 2.41, 3.03, 3.63, 4.25, 4.86, 5.47, 6.08, 6.69 };

        double calculatedSpeed;

        if (vo2 >= vo2Points[^1])
        {
            calculatedSpeed = speedPoints[^1];
        }
        else
        {
            int segment = 0;
            while (segment < vo2Points.Length - 2 &&
                   vo2 > vo2Points[segment + 1])
            {
                segment++;
            }

            double fraction =
                (vo2 - vo2Points[segment]) /
                (vo2Points[segment + 1] - vo2Points[segment]);

            calculatedSpeed =
                speedPoints[segment] +
                fraction * (speedPoints[segment + 1] - speedPoints[segment]);
        }

        // 0 km/h는 검사 단계로 사용하지 않으므로 1.80 km/h부터 비교합니다.
        int nearestIndex = 1;
        double smallestDifference = Math.Abs(calculatedSpeed - speedPoints[1]);

        for (int i = 2; i < speedPoints.Length; i++)
        {
            double difference = Math.Abs(calculatedSpeed - speedPoints[i]);
            if (difference < smallestDifference)
            {
                smallestDifference = difference;
                nearestIndex = i;
            }
        }

        // 최종 결과는 가장 가까운 기준 속도와 그 단계로 기록합니다.
        double selectedSpeed = speedPoints[nearestIndex];
        int selectedLevel = nearestIndex;

        EswtLevelBox.Text = selectedLevel.ToString();
        EswtSpeedBox.Text = selectedSpeed.ToString(
            "F2", System.Globalization.CultureInfo.InvariantCulture);

        // 10 m 셔틀 1회 소요시간(초) = 36 / 속도(km/h)
        double shuttleSeconds = 36.0 / selectedSpeed;
        EswtIntervalBox.Text = shuttleSeconds.ToString(
            "F1", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void EswtRxLevelBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (EswtPrescription?.IsChecked == true)
            UpdatePrescriptionStage();
    }

    private void UpdatePrescriptionStage()
    {
        if (EswtRxLevelBox == null || EswtRxSpeedBox == null || EswtRxIntervalBox == null) return;
        if (!int.TryParse(EswtRxLevelBox.Text, out int level))
        {
            EswtRxSpeedBox.Clear(); EswtRxIntervalBox.Clear();
            return;
        }
        var stage = EswtStages.FirstOrDefault(s => s.Level == level);
        if (stage == null)
        {
            EswtRxSpeedBox.Clear(); EswtRxIntervalBox.Clear();
            return;
        }
        EswtRxSpeedBox.Text = stage.Speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        EswtRxIntervalBox.Text = stage.ShuttleSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }


    private double GetSelectedEswtInterval()
    {
        string text = EswtPrescription?.IsChecked == true ? EswtRxIntervalBox.Text : EswtIntervalBox.Text;
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    private double GetSelectedEswtSpeed()
    {
        string text = EswtPrescription?.IsChecked == true ? EswtRxSpeedBox.Text : EswtSpeedBox.Text;
        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : 0;
    }

    private void EswtStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (EswtFromIswt.IsChecked != true && EswtPrescription.IsChecked != true)
        {
            MessageBox.Show("ESWT 방식을 먼저 선택해 주세요.", "확인", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        eswtLastInterval = GetSelectedEswtInterval();
        if (eswtLastInterval <= 0 || GetSelectedEswtSpeed() <= 0)
        {
            MessageBox.Show("85% Peak VO₂ 계산 또는 담당의 처방 단계를 먼저 확인해 주세요.", "확인",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!eswtHasStarted)
        {
            eswtStopwatch.Reset();
            lastWarmupCue = 0;
            eswtHasStarted = true;
        }
        eswtStopwatch.Start();
        eswtTimer.Start();
        UpdateEswtTimerDisplay();
        EswtStartButton.IsEnabled = false;
        EswtStopButton.IsEnabled = true;
        EswtFromIswt.IsEnabled = false;
        EswtPrescription.IsEnabled = false;
        EswtRxLevelBox.IsEnabled = false;
    }

    private void EswtStopButton_Click(object sender, RoutedEventArgs e)
    {
        eswtTimer.Stop();
        eswtStopwatch.Stop();
        EswtStartButton.IsEnabled = false;
        EswtStopButton.IsEnabled = false;
        EswtFromIswt.IsEnabled = false;
        EswtPrescription.IsEnabled = false;
        EswtRxLevelBox.IsEnabled = false;
        UpdateEswtTimerDisplay();
        UpdateEswtTimeAndDistance();
        MessageBox.Show("ESWT 검사가 종료되었습니다. 총 보행시간과 거리가 리포트에 기록되었습니다.",
            "검사 종료", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EswtTimer_Tick(object? sender, EventArgs e)
    {
        UpdateEswtTimerDisplay();
        UpdateEswtTimeAndDistance();
    }

    private void UpdateEswtTimerDisplay()
    {
        double elapsed = eswtStopwatch.Elapsed.TotalSeconds;
        bool warmingUp = elapsed < EswtWarmupSeconds;
        double phaseDuration = warmingUp ? EswtWarmupSeconds : eswtLastInterval;
        double phaseElapsed = warmingUp ? elapsed : elapsed - EswtWarmupSeconds;
        double remaining = warmingUp
            ? Math.Max(0, EswtWarmupSeconds - elapsed)
            : (phaseDuration > 0 ? phaseDuration - (phaseElapsed % phaseDuration) : 0);
        if (!warmingUp && phaseDuration > 0 && remaining < 0.05)
            remaining = phaseDuration;

        EswtCountdownText.Text = FormatClock(remaining);
        EswtCountdownCaption.Text = warmingUp ? "Warming Up" : "1셔틀 소요 시간";
        EswtPhaseText.Text = warmingUp ? "준비 운동 (2분)" : "이제 검사를 시작합니다";
        if (!warmingUp && elapsed >= EswtWarmupSeconds + 4)
            EswtPhaseText.Text = "ESWT 운동 중";

        double fraction = phaseDuration > 0 ? Math.Clamp(remaining / phaseDuration, 0, 1) : 0;
        DrawEswtCountdownArc(fraction);

        // 준비운동 시작 후 30초, 60초, 90초, 120초에 각각 한 번씩 알림을 냅니다.
        int cue = Math.Min(4, (int)Math.Floor(elapsed / 30.0));
        while (lastWarmupCue < cue)
        {
            lastWarmupCue++;
            if (lastWarmupCue < 4)
                SystemSounds.Beep.Play();
            else
            {
                EswtPhaseText.Text = "이제 검사를 시작합니다";
                SystemSounds.Exclamation.Play();
            }
        }

        TimeSpan t = eswtStopwatch.Elapsed;
        EswtElapsedText.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
    }

    private static string FormatClock(double seconds)
    {
        int total = Math.Max(0, (int)Math.Ceiling(seconds));
        return $"{total / 60:00}:{total % 60:00}";
    }

    private void DrawEswtCountdownArc(double fraction)
    {
        if (EswtProgressArc == null) return;
        const double center = 140;
        const double radius = 126;
        double sweep = Math.Clamp(fraction, 0, 1) * 359.9;
        var start = new Point(center, center - radius);
        double radians = (-90 + sweep) * Math.PI / 180.0;
        var end = new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0,
            sweep > 180, SweepDirection.Clockwise, true));
        EswtProgressArc.Data = new PathGeometry(new[] { figure });
    }

    private void EswtTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateEswtTimeAndDistance();
    }

    private void UpdateEswtTimeAndDistance()
    {
        if (EswtTimeMinBox == null || EswtTimeSecBox == null || EswtTimeTotalBox == null || EswtDistanceBox == null) return;
        // 총 보행시간과 거리는 2분 준비운동을 제외한 실제 운동 구간으로 계산합니다.
        int totalSeconds = Math.Max(0, (int)Math.Floor(eswtStopwatch.Elapsed.TotalSeconds - EswtWarmupSeconds));
        EswtTimeMinBox.Text = (totalSeconds / 60).ToString();
        EswtTimeSecBox.Text = (totalSeconds % 60).ToString();
        EswtTimeTotalBox.Text = totalSeconds.ToString();

        double speed = GetSelectedEswtSpeed();
        if (speed > 0)
            EswtDistanceBox.Text = (speed * totalSeconds / 3600.0 * 1000.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        else
            EswtDistanceBox.Clear();
    }

    private static string Digits(string value)
    {
        var only = new string(value.Where(char.IsDigit).ToArray());
        return only;
    }

    private static string ExtractLabelValue(string text, string label)
    {
        foreach (string line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith(label, StringComparison.Ordinal))
                return line[(line.IndexOf(':') + 1)..].Trim();
        }
        return "0";
    }

    private static string ExtractNumber(string value)
    {
        var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+");
        return match.Success ? match.Value : "0";
    }

    private string BuildResultText()
    {
        string mMrc = Mmrc0.IsChecked == true ? "0" : Mmrc1.IsChecked == true ? "1" :
            Mmrc2.IsChecked == true ? "2" : Mmrc3.IsChecked == true ? "3" :
            Mmrc4.IsChecked == true ? "4" : "미선택";
        string stopReasons = string.Join(", ", new[]
        {
            StopDyspnea.IsChecked == true ? "호흡 곤란 호소" : "",
            StopSpO2.IsChecked == true ? "산소포화도 80% 이하" : "",
            StopLeg.IsChecked == true ? "근골격계 문제" : "",
            StopTargetHr.IsChecked == true ? "target HR 초과" : "",
            StopOther.IsChecked == true ? "기타: " + StopOtherBox.Text : ""
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var sb = new StringBuilder();
        sb.AppendLine("ISWT 최종 리포트");
        sb.AppendLine("기록 일시: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine();
        sb.AppendLine("[환자 정보]");
        sb.AppendLine(patientInfo);
        sb.AppendLine("생년월일: " + BirthDateBox.Text);
        sb.AppendLine();
        sb.AppendLine("[검사 전 기본 활력징후]");
        sb.AppendLine($"Resting SpO2: {restingSpo2} %");
        sb.AppendLine($"Resting HR: {restingHr} 회/분");
        sb.AppendLine($"Resting BP: {restingSystolic} / {restingDiastolic} mmHg");
        sb.AppendLine();
        sb.AppendLine("[검사 결과]");
        sb.AppendLine(testInfo);
        sb.AppendLine();
        sb.AppendLine("[호흡곤란 내용 (mMRC)] " + mMrc);
        sb.AppendLine($"산소 흡입: {OxygenBox.Text} L/min");
        sb.AppendLine($"분당 호흡수: {RespRateBox.Text} 회/분");
        sb.AppendLine($"예측 최대 HR: {PredictedHrBox.Text} 회/분");
        sb.AppendLine($"85% target HR: {TargetHrBox.Text} 회/분");
        sb.AppendLine("검사 중단 사유: " + (stopReasons.Length == 0 ? "미선택" : stopReasons));
        sb.AppendLine("추가 보행 가능: " + (FurtherYes.IsChecked == true ? "예" : "아니오"));
        sb.AppendLine();
        sb.AppendLine("[활력징후 및 Borg Scale]");
        sb.AppendLine("항목 | 검사 전 | 검사 직후 | 검사 1분 후");
        sb.AppendLine($"BP (mmHg) | {BpBefore.Text} | {BpAfter.Text} | {BpMinute.Text}");
        sb.AppendLine($"HR (회/분) | {HrBefore.Text} | {HrAfter.Text} | {HrMinute.Text}");
        sb.AppendLine($"SpO2 (%) | {Spo2Before.Text} | {Spo2After.Text} | {Spo2Minute.Text}");
        sb.AppendLine($"호흡곤란 Borg | {DyspneaBefore.Text} | {DyspneaAfter.Text} | {DyspneaMinute.Text}");
        sb.AppendLine($"하지 피로도 Borg | {LegBefore.Text} | {LegAfter.Text} | {LegMinute.Text}");
        sb.AppendLine("HR 회복 시간: " + RecoveryTimeBox.Text);
        sb.AppendLine();
        sb.AppendLine("[ISWT]");
        sb.AppendLine($"완료 단계: {CompletedLevelsBox.Text}");
        sb.AppendLine($"현재 단계 셔틀: {CompletedShuttlesBox.Text}");
        sb.AppendLine($"총 셔틀: {TotalShuttlesBox.Text}");
        sb.AppendLine($"총 보행 거리: {DistanceBox.Text} m");
        sb.AppendLine($"peak VO2: {PeakVo2Box.Text}");
        sb.AppendLine($"85% of peak VO2: {TargetVo2Box.Text}");
        sb.AppendLine();
        sb.AppendLine("[ESWT]");
        sb.AppendLine($"ISWT 기반 85% peak VO2: {EswtVo2Box.Text} ml/min/kg");
        sb.AppendLine($"담당의 운동 처방 사용 여부: {(EswtPrescription.IsChecked == true ? "선택" : "미선택")}");
        sb.AppendLine($"보행 속도: {EswtSpeedBox.Text} km/hr");
        sb.AppendLine($"처방 보행 속도: {EswtRxSpeedBox.Text} km/hr");
        sb.AppendLine($"1셔틀 소요 시간: {EswtIntervalBox.Text} sec");
        sb.AppendLine($"처방 1셔틀 소요 시간: {EswtRxIntervalBox.Text} sec");
        sb.AppendLine($"단계: {EswtLevelBox.Text}");
        sb.AppendLine($"처방 단계: {EswtRxLevelBox.Text}");
        sb.AppendLine($"총 보행 시간: {EswtTimeMinBox.Text}분 {EswtTimeSecBox.Text}초 = {EswtTimeTotalBox.Text}초");
        sb.AppendLine($"ESWT 총 경과시간(준비운동 포함): {EswtElapsedText.Text}");
        sb.AppendLine("ESWT 준비운동: 2분");
        sb.AppendLine($"ESWT 1셔틀 카운트다운: {EswtCountdownText.Text}");
        sb.AppendLine($"총 보행 거리: {EswtDistanceBox.Text} m");
        return sb.ToString();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = "ISWT 결과 저장",
                Filter = "텍스트 파일 (*.txt)|*.txt",
                DefaultExt = ".txt",
                AddExtension = true,
                FileName = $"ISWT_{DateTime.Now:yyyyMMdd_HHmmss}.txt"
            };

            if (dialog.ShowDialog(this) != true) return;
            File.WriteAllText(dialog.FileName, BuildResultText(), new UTF8Encoding(true));
            MessageBox.Show("검사 결과를 TXT 파일로 저장했습니다.", "저장 완료",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("TXT 저장 중 오류가 발생했습니다.\n" + ex.Message, "저장 오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PrintButton_Click(object sender, RoutedEventArgs e)
    {
        string? temporaryTextFile = null;
        try
        {
            temporaryTextFile = Path.Combine(Path.GetTempPath(),
                $"ISWT_Print_{DateTime.Now:yyyyMMdd_HHmmss_fff}.txt");
            File.WriteAllText(temporaryTextFile, BuildResultText(), new UTF8Encoding(true));

            var startInfo = new ProcessStartInfo
            {
                FileName = temporaryTextFile,
                Verb = "print",
                UseShellExecute = true,
                CreateNoWindow = true
            };
            Process.Start(startInfo);
            MessageBox.Show("TXT 리포트를 기본 텍스트 편집기의 인쇄 기능으로 전달했습니다.\n프린터 선택 및 인쇄는 열린 인쇄 창에서 진행해 주세요.",
                "인쇄", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("TXT 인쇄를 시작하지 못했습니다.\n" + ex.Message +
                "\n\n결과 저장 버튼으로 TXT 파일을 저장한 뒤 메모장에서 직접 인쇄할 수도 있습니다.",
                "인쇄 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
