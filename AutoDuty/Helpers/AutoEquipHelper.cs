using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using AutoDuty.IPC;
using System.Collections.Generic;
using Dalamud.Plugin.Services;
using ECommons.Throttlers;
using System;

namespace AutoDuty.Helpers
{
    using Windows;

    internal unsafe class AutoEquipHelper : ActiveHelperBase<AutoEquipHelper>
    {
        internal override void Start()
        {
            if (Plugin.Configuration.AutoEquipRecommendedGearGearsetter && Gearsetter_IPCSubscriber.IsEnabled)
            {
                this.TimeOut    = 5000;
                this.gearsetter = true;
            }
            else
            {
                this.TimeOut    = 2000;
                this.gearsetter = false;
            }
            base.Start();
        }

        private bool gearsetter;

        protected override string Name        => nameof(AutoEquipHelper);
        protected override string DisplayName => "Auto Equip";

        protected override int TimeOut { get; set; }


        protected override void     HelperUpdate(IFramework framework)
        {
            if(this.gearsetter)
                this.AutoEquipGearSetterUpdate(framework);
            else
                this.AutoEquipUpdate(framework);
        }

        internal override void Stop()
        {
            base.Stop();

            // RaptureGearsetModule.Instance() 是 FFXIVClientStructs 裡手寫的取得子
            // (`uiModule == null ? null : uiModule->GetRaptureGearsetModule()`),UIModule 尚未建立時會回 null,
            // 原本在同一行連續解參考兩次。取進區域變數判空後同幀即用;為 null 時跳過更新裝備組,
            // 其餘收尾(狀態歸零、PortraitHelper)照常執行。
            RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule != null)
                gearsetModule->UpdateGearset(gearsetModule->CurrentGearsetIndex);

            this._statesExecuted = AutoEquipState.None;
            this._index          = 0;
            this._gearset        = null;
            PortraitHelper.Invoke();
        }

        [Flags]
        enum AutoEquipState : int
        {
            None                                  = 0,
            Setting_Up                            = 1 << 0,
            Equipping                             = 1 << 1,
            Updating_Gearset                      = 1 << 2,
            Getting_Recommended_Gear              = 1 << 3,
            Recommended_Gear_Need_Second_Pass     = 1 << 4,
            Updating_Gearset_Second_Pass          = 1 << 5,
            Getting_Recommended_Gear_Second_Pass  = 1 << 6,
        }

        private AutoEquipState _statesExecuted = AutoEquipState.None;

        private void AutoEquipUpdate(IFramework framework)
        {
            if (!EzThrottler.Throttle(this.Name, 250))
                return;

            // RecommendEquipModule.Instance() 是手寫的取得子
            // (`uiModule == null ? null : uiModule->GetRecommendEquipModule()`),UIModule 尚未建立時會回 null,
            // 原本三處都無條件解參考。取進區域變數判空後同幀即用;為 null 時本 tick 不動作,
            // 下 tick 節流放行時再試(每幀熱路徑,不寫 log),逾時仍由 Start() 排的 TimeOut 收尾。
            RecommendEquipModule* recommendEquipModule = RecommendEquipModule.Instance();
            if (recommendEquipModule == null)
                return;

            if (recommendEquipModule->IsUpdating)
                    return;

            if (!this._statesExecuted.HasFlag(AutoEquipState.Setting_Up))
            {
                DebugLog($"RecommendEquipModule - SetupForClassJob");
                recommendEquipModule->SetupForClassJob((byte)Svc.Objects.LocalPlayer!.ClassJob.RowId);
                this._statesExecuted |= AutoEquipState.Setting_Up;
            }
            else if (!this._statesExecuted.HasFlag(AutoEquipState.Equipping))
            {
                DebugLog($"RecommendEquipModule - EquipRecommendedGear");
                recommendEquipModule->EquipRecommendedGear();
                this._statesExecuted |= AutoEquipState.Equipping;
            }
            else
            {
                DebugLog($"Stop");
                this.Stop();
            }
        }

