using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using ECommons;
using ECommons.DalamudServices;
using ECommons.ImGuiMethods;
using ECommons.Logging;
using ECommons.SimpleGui;
using Dalamud.Bindings.ImGui;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Numerics;
using visland.Helpers;
using visland.IPC;
using static visland.Gathering.GatherRouteDB;

namespace visland.Gathering;

public class GatherWindow : Window, IDisposable
{
    private readonly UITree _tree = new();
    private readonly List<System.Action> _postDraw = [];

    public GatherRouteDB RouteDB = null!;
    public GatherRouteExec Exec = new();
    public GatherDebug _debug = null!;

    private int selectedRouteIndex = -1;
    public static bool loop;

    private readonly List<uint> Colours = GenericHelpers.GetSheet<UIColor>()!.Select(x => x.Dark).ToList();
    private Vector4 greenColor = new Vector4(0x5C, 0xB8, 0x5C, 0xFF) / 0xFF;
    private Vector4 redColor = new Vector4(0xD9, 0x53, 0x4F, 0xFF) / 0xFF;
    private Vector4 yellowColor = new Vector4(0xD9, 0xD9, 0x53, 0xFF) / 0xFF;

    private readonly List<int> Items = GenericHelpers.GetSheet<Item>()?.Select(x => (int)x.RowId).ToList()!;
    private ExcelSheet<Item> _items = null!;

    private string searchString = string.Empty;
    private readonly List<Route> FilteredRoutes = [];
    private FontAwesomeIcon PlayIcon => Exec.CurrentRoute != null && !Exec.Paused ? FontAwesomeIcon.Pause : FontAwesomeIcon.Play;
    private string PlayTooltip => Exec.CurrentRoute == null ? "開始路線" : Exec.Paused ? "繼續路線" : "暫停路線";

    public GatherWindow() : base("採集自動化", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(800, 800);
        SizeCondition = ImGuiCond.FirstUseEver;
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Setup()
    {
        if (EzConfigGui.Window is { } window)
        {
            window.Size = new Vector2(800, 800);
            window.SizeCondition = ImGuiCond.FirstUseEver;
        }
        RouteDB = Service.Config.Get<GatherRouteDB>();

        _debug = new(Exec);
        _items = GenericHelpers.GetSheet<Item>()!;
    }

    public void Dispose() => Exec.Dispose();

    public override void PreOpenCheck() => Exec.Update();

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("Tabs");
        if (tabs)
        {
            using (var tab = ImRaii.TabItem("路線"))
                if (tab)
                {
                    DrawExecution();
                    ImGui.Separator();
                    ImGui.Spacing();

                    var cra = ImGui.GetContentRegionAvail();
                    var sidebar = cra with { X = cra.X * 0.40f };
                    var editor = cra with { X = cra.X * 0.60f };

                    DrawSidebar(sidebar);
                    ImGui.SameLine();
                    DrawEditor(editor);

                    foreach (var a in _postDraw)
                        a();
                    _postDraw.Clear();
                }
            using (var tab = ImRaii.TabItem("紀錄"))
                if (tab)
                    InternalLog.PrintImgui();
            using (var tab = ImRaii.TabItem("偵錯"))
                if (tab)
                    _debug.Draw();
        }
    }

    private void DrawExecution()
    {
        ImGuiEx.Text("狀態：");
        ImGui.SameLine();

        if (Exec.CurrentRoute != null)
            Utils.FlashText($"{(Exec.Paused ? "已暫停" : Exec.Waiting ? "等待中" : "執行中")}", new Vector4(1.0f, 1.0f, 1.0f, 1.0f), Exec.Paused ? new Vector4(1.0f, 0.0f, 0.0f, 1.0f) : new Vector4(0.0f, 1.0f, 0.0f, 1.0f), 2);
        ImGui.SameLine();

        if (Exec.CurrentRoute == null || Exec.CurrentWaypoint >= Exec.CurrentRoute.Waypoints.Count)
        {
            ImGui.Text("目前沒有執行路線");
            return;
        }

        if (Exec.CurrentRoute != null) // Finish() call could've reset it
        {
            ImGui.SameLine();
            ImGuiEx.Text($"{Exec.CurrentRoute.Name}：步驟 #{Exec.CurrentWaypoint + 1} {Exec.CurrentRoute.Waypoints[Exec.CurrentWaypoint].Position}");

            if (Exec.Waiting)
            {
                ImGui.SameLine();
                ImGuiEx.Text($"等待 {Exec.WaitUntil - System.Environment.TickCount64} 毫秒");
            }
        }

        ImGui.SameLine();
        ImGuiEx.Text($"狀態階段：{Exec.CurrentState}");
    }

