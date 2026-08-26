using System;
using Dalamud.Configuration;

namespace LatihasChocobo;

[Serializable]
public class MConfiguration : IPluginConfiguration {
	public string AutoDutyTerritory = "";
	public bool Enabled, AutoDuty, AutoUseItem = true, DisableSpeedUpWhenLowHP = true, EnableSpeedUpWhenHighHP = true, MaxLevelMode;
	public bool BoundaryMode;
	public int BoundaryPhase; // 1=靠左, 2=靠右, 0=未啟動
	public float JumpPeakTime = 0.5f;   // 按下跳躍到最高點的秒數（自動校正）
	public float JumpPeakHeight = 3f;   // 最高點相對起跳的高度差（u）
	public int PressMs = 30, AutoDutyWait = 5, CcbMaxStar = 1;
	public int KC_A = 65, KC_D = 68, KC_W = 87, KC_SPACE = 32, KC_1 = 49, KC_2 = 50;
	public float SpeedHighW = 5f;
	public int Version { get; set; }
	public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}