        private List<(uint ItemId, InventoryType? SourceInventory, int? SourceInventorySlot, RaptureGearsetModule.GearsetItemIndex TargetSlot)>? _gearset           = null;
        private int                                                                                                                               _index             = 0;

        private void AutoEquipGearSetterUpdate(IFramework framework)
        {
            if (!EzThrottler.Check("AutoEquipGearSetter"))
                return;

            EzThrottler.Throttle("AutoEquipGearSetter", 50);

            // 同 AutoEquipUpdate:RaptureGearsetModule.Instance() 手寫取得子會在 UIModule 尚未建立時回 null,
            // 底下四個分支原本都無條件解參考(其中兩處還是同一行連續兩次)。取進區域變數判空後同幀即用;
            // 為 null 時本 tick 不動作,下 tick 再試(每幀熱路徑,不寫 log),逾時由 TimeOut 收尾。
            RaptureGearsetModule* gearsetModule = RaptureGearsetModule.Instance();
            if (gearsetModule == null)
                return;

            if (!this._statesExecuted.HasFlag(AutoEquipState.Updating_Gearset))
            {
                DebugLog($"RaptureGearsetModule - UpdateGearset");
                gearsetModule->UpdateGearset(gearsetModule->CurrentGearsetIndex);
                this._statesExecuted |= AutoEquipState.Updating_Gearset;
                EzThrottler.Throttle("AutoEquipGearSetter", 500, true);
            }
            else if (!this._statesExecuted.HasFlag(AutoEquipState.Getting_Recommended_Gear))
            {
                DebugLog($"Gearsetter_IPCSubscriber - GetRecommendationsForGearset");
                this._gearset     =  Gearsetter_IPCSubscriber.GetRecommendationsForGearset((byte)gearsetModule->CurrentGearsetIndex);
                this._statesExecuted |= AutoEquipState.Getting_Recommended_Gear;
            }
            else if (this._gearset != null && this._index < this._gearset.Count)
            {
                (uint itemId, InventoryType? inventoryType, int? sourceInventorySlot, RaptureGearsetModule.GearsetItemIndex targetSlot) = this._gearset[this._index];
                DebugLog($"Equip item {itemId} in {targetSlot} from {inventoryType} (slot {sourceInventorySlot})");

                if (inventoryType != null && sourceInventorySlot != null)
                {
                    var itemData = InventoryHelper.GetExcelItem(itemId);
                    if (itemData == null) return;
                    var equipSlotIndex = targetSlot;// InventoryHelper.GetEquippedSlot(itemData.Value);

                    // 🔴 這三處原本都是 `InventoryManager.Instance()->GetInventoryContainer(x)->Items[i]` 的裸鏈。
                    //    `GetInventoryContainer` 合法回 null，而且這裡的 inventoryType 與 sourceInventorySlot
                    //    是 **Gearsetter 外掛透過 IPC 給的**，不是我們自己算出來的 —— 容器不存在、
                    //    或索引超出 Size 都不是理論可能而已。改用 InventoryHelper.TryGetItem，
                    //    它同時做容器判空與 Size 上界（越界讀到的是相鄰記憶體而不是 null，失敗完全靜默）。
                    // fail-closed：讀不到就走原本「槽位裡的東西跟預期不符」那條路 ——
                    //    標記需要第二輪、跳過這一件，而不是照著讀不到的資料把裝備搬來搬去。
                    if (!InventoryHelper.TryGetItem(inventoryType.Value, (int)sourceInventorySlot, out InventoryItem sourceItem)
                        || sourceItem.ItemId != itemId)
                    {
                        DebugLog($"Item in slot does not match expected item");
                        this._statesExecuted |= AutoEquipState.Recommended_Gear_Need_Second_Pass;
                        this._index++;
                        return;
                    }

                    // fail-closed：讀不到目前裝備欄就當「那一格是空的」＝不做「把舊裝備收回背包」這個動作。
                    // 反過來（當成有東西）會對著讀不到的資料呼叫 MoveItemSlot。
                    if (Plugin.Configuration.AutoEquipRecommendedGearGearsetterOldToInventory && equipSlotIndex is not RaptureGearsetModule.GearsetItemIndex.MainHand and not RaptureGearsetModule.GearsetItemIndex.OffHand &&
                        InventoryHelper.TryGetItem(InventoryType.EquippedItems, (int)equipSlotIndex, out InventoryItem oldItem) && !oldItem.IsEmpty())
                    {
                        if (InventoryManager.Instance()->GetEmptySlotsInBag() < 1)
                        {
                            DebugLog("Moving to inventory ignored because no empty inventory slot");
                        }
                        else
                        {
                            // 🔴 原本是 `(inv, slot) = GetFirstAvailableSlot(Bag);` 再用 `slot <= 0` 判失敗。
                            //    0 既是「找不到」的哨兵、也是合法的第 0 格 —— 空格剛好落在某個背包第 0 格時
                            //    會被誤判成「這個背包沒空位」。上面已經用 GetEmptySlotsInBag() >= 1 確認過
                            //    背包確實有空位，所以那正是下面這句 "somehow" 會被印出來的實際成因。
                            //    改用 Try 版：成敗看回傳值，slot 只在成功時有意義。
                            if (!InventoryHelper.TryGetFirstAvailableSlot(out InventoryType inv, out ushort slot, InventoryHelper.Bag))
                            {
                                DebugLog("Moving to inventory ignored because no empty inventory slot found.. somehow");
                            }
                            else
                            {
                                InventoryManager.Instance()->MoveItemSlot(InventoryType.EquippedItems, (ushort)equipSlotIndex, inv, slot, true);
                                DebugLog("Moving old item to inventory");
                                return;
                            }
                        }
                    }



                    DebugLog("Actually equipping");
                    InventoryHelper.EquipGear(itemData.Value, (InventoryType)inventoryType, (int)sourceInventorySlot, equipSlotIndex);
                    // fail-closed：這是「裝上去成功了沒」的確認。讀不到就不推進 _index，
                    // 下一輪會重試同一件 —— 把「確認不了」當成「成功」會靜默跳過一件裝備。
                    if (InventoryHelper.TryGetItem(InventoryType.EquippedItems, (int)equipSlotIndex, out InventoryItem equipped)
                        && equipped.ItemId == itemId)
                    {
                        DebugLog($"Successfully Equipped {itemData.Value.Name} to {equipSlotIndex.ToCustomString()}");
                        this._index++;
                    }
                }
                else
                    this._index++;
            }
            else if (this._statesExecuted.HasFlag(AutoEquipState.Recommended_Gear_Need_Second_Pass) && !this._statesExecuted.HasFlag(AutoEquipState.Updating_Gearset_Second_Pass))
            {
                // Gearsetter returns the same ring slot for both hands if two instances of the same ring should be used. This allows equiping one of them and the other one.
                DebugLog($"RaptureGearsetModule - UpdateGearsetSecondPass");
                gearsetModule->UpdateGearset(gearsetModule->CurrentGearsetIndex);
                this._statesExecuted |= AutoEquipState.Updating_Gearset_Second_Pass;
                EzThrottler.Throttle("AutoEquipGearSetter", 500, true);
            }
            else if (this._statesExecuted.HasFlag(AutoEquipState.Recommended_Gear_Need_Second_Pass) && !this._statesExecuted.HasFlag(AutoEquipState.Getting_Recommended_Gear_Second_Pass))
            {
                DebugLog($"Gearsetter_IPCSubscriber - GetRecommendationsForGearset");
                this._gearset     =  Gearsetter_IPCSubscriber.GetRecommendationsForGearset((byte)gearsetModule->CurrentGearsetIndex);
                this._index       = 0;
                this._statesExecuted |= AutoEquipState.Getting_Recommended_Gear_Second_Pass;
            }
            else
            {
                DebugLog($"Gearsetter doesn't recommend any more");
                this.Stop();
            }
        }
    }
}