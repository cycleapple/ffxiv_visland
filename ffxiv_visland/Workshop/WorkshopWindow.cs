using Dalamud.Interface.Utility.Raii;
using visland.Helpers;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace visland.Workshop;

unsafe class WorkshopWindow : UIAttachedWindow
{
    private WorkshopConfig _config;
    private WorkshopManual _manual = new();
    private WorkshopOCImport _oc = new();
    private WorkshopDebug _debug = new();

    public WorkshopWindow() : base("開拓工房自動化", "MJICraftSchedule", new(500, 650))
    {
        _config = Service.Config.Get<WorkshopConfig>();
    }

    public override void PreOpenCheck()
    {
        base.PreOpenCheck();
        var agent = AgentMJICraftSchedule.Instance();
        IsOpen &= agent != null && agent->Data != null;

        _oc.Update();
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("OC 排程匯入"))
                if (tab)
                    _oc.Draw();
            using (var tab = ImRaii.TabItem("手動排程"))
                if (tab)
                    _manual.Draw();
            using (var tab = ImRaii.TabItem("設定"))
                if (tab)
                    DrawSettings();
            using (var tab = ImRaii.TabItem("偵錯"))
                if (tab)
                    _debug.Draw();
        }
    }

    public override void OnOpen()
    {
        if (_config.AutoOpenNextDay)
        {
            WorkshopUtils.SetCurrentCycle(AgentMJICraftSchedule.Instance()->Data->CycleInProgress + 1);
        }
        if (_config.AutoImport)
        {
            _oc.ImportRecsFromClipboard(true);
        }
    }

    private void DrawSettings()
    {
        if (ImGui.Checkbox("開啟時自動選擇下一個生產週期", ref _config.AutoOpenNextDay))
            _config.NotifyModified();
        if (ImGui.Checkbox("開啟時自動匯入基礎推薦排程", ref _config.AutoImport))
            _config.NotifyModified();
        if (ImGui.Checkbox("使用實驗性需求求解器", ref _config.UseFavorSolver))
            _config.NotifyModified();
    }
}
