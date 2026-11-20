using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SlotboundTrainer.Localization;
using SlotboundTrainer.Memory;

namespace SlotboundTrainer;

public partial class MainWindow : Window
{
    private readonly LuaJitMemoryEngine _engine = new();
    private readonly DispatcherTimer _timer = new();
    private bool _isScanning;
    private bool _inTick;
    private bool _wasInMenu;
    private bool _suppressSelection;
    private List<RosterUnitInfo> _cachedUnits = new();

    private static readonly SolidColorBrush BrushGold = new(Color.FromRgb(0xF5, 0xC5, 0x5B));
    private static readonly SolidColorBrush BrushGreen = new(Color.FromRgb(0x34, 0xD3, 0x99));
    private static readonly SolidColorBrush BrushRed = new(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly SolidColorBrush BrushSub = new(Color.FromRgb(0x9A, 0xA3, 0xC2));

    public MainWindow()
    {
        InitializeComponent();
        RefreshLocalizedDynamicControls();

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += async (_, _) => await AttachAsync();
        Closing += (_, _) =>
        {
            _timer.Stop();
            if (_engine.IsAttached)
            {
                if (SwZeroSpinCost.IsChecked == true) _engine.RestoreSpinCostDefaults();
                if (SwFreeShopReroll.IsChecked == true) _engine.RestoreShopRerollCost();
                if (SwForceWin.IsChecked == true) _engine.SetForceWinOnce(false);
            }
            _engine.Dispose();
        };

        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    private void EnableDarkTitleBar()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int useDark = 1;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { /* ignore on older Windows */ }
    }

    private void BtnLang_Click(object sender, RoutedEventArgs e)
    {
        LangManager.Toggle();
        RefreshLocalizedDynamicControls();
        if (_engine.IsAttached)
        {
            TxtStatus.Text = LangManager.F("FmtConnected", _engine.ProcessId, _engine.GlobalTableAddress);
            RefreshOddsUI(syncInputs: false);
            RefreshUnitsGrid();
        }
        else
        {
            TxtStatus.Text = LangManager.T("FmtDisconnected");
        }
        Log(LangManager.T("MsgLangSwitched"), BrushGreen);
    }

    private void RefreshLocalizedDynamicControls()
    {
        ColUnitSym.Header = LangManager.T("UnitColSym");
        ColUnitName.Header = LangManager.T("UnitColName");
        ColUnitGrade.Header = LangManager.T("UnitColGrade");
        ColUnitRank.Header = LangManager.T("UnitColRank");
        ColUnitAbsorb.Header = LangManager.T("UnitColAbsorb");
        ColUnitAtk.Header = LangManager.T("UnitColAtk");
        ColUnitHp.Header = LangManager.T("UnitColHp");
        ColUnitAspd.Header = LangManager.T("UnitColAspd");
        ColUnitSlots.Header = LangManager.T("UnitColSlots");
        UpdateRaritySumLabel();
    }

    private void NavTab_Checked(object sender, RoutedEventArgs e)
    {
        if (PageResources == null || PageOdds == null || PageUnits == null || PageLogs == null) return;
        PageResources.Visibility = NavResources.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageOdds.Visibility = NavOdds.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageUnits.Visibility = NavUnits.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageLogs.Visibility = NavLogs.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavOdds.IsChecked == true && _engine.IsAttached) RefreshOddsUI(syncInputs: false);
        if (NavUnits.IsChecked == true && _engine.IsAttached) RefreshUnitsGrid();
    }

    private async Task AttachAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        BtnAttach.IsEnabled = false;
        ClearAllLocks(restoreToggles: false);

        StatusDot.Fill = BrushGold;
        TxtStatus.Text = LangManager.T("FmtScanning");
        TxtStatus.Foreground = BrushGold;

        string msg = "";
        bool ok = await Task.Run(() => _engine.Attach(out msg));

        _isScanning = false;
        BtnAttach.IsEnabled = true;

