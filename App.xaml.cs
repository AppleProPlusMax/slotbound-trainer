using System;
using System.IO;
using System.Windows;
using SlotboundTrainer.Localization;
using SlotboundTrainer.Memory;

namespace SlotboundTrainer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0] == "--selftest")
        {
            RunSelfTest(e.Args.Length > 1 ? e.Args[1] : null);
            Shutdown(0);
            return;
        }

        LangManager.InitDefault();

        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"发生未处理的界面异常：{ex.Exception.Message}", "Slotbound 修改器 - 安全拦截",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception err)
            {
                MessageBox.Show($"发生严重异常：{err.Message}", "Slotbound 修改器 - 安全拦截",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        base.OnStartup(e);
        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private static void RunSelfTest(string? outPath)
    {
        using var sw = new StringWriter();
        using var engine = new LuaJitMemoryEngine();
        bool ok = engine.Attach(out string msg);
        sw.WriteLine($"Attach: {ok} | {msg}");
        if (ok)
        {
            var snap = engine.ReadSnapshot();
            sw.WriteLine($"Phase={snap.Phase}, Stage={snap.Stage}, Wave={snap.WaveId}");
            sw.WriteLine($"Coins={snap.Coins}, Jungsu={snap.Jungsu}, SpinsLeft={snap.SpinsLeft}, FreeSpins={snap.FreeSpinCredits}");
            sw.WriteLine($"SpinCost={snap.SpinCost}, CoreRerolls={snap.CoreFreeRerolls}, BaseHP={snap.BaseHp}, Originite={snap.OriginiteEarned}, ForceWin={snap.ForceWinOnce}");
            sw.WriteLine($"Roster={snap.RosterCount}, Allies={snap.AlliesCount}, Enemies={snap.EnemiesCount}");

            bool writeOk = engine.SetCoins(snap.Coins);
            sw.WriteLine($"Non-destructive WriteProcessMemory(coins={snap.Coins}): {writeOk}");

            var probs = engine.ReadProbabilities();
            sw.WriteLine($"Probabilities Available={probs.Available}: Normal={probs.NormalPct}%, Adv={probs.AdvancedPct}%, Rare={probs.RarePct}%, Leg={probs.LegendaryPct}%, Prism={probs.PrismPct}%");
            sw.WriteLine($"Slot ForceWin={probs.ForceWinPct}%, Weights: W={probs.WeightW}% A={probs.WeightA}% S={probs.WeightS}% H={probs.WeightH}% M={probs.WeightM}% D={probs.WeightD}%");
            bool probWriteOk = engine.SetRarityProbabilities(probs.NormalPct, probs.AdvancedPct, probs.RarePct, probs.LegendaryPct, probs.PrismPct);
            sw.WriteLine($"Non-destructive WriteProcessMemory(rarityProbs): {probWriteOk}");

            var units = engine.GetRosterUnits();
            sw.WriteLine($"RosterUnits ({units.Count}):");
            foreach (var u in units)
            {
                sw.WriteLine($"  #{u.Index} [{u.Symbol}] {u.DisplayName} ({u.Subclass} / {u.GradeLabel}, rank={u.Rank}, absorb={u.AbsorbLv}) ATK={u.Atk:0.##} HP={u.Hp:0.##} ASPD={u.Aspd:0.###} Slots={u.MaxImprintSlots}");
            }
        }

        string text = sw.ToString();
        if (!string.IsNullOrEmpty(outPath))
            File.WriteAllText(outPath, text);
        else
            Console.WriteLine(text);
    }
}