    private unsafe void DrawSidebar(Vector2 size)
    {
        using (ImRaii.Child("Sidebar", size, false))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                RouteDB.Routes.Add(new() { Name = "Unnamed Route" });
                RouteDB.NotifyModified();
            }

        if (ImGui.IsItemHovered()) ImGui.SetTooltip("建立新路線");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileImport))
                TryImport(RouteDB);
            if (ImGui.IsItemHovered())
            ImGui.SetTooltip("從剪貼簿匯入路線");

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Cog))
                ImGui.OpenPopup("Advanced Options");
            DrawRouteSettingsPopup();

            ImGui.SameLine();
            RapidImport();

            ImGuiEx.TextV("搜尋：");
            ImGui.SameLine();
            ImGuiEx.SetNextItemFullWidth();
            if (ImGui.InputText("###RouteSearch", ref searchString, 500))
            {
                FilteredRoutes.Clear();
                if (searchString.Length > 0)
                {
                    foreach (var route in RouteDB.Routes)
                    {
                        if (route.Name.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase) || route.Group.Contains(searchString, System.StringComparison.CurrentCultureIgnoreCase))
                            FilteredRoutes.Add(route);
                    }
                }
            }

            ImGui.Separator();

            using (ImRaii.Child("routes"))
            {
                var groups = GetGroups(RouteDB, true);
                foreach (var group in groups)
                {
                    foreach (var _ in _tree.Node($"{group}###{groups.IndexOf(group)}", contextMenu: () => ContextMenuGroup(group)))
                    {
                        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
                        for (var i = 0; i < routeSource.Count; i++)
                        {
                            var route = routeSource[i];
                            var routeGroup = string.IsNullOrEmpty(route.Group) ? "None" : route.Group;
                            if (routeGroup == group)
                            {
                    if (ImGui.Selectable($"{route.Name}（{route.Waypoints.Count} 個步驟）###{i}", i == selectedRouteIndex))
                                    selectedRouteIndex = i;
                                //if (ImRaii.ContextPopup($"{route.Name}{i}"))
                                //{
                                //    selectedRouteIndex = i;
                                //    ContextMenuRoute(routeSource[i]);
                                //}
                            }
                        }
                    }
                }
            }
        }
    }

    internal static bool RapidImportEnabled = false;
    private void RapidImport()
    {
        if (ImGui.Checkbox("啟用快速匯入", ref RapidImportEnabled))
            ImGui.SetClipboardText("");

        ImGuiComponents.HelpMarker("只要複製路線即可連續匯入多個預設。Visland 會讀取剪貼簿並嘗試匯入內容；啟用時會先清空剪貼簿。");
        if (RapidImportEnabled)
        {
            try
            {
                var text = ImGui.GetClipboardText();
                if (text != "")
                {
                    TryImport(RouteDB);
                    ImGui.SetClipboardText("");
                }
            }
            catch (Exception e)
            {
                Svc.Log.Error(e.Message, e);
            }
        }
    }

    private void DrawRouteSettingsPopup()
    {
        using var popup = ImRaii.Popup("Advanced Options");
        if (popup.Success)
        {
        Utils.DrawSection("全域路線編輯選項", ImGuiColors.ParsedGold);
        if (ImGui.SliderFloat("預設路徑點半徑", ref RouteDB.DefaultWaypointRadius, 0, 100))
                RouteDB.NotifyModified();
        if (ImGui.SliderFloat("預設互動半徑", ref RouteDB.DefaultInteractionRadius, 0, 100))
                RouteDB.NotifyModified();

        Utils.DrawSection("全域路線執行選項", ImGuiColors.ParsedGold);

        if (ImGui.Checkbox("自動啟用無人島採集模式", ref RouteDB.GatherModeOnStart))
                RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker("在無人島開始執行路線時，自動啟用「採集模式」。");

            using (ImRaii.Disabled())
            {
        if (ImGui.Checkbox("發生錯誤時停止路線", ref RouteDB.DisableOnErrors))
                    RouteDB.NotifyModified();
            }
        ImGuiComponents.HelpMarker("背包已滿而無法採集節點時停止執行路線。");

        if (ImGui.Checkbox("跨區域時傳送", ref RouteDB.TeleportBetweenZones))
                RouteDB.NotifyModified();

            Utils.WorkInProgressIcon();
            ImGui.SameLine();
        if (ImGui.Checkbox("自動採集", ref RouteDB.AutoGather))
                RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker($"僅套用於非無人島路線。會自動採集「目標物品」欄位中的物品，並使用最佳可用技能。");

            //if (ImGui.SliderInt("Land Distance", ref RouteDB.LandDistance, 1, 30))
            //    RouteDB.NotifyModified();
            //ImGuiComponents.HelpMarker("Only applies to waypoints auto generated from node scanning. How far to land from the node to land and switch from fly pathfinding to ground pathfinding.");

        Utils.DrawSection("全域路線附加功能", ImGuiColors.ParsedGold);

        if (ImGui.Checkbox("路線執行中精製魔晶石", ref RouteDB.ExtractMateria))
                RouteDB.NotifyModified();
        if (ImGui.Checkbox("路線執行中修理裝備", ref RouteDB.RepairGear))
                RouteDB.NotifyModified();
        if (ImGui.SliderFloat("修理耐久度門檻", ref RouteDB.RepairPercent, 0, 100))
                RouteDB.NotifyModified();
        if (ImGui.Checkbox("路線執行中分解收藏品", ref RouteDB.PurifyCollectables))
                RouteDB.NotifyModified();
            ImGuiComponents.HelpMarker($"Also known as {GenericHelpers.GetRow<Addon>(2160)!.Value.Text}");
        if (ImGui.Checkbox("路線執行中檢查 AutoRetainer", ref RouteDB.AutoRetainerIntegration))
                RouteDB.NotifyModified();
        ImGuiComponents.HelpMarker($"任一已啟用角色有雇員或潛水艇歸來時，會啟用多角色模式。需要在 AutoRetainer 將目前角色設為偏好角色，並啟用傳送至部隊設定。");
            if (ImGuiEx.ExcelSheetCombo("##Foods", out Item i, _ => $"[{RouteDB.GlobalFood}] {GenericHelpers.GetRow<Item>((uint)RouteDB.GlobalFood)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
            {
                RouteDB.GlobalFood = (int)i.RowId;
                RouteDB.NotifyModified();
            }
            if (RouteDB.GlobalFood != 0)
            {
                ImGui.SameLine();
                if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearGlobalFood"))
                {
                    RouteDB.GlobalFood = 0;
                    RouteDB.NotifyModified();
                }
            }
        ImGuiComponents.HelpMarker("此處設定的食物會套用至所有路線，除非路線另有指定。");
        }
    }

    private void DrawEditor(Vector2 size)
    {
        if (selectedRouteIndex == -1) return;

        var routeSource = FilteredRoutes.Count > 0 ? FilteredRoutes : RouteDB.Routes;
        if (routeSource.Count == 0) return;
        var route = selectedRouteIndex >= routeSource.Count ? routeSource.Last() : routeSource[selectedRouteIndex];

        using (ImRaii.Child("Editor", size))
        {
            if (ImGuiComponents.IconButton(PlayIcon))
            {
                if (Exec.CurrentRoute != null)
                    Exec.Paused = !Exec.Paused;
                if (Exec.CurrentRoute == null && route.Waypoints.Count > 0)
                    Exec.Start(route, 0, true, loop, route.Waypoints[0].Pathfind);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(PlayTooltip);
            ImGui.SameLine();

            ImGui.PushStyleColor(ImGuiCol.Button, loop ? greenColor : redColor);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, loop ? greenColor : redColor);
            if (ImGuiComponents.IconButton(FontAwesomeIcon.SyncAlt))
                loop ^= true;
            ImGui.PopStyleColor(2);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("循環執行路線");
            ImGui.SameLine();

            if (Exec.CurrentRoute != null)
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Stop))
                    Exec.Finish();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("停止路線");
                ImGui.SameLine();
            }

            var canDelete = !ImGui.GetIO().KeyCtrl;
            using (ImRaii.Disabled(canDelete))
            {
                if (ImGuiComponents.IconButton(FontAwesomeIcon.Trash))
                {
                    if (Exec.CurrentRoute == route)
                        Exec.Finish();
                    RouteDB.Routes.Remove(route);
                    RouteDB.NotifyModified();
                }
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip("刪除路線（按住 CTRL）");
            ImGui.SameLine();

            if (ImGuiComponents.IconButton(FontAwesomeIcon.FileExport))
            {
                ImGui.SetClipboardText(JsonConvert.SerializeObject(route));
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("匯出路線（\uE052 Base64）");
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                    ImGui.SetClipboardText(Utils.ToCompressedBase64(route));
            }

            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.EllipsisH))
                ImGui.OpenPopup("##MassEditing");
            DrawMassEditContextMenu(route);

            var name = route.Name;
            var group = route.Group;
            var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
            ImGuiEx.TextV("Name: ");
            ImGui.SameLine();
            if (ImGui.InputText("##name", ref name, 256))
            {
                route.Name = name;
                RouteDB.NotifyModified();
            }
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Plus))
            {
                Exec.Finish();
                var player = Service.ClientState.LocalPlayer;
                if (player != null)
                {
                    route.Waypoints.Add(new() { Position = player.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                    RouteDB.NotifyModified();
                }
            }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("新增路徑點：目前位置");
            ImGui.SameLine();
            if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    route.Waypoints.Add(new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                    RouteDB.NotifyModified();
                    Exec.Start(route, route.Waypoints.Count - 1, false, false);
                }
            }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("新增路徑點：與目標互動");

            ImGuiEx.TextV("Group: ");
            ImGui.SameLine();
            if (ImGui.InputText("##group", ref group, 256))
            {
                route.Group = group;
                RouteDB.NotifyModified();
            }

            if (RouteDB.AutoGather)
            {
                ImGuiEx.TextV("Item Target: ");
                ImGui.SameLine();
                if (ImGuiEx.ExcelSheetCombo("##Gatherables", out GatheringItem gatherable, _ => $"[{route.TargetGatherItem}] {GenericHelpers.GetRow<Item>((uint)route.TargetGatherItem)?.Name.ToString()}", x => $"[{x.RowId}] {GenericHelpers.GetRow<Item>(x.Item.RowId)?.Name.ToString()}", x => x.Item.RowId != 0))
                {
                    route.TargetGatherItem = (int)gatherable.Item.RowId;
                    RouteDB.NotifyModified();
                }
                if (route.TargetGatherItem != 0)
                {
                    ImGui.SameLine();
                    if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearItemTarget"))
                    {
                        route.TargetGatherItem = 0;
                        RouteDB.NotifyModified();
                    }
                }
            }

            using (ImRaii.Child("waypoints"))
            {
                for (var i = 0; i < route.Waypoints.Count; ++i)
                {
                    var wp = route.Waypoints[i];
                    foreach (var wn in _tree.Node($"#{i + 1}: [x: {wp.Position.X:f0}, y: {wp.Position.Y:f0}, z: {wp.Position.Z:f0}] ({wp.Movement}) {(wp.InteractWithOID != 0 ? $" @ {wp.InteractWithName} ({wp.InteractWithOID:X})" : "")}###{i}", color: wp.IsPhantom ? ImGuiColors.HealerGreen.ToHex() : 0xffffffff, contextMenu: () => ContextMenuWaypoint(route, i)))
                        DrawWaypoint(wp);
                }
            }
        }
    }


    private bool pathfind;
    private int zoneID;
    private float radius;
    private InteractionType interaction;
    private void DrawMassEditContextMenu(Route route)
    {
        using var popup = ImRaii.Popup("##MassEditing");
        if (!popup) return;

        Utils.DrawSection("路線設定", ImGuiColors.ParsedGold);
        if (ImGuiEx.ExcelSheetCombo("##Foods", out Item i, _ => $"[{route.Food}] {GenericHelpers.GetRow<Item>((uint)route.Food)?.Name}", x => $"[{x.RowId}] {x.Name}", x => x.ItemUICategory.RowId == 46))
        {
            route.Food = (int)i.RowId;
            RouteDB.NotifyModified();
        }
        if (RouteDB.GlobalFood != 0)
        {
            ImGui.SameLine();
            if (ImGuiEx.IconButton(FontAwesomeIcon.Undo, "ClearLocalFood"))
            {
                route.Food = 0;
                RouteDB.NotifyModified();
            }
        }
        ImGuiComponents.HelpMarker("此處設定的食物只套用於本路線，並覆蓋全域食物設定。");

        Utils.DrawSection("批次編輯", ImGuiColors.ParsedGold);
        ImGui.Checkbox("尋路", ref pathfind);
        ImGui.SameLine();
        if (ImGui.Button("全部套用###Pathfind"))
        {
            route?.Waypoints.ForEach(x => x.Pathfind = pathfind);
            RouteDB.NotifyModified();
        }

        ImGui.InputInt("區域", ref zoneID);
        ImGui.SameLine();
        if (ImGui.Button("全部套用###Zone"))
        {
            route?.Waypoints.ForEach(x => x.ZoneID = zoneID);
            RouteDB.NotifyModified();
        }

        ImGui.InputFloat("半徑", ref radius);
        ImGui.SameLine();
        if (ImGui.Button("全部套用###Radius"))
        {
            route?.Waypoints.ForEach(x => x.Radius = radius);
            RouteDB.NotifyModified();
        }

        UICombo.Enum("互動類型", ref interaction);
        ImGui.SameLine();
        if (ImGui.Button("全部套用###Interaction"))
        {
            route?.Waypoints.ForEach(x => x.Interaction = interaction);
            RouteDB.NotifyModified();
        }
    }

    private void DrawWaypoint(Waypoint wp)
    {
        if (ImGuiEx.IconButton(FontAwesomeIcon.MapMarker) && PlayerEx.Available)
        {
            wp.Position = PlayerEx.Object.Position;
            wp.ZoneID = Service.ClientState.TerritoryType;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("設為目前位置");
        ImGui.SameLine();
        if (ImGui.InputFloat3("位置", ref wp.Position))
            RouteDB.NotifyModified();

        if (ImGui.InputInt("區域 ID", ref wp.ZoneID))
            RouteDB.NotifyModified();

        if (ImGui.InputFloat("半徑（公尺）", ref wp.Radius))
            RouteDB.NotifyModified();

        if (UICombo.Enum("移動模式", ref wp.Movement))
            RouteDB.NotifyModified();

        ImGui.SameLine();
        using (var noNav = ImRaii.Disabled(!Utils.HasPlugin(NavmeshIPC.Name)))
        {
            if (ImGui.Checkbox("尋路", ref wp.Pathfind))
                RouteDB.NotifyModified();
        }
        if (!Utils.HasPlugin(NavmeshIPC.Name))
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip($"此功能需要安裝 {NavmeshIPC.Name}。");

        if (ImGuiComponents.IconButton(FontAwesomeIcon.UserPlus))
        {
            if (wp.InteractWithOID == default)
            {
                var target = Service.TargetManager.Target;
                if (target != null)
                {
                    wp.Position = target.Position;
                    wp.Radius = RouteDB.DefaultInteractionRadius;
                    wp.InteractWithName = target.Name.ToString().ToLower();
                    wp.InteractWithOID = target.DataId;
                    RouteDB.NotifyModified();
                }
            }
            else
                wp.InteractWithOID = default;
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("新增或移除路徑點目標");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.CommentDots))
        {
            wp.showInteractions ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("顯示或隱藏互動設定");
        ImGui.SameLine();
        if (ImGuiEx.IconButton(FontAwesomeIcon.Clock))
        {
            wp.showWaits ^= true;
            RouteDB.NotifyModified();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("顯示或隱藏等待設定");

        if (wp.showInteractions)
        {
            if (UICombo.Enum("互動類型", ref wp.Interaction))
                RouteDB.NotifyModified();
            switch (wp.Interaction)
            {
                case InteractionType.None: break;
                case InteractionType.Standard: break;
                case InteractionType.StartRoute:
                    if (UICombo.String("路線名稱", RouteDB.Routes.Select(r => r.Name).ToArray(), ref wp.RouteName))
                        RouteDB.NotifyModified();
                    break;
                case InteractionType.NodeScan:
                    ImGui.SameLine();
                    Utils.WorkInProgressIcon();
                    ImGuiComponents.HelpMarker("節點掃描會檢查附近可選取的採集點；若找不到，則使用採集職業的顯示採集點技能並前往該處。系統會建立暫時路徑點並導航，每個暫時路徑點也會繼續掃描。這些特殊路徑點不會儲存至路線。");
                    ImGui.TextUnformatted("此功能目前可能無法正常處理陸上採集點。");
                    break;
            }
        }

        if (wp.showWaits)
        {
            if (ImGui.InputFloat2("等待艾奧傑亞時間", ref wp.WaitTimeET))
                RouteDB.NotifyModified();
            if (ImGui.SliderInt("等待（毫秒）", ref wp.WaitTimeMs, 0, 60000))
                RouteDB.NotifyModified();
            if (UICombo.Enum("等待條件", ref wp.WaitForCondition))
                RouteDB.NotifyModified();
        }
    }

    private void ContextMenuGroup(string group)
    {
        var old = group;
        ImGuiEx.TextV("Name: ");
        ImGui.SameLine();
        if (ImGui.InputText("##groupname", ref group, 256))
        {
            RouteDB.Routes.Where(r => r.Group == old).ToList().ForEach(r => r.Group = group);
            RouteDB.NotifyModified();
        }
    }

    private void ContextMenuRoute(Route r)
    {
        var group = r.Group;
        ImGuiEx.TextV("Group: ");
        ImGui.SameLine();
        if (ImGui.InputText("##group", ref group, 256))
        {
            r.Group = group;
            RouteDB.NotifyModified();
        }
        if (ImGui.BeginMenu("Add Route to Existing Group"))
        {
            var groupsCmr = GetGroups(RouteDB, true);
            foreach (var groupCmr in groupsCmr)
            {
                if (ImGui.MenuItem(groupCmr))
                    r.Group = groupCmr;
                RouteDB.NotifyModified();
            }
            ImGui.EndMenu();
        }
    }

    private void ContextMenuWaypoint(Route r, int i)
    {
        if (ImGui.MenuItem("只執行此步驟"))
            Exec.Start(r, i, false, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("從此步驟開始執行路線一次"))
            Exec.Start(r, i, true, false, r.Waypoints[i].Pathfind);

        if (ImGui.MenuItem("從此步驟開始並循環執行路線"))
            Exec.Start(r, i, true, true, r.Waypoints[i].Pathfind);

        var movementType = Service.Condition[ConditionFlag.InFlight] ? Movement.MountFly : Service.Condition[ConditionFlag.Mounted] ? Movement.MountNoFly : Movement.Normal;
        var target = Service.TargetManager.Target;

        if (ImGui.MenuItem($"切換為{(r.Waypoints[i].InteractWithOID != default ? "一般路徑點" : "互動路徑點")}"))
        {
            _postDraw.Add(() =>
            {
                r.Waypoints[i].InteractWithOID = r.Waypoints[i].InteractWithOID != default ? default : target?.DataId ?? default;
                RouteDB.NotifyModified();
            });
        }

        if (ImGui.MenuItem("在上方插入步驟"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = Service.ClientState.LocalPlayer.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem("在下方插入步驟"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (Service.ClientState.LocalPlayer != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = Service.ClientState.LocalPlayer.Position, Radius = RouteDB.DefaultWaypointRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }
        if (ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    if (target != null)
                    {
                        r.Waypoints.Insert(i + 1, new() { Position = target.Position, Radius = RouteDB.DefaultInteractionRadius, ZoneID = Service.ClientState.TerritoryType, Movement = movementType, InteractWithOID = target.DataId, InteractWithName = target.Name.ToString().ToLower() });
                        RouteDB.NotifyModified();
                    }
                }
            });
        }

        if (ImGui.MenuItem("上移"))
        {
            _postDraw.Add(() =>
            {
                if (i > 0 && i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i - 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem("下移"))
        {
            _postDraw.Add(() =>
            {
                if (i + 1 < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    var wp = r.Waypoints[i];
                    r.Waypoints.RemoveAt(i);
                    r.Waypoints.Insert(i + 1, wp);
                    RouteDB.NotifyModified();
                }
            });
        }

        if (ImGui.MenuItem("刪除"))
        {
            _postDraw.Add(() =>
            {
                if (i < r.Waypoints.Count)
                {
                    if (Exec.CurrentRoute == r)
                        Exec.Finish();
                    r.Waypoints.RemoveAt(i);
                    RouteDB.NotifyModified();
                }
            });
        }
    }
}