        if (ok)
        {
            StatusDot.Fill = BrushGreen;
            TxtStatus.Text = LangManager.F("FmtConnected", _engine.ProcessId, _engine.GlobalTableAddress);
            TxtStatus.Foreground = BrushGreen;
            Log(LangManager.IsEnglish ? $"Attached to Slotbound.exe (PID {_engine.ProcessId}, _G 0x{_engine.GlobalTableAddress:X})" : msg, BrushGreen);
            RefreshUnitsGrid();
            RefreshOddsUI(syncInputs: true);
        }
        else
        {
            StatusDot.Fill = BrushRed;
            TxtStatus.Text = LangManager.T("FmtDisconnected");
            TxtStatus.Foreground = BrushRed;
            Log(msg, BrushRed);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_inTick || _isScanning || !_engine.IsAttached) return;
        _inTick = true;
        try
        {
            var snap = _engine.ReadSnapshot();
            if (!snap.Attached)
            {
                ClearAllLocks(restoreToggles: false);
                StatusDot.Fill = BrushRed;
                TxtStatus.Text = LangManager.T("FmtLostConn");
                TxtStatus.Foreground = BrushRed;
                return;
            }

            string menuNote = snap.IsInMenu ? LangManager.T("FmtMenuPaused") : "";
            TxtGameInfo.Text = LangManager.F("FmtGameInfo",
                snap.Phase, snap.Stage, snap.WaveId, snap.RosterCount, snap.AlliesCount, snap.EnemiesCount, menuNote);

            TxtCoinsCur.Text = snap.Coins.ToString("0.##", CultureInfo.InvariantCulture);
            TxtJungsuCur.Text = snap.Jungsu.ToString("0.##", CultureInfo.InvariantCulture);
            TxtSpinsCur.Text = snap.SpinsLeft.ToString("0.##", CultureInfo.InvariantCulture);
            TxtFreeSpinsCur.Text = snap.FreeSpinCredits.ToString("0.##", CultureInfo.InvariantCulture);
            TxtRerollsCur.Text = snap.CoreFreeRerolls.ToString("0.##", CultureInfo.InvariantCulture);
            TxtBaseHpCur.Text = snap.BaseHp.ToString("0.##", CultureInfo.InvariantCulture);
            TxtOriginiteCur.Text = snap.OriginiteEarned.ToString("0.##", CultureInfo.InvariantCulture);

            if (snap.IsInMenu)
            {
                if (!_wasInMenu)
                {
                    _wasInMenu = true;
                    Log(LangManager.T("MsgMenuPausedLog"), BrushGold);
                }
                return;
            }
            if (_wasInMenu)
            {
                _wasInMenu = false;
                Log(LangManager.T("MsgMenuResumedLog"), BrushGreen);
            }

            if (SwLockCoins.IsChecked == true && TryParseBox(BoxCoins, out double c)) _engine.SetCoins(c);
            if (SwLockJungsu.IsChecked == true && TryParseBox(BoxJungsu, out double j)) _engine.SetJungsu(j);
            if (SwLockSpins.IsChecked == true && TryParseBox(BoxSpins, out double s)) _engine.SetSpinsLeft(s);
            if (SwLockFreeSpins.IsChecked == true && TryParseBox(BoxFreeSpins, out double fs)) _engine.SetFreeSpinCredits(fs);
            if (SwLockRerolls.IsChecked == true && TryParseBox(BoxRerolls, out double rr)) _engine.SetCoreFreeRerolls(rr);
            if (SwLockBaseHp.IsChecked == true && TryParseBox(BoxBaseHp, out double bhp)) _engine.SetBaseHp(bhp);
            if (SwZeroSpinCost.IsChecked == true) _engine.SetZeroSpinCost();
            if (SwFreeShopReroll.IsChecked == true) _engine.SetFreeShopReroll();
            if (SwForceWin.IsChecked == true) _engine.SetForceWinOnce(true);
            if (SwGodAllies.IsChecked == true && snap.AlliesCount > 0) _engine.HealAlliesFull();
            if (SwOneHitEnemies.IsChecked == true && snap.EnemiesCount > 0) _engine.WeakenAllEnemies(1.0);

            if (PageOdds.Visibility == Visibility.Visible) RefreshOddsUI(syncInputs: false);
        }
        catch (Exception ex)
        {
            Log($"[Error] {ex.Message}", BrushRed);
        }
        finally
        {
            _inTick = false;
        }
    }

    private void ClearAllLocks(bool restoreToggles)
    {
        bool wasZero = SwZeroSpinCost.IsChecked == true;
        bool wasShop = SwFreeShopReroll.IsChecked == true;
        bool wasForce = SwForceWin.IsChecked == true;

        SwLockCoins.IsChecked = false;
        SwLockJungsu.IsChecked = false;
        SwLockSpins.IsChecked = false;
        SwLockFreeSpins.IsChecked = false;
        SwLockRerolls.IsChecked = false;
        SwLockBaseHp.IsChecked = false;
        SwZeroSpinCost.IsChecked = false;
        SwFreeShopReroll.IsChecked = false;
        SwForceWin.IsChecked = false;
        SwGodAllies.IsChecked = false;
        SwOneHitEnemies.IsChecked = false;

        if (restoreToggles && _engine.IsAttached)
        {
            if (wasZero) _engine.RestoreSpinCostDefaults();
            if (wasShop) _engine.RestoreShopRerollCost();
            if (wasForce) _engine.SetForceWinOnce(false);
        }
    }

    // ==================== Header & Resource Handlers ====================

    private async void BtnAttach_Click(object sender, RoutedEventArgs e) => await AttachAsync();

    private void BtnUnlockAll_Click(object sender, RoutedEventArgs e)
    {
        ClearAllLocks(restoreToggles: true);
        Log(LangManager.T("MsgUnlockedAll"), BrushGold);
    }

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox tb) return;
        e.Handled = true;
        switch (tb.Tag as string)
        {
            case "coins": BtnSetCoins_Click(sender, e); break;
            case "jungsu": BtnSetJungsu_Click(sender, e); break;
            case "spins": BtnSetSpins_Click(sender, e); break;
            case "freespins": BtnSetFreeSpins_Click(sender, e); break;
            case "rerolls": BtnSetRerolls_Click(sender, e); break;
            case "basehp": BtnSetBaseHp_Click(sender, e); break;
            case "originite": BtnSetOriginite_Click(sender, e); break;
        }
    }

    private void BtnSetCoins_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxCoins, v => _engine.SetCoins(v), LangManager.T("ResCoins"));
    private void BtnAddCoins1k_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.Coins, 1000, v => _engine.SetCoins(v), LangManager.T("ResCoins"));
    private void BtnAddCoins10k_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.Coins, 10000, v => _engine.SetCoins(v), LangManager.T("ResCoins"));

    private void BtnSetJungsu_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxJungsu, v => _engine.SetJungsu(v), LangManager.T("ResEssence"));
    private void BtnAddJungsu50_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.Jungsu, 50, v => _engine.SetJungsu(v), LangManager.T("ResEssence"));
    private void BtnAddJungsu500_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.Jungsu, 500, v => _engine.SetJungsu(v), LangManager.T("ResEssence"));

    private void BtnSetSpins_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxSpins, v => _engine.SetSpinsLeft(v), LangManager.T("ResSpins"));
    private void BtnAddSpins10_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.SpinsLeft, 10, v => _engine.SetSpinsLeft(v), LangManager.T("ResSpins"));
    private void BtnAddSpins50_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.SpinsLeft, 50, v => _engine.SetSpinsLeft(v), LangManager.T("ResSpins"));

    private void BtnSetFreeSpins_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxFreeSpins, v => _engine.SetFreeSpinCredits(v), LangManager.T("ResFreeSpins"));
    private void BtnAddFreeSpins10_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.FreeSpinCredits, 10, v => _engine.SetFreeSpinCredits(v), LangManager.T("ResFreeSpins"));
    private void BtnAddFreeSpins50_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.FreeSpinCredits, 50, v => _engine.SetFreeSpinCredits(v), LangManager.T("ResFreeSpins"));

    private void BtnSetRerolls_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxRerolls, v => _engine.SetCoreFreeRerolls(v), LangManager.T("ResCoreRerolls"));
    private void BtnAddRerolls10_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.CoreFreeRerolls, 10, v => _engine.SetCoreFreeRerolls(v), LangManager.T("ResCoreRerolls"));
    private void BtnAddRerolls50_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.CoreFreeRerolls, 50, v => _engine.SetCoreFreeRerolls(v), LangManager.T("ResCoreRerolls"));

    private void BtnSetBaseHp_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxBaseHp, v => _engine.SetBaseHp(v), LangManager.T("ResBaseHp"));
    private void BtnAddBaseHp10_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.BaseHp, 10, v => _engine.SetBaseHp(v), LangManager.T("ResBaseHp"));
    private void BtnSetBaseHp999_Click(object sender, RoutedEventArgs e) => ExecWrite(() => _engine.SetBaseHp(999), LangManager.F("MsgSetItem", LangManager.T("ResBaseHp"), 999));

    private void BtnSetOriginite_Click(object sender, RoutedEventArgs e) => SetFromBox(BoxOriginite, v => _engine.SetOriginiteEarned(v), LangManager.T("ResOriginite"));
    private void BtnAddOriginite1k_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.OriginiteEarned, 1000, v => _engine.SetOriginiteEarned(v), LangManager.T("ResOriginite"));
    private void BtnAddOriginite10k_Click(object sender, RoutedEventArgs e) => AddDelta(s => s.OriginiteEarned, 10000, v => _engine.SetOriginiteEarned(v), LangManager.T("ResOriginite"));

    // ==================== Switches & Battle Handlers ====================

    private void SwZeroSpinCost_Changed(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsAttached) return;
        if (SwZeroSpinCost.IsChecked == true)
        {
            _engine.SetZeroSpinCost();
            Log(LangManager.T("MsgZeroSpinOn"), BrushGreen);
        }
        else
        {
            _engine.RestoreSpinCostDefaults();
            Log(LangManager.T("MsgZeroSpinOff"), BrushGold);
        }
    }

    private void SwFreeShopReroll_Changed(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsAttached) return;
        if (SwFreeShopReroll.IsChecked == true)
        {
            _engine.SetFreeShopReroll();
            Log(LangManager.T("MsgFreeShopOn"), BrushGreen);
        }
        else
        {
            _engine.RestoreShopRerollCost();
            Log(LangManager.T("MsgFreeShopOff"), BrushGold);
        }
    }

    private void SwForceWin_Changed(object sender, RoutedEventArgs e)
    {
        if (!_engine.IsAttached) return;
        bool on = SwForceWin.IsChecked == true;
        _engine.SetForceWinOnce(on);
        Log(LangManager.T(on ? "MsgForceWinOn" : "MsgForceWinOff"), on ? BrushGreen : BrushGold);
    }

    private void BtnHealAllies_Click(object sender, RoutedEventArgs e)
    {
        int n = _engine.HealAlliesFull();
        Log(LangManager.F("MsgHealedAllies", n), BrushGreen);
    }

    private void BtnWeakenEnemies_Click(object sender, RoutedEventArgs e)
    {
        int n = _engine.WeakenAllEnemies(1.0);
        Log(LangManager.F("MsgWeakenedEnemies", n), BrushGreen);
    }

    // ==================== Probability Page Handlers ====================

    private void RefreshOddsUI(bool syncInputs)
    {
        if (!_engine.IsAttached) return;
        var p = _engine.ReadProbabilities();
        if (!p.Available) return;

        TxtProbNormalCur.Text = LangManager.F("FmtCurrentPct", p.NormalPct);
        TxtProbAdvancedCur.Text = LangManager.F("FmtCurrentPct", p.AdvancedPct);
        TxtProbRareCur.Text = LangManager.F("FmtCurrentPct", p.RarePct);
        TxtProbLegendaryCur.Text = LangManager.F("FmtCurrentPct", p.LegendaryPct);
        TxtProbPrismCur.Text = LangManager.F("FmtCurrentPct", p.PrismPct);
        TxtForceWinCur.Text = LangManager.F("FmtCurrentPct", p.ForceWinPct);

        TxtSymWCur.Text = LangManager.F("FmtCurrentPct", p.WeightW);
        TxtSymACur.Text = LangManager.F("FmtCurrentPct", p.WeightA);
        TxtSymSCur.Text = LangManager.F("FmtCurrentPct", p.WeightS);
        TxtSymHCur.Text = LangManager.F("FmtCurrentPct", p.WeightH);
        TxtSymMCur.Text = LangManager.F("FmtCurrentPct", p.WeightM);
        TxtSymDCur.Text = LangManager.F("FmtCurrentPct", p.WeightD);

        if (syncInputs)
        {
            BoxProbNormal.Text = p.NormalPct.ToString("0.##", CultureInfo.InvariantCulture);
            BoxProbAdvanced.Text = p.AdvancedPct.ToString("0.##", CultureInfo.InvariantCulture);
            BoxProbRare.Text = p.RarePct.ToString("0.##", CultureInfo.InvariantCulture);
            BoxProbLegendary.Text = p.LegendaryPct.ToString("0.##", CultureInfo.InvariantCulture);
            BoxProbPrism.Text = p.PrismPct.ToString("0.##", CultureInfo.InvariantCulture);
            BoxForceWinProb.Text = p.ForceWinPct.ToString("0.##", CultureInfo.InvariantCulture);

            BoxSymW.Text = p.WeightW.ToString("0.##", CultureInfo.InvariantCulture);
            BoxSymA.Text = p.WeightA.ToString("0.##", CultureInfo.InvariantCulture);
            BoxSymS.Text = p.WeightS.ToString("0.##", CultureInfo.InvariantCulture);
            BoxSymH.Text = p.WeightH.ToString("0.##", CultureInfo.InvariantCulture);
            BoxSymM.Text = p.WeightM.ToString("0.##", CultureInfo.InvariantCulture);
            BoxSymD.Text = p.WeightD.ToString("0.##", CultureInfo.InvariantCulture);
            UpdateRaritySumLabel();
        }
    }

    private void RarityBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRaritySumLabel();

    private void UpdateRaritySumLabel()
    {
        if (TxtRaritySum == null || BoxProbNormal == null || BoxProbAdvanced == null ||
            BoxProbRare == null || BoxProbLegendary == null || BoxProbPrism == null) return;

        double sum = 0;
        if (TryParseBox(BoxProbNormal, out double n)) sum += n;
        if (TryParseBox(BoxProbAdvanced, out double a)) sum += a;
        if (TryParseBox(BoxProbRare, out double r)) sum += r;
        if (TryParseBox(BoxProbLegendary, out double l)) sum += l;
        if (TryParseBox(BoxProbPrism, out double pr)) sum += pr;

        TxtRaritySum.Text = LangManager.F("FmtRaritySum", sum);
        TxtRaritySum.Foreground = Math.Abs(sum - 100.0) < 0.05 ? BrushGreen : BrushGold;
    }

    private void BtnRefreshOdds_Click(object sender, RoutedEventArgs e)
    {
        RefreshOddsUI(syncInputs: true);
        Log(LangManager.T("MsgOddsRefreshed"), BrushGreen);
    }

    private void RarityBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnApplyRarity_Click(sender, e); }
    }

    private void ForceWinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnApplyForceWin_Click(sender, e); }
    }

    private void SymbolBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnApplySymbols_Click(sender, e); }
    }

    private void UnitBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; BtnApplyUnit_Click(sender, e); }
    }

    private void BtnClearLog_Click(object sender, RoutedEventArgs e)
    {
        TxtLog.Clear();
        Log(LangManager.T("MsgLogCleared"), BrushSub);
    }

    private void BtnApplyRarity_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseBox(BoxProbNormal, out double n) ||
            !TryParseBox(BoxProbAdvanced, out double a) ||
            !TryParseBox(BoxProbRare, out double r) ||
            !TryParseBox(BoxProbLegendary, out double l) ||
            !TryParseBox(BoxProbPrism, out double pr))
        {
            Log(LangManager.T("MsgInvalidRarity"), BrushRed);
            return;
        }
        if (n + a + r + l + pr <= 0)
        {
            Log(LangManager.T("MsgZeroRarityGuard"), BrushRed);
            return;
        }
        ExecWrite(() => _engine.SetRarityProbabilities(n, a, r, l, pr),
            LangManager.F("MsgRaritySet", n, a, r, l, pr));
        RefreshOddsUI(syncInputs: false);
    }

    private void ApplyRarityPreset(double n, double a, double r, double l, double pr, string labelKey)
    {
        BoxProbNormal.Text = n.ToString("0.##", CultureInfo.InvariantCulture);
        BoxProbAdvanced.Text = a.ToString("0.##", CultureInfo.InvariantCulture);
        BoxProbRare.Text = r.ToString("0.##", CultureInfo.InvariantCulture);
        BoxProbLegendary.Text = l.ToString("0.##", CultureInfo.InvariantCulture);
        BoxProbPrism.Text = pr.ToString("0.##", CultureInfo.InvariantCulture);
        ExecWrite(() => _engine.SetRarityProbabilities(n, a, r, l, pr),
            LangManager.F("MsgPresetApplied", LangManager.T(labelKey)));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnPresetLegendary_Click(object sender, RoutedEventArgs e) => ApplyRarityPreset(0, 0, 0, 100, 0, "BtnPresetLeg");
    private void BtnPresetPrism_Click(object sender, RoutedEventArgs e) => ApplyRarityPreset(0, 0, 0, 0, 100, "BtnPresetPrism");
    private void BtnPresetHalfLegPrism_Click(object sender, RoutedEventArgs e) => ApplyRarityPreset(0, 0, 0, 50, 50, "BtnPresetHalf");

    private void BtnRestoreRarity_Click(object sender, RoutedEventArgs e)
    {
        ExecWrite(() => _engine.RestoreDefaultRarityProbabilities(), LangManager.T("MsgRarityRestored"));
        RefreshOddsUI(syncInputs: true);
    }

    private void BtnApplyForceWin_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseBox(BoxForceWinProb, out double v))
        {
            Log(LangManager.T("MsgInvalidForceWin"), BrushRed);
            return;
        }
        ExecWrite(() => _engine.SetSlotWinProbability(v, 0), LangManager.F("MsgForceWinSet", Math.Clamp(v, 0, 100)));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnForceWin100_Click(object sender, RoutedEventArgs e)
    {
        BoxForceWinProb.Text = "100";
        ExecWrite(() => _engine.SetSlotWinProbability(100, 0), LangManager.F("MsgForceWinSet", 100));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnForceWin0_Click(object sender, RoutedEventArgs e)
    {
        BoxForceWinProb.Text = "0";
        ExecWrite(() => _engine.SetSlotWinProbability(0, 0), LangManager.T("MsgForceWinZero"));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnApplySymbols_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseBox(BoxSymW, out double w) ||
            !TryParseBox(BoxSymA, out double a) ||
            !TryParseBox(BoxSymS, out double s) ||
            !TryParseBox(BoxSymH, out double h) ||
            !TryParseBox(BoxSymM, out double m) ||
            !TryParseBox(BoxSymD, out double d))
        {
            Log(LangManager.T("MsgInvalidSymbols"), BrushRed);
            return;
        }
        if (w + a + s + h + m + d <= 0)
        {
            Log(LangManager.T("MsgZeroSymbolsGuard"), BrushRed);
            return;
        }
        ExecWrite(() => _engine.SetSymbolWeights(w, a, s, h, m, d),
            LangManager.F("MsgSymbolsSet", w, a, s, h, m, d));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnEqualSymbols_Click(object sender, RoutedEventArgs e)
    {
        BoxSymW.Text = "16.7";
        BoxSymA.Text = "16.7";
        BoxSymS.Text = "16.7";
        BoxSymH.Text = "16.7";
        BoxSymM.Text = "16.6";
        BoxSymD.Text = "16.6";
        ExecWrite(() => _engine.SetSymbolWeights(16.7, 16.7, 16.7, 16.7, 16.6, 16.6), LangManager.T("MsgSymbolsEqual"));
        RefreshOddsUI(syncInputs: false);
    }

    private void BtnRestoreSymbols_Click(object sender, RoutedEventArgs e)
    {
        ExecWrite(() => _engine.RestoreDefaultSymbolWeights(), LangManager.T("MsgSymbolsRestored"));
        RefreshOddsUI(syncInputs: true);
    }

    // ==================== Units Page Handlers ====================

    private void BtnRefreshUnits_Click(object sender, RoutedEventArgs e) => RefreshUnitsGrid();

    private void BtnBoostAtk_Click(object sender, RoutedEventArgs e)
    {
        int n = _engine.BoostAllRosterUnits(2.0, 0, 0);
        Log(LangManager.F("MsgBoostAtk", n), BrushGreen);
        RefreshUnitsGrid();
    }

    private void BtnBoostHp_Click(object sender, RoutedEventArgs e)
    {
        int n = _engine.BoostAllRosterUnits(0, 2.0, 0);
        Log(LangManager.F("MsgBoostHp", n), BrushGreen);
        RefreshUnitsGrid();
    }

    private void BtnBoostAspd_Click(object sender, RoutedEventArgs e)
    {
        int n = _engine.BoostAllRosterUnits(0, 0, 1.5);
        Log(LangManager.F("MsgBoostAspd", n), BrushGreen);
        RefreshUnitsGrid();
    }

    private void RefreshUnitsGrid()
    {
        if (!_engine.IsAttached) return;
        int prevIndex = GridUnits.SelectedIndex;
        _suppressSelection = true;
        try
        {
            _cachedUnits = _engine.GetRosterUnits();
            GridUnits.ItemsSource = null;
            GridUnits.ItemsSource = _cachedUnits;
            if (_cachedUnits.Count > 0)
            {
                GridUnits.SelectedIndex = Math.Clamp(prevIndex < 0 ? 0 : prevIndex, 0, _cachedUnits.Count - 1);
            }
        }
        finally
        {
            _suppressSelection = false;
        }
        SyncSelectedUnitEditor();
    }

    private void GridUnits_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressSelection) SyncSelectedUnitEditor();
    }

    private void SyncSelectedUnitEditor()
    {
        if (GridUnits.SelectedItem is not RosterUnitInfo u) return;
        BoxUnitAtk.Text = u.Atk.ToString("0.##", CultureInfo.InvariantCulture);
        BoxUnitHp.Text = u.Hp.ToString("0.##", CultureInfo.InvariantCulture);
        BoxUnitAspd.Text = u.Aspd.ToString("0.###", CultureInfo.InvariantCulture);
        BoxUnitAbsorb.Text = u.AbsorbLv.ToString("0", CultureInfo.InvariantCulture);
        BoxUnitSlots.Text = u.MaxImprintSlots.ToString("0", CultureInfo.InvariantCulture);
    }

    private void BtnApplyUnit_Click(object sender, RoutedEventArgs e)
    {
        if (GridUnits.SelectedItem is not RosterUnitInfo u)
        {
            Log(LangManager.T("MsgSelectUnitFirst"), BrushGold);
            return;
        }

        double? atk = TryParseBox(BoxUnitAtk, out double vAtk) ? vAtk : null;
        double? hp = TryParseBox(BoxUnitHp, out double vHp) ? vHp : null;
        double? aspd = TryParseBox(BoxUnitAspd, out double vAspd) ? vAspd : null;
        double? absorb = TryParseBox(BoxUnitAbsorb, out double vAbs) ? vAbs : null;
        double? slots = TryParseBox(BoxUnitSlots, out double vSlots) ? vSlots : null;

        bool ok = _engine.UpdateRosterUnitByIdentity(u.Index, u.Symbol, u.Subclass, atk, hp, aspd, absorb, slots);
        if (ok)
            Log(LangManager.F("MsgUnitUpdated", u.Index, u.DisplayName, atk ?? u.Atk, hp ?? u.Hp, aspd ?? u.Aspd), BrushGreen);
        else
            Log(LangManager.F("MsgUnitBlocked", u.Index, u.DisplayName), BrushRed);

        RefreshUnitsGrid();
    }

    // ==================== Helpers ====================

    private static bool TryParseBox(TextBox tb, out double value) =>
        double.TryParse(tb.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        !double.IsNaN(value) && !double.IsInfinity(value);

    private void SetFromBox(TextBox tb, Func<double, bool> writer, string name)
    {
        if (!TryParseBox(tb, out double val))
        {
            Log(LangManager.F("MsgInvalidNum", name), BrushRed);
            return;
        }
        ExecWrite(() => writer(val), LangManager.F("MsgSetItem", name, val));
    }

    private void AddDelta(Func<GameStateSnapshot, double> getter, double delta, Func<double, bool> writer, string name)
    {
        if (!_engine.IsAttached) { Log(LangManager.T("MsgNotAttached"), BrushRed); return; }
        var snap = _engine.ReadSnapshot();
        double next = getter(snap) + delta;
        ExecWrite(() => writer(next), LangManager.F("MsgAddItem", name, delta, next));
    }

    private void ExecWrite(Func<bool> action, string msg)
    {
        if (!_engine.IsAttached)
        {
            Log(LangManager.T("MsgNotAttached"), BrushRed);
            return;
        }
        bool ok = action();
        if (ok) Log(LangManager.F("MsgWriteOk", msg), BrushGreen);
        else Log(LangManager.F("MsgWriteFail", msg), BrushRed);
    }

    private void Log(string message, Brush? color = null)
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        void Update()
        {
            ToastDot.Fill = color ?? BrushSub;
            TxtToast.Text = $"{ts}  {message}";
            TxtToast.Foreground = color ?? BrushSub;

            if (TxtLog.Text.Length > 20000)
                TxtLog.Text = TxtLog.Text.Substring(TxtLog.Text.Length - 10000);
            TxtLog.AppendText($"[{ts}] {message}{Environment.NewLine}");
            TxtLog.ScrollToEnd();
        }

        if (!Dispatcher.CheckAccess()) Dispatcher.BeginInvoke((Action)Update);
        else Update();
    }
}